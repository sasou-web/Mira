// Mira web against a real Jellyfin, in Chromium at an iPhone's size: the sign-in form, every screen, playback, the
// next episode, resume, the reports Jellyfin receives, the choice of tracks on a title page, sign-out. Screenshots for
// review; results.txt; exit 1 on failure.
//   node tools/web/check.mjs <Mira web url> <user> <password> <series name> <out folder>
// Chromium has no H.264: the titles checked here are in WebM; Safari's HLS path is checked by safari.mjs. What
// Jellyfin and Mira's plugin send Apple's player for an anime (its ASS subtitles) is checked with an iPhone's profile.
// Apple's full screen player is simulated here (Chromium has none), to check what Mira does around it: start, next
// episode in place, Picture in Picture, back to the title page once closed. safari.mjs checks it in Safari itself.
// So is the Home Screen app on iPhone (navigator.standalone): pull to refresh, swipe back from the edge, tabs, sheets pulled down.
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

  // An anime's ASS subtitles, inside its file: Jellyfin would draw them into the picture (it never converts ASS by
  // profile); Mira asks for them as WebVTT, beside the video. Their line runs from 5 s to 8 s.
  const anime = (await api('Items?recursive=true&includeItemTypes=Movie&searchTerm=Signes')).Items[0];
  const dialogue = (await api(`Items/${anime.Id}`)).MediaSources[0].MediaStreams.find((x) => x.Type === 'Subtitle' && x.Codec === 'ass' && !x.IsForced);
  await page.evaluate(([id, index]) => { location.hash = `#/lecture/${id}?debut=1&sous-titres=${index}`; }, [anime.Id, dialogue.Index]);
  const assCue = await page.waitForFunction(() => {
    const v = document.querySelector('video'), t = v && [...v.textTracks].find((x) => x.mode === 'showing');
    return t?.activeCues?.length ? { text: t.activeCues[0].text, t: v.currentTime, src: v.currentSrc, label: t.label } : null;
  }, null, { timeout: 30000, polling: 250 }).then((handle) => handle.jsonValue(), () => null);
  check('lecture : sous-titres ASS d’un animé, en texte à côté de la vidéo', assCue?.text === 'Cinq secondes' && assCue.t > 4.5 && assCue.t < 8.5 && !/SubtitleMethod=Encode/i.test(assCue.src),
    assCue ? `${assCue.label} « ${assCue.text} » à ${assCue.t.toFixed(1)} s${/SubtitleMethod=Encode/i.test(assCue.src) ? ', incrustés dans l’image' : ''}` : 'aucune ligne affichée');
  // The forced signs too: outside Safari, a « forced » <track> would be metadata, never drawn.
  const kinds = await page.evaluate(() => [...document.querySelectorAll('video track')].map((t) => t.kind));
  check('lecture : sous-titres forcés affichables hors de Safari', kinds.length >= 2 && !kinds.includes('metadata'), kinds.join(', '));
  await shot('13-ass');
  // Mira's own full screen (Android, a computer): its sheets show in it, not under it.
  await page.locator('.player .controls button[aria-label="Plein écran"]').click({ force: true }); await wait(800);
  await page.locator('.player .controls button[aria-label="Audio et sous-titres"]').click({ force: true }); await wait(700);
  const inFull = await page.evaluate(() => {
    const full = document.fullscreenElement, layer = document.querySelector('.sheet-layer');
    return { full: !!full, inside: !!(full && layer && full.contains(layer)), visible: !!layer && layer.getBoundingClientRect().height > 0 };
  });
  check('plein écran de Mira : la feuille des pistes s’y affiche', inFull.full && inFull.inside && inFull.visible,
    `${inFull.full ? 'plein écran' : 'pas de plein écran'}, feuille ${inFull.inside ? 'dedans' : 'en dehors'}`);
  await page.keyboard.press('Escape'); await wait(300);
  await page.evaluate(() => document.exitFullscreen?.().catch(() => {})); await wait(500);
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
  // Closed by the person while the next episode loads: Mira closes, and does not open Apple's player again.
  await ap.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, episodes[0].Id);
  const switched = await ap.waitForFunction((id) => location.hash.includes(id), episodes[1].Id, { timeout: 30000, polling: 50 }).then(() => true, () => false);
  await ap.evaluate(() => document.querySelector('video')?.webkitExitFullscreen());
  await ap.waitForTimeout(3000);
  now = await state();
  check('lecteur d’Apple (simulé) : fermé pendant le chargement de l’épisode suivant, il ne revient pas', switched && now.hash.startsWith(`#/titre/${series.Id}`) && now.mode !== 'fullscreen',
    `${switched ? 'É2 en chargement' : 'É2 jamais atteint'}, puis ${now.hash.split('?')[0]}${now.mode ? `, ${now.mode}` : ''}`);
} catch (error) {
  check('lecteur d’Apple (simulé) : déroulé', false, error.message.split('\n')[0]);
  await ap.screenshot({ path: `${out}/error-apple.png` }).catch(() => {});
}
check('lecteur d’Apple (simulé) : aucune erreur JavaScript', appleErrors.length === 0, appleErrors.slice(0, 3).join(' | '));

// An anime for Apple's player, asked for as an iPhone does (its codecs are claimed here: Chromium has neither H.264
// nor HLS). Jellyfin's answers and Mira's plugin are real: the HLS stream lists every text subtitle, ASS included,
// with Mira's names, and its WebVTT comes without the MPEG-TS time map (10 s late on fMP4), at the file's own times.
const iphone = await browser.newContext({ ...devices['iPhone 15 Pro'] });
await iphone.addInitScript(() => {
  HTMLMediaElement.prototype.canPlayType = (type) => (/mpegurl|avc1|mp4a|hvc1|hev1|audio\/mpeg/i.test(type) ? 'probably' : '');
});
const ip = await iphone.newPage();
try {
  await ip.goto(`${base}#/connexion`); await ip.waitForTimeout(1500);
  const seen = await ip.evaluate(async ([name, secret]) => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    const { deviceProfile } = await import(`${root}player/profile.js`);
    const { appleStream } = await import(`${root}player/tracks.js`);
    await api.signIn(name, secret);
    const jellyfin = location.pathname.replace(/\/Mira\/?.*$/i, '');
    const film = (await api.browse({ types: 'Movie', search: 'Signes' })).Items[0];
    const source = (await api.item(film.Id)).MediaSources[0];
    const subtitles = source.MediaStreams.filter((x) => x.Type === 'Subtitle');
    const dialogue = subtitles.find((x) => x.Codec === 'ass' && !x.IsForced), english = subtitles.find((x) => x.Language === 'eng');
    const runs = [];
    for (const choice of [-1, dialogue.Index, english.Index]) {
      const info = await api.playbackInfo(film.Id, {
        DeviceProfile: deviceProfile(20_000_000, { hlsSubtitles: true }), MaxStreamingBitrate: 20_000_000, MediaSourceId: source.Id,
        SubtitleStreamIndex: choice, EnableDirectPlay: true, EnableDirectStream: true, EnableTranscoding: true, AllowVideoStreamCopy: true, AllowAudioStreamCopy: true,
      });
      const played = info.MediaSources[0];
      const { address, inband } = appleStream(played.TranscodingUrl ?? '', played, choice);
      const master = await (await fetch(jellyfin + address)).text();
      const lines = master.split('\n').filter((l) => l.includes('TYPE=SUBTITLES'));
      const attribute = (line, key) => line.match(new RegExp(`${key}="([^"]*)"`))?.[1] ?? line.match(new RegExp(`${key}=([A-Z]+)`))?.[1] ?? '';
      runs.push({
        choice, inband, burned: played.MediaStreams.some((x) => x.Type === 'Subtitle' && x.IsTextSubtitleStream && x.DeliveryMethod === 'Encode'),
        hls: (address.match(/SubtitleMethod=Hls/g) ?? []).length === 1 && !/SubtitleMethod=Encode/.test(address) && address.startsWith('/Mira/hls/'),
        names: lines.map((l) => attribute(l, 'NAME')), languages: lines.map((l) => attribute(l, 'LANGUAGE')),
        defaults: lines.map((l) => attribute(l, 'DEFAULT')).join(''), forced: lines.map((l) => attribute(l, 'FORCED')).join(''),
        mira: lines.every((l) => attribute(l, 'URI').startsWith(`${jellyfin}/Mira/hls/`)), uri: lines.length ? attribute(lines[0], 'URI') : '',
      });
    }
    // The dialogue's subtitle playlist (the first in the stream): its two 30 s WebVTT segments.
    const playlist = await (await fetch(runs[1].uri)).text();
    const segments = playlist.split('\n').filter((l) => l && !l.startsWith('#'));
    const texts = await Promise.all(segments.slice(0, 2).map(async (u) => (await fetch(u)).text()));
    return { runs, segments, texts };
  }, [user, password]);
  const [none, ass, srt] = seen.runs;
  check('animé (profil d’iPhone) : sous-titres ASS et SRT jamais incrustés', seen.runs.every((r) => !r.burned && r.inband && r.hls),
    seen.runs.map((r) => `${r.choice} : ${r.burned ? 'incrustés' : r.hls ? 'dans le flux' : 'hors du flux'}`).join(', '));
  check('animé (profil d’iPhone) : quatre sous-titres nommés en français dans le flux d’Apple',
    ass.names.join('|') === 'Français (ASS)|Français (ASS · forcés)|Anglais|Piste 4' && ass.languages.join('|') === 'fra|fra|eng|und' && ass.forced === 'NOYESNONO' && ass.mira,
    `${ass.names.join(', ')} (${ass.languages.join(', ')})`);
  check('animé (profil d’iPhone) : seul le choix de la fiche est en tête, aucun avec « Aucun »',
    none.defaults === 'NONONONO' && ass.defaults === 'YESNONONO' && srt.defaults === 'NONOYESNO', `aucun ${none.defaults}, ASS ${ass.defaults}, SRT ${srt.defaults}`);
  check('animé (profil d’iPhone) : WebVTT à l’heure, sans la carte MPEG-TS',
    seen.segments.length === 2 && seen.segments.every((u) => /AddVttTimeMap=false/.test(u)) && !seen.texts.some((t) => /X-TIMESTAMP-MAP/.test(t))
    && /00:00:05\.000 --> 00:00:08\.000\nCinq secondes/.test(seen.texts[0]) && /00:00:35\.000 --> 00:00:38\.000\nTrente-cinq/.test(seen.texts[1] ?? ''),
    `${seen.segments.length} segment(s), ${seen.texts.map((t) => JSON.stringify(t.slice(0, 60))).join(' ; ')}`);
} catch (error) {
  check('animé (profil d’iPhone) : déroulé', false, error.message.split('\n')[0]);
}
await iphone.close();

// The Home Screen app on iPhone, simulated (navigator.standalone): pull to refresh, Back and iOS's own swipe from the
// edge, tabs that do not pile up history, sheets pulled down to close. Touches go through Chromium's DevTools protocol.
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
  // Pulled down from the top, Home asks Jellyfin again.
  const asked = hp.waitForRequest((r) => /UserItems\/Resume/.test(r.url()), { timeout: 6000 }).then(() => true, () => false);
  await drag(200, 260, 205, 520, 16);
  check('app (simulée) : tirer vers le bas pour actualiser', await asked);
  await hp.waitForTimeout(1500);
  const openTitle = async () => { await hp.locator('a.card[href^="#/titre/"]').first().click(); await hp.waitForTimeout(1500); return where(); };
  // Mira's Back draws iOS's move; after the system's own swipe from the edge (iOS cancels the touch), it draws none.
  const watchMoves = () => hp.evaluate(() => {
    window.miraMoves = [];
    new MutationObserver(() => { const nav = document.documentElement.dataset.nav; if (nav) window.miraMoves.push(nav); })
      .observe(document.documentElement, { attributes: true, attributeFilter: ['data-nav'] });
  });
  const opened = await openTitle();
  await watchMoves();
  await hp.locator('.floating-back').click(); await hp.waitForTimeout(1200);
  let at = await where();
  let moves = await hp.evaluate(() => window.miraMoves);
  check('app (simulée) : Retour, l’écran repart vers la droite', opened.hash.startsWith('#/titre/') && opened.depth === 1 && at.hash === '#/' && at.depth === 0
    && moves.includes('pop') && await hp.locator('.view.home').isVisible(), `${opened.hash.split('?')[0]} (${opened.depth}) → ${at.hash} (${at.depth}), ${moves.join(',') || 'aucun mouvement'}`);
  await openTitle();
  await watchMoves();
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: 4, y: 420 }] });
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
  await hp.evaluate(() => history.back()); await hp.waitForTimeout(1200);
  at = await where();
  moves = await hp.evaluate(() => window.miraMoves);
  check('app (simulée) : balayage d’iOS depuis le bord, pas de second mouvement', at.hash === '#/' && at.depth === 0 && !moves.length
    && await hp.locator('.view.home').isVisible(), `${at.hash} (${at.depth}), ${moves.join(',') || 'aucun mouvement'}`);
  await openTitle();
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
  // The small title bar, once there, takes a touch itself (back to the top) rather than letting it through to what it covers.
  const size = hp.viewportSize();
  await hp.setViewportSize({ width: size.width, height: 360 }); await hp.waitForTimeout(300);
  await hp.evaluate(() => scrollTo(0, document.documentElement.scrollHeight)); await hp.waitForTimeout(400);
  const bar = await hp.evaluate(() => { const b = document.querySelector('.topbar'), r = b.getBoundingClientRect(); return { on: b.classList.contains('on'), x: r.left + r.width / 2, y: r.bottom - 14 }; });
  if (bar.on) await hp.mouse.click(bar.x, bar.y);
  await hp.waitForTimeout(900);
  const afterBar = await hp.evaluate(() => ({ hash: location.hash, y: scrollY }));
  await hp.setViewportSize(size);
  check('app (simulée) : la barre du titre ramène en haut, sans ouvrir ce qu’elle couvre', bar.on && afterBar.hash === film && afterBar.y < 5,
    `${bar.on ? 'barre affichée' : 'barre absente'}, ${afterBar.hash}, ${Math.round(afterBar.y)} px`);
  await hp.screenshot({ path: `${out}/16-app-title.png` });
} catch (error) {
  check('app (simulée) : déroulé', false, error.message.split('\n')[0]);
  await hp.screenshot({ path: `${out}/error-app.png` }).catch(() => {});
}
check('app (simulée) : aucune erreur JavaScript', homeErrors.length === 0, homeErrors.slice(0, 3).join(' | '));

// Tabs as an app's: a tab's screen shows whole (no placeholders, nothing moving) and the one left dissolves into it;
// Recherche takes the keyboard only when touched again; Réglages and Home change in place.
const tabs = await browser.newContext({ ...devices['iPhone 15 Pro'] });
await tabs.addInitScript(() => Object.defineProperty(Navigator.prototype, 'standalone', { configurable: true, get: () => true }));
const tp = await tabs.newPage();
const tabErrors = [];
let restoreFavorite = null;   // the favourite changed for a check, put back whatever happens
tp.on('pageerror', (e) => tabErrors.push(e.message));
try {
  await tp.goto(`${base}#/connexion`); await tp.waitForTimeout(1500);
  if (await tp.locator('.users').count()) await tp.getByRole('button', { name: 'Autre compte' }).click();
  await tp.fill('#login-name', user); await tp.fill('#login-password', password);
  await tp.getByRole('button', { name: 'Se connecter' }).click();
  await tp.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await tp.waitForTimeout(2500);
  // Every frame for a second after the tap: the dissolving layer, a placeholder seen in the screen, the grid's top,
  // and when the tab lit up and the new screen came.
  const record = (tab) => tp.evaluate(async (name) => {
    const frames = [], t0 = performance.now();
    document.querySelector(`.tabbar a[data-tab="${name}"]`).click();
    while (performance.now() - t0 < 1000) {
      await new Promise((r) => requestAnimationFrame(r));
      const grid = document.querySelector('#view .library-grid');
      frames.push({
        t: Math.round(performance.now() - t0), leaving: !!document.querySelector('.view-leaving'),
        skeleton: [...document.querySelectorAll('#view .skeleton')].some((x) => x.getClientRects().length),
        lit: document.querySelector('.tabbar a[aria-current="page"]')?.dataset.tab ?? '', views: document.querySelectorAll('#view > .view').length,
        library: !!document.querySelector('#view > .view.library'), top: grid ? Math.round(grid.getBoundingClientRect().top) : null,
      });
    }
    return frames;
  }, tab);
  // The first page a little slow, as from a PC at home: the tab must light up before its screen comes.
  const firstPage = (url) => /\/Items$/i.test(url.pathname) && /IncludeItemTypes=Movie/i.test(url.search);
  await tp.route(firstPage, async (r) => { await new Promise((s) => setTimeout(s, 150)); await r.continue(); });
  let frames = await record('films');
  await tp.unroute(firstPage);
  const shown = frames.find((f) => f.library);
  const tops = [...new Set(frames.filter((f) => f.top != null).map((f) => f.top))];
  check('onglets : Films s’affiche entier, sans emplacements vides ni décalage', !!shown && !frames.some((f) => f.skeleton) && tops.length === 1,
    `${shown ? `à ${shown.t} ms` : 'jamais affiché'}, ${frames.some((f) => f.skeleton) ? 'emplacements vides vus' : 'aucun emplacement vide'}, haut de la grille ${tops.join(' → ') || '?'}`);
  const lit = frames.find((f) => f.lit === 'films')?.t;
  check('onglets : l’écran quitté se fond dans le suivant, l’onglet s’allume au toucher',
    frames.some((f) => f.leaving) && !frames.at(-1).leaving && frames.every((f) => f.views <= 1) && lit != null && lit <= 80 && !!shown && lit < shown.t,
    `fondu ${frames.some((f) => f.leaving) ? 'vu' : 'absent'}${frames.at(-1).leaving ? ', resté' : ''}, onglet allumé à ${lit ?? '?'} ms, écran à ${shown?.t ?? '?'} ms`);
  frames = await record('home');
  check('onglets : retour à l’Accueil gardé, en fondu aussi', frames.some((f) => f.leaving) && !frames.at(-1).leaving && await tp.locator('#view > .view.home').count() === 1,
    `fondu ${frames.some((f) => f.leaving) ? 'vu' : 'absent'}`);

  // Recherche: no keyboard on arrival (iOS opens none for a focus given later, and the screen may stay shorter);
  // the tab touched again, at the top, gives the field the keyboard.
  await tp.locator('.tabbar a[data-tab="search"]').click(); await tp.waitForTimeout(700);
  const first = await tp.evaluate(() => document.activeElement?.matches('.search input') ?? false);
  await tp.locator('.tabbar a[data-tab="search"]').click(); await tp.waitForTimeout(300);
  const second = await tp.evaluate(() => document.activeElement?.matches('.search input') ?? false);
  check('recherche : le champ ne prend le clavier qu’au second toucher de l’onglet', !first && second, `${first ? 'clavier à l’arrivée' : 'pas de clavier à l’arrivée'}, ${second ? 'clavier au second toucher' : 'pas de clavier au second toucher'}`);
  await tp.evaluate(() => document.activeElement?.blur());

  // Réglages, seen again: the same screen, not drawn anew.
  await tp.locator('.tabbar a[data-tab="settings"]').click(); await tp.waitForTimeout(1200);
  await tp.evaluate(() => { document.querySelector('#view .settings .group').dataset.mark = 'kept'; });
  await tp.locator('.tabbar a[data-tab="home"]').click(); await tp.waitForTimeout(600);
  await tp.locator('.tabbar a[data-tab="settings"]').click(); await tp.waitForTimeout(800);
  check('réglages : revus sans être redessinés', await tp.evaluate(() => document.querySelector('#view .settings .group')?.dataset.mark === 'kept'));

  // Home, refreshed after a change on the server: the cards that did not change stay as they were (pictures and all).
  await tp.locator('.tabbar a[data-tab="home"]').click(); await tp.waitForTimeout(800);
  const marked = await tp.evaluate(() => {
    const cards = [...document.querySelectorAll('#view .section .row > a.card')];
    cards.forEach((c, i) => { c.dataset.mark = String(i); });
    return cards.length;
  });
  const latest = await tp.evaluate(async () => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const root = location.pathname.replace(/\/Mira\/?.*$/i, '');
    const r = await fetch(`${root}/Items?recursive=true&includeItemTypes=Movie&sortBy=DateCreated&sortOrder=Descending&limit=1&userId=${s.userId}`,
      { headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
    return (await r.json()).Items[0];
  });
  const favorite = (method) => tp.evaluate(async ([id, m]) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/UserFavoriteItems/${id}?userId=${s.userId}`,
      { method: m, headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
  }, [latest.Id, method]);
  const wasFavorite = !!latest.UserData?.IsFavorite;
  await favorite(wasFavorite ? 'DELETE' : 'POST');
  restoreFavorite = () => favorite(wasFavorite ? 'POST' : 'DELETE');
  const asked = tp.waitForRequest((r) => /UserItems\/Resume/.test(r.url()), { timeout: 6000 }).then(() => true, () => false);
  await tp.evaluate(() => window.dispatchEvent(new Event('online')));
  const refreshed = await asked;
  await tp.waitForTimeout(1500);
  const after = await tp.evaluate(() => {
    const cards = [...document.querySelectorAll('#view .section .row > a.card')];
    return { total: cards.length, kept: cards.filter((c) => c.dataset.mark != null).length, made: cards.filter((c) => c.dataset.mark == null).length };
  });
  await restoreFavorite(); restoreFavorite = null;
  check('accueil : actualisé sur place, seules les cartes changées sont refaites', refreshed && marked > 0 && after.kept > 0 && after.made >= 1 && after.made < after.total,
    `${marked} cartes, puis ${after.kept} gardées et ${after.made} refaites${refreshed ? '' : ', pas actualisé'}`);
} catch (error) {
  check('onglets : déroulé', false, error.message.split('\n')[0]);
  await tp.screenshot({ path: `${out}/error-tabs.png` }).catch(() => {});
} finally {
  await restoreFavorite?.().catch(() => {});
}
check('onglets : aucune erreur JavaScript', tabErrors.length === 0, tabErrors.slice(0, 3).join(' | '));
await tabs.close();

// A stream the player refuses while Jellyfin answers: asked again with its video converted, then with everything
// converted, then said once nothing is left (here the conversions are cut off by the test on purpose). An H.264 film:
// Chromium converts it, as an iPhone converts what it cannot play (Jellyfin 12.1 fails to convert a VP9 file of an
// unknown level into VP9, « level -99 »: not a case for a phone).
const fall = await browser.newContext({ ...devices['iPhone 15 Pro'] });
const fp = await fall.newPage();
const fallErrors = [], asks = [];
fp.on('pageerror', (e) => fallErrors.push(e.message));
fp.on('request', (r) => {
  if (!/\/PlaybackInfo/i.test(r.url()) || r.method() !== 'POST') return;
  try { const b = JSON.parse(r.postData() ?? '{}'); asks.push([b.EnableDirectPlay, b.AllowVideoStreamCopy, b.AllowAudioStreamCopy].map((x) => (x ? 1 : 0)).join('')); } catch { asks.push('?'); }
});
let cut = 0, cutUntil = 0;
await fp.route((url) => /\/videos\/[^/]+\/stream\.webm/i.test(url.pathname), (route) => (cut++ < cutUntil ? route.abort('failed') : route.continue()));
try {
  await fp.goto(`${base}#/connexion`); await fp.waitForTimeout(1500);
  const film = await fp.evaluate(async ([name, secret]) => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    await api.signIn(name, secret);
    return (await api.browse({ types: 'Movie', search: 'Aube' })).Items[0];
  }, [user, password]);
  const play = async (cuts) => {
    cut = 0; cutUntil = cuts; asks.length = 0;
    await fp.evaluate(() => { location.hash = '#/'; }); await fp.waitForTimeout(1500);
    await fp.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.Id);
    // A software VP9 conversion takes a while to start on a small machine.
    for (let i = 0; i < 90; i++) {
      await fp.waitForTimeout(500);
      const state = await fp.evaluate(() => ({ t: document.querySelector('video')?.currentTime ?? 0, failed: !!document.querySelector('.p-message:not([hidden]) .detail') }));
      if (state.t > 1.5 || state.failed) break;
    }
    return fp.evaluate(() => ({ t: document.querySelector('video')?.currentTime ?? 0, message: document.querySelector('.p-message:not([hidden])')?.textContent ?? '',
      detail: document.querySelector('.p-message:not([hidden]) .detail')?.textContent ?? '' }));
  };
  let state = await play(2);
  check('lecture refusée : demandée à nouveau, vidéo puis tout converti, et elle joue', asks.join(',') === '111,001,000' && state.t > 1.5 && !state.message,
    `demandes ${asks.join(', ')} (lecture directe, vidéo copiée, audio copié), ${state.t.toFixed(1)} s`);
  state = await play(99);
  check('lecture refusée partout : le dit, avec l’erreur du navigateur', asks.join(',') === '111,001,000' && /même entièrement converti/.test(state.message) && /^Erreur/.test(state.detail),
    `demandes ${asks.join(', ')}, « ${state.detail} »`);
  await fp.screenshot({ path: `${out}/17-refused.png` });
} catch (error) {
  check('lecture refusée : déroulé', false, error.message.split('\n')[0]);
}
check('lecture refusée : aucune erreur JavaScript', fallErrors.length === 0, fallErrors.slice(0, 3).join(' | '));
await fall.close();
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
await browser.close();
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
