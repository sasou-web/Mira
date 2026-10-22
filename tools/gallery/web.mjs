// The README's captures of Mira web, a whole iPhone screen in Chromium, on the library of tools/gallery/setup.mjs:
// Home, a film's page, the films and a series' page.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/gallery/web.mjs <url of /Mira/> <user> <password> <folder>
import { mkdirSync, writeFileSync } from 'node:fs';
const [base, user, password, out] = process.argv.slice(2);
if (!out) { console.error('usage: web.mjs <url /Mira/> <user> <password> <folder>'); process.exit(2); }
mkdirSync(out, { recursive: true });
const { chromium, devices } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch();
// The whole screen of an iPhone 17 Pro (402 × 874 points, ×3), as opened from its home screen: no Safari bars, the page
// under the status bar and the home indicator (black-translucent), kept clear by the safe areas iOS gives it. Still
// images: the Home's banner does not turn between two captures.
const context = await browser.newContext({
  ...devices['iPhone 15 Pro'], viewport: { width: 402, height: 874 }, screen: { width: 402, height: 874 }, deviceScaleFactor: 3,
  locale: 'fr-FR', reducedMotion: 'reduce',
});
// As opened from the iPhone's home screen, the way Mira web is meant to be used: no invitation to add it there.
await context.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
const page = await context.newPage();
const cdp = await context.newCDPSession(page);
await cdp.send('Emulation.setSafeAreaInsetsOverride', { insets: { top: 62, bottom: 34, left: 0, right: 0 } });
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
const wait = (ms) => page.waitForTimeout(ms);
// Every image on screen drawn, then the screen at rest (the page's own fades end within a second).
const settle = async () => {
  await page.waitForFunction(() => [...document.images].filter((i) => {
    const r = i.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.width > 0;
  }).every((i) => i.complete), null, { timeout: 15000 }).catch(() => console.log('::warning::images still loading'));
  await wait(1500);
};
const shot = async (name) => { await settle(); await page.screenshot({ path: `${out}/${name}.png` }); console.log(`${name}.png`); };
const api = (path) => page.evaluate(async (p) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { headers: { Authorization: `MediaBrowser Client="gallery", Device="gallery", DeviceId="gallery", Version="1", Token="${s.token}"` } });
  return r.json();
}, path);

try {
  await page.goto(base); await wait(1500);
  if (await page.locator('.users').count()) await page.getByRole('button', { name: 'Autre compte' }).click();
  await page.fill('#login-name', user); await page.fill('#login-password', password);
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await page.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await wait(3000);
  // Whatever greets a first visit (« Nouveau dans Mira », an invitation) is closed: the captures show the app itself.
  for (const name of ['Fermer', 'Plus tard', 'Pas maintenant', 'OK']) {
    const button = page.getByRole('button', { name, exact: true });
    if (await button.count() && await button.first().isVisible()) await button.first().click().catch(() => {});
  }
  await shot('web-home');
  const session = await page.evaluate(() => JSON.parse(localStorage.getItem('mira.session')));
  const { Items: titles } = await api(`Items?userId=${session.userId}&recursive=true&includeItemTypes=Movie,Series&fields=Path`);
  const find = (name) => titles.find((x) => (x.Path ?? '').replace(/\\/g, '/').includes(name));
  const film = find('Spring (2019)'), series = find('Pioneer One');
  await page.locator('.tabbar a[data-tab="films"]').click(); await wait(2500);
  await shot('web-films');
  if (film) { await page.goto(`${base}#/titre/${film.Id}`); await wait(2500); await shot('web-film'); }
  if (series) { await page.goto(`${base}#/titre/${series.Id}`); await wait(2500); await shot('web-series'); }
  // The featured film's backdrop, as Jellyfin serves it (anonymous, a download rather than a page): the still of the
  // showcase site, in PNG like the captures.
  if (film) {
    const still = await page.request.get(new URL(`../Items/${film.Id}/Images/Backdrop?maxWidth=1920&quality=95&format=Png`, base).href);
    if (!still.ok()) throw new Error(`backdrop: ${still.status()}`);
    writeFileSync(`${out}/film-still.png`, await still.body()); console.log('film-still.png');
  }
  if (errors.length) console.log(`::warning::page errors: ${errors.join(' | ')}`);
} finally { await browser.close(); }
