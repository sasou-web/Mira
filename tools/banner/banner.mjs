// The banner at the head of a release's page, drawn from real captures of this version's Mira (Windows, Mac, iPhone) and
// from the English of its entry in WhatsNew.json; what it shows is planned by tools/banner/plan.mjs (scenes.json).
// Two templates, chosen by the version:
//   - changes (a patch, x.y.z with z > 0): the version, its summary, then a card per point of WhatsNew.json, cut from
//     the screen it changed, what changed ringed (or the point's icon when it shows on no screen);
//   - lineup (x.y.0): the version and its summary over a film's backdrop, in front of it Mira for Windows, Mira for Mac
//     and Mira for iPhone, on the screens this version changed, what changed lit.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/banner.mjs <x.y.z> <captures folder> <out.jpg> [template]
// The folder (searched with its subfolders) holds the captures by name: windows-*.png (Mira.exe --readme-gallery),
// mac-*.png (Mira --gallery), iphone-*.png (testGallery of MiraUITests) and the films' backdrops, still-<name>.jpg
// (tools/banner/stills.mjs). JPEG copies are taken as well.
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { plan, platformOf } from './plan.mjs';

const [version, folderArg, outArg, templateArg] = process.argv.slice(2);
if (!outArg) { console.error('usage: banner.mjs <x.y.z> <captures folder> <out.jpg> [changes|lineup]'); process.exit(2); }
const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const folder = resolve(folderArg), out = resolve(outArg);
const p = plan(version, templateArg);
const template = p.template;

// ——— The captures ———
const files = [];
(function walk(dir) { for (const name of readdirSync(dir)) { const f = join(dir, name); statSync(f).isDirectory() ? walk(f) : files.push(f); } })(folder);
const named = (base) => files.find((f) => f.endsWith(`/${base}.png`)) ?? files.find((f) => f.endsWith(`/${base}.jpg`));
// The size in pixels of a PNG or a JPEG, from its header.
function size(file) {
  const b = readFileSync(file);
  if (b.readUInt32BE(0) === 0x89504e47) return { w: b.readUInt32BE(16), h: b.readUInt32BE(20) };
  for (let i = 2; i < b.length;) {
    const marker = b[i + 1], length = b.readUInt16BE(i + 2);
    if (marker >= 0xc0 && marker <= 0xcf && ![0xc4, 0xc8, 0xcc].includes(marker)) return { w: b.readUInt16BE(i + 7), h: b.readUInt16BE(i + 5) };
    i += 2 + length;
  }
  throw new Error(`no size in ${file}`);
}
const shot = (name, focus = null) => {
  const file = named(name);
  if (!file) throw new Error(`no capture named ${name} in ${folder}`);
  const { w, h } = size(file);
  return { name, file: pathToFileURL(file).href, w, h, kind: platformOf(name) === 'iphone' ? 'phone' : platformOf(name), focus: focus && [focus[0] * w, focus[1] * h, focus[2] * w, focus[3] * h] };
};

// The version's own numbers (FNV-1a of « x.y.z »): the same version always picks the same film.
let seed = 0x811c9dc5;
for (const c of version) seed = Math.imul(seed ^ c.charCodeAt(0), 0x01000193) >>> 0;
const stills = files.filter((f) => /\/still-[a-z0-9-]+\.(jpg|png)$/.test(f)).sort();
const still = p.backdrop ? named(`still-${p.backdrop}`) : stills[seed % Math.max(1, stills.length)];
if (p.backdrop && !still) throw new Error(`no backdrop named still-${p.backdrop}`);

// ——— Mira's icons (IconPaths of Core and the Windows app's own): stroked on a 24 grid, play and pause filled ———
const icons = {};
for (const source of ['src/Mira.Core/IconPaths.cs', 'src/Mira.Desktop/Views/Icon.cs']) {
  for (const [, name, path] of readFileSync(join(root, source), 'utf8').matchAll(/\["([a-z-]+)"\] = "([^"]+)"/g)) icons[name] ??= path;
}
const speaker = readFileSync(join(root, 'src/Mira.Core/IconPaths.cs'), 'utf8').match(/Speaker = "([^"]+)"/)[1];
const washed = new Set(['home', 'film', 'series', 'library', 'settings', 'heart', 'info', 'help', 'bookmark', 'phone', 'laptop', 'calendar']);
const icon = (name, cls = '') => {
  const path = icons[name] ?? icons.info, filled = ['play', 'pause'].includes(name);
  return `<svg class="ic ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${
    ['volume', 'volume-low', 'mute'].includes(name) ? `<path d="${speaker}" fill="currentColor" stroke-width="1"/>` : ''}${
    washed.has(name) ? `<path d="${path}" fill="currentColor" fill-opacity=".15" stroke="none"/>` : ''}<path d="${path}"${filled ? ' fill="currentColor" stroke="none"' : ''}/></svg>`;
};
const mark = `<svg viewBox="0 0 24 24" class="mark"><path fill="#f5f5f7" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="#101013" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></svg>`;
const platformName = { windows: 'Windows', mac: 'macOS', iphone: 'iPhone', server: 'Jellyfin PC' };
const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// ——— The devices ———
const W = 1200, H = 600;
// A capture in its frame, width in CSS pixels; what changed lit when the capture gives it.
const frame = (s, width, { lit = true, cls = '', style = '' } = {}) => {
  const k = width / s.w;
  const spot = lit && s.focus ? `<i class="spot" style="left:${s.focus[0] * k}px;top:${s.focus[1] * k}px;width:${s.focus[2] * k}px;height:${s.focus[3] * k}px"></i>` : '';
  if (s.kind === 'phone') return `<figure class="phone ${cls}" style="width:${width}px;${style}"><div class="glass"><img src="${s.file}">${spot}</div></figure>`;
  // Mira for Mac's capture keeps the room of the traffic lights (as on a Mac); they are drawn here, as macOS draws them.
  const lights = s.kind === 'mac' ? `<span class="lights" style="--u:${width / 1440}px"><b></b><b></b><b></b></span>` : '';
  return `<figure class="win ${s.kind} ${cls}" style="width:${width}px;height:${s.h * k}px;${style}"><img src="${s.file}">${lights}${spot}</figure>`;
};
// A box of width × height cut from a capture around what changed: as large as fits (a fifth of the height at most
// for a small one), centred, kept in the picture; the whole capture when nothing is focused.
const crop = (s, width, height) => {
  const [x, y, w, h] = s.focus ?? [0, 0, s.w, s.h];
  const k = Math.max(width / s.w, height / s.h, Math.min(0.86 * width / w, height * 0.22 / h));
  const left = Math.min(Math.max((x + w / 2) * k - width / 2, 0), s.w * k - width);
  const top = Math.min(Math.max((y + h / 2) * k - height / 2, 0), s.h * k - height);
  const ring = s.focus && w < s.w * 0.9 ? `<i class="ring" style="left:${x * k - left - 6}px;top:${y * k - top - 6}px;width:${w * k + 12}px;height:${h * k + 12}px"></i>` : '';
  return `<div class="crop" style="width:${width}px;height:${height}px"><img src="${s.file}" style="width:${s.w * k}px;left:${-left}px;top:${-top}px">${ring}</div>`;
};

const font = (weight) => pathToFileURL(join(root, 'src/Mira.Jellyfin/Pages/fonts', `nunito-${weight}.woff2`)).href;
const base = `
@font-face { font-family: Nunito; font-weight: 600; src: url(${font(600)}); }
@font-face { font-family: Nunito; font-weight: 700; src: url(${font(700)}); }
@font-face { font-family: Nunito; font-weight: 800; src: url(${font(800)}); }
* { box-sizing: border-box; margin: 0; }
html, body { width: ${W}px; height: ${H}px; overflow: hidden; background: #0a0a0b; }
body { position: relative; font-family: Nunito, sans-serif; color: #f4f4f5; -webkit-font-smoothing: antialiased; }
.ic { width: 1em; height: 1em; flex: none; }
.mark { width: 30px; height: 30px; }
.brand { display: flex; align-items: center; gap: 11px; font-weight: 800; font-size: 26px; letter-spacing: -0.2px; }
.version { font-weight: 800; font-variant-numeric: tabular-nums; white-space: nowrap; }
.grain { position: absolute; inset: 0; opacity: 0.06; mix-blend-mode: overlay; pointer-events: none; z-index: 20;
  background: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E"); }
figure { margin: 0; }
.win { position: absolute; overflow: hidden; background: #000; }
.win img { display: block; width: 100%; height: 100%; }
.win.windows { border-radius: 8px; box-shadow: 0 0 0 1px rgba(255,255,255,0.10), 0 30px 70px rgba(0,0,0,0.6), 0 8px 20px rgba(0,0,0,0.45); }
.win.mac { border-radius: 11px; box-shadow: inset 0 0 0 0.5px rgba(255,255,255,0.22), 0 0 0 0.5px rgba(0,0,0,0.9), 0 30px 70px rgba(0,0,0,0.6), 0 8px 20px rgba(0,0,0,0.45); }
.lights { position: absolute; left: calc(20 * var(--u)); top: calc(19 * var(--u)); display: flex; gap: calc(8 * var(--u)); }
.lights b { width: calc(12 * var(--u)); height: calc(12 * var(--u)); border-radius: 50%; background: #ff5f57; box-shadow: inset 0 0 0 0.5px rgba(0,0,0,0.25); }
.lights b:nth-child(2) { background: #febc2e; } .lights b:nth-child(3) { background: #28c840; }
.spot { position: absolute; border-radius: 8px; box-shadow: 0 0 0 2000px rgba(0,0,0,var(--dim, 0.45)), 0 0 0 1.5px rgba(255,255,255,0.9), 0 0 24px 4px rgba(255,255,255,0.18); z-index: 2; }
.crop { position: relative; overflow: hidden; }
.crop img { position: absolute; display: block; }
.ring { position: absolute; border-radius: 12px; box-shadow: 0 0 0 1.5px rgba(255,255,255,0.85), 0 0 22px 2px rgba(255,255,255,0.15); }
/* A recent iPhone around a whole screen of Mira for iPhone (402 × 874 points), as site/css/site.css draws it. */
.phone { position: absolute; container-type: inline-size; border-radius: 17.6% / 8.39%;
  background: linear-gradient(145deg, #75777d, #2e2f33 16%, #1c1d20 50%, #2e2f33 84%, #66686e);
  box-shadow: inset 0 0 0 0.5px rgba(255,255,255,0.35), 0 0 0 0.5px #000, 0 30px 60px rgba(0,0,0,0.65), 0 10px 24px rgba(0,0,0,0.5); }
.phone .glass { position: relative; margin: 1.2cqw; border: 2cqw solid #000; border-radius: 16.4cqw; overflow: hidden; background: #000; }
.phone .glass img { display: block; width: 100%; }
.phone .spot { border-radius: 3cqw; }
`;

// ——— lineup: the film behind, Mira on each of its screens in front of it ———
function lineup() {
  const l = p.lineup;
  const lead = shot(l.lead, l.focus), back = shot(l.back), phone = shot(l.phone);
  return `<style>${base}
.backdrop { position: absolute; inset: 0; background: ${still ? `url(${pathToFileURL(still).href}) 60% 30% / cover` : '#151517'}; filter: blur(2px) saturate(110%) brightness(0.75); transform: scale(1.02); }
.veil { position: absolute; inset: 0; background:
  linear-gradient(90deg, #0a0a0b 0 22%, rgba(10,10,11,0.86) 40%, rgba(10,10,11,0.45) 62%, rgba(10,10,11,0.35)),
  linear-gradient(0deg, rgba(10,10,11,0.9), transparent 45%); }
.name { position: absolute; left: 64px; top: 0; bottom: 0; width: 380px; display: flex; flex-direction: column; justify-content: center; z-index: 5; }
.name .version { font-size: 120px; line-height: 0.9; letter-spacing: -4px; margin: 14px 0 22px -5px; }
.summary { font-weight: 600; font-size: 17px; line-height: 1.4; color: rgba(244,244,245,0.78); max-width: 340px; }
.platforms { margin-top: 26px; font-weight: 700; font-size: 12.5px; letter-spacing: 1.6px; text-transform: uppercase; color: rgba(244,244,245,0.5); }
.win .spot { --dim: 0.3; }
.win.back { filter: brightness(0.7); }
</style>
<div class="backdrop"></div><div class="veil"></div>
${frame(back, 470, { lit: false, cls: 'back', style: 'left:700px;top:40px;z-index:1' })}
${frame(lead, 600, { style: 'left:470px;top:190px;z-index:2' })}
${frame(phone, 148, { style: 'left:1010px;top:236px;z-index:3' })}
<div class="name"><div class="brand">${mark}Mira</div><div class="version" data-fit="440">${version}</div>
<p class="summary">${escape(p.summary)}</p><p class="platforms">Windows · macOS · iPhone · Web</p></div>
<div class="grain"></div>`;
}

// ——— changes: a card per point of this version, cut from the screen it changed ———
function changes() {
  const n = p.items.length, gap = 20;
  const width = (W - 112 - gap * (n - 1)) / n, height = n <= 3 ? 248 : n === 4 ? 224 : 200;
  // The points that show on a screen first, then those drawn as their icon.
  const items = [...p.items.filter((x) => x.shot), ...p.items.filter((x) => !x.shot)];
  const cards = items.map((item) => {
    const media = item.shot ? crop(shot(item.shot, item.focus), width, height) : `<div class="tile">${icon(item.icon)}</div>`;
    return `<article class="card"><div class="media">${media}</div>
      <div class="text"><span class="tag">${icon(item.icon)}${platformName[item.platform] ?? item.platform}</span><h3>${escape(item.title)}</h3></div></article>`;
  }).join('');
  return `<style>${base}
body { background: #0b0b0c; }
.wash { position: absolute; inset: 0; background:
  radial-gradient(60% 90% at 88% 0%, color-mix(in srgb, var(--film) 34%, transparent), transparent 70%),
  radial-gradient(50% 70% at 0% 100%, color-mix(in srgb, var(--film) 14%, transparent), transparent 70%); }
.head { position: absolute; left: 56px; right: 56px; top: 44px; display: flex; align-items: flex-end; justify-content: space-between; gap: 40px; }
.head .version { font-size: 76px; line-height: 0.85; letter-spacing: -2.5px; margin-top: 10px; }
.summary { font-weight: 600; font-size: 17px; line-height: 1.45; color: rgba(244,244,245,0.7); max-width: 440px; text-align: right; }
.cards { position: absolute; left: 56px; right: 56px; bottom: 48px; display: grid; grid-template-columns: repeat(${n}, 1fr); gap: ${gap}px; }
.card { background: rgba(255,255,255,0.045); border-radius: 18px; overflow: hidden; box-shadow: inset 0 0 0 1px rgba(255,255,255,0.07); }
.media { height: ${height}px; display: grid; place-items: center; overflow: hidden; background: #050506; position: relative; }
.tile { width: 100%; height: 100%; display: grid; place-items: center; font-size: ${n <= 3 ? 84 : 68}px; color: rgba(244,244,245,0.9);
  background: radial-gradient(closest-side, color-mix(in srgb, var(--film) 40%, transparent), transparent), #0e0e10; }
.tile .ic { stroke-width: 1.6; }
.text { padding: 16px ${n <= 3 ? 20 : 16}px 20px; }
.tag { display: inline-flex; align-items: center; gap: 6px; font-weight: 700; font-size: 11.5px; letter-spacing: 1.3px; text-transform: uppercase; color: rgba(244,244,245,0.55); }
.tag .ic { font-size: 14px; }
h3 { margin-top: 7px; font-weight: 800; font-size: ${n <= 3 ? 19 : 16.5}px; line-height: 1.25; letter-spacing: -0.1px; }
</style>
<div class="wash"></div>
<div class="head"><div><div class="brand">${mark}Mira</div><div class="version">${version}</div></div><p class="summary">${escape(p.summary)}</p></div>
<div class="cards">${cards}</div>
<div class="grain"></div>`;
}

const draw = { lineup, changes }[template];
// The film's colour, for the light of the cards: the backdrop's most vivid colour among pixels bright enough to light anything.
const script = `(async () => {
  ${still ? `const img = new Image(); img.src = ${JSON.stringify(pathToFileURL(still).href)}; await img.decode();
  const c = new OffscreenCanvas(48, 27), g = c.getContext('2d'); g.drawImage(img, 0, 0, 48, 27);
  const d = g.getImageData(0, 0, 48, 27).data;
  let best = [216, 106, 58], score = -1;
  for (let i = 0; i < d.length; i += 4) {
    const r = d[i], gg = d[i + 1], b = d[i + 2], max = Math.max(r, gg, b), min = Math.min(r, gg, b);
    const s = max ? (max - min) / max : 0, v = max / 255, value = s * 0.7 + v * 0.3;
    if (v > 0.35 && value > score) { score = value; best = [r, gg, b]; }
  }
  document.body.style.setProperty('--film', 'rgb(' + best.join(',') + ')');` : ''}
  // A longer version (0.10.12) kept clear of the windows.
  for (const v of document.querySelectorAll('[data-fit]')) {
    for (let size = parseFloat(getComputedStyle(v).fontSize); v.scrollWidth > +v.dataset.fit && size > 50; size -= 4) v.style.fontSize = size + 'px';
  }
  await document.fonts.ready;
  document.body.dataset.ready = 1;
})();`;
const html = `<!doctype html><html><head><meta charset="utf-8"></head><body style="--film:#d86a3a">${draw()}<script>${script}</script></body></html>`;

const work = mkdtempSync(join(tmpdir(), 'mira-banner-'));
const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch({ args: ['--allow-file-access-from-files'] });
try {
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 2 });
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('requestfailed', (r) => errors.push(`${r.url().slice(0, 120)}: ${r.failure()?.errorText}`));
  writeFileSync(join(work, 'banner.html'), html);
  await page.goto(pathToFileURL(join(work, 'banner.html')).href, { waitUntil: 'load' });
  await page.waitForFunction(() => document.body.dataset.ready, null, { timeout: 15000 });
  // Every image decoded before the picture is taken.
  await page.evaluate(() => Promise.all([...document.images].map((i) => i.decode().catch(() => {}))));
  if (errors.length) throw new Error(errors.join('\n'));
  await page.screenshot({ path: out, type: out.endsWith('.png') ? 'png' : 'jpeg', ...(out.endsWith('.png') ? {} : { quality: 88 }) });
  if (process.env.MIRA_BANNER_HTML) writeFileSync(process.env.MIRA_BANNER_HTML, html);
  console.log(`${out} (${template})`);
} finally { await browser.close(); rmSync(work, { recursive: true, force: true }); }
