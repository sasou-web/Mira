// What a release's banner shows (tools/banner/banner.mjs), read from tools/banner/scenes.json and the version's entry
// of src/Mira.Core/WhatsNew.json (its English). Read by banner.yml (whether Mira for Windows, Mac or iPhone must be
// captured):
//   node tools/banner/plan.mjs <x.y.z>   → lines « template=… », « windows=true|false », « mac=… », « iphone=… »
//
// Two templates: « closeup » (every patch, x.y.z with z > 0) and « lineup » (x.y.0); "template" in the entry forces one.
// « film », the patches' template from 0.9.7 to 0.9.12 (an entry with a "band"), is kept for their entries only:
// ogsas chose « Gros plan » instead on 2026-11-15 (/mnt/project-files/banniere-release/propositions-2026-11-15/).
//
// closeup (« Gros plan »): one news, one real thing of the app whole and readable on the right, lit by a soft glow of
// the release's film; on the left Mira, the version and its date, the point's English title set large, its text below,
// and at the bottom « Also » with the other points' titles. scenes.json, one entry per version:
//   "0.9.13": { "film": "blade-runner-2049", "point": 1, "shot": "windows-player", "at": [530, 150], "size": [910, 730], "scale": 0.62 }
//   - film: a title of the banners' library (tools/banner/media.sh, its folder's name without the year, in lower case,
//     words joined by « - »: « howl-s-moving-castle »), else the version's own (FNV-1a), never the one of the release
//     before. Only its colours show: its backdrop (still-<film>.jpg of stills.mjs) blurred into a glow.
//   - point: the point of WhatsNew.json (from 1) the banner is about: its en.title is the title, its en.text the text
//     under it (« say » shortens it by hand: four lines at most), the other points' titles go to « Also » (« also »
//     by hand: a list, [] for none; those that do not fit on the line are left out, the first ones kept).
//     « title » writes the title by hand.
//   - shot: "windows-<screen>", "mac-<screen>" or "iphone-<screen>" (SHOTS below); at: the thing's top left in the
//     app's units (the capture's pixels ÷ 2, ÷ 3 for the iPhone); size: [width, height] in the same units; scale:
//     banner pixels per unit (0.45 to 1.5). It must fit 636 × 500 banner pixels; it sits centred between x = 500 and
//     1136 (« right »: its distance to the right edge instead), centred in height. « radius »: its corners (18).
//     Mac: the traffic lights drawn when it starts at [0, 0]; iPhone: its corners and Dynamic Island.
//   - glow: { "bright": 1.3, "vivid": 1.7 } the film's glow, brighter or more saturated for a dark film.
//   - no shot (a release of fixes only): Mira for Windows' Home, whole.
//   Every release its own banner (ogsas, 2026-11-14): the thing shown never cuts more than half of what the release
//   before cut from the same screen (its entry is kept for this: never prune the latest one), never Home twice in a row,
//   never the same film twice in a row. The newest version of WhatsNew.json must have its entry (plan.test.mjs, ci.yml).
//   What the thing shows must look finished (ogsas, 2026-11-18, after the first try of 0.9.13): every title whole, never
//   cut by « … » (pick a shorter one: a saga named « Mad Max Collection », not « Spider-Man: Spider-Verse Collection »);
//   its space filled (four titles or more in a row, never a half-empty page); the account's real photo, never its bare
//   initial (tools/gallery/setup.mjs gives it one); the rail lit on the page the thing belongs to. Look at the drawn
//   banner before taking it, and change the capture (tools/banner/media.sh, the gallery) rather than the crop when it fails.
//   And, from 0.9.13's banner (kept as it was by ogsas): a window shown whole (its close button in), framed tight enough
//   that no empty slot is left and the posters are large; the account's photo readable when it shows (simple and
//   contrasted, or a crop that shows it); the point's text names only what the image shows (« say » rewrites it).
//
// film (0.9.7 to 0.9.12): the release's film at the top right, melting into black as Mira shows a title; the version,
// its date and its sentence on the left (en.summary cut at its first comma: the start in bold); below, a band of real
// pieces of screens side by side, each under the English title of its point:
//   "0.9.7": { "film": "spider-verse", "band": [{ "point": 1, "shot": "windows-subtitles", "at": [109, 34], "scale": 1, "width": 284 }, …] }
//   - film: a film with its place in FILMS below. point: the caption (or "caption" by hand). at, scale (0.45 to 1.2),
//     width (banner pixels): pieces start at x = 56, 16 apart, and must reach x = 1144. No band: Windows' Home.
// lineup: the version and its summary over the film, Mira for Windows, Mac and iPhone in front of it.
//   "lineup": { "lead", "focus"?, "back", "phone" } the window in front (focus: [x, y, w, h] in fractions of it, lit),
//   the one behind and the phone; windows-home, mac-films and iphone-home by default.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const platformOf = (shot) => /^(windows|mac|iphone)-/.exec(shot ?? '')?.[1] ?? null;

// The screens each gallery captures. For a banner only: the films' filters open, Invincible's missing episodes at
// their place (tools/web/fake-tvdb) and, on Windows, the mini-player out against the right edge over Home, the player
// whole (paused, its controls shown) and the Downloads page at rest (« Régie », the README's posters, real releases' names and sizes).
export const SHOTS = {
  windows: ['home', 'film', 'series', 'films', 'subtitles', 'films-more', 'filters', 'missing', 'shelf', 'family', 'mini', 'player', 'downloads', 'settings', 'security', 'privacy', 'discord'],
  mac: ['home', 'film', 'series', 'films', 'settings', 'filters', 'missing'],
  iphone: ['home', 'movies', 'film', 'together', 'series'],
};
// Where each film's backdrop sits (banner pixels; 16:9, w wide; faded on the left over `fade` pixels): its subjects
// between x = 480 and 1200, nothing bright under the sentence (x < 470) nor under the captions (y > 282).
export const FILMS = {
  'spider-verse': { x: 372, y: -62, w: 910, fade: 290 },
  'wild-robot': { x: 330, y: -150, w: 960, fade: 300 },
  arcane: { x: 400, y: -40, w: 800, fade: 240 },
  coco: { x: 350, y: -10, w: 1000, fade: 290 },
  klaus: { x: 420, y: -20, w: 1050, fade: 250 },
  nimona: { x: 310, y: -30, w: 1000, fade: 260 },
  'puss-in-boots': { x: 350, y: -60, w: 1000, fade: 290 },
  'your-name': { x: 340, y: 0, w: 960, fade: 200 },
};

// The banner's geometry (CSS pixels of a 1200 × 600 page, drawn at ×2).
export const BAND = { left: 56, right: 1144, gap: 16, top: 318, caption: 288, bottom: 600 };
const SCALE = [0.45, 1.2];
const WHOLE = [{ shot: 'windows-home', at: [0, 0], scale: 0.756, width: 1088 }];

const units = (shot) => (platformOf(shot) === 'iphone' ? 3 : 2);
const fnv = (text) => {
  let seed = 0x811c9dc5;
  for (const c of text) seed = Math.imul(seed ^ c.charCodeAt(0), 0x01000193) >>> 0;
  return seed;
};

// The sentence: the summary cut at its first comma, the start (comma kept) in bold.
export function sentence(summary) {
  const i = summary.indexOf(',');
  return i < 0 ? { bold: summary.trim(), rest: '' } : { bold: summary.slice(0, i + 1).trim(), rest: summary.slice(i + 1).trim() };
}

// « November 13, 2026 » from « 2026-11-13 ».
export const longDate = (date) => new Date(`${date}T12:00:00Z`).toLocaleDateString('en-US', { timeZone: 'UTC', month: 'long', day: 'numeric', year: 'numeric' });

// The version's own film, never the one before it.
export function filmOf(version, scenes, news) {
  const own = scenes[version]?.film;
  if (own) return own;
  const names = Object.keys(FILMS).sort();
  const at = news.findIndex((x) => x.version === version);
  const before = at >= 0 ? news[at + 1]?.version : null;
  const avoid = before ? scenes[before]?.film ?? scenes[before]?.backdrop ?? names[fnv(before) % names.length] : null;
  const seed = fnv(version);
  const film = names[seed % names.length];
  return film === avoid ? names[(seed + 1) % names.length] : film;
}

// The band, checked and laid out: each piece's left, its caption and the capture it is cut from.
export function band(version, entry, items) {
  const pieces = entry.band?.length ? entry.band : WHOLE;
  const where = (i) => `${version}, piece ${i + 1}`;
  if (pieces.length > 4) throw new Error(`${version}: ${pieces.length} pieces in the band, 4 at most (each one would be too small to read)`);
  let left = BAND.left;
  const laid = pieces.map((piece, i) => {
    const platform = platformOf(piece.shot);
    if (!platform || !SHOTS[platform].includes(piece.shot.slice(platform.length + 1))) {
      throw new Error(`${where(i)}: no gallery takes ${piece.shot ?? '(no shot)'} (${Object.entries(SHOTS).map(([p, s]) => s.map((x) => `${p}-${x}`).join(', ')).join('; ')})`);
    }
    if (!Array.isArray(piece.at) || piece.at.length !== 2 || piece.at.some((v) => typeof v !== 'number' || v < 0)) throw new Error(`${where(i)}: at is [x, y] in the app's units`);
    if (typeof piece.scale !== 'number' || piece.scale < SCALE[0] || piece.scale > SCALE[1]) {
      throw new Error(`${where(i)}: scale ${piece.scale} is out of ${SCALE[0]} to ${SCALE[1]} (too small to read, or blurred)`);
    }
    if (typeof piece.width !== 'number' || piece.width < 80) throw new Error(`${where(i)}: width is the piece's width in banner pixels (80 at least)`);
    let caption = null;
    if (piece.caption != null) caption = String(piece.caption);
    else if (piece.point != null) {
      const point = items[piece.point - 1];
      if (!Number.isInteger(piece.point) || !point) throw new Error(`${where(i)}: point ${piece.point} is not one of the ${items.length} points of WhatsNew.json`);
      caption = point.en?.title ?? point.title;
    } else if (entry.band?.length) throw new Error(`${where(i)}: no point nor caption (each piece shows one of this version's news, under its title)`);
    const at = left;
    left += piece.width + BAND.gap;
    return { shot: piece.shot, at: piece.at, scale: piece.scale, width: piece.width, left: at, caption, units: units(piece.shot) };
  });
  const end = left - BAND.gap;
  if (end < BAND.right) throw new Error(`${version}: the band ends at x = ${end}, short of ${BAND.right} (widen a piece by ${BAND.right - end} px)`);
  return laid;
}

// ——— closeup ———
// The banner's geometry: the words on the left, the thing shown in the room on the right.
export const CLOSEUP = { left: 64, words: 420, room: [500, 1136], width: 636, height: 500, scale: [0.45, 1.5], radius: 18 };
const HOME = { shot: 'windows-home', at: [0, 0], size: [1440, 900], scale: 0.44 };

// A title of the library by its folder's name: « Howl's Moving Castle (2004) » → « howl-s-moving-castle ».
export const slug = (name) => name.replace(/\s*\(\d{4}\)\s*$/, '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()
  .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
// The banners' library (tools/banner/media.sh): every film and series, whose backdrops stills.mjs takes.
export function library(script = readFileSync(join(root, 'tools/banner/media.sh'), 'utf8')) {
  return [...new Set([...script.matchAll(/^\s*(?:film|series) "([^"]+)"/gm)].map((m) => slug(m[1])))];
}
// The films of « film » by their short names, the same titles in the library.
const ALIASES = { 'spider-verse': 'spider-man-across-the-spider-verse', 'wild-robot': 'the-wild-robot', 'puss-in-boots': 'puss-in-boots-the-last-wish' };
const titleOf = (film) => (film ? ALIASES[film] ?? film : null);
// Titles whose backdrop is no film's colours.
export const NO_GLOW = { 'spirited-away': 'its first backdrop on TMDb is a photo of Hayao Miyazaki' };

// The version's own glow, never the film of the release before.
export function glowOf(version, scenes, news, titles = library()) {
  const own = scenes[version]?.film;
  if (own) return own;
  const last = before(version, scenes, news);
  const lastFilm = !last ? null : last.entry.film ?? (templateOf(last.version, last.entry) === 'closeup' ? glowOf(last.version, scenes, news, titles) : filmOf(last.version, scenes, news));
  const avoid = titleOf(lastFilm);
  const names = titles.filter((x) => !NO_GLOW[x]).sort(), seed = fnv(version);
  const film = names[seed % names.length];
  return film === avoid ? names[(seed + 1) % names.length] : film;
}

// The thing shown, checked and placed, and the words around it.
export function closeUp(version, entry, release) {
  const items = release.items;
  const thing = entry.shot ? entry : HOME;
  const platform = platformOf(thing.shot);
  if (!platform || !SHOTS[platform].includes(thing.shot.slice(platform.length + 1))) {
    throw new Error(`${version}: no gallery takes ${thing.shot} (${Object.entries(SHOTS).map(([p, s]) => s.map((x) => `${p}-${x}`).join(', ')).join('; ')})`);
  }
  const numbers = (v, n) => Array.isArray(v) && v.length === n && v.every((x) => typeof x === 'number' && x >= 0);
  if (!numbers(thing.at, 2)) throw new Error(`${version}: at is [x, y] in the app's units`);
  if (!numbers(thing.size, 2) || thing.size.some((x) => x <= 0)) throw new Error(`${version}: size is [width, height] in the app's units`);
  if (thing !== HOME && (typeof thing.scale !== 'number' || thing.scale < CLOSEUP.scale[0] || thing.scale > CLOSEUP.scale[1])) {
    throw new Error(`${version}: scale ${thing.scale} is out of ${CLOSEUP.scale[0]} to ${CLOSEUP.scale[1]} (too small to read, or blurred)`);
  }
  const width = Math.round(thing.size[0] * thing.scale), height = Math.round(thing.size[1] * thing.scale);
  if (width > CLOSEUP.width) throw new Error(`${version}: the thing shown is ${width} px wide, ${CLOSEUP.width} at most (lower its scale to ${Math.floor((CLOSEUP.width / thing.size[0]) * 100) / 100} or cut it narrower)`);
  if (height > CLOSEUP.height) throw new Error(`${version}: the thing shown is ${height} px high, ${CLOSEUP.height} at most (lower its scale to ${Math.floor((CLOSEUP.height / thing.size[1]) * 100) / 100} or cut it lower)`);
  let left = CLOSEUP.room[0] + Math.round((CLOSEUP.width - width) / 2);
  if (entry.right != null) {
    if (typeof entry.right !== 'number') throw new Error(`${version}: right is a number of banner pixels`);
    left = 1200 - entry.right - width;
    if (left < CLOSEUP.room[0]) throw new Error(`${version}: right ${entry.right} puts the thing shown at x = ${left}, over the words (${CLOSEUP.room[0]} at least)`);
  }
  let point = null;
  if (entry.point != null) {
    point = items[entry.point - 1];
    if (!Number.isInteger(entry.point) || !point) throw new Error(`${version}: point ${entry.point} is not one of the ${items.length} points of WhatsNew.json`);
  } else if (entry.title == null) throw new Error(`${version}: no point nor title (the banner is about one of this version's news)`);
  const title = String(entry.title ?? point.en?.title ?? point.title);
  const say = String(entry.say ?? (point ? point.en?.text ?? point.text : release.en?.summary ?? release.summary));
  if (entry.also != null && !(Array.isArray(entry.also) && entry.also.every((x) => typeof x === 'string'))) throw new Error(`${version}: also is a list of titles`);
  const also = entry.also ?? items.filter((x) => x !== point).map((x) => x.en?.title ?? x.title);
  const glow = { bright: entry.glow?.bright ?? 1.3, vivid: entry.glow?.vivid ?? 1.7 };
  return {
    shot: thing.shot, at: thing.at, size: thing.size, scale: thing.scale, units: units(thing.shot), radius: entry.radius ?? CLOSEUP.radius,
    left, top: Math.round((600 - height) / 2), width, height, title, say, also, glow,
  };
}

// What a piece cuts from its capture, in the app's units.
const cut = (piece) => ({ shot: piece.shot, x: piece.at[0], y: piece.at[1], w: piece.width / piece.scale, h: (BAND.bottom - BAND.top + 2) / piece.scale });
const overlap = (a, b) => Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));

// The release before this one (the next entry of WhatsNew.json) and its scene, if scenes.json still has it.
export function before(version, scenes, news) {
  const at = news.findIndex((x) => x.version === version);
  const previous = at >= 0 ? news[at + 1]?.version : null;
  return previous && scenes[previous] ? { version: previous, entry: scenes[previous] } : null;
}

// What an entry cuts from its captures, film or closeup (null for lineup).
function cuts(entry, template) {
  if (template === 'film') return (entry.band?.length ? entry.band : WHOLE).map(cut);
  if (template === 'closeup') {
    const t = entry.shot ? entry : HOME;
    return [{ shot: t.shot, x: t.at?.[0] ?? 0, y: t.at?.[1] ?? 0, w: t.size?.[0] ?? 0, h: t.size?.[1] ?? 0 }];
  }
  return null;
}
const templateOf = (version, entry) => entry.template ?? (Number(version.split('.')[2]) > 0 ? (entry.band ? 'film' : 'closeup') : 'lineup');
const homeOnly = (entry, template) => (template === 'film' ? !entry.band?.length : !entry.shot);

// Never the banner of the release before: a thing that cuts more than half of what it cut from the same screen, Home
// twice in a row, the same glow twice in a row, the same lineup twice in a row.
function repeats(version, entry, template, scenes, news, film) {
  const last = before(version, scenes, news);
  if (!last) return;
  const lastTemplate = templateOf(last.version, last.entry);
  if (template === 'lineup' || lastTemplate === 'lineup') {
    if (template !== lastTemplate) return;
    const l = (e) => JSON.stringify([e.lineup?.lead ?? 'windows-home', e.lineup?.back ?? 'mac-films', e.lineup?.phone ?? 'iphone-home', e.lineup?.focus ?? null]);
    if (l(entry) === l(last.entry)) throw new Error(`${version}: the same lineup as ${last.version}: show this version's own screens`);
    return;
  }
  if (template === 'closeup' && last.entry.film && titleOf(film) === titleOf(last.entry.film)) throw new Error(`${version}: the film ${film} again, as ${last.version}: take another one`);
  if (homeOnly(entry, template) && homeOnly(last.entry, lastTemplate)) throw new Error(`${version}: no ${template === 'film' ? 'band' : 'shot'}, as ${last.version}: show at least one of this version's points`);
  const theirs = cuts(last.entry, lastTemplate);
  cuts(entry, template).forEach((a, i) => {
    for (const b of theirs) {
      if (a.shot === b.shot && overlap(a, b) > 0.5 * Math.min(a.w * a.h, b.w * b.h)) {
        const at = template === 'film' ? (entry.band?.length ? entry.band : WHOLE)[i].at : (entry.shot ? entry : HOME).at;
        throw new Error(`${version}${template === 'film' ? `, piece ${i + 1}` : ''}: ${a.shot} at [${at}] shows again what ${last.version}'s banner showed: show this version's own news`);
      }
    }
  });
}

export function plan(version, { template: forced = null, scenes, news } = {}) {
  if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) throw new Error(`not a version: ${version}`);
  scenes ??= JSON.parse(readFileSync(join(root, 'tools/banner/scenes.json'), 'utf8'));
  news ??= JSON.parse(readFileSync(join(root, 'src/Mira.Core/WhatsNew.json'), 'utf8'));
  const entry = scenes[version];
  if (!entry) throw new Error(`tools/banner/scenes.json has no entry for ${version}`);
  const release = news.find((x) => x.version === version);
  if (!release) throw new Error(`src/Mira.Core/WhatsNew.json has no entry for ${version}`);
  const template = forced ?? templateOf(version, entry);
  if (!['closeup', 'film', 'lineup'].includes(template)) throw new Error(`${version}: unknown template ${template} (closeup, film or lineup)`);
  let film;
  if (template === 'closeup') {
    const titles = library();
    film = glowOf(version, scenes, news, titles);
    if (NO_GLOW[titleOf(film)]) throw new Error(`${version}: not ${film}: ${NO_GLOW[titleOf(film)]}`);
    if (!titles.includes(titleOf(film))) throw new Error(`${version}: the film ${film} is not in the banners' library (tools/banner/media.sh: ${titles.join(', ')})`);
  } else {
    film = filmOf(version, scenes, news);
    if (!FILMS[film]) throw new Error(`${version}: the film ${film} has no place in FILMS of plan.mjs (${Object.keys(FILMS).join(', ')})`);
  }
  const summary = release.en?.summary ?? release.summary;
  let pieces = [], lineup = null, close = null;
  if (template === 'film') pieces = band(version, entry, release.items);
  else if (template === 'closeup') close = closeUp(version, entry, release);
  else {
    const l = entry.lineup ?? {};
    lineup = { lead: l.lead ?? 'windows-home', back: l.back ?? 'mac-films', phone: l.phone ?? 'iphone-home', focus: l.focus ?? null };
    if (lineup.focus && (lineup.focus.length !== 4 || lineup.focus.some((v) => v < 0 || v > 1))) throw new Error(`${version}: lineup.focus is four fractions of the capture`);
    for (const s of [lineup.lead, lineup.back, lineup.phone]) {
      const p = platformOf(s);
      if (!p || !SHOTS[p].includes(s.slice(p.length + 1))) throw new Error(`${version}: no gallery takes ${s}`);
    }
  }
  repeats(version, entry, template, scenes, news, film);
  const shots = [...new Set([...pieces.map((x) => x.shot), close?.shot, lineup?.lead, lineup?.back, lineup?.phone].filter(Boolean))];
  return {
    version, template, date: longDate(release.date), summary, sentence: sentence(summary), film, still: template === 'closeup' ? titleOf(film) : film, place: FILMS[film] ?? null, band: pieces, closeup: close, lineup, shots,
    windows: shots.some((s) => platformOf(s) === 'windows'), mac: shots.some((s) => platformOf(s) === 'mac'), iphone: shots.some((s) => platformOf(s) === 'iphone'),
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const p = plan(process.argv[2]);
    console.log(`template=${p.template}\nwindows=${p.windows}\nmac=${p.mac}\niphone=${p.iphone}`);
  } catch (e) { console.error(e.message); process.exit(1); }
}
