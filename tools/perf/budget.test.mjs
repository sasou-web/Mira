// node --test tools/perf/budget.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { judge, metric, best, table } from './budget.mjs';

const budgets = JSON.parse(readFileSync(new URL('./budgets.json', import.meta.url), 'utf8'));
const budget = { phases: { 'Films : ouverture (1re fois)': { Held: { warnMax: 80, max: 160 } }, 'Films : molette': { Shown: { warnMin: 0.6, min: 0.45 } } } };
const report = (held, shown) => ({ phases: [
  { Name: 'Films : ouverture (1re fois)', Shown: 0.85, WorkMax: 12, LongestOther: held },
  { Name: 'Films : molette', Shown: shown, WorkMax: 0.4, LongestOther: 1 },
] });
const verdicts = (rows) => rows.map((r) => r.verdict);

test('Held is the longest the thread was held: a frame, another operation or a click', () => {
  assert.equal(metric({ WorkMax: 12, LongestOther: 40 }, 'Held'), 40);
  assert.equal(metric({ Click: 30 }, 'Held'), 30);
  assert.equal(metric({ Shown: 0.5 }, 'Held'), undefined);
});

test('within budget, past a warning, past a failure', () => {
  assert.deepEqual(verdicts(judge(budget, [report(40, 0.8)])), ['ok', 'ok']);
  assert.deepEqual(verdicts(judge(budget, [report(90, 0.55)])), ['warn', 'warn']);
  assert.deepEqual(verdicts(judge(budget, [report(200, 0.3)])), ['fail', 'fail']);
});

test('measured again: each metric at its best, a failure only when every run fails', () => {
  assert.deepEqual(verdicts(judge(budget, [report(200, 0.3), report(50, 0.7)])), ['ok', 'ok']);
  assert.equal(best([report(200, 0.3), report(170, 0.2)], 'Films : molette', 'Shown', { min: 0.45 }), 0.3);
  assert.deepEqual(verdicts(judge(budget, [report(200, 0.3), report(170, 0.2)])), ['fail', 'fail']);
});

test('a phase missing from the report fails: a renamed phase is renamed in budgets.json too', () => {
  const rows = judge(budget, [{ phases: [{ Name: 'Films : molette', Shown: 0.8 }] }]);
  assert.deepEqual(verdicts(rows), ['fail', 'ok']);
  assert.match(rows[0].why, /missing/);
});

test('compared with main: a clear step back warns, the noise of a runner does not', () => {
  const compare = budgets.compare;
  assert.deepEqual(verdicts(judge(budget, [report(45, 0.75)], report(38, 0.8), compare)), ['ok', 'ok']);
  assert.deepEqual(verdicts(judge(budget, [report(70, 0.55)], report(38, 0.8), compare)), ['warn', 'warn']);
  // Never a failure from main alone.
  assert.ok(!judge(budget, [report(150, 0.5)], report(10, 0.95), compare).some((r) => r.verdict === 'fail'));
});

test('the table says each verdict, with the limits', () => {
  const text = table(judge(budget, [report(200, 0.55)]), 'Windows');
  assert.match(text, /❌ \| Films : ouverture \(1re fois\) \| thread held \| 200\.0 ms/);
  assert.match(text, /⚠️ \| Films : molette \| shown \| 55 %/);
});

test('every budget is a floor or a ceiling, its warning before its failure', () => {
  for (const [source, { phases }] of Object.entries(budgets.sources))
    for (const [phase, metrics] of Object.entries(phases))
      for (const [name, limit] of Object.entries(metrics)) {
        const where = `${source}, ${phase}, ${name}`;
        const up = limit.max !== undefined || limit.warnMax !== undefined, down = limit.min !== undefined || limit.warnMin !== undefined;
        assert.ok(up !== down, `${where}: a floor or a ceiling, not both`);
        if (limit.warnMax !== undefined && limit.max !== undefined) assert.ok(limit.warnMax < limit.max, where);
        if (limit.warnMin !== undefined && limit.min !== undefined) assert.ok(limit.warnMin > limit.min, where);
      }
});

test('the phases budgeted are those the checks measure', () => {
  const windows = readFileSync('src/Mira.Desktop/Checks/MainWindow.SmoothCheck.cs', 'utf8');
  const mac = readFileSync('src/Mira.Mac/Checks/SmoothCheck.cs', 'utf8');
  const idle = readFileSync('tools/idle-check.ps1', 'utf8');
  const iphone = readFileSync('tools/perf/ios-opening.mjs', 'utf8');
  const source = { 'windows-edge': windows, 'windows-offscreen': windows, mac, idle, iphone };
  for (const [name, { phases }] of Object.entries(budgets.sources))
    for (const phase of Object.keys(phases))
      assert.ok(source[name].includes(['idle', 'iphone'].includes(name) ? `'${phase}'` : `Measure("${phase}"`), `${name}: no phase « ${phase} » in its check`);
});

test('the iPhone opening read from the tests log, each phase at its best', async () => {
  const { phases } = await import('./ios-opening.mjs');
  const log = ['MIRA-OPENING slow: launched after 8.5 s, opening shown: false', 'MIRA-OPENING slow: gone after 10.5 s', 'MIRA-OPENING away: gone after 9.4 s',
    'MIRA-OPENING slow: launched after 6 s, opening shown: true', 'MIRA-OPENING slow: gone after 9 s'].join('\n');
  assert.deepEqual(phases(log), [
    { Name: 'Opening, Jellyfin slow: launch to Home', Seconds: 9 },
    { Name: 'Opening, Jellyfin slow: Home after the launch returned', Seconds: 2 },
    { Name: 'Opening, Jellyfin away: launch to Home', Seconds: 9.4 },
  ]);
});
