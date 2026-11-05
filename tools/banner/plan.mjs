// What a release's banner shows (tools/banner/banner.mjs), read from tools/banner/scenes.json and the version's entry
// of src/Mira.Core/WhatsNew.json (its English). Read by banner.yml (whether Mira for Windows, Mac or iPhone must be
// captured):
//   node tools/banner/plan.mjs <x.y.z>   → lines « template=… », « windows=true|false », « mac=… », « iphone=… »
//
// scenes.json, one entry per version:
//   { "items": [ one per point of WhatsNew.json, in its order ], "template"?, "backdrop"?, "lineup"? }
//   item: { "shot"?: a capture, "focus"?: [x, y, width, height] in fractions of it, "platform"? }
//   - shot: "windows-<screen>" (Mira.exe --readme-gallery: home, film, series, films, subtitles), "mac-<screen>"
//     (Mira --gallery: home, film, series, films, settings) or "iphone-<screen>" (testGallery of MiraUITests: home,
//     movies, film, together, series).
//   - no shot: the point is drawn as its icon (a change that shows on no screen: a fix on the Jellyfin PC…).
//   - platform: the tag above the point (windows, mac, web, iphone, server), else the shot's.
//   template: "changes" (a card per point: patch versions, x.y.z with z > 0) or "lineup" (Mira on every screen it
//   has, over a film: x.y.0), else chosen by the version. backdrop: a film of the library (still-<name>.jpg of
//   stills.mjs: arcane, spider-verse, klaus, coco, wild-robot, puss-in-boots, nimona, your-name), else the version's.
//   lineup: { "lead", "focus"?, "back", "phone" } the window in front, the one behind and the phone; by default the
//   first point's window (else windows-home), the other app's Home, the first point's iPhone screen (else iphone-home).
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const platformOf = (shot) => /^(windows|mac|iphone)-/.exec(shot ?? '')?.[1] ?? null;

export function plan(version, override = null) {
  if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) throw new Error(`not a version: ${version}`);
  const entry = JSON.parse(readFileSync(join(root, 'tools/banner/scenes.json'), 'utf8'))[version];
  if (!entry) throw new Error(`tools/banner/scenes.json has no entry for ${version}`);
  const news = JSON.parse(readFileSync(join(root, 'src/Mira.Core/WhatsNew.json'), 'utf8')).find((x) => x.version === version);
  if (!news) throw new Error(`src/Mira.Core/WhatsNew.json has no entry for ${version}`);
  if ((entry.items ?? []).length !== news.items.length) throw new Error(`scenes.json lists ${entry.items?.length ?? 0} points for ${version}, WhatsNew.json ${news.items.length}`);
  const items = news.items.map((point, i) => {
    const scene = entry.items[i] ?? {};
    if (scene.focus && (scene.focus.length !== 4 || scene.focus.some((v) => v < 0 || v > 1))) throw new Error(`${version}, point ${i + 1}: focus is four fractions of the capture`);
    if (scene.shot && !platformOf(scene.shot)) throw new Error(`${version}, point ${i + 1}: no gallery takes ${scene.shot}`);
    const platform = scene.platform ?? platformOf(scene.shot);
    if (!platform) throw new Error(`${version}, point ${i + 1}: neither a shot nor a platform`);
    return { icon: point.icon, title: point.en?.title ?? point.title, shot: scene.shot ?? null, focus: scene.focus ?? null, platform };
  });
  const template = override ?? entry.template ?? (Number(version.split('.')[2]) > 0 ? 'changes' : 'lineup');
  if (!['changes', 'lineup'].includes(template)) throw new Error(`${version}: unknown template ${template}`);
  let lineup = null;
  if (template === 'lineup') {
    const desk = items.find((x) => ['windows', 'mac'].includes(platformOf(x.shot)));
    const lead = entry.lineup?.lead ?? desk?.shot ?? 'windows-home';
    // The other app's Home behind it (its films when the Home is in front): two different screens, not the same twice.
    const other = platformOf(lead) === 'mac' ? 'windows' : 'mac';
    const back = entry.lineup?.back ?? `${other}-${lead.endsWith('-home') ? 'films' : 'home'}`;
    const phone = entry.lineup?.phone ?? items.find((x) => platformOf(x.shot) === 'iphone')?.shot ?? 'iphone-home';
    lineup = { lead, back, phone, focus: entry.lineup?.lead ? entry.lineup.focus ?? null : desk?.focus ?? null };
  }
  const shots = [...new Set([...items.map((x) => x.shot), lineup?.lead, lineup?.back, lineup?.phone].filter(Boolean))];
  return {
    version, template, summary: news.en?.summary ?? news.summary, items, lineup, backdrop: entry.backdrop ?? null, shots,
    windows: shots.some((s) => platformOf(s) === 'windows'), mac: shots.some((s) => platformOf(s) === 'mac'), iphone: shots.some((s) => platformOf(s) === 'iphone'),
  };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const p = plan(process.argv[2]);
    console.log(`template=${p.template}\nwindows=${p.windows}\nmac=${p.mac}\niphone=${p.iphone}`);
  } catch (e) { console.error(e.message); process.exit(1); }
}
