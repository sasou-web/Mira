// Titles Jellyfin recognized badly, for an administrator, as Mira for Windows and Mac judge them (TitleIdentity of
// Mira.Core; cases shared in tests/fixtures/identify.json): « Titres à corriger » (views/fix.js) lists the films and
// series matched to nothing, without a poster or a year, or twice; « Identifier… » on a title's page (views/detail.js)
// asks Jellyfin for what its metadata sources propose, Jellyfin speaking to TMDb as it already does, never the phone.
// Nothing of the browser at load: Node imports it as it is.
import { tr } from './i18n.js';
import { titleKey, parseRelease } from './releases.js';

/** What is wrong with a title, in the order the list says it. */
export const PROBLEMS = ['Unidentified', 'Duplicate', 'NoPoster', 'NoYear', 'NoOverview'];
/** The fields Jellyfin is asked for to judge a title. */
export const FIELDS = 'ProviderIds,Overview,ProductionYear';

// Jellyfin names a film without metadata after its folder: « Dune (2021) ».
const YEAR = /\s*\(((?:18|19|20)\d\d)\)\s*$/;
const nameKey = (name) => titleKey(String(name ?? '').replace(YEAR, ''));
const folderYear = (name) => { const m = String(name ?? '').match(YEAR); return m ? Number(m[1]) : null; };
const filled = (value) => typeof value === 'string' && value.trim() !== '';

/** Whether Jellyfin matched the title to anything: one id of a metadata source, whichever. */
export const identified = (item) => Object.values(item?.ProviderIds ?? {}).some(filled);

/** Two titles of one kind that are the same: an id of a metadata source in common, or the same name and year. */
function same(a, b) {
  if (a.Type !== b.Type) return false;
  const others = Object.entries(b.ProviderIds ?? {});
  const shared = Object.entries(a.ProviderIds ?? {}).some(([key, value]) => filled(value)
    && others.some(([k, v]) => k.toLowerCase() === key.toLowerCase() && String(v ?? '').trim().toLowerCase() === value.trim().toLowerCase()));
  if (shared) return true;
  const key = nameKey(a.Name);
  return key !== '' && key === nameKey(b.Name) && (a.ProductionYear ?? folderYear(a.Name)) === (b.ProductionYear ?? folderYear(b.Name));
}

/**
 * The films and series that need a look: [{ item, problems, twin }], those Jellyfin did not recognize first, then
 * the duplicates, then the rest, by name within each. A film without a poster of its own has one all the same (a
 * frame of the video): only « Non reconnu » says it.
 */
export function checkTitles(titles) {
  const list = (titles ?? []).filter((x) => x.Type === 'Movie' || x.Type === 'Series');
  const entries = [];
  for (const item of list) {
    const problems = [];
    const known = identified(item);
    if (!known) problems.push('Unidentified');
    const twin = list.find((x) => x.Id !== item.Id && same(item, x)) ?? null;
    if (twin) problems.push('Duplicate');
    if (!item.ImageTags?.Primary) problems.push('NoPoster');
    if (item.ProductionYear == null) problems.push('NoYear');
    if (known && !filled(item.Overview)) problems.push('NoOverview');
    if (problems.length) entries.push({ item, problems, twin });
  }
  const name = (x) => String(x.item.Name ?? '');
  return entries.sort((a, b) => PROBLEMS.indexOf(a.problems[0]) - PROBLEMS.indexOf(b.problems[0])
    || name(a).localeCompare(name(b), undefined, { sensitivity: 'accent' }) || (a.item.Id < b.item.Id ? -1 : a.item.Id > b.item.Id ? 1 : 0));
}

/** « Dune (2021) », the year added unless the name already ends with it. */
export const named = (item) => (item.ProductionYear && folderYear(item.Name) == null ? `${item.Name} (${item.ProductionYear})` : String(item.Name ?? ''));

export function problemText(problem, twin = null) {
  switch (problem) {
    case 'Unidentified': return tr('Non reconnu');
    case 'Duplicate': return twin ? tr('En double avec « {name} »', { name: named(twin) }) : tr('En double');
    case 'NoPoster': return tr('Sans affiche');
    case 'NoYear': return tr('Sans année');
    default: return tr('Sans résumé');
  }
}
/** « Non reconnu · Sans affiche ». */
export const entryText = (entry) => entry.problems.map((p) => problemText(p, entry.twin)).join(' · ');

/**
 * What « Chercher » sends: { name, year, providerIds }. A link to TMDb or an IMDb id (or its page) finds that title and
 * nothing else; otherwise the name, with the year of the year box, else the one the name ends with.
 */
export function searchQuery(text, year) {
  const typed = String(text ?? '').trim();
  const tmdb = typed.match(/themoviedb\.org\/(?:movie|tv)\/(\d+)/i);
  if (tmdb) return { name: '', year: null, providerIds: { Tmdb: tmdb[1] } };
  const imdb = typed.match(/\b(tt\d{6,10})\b/i);
  if (imdb) return { name: '', year: null, providerIds: { Imdb: imdb[1].toLowerCase() } };
  const box = String(year ?? '').trim();
  const chosen = /^\d+$/.test(box) && Number(box) >= 1880 && Number(box) <= 2100 ? Number(box) : folderYear(typed);
  return { name: typed.replace(YEAR, '').trim(), year: chosen, providerIds: {} };
}

/**
 * The search « Identifier… » opens with: { name, year }, the name without the year of its folder; a name left as its
 * file had it (« Dune.Part.Two.2024.MULTi.1080p ») read as a release name is.
 */
export function prefill(item) {
  const name = String(item?.Name ?? '').trim();
  if (/^\S+[._]\S+$/.test(name)) {
    const release = parseRelease(name);
    if (release.title) return { name: release.title, year: release.year ?? item.ProductionYear ?? null };
  }
  return { name: name.replace(YEAR, '').trim(), year: item?.ProductionYear ?? folderYear(name) };
}

/** A proposal of Jellyfin (its RemoteSearchResult): its year, from its date when it has none. */
export function matchYear(match) {
  if (Number.isInteger(match?.ProductionYear)) return match.ProductionYear;
  const date = String(match?.PremiereDate ?? '');
  return /^\d{4}/.test(date) ? Number(date.slice(0, 4)) : null;
}
/** « 2021 · TheMovieDb » under a proposal's name. */
export const matchLine = (match) => [matchYear(match), match?.SearchProviderName].filter((x) => x != null && String(x).trim() !== '').join(' · ');

/** The address of a proposal's poster, fetched by Mira's extension for an administrator (never by the phone from TMDb). */
export const imagePath = (url) => `Mira/identify/image?url=${encodeURIComponent(url)}`;
