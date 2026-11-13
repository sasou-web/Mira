// The banner at the head of a release's page, drawn from real captures of this version's Mira (Windows, Mac, iPhone) and
// from the English of its entry in WhatsNew.json; what it shows is planned by tools/banner/plan.mjs (scenes.json).
// Two templates, chosen by the version:
//   - film (a patch, x.y.z with z > 0): the release's film at the top right, melting into black as Mira shows a title,
//     the version and its sentence on the left, below a band of real pieces of screens, each under its point's title;
//   - lineup (x.y.0): the version and its summary over a film's backdrop, in front of it Mira for Windows, Mira for Mac
//     and Mira for iPhone.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/banner.mjs <x.y.z> <captures folder> <out.jpg> [film|lineup]
// The folder (searched with its subfolders) holds the captures by name: windows-*.png (Mira.exe --readme-gallery),
// mac-*.png (Mira --gallery), iphone-*.png (testGallery of MiraUITests) and the films' backdrops, still-<name>.jpg
// (tools/banner/stills.mjs). JPEG copies are taken as well.
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { BAND, plan, platformOf } from './plan.mjs';

const [version, folderArg, outArg, templateArg] = process.argv.slice(2);
if (!outArg) { console.error('usage: banner.mjs <x.y.z> <captures folder> <out.jpg> [film|lineup]'); process.exit(2); }
const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const folder = resolve(folderArg), out = resolve(outArg);
const p = plan(version, { template: templateArg ?? null });
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
const still = named(`still-${p.film}`);
if (!still) throw new Error(`no backdrop named still-${p.film} in ${folder}`);
const stillUrl = pathToFileURL(still).href;

const mark = (ink, cut) => `<svg viewBox="0 0 24 24" class="mark"><path fill="${ink}" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="${cut}" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></svg>`;
const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
// Mira for Mac's capture keeps the room of the traffic lights (as on a Mac); they are drawn here, as macOS draws them.
const lights = (u) => `<span class="lights" style="--u:${u}px"><b></b><b></b><b></b></span>`;
const lightsCss = `
.lights { position: absolute; left: calc(20 * var(--u)); top: calc(19 * var(--u)); display: flex; gap: calc(8 * var(--u)); z-index: 2; }
.lights b { width: calc(12 * var(--u)); height: calc(12 * var(--u)); border-radius: 50%; background: #ff5f57; box-shadow: inset 0 0 0 0.5px rgba(0,0,0,0.25); }
.lights b:nth-child(2) { background: #febc2e; } .lights b:nth-child(3) { background: #28c840; }`;

const W = 1200, H = 600;

// ——— film: the release's film, its sentence, the band of real pieces of screens ———
function film() {
  const f = p.place, low = BAND.caption - 6, tall = BAND.bottom - BAND.top + 2;
  const pieces = p.band.map((x, i) => {
    const s = shot(x.shot), k = x.scale, origin = x.at[0] === 0 && x.at[1] === 0;
    const kind = s.kind === 'phone' ? 'phone' : 'win';
    const inner = origin && s.kind === 'mac' ? lights(k) : '';
    const caption = x.caption == null ? '' : `<div class="cap" data-piece="${i}" style="left:${x.left}px">${escape(x.caption)}</div>`;
    return `${caption}<div class="piece ${kind}${origin ? ' origin' : ''}" data-piece="${i}" data-units="${s.w / x.units}x${s.h / x.units}"
      style="left:${x.left}px;top:${BAND.top}px;width:${x.width}px;height:${tall}px"><img src="${s.file}"
      style="width:${(s.w / x.units) * k}px;left:${-x.at[0] * k}px;top:${-x.at[1] * k}px">${inner}</div>`;
  }).join('\n');
  return `<style>
@font-face { font-family: Nunito; font-weight: 200 1000; src: url(${pathToFileURL(join(root, 'site/fonts/nunito.woff2')).href}) format('woff2'); }
* { box-sizing: border-box; margin: 0; padding: 0; }
html, body { width: ${W}px; height: ${H}px; overflow: hidden; background: #000; }
body { position: relative; font-family: Nunito, sans-serif; font-weight: 600; color: #f4f4f4; -webkit-font-smoothing: antialiased; }
.mark { width: 1.18em; height: 1.18em; flex: none; }
.still { position: absolute; left: ${f.x}px; top: ${f.y}px; width: ${f.w}px; height: ${f.w * 9 / 16}px; background: url(${stillUrl}) center / cover;
  -webkit-mask-image: linear-gradient(to right, transparent 0, #000 ${f.fade}px), linear-gradient(to bottom, #000 ${low - f.y - 120}px, transparent ${low - f.y}px);
  -webkit-mask-composite: source-in; mask-composite: intersect; }
.head { position: absolute; left: 56px; top: 44px; }
.brand { display: flex; align-items: center; gap: 0.42em; font-weight: 800; font-size: 22px; letter-spacing: -0.01em; line-height: 1; }
.version { font-weight: 800; font-size: 88px; font-variant-numeric: tabular-nums; white-space: nowrap; letter-spacing: -0.045em; line-height: 0.8; margin: 22px 0 0 -4px; }
.date { position: absolute; left: 152px; top: 52px; font-weight: 800; font-size: 10.5px; letter-spacing: 0.16em; text-transform: uppercase; color: #868686; }
.sentence { position: absolute; left: 56px; top: 178px; width: 400px; font-size: 21px; line-height: 1.2; letter-spacing: -0.028em; font-weight: 800; }
.sentence span { display: block; font-weight: 300; color: #c4c4c4; text-wrap: balance; }
.cap { position: absolute; top: ${BAND.caption}px; font-size: 14.5px; font-weight: 700; letter-spacing: -0.01em; white-space: nowrap; }
.piece { position: absolute; overflow: hidden; background: #0d0d0d; }
.piece img { position: absolute; display: block; max-width: none; }
.piece::after { content: ''; position: absolute; inset: 0; border-radius: inherit; box-shadow: inset 0 0 0 1px rgba(255,255,255,0.11); pointer-events: none; }
.win { border-radius: 12px 12px 0 0; }
.phone { border-radius: 34px 34px 0 0; }
.phone.origin::before { content: ''; position: absolute; z-index: 2; left: 50%; top: 7px; width: 78px; height: 23px; margin-left: -39px; border-radius: 12px; background: #000; }
${lightsCss}
</style>
<div class="still"></div>
<div class="head"><div class="brand">${mark('#f4f4f4', '#000')}Mira</div><div class="version">${escape(version)}</div></div>
<div class="date">${escape(p.date)}</div>
<div class="sentence">${escape(p.sentence.bold)}${p.sentence.rest ? `<span>${escape(p.sentence.rest)}</span>` : ''}</div>
${pieces}`;
}

// What the film template must hold, measured in the page with its real fonts: the sentence above the captions, each
// caption on its piece and inside the band, each piece cut inside its capture.
const checks = `(() => {
  const problems = [], band = ${JSON.stringify(BAND)}, W = ${W};
  const sentence = document.querySelector('.sentence');
  if (sentence) {
    const bottom = sentence.getBoundingClientRect().bottom;
    if (bottom > band.caption - 4) problems.push('the sentence ends at y = ' + Math.round(bottom) + ', below ' + (band.caption - 4) + ': shorten en.summary');
  }
  for (const cap of document.querySelectorAll('.cap')) {
    const piece = document.querySelector('.piece[data-piece="' + cap.dataset.piece + '"]');
    const width = Math.ceil(cap.getBoundingClientRect().width), left = parseFloat(cap.style.left), room = parseFloat(piece.style.width);
    if (width > room) problems.push('caption « ' + cap.textContent + ' » is ' + width + ' px wide, its piece ' + room + ' px');
    if (left + width > band.right) problems.push('caption « ' + cap.textContent + ' » ends at x = ' + (left + width) + ', past ' + band.right);
  }
  for (const piece of document.querySelectorAll('.piece')) {
    const img = piece.querySelector('img'), [dw, dh] = piece.dataset.units.split('x').map(Number), k = parseFloat(img.style.width) / dw;
    const x = -parseFloat(img.style.left) / k, y = -parseFloat(img.style.top) / k, left = parseFloat(piece.style.left);
    const shown = Math.min(parseFloat(piece.style.width), W - left) / k, high = (band.bottom - band.top) / k;
    const name = decodeURIComponent(img.src.split('/').pop());
    if (x + shown > dw + 0.5) problems.push('piece ' + (+piece.dataset.piece + 1) + ' (' + name + ') runs ' + Math.round(x + shown - dw) + ' units past its capture on the right');
    if (y + high > dh + 0.5) problems.push('piece ' + (+piece.dataset.piece + 1) + ' (' + name + ') runs ' + Math.round(y + high - dh) + ' units past its capture at the bottom');
  }
  return problems;
})()`;

// ——— lineup: the film behind, Mira on each of its screens in front of it ———
const font = (weight) => pathToFileURL(join(root, 'src/Mira.Jellyfin/Pages/fonts', `nunito-${weight}.woff2`)).href;
const base = `
@font-face { font-family: Nunito; font-weight: 600; src: url(${font(600)}); }
@font-face { font-family: Nunito; font-weight: 700; src: url(${font(700)}); }
@font-face { font-family: Nunito; font-weight: 800; src: url(${font(800)}); }
* { box-sizing: border-box; margin: 0; }
html, body { width: ${W}px; height: ${H}px; overflow: hidden; background: #0a0a0b; }
body { position: relative; font-family: Nunito, sans-serif; color: #f4f4f5; -webkit-font-smoothing: antialiased; }
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
${lightsCss}
.spot { position: absolute; border-radius: 8px; box-shadow: 0 0 0 2000px rgba(0,0,0,var(--dim, 0.45)), 0 0 0 1.5px rgba(255,255,255,0.9), 0 0 24px 4px rgba(255,255,255,0.18); z-index: 2; }
/* A recent iPhone around a whole screen of Mira for iPhone (402 × 874 points), as site/css/site.css draws it. */
.phone { position: absolute; container-type: inline-size; border-radius: 17.6% / 8.39%;
  background: linear-gradient(145deg, #75777d, #2e2f33 16%, #1c1d20 50%, #2e2f33 84%, #66686e);
  box-shadow: inset 0 0 0 0.5px rgba(255,255,255,0.35), 0 0 0 0.5px #000, 0 30px 60px rgba(0,0,0,0.65), 0 10px 24px rgba(0,0,0,0.5); }
.phone .glass { position: relative; margin: 1.2cqw; border: 2cqw solid #000; border-radius: 16.4cqw; overflow: hidden; background: #000; }
.phone .glass img { display: block; width: 100%; }
.phone .spot { border-radius: 3cqw; }
`;
// A capture in its frame, width in CSS pixels; what changed lit when the capture gives it.
const frame = (s, width, { lit = true, cls = '', style = '' } = {}) => {
  const k = width / s.w;
  const spot = lit && s.focus ? `<i class="spot" style="left:${s.focus[0] * k}px;top:${s.focus[1] * k}px;width:${s.focus[2] * k}px;height:${s.focus[3] * k}px"></i>` : '';
  if (s.kind === 'phone') return `<figure class="phone ${cls}" style="width:${width}px;${style}"><div class="glass"><img src="${s.file}">${spot}</div></figure>`;
  return `<figure class="win ${s.kind} ${cls}" style="width:${width}px;height:${s.h * k}px;${style}"><img src="${s.file}">${s.kind === 'mac' ? lights(width / 1440) : ''}${spot}</figure>`;
};
function lineup() {
  const l = p.lineup;
  const lead = shot(l.lead, l.focus), back = shot(l.back), phone = shot(l.phone);
  return `<style>${base}
.backdrop { position: absolute; inset: 0; background: url(${stillUrl}) 60% 30% / cover; filter: blur(2px) saturate(110%) brightness(0.75); transform: scale(1.02); }
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
<div class="name"><div class="brand">${mark('#f5f5f7', '#101013')}Mira</div><div class="version" data-fit="440">${escape(version)}</div>
<p class="summary">${escape(p.summary)}</p><p class="platforms">Windows · macOS · iPhone</p></div>
<div class="grain"></div>`;
}

const draw = { film, lineup }[template];
const script = `(async () => {
  // A longer version (0.10.12) kept clear of the windows.
  for (const v of document.querySelectorAll('[data-fit]')) {
    for (let size = parseFloat(getComputedStyle(v).fontSize); v.scrollWidth > +v.dataset.fit && size > 50; size -= 4) v.style.fontSize = size + 'px';
  }
  await document.fonts.ready;
  document.body.dataset.ready = 1;
})();`;
const html = `<!doctype html><html><head><meta charset="utf-8"></head><body>${draw()}<script>${script}</script></body></html>`;

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
  if (process.env.MIRA_BANNER_HTML) writeFileSync(process.env.MIRA_BANNER_HTML, html);
  if (template === 'film') {
    const problems = await page.evaluate(checks);
    if (problems.length) throw new Error(`${version}: the banner does not hold:\n- ${problems.join('\n- ')}`);
  }
  await page.screenshot({ path: out, type: out.endsWith('.png') ? 'png' : 'jpeg', ...(out.endsWith('.png') ? {} : { quality: 88 }) });
  console.log(`${out} (${template}, ${p.film})`);
} finally { await browser.close(); rmSync(work, { recursive: true, force: true }); }
