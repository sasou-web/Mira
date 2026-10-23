// « Télécharger sur le PC serveur »: whether a title found is already in the library, so that it is not downloaded
// twice (views/torlink.js), as Mira for Windows and Mac say it (OwnedTitles of Mira.Core; cases shared in
// tests/fixtures/owned.json). Jellyfin is asked once per title, never anything else. Nothing of the browser at load:
// Node imports it as it is.
import { tr, trn } from './i18n.js';
import { titleKey, episodeLabel } from './releases.js';

/** Titles looked for at most, the first of a search: a search of 60 results asks Jellyfin a dozen times at most. */
export const MAX_LOOKUPS = 10;

// Jellyfin names a film without metadata after its folder: « Dune (2021) ».
const YEAR = /\s*\((?:19|20)\d\d\)\s*$/;
const nameKey = (name) => titleKey(String(name ?? '').replace(YEAR, ''));
const filmLike = (group) => group.release.episode === null && !group.release.pack;

/**
 * The titles of groupReleases to look for in Jellyfin, once each and in their order: [{ key, title, film }], at most
 * MAX_LOOKUPS. `key` is the lookup of a group (lookupKey).
 */
export function ownedLookups(groups) {
  const found = new Map();
  for (const group of groups ?? []) {
    const key = lookupKey(group);
    if (!found.has(key) && found.size < MAX_LOOKUPS) found.set(key, { key, title: group.title.replace(YEAR, ''), film: filmLike(group) });
  }
  return [...found.values()];
}

/** A film and an episode of the same name are looked for apart: one among the films, the other among the series. */
export const lookupKey = (group) => `${filmLike(group) ? 'film' : 'show'}|${group.show}`;

/**
 * What the library has of a group: { text, itemId, owned } or null. `items`: the films and series Jellyfin found for
 * its title (Name, OriginalTitle, ProductionYear, Type, Id); `episodes`: those of the series found, without missing ones
 * (ParentIndexNumber, IndexNumber, IndexNumberEnd). `owned`: this very film, episode or season; else the series only.
 */
export function ownedLine(group, items, episodes = []) {
  const keys = new Set([group.show, nameKey(group.title), group.release.alternate ? titleKey(group.release.alternate) : null].filter(Boolean));
  const named = (x) => keys.has(nameKey(x.Name)) || (x.OriginalTitle && keys.has(nameKey(x.OriginalTitle)));
  const { release } = group;
  if (filmLike(group)) {
    const year = release.year;
    const film = (items ?? []).find((x) => x.Type === 'Movie' && named(x) && !(year && x.ProductionYear && Math.abs(x.ProductionYear - year) > 1));
    return film ? { text: tr('Déjà dans ta bibliothèque'), itemId: film.Id, owned: true } : null;
  }
  const series = (items ?? []).find((x) => x.Type === 'Series' && named(x));
  if (!series) return null;
  const own = (episodes ?? []).filter((x) => !x.SeriesId || x.SeriesId === series.Id);
  const has = (season, number) => own.some((x) => (season === null || x.ParentIndexNumber === season)
    && (x.IndexNumber === number || (x.IndexNumberEnd && x.IndexNumber <= number && number <= x.IndexNumberEnd)));
  const show = { itemId: series.Id, owned: false };
  if (release.pack) {
    // A season (or a range of it), or every season: what of it is already there.
    const inside = own.filter((x) => (release.season === null || x.ParentIndexNumber === release.season)
      && (release.episode === null || release.lastEpisode === null || (x.IndexNumber >= release.episode && x.IndexNumber <= release.lastEpisode)));
    const n = inside.length;
    if (!n) return { ...show, text: release.season === null ? tr('Série dans ta bibliothèque') : tr('Série dans ta bibliothèque, pas cette saison') };
    return {
      ...show, owned: true,
      text: release.season === null ? trn(n, 'Tu as déjà {n} épisode de cette série', 'Tu as déjà {n} épisodes de cette série')
        : trn(n, 'Tu as déjà {n} épisode de la saison {season}', 'Tu as déjà {n} épisodes de la saison {season}', { season: release.season }),
    };
  }
  // Numbered without a season (an anime): Jellyfin numbers it by season, the number says nothing of it there.
  if (release.absolute) return { ...show, text: tr('Série dans ta bibliothèque') };
  const wanted = [];
  for (let n = release.episode; n <= (release.lastEpisode ?? release.episode); n++) wanted.push(n);
  return wanted.every((n) => has(release.season, n))
    ? { ...show, owned: true, text: tr('Tu as déjà {label}', { label: episodeLabel(release) }) }
    : { ...show, text: tr('Série dans ta bibliothèque, pas cet épisode') };
}
