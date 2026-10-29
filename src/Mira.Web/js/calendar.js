// « Épisodes manquants et prochaines sorties »: what Jellyfin knows of a series beyond its files, when Jellyfin's
// TheTVDB extension is installed (it adds a virtual episode, LocationType « Virtual », for each episode the library
// lacks, with the day it airs). Missing: aired before today; coming: today or later; announced without a day: neither.
// The specials (season 0) never count. As Mira for Windows and Mac say it (EpisodeCalendar.cs, cases in
// tests/fixtures/calendar.json). Imported by Node too (tools/web/unit): nothing of the browser here.
import { tr, trn, locale } from './i18n.js';

/** The day an episode airs, « 2026-11-02 », as TheTVDB gives it (Jellyfin keeps it at midnight UTC); null without one. */
export const airDay = (episode) => (/^\d{4}-\d{2}-\d{2}/.test(episode?.PremiereDate ?? '') ? episode.PremiereDate.slice(0, 10) : null);

/** Today on this device, as a day (« 2026-10-29 »), to compare with airDay. */
export function today(now = new Date()) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

const order = (a, b) => (airDay(a) ?? '9999').localeCompare(airDay(b) ?? '9999') || (a.ParentIndexNumber ?? 0) - (b.ParentIndexNumber ?? 0) || (a.IndexNumber ?? 0) - (b.IndexNumber ?? 0);
const inSeasons = (x) => x?.Type === 'Episode' && x.LocationType === 'Virtual' && (x.ParentIndexNumber ?? 0) >= 1 && x.IndexNumber != null;

/**
 * Of a series' episodes (its virtual ones at least): { missing, coming }, each in the order they air. Missing: aired
 * before `day` and not in the library; coming: airing `day` or later.
 */
export function calendar(episodes, day) {
  const known = (episodes ?? []).filter((x) => inSeasons(x) && airDay(x));
  return {
    missing: known.filter((x) => airDay(x) < day).sort(order),
    coming: known.filter((x) => airDay(x) >= day).sort(order),
  };
}

/** « S2 · É5 », « S2 · É5–8 » for episodes in a row: the episodes of a list gathered, MAX_RANGES at most. */
export const MAX_RANGES = 3;
export function ranges(episodes) {
  const sorted = [...episodes].sort((a, b) => a.ParentIndexNumber - b.ParentIndexNumber || a.IndexNumber - b.IndexNumber);
  const runs = [];
  for (const x of sorted) {
    const last = runs.at(-1);
    if (last && last.season === x.ParentIndexNumber && x.IndexNumber === last.to + 1) { last.to = x.IndexNumber; last.count++; } else runs.push({ season: x.ParentIndexNumber, from: x.IndexNumber, to: x.IndexNumber, count: 1 });
  }
  const shown = runs.slice(0, MAX_RANGES);
  return {
    labels: shown.map((r) => `S${r.season} · ${tr('É{n}', { n: r.from === r.to ? r.from : `${r.from}–${r.to}` })}`),
    rest: runs.slice(MAX_RANGES).reduce((n, r) => n + r.count, 0),
  };
}

/** « 2 épisodes manquants : S1 · É3, S2 · É2 », « … et 6 autres »; '' without any. */
export function missingText(missing) {
  if (!missing.length) return '';
  const { labels, rest } = ranges(missing);
  const list = rest ? tr('{list} et {n} autres', { list: labels.join(', '), n: rest }) : labels.join(', ');
  return trn(missing.length, '{n} épisode manquant : {list}', '{n} épisodes manquants : {list}', { list });
}

/** Days apart, `a` to `b` (« 2026-10-29 », « 2026-11-01 »: 3). */
const daysBetween = (a, b) => Math.round((Date.parse(`${b}T00:00:00Z`) - Date.parse(`${a}T00:00:00Z`)) / 86_400_000);

/**
 * A day as a sentence says it, from `day0` (today): « aujourd’hui », « demain », « hier », « samedi » within the
 * week, « jeudi 5 novembre » beyond, with its year in another year.
 */
export function dayText(day, day0) {
  const apart = daysBetween(day0, day);
  if (apart === 0) return tr('aujourd’hui');
  if (apart === 1) return tr('demain');
  if (apart === -1) return tr('hier');
  const date = new Date(`${day}T12:00:00Z`);
  const options = { weekday: 'long', timeZone: 'UTC' };
  if (apart < 2 || apart > 6) Object.assign(options, { day: 'numeric', month: 'long' }, day.slice(0, 4) !== day0.slice(0, 4) ? { year: 'numeric' } : {});
  return new Intl.DateTimeFormat(locale(), options).format(date);
}

/** The same at the head of a day's list: « Aujourd’hui », « Samedi ». */
export const dayTitle = (day, day0) => dayText(day, day0).replace(/^./, (c) => c.toUpperCase());

/** « Prochain épisode : S2 · É3, samedi »; '' without one. */
export function nextText(coming, day0) {
  const next = coming[0];
  if (!next) return '';
  return tr('Prochain épisode : {episode}, {day}', { episode: `S${next.ParentIndexNumber} · ${tr('É{n}', { n: next.IndexNumber })}`, day: dayText(airDay(next), day0) });
}

/** Coming episodes by day, in order: [{ day, episodes }]. */
export function byDay(coming) {
  const days = [];
  for (const x of [...coming].sort(order)) {
    const day = airDay(x);
    if (days.at(-1)?.day === day) days.at(-1).episodes.push(x); else days.push({ day, episodes: [x] });
  }
  return days;
}
