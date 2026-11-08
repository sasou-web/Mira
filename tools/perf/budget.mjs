// Performance budgets of the CI: the reports of --smooth-check (Windows' tools/smooth-check.ps1, the Mac's
// src/Mira.Mac/Checks/SmoothCheck.cs) and of tools/idle-check.ps1 held against tools/perf/budgets.json, so that the
// smoothness won in 0.9.4 is not lost again without a word. Two levels per metric: "fail" stops the job, set far
// enough from what the runners measure that their noise never reaches it (the numbers it was set from are in
// budgets.json); "warn" only says so, in the log and the job's summary. With --baseline (the same report from the last
// run of main), each metric is compared with main's too, and a clear step back is a warning, never a failure: two
// runners differ more than two commits do.
//   node tools/perf/budget.mjs <source> <report.json> [<report.json> …] [--baseline <report.json>] [--title <text>]
// <source>: a key of budgets.json (windows-edge, windows-offscreen, mac, idle). Several reports of one source (the same
// check run again after a failure) count each metric at its best: a failure is a regression only when every run shows
// it. Exit code 1 on a failure (or a budgeted phase missing from the report), 0 otherwise.
import { readFileSync, appendFileSync, existsSync } from 'node:fs';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/** The metrics a budget may name: those of the reports, and Held (the longest the interface's thread was held). */
export function metric(phase, name) {
  if (name === 'Held') {
    // Windows: a frame's work or another operation; Mac: the click's own work or the longest gap between frames.
    const parts = ['WorkMax', 'LongestOther', 'Click'].map((x) => phase[x]).filter((x) => typeof x === 'number');
    return parts.length ? Math.max(...parts) : undefined;
  }
  return typeof phase[name] === 'number' ? phase[name] : undefined;
}

/** Whether a larger value is worse: limits with "max" are ceilings, with "min" floors. */
const ceiling = (limit) => limit.max !== undefined || limit.warnMax !== undefined;

/** The best value of a metric across several runs of the same check (undefined when none has it). */
export function best(reports, phaseName, name, limit) {
  const values = reports
    .map((report) => report.phases?.find((p) => p.Name === phaseName))
    .filter(Boolean)
    .map((phase) => metric(phase, name))
    .filter((x) => x !== undefined);
  if (!values.length) return undefined;
  return ceiling(limit) ? Math.min(...values) : Math.max(...values);
}

/**
 * Each budgeted metric's verdict: ok, warn (past "warnMax"/"warnMin", or clearly worse than main) or fail (past
 * "max"/"min", or its phase missing). Comparison with main: a ceiling worse by more than both its ratio and its slack,
 * a floor lower by more than its drop (defaults in budgets.json "compare").
 */
export function judge(budget, reports, baseline, compare = {}) {
  const rows = [];
  for (const [phaseName, metrics] of Object.entries(budget.phases ?? {})) {
    for (const [name, limit] of Object.entries(metrics)) {
      const value = best(reports, phaseName, name, limit);
      const row = { phase: phaseName, metric: name, value, limit, verdict: 'ok', why: '' };
      if (value === undefined) { Object.assign(row, { verdict: 'fail', why: 'phase or metric missing from the report' }); rows.push(row); continue; }
      const up = ceiling(limit);
      if (up ? value > limit.max : value < limit.min) Object.assign(row, { verdict: 'fail', why: `${up ? 'over' : 'under'} ${fmt(up ? limit.max : limit.min, name)}` });
      else if (up ? value > limit.warnMax : value < limit.warnMin) Object.assign(row, { verdict: 'warn', why: `${up ? 'over' : 'under'} ${fmt(up ? limit.warnMax : limit.warnMin, name)}` });
      const base = baseline ? best([baseline], phaseName, name, limit) : undefined;
      if (base !== undefined) {
        row.base = base;
        const rule = { ...(compare.default ?? {}), ...(compare[name] ?? {}) };
        const worse = up
          ? value > base * (rule.ratio ?? 1.25) && value - base > (rule.slack ?? 0)
          : base - value > (rule.drop ?? 0.1);
        if (worse && row.verdict === 'ok') Object.assign(row, { verdict: 'warn', why: `worse than main (${fmt(base, name)})` });
      }
      rows.push(row);
    }
  }
  return rows;
}

/** A value as the reports show it: a share as a percentage, a time in ms, memory in MB. */
export function fmt(value, name) {
  if (value === undefined) return '?';
  if (['Shown', 'OnTime', 'Busy'].includes(name)) return `${(value * 100).toFixed(0)} %`;
  if (name === 'Cpu') return `${value.toFixed(2)} % of a core`;
  if (name === 'PrivateMB') return `${value.toFixed(0)} MB`;
  if (name === 'Written') return `${value.toFixed(0)} bytes`;
  if (name === 'Seconds') return `${value.toFixed(1)} s`;
  return `${value.toFixed(1)} ms`;
}

const label = { Shown: 'shown', OnTime: 'on time', Held: 'thread held', GapMax: 'longest gap', Cpu: 'processor', PrivateMB: 'private memory', Written: 'written', Seconds: 'time' };

/** The verdicts as a Markdown table, for the log and the job's summary. */
export function table(rows, title) {
  const mark = { ok: '✅', warn: '⚠️', fail: '❌' };
  const lines = [`#### ${title}`, '', '| | Phase | Metric | Measured | Main | Warn | Fail | |', '|---|---|---|---|---|---|---|---|'];
  for (const r of rows) {
    const up = ceiling(r.limit);
    lines.push(`| ${mark[r.verdict]} | ${r.phase} | ${label[r.metric] ?? r.metric} | ${fmt(r.value, r.metric)} | ${r.base === undefined ? '' : fmt(r.base, r.metric)} | ${(up ? r.limit.warnMax : r.limit.warnMin) === undefined ? '' : fmt(up ? r.limit.warnMax : r.limit.warnMin, r.metric)} | ${(up ? r.limit.max : r.limit.min) === undefined ? '' : fmt(up ? r.limit.max : r.limit.min, r.metric)} | ${r.why} |`);
  }
  return lines.join('\n') + '\n';
}

function main(argv) {
  const [source, ...rest] = argv;
  const reports = []; let baseline; let title = source;
  for (let i = 0; i < rest.length; i++) {
    if (rest[i] === '--baseline') { const path = rest[++i]; if (path && existsSync(path)) baseline = JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, '')); else console.log(`No report of main at ${path}: no comparison.`); }
    else if (rest[i] === '--title') title = rest[++i];
    else reports.push(JSON.parse(readFileSync(rest[i], 'utf8').replace(/^﻿/, '')));
  }
  const budgets = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'budgets.json'), 'utf8'));
  const budget = budgets.sources[source];
  if (!budget) throw new Error(`No budget for ${source} in budgets.json.`);
  if (!reports.length) throw new Error('No report given.');
  const rows = judge(budget, reports, baseline, budgets.compare);
  const text = table(rows, `${title}${reports.length > 1 ? ` (best of ${reports.length} runs)` : ''}`);
  console.log(text);
  for (const r of rows.filter((x) => x.verdict !== 'ok'))
    console.log(`::${r.verdict === 'fail' ? 'error' : 'warning'} title=Performance (${source})::${r.phase}, ${label[r.metric] ?? r.metric}: ${fmt(r.value, r.metric)}, ${r.why}`);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, text + '\n');
  return rows.some((x) => x.verdict === 'fail') ? 1 : 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) process.exitCode = main(process.argv.slice(2));
