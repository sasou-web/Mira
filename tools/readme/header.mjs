// The README's header (docs/screenshots/readme/header.jpg): the library of the captures as a wall of posters leaning back,
// the name and the line of the site in the middle (« Le vidéoclub », the site's direction chosen by ogsas on 2026-11-15).
// Posters: the gallery's Jellyfin (TMDb, the README's rights), poster-<slug>.jpg as tools/banner/stills.mjs
// writes them with MIRA_STILLS_ALL=1 or the captures' folder keeps them.
//   PLAYWRIGHT=… node tools/readme/header.mjs <posters folder> <out.jpg> [en|fr]
import { mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const [folder, out, language = 'en'] = process.argv.slice(2);
if (!out || !['en', 'fr'].includes(language)) { console.error('usage: header.mjs <posters folder> <out.jpg> [en|fr]'); process.exit(2); }
const words = language === 'fr'
  ? { first: 'Ta bibliothèque Jellyfin,', then: 'comme au cinéma.', line: 'Des apps gratuites pour Windows, Mac et iPhone.' }
  : { first: 'Your Jellyfin library,', then: 'played like at the movies.', line: 'Free apps for Windows, Mac and iPhone.' };
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT || 'playwright');

// Arcane stays out of Mira's public pages (ogsas, 2026-11-15).
const left = new Set(['arcane']);
const posters = readdirSync(folder).filter((f) => /^poster-.*\.(jpe?g|webp|png)$/.test(f))
  .filter((f) => !left.has(f.replace(/^poster-|\.\w+$/g, ''))).sort();
if (posters.length < 20) { console.error(`${posters.length} posters in ${folder}: 20 at least`); process.exit(1); }
// A fixed shuffle: the same wall from the same posters, neighbours never from the same family of names.
const mixed = posters.map((p, i) => [((i * 2654435761) >>> 0) % 997, p]).sort((a, b) => a[0] - b[0]).map(([, p]) => p);
const wall = Array.from({ length: 84 }, (_, i) => mixed[i % mixed.length]);
const font = pathToFileURL(resolve('site/fonts/nunito.woff2')).href;
const logo = `data:image/png;base64,${readFileSync('src/Mira.Desktop/Assets/mira.png').toString('base64')}`;

const html = `<!doctype html><meta charset="utf-8"><style>
@font-face { font-family: Nunito; font-weight: 200 1000; src: url('${font}') format('woff2'); }
html, body { margin: 0; background: #000; }
.hero { position: relative; width: 1600px; height: 600px; overflow: hidden; background: #000; font-family: Nunito; color: #f4f4f4; }
.wall { position: absolute; inset: 0; perspective: 1400px; }
.plane { position: absolute; left: 50%; top: 46%; display: grid; grid-template-columns: repeat(14, 1fr); gap: 18px; width: 2500px;
  transform: translate(-50%, -50%) rotateX(32deg) rotateZ(-9deg); }
.plane img { width: 100%; aspect-ratio: 2 / 3; object-fit: cover; border-radius: 12px; display: block; }
.wall::after { content: ''; position: absolute; inset: 0; background:
  linear-gradient(to bottom, rgba(0,0,0,0.55) 0, rgba(0,0,0,0) 22%, rgba(0,0,0,0) 70%, rgba(0,0,0,0.85) 100%),
  radial-gradient(ellipse 50% 52% at 50% 50%, rgba(0,0,0,0.93) 0, rgba(0,0,0,0.87) 50%, rgba(0,0,0,0.3) 82%, rgba(0,0,0,0.15) 100%); }
.lead { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; text-align: center; }
.name { display: flex; align-items: center; gap: 16px; margin-bottom: 26px; font-size: 40px; font-weight: 800; letter-spacing: -0.02em; }
.name img { width: 56px; height: 56px; }
h1 { margin: 0; font-size: 84px; line-height: 0.98; letter-spacing: -0.045em; font-weight: 300; }
h1 b { display: block; font-weight: 800; }
p { margin: 26px 0 0; color: #d6d6d6; font-size: 24px; font-weight: 500; }
</style><div class="hero"><div class="wall"><div class="plane">${wall.map((p) => `<img src="${pathToFileURL(join(resolve(folder), p)).href}">`).join('')}</div></div>
<div class="lead"><div class="name"><img src="${logo}" alt="">Mira</div><h1><b>${words.first}</b> ${words.then}</h1>
<p>${words.line}</p></div></div>`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 600 }, deviceScaleFactor: 2 });
// A file page: from about:blank, Chromium refuses the posters and the font as file:// addresses.
const work = mkdtempSync(join(tmpdir(), 'mira-header-'));
writeFileSync(join(work, 'header.html'), html);
await page.goto(pathToFileURL(join(work, 'header.html')).href, { waitUntil: 'load' });
await page.evaluate(() => document.fonts.ready);
await page.locator('.hero').screenshot({ path: out, type: 'jpeg', quality: 84 });
await browser.close();
rmSync(work, { recursive: true, force: true });
console.log(`${out}: ${posters.length} posters`);
