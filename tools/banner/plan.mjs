// What a release's banner shows (tools/banner/banner.mjs), read from tools/banner/scenes.json and the version's entry
// of src/Mira.Core/WhatsNew.json (its English). Read by banner.yml (whether Mira for Windows, Mac or iPhone must be
// captured):
//   node tools/banner/plan.mjs <x.y.z>   → lines « template=… », « windows=true|false », « mac=… », « iphone=… »
//
// Two templates: « film » (every patch, x.y.z with z > 0) and « lineup » (x.y.0); "template" in the entry forces one.
//
// film: the release's film at the top right, melting into black as Mira shows a title; the version, its date and its
// sentence on the left (en.summary cut at its first comma: the start in bold); below, a band of real pieces of screens
// side by side, each under the English title of its point. scenes.json, one entry per version:
//   "0.9.7": {
//     "film": "spider-verse",
//     "band": [
//       { "point": 1, "shot": "windows-subtitles", "at": [109, 34], "scale": 1, "width": 284 },
//       { "point": 2, "shot": "windows-series", "at": [93, 86], "scale": 0.578, "width": 576 },
//       { "point": 3, "shot": "mac-home", "at": [0, 0], "scale": 0.5, "width": 520 }
//     ]
//   }
//   - film: a film of the library with its place in FILMS below (still-<name>.jpg of stills.mjs), else the version's
//     own (FNV-1a), never the one of the release before.
//   - point: the point of WhatsNew.json (from 1) whose English title is the caption; "caption" writes it by hand;
//     neither: no caption. A point that shows on no screen has no piece: it stays in the sentence.
//   - shot: "windows-<screen>" (Mira.exe --readme-gallery), "mac-<screen>" (Mira --gallery) or "iphone-<screen>"
//     (testGallery of MiraUITests), screens in SHOTS below.
//   - at: the piece's top left in the app's own units (the capture's pixels ÷ 2, ÷ 3 for the iPhone); scale: banner
//     pixels per unit (0.45 to 1.2: readable at 860 px on GitHub, 480 px on Discord); width: the piece's width in
//     banner pixels. Pieces start at x = 56, 16 apart, and must reach x = 1144 (the last one may run off the edge).
//     Mac: the traffic lights drawn when the piece starts at [0, 0]; iPhone: its corners and Dynamic Island.
//   - no band (a release of fixes only): Mira for Windows' Home, whole, without a caption.
// lineup: the version and its summary over the film, Mira for Windows, Mac and iPhone in front of it.
//   "lineup": { "lead", "focus"?, "back", "phone" } the window in front (focus: [x, y, w, h] in fractions of it, lit),
//   the one behind and the phone; windows-home, mac-films and iphone-home by default.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const platformOf = (shot) => /^(windows|mac|iphone)-/.exec(shot ?? '')?.[1] ?? null;

// The screens each gallery captures. For a banner only: the films' filters open, Invincible's missing episodes at
// their place (tools/web/fake-tvdb) and, on Windows, the mini-player out against the right edge over Home.
export const SHOTS = {
  windows: ['home', 'film', 'series', 'films', 'subtitles', 'filters', 'missing', 'mini'],
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
    }
    const at = left;
    left += piece.width + BAND.gap;
    return { shot: piece.shot, at: piece.at, scale: piece.scale, width: piece.width, left: at, caption, units: units(piece.shot) };
  });
  const end = left - BAND.gap;
  if (end < BAND.right) throw new Error(`${version}: the band ends at x = ${end}, short of ${BAND.right} (widen a piece by ${BAND.right - end} px)`);
  return laid;
}

export function plan(version, { template: forced = null, scenes, news } = {}) {
  if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) throw new Error(`not a version: ${version}`);
  scenes ??= JSON.parse(readFileSync(join(root, 'tools/banner/scenes.json'), 'utf8'));
  news ??= JSON.parse(readFileSync(join(root, 'src/Mira.Core/WhatsNew.json'), 'utf8'));
  const entry = scenes[version];
  if (!entry) throw new Error(`tools/banner/scenes.json has no entry for ${version}`);
  const release = news.find((x) => x.version === version);
  if (!release) throw new Error(`src/Mira.Core/WhatsNew.json has no entry for ${version}`);
  const template = forced ?? entry.template ?? (Number(version.split('.')[2]) > 0 ? 'film' : 'lineup');
  if (!['film', 'lineup'].includes(template)) throw new Error(`${version}: unknown template ${template} (film or lineup)`);
  const film = filmOf(version, scenes, news);
  if (!FILMS[film]) throw new Error(`${version}: the film ${film} has no place in FILMS of plan.mjs (${Object.keys(FILMS).join(', ')})`);
  const summary = release.en?.summary ?? release.summary;
  let pieces = [], lineup = null;
  if (template === 'film') pieces = band(version, entry, release.items);
  else {
    const l = entry.lineup ?? {};
    lineup = { lead: l.lead ?? 'windows-home', back: l.back ?? 'mac-films', phone: l.phone ?? 'iphone-home', focus: l.focus ?? null };
    if (lineup.focus && (lineup.focus.length !== 4 || lineup.focus.some((v) => v < 0 || v > 1))) throw new Error(`${version}: lineup.focus is four fractions of the capture`);
    for (const s of [lineup.lead, lineup.back, lineup.phone]) {
      const p = platformOf(s);
      if (!p || !SHOTS[p].includes(s.slice(p.length + 1))) throw new Error(`${version}: no gallery takes ${s}`);
    }
  }
  const shots = [...new Set([...pieces.map((x) => x.shot), lineup?.lead, lineup?.back, lineup?.phone].filter(Boolean))];
  return {
    version, template, date: longDate(release.date), summary, sentence: sentence(summary), film, place: FILMS[film], band: pieces, lineup, shots,
    windows: shots.some((s) => platformOf(s) === 'windows'), mac: shots.some((s) => platformOf(s) === 'mac'), iphone: shots.some((s) => platformOf(s) === 'iphone'),
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const p = plan(process.argv[2]);
    console.log(`template=${p.template}\nwindows=${p.windows}\nmac=${p.mac}\niphone=${p.iphone}`);
  } catch (e) { console.error(e.message); process.exit(1); }
}
