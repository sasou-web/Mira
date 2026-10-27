// A series' seasons on its title page: the season list (long ones in slices of 100, as on Windows), what « Marquer la
// saison comme vue » marks, and the season of the episode Lecture plays when another is shown. As EpisodePages of
// Mira.Core (cases shared in tests/fixtures/seasons.json). Imported by Node's tests: nothing of the browser here.
import { tr } from './i18n.js';

const SLICE = 100;
export const seasonOf = (x) => x.ParentIndexNumber ?? 1;

/** The season list: a season, or slices of a long one (« Saison 1 · 101–200 »). */
export function episodePages(episodes) {
  const seasons = new Map();
  for (const e of episodes) { const s = seasonOf(e); if (!seasons.has(s)) seasons.set(s, []); seasons.get(s).push(e); }
  const pages = [];
  for (const [season, list] of [...seasons].sort((a, b) => a[0] - b[0])) {
    const name = season === 0 ? tr('Épisodes spéciaux') : tr('Saison {n}', { n: season });
    if (list.length <= SLICE) { pages.push({ season, skip: 0, take: list.length, label: name }); continue; }
    for (let skip = 0; skip < list.length; skip += SLICE) {
      const take = Math.min(SLICE, list.length - skip);
      const first = list[skip].IndexNumber ?? skip + 1, last = list[skip + take - 1].IndexNumber ?? skip + take;
      pages.push({ season, skip, take, label: first === last ? `${name} · ${first}` : `${name} · ${first}–${last}` });
    }
  }
  return pages;
}
export const pageEpisodes = (episodes, page) => episodes.filter((x) => seasonOf(x) === page.season).slice(page.skip, page.skip + page.take);
export function pageHolding(pages, episodes, episode) {
  if (!episode) return null;
  const position = episodes.filter((x) => seasonOf(x) === seasonOf(episode)).findIndex((x) => x.Id === episode.Id);
  return position < 0 ? null : pages.find((p) => p.season === seasonOf(episode) && position >= p.skip && position < p.skip + p.take) ?? null;
}

/** A season of its own to mark only beside another: with one, the series' own button does it. */
export const marksSeasons = (episodes) => new Set(episodes.map(seasonOf)).size > 1;

/**
 * What « Marquer la saison comme vue » marks: the season as Jellyfin holds it (one call, every episode with it), else
 * each episode (episodes without a season of Jellyfin's, or split between two). played: all its episodes are watched,
 * and the button offers « non vue ».
 */
export function seasonMark(episodes, season) {
  const list = episodes.filter((x) => seasonOf(x) === season);
  const held = new Set(list.map((x) => x.SeasonId));
  return { ids: held.size === 1 && list[0]?.SeasonId ? [list[0].SeasonId] : list.map((x) => x.Id), played: list.length > 0 && list.every((x) => !!x.UserData?.Played) };
}

/** The page holding the episode Lecture plays when another season is shown (« À suivre », above its episodes); else null. */
export function nextElsewhere(pages, episodes, shown, next) {
  const holding = pageHolding(pages, episodes, next);
  return holding && shown && (holding.season !== shown.season || holding.skip !== shown.skip) ? holding : null;
}
