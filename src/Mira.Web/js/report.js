// « Ton bilan »: what this account finished watching since a date, from Jellyfin's own record (each title's last play),
// as Mira on Windows and Mac (WatchReport.cs, cases in tests/fixtures/report.json). A title watched twice counts once.
// Imported by Node too (tools/web/unit): nothing of the browser here.
import { tr, compareText } from './i18n.js';

const minutesOf = (x) => Math.round(Math.max(0, x.RunTimeTicks ?? 0) / 600_000_000);
const playedAt = (x) => Date.parse(x.UserData?.LastPlayedDate ?? '') || 0;
const byName = compareText;

/** « Ce mois-ci » starts on the 1st at midnight here, « Cette année » on 1 January. */
export function reportStart(now, year) {
  const d = new Date(now);
  return new Date(d.getFullYear(), year ? 0 : d.getMonth(), 1).getTime();
}

/** played: films and episodes played to the end; series: theirs, with Genres and UserData.Played; from: ms since 1970. */
export function watchReport(played, series, from) {
  const shows = new Map(series.map((x) => [x.Id, x]));
  const items = played.filter((x) => (x.Type === 'Movie' || x.Type === 'Episode') && playedAt(x) >= from);
  const genres = new Map();
  for (const item of items) {
    const kinds = item.Type === 'Episode' && shows.has(item.SeriesId) ? shows.get(item.SeriesId).Genres ?? [] : item.Genres ?? [];
    for (const genre of new Set(kinds)) genres.set(genre, (genres.get(genre) ?? 0) + minutesOf(item));
  }
  const groups = new Map();
  for (const item of items) if (item.Type === 'Episode' && item.SeriesId) groups.set(item.SeriesId, [...(groups.get(item.SeriesId) ?? []), item]);
  const name = (id, group) => shows.get(id)?.Name || group[0].SeriesName || tr('Série');
  const latest = (group) => Math.max(...group.map(playedAt));
  const finished = [...groups].filter(([id]) => shows.get(id)?.UserData?.Played).sort((a, b) => latest(b[1]) - latest(a[1])).map(([id, group]) => name(id, group));
  const most = [...groups].map(([id, group]) => ({ name: name(id, group), episodes: group.length, minutes: group.reduce((s, x) => s + minutesOf(x), 0) }))
    .sort((a, b) => b.minutes - a.minutes || byName(a.name, b.name)).slice(0, 3);
  return {
    minutes: items.reduce((s, x) => s + minutesOf(x), 0),
    films: items.filter((x) => x.Type === 'Movie').length,
    episodes: items.filter((x) => x.Type === 'Episode').length,
    finished,
    genres: [...genres].sort((a, b) => b[1] - a[1] || byName(a[0], b[0])).slice(0, 3).map(([g]) => g),
    series: most,
  };
}

/** « 45 min », « 3 h », « 12 h 05 ». */
export const reportTime = (m) => (m < 60 ? tr('{m} min', { m: Math.max(0, m) }) : m % 60 === 0 ? tr('{h} h', { h: Math.floor(m / 60) })
  : tr('{h} h {mm}', { h: Math.floor(m / 60), mm: String(m % 60).padStart(2, '0') }));
