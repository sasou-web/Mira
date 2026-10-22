// The cards shown where a link to the site is shared (site/img/share.jpg and share-en.jpg, 1200 × 630): the site's
// opening, drawn from its own fonts, mark and featured film's still. PLAYWRIGHT=<.../playwright/index.mjs> node tools/site/share.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const site = pathToFileURL(resolve('site')).href;
const cards = {
  'share.jpg': ['Ta bibliothèque Jellyfin, comme au cinéma.', 'Une séance privée, chez toi'],
  'share-en.jpg': ['Your Jellyfin library, like at the movies.', 'A private screening, at home'],
};
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
// A page opened from a file may read the site's files; one set as content may not.
const scratch = mkdtempSync(join(tmpdir(), 'mira-share-'));
for (const [file, [title, kicker]] of Object.entries(cards)) {
  writeFileSync(join(scratch, 'card.html'), `<!doctype html><html><head><style>
    @font-face { font-family: N; font-weight: 800; src: url('${site}/fonts/nunito-sans-800.woff2'); }
    @font-face { font-family: N; font-weight: 700; src: url('${site}/fonts/nunito-sans-700.woff2'); }
    body { margin: 0; width: 1200px; height: 630px; overflow: hidden; background: #000; color: #f4f4f5; font-family: N; position: relative; }
    .still { position: absolute; left: 0; right: 0; top: 64px; height: 502px; width: 1200px; object-fit: cover; object-position: 50% 40%; filter: brightness(.8); }
    .shade { position: absolute; left: 0; right: 0; top: 64px; height: 502px;
      background: linear-gradient(90deg, rgba(0,0,0,.8), rgba(0,0,0,.3) 55%, transparent 80%), linear-gradient(transparent 55%, rgba(0,0,0,.7)); }
    .copy { position: absolute; left: 72px; top: 120px; display: grid; gap: 14px; }
    .kicker { font-size: 15px; font-weight: 800; letter-spacing: .3em; text-transform: uppercase; color: #cfcfd4; }
    h1 { margin: 0; font-size: 168px; line-height: .8; letter-spacing: -.06em; font-weight: 800; }
    .line { position: absolute; left: 0; right: 0; bottom: 92px; text-align: center; font-size: 34px; font-weight: 700; }
    .line span { padding: 2px 12px; border-radius: 5px; background: rgba(0,0,0,.42); text-shadow: 0 2px 4px rgba(0,0,0,.9); }
    .bar { position: absolute; left: 48px; right: 48px; bottom: 30px; height: 5px; border-radius: 3px; background: rgba(255,255,255,.22); }
    .bar i { position: absolute; inset: 0 auto 0 0; width: 9%; border-radius: 3px; background: #f5f5f7; }
    .brand { position: absolute; left: 48px; top: 16px; display: flex; align-items: center; gap: 10px; font-size: 24px; font-weight: 800; }
    .brand img { width: 32px; height: 32px; }
  </style></head><body>
    <img class="still" src="${site}/img/film-still-1280.webp"><div class="shade"></div>
    <div class="brand"><img src="${site}/img/mark.svg">Mira</div>
    <div class="copy"><div class="kicker">${kicker}</div><h1>Mira</h1></div>
    <p class="line"><span>${title}</span></p>
    <div class="bar"><i></i></div>
  </body></html>`);
  await page.goto(pathToFileURL(join(scratch, 'card.html')).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `site/img/${file}`, type: 'jpeg', quality: 86 });
}
await browser.close();
rmSync(scratch, { recursive: true, force: true });
