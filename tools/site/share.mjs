// The cards shown where a link to the site is shared (site/img/share.jpg and share-en.jpg, 1200 × 630): drawn from
// the site's own fonts, mark and screenshots. PLAYWRIGHT=<.../playwright/index.mjs> node tools/site/share.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const site = pathToFileURL(resolve('site')).href;
const cards = {
  'share.jpg': ['Ta bibliothèque Jellyfin, comme au cinéma.', 'Gratuit · Windows, Mac, iPhone, Android'],
  'share-en.jpg': ['Your Jellyfin library, like at the movies.', 'Free · Windows, Mac, iPhone, Android'],
};
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1200, height: 630 } });
// A page opened from a file may read the site's files; one set as content may not.
const scratch = mkdtempSync(join(tmpdir(), 'mira-share-'));
for (const [file, [title, line]] of Object.entries(cards)) {
  writeFileSync(join(scratch, 'card.html'), `<!doctype html><html><head><style>
    @font-face { font-family: N; font-weight: 800; src: url('${site}/fonts/nunito-sans-800.woff2'); }
    @font-face { font-family: N; font-weight: 600; src: url('${site}/fonts/nunito-sans-600.woff2'); }
    body { margin: 0; width: 1200px; height: 630px; overflow: hidden; background: #070708; color: #f4f4f5; font-family: N; position: relative; }
    .glow { position: absolute; right: -120px; top: 40px; width: 820px; height: 520px; filter: blur(90px) saturate(150%); opacity: .55; }
    .shot { position: absolute; left: 560px; top: 120px; width: 860px; border-radius: 18px; border: 1px solid #26262a;
      box-shadow: 0 40px 100px rgba(0,0,0,.7); transform: perspective(1600px) rotateY(-14deg) rotateX(4deg); transform-origin: 0 50%; }
    .copy { position: absolute; left: 72px; top: 0; bottom: 0; width: 500px; display: flex; flex-direction: column; justify-content: center; gap: 28px; }
    .brand { display: flex; align-items: center; gap: 14px; font-size: 34px; font-weight: 800; letter-spacing: -.02em; }
    .brand img { width: 52px; height: 52px; }
    h1 { margin: 0; font-size: 60px; line-height: .98; letter-spacing: -.035em; font-weight: 800; }
    p { margin: 0; color: #a2a2a6; font-size: 24px; font-weight: 600; }
    .fade { position: absolute; inset: 0; background: linear-gradient(90deg, #070708 44%, rgba(7,7,8,.6) 60%, transparent 80%); }
  </style></head><body>
    <img class="glow" src="${site}/img/windows-home-960.webp">
    <img class="shot" src="${site}/img/windows-home-1600.webp">
    <div class="fade"></div>
    <div class="copy"><div class="brand"><img src="${site}/img/mark.svg">Mira</div><h1>${title}</h1><p>${line}</p></div>
  </body></html>`);
  await page.goto(pathToFileURL(join(scratch, 'card.html')).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `site/img/${file}`, type: 'jpeg', quality: 86 });
}
await browser.close();
rmSync(scratch, { recursive: true, force: true });
