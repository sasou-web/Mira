// The cards shown where a link to the site is shared (site/img/share.jpg, share-en.jpg and invite-en.jpg, 1200 × 630), in the site's
// own style (« Le vidéoclub »): the wall of posters leaning back, the title over it, and the window of Mira coming up
// from the bottom. Drawn from the site's own font, mark, posters and capture: run after tools/site/images.py.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/site/share.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const site = pathToFileURL(resolve('site')).href;
const cards = {
  'share.jpg': '<b>Ta bibliothèque Jellyfin,</b> comme au cinéma.',
  'share-en.jpg': '<b>Your Jellyfin library,</b> like at the movies.',
  // A friend's invite or a home link (site/join, site/home): the same card for every link, the link itself never sent.
  'invite-en.jpg': '<b>You’re invited</b> to watch.',
};
// The wall as the English page lays it out (written there by images.py).
const wall = [...readFileSync('site/index.html', 'utf8').matchAll(/<img src="img\/p\/([^"]+)"/g)].map(m => m[1]);
if (wall.length < 42) throw new Error('No wall of posters in site/index.html: run tools/site/images.py first');
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
// A page opened from a file may read the site's files; one set as content may not.
const scratch = mkdtempSync(join(tmpdir(), 'mira-share-'));
for (const [file, title] of Object.entries(cards)) {
  writeFileSync(join(scratch, 'card.html'), `<!doctype html><html><head><meta charset="utf-8"><style>
    @font-face { font-family: N; font-weight: 200 1000; src: url('${site}/fonts/nunito.woff2'); }
    body { position: relative; margin: 0; width: 1200px; height: 630px; overflow: hidden; background: #000000; color: #f4f4f4; font-family: N; }
    .wall { position: absolute; inset: 0; overflow: hidden; perspective: 1100px; }
    .plane { position: absolute; left: 50%; top: 40%; display: grid; grid-template-columns: repeat(14, 1fr); gap: 14px; width: 1900px; transform: translate(-50%, -50%) rotateX(32deg) rotateZ(-9deg); }
    .plane img { width: 100%; aspect-ratio: 2 / 3; object-fit: cover; border-radius: 9px; }
    .wall::after { content: ''; position: absolute; inset: 0; background:
      linear-gradient(to bottom, rgba(0,0,0,0.55) 0, rgba(0,0,0,0) 22%, rgba(0,0,0,0) 48%, rgba(0,0,0,0.9) 70%, #000 82%),
      radial-gradient(ellipse 52% 46% at 50% 30%, rgba(0,0,0,0.92) 0, rgba(0,0,0,0.8) 50%, rgba(0,0,0,0.25) 85%, rgba(0,0,0,0.1) 100%); }
    .title { position: absolute; left: 0; right: 0; top: 58px; display: grid; justify-items: center; gap: 22px; text-align: center; }
    h1 { margin: 0; font-size: 66px; font-weight: 300; line-height: 0.98; letter-spacing: -0.045em; }
    h1 b { display: block; font-weight: 800; }
    .brand { display: flex; align-items: center; gap: 12px; font-size: 30px; font-weight: 800; letter-spacing: -0.02em; }
    .brand img { width: 40px; height: 40px; }
    .window { position: absolute; left: 90px; right: 90px; top: 352px; aspect-ratio: 16 / 10; overflow: hidden; border-radius: 14px; box-shadow: inset 0 0 0 1px rgba(255,255,255,0.1); }
    .window img { display: block; width: 100%; }
  </style></head><body>
    <div class="wall"><div class="plane">${wall.map(p => `<img src="${site}/img/p/${p}" alt="">`).join('')}</div></div>
    <div class="title"><h1>${title}</h1><div class="brand"><img src="${site}/img/mark.svg" alt="">Mira</div></div>
    <div class="window"><img src="${site}/img/windows-home-2000.webp" alt=""></div>
  </body></html>`);
  await page.goto(pathToFileURL(join(scratch, 'card.html')).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `site/img/${file}`, type: 'jpeg', quality: 86 });
}
await browser.close();
rmSync(scratch, { recursive: true, force: true });
// Discord, Slack and the others keep a card by its address, for weeks: each page names its card with the card's own
// fingerprint, a new address as soon as the card changes.
for (const [page, file] of [['site/fr/index.html', 'share.jpg'], ['site/index.html', 'share-en.jpg'], ['site/join/index.html', 'invite-en.jpg'], ['site/home/index.html', 'invite-en.jpg']]) {
  const version = createHash('sha256').update(readFileSync(`site/img/${file}`)).digest('hex').slice(0, 10);
  const before = readFileSync(page, 'utf8');
  const after = before.replace(new RegExp(`/img/${file.replace('.', '\\.')}(\\?v=[0-9a-f]+)?"`), `/img/${file}?v=${version}"`);
  if (after === before && !before.includes(`/img/${file}?v=${version}"`)) throw new Error(`No og:image for ${file} in ${page}`);
  writeFileSync(page, after);
}
