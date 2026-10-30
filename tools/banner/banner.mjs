// The banner at the head of a release's page: Mira's name and the version, then the screens this version changed,
// as captured by tools/banner/capture.mjs, set in phones and windows in Mira's own look. Nothing written about the
// changes: they show on the captures, and a magnified detail of each one points at what moved. Each version draws
// its own banner: the layout, the angle, the order and the light come from the version number and from the captures.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/banner.mjs <version> <captures folder> <out.jpg>
// The folder holds the PNGs and their banner.json (written by capture.mjs): [{ file, kind: 'phone' | 'screen',
// focus?: { x, y, width, height } in CSS pixels of the capture, scale }], plus still.png, a film's backdrop, if any.
import { readFileSync, existsSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const [version, folder, out] = process.argv.slice(2);
if (!out || !/^\d+\.\d+\.\d+$/.test(version)) { console.error('usage: banner.mjs <x.y.z> <captures folder> <out.jpg>'); process.exit(2); }
const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const shots = JSON.parse(readFileSync(join(folder, 'banner.json'), 'utf8'));
if (!shots.length) { console.error('banner.json lists no capture'); process.exit(1); }

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
const still = ['still.png', 'still.jpg'].find((f) => existsSync(join(folder, f)));

// The banner, 1200 × 600 points drawn at ×2.
const W = 1200, H = 600;
const phones = shots.filter((s) => s.kind === 'phone');
const screens = shots.filter((s) => s.kind === 'screen');
// Three ways to set the screens, chosen by the version among those its captures allow.
const layouts = [];
if (phones.length >= 3) layouts.push('columns', 'fan');
if (screens.length && phones.length) layouts.push('desk');
if (!layouts.length) layouts.push(screens.length ? 'desk' : 'fan');
const layout = process.env.MIRA_BANNER_LAYOUT ?? layouts[Math.floor(random() * layouts.length)];
const side = random() < 0.5 ? -1 : 1;

const phoneHtml = (shot, width, style = '') => `
  <figure class="phone" style="width:${width}px;${style}"><div class="glass"><img src="${image(shot.file)}"></div></figure>`;
const screenHtml = (shot, width, style = '') => `
  <figure class="window" style="width:${width}px;${style}">
    <div class="window-bar"><i></i><i></i><i></i></div><img src="${image(shot.file)}">
  </figure>`;

// A magnified detail: the part of a capture that changed, cut out at a larger size, in a card of Mira's glass.
function detail(shot, at, size) {
  const f = shot.focus; if (!f) return '';
  const scale = Math.min(size / f.width, (size * 0.62) / f.height);
  const capture = shot.captureWidth ?? 402;
  return `<div class="detail" style="left:${at.x}px;top:${at.y}px;width:${f.width * scale}px;height:${f.height * scale}px;transform:rotate(${at.r ?? 0}deg)">
    <img src="${image(shot.file)}" style="width:${capture * scale}px;left:${-f.x * scale}px;top:${-f.y * scale}px"></div>`;
}

let stage = '', details = '';
if (layout === 'columns') {
  // Tilted columns of phones running off the edge, the main capture in the middle one.
  const angle = side * between(9, 15);
  const order = [...phones.slice(1), phones[0], ...phones.slice(1)];
  const columns = [0, 1, 2].map((c) => [order[(c * 2) % order.length], order[(c * 2 + 1) % order.length]]);
  columns[1] = [phones[0], phones[1 % phones.length]];
  const lift = [between(-150, -60), between(-260, -200), between(-120, 0)];
  stage = `<div class="tilt" style="left:${between(470, 520)}px;top:${between(-40, 20)}px;transform:rotate(${angle}deg)">${
    columns.map((column, c) => `<div class="column" style="left:${c * 250}px;top:${lift[c]}px">${column.map((s) => phoneHtml(s, 230, 'position:relative;margin-bottom:22px')).join('')}</div>`).join('')}</div>`;
  const focused = shots.filter((s) => s.focus).slice(0, 2);
  details = focused.map((s, i) => detail(s, { x: i ? 820 + between(-20, 40) : 470 + between(-10, 30), y: i ? 400 + between(-30, 20) : 120 + between(-20, 40), r: side * between(-3, 3) }, 300)).join('');
} else if (layout === 'fan') {
  // Three phones opened as a fan from the middle one, as on miracinema.app.
  const center = between(820, 880), spread = between(150, 185), turn = between(7, 11);
  const three = [phones[1 % phones.length], phones[0], phones[2 % phones.length]];
  stage = three.map((s, i) => {
    const k = i - 1, w = i === 1 ? 250 : 220;
    return phoneHtml(s, w, `position:absolute;left:${center + k * spread - w / 2}px;top:${i === 1 ? 70 : 115}px;z-index:${i === 1 ? 3 : 1};transform:rotate(${k * turn}deg)`);
  }).join('');
  const focused = shots.filter((s) => s.focus).slice(0, 2);
  details = focused.map((s, i) => detail(s, { x: i ? center + 60 : 470, y: i ? 430 : 300 + between(-30, 30), r: (i ? 1 : -1) * between(1, 3) }, 290)).join('');
} else {
  // A window of Mira web on a computer, turned towards the name, and a phone in front of it.
  const screen = screens[0], phone = phones[0] ?? null;
  stage = `<div class="desk" style="transform:perspective(1600px) rotateY(${-between(14, 20)}deg) rotateX(${between(3, 6)}deg)">${screenHtml(screen, 760)}</div>`
    + (phone ? phoneHtml(phone, 200, `position:absolute;left:${between(1000, 1040)}px;top:${between(210, 250)}px;z-index:4;transform:rotate(${between(4, 8)}deg)`) : '');
  const focused = shots.filter((s) => s.focus).slice(0, 2);
  details = focused.map((s, i) => detail(s, { x: i ? 900 : 470, y: i ? 60 : 400, r: i ? 2 : -2 }, 300)).join('');
}

// The player's bar of Mira along the bottom: one chapter per changed screen, played up to the version's own place.
const chapters = shots.length + 1;
const played = between(0.35, 0.8);

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face { font-family: Nunito; font-weight: 600; src: url(${font(600)}) format('woff2'); }
@font-face { font-family: Nunito; font-weight: 800; src: url(${font(800)}) format('woff2'); }
* { box-sizing: border-box; margin: 0; }
html, body { width: ${W}px; height: ${H}px; background: #070708; overflow: hidden; }
body { position: relative; font-family: Nunito, sans-serif; color: #f4f4f5; }
.backdrop { position: absolute; inset: -40px; background: ${still ? `url(${image(still)}) center / cover` : '#111112'}; filter: blur(38px) saturate(130%) brightness(0.42); }
.shade { position: absolute; inset: 0; background:
  radial-gradient(120% 90% at ${side > 0 ? '78%' : '82%'} 30%, transparent 0 30%, rgba(7, 7, 8, 0.55) 70%),
  linear-gradient(90deg, #070708 0 24%, rgba(7, 7, 8, 0.86) 42%, rgba(7, 7, 8, 0.2) 70%, rgba(7, 7, 8, 0.45)); }
.grain { position: absolute; inset: 0; opacity: 0.07; background-image: radial-gradient(#fff 0.6px, transparent 0.7px); background-size: 14px 14px; }
.stage { position: absolute; inset: 0; overflow: hidden; }
.tilt { position: absolute; width: 760px; height: 1300px; transform-origin: 0 0; }
.column { position: absolute; }
.phone { position: absolute; border-radius: 17.6% / 8.39%; container-type: inline-size;
  background: linear-gradient(145deg, #75777d, #2e2f33 16%, #1c1d20 50%, #2e2f33 84%, #66686e);
  box-shadow: inset 0 0 0 0.5px rgba(255, 255, 255, 0.35), 0 0 0 0.5px #000, 0 40px 80px rgba(0, 0, 0, 0.7); }
.phone .glass { position: relative; margin: 1.2cqw; border: 2cqw solid #000; border-radius: 16.4cqw; overflow: hidden; background: #000; }
.phone img { display: block; width: 100%; }
.phone .glass::after { content: ''; position: absolute; inset: 0; background: linear-gradient(115deg, rgba(255, 255, 255, 0.07), transparent 32%); }
.desk { position: absolute; left: 520px; top: 70px; transform-origin: 0 50%; z-index: 2; }
.window { position: relative; margin: 0; border-radius: 12px; overflow: hidden; background: #111112;
  box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.12), 0 50px 90px rgba(0, 0, 0, 0.7); }
.window img { display: block; width: 100%; }
.window-bar { display: flex; gap: 7px; padding: 10px 12px; background: #1b1b1d; }
.window-bar i { width: 11px; height: 11px; border-radius: 50%; background: #3a3a3e; }
.detail { position: absolute; z-index: 6; overflow: hidden; border-radius: 18px; background: #111112;
  box-shadow: 0 0 0 1px rgba(255, 255, 255, 0.16), 0 0 0 7px rgba(11, 11, 12, 0.78), 0 0 0 8px rgba(255, 255, 255, 0.08), 0 36px 70px rgba(0, 0, 0, 0.75); }
.detail img { position: absolute; max-width: none; }
.name { position: absolute; left: 72px; bottom: 112px; z-index: 8; }
.brand { display: flex; align-items: center; gap: 14px; font-weight: 800; font-size: 46px; letter-spacing: -0.5px; }
.brand svg { width: 44px; height: 44px; }
.version { font-weight: 800; font-size: 168px; line-height: 0.86; letter-spacing: -6px; margin: 14px 0 0 -6px;
  font-variant-numeric: tabular-nums; text-shadow: 0 10px 40px rgba(0, 0, 0, 0.5); }
.bar { position: absolute; left: 72px; right: 72px; bottom: 44px; z-index: 8; display: flex; gap: 4px; height: 5px; }
.bar i { flex: 1; border-radius: 3px; background: rgba(255, 255, 255, 0.22); overflow: hidden; position: relative; }
.bar i b { position: absolute; inset: 0 auto 0 0; background: #f5f5f7; }
.knob { position: absolute; bottom: 38.5px; width: 16px; height: 16px; border-radius: 50%; background: #f5f5f7; z-index: 9;
  box-shadow: 0 2px 10px rgba(0, 0, 0, 0.5); }
</style></head><body>
<div class="backdrop"></div><div class="shade"></div><div class="grain"></div>
<div class="stage">${stage}${details}</div>
<div class="name">
  <div class="brand"><svg viewBox="0 0 24 24"><path fill="#f5f5f7" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="#101013" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></svg>Mira</div>
  <div class="version">${version}</div>
</div>
<div class="bar">${Array.from({ length: chapters }, (_, i) => {
  const fill = Math.max(0, Math.min(1, played * chapters - i));
  return `<i><b style="width:${fill * 100}%"></b></i>`;
}).join('')}</div>
<div class="knob" style="left:${72 + played * (W - 144) - 8}px"></div>
</body></html>`;

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 2 });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: out, type: out.endsWith('.png') ? 'png' : 'jpeg', ...(out.endsWith('.png') ? {} : { quality: 88 }) });
  if (process.env.MIRA_BANNER_HTML) writeFileSync(process.env.MIRA_BANNER_HTML, html);
  console.log(`${out}: ${layout}`);
} finally { await browser.close(); }
