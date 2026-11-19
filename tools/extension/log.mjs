// Jellyfin's log after the checks of the extension (web.yml): fails on anything the extension threw or reported as an
// error, which no answer of its API shows (a loop of its own failing every minute, a controller's exception that
// Jellyfin turns into a 500, the plugin not loaded, a method this Jellyfin lacks). Reads `docker logs` on its input.
//   docker logs jellyfin 2>&1 | node tools/extension/log.mjs
// An entry is its header line (« [12:00:00] [ERR] [27] Mira.Jellyfin.TvdbOrders: … ») and the lines after it (the
// exception). Counted: an error (ERR, FTL) or a warning (WRN) of the extension's own loggers or naming Mira, and any
// entry whose exception passes through Mira's code (« at Mira. »), whatever logged it; not the one warning expected below.
import fs from 'node:fs';

const text = fs.readFileSync(0, 'utf8');
const header = /^\[[0-9:. -]+\] \[(VRB|DBG|INF|WRN|ERR|FTL)\] \[\d+\] ([^:]+):/;
const entries = [];
for (const line of text.split(/\r?\n/)) {
  const match = header.exec(line);
  if (match) entries.push({ level: match[1], logger: match[2], lines: [line] });
  else if (entries.length && line.trim()) entries.at(-1).lines.push(line);
}
const ours = (entry) => /^Mira\./.test(entry.logger) || /\bMira\b/.test(entry.lines[0]);
// Expected, and said in the journal of the apps: subtitles Jellyfin 12 could not write beside read-only media (web.yml).
const expected = (entry) => entry.level === 'WRN' && /Mira: subtitles for .* not written by Jellyfin: /.test(entry.lines[0]) && entry.lines.length === 1;
const found = entries.filter((entry) => !expected(entry) && ((['ERR', 'FTL', 'WRN'].includes(entry.level) && ours(entry)) || entry.lines.some((line) => /^\s+at Mira\./.test(line))));
console.log(`Jellyfin's log: ${entries.length} entries, ${found.length} from the extension that should not be there`);
for (const entry of found.slice(0, 20)) console.log(`::error title=Jellyfin log::${entry.lines[0].replace(/^\[[^\]]*\] /, '')}\n${entry.lines.slice(1, 12).join('\n')}\n`);
if (!entries.length) { console.log('::error title=Jellyfin log::no entry read: is the container there?'); process.exit(1); }
process.exit(found.length ? 1 : 0);
