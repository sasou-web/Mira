// « Chercher une meilleure version » of a film or an episode (an administrator): the results of TorLink that are this
// very title, in a better picture than its file, as Mira for Windows and Mac judge them (TitleUpgrade of Mira.Core;
// cases shared in tests/fixtures/upgrade.json). Never a second version kept beside: on the PC of Jellyfin, the old file
// goes to the Recycle Bin once the new one is checked. Nothing of the browser at load: Node imports it as it is.
import { tr } from './i18n.js';
import { parseRelease, titleKey, sourceLabel } from './releases.js';

/** Its length may differ by this share at most from the old one's: another cut, or another title, otherwise. */
export const LENGTH_TOLERANCE = 0.02;

// From the worst to the best; a version filmed in a theatre is below everything.
const SOURCE_TIER = { cam: 0, dvd: 1, hdtv: 2, hdrip: 2, webrip: 3, webdl: 4, bluray: 5, remux: 6 };
// A release that does not say where it comes from: often a re-encode of anything.
const UNKNOWN_RELEASE = 2;

// Jellyfin names a film without metadata after its folder: « Dune (2021) ».
const YEAR = /\s*\((?:19|20)\d\d\)\s*$/;
const nameKey = (name) => titleKey(String(name ?? '').replace(YEAR, ''));
// Japanese, Chinese, Korean…: an original title in another script is no name to search releases by.
const latin = (text) => text.split('').every((c) => !/\p{L}/u.test(c) || c <= 'ɏ' || (c >= 'Ḁ' && c <= 'ỿ'));

/**
 * The picture of a title's file: { resolution (2160, 1440, 1080, 720, 480), source } by the width or height of its first
 * video stream (a film cropped to 3840 × 1600 is still 4K) and its file's name, else its folder's; null when unknown.
 */
export function currentQuality(item) {
  const source = item?.MediaSources?.[0];
  const video = source?.MediaStreams?.find((x) => x.Type === 'Video');
  return quality(video?.Width ?? 0, video?.Height ?? 0, source?.Path ?? item?.Path);
}

/** The picture of a file of this width and height (0 when unknown) at this path. */
export function quality(width, height, path) {
  const resolution = width >= 3800 || height >= 2000 ? 2160 : width >= 2500 || height >= 1400 ? 1440 : width >= 1800 || height >= 1000 ? 1080
    : width >= 1200 || height >= 700 ? 720 : width > 0 || height > 0 ? 480 : null;
  const parts = String(path ?? '').replace(/\\/g, '/').split('/').filter(Boolean);
  const source = parts.length ? parseRelease(parts[parts.length - 1]).source ?? (parts.length > 1 ? parseRelease(parts[parts.length - 2]).source : null) : null;
  return { resolution, source };
}

const resolutionTier = (resolution) => resolution >= 2160 ? 4 : resolution >= 1080 ? 3 : resolution >= 720 ? 2 : resolution > 0 ? 1 : 0;

/**
 * Whether a release (parseRelease) has a better picture than the file: a higher resolution from a source as good or one
 * step below (a 1080p WEB-DL beats a 720p BluRay), never a 4K from a lesser source; the same resolution from a better
 * source. Anything beats a version filmed in a theatre; a release that does not say its resolution, a whole season or a
 * version filmed in a theatre is never offered. A file whose source is unknown counts as a WEB-DL (a DVD under 720p).
 */
export function better(current, release) {
  if (release.pack || release.source === 'cam' || !release.resolution) return false;
  const r = resolutionTier(release.resolution), c = resolutionTier(current.resolution);
  if (current.source === 'cam') return r >= 2;
  const rs = SOURCE_TIER[release.source] ?? UNKNOWN_RELEASE;
  const cs = SOURCE_TIER[current.source] ?? (c <= 1 ? SOURCE_TIER.dvd : SOURCE_TIER.webdl);
  if (r > c) return rs >= cs - (release.resolution >= 2160 ? 0 : 1);
  return r === c && rs > cs;
}

/**
 * Whether a release is this very title: a film of the same name (or original name) and year (±1, a release without a
 * year passes); an episode of the series under the same season and number (one numbered without a season only in its
 * first season). Never a whole season or several films.
 */
export function fits(item, release) {
  if (release.pack) return false;
  const keys = new Set([titleKey(release.title)]);
  if (release.alternate) keys.add(titleKey(release.alternate));
  if (item.Type === 'Movie') {
    if (release.episode !== null) return false;
    const named = keys.has(nameKey(item.Name)) || (!!item.OriginalTitle && keys.has(nameKey(item.OriginalTitle)));
    return named && !(release.year && item.ProductionYear && Math.abs(item.ProductionYear - release.year) > 1);
  }
  const number = item.IndexNumber;
  if (item.Type !== 'Episode' || release.episode === null || number == null || !keys.has(nameKey(item.SeriesName))) return false;
  if (release.absolute ? item.ParentIndexNumber !== 1 : release.season !== (item.ParentIndexNumber ?? null)) return false;
  return release.episode === number && (release.lastEpisode ?? release.episode) === (item.IndexNumberEnd ?? number);
}

const two = (n) => String(n ?? 1).padStart(2, '0');
const clean = (text) => String(text ?? '').replace(YEAR, '').replace(/[:;/\\|·–—]+/g, ' ').replace(/\s+/g, ' ').trim();

/** What TorLink searches: a film by its original name when written in Latin letters (releases mostly use it), and its year; an episode as « Show S01E02 ». */
export function upgradeQuery(item) {
  if (item.Type === 'Episode') return clean(`${item.SeriesName ?? ''} S${two(item.ParentIndexNumber)}E${two(item.IndexNumber)}`);
  const name = item.OriginalTitle && latin(item.OriginalTitle) ? item.OriginalTitle : item.Name;
  return clean(item.ProductionYear ? `${clean(name)} ${item.ProductionYear}` : name);
}

/** The results of a search that are this title in a better picture than its file. */
export function candidates(item, hits) {
  const current = currentQuality(item);
  return hits.filter((hit) => { const release = parseRelease(hit.name); return fits(item, release) && better(current, release); });
}

/** The new file lasts as long as the old one, within LENGTH_TOLERANCE (seconds). */
export function sameLength(old, found) {
  return old > 0 && found > 0 && Math.abs(found - old) <= old * LENGTH_TOLERANCE;
}

/** « 720p · WEBRip », « 1080p · source inconnue », « Filmé en salle »: the picture of a file, in the words of the page. */
export function qualityText({ resolution, source }) {
  const shown = resolution === 2160 ? '4K' : resolution === 480 ? 'SD' : resolution ? `${resolution}p` : tr('Définition inconnue');
  return [shown, source ? sourceLabel(source) : tr('source inconnue')].join(' · ');
}

/** « Dune (2021) », « The Bear · S01E02 »: the title replaced, in a list or a sentence. */
export function upgradeTitle(item) {
  if (item.Type === 'Episode') return `${item.SeriesName ?? ''} · S${two(item.ParentIndexNumber)}E${two(item.IndexNumber)}`;
  return item.ProductionYear && !YEAR.test(item.Name) ? `${item.Name} (${item.ProductionYear})` : item.Name;
}
