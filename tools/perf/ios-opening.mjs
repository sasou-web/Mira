// The iPhone's opening as testOpening times it (src/Mira.iOS/MiraUITests, lines MIRA-OPENING of the tests' log), as
// a report for tools/perf/budget.mjs (source "iphone"): Jellyfin slow by 1.5 s (the stand-in's /Mock/Slow), from the
// launch to Home, and from XCTest's return from the launch to Home (XCTest's own wait taken out); Jellyfin away, from
// the launch to the Home kept from before. A test run again (-retry-tests-on-failure) counts at its best.
//   node tools/perf/ios-opening.mjs <tests.log> <report.json>
import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

/** The phases of the log's MIRA-OPENING lines, each at its shortest. */
export function phases(log) {
  const seen = {};
  const keep = (name, seconds) => { if (Number.isFinite(seconds)) seen[name] = Math.min(seen[name] ?? Infinity, seconds); };
  let launched;
  for (const line of log.split('\n')) {
    let m = line.match(/MIRA-OPENING slow: launched after ([\d.]+) s/);
    if (m) { launched = Number(m[1]); continue; }
    m = line.match(/MIRA-OPENING slow: gone after ([\d.]+) s/);
    if (m) {
      keep('Opening, Jellyfin slow: launch to Home', Number(m[1]));
      if (launched !== undefined) keep('Opening, Jellyfin slow: Home after the launch returned', Number(m[1]) - launched);
      launched = undefined; continue;
    }
    m = line.match(/MIRA-OPENING away: gone after ([\d.]+) s/);
    if (m) keep('Opening, Jellyfin away: launch to Home', Number(m[1]));
  }
  return Object.entries(seen).map(([Name, Seconds]) => ({ Name, Seconds: Math.round(Seconds * 1000) / 1000 }));
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const [log, out] = process.argv.slice(2);
  const found = phases(readFileSync(log, 'utf8'));
  writeFileSync(out, JSON.stringify({ header: ['testOpening, simulator of ios.yml'], phases: found, failure: null }, null, 2) + '\n');
  console.log(found.map((p) => `${p.Name}: ${p.Seconds.toFixed(2)} s`).join('\n') || 'No MIRA-OPENING line in the log.');
}
