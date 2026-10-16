// « Nouvel épisode »: the episodes Jellyfin added since the last look, of the series this account follows (one already
// started, or a favourite), one line for each series, as Mira for Windows notifies them (NewEpisodes.cs, cases in
// tests/fixtures/new-episodes.json). The mark is Jellyfin's own date of the newest episode seen, never this phone's
// clock. Imported by Node too (tools/web/unit): nothing of the browser here.
import { tr, trn } from './i18n.js';

export const MAX_NOTICES = 3;

/** Jellyfin's dates have seven decimals, more than every browser reads: the milliseconds, or null. */
export function dateOf(text) {
  const time = Date.parse(String(text ?? '').replace(/(\.\d{3})\d+/, '$1'));
  return Number.isFinite(time) ? time : null;
}

/** Started (part of it watched: a new episode makes it unwatched again) or in the favourites. */
export const follows = (series) => !!series?.UserData?.IsFavorite || (series?.UserData?.PlayedPercentage ?? 0) > 0;

/** The newest date of these episodes when it is past `seen` (ms, or null): the next mark. */
export function newestEpisode(episodes, seen) {
  let newest = seen;
  for (const episode of episodes) {
    const date = dateOf(episode.DateCreated);
    if (date != null && (newest == null || date > newest)) newest = date;
  }
  return newest;
}

const label = (x) => (x.ParentIndexNumber != null
  ? (x.IndexNumber != null ? tr('S{s} É{e}', { s: x.ParentIndexNumber, e: x.IndexNumber }) : `S${x.ParentIndexNumber}`)
  : (x.IndexNumber != null ? tr('É{n}', { n: x.IndexNumber }) : ''));

/** « Nouvel épisode : S2 É5 · Le départ », « 3 nouveaux épisodes : S2 É5 à É7 », « 4 nouveaux épisodes, dès S1 É9 ». */
export function newEpisodesText(episodes) {
  const first = episodes[0];
  if (episodes.length === 1) return tr('Nouvel épisode : {episode}', { episode: [label(first), first.Name ?? ''].filter(Boolean).join(' · ') });
  const last = episodes.at(-1);
  return first.ParentIndexNumber === last.ParentIndexNumber && last.IndexNumber != null && first.IndexNumber != null
    ? tr('{n} nouveaux épisodes : {first} à É{last}', { n: episodes.length, first: label(first), last: last.IndexNumber })
    : tr('{n} nouveaux épisodes, dès {first}', { n: episodes.length, first: label(first) });
}

/**
 * The lines for the episodes added after `seen` (ms), not watched yet, of the followed series among `series` (by id):
 * the most recent series first, MAX_NOTICES at most, and a line for the rest. { notices: [{ seriesId, title, text,
 * episode (the first of them, to open), count }], more, series (how many series in all) }.
 */
export function pickNewEpisodes(episodes, series, seen) {
  const groups = new Map();
  for (const x of episodes) {
    const date = dateOf(x.DateCreated);
    if (x.Type !== 'Episode' || date == null || !(date > seen) || x.UserData?.Played || !x.SeriesId || !follows(series[x.SeriesId])) continue;
    groups.set(x.SeriesId, [...(groups.get(x.SeriesId) ?? []), x]);
  }
  const newest = (group) => Math.max(...group.map((x) => dateOf(x.DateCreated)));
  const order = (a, b) => (a.ParentIndexNumber ?? 0) - (b.ParentIndexNumber ?? 0) || (a.IndexNumber ?? 0) - (b.IndexNumber ?? 0);
  const sorted = [...groups.values()].sort((a, b) => newest(b) - newest(a)).map((group) => [...group].sort(order));
  const notices = sorted.slice(0, MAX_NOTICES).map((group) => ({
    seriesId: group[0].SeriesId, title: series[group[0].SeriesId]?.Name || group[0].SeriesName || '', text: newEpisodesText(group), episode: group[0], count: group.length,
  }));
  const rest = sorted.length - notices.length;
  const more = rest <= 0 ? null : trn(rest, 'Et une autre série que tu suis a un nouvel épisode.', 'Et {n} autres séries que tu suis ont de nouveaux épisodes.');
  return { notices, more, series: sorted.length };
}
