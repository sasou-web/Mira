// The cards shown where a link to the site is shared (site/img/share.jpg and share-en.jpg, 1200 × 630), in the site's
// own style: its opening line in light Nunito beside the name, then Mira for Windows' Home edge to edge, as the page
// shows it. Drawn from the site's own font, mark and capture.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/site/share.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const site = pathToFileURL(resolve('site')).href;
const cards = {
  'share.jpg': 'Ta bibliothèque Jellyfin,<br>comme au cinéma.',
  'share-en.jpg': 'Your Jellyfin library,<br>like at the movies.',
};
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
// A page opened from a file may read the site's files; one set as content may not.
const scratch = mkdtempSync(join(tmpdir(), 'mira-share-'));
for (const [file, title] of Object.entries(cards)) {
  writeFileSync(join(scratch, 'card.html'), `<!doctype html><html><head><meta charset="utf-8"><style>
    @font-face { font-family: N; font-weight: 200 1000; src: url('${site}/fonts/nunito.woff2'); }
    body { margin: 0; width: 1200px; height: 630px; overflow: hidden; background: #000000; color: #f4f4f4; font-family: N; }
    .card { display: flex; align-items: flex-end; justify-content: space-between; gap: 40px; height: 268px; padding: 0 64px 42px; box-sizing: border-box; }
    h1 { margin: 0; font-size: 62px; font-weight: 300; line-height: 0.98; letter-spacing: -0.045em; }
    .brand { display: flex; align-items: center; gap: 14px; padding-bottom: 6px; font-size: 34px; font-weight: 800; letter-spacing: -0.02em; }
    .brand img { width: 46px; height: 46px; }
    .screen { height: 362px; overflow: hidden; }
    .screen img { display: block; width: 100%; }
  </style></head><body>
    <div class="card"><h1>${title}</h1><div class="brand"><img src="${site}/img/mark.svg" alt="">Mira</div></div>
    <div class="screen"><img src="${site}/img/windows-home-2400.webp" alt=""></div>
  </body></html>`);
  await page.goto(pathToFileURL(join(scratch, 'card.html')).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `site/img/${file}`, type: 'jpeg', quality: 86 });
}
await browser.close();
rmSync(scratch, { recursive: true, force: true });
// Discord, Slack and the others keep a card by its address, for weeks: each page names its card with the card's own
// fingerprint, a new address as soon as the card changes.
for (const [page, file] of [['site/index.html', 'share.jpg'], ['site/en/index.html', 'share-en.jpg']]) {
  const version = createHash('sha256').update(readFileSync(`site/img/${file}`)).digest('hex').slice(0, 10);
  const before = readFileSync(page, 'utf8');
  const after = before.replace(new RegExp(`/img/${file.replace('.', '\\.')}(\\?v=[0-9a-f]+)?"`), `/img/${file}?v=${version}"`);
  if (after === before && !before.includes(`/img/${file}?v=${version}"`)) throw new Error(`No og:image for ${file} in ${page}`);
  writeFileSync(page, after);
}
