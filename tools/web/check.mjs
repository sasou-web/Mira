// Mira web against a real Jellyfin, in Chromium at an iPhone's size: the sign-in form, every screen, playback, the
// next episode, resume, the reports Jellyfin receives, the choice of tracks on a title page, sign-out. Screenshots for
// review; results.txt; exit 1 on failure.
//   node tools/web/check.mjs <Mira web url> <user> <password> <series name> <out folder>
// Chromium has no H.264: the titles checked here are in WebM; Safari's HLS path is checked by safari.mjs.
// Apple's full screen player is simulated here (Chromium has none), to check what Mira does around it: start, next
// episode in place, Picture in Picture, back to the title page once closed. safari.mjs checks it in Safari itself.
// So is the Home Screen app on iPhone (navigator.standalone): swipe back from the edge, tabs, sheets pulled down.
// With MIRA_STOP and MIRA_START (commands that stop and start Jellyfin), Mira is also opened while Jellyfin is away,
// as when a phone opens it before the PC has started: the page kept by the phone opens and waits for the server.
import fs from 'node:fs';
import { execSync } from 'node:child_process';
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

  // Tracks chosen on a title page: Lueur Web has French and English audio, and subtitles in both beside it.
  const film = (await api('Items?recursive=true&includeItemTypes=Movie&searchTerm=Lueur')).Items[0];
  const streams = (await api(`Items/${film.Id}`)).MediaSources[0].MediaStreams;
  const english = streams.find((x) => x.Type === 'Audio' && x.Language === 'eng'), frenchSubs = streams.find((x) => x.Type === 'Subtitle' && x.Language === 'fra');
  await api(`UserPlayedItems/${film.Id}`, 'DELETE');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await wait(2000);
  const line = await page.locator('.tracks-line').innerText().catch(() => '');
  check('fiche : pistes choisies par les langues des réglages', /Audio : Français/.test(line) && /Sous-titres : aucun/.test(line), line.replace(/\s+/g, ' '));
  await page.locator('.tracks-line').click(); await wait(500);
  await shot('11-tracks');
  await page.locator('.sheet-item', { hasText: 'Anglais' }).first().click(); await wait(300);
  await page.locator('.tracks-line').click(); await wait(500);
  await page.locator('.sheet-item', { hasText: 'Français' }).last().click(); await wait(300);
  const chosenLine = await page.locator('.tracks-line').innerText();
  check('fiche : audio et sous-titres changés', /Audio : Anglais/.test(chosenLine) && /Sous-titres : Français/.test(chosenLine), chosenLine.replace(/\s+/g, ' '));
  const asked = page.waitForRequest((r) => /PlaybackInfo/.test(r.url()) && r.method() === 'POST', { timeout: 15000 });
  await page.locator('.detail-actions .btn.primary').click();
  const body = (await asked).postDataJSON();
  check('lecture : pistes de la fiche demandées à Jellyfin', body.AudioStreamIndex === english.Index && body.SubtitleStreamIndex === frenchSubs.Index,
    `audio ${body.AudioStreamIndex}, sous-titres ${body.SubtitleStreamIndex}`);
  await wait(5000);
  const shown = await page.evaluate(() => {
    const v = document.querySelector('video'), t = [...v.textTracks].find((x) => x.mode === 'showing');
    return { t: v.currentTime, paused: v.paused, label: t?.label ?? '', cue: t?.activeCues?.[0]?.text ?? '', converted: !/[?&]Static=true/i.test(v.currentSrc) };
  });
  // Without audioTracks, Chromium plays a file's first audio track only: Jellyfin sends the English one in a new stream.
  check('lecture : piste anglaise et sous-titres français', !shown.paused && shown.t > 1 && shown.converted && shown.label === 'Français' && shown.cue === 'Lueur en VF.',
    `${shown.t.toFixed(1)} s, ${shown.converted ? 'flux converti' : 'fichier tel quel'}, ${shown.label} « ${shown.cue} »`);
  await shot('12-tracks-playing');
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500);

  if (process.env.MIRA_STOP && process.env.MIRA_START) {
    // The PC is off or still starting: the page the phone keeps opens, says so, and comes back by itself.
    execSync(process.env.MIRA_STOP, { stdio: 'ignore' });
    try {
      await page.goto(base); await wait(1500);
      const away = await page.locator('.netbar').isVisible().catch(() => false);
      check('serveur éteint : Mira s’ouvre et attend', away && await page.locator('.tabbar').count() === 1, await page.locator('.netbar').innerText().catch(() => 'pas de bandeau'));
      await shot('13-server-away');
    } catch (error) {
      check('serveur éteint : Mira s’ouvre et attend', false, error.message.split('\n')[0]);
    } finally { execSync(process.env.MIRA_START, { stdio: 'ignore' }); }
    const back = await page.locator('.netbar').waitFor({ state: 'hidden', timeout: 180_000 }).then(() => true, () => false);
    await wait(3000);
    check('serveur revenu : Mira reprend toute seule', back && await page.locator('.section').count() > 0, `${await page.locator('.section h2').allInnerTexts()}`);
  }

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

// Apple's player, simulated: full screen as WebKit reports it (webkitPresentationMode and its event).
const apple = await browser.newContext({ ...devices['iPhone 15 Pro'] });
await apple.addInitScript(() => {
  const proto = HTMLVideoElement.prototype, modes = new WeakMap();
  const set = (video, mode) => {
    if ((modes.get(video) ?? 'inline') === mode) return;
    modes.set(video, mode);
    setTimeout(() => video.dispatchEvent(new Event('webkitpresentationmodechanged')), 60);
  };
  Object.defineProperty(proto, 'webkitPresentationMode', { configurable: true, get() { return modes.get(this) ?? 'inline'; } });
  Object.defineProperty(proto, 'webkitDisplayingFullscreen', { configurable: true, get() { return modes.get(this) === 'fullscreen'; } });
  proto.webkitEnterFullscreen = function () { if (!this.getAttribute('src')) throw new DOMException('no media', 'InvalidStateError'); set(this, 'fullscreen'); };
  proto.webkitExitFullscreen = function () { set(this, 'inline'); };
  proto.webkitSetPresentationMode = function (mode) { set(this, mode); };
  proto.webkitSupportsPresentationMode = () => true;
});
const ap = await apple.newPage();
const appleErrors = [];
ap.on('pageerror', (e) => appleErrors.push(e.message));
const state = () => ap.evaluate(() => { const v = document.querySelector('video'); return v ? { mode: v.webkitPresentationMode, paused: v.paused, t: v.currentTime, hash: location.hash } : { hash: location.hash }; });
try {
  await ap.goto(`${base}#/connexion`); await ap.waitForTimeout(1500);
  if (await ap.locator('.users').count()) await ap.getByRole('button', { name: 'Autre compte' }).click();
  await ap.fill('#login-name', user); await ap.fill('#login-password', password);
  await ap.getByRole('button', { name: 'Se connecter' }).click();
  await ap.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  const token = await ap.evaluate(() => JSON.parse(localStorage.getItem('mira.session')));
  const call = (path, method = 'GET') => ap.evaluate(async ([p, m, t]) => {
    const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { method: m, headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${t}"` } });
    const x = await r.text(); return x ? JSON.parse(x) : null;
  }, [path, method, token.token]);
  const series = (await call(`Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent(seriesName)}`)).Items[0];
  const episodes = (await call(`Shows/${series.Id}/Episodes?userId=${token.userId}`)).Items;
  for (const e of episodes) await call(`UserPlayedItems/${e.Id}`, 'DELETE');
  await ap.evaluate((id) => { location.hash = `#/titre/${id}`; }, series.Id); await ap.waitForTimeout(2500);
  await ap.locator('.detail-actions .btn.primary').click(); await ap.waitForTimeout(3500);
  let now = await state();
  check('lecteur d’Apple (simulé) : plein écran dès Lecture', now.mode === 'fullscreen' && !now.paused && now.t > 0.5, `${now.mode}, ${now.t?.toFixed(1)} s`);
  const moved = await ap.waitForFunction((id) => location.hash.includes(id), episodes[1].Id, { timeout: 30000 }).then(() => true, () => false);
  await ap.waitForTimeout(3000);
  now = await state();
  check('lecteur d’Apple (simulé) : épisode suivant sans quitter le plein écran', moved && now.mode === 'fullscreen' && !now.paused && now.t > 0.5, `${now.mode}, ${now.t?.toFixed(1)} s`);
  await ap.evaluate(() => document.querySelector('video').webkitSetPresentationMode('picture-in-picture')); await ap.waitForTimeout(500);
  const pip = await ap.locator('.n-status').innerText();
  await ap.screenshot({ path: `${out}/14-apple-pip.png` });
  await ap.evaluate(() => document.querySelector('video').webkitSetPresentationMode('fullscreen')); await ap.waitForTimeout(500);
  now = await state();
  check('lecteur d’Apple (simulé) : image dans l’image, puis retour', /image dans l’image/.test(pip) && now.mode === 'fullscreen' && !now.paused, pip.replace(/\s+/g, ' '));
  await ap.evaluate(() => document.querySelector('video').webkitExitFullscreen());
  const closed = await ap.waitForFunction((id) => location.hash.startsWith(`#/titre/${id}`), series.Id, { timeout: 8000 }).then(() => true, () => false);
  await ap.waitForTimeout(1500);
  const kept = await call(`Items/${episodes[1].Id}?userId=${token.userId}`);
  check('lecteur d’Apple (simulé) : fermé, retour à la fiche et position gardée', closed && !(await ap.locator('video').count()) && kept.UserData.PlaybackPositionTicks > 5_000_000,
    `${(await state()).hash.split('?')[0]}, ${(kept.UserData.PlaybackPositionTicks / 1e7).toFixed(1)} s`);
  await ap.screenshot({ path: `${out}/15-apple-closed.png` });
} catch (error) {
  check('lecteur d’Apple (simulé) : déroulé', false, error.message.split('\n')[0]);
  await ap.screenshot({ path: `${out}/error-apple.png` }).catch(() => {});
}
check('lecteur d’Apple (simulé) : aucune erreur JavaScript', appleErrors.length === 0, appleErrors.slice(0, 3).join(' | '));

// The Home Screen app on iPhone, simulated (navigator.standalone): the swipe from the left edge to go back, tabs that
// do not pile up history, sheets pulled down to close. Touches go through Chromium's DevTools protocol.
const home = await browser.newContext({ ...devices['iPhone 15 Pro'] });
await home.addInitScript(() => Object.defineProperty(Navigator.prototype, 'standalone', { configurable: true, get: () => true }));
const hp = await home.newPage();
const homeErrors = [];
hp.on('pageerror', (e) => homeErrors.push(e.message));
const cdp = await home.newCDPSession(hp);
const drag = async (x0, y0, x1, y1, steps = 14) => {
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: x0, y: y0 }] });
  for (let i = 1; i <= steps; i++) {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: x0 + ((x1 - x0) * i) / steps, y: y0 + ((y1 - y0) * i) / steps }] });
    await hp.waitForTimeout(16);
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
};
const where = () => hp.evaluate(() => ({ hash: location.hash || '#/', depth: history.state?.depth ?? null }));
try {
  await hp.goto(`${base}#/connexion`); await hp.waitForTimeout(1500);
  if (await hp.locator('.users').count()) await hp.getByRole('button', { name: 'Autre compte' }).click();
  await hp.fill('#login-name', user); await hp.fill('#login-password', password);
  await hp.getByRole('button', { name: 'Se connecter' }).click();
  await hp.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await hp.waitForTimeout(2500);
  const openTitle = async () => { await hp.locator('a.card[href^="#/titre/"]').first().click(); await hp.waitForTimeout(1500); return where(); };
  const opened = await openTitle();
  await drag(4, 420, 330, 430); await hp.waitForTimeout(1200);
  let at = await where();
  check('app (simulée) : balayage depuis le bord pour revenir', opened.hash.startsWith('#/titre/') && opened.depth === 1 && at.hash === '#/' && at.depth === 0
    && await hp.locator('.view.home').isVisible() && !(await hp.locator('.swipe-under').count()), `${opened.hash.split('?')[0]} (${opened.depth}) → ${at.hash} (${at.depth})`);
  await openTitle();
  await drag(4, 420, 70, 425); await hp.waitForTimeout(1000);
  at = await where();
  const settled = await hp.evaluate(() => { const v = document.querySelector('#view > .view'); return !v.style.transform && !v.classList.contains('swiping') && !document.querySelector('.swipe-under'); });
  check('app (simulée) : balayage trop court, la fiche reste', at.hash.startsWith('#/titre/') && settled, at.hash.split('?')[0]);
  await hp.locator('.tabbar a[data-tab="films"]').click(); await hp.waitForTimeout(1500);
  at = await where();
  check('app (simulée) : onglet touché depuis une fiche, sans empiler l’historique', at.hash === '#/films' && at.depth === 0, `${at.hash} (${at.depth})`);
  await hp.locator('.tabbar a[data-tab="films"]').click(); await hp.waitForTimeout(600);
  const film = await hp.evaluate(() => [...document.querySelectorAll('a.card')].find((a) => /Lueur/.test(a.textContent))?.getAttribute('href'));
  await hp.evaluate((href) => { location.hash = href; }, film); await hp.waitForTimeout(2000);
  await hp.locator('.tracks-line').click(); await hp.waitForTimeout(700);
  const box = await hp.locator('.sheet').boundingBox();
  await drag(box.x + box.width / 2, box.y + 14, box.x + box.width / 2, box.y + 14 + Math.max(260, box.height * 0.6)); await hp.waitForTimeout(800);
  check('app (simulée) : feuille fermée en la tirant vers le bas', await hp.locator('.sheet-layer').count() === 0);
  await hp.screenshot({ path: `${out}/16-app-title.png` });
} catch (error) {
  check('app (simulée) : déroulé', false, error.message.split('\n')[0]);
  await hp.screenshot({ path: `${out}/error-app.png` }).catch(() => {});
}
check('app (simulée) : aucune erreur JavaScript', homeErrors.length === 0, homeErrors.slice(0, 3).join(' | '));
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
await browser.close();
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
