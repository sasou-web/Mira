// The banner at the head of a release's page: Mira's name and the version, then the screens this version changed,
// as captured by tools/banner/capture.mjs, on large phones running off the edge. Nothing written about the changes:
// they show on the captures, where the rest of each screen is dimmed around what changed. Each version draws its own
// banner: the angle, the rhythm of the phones and the light come from the version number and from the captures.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/banner.mjs <version> <captures folder> <out.jpg>
// The folder holds the PNGs and their banner.json (written by capture.mjs): [{ file, kind: 'phone' | 'screen',
// focus?: { x, y, width, height } in CSS pixels of the capture }], plus still.jpg, a film's backdrop, if any.
import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const [version, folder, out] = process.argv.slice(2);
if (!out || !/^\d+\.\d+\.\d+$/.test(version)) { console.error('usage: banner.mjs <x.y.z> <captures folder> <out.jpg>'); process.exit(2); }
const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const shots = JSON.parse(readFileSync(join(folder, 'banner.json'), 'utf8')).filter((s) => s.kind === 'phone');
if (!shots.length) { console.error('banner.json lists no phone capture'); process.exit(1); }

// The version's own numbers: FNV-1a of « x.y.z », then mulberry32. The same version always draws the same banner.
let seed = 0x811c9dc5;
for (const c of version) seed = Math.imul(seed ^ c.charCodeAt(0), 0x01000193) >>> 0;
const random = () => {
  seed = (seed + 0x6d2b79f5) >>> 0;
  let t = seed; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
};
const between = (a, b) => a + (b - a) * random();

const data = (file, type) => `data:${type};base64,${readFileSync(file).toString('base64')}`;
const font = (weight) => data(join(root, 'src/Mira.Web/fonts', `nunito-${weight}.woff2`), 'font/woff2');
const image = (file) => data(join(folder, file), file.endsWith('.jpg') ? 'image/jpeg' : 'image/png');
const still = ['still.jpg', 'still.png'].find((f) => existsSync(join(folder, f)));

// 1200 × 600 points drawn at ×2. Three phones, the main change in the middle one, each placed so that what changed
// on it is in view, the outer ones running off the bottom and the top.
const W = 1200, H = 600, PHONE = 262, GAP = 28, SCREEN = PHONE - 18;
const order = [shots[1], shots[0], shots[2]].filter(Boolean);
const angle = -between(9, 15);
const startX = between(500, 530);
const lifts = order.map((shot, i) => {
  const f = shot.focus ?? { y: 300, height: 200 };
  const centre = 9 + (f.y + f.height / 2) * (SCREEN / 402);
  // Where it lands once the phones are turned: the shift the angle gives a column further right taken back.
  const shift = [between(150, 200), between(-70, -40), between(-170, -120)][i];
  const x = i * (PHONE + GAP) + PHONE / 2, a = (angle * Math.PI) / 180;
  return H / 2 - centre + (shift - x * Math.sin(a)) / Math.cos(a);
});

// What changed stays lit, the rest of the screen sinks a little.
const spotlight = (shot) => {
  const f = shot.focus; if (!f) return '';
  const k = 100 / 402;
  return `<i class="spot" style="left:${f.x * k}cqw;top:${f.y * k}cqw;width:${f.width * k}cqw;height:${f.height * k}cqw"></i>`;
};
const phones = order.map((shot, i) => `
  <figure class="phone" style="left:${i * (PHONE + GAP)}px;top:${lifts[i]}px">
    <div class="glass"><img src="${image(shot.file)}">${spotlight(shot)}</div>
  </figure>`).join('');

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face { font-family: Nunito; font-weight: 800; src: url(${font(800)}) format('woff2'); }
* { box-sizing: border-box; margin: 0; }
html, body { width: ${W}px; height: ${H}px; background: #0b0b0c; overflow: hidden; }
body { position: relative; font-family: Nunito, sans-serif; color: #f4f4f5; }
/* The light: the film's backdrop, blurred into a colour field, stronger behind the phones. */
.light { position: absolute; inset: -80px; background: ${still ? `url(${image(still)}) ${Math.round(between(20, 80))}% center / cover` : '#1b1b1d'};
  filter: blur(60px) saturate(120%) brightness(0.7); }
.shade { position: absolute; inset: 0; background:
  linear-gradient(90deg, #0b0b0c 0 30%, rgba(11, 11, 12, 0.82) 48%, rgba(11, 11, 12, 0.3) 75%, rgba(11, 11, 12, 0.4)),
  linear-gradient(180deg, transparent 60%, rgba(11, 11, 12, 0.6)); }
.dots { position: absolute; inset: 0; opacity: 0.09; background-image: radial-gradient(#fff 0.7px, transparent 0.8px); background-size: 18px 18px;
  -webkit-mask-image: linear-gradient(90deg, #000 0 45%, transparent 70%); }
.tilt { position: absolute; left: ${startX}px; top: 0; width: ${3 * PHONE + 2 * GAP}px; height: ${H}px; transform-origin: 0 50%; transform: rotate(${angle}deg); }
.phone { position: absolute; width: ${PHONE}px; margin: 0; padding: 9px; border-radius: 44px; background: #050506;
  box-shadow: inset 0 0 0 1.5px rgba(255, 255, 255, 0.14), 0 50px 90px rgba(0, 0, 0, 0.6), 0 12px 30px rgba(0, 0, 0, 0.4); }
.glass { position: relative; border-radius: 36px; overflow: hidden; background: #000; container-type: inline-size; }
.glass img { display: block; width: 100%; }
.spot { position: absolute; border-radius: 4cqw; box-shadow: 0 0 0 200cqw rgba(0, 0, 0, 0.36); }
.name { position: absolute; left: 76px; top: 50%; transform: translateY(-46%); z-index: 3; }
.brand { display: flex; align-items: center; gap: 12px; font-weight: 800; font-size: 34px; color: rgba(244, 244, 245, 0.92); }
.brand svg { width: 34px; height: 34px; }
.version { font-weight: 800; font-size: 150px; line-height: 0.9; letter-spacing: -5px; margin: 10px 0 0 -5px; font-variant-numeric: tabular-nums; }
</style></head><body>
<div class="light"></div><div class="shade"></div><div class="dots"></div>
<div class="tilt">${phones}</div>
<div class="name">
  <div class="brand"><svg viewBox="0 0 24 24"><path fill="#f5f5f7" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="#101013" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></svg>Mira</div>
  <div class="version">${version}</div>
</div>
</body></html>`;

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 2 });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: out, type: out.endsWith('.png') ? 'png' : 'jpeg', ...(out.endsWith('.png') ? {} : { quality: 88 }) });
  if (process.env.MIRA_BANNER_HTML) writeFileSync(process.env.MIRA_BANNER_HTML, html);
  console.log(out);
} finally { await browser.close(); }
