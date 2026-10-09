// Mira web against a real Jellyfin, in Chromium at an iPhone's size: the sign-in form, every screen, playback, the
// next episode, resume, the reports Jellyfin receives, sign-out. Screenshots for review; results.txt; exit 1 on failure.
//   node tools/web/check.mjs <Mira web url> <user> <password> <series name> <out folder>
// Chromium has no H.264: the series checked here is in WebM; Safari's HLS path is checked by safari.mjs.
import fs from 'node:fs';
const { chromium, devices } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const [base = 'http://127.0.0.1:8096/Mira/', user = 'sasou', password = 'premier', seriesName = 'Courte Web', out = 'shots/e2e'] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });
const results = [];
const check = (name, ok, detail = '') => { results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' — ' + detail : ''}`); console.log(results.at(-1)); };
const browser = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] });
const context = await browser.newContext({ ...devices['iPhone 15 Pro'] });
const page = await context.newPage();
const errors = [], reports = [];
page.on('pageerror', (e) => errors.push(e.message));
page.on('request', (r) => { const m = r.url().match(/Sessions\/Playing(\/\w+)?/); if (m && r.method() === 'POST') reports.push(m[1] ? m[1].slice(1) : 'Start'); });
const shot = (name) => page.screenshot({ path: `${out}/${name}.png` });
const wait = (ms) => page.waitForTimeout(ms);
const api = async (path, method = 'GET') => page.evaluate(async ([p, m]) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { method: m, headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
  const t = await r.text(); return t ? JSON.parse(t) : null;
}, [path, method]);

try {
  await page.goto(base); await wait(1500);
  check('connexion : formulaire affiché', await page.locator('form.login-form, .users').count() > 0);
  await shot('01-login');
  if (await page.locator('.users').count()) await page.getByRole('button', { name: 'Autre compte' }).click();
  await page.fill('#login-name', user); await page.fill('#login-password', password);
  await page.getByRole('button', { name: 'Se connecter' }).click();
  await page.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await wait(3000);
  check('accueil : rangées affichées', await page.locator('.section').count() > 0, `${await page.locator('.section h2').allInnerTexts()}`);
  await shot('02-home');

  await page.locator('.tabbar a[data-tab="films"]').click(); await wait(2000);
  const count = await page.locator('.library-count').innerText();
  check('films : grille', /film/.test(count), count);
  await shot('03-films');

  await page.locator('.tabbar a[data-tab="search"]').click(); await wait(800);
  await page.fill('input[type=search]', seriesName.split(' ')[0]); await wait(1800);
  check('recherche', await page.locator('.search .card').count() > 0);
  await shot('04-search');

  const found = await api(`Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent(seriesName)}`);
  const series = found.Items[0];
  const episodes = (await api(`Shows/${series.Id}/Episodes?userId=${JSON.parse(await page.evaluate(() => localStorage.getItem('mira.session'))).userId}`)).Items;
  for (const e of episodes) await api(`UserPlayedItems/${e.Id}`, 'DELETE');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, series.Id); await wait(2500);
  const primary = await page.locator('.detail-actions .btn.primary').innerText();
  check('fiche de la série', /S1 · É1/.test(primary), primary.replace(/\s+/g, ' '));
  check('fiche : épisodes', await page.locator('.episode').count() === episodes.length, `${episodes.length} épisodes`);
  await shot('05-series');

  reports.length = 0;
  await page.locator('.detail-actions .btn.primary').click();
  await wait(3500);
  const playing = await page.evaluate(() => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime > 0.5; });
  check('lecture : l’épisode 1 joue', playing);
  await shot('06-player');
  await page.waitForFunction((id) => location.hash.includes(id), episodes[1].Id, { timeout: 30000 }).then(() => check('épisode suivant automatique', true, 'É1 → É2'), () => check('épisode suivant automatique', false));
  await wait(3000);
  const t2 = await page.evaluate(() => document.querySelector('video')?.currentTime ?? 0);
  check('lecture : l’épisode 2 joue', t2 > 0.5, `${t2.toFixed(1)} s`);
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true });
  await wait(2500);
  const first = await api(`Items/${episodes[0].Id}`), second = await api(`Items/${episodes[1].Id}`);
  check('Jellyfin : épisode 1 vu', first.UserData.Played === true);
  check('Jellyfin : position de l’épisode 2', second.UserData.PlaybackPositionTicks > 10_000_000, `${(second.UserData.PlaybackPositionTicks / 1e7).toFixed(1)} s`);
  check('rapports envoyés', ['Start', 'Progress', 'Stopped'].every((k) => reports.includes(k)), [...new Set(reports)].join(', '));
  await wait(1000);
  const resumeLabel = await page.locator('.detail-actions .btn.primary').innerText().catch(() => '');
  check('retour sur la fiche : reprise proposée', /Reprendre S1 · É2/.test(resumeLabel), resumeLabel.replace(/\s+/g, ' '));
  await shot('07-series-after');

  await page.locator('.detail-actions .btn.primary').click(); await wait(3000);
  const resumed = await page.evaluate(() => document.querySelector('video')?.currentTime ?? 0);
  check('reprise au bon endroit', resumed >= second.UserData.PlaybackPositionTicks / 1e7 - 1, `${resumed.toFixed(1)} s`);
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500);

  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1500);
  await shot('08-settings');
  await page.evaluate(() => { location.hash = '#/partager'; }); await wait(1500);
  check('page « Sur ton téléphone » : QR code', await page.locator('.qr svg').count() === 1, await page.locator('.address').innerText());
  await shot('09-share');
  await page.setViewportSize({ width: 1280, height: 800 }); await wait(300);
  await page.evaluate(() => { location.hash = '#/'; }); await wait(2500);
  check('grand écran : barre de navigation à gauche', await page.locator('.tabbar').evaluate((n) => getComputedStyle(n).flexDirection === 'column'));
  await shot('10-wide-home');
  await page.setViewportSize({ width: 393, height: 852 }); await wait(300);
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1200);
  await page.getByRole('button', { name: 'Se déconnecter' }).click(); await wait(400);
  await page.locator('.sheet-item.danger').click(); await wait(1500);
  check('déconnexion', (await page.evaluate(() => location.hash)) === '#/connexion' && !(await page.evaluate(() => localStorage.getItem('mira.session'))));
} catch (error) {
  check('déroulé', false, error.message.split('\n')[0]);
  await shot('error').catch(() => {});
}
check('aucune erreur JavaScript', errors.length === 0, errors.slice(0, 3).join(' | '));
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
await browser.close();
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
