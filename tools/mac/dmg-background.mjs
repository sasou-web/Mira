// The picture behind Mira's disk image window (tools/mac/dmg/background.png and background@2x.png, read by
// tools/mac/dmg/settings.py): Mira's blacks and off-white, the arrow from Mira to Applications, and the one step macOS
// asks for on the first launch of an app signed ad hoc. Icons' places come from tools/mac/dmg/layout.json.
// Finder writes each icon's name over the picture, in black in light appearance and in white in dark: each name sits on
// a mid grey plate that keeps both readable (about 4.5:1 either way). labelBelow is where Finder centres the name under
// its icon (112 px icons, 13 px text, measured on macOS 15); plates gives each plate's width.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/mac/dmg-background.mjs
import { pathToFileURL } from 'node:url';
import { join, resolve } from 'node:path';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const layout = JSON.parse(readFileSync('tools/mac/dmg/layout.json', 'utf8'));
const { width, height, iconSize } = layout;
const [appX, y] = layout.app, [appsX] = layout.applications;
const labelY = y + layout.labelBelow, plates = layout.plates;
const site = pathToFileURL(resolve('site')).href;
// The arrow runs between the two icons, a little apart from each.
const from = appX + iconSize / 2 + 30, to = appsX - iconSize / 2 - 30;
const html = `<!doctype html><html><head><style>
  @font-face { font-family: N; font-weight: 600; src: url('${site}/fonts/nunito-sans-600.woff2'); }
  @font-face { font-family: N; font-weight: 700; src: url('${site}/fonts/nunito-sans-700.woff2'); }
  html, body { margin: 0; }
  body { width: ${width}px; height: ${height}px; overflow: hidden; position: relative; font-family: N; color: #a2a2a6;
    background: #070708; -webkit-font-smoothing: antialiased; }
  /* The screening room: light falls from the projector above, on the two icons. */
  .beam { position: absolute; inset: 0; background:
    radial-gradient(ellipse 62% 74% at 50% -8%, rgba(245,245,247,.085), rgba(245,245,247,.03) 52%, transparent 78%); }
  .stage { position: absolute; left: 0; right: 0; top: ${y - 96}px; height: 210px; background:
    radial-gradient(ellipse 30% 46% at ${appX}px 46%, rgba(245,245,247,.05), transparent 70%),
    radial-gradient(ellipse 30% 46% at ${appsX}px 46%, rgba(245,245,247,.05), transparent 70%); }
  .plate { position: absolute; top: ${labelY - 11}px; height: 22px; border-radius: 11px; background: #76767b;
    box-shadow: inset 0 1px 0 rgba(255,255,255,.12), 0 6px 18px rgba(0,0,0,.45); }
  svg.arrow { position: absolute; left: ${from}px; top: ${y - 12}px; }
  .foot { position: absolute; left: 32px; right: 32px; bottom: 0; height: 78px; border-top: 1px solid #1d1d20;
    display: flex; flex-direction: column; justify-content: center; align-items: center; gap: 5px; text-align: center; }
  .foot p { margin: 0; font-size: 11.5px; line-height: 1.25; font-weight: 600; letter-spacing: .005em; }
  .foot b { color: #d4d4d8; font-weight: 700; }
  .foot .sep { color: #5c5c62; padding: 0 3px; }
</style></head><body>
  <div class="beam"></div><div class="stage"></div>
  <div class="plate" style="left: ${appX - plates.app / 2}px; width: ${plates.app}px"></div>
  <div class="plate" style="left: ${appsX - plates.applications / 2}px; width: ${plates.applications}px"></div>
  <svg class="arrow" width="${to - from}" height="24" viewBox="0 0 ${to - from} 24">
    <!-- In user space: a horizontal line has no height for a bounding box gradient to stretch over. -->
    <defs><linearGradient id="g" gradientUnits="userSpaceOnUse" x1="0" x2="${to - from}" y1="0" y2="0"><stop offset="0" stop-color="#f5f5f7" stop-opacity="0"/>
      <stop offset=".55" stop-color="#f5f5f7" stop-opacity=".5"/><stop offset="1" stop-color="#f5f5f7" stop-opacity=".92"/></linearGradient></defs>
    <path d="M2 12H${to - from - 4}" stroke="url(#g)" stroke-width="2" stroke-linecap="round" fill="none"/>
    <path d="M${to - from - 12} 5L${to - from - 3} 12L${to - from - 12} 19" stroke="#f5f5f7" stroke-opacity=".92" stroke-width="2"
      stroke-linecap="round" stroke-linejoin="round" fill="none"/>
  </svg>
  <div class="foot">
    <p>First launch: <b>System Settings</b><span class="sep">›</span><b>Privacy &amp; Security</b><span class="sep">›</span><b>Open Anyway</b></p>
    <p>Premier lancement : <b>Réglages Système</b><span class="sep">›</span><b>Confidentialité et sécurité</b><span class="sep">›</span><b>Ouvrir quand même</b></p>
  </div>
</body></html>`;
const browser = await chromium.launch();
const scratch = mkdtempSync(join(tmpdir(), 'mira-dmg-'));
writeFileSync(join(scratch, 'background.html'), html);
for (const [scale, file] of [[1, 'background.png'], [2, 'background@2x.png']]) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: scale });
  await page.goto(pathToFileURL(join(scratch, 'background.html')).href, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: `tools/mac/dmg/${file}` });
  await page.close();
}
await browser.close();
rmSync(scratch, { recursive: true, force: true });
