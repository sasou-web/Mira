// The banner at the head of a release's page: Mira's name and the version over a film's backdrop, as Mira's Home
// shows a title, then the screens this version changed, as captured by tools/banner/capture.mjs, on three iPhones
// drawn as the showcase site draws them (site/css/site.css). Nothing written about the changes: they show on the
// captures, where what changed stays lit. Each version draws its own banner: the backdrop, the fan of the phones and
// their glow come from the version number and from the captures.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/banner.mjs <version> <captures folder> <out.jpg>
// The folder holds the PNGs and their banner.json (written by capture.mjs): [{ file, kind: 'phone' | 'screen',
// focus?: { x, y, width, height } in CSS pixels of the capture }], plus still-1.jpg to still-N.jpg, films' backdrops.
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
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
const stills = readdirSync(folder).filter((f) => /^still(-\d+)?\.(jpg|png)$/.test(f)).sort();
const still = stills.length ? stills[Math.floor(random() * stills.length)] : null;

// 1200 × 600 points drawn at ×2. The main change on the middle phone, in front; the next two behind it, fanned out.
const W = 1200, H = 600, PHONE = 236, SIDE = 0.86;
const order = [shots[1], shots[0], shots[2]].filter(Boolean);
const centre = { x: between(812, 842), y: H / 2 + between(-6, 10) };
const spread = between(184, 196), turn = between(4, 7), drop = between(14, 34);
const place = order.length === 1 ? [[0, 0, 0, 1]] : order.length === 2 ? [[-spread / 2, drop / 2, -turn, SIDE], [spread / 2, 0, 0, 1]]
  : [[-spread, drop, -turn, SIDE], [0, 0, 0, 1], [spread, -drop * 0.6, turn, SIDE]];
const front = order.length === 2 ? 1 : Math.min(1, order.length - 1);

// What changed stays lit, the rest of the screen sinks a little. In cqw, a hundredth of the phone's width: the screen
// is 93.6 wide, 3.2 in from the edge.
const spotlight = (shot) => {
  const f = shot.focus; if (!f) return '';
  const k = 93.6 / 402;
  return `<i class="spot" style="left:${f.x * k}cqw;top:${f.y * k}cqw;width:${f.width * k}cqw;height:${f.height * k}cqw"></i>`;
};
const phones = order.map((shot, i) => {
  const [dx, dy, r, s] = place[i];
  return `<figure class="phone${i === front ? ' front' : ''}" style="transform:translate(${centre.x + dx - PHONE / 2}px,${centre.y + dy - PHONE * 1.049}px) rotate(${r}deg) scale(${s})">
    <div class="glass"><img src="${image(shot.file)}">${spotlight(shot)}</div>
  </figure>`;
}).join('');

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
@font-face { font-family: Nunito; font-weight: 800; src: url(${font(800)}) format('woff2'); }
@font-face { font-family: Nunito; font-weight: 700; src: url(${font(700)}) format('woff2'); }
* { box-sizing: border-box; margin: 0; }
html, body { width: ${W}px; height: ${H}px; background: #070708; overflow: hidden; }
body { position: relative; font-family: Nunito, sans-serif; color: #f4f4f5; }
/* The film, as Mira's Home shows it: its backdrop on the right, sinking into black towards the name and the bottom. */
.backdrop { position: absolute; inset: 0;
  background: ${still ? `url(${image(still)}) ${Math.round(between(30, 70))}% ${Math.round(between(20, 45))}% / cover` : '#1b1b1d'};
  filter: blur(1px) saturate(120%) brightness(0.9); transform: scale(1.02); }
.veil { position: absolute; inset: 0; background:
  linear-gradient(90deg, rgba(7, 7, 8, 0.94) 0 16%, rgba(7, 7, 8, 0.78) 36%, rgba(7, 7, 8, 0.36) 58%, rgba(7, 7, 8, 0.2) 80%, rgba(7, 7, 8, 0.35)),
  linear-gradient(0deg, rgba(7, 7, 8, 0.8), transparent 40%),
  linear-gradient(180deg, rgba(7, 7, 8, 0.45), transparent 25%); }
/* The film's colour behind the phones (set from the backdrop below). */
.glow { position: absolute; left: ${centre.x - 360}px; top: ${centre.y - 300}px; width: 720px; height: 600px; border-radius: 50%;
  background: radial-gradient(closest-side, var(--glow, rgba(255, 255, 255, 0.2)), transparent); opacity: 0.7; mix-blend-mode: screen; }
.grain { position: absolute; inset: 0; opacity: 0.07; mix-blend-mode: overlay;
  background: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.9' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E"); }
/* A recent iPhone around a whole screen of Mira web (402 × 874 points), as site/css/site.css draws it: titanium band,
   black border, the screen's rounded corners, status bar and home indicator. Sizes in cqw of the phone's width. */
.phone { position: absolute; left: 0; top: 0; width: ${PHONE}px; margin: 0; container-type: inline-size; transform-origin: 50% 50%;
  border-radius: 17.6% / 8.39%;
  background: linear-gradient(145deg, #75777d, #2e2f33 16%, #1c1d20 50%, #2e2f33 84%, #66686e);
  box-shadow: inset 0 0 0 0.5px rgba(255, 255, 255, 0.35), 0 0 0 0.5px #000, 0 40px 70px rgba(0, 0, 0, 0.65), 0 10px 24px rgba(0, 0, 0, 0.5); }
.phone:not(.front) { filter: brightness(0.82); }
.phone.front { z-index: 2; }
.phone::before, .phone::after { content: ''; position: absolute; width: 0.8%; border-radius: 1px; }
.phone::before { left: -0.6%; top: 19%; height: 23.5%; background: linear-gradient(#45464b 0 19%, transparent 19% 34%, #45464b 34% 62%, transparent 62% 72%, #45464b 72%); }
.phone::after { right: -0.6%; top: 29%; height: 32%; background: linear-gradient(#45464b 0 30%, transparent 30% 81%, #2a2b30 81%); }
.glass { position: relative; margin: 1.2cqw; border: 2cqw solid #000; border-radius: 16.4cqw; overflow: hidden; background: #000; }
.glass img { display: block; width: 100%; height: auto; }
.glass::before, .glass::after { content: ''; position: absolute; left: 0; right: 0; z-index: 1; pointer-events: none; }
.glass::before { top: 0; aspect-ratio: 402 / 54;
  background: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 402 54'%3E%3Ctext x='78' y='35.5' text-anchor='middle' font-family='Helvetica,Arial,sans-serif' font-size='17' font-weight='600' fill='%23fff'%3E9:41%3C/text%3E%3Crect x='138.5' y='11' width='125' height='37' rx='18.5' fill='%23000'/%3E%3Ccircle cx='245' cy='29.5' r='5.5' fill='%230d0f17'/%3E%3Ccircle cx='245' cy='29.5' r='2.4' fill='%23151a2b'/%3E%3Cg fill='%23fff'%3E%3Crect x='289' y='31' width='3.4' height='4.5' rx='1'/%3E%3Crect x='294.2' y='28.5' width='3.4' height='7' rx='1'/%3E%3Crect x='299.4' y='26' width='3.4' height='9.5' rx='1'/%3E%3Crect x='304.6' y='23.5' width='3.4' height='12' rx='1'/%3E%3Cpath d='M322.5 35.5l-3.4-3.6a4.9 4.9 0 0 1 6.8 0z'/%3E%3C/g%3E%3Cg fill='none' stroke='%23fff' stroke-width='2.4' stroke-linecap='round'%3E%3Cpath d='M316.6 29.4a8.4 8.4 0 0 1 11.8 0'/%3E%3Cpath d='M313.3 26a13.1 13.1 0 0 1 18.4 0'/%3E%3C/g%3E%3Crect x='339.5' y='23.5' width='25.5' height='12' rx='3.8' fill='none' stroke='%23fff' stroke-opacity='.4'/%3E%3Crect x='341.5' y='25.5' width='19' height='8' rx='2.2' fill='%23fff'/%3E%3Crect x='366' y='27.5' width='1.6' height='4' rx='.8' fill='%23fff' fill-opacity='.4'/%3E%3C/svg%3E") top / 100% auto no-repeat; }
.glass::after { top: 0; bottom: 0; background:
  url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 402 21'%3E%3Crect x='134' y='8' width='134' height='5' rx='2.5' fill='%23fff' fill-opacity='.9'/%3E%3C/svg%3E") bottom / 100% auto no-repeat,
  linear-gradient(115deg, rgba(255, 255, 255, 0.08), transparent 32%); }
.spot { position: absolute; border-radius: 3.5cqw; box-shadow: 0 0 0 200cqw rgba(0, 0, 0, 0.42); }
.name { position: absolute; left: 80px; top: 50%; transform: translateY(-50%); z-index: 3; }
.brand { display: flex; align-items: center; gap: 12px; font-weight: 800; font-size: 32px; color: rgba(244, 244, 245, 0.94); }
.brand svg { width: 34px; height: 34px; }
.version { font-weight: 800; font-size: 148px; line-height: 0.92; letter-spacing: -4px; margin: 8px 0 0 -6px; font-variant-numeric: tabular-nums;
  text-shadow: 0 8px 40px rgba(0, 0, 0, 0.5); }
</style></head><body>
<div class="backdrop"></div><div class="veil"></div><div class="glow"></div>
${phones}
<div class="grain"></div>
<div class="name">
  <div class="brand"><svg viewBox="0 0 24 24"><path fill="#f5f5f7" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="#101013" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></svg>Mira</div>
  <div class="version">${version}</div>
</div>
<script>
  // The glow: the backdrop's most vivid colour, among its pixels bright enough to light anything.
  (async () => {
    const source = getComputedStyle(document.querySelector('.backdrop')).backgroundImage.match(/url\\("?(.*?)"?\\)$/)?.[1];
    if (!source) { document.body.dataset.ready = 1; return; }
    const img = new Image(); img.src = source; await img.decode();
    const c = new OffscreenCanvas(48, 27), g = c.getContext('2d'); g.drawImage(img, 0, 0, 48, 27);
    const d = g.getImageData(0, 0, 48, 27).data;
    let best = [255, 255, 255], score = -1;
    for (let i = 0; i < d.length; i += 4) {
      const [r, gg, b] = [d[i], d[i + 1], d[i + 2]], max = Math.max(r, gg, b), min = Math.min(r, gg, b);
      const s = max ? (max - min) / max : 0, v = max / 255, value = s * 0.7 + v * 0.3;
      if (v > 0.35 && value > score) { score = value; best = [r, gg, b]; }
    }
    document.body.style.setProperty('--glow', 'rgb(' + best.join(',') + ')');
    // A longer version (0.10.12) kept clear of the phones.
    const v = document.querySelector('.version');
    for (let size = 148; v.getBoundingClientRect().right > ${Math.round(centre.x - spread - PHONE * SIDE / 2 - 60)} && size > 60; size -= 4) v.style.fontSize = size + 'px';
    document.body.dataset.ready = 1;
  })();
</script>
</body></html>`;

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: W, height: H }, deviceScaleFactor: 2 });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() => document.body.dataset.ready, null, { timeout: 10000 });
  await page.screenshot({ path: out, type: out.endsWith('.png') ? 'png' : 'jpeg', ...(out.endsWith('.png') ? {} : { quality: 88 }) });
  if (process.env.MIRA_BANNER_HTML) writeFileSync(process.env.MIRA_BANNER_HTML, html);
  console.log(out);
} finally { await browser.close(); }
