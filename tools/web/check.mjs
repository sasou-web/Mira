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
// Security: sign-out and a session refused leave nothing of the account; another server answering at the address
// (simulated) never gets the token; the sign-in screen warns over plain HTTP from the Internet; the page's security
// policy refuses inline styles but its own, and no page of the whole run breaks it.
// Robustness: the site's storage blocked, a Wi-Fi's page answering for Jellyfin, a library of 3,000 films.
import './unit/fr.mjs';
import fs from 'node:fs';
import { execSync } from 'node:child_process';
import { phoneNotes } from '../../src/Mira.Web/js/whats-new.js';
const { chromium, devices } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const [base = 'http://127.0.0.1:8096/Mira/', user = 'mira', password = 'mira-check', seriesName = 'Courte Web', out = 'shots/e2e'] = process.argv.slice(2);
fs.mkdirSync(out, { recursive: true });
const results = [];
const check = (name, ok, detail = '') => { results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' — ' + detail : ''}`); console.log(results.at(-1)); };
// The account's choices kept on Jellyfin by an earlier run (« Tes choix gardés sur ton Jellyfin ») would follow it into
// every phone below: each starts from Mira's defaults.
await (async () => {
  const server = base.replace(/\/Mira\/?.*$/i, ''), auth = (token) => `MediaBrowser Client="check", Device="check", DeviceId="check-start", Version="1"${token ? `, Token="${token}"` : ''}`;
  const signed = await (await fetch(`${server}/Users/AuthenticateByName`, { method: 'POST', headers: { Authorization: auth(), 'Content-Type': 'application/json' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
  const headers = { Authorization: auth(signed.AccessToken), 'Content-Type': 'application/json' };
  const address = `${server}/DisplayPreferences/mira?userId=${signed.User.Id}&client=mira`;
  const prefs = await (await fetch(address, { headers })).json();
  if (prefs.CustomPrefs?.['mira.choices']) { delete prefs.CustomPrefs['mira.choices']; await fetch(address, { method: 'POST', headers, body: JSON.stringify(prefs) }); }
  await fetch(`${server}/Sessions/Logout`, { method: 'POST', headers });
})().catch(() => {});
// mira-check.example: Mira at an Internet name, served by the same Jellyfin (the warning on the sign-in screen);
// mira-check.tail0000.ts.net: at a Tailscale name, as the QR code gives it (« Automatique » measures the connection).
const internetName = 'mira-check.example', tailscaleName = 'mira-check.tail0000.ts.net';
const browser = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required',
  `--host-resolver-rules=MAP ${internetName} ${new URL(base).hostname}, MAP ${tailscaleName} ${new URL(base).hostname}`] });
// What the page's security policy refuses, in every page of every context below: nothing is expected.
const violations = [];
const watchPolicy = async (context, name, seen = violations) => {
  await context.exposeBinding('miraPolicyViolation', (_, text) => seen.push(`${name} : ${text}`));
  await context.addInitScript(() => document.addEventListener('securitypolicyviolation', (e) => {
    window.miraPolicyViolation?.(`${e.effectiveDirective} ${e.blockedURI || 'inline'}${e.sourceFile ? ` (${e.sourceFile.split('/').pop()}:${e.lineNumber})` : ''}`);
  }, true));
};
// A phone set in French: the checks below read Mira's French (English, Mira's default, is checked at the end).
const newContext = async (name, locale = 'fr-FR') => { const c = await browser.newContext({ ...devices['iPhone 15 Pro'], locale }); await watchPolicy(c, name); return c; };
const signIn = async (p, url = `${base}#/connexion`) => {
  await p.goto(url); await p.waitForTimeout(1500);
  if (await p.locator('.users').count()) await p.getByRole('button', { name: 'Autre compte' }).click();
  await p.fill('#login-name', user); await p.fill('#login-password', password);
  await p.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await p.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
};
const context = await newContext('parcours');
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
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await page.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await wait(3000);
  const version = await page.evaluate(() => document.querySelector('meta[name="mira-version"]').content);
  const seenVersion = () => page.evaluate(() => JSON.parse(localStorage.getItem('mira.settings') ?? '{}').seenVersion ?? null);
  check('connexion : « Nouveau dans Mira » attend la version suivante', (await seenVersion()) === version && !(await page.locator('.news').count()), `${version}, vue ${await seenVersion()}`);
  check('accueil : rangées affichées', await page.locator('.section').count() > 0, `${await page.locator('.section h2').allInnerTexts()}`);
  await shot('02-home');

  await page.locator('.tabbar a[data-tab="films"]').click(); await wait(2000);
  const count = await page.locator('.library-count').innerText();
  check('films : grille', /film/.test(count), count);
  await shot('03-films');
  // « Lettre », with the sort by title only: the films under L (tools/web/media.sh: Aube, Chapitres, Lueur, Signes).
  const sortBy = async (label) => {
    await page.locator('#view .library .chip', { hasText: 'Trier' }).click();
    await page.locator('.sheet-item', { hasText: label }).click(); await wait(1500);
  };
  const letterChip = () => page.locator('#view .library .chip', { hasText: 'Lettre' });
  const chipBefore = await letterChip().count();
  await sortBy('Titre');
  await letterChip().click(); await wait(500);
  await shot('03-films-letters');
  await page.locator('.letters .letter', { hasText: /^L$/ }).click(); await wait(500);
  // The page's own cards, not the placeholders it shows while Jellyfin answers.
  const titlesShown = () => page.locator('#view .library .grid .card:not([aria-hidden="true"]) .card-title');
  const settled = () => page.waitForFunction(() => document.querySelectorAll('#view .library .grid .card').length > 0
    && !document.querySelector('#view .library .grid .card[aria-hidden="true"]'), null, { timeout: 10000 }).catch(() => {});
  await settled();
  const lettered = await titlesShown().allInnerTexts();
  const chipText = await letterChip().innerText().catch(() => '');
  await shot('03-films-letter');
  await sortBy('Ajouts récents'); await settled();
  const afterLetter = await titlesShown().count();
  check('films : « Lettre » avec le tri par titre seulement, les titres en L', !chipBefore && lettered.join(', ') === 'Lueur Web (2020)' && /Lettre : L/.test(chipText)
    && await letterChip().count() === 0 && afterLetter >= 4, `${lettered.join(', ')} ; « ${chipText} » ; ${afterLetter} films avec un autre tri`);

  await page.locator('.tabbar a[data-tab="search"]').click(); await wait(800);
  await page.fill('input[type=search]', seriesName.split(' ')[0]); await wait(1800);
  check('recherche', await page.locator('.search .card').count() > 0);
  await shot('04-search');
  // An episode by its own name, a saga by its name (tools/web/media.sh), each in a section of its own: the episode
  // opens its series at it, the saga its own page, its films in the order they came out.
  const section = (title) => page.waitForFunction((name) => {
    if (document.querySelector('#view .search .pending')) return null;
    const head = [...document.querySelectorAll('#view .search .section h2')].find((x) => x.textContent === name);
    return head ? [...head.closest('.section').querySelectorAll('.card')].map((c) => [c.querySelector('.card-title')?.textContent, c.querySelector('.card-sub')?.textContent, c.getAttribute('href')].join(' | ')) : null;
  }, title, { timeout: 8000 }).then((handle) => handle.jsonValue(), () => null);
  await page.fill('input[type=search]', 'S01E03'); await wait(1500);
  const foundEpisodes = await section('Épisodes');
  check('recherche : un épisode par son nom, avec sa série et son numéro', foundEpisodes?.length === 1 && /^Courte Web \| S1 · É3 · Courte Web S01E03 \| #\/titre\/[0-9a-f]+\?episode=[0-9a-f]+$/.test(foundEpisodes[0]),
    foundEpisodes?.join(' ; ') ?? 'aucune section « Épisodes »');
  await page.fill('input[type=search]', 'Saga Test'); await wait(1500);
  const foundSagas = await section('Sagas');
  let sagaPage = null;
  if (foundSagas?.length === 1) {
    await page.locator('#view .search .card[href^="#/saga/"]').click();
    sagaPage = await page.waitForFunction(() => {
      // The page's own cards, not the placeholders it shows while Jellyfin answers.
      const titles = [...document.querySelectorAll('#view .saga-page .grid:not([aria-hidden="true"]) .card-title')].map((x) => x.textContent);
      return location.hash.startsWith('#/saga/') && titles.length ? { titles, sub: document.querySelector('#view .saga-page .page-sub')?.textContent ?? '' } : null;
    }, null, { timeout: 8000 }).then((handle) => handle.jsonValue(), () => null);
    await wait(600); await shot('04-saga');
  }
  check('recherche : une saga, sa page avec ses films dans l’ordre', /^Saga Test \| 2 titres \| #\/saga\/[0-9a-f]+$/.test(foundSagas?.[0] ?? '')
    && sagaPage?.titles.join(', ') === 'Lueur Web (2020), Aube Test (2021)' && sagaPage.sub === '2 films · 2020 – 2021',
    `${foundSagas?.join(' ; ') ?? 'aucune section « Sagas »'} ; ${sagaPage ? `${sagaPage.titles.join(', ')} (${sagaPage.sub})` : 'pas de page de saga'}`);

  const found = await api(`Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent(seriesName)}`);
  const series = found.Items[0];
  const episodes = (await api(`Shows/${series.Id}/Episodes?userId=${JSON.parse(await page.evaluate(() => localStorage.getItem('mira.session'))).userId}`)).Items;
  for (const e of episodes) await api(`UserPlayedItems/${e.Id}`, 'DELETE');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, series.Id); await wait(2500);
  const primary = await page.locator('.detail-actions .btn.primary').innerText();
  check('fiche de la série', /S1 · É1/.test(primary), primary.replace(/\s+/g, ' '));
  check('fiche : épisodes', await page.locator('.episode').count() === episodes.length, `${episodes.length} épisodes`);
  // The picture and the end of the episode Lecture plays, asked for after the page shows.
  const facts = () => page.evaluate(() => document.querySelector('#view .detail-meta')?.innerText ?? '');
  await page.waitForFunction(() => /SD.*Fin à/.test(document.querySelector('#view .detail-meta')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
  const seriesFacts = await facts();
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

  // A long press on an episode: its whole summary (cut at two lines in the list), its length and picture, and « Lire
  // depuis le début » for the one under way. The summary is given to it here (the test files have none).
  const summary = 'Un résumé assez long pour dépasser les deux lignes de la liste des épisodes : il continue encore, et encore, '
    + 'jusqu’à la fin de cette phrase que seul le menu du toucher long montre en entier.';
  await page.evaluate(async ([id, text]) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const headers = { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"`, 'Content-Type': 'application/json' };
    const root = location.pathname.replace(/\/Mira\/?.*$/i, '');
    const item = await (await fetch(`${root}/Items/${id}?userId=${s.userId}`, { headers })).json();
    await fetch(`${root}/Items/${id}`, { method: 'POST', headers, body: JSON.stringify({ ...item, Overview: text, LockedFields: [...(item.LockedFields ?? []), 'Overview'] }) });
  }, [episodes[1].Id, summary]);
  await page.locator(`#view .episode[data-id="${episodes[1].Id}"]`).click({ button: 'right' });
  await page.waitForFunction(() => /SD/.test(document.querySelector('.sheet .sheet-about-meta')?.textContent ?? '') && document.querySelector('.sheet p.sheet-about-text'), null, { timeout: 8000 }).catch(() => {});
  const menu = await page.evaluate(() => ({
    name: document.querySelector('.sheet .sheet-about-name')?.textContent ?? '', facts: document.querySelector('.sheet .sheet-about-meta')?.textContent ?? '',
    text: document.querySelector('.sheet p.sheet-about-text')?.textContent ?? '', actions: [...document.querySelectorAll('.sheet .sheet-item')].map((x) => x.textContent.trim()),
  }));
  await shot('07-episode-menu');
  check('toucher long sur un épisode : résumé entier, durée, image, « Lire depuis le début »',
    menu.text === summary && /^2\. /.test(menu.name) && /min/.test(menu.facts) && /SD/.test(menu.facts) && menu.actions[0] === 'Reprendre' && menu.actions[1] === 'Lire depuis le début',
    `${menu.name} ; ${menu.facts} ; ${menu.text.length} caractères ; ${menu.actions.join(', ')}`);
  await page.keyboard.press('Escape'); await wait(500);

  await page.locator('.detail-actions .btn.primary').click(); await wait(3000);
  const resumed = await page.evaluate(() => document.querySelector('video')?.currentTime ?? 0);
  check('reprise au bon endroit', resumed >= second.UserData.PlaybackPositionTicks / 1e7 - 1, `${resumed.toFixed(1)} s`);
  // « Arrêter » from « En ce moment sur Jellyfin » (Mira for Windows, Mira web): Jellyfin's Playstate Stop, through the
  // socket the player keeps open, closes it.
  const phone = await page.evaluate(() => JSON.parse(localStorage.getItem('mira.device')));
  const watching = await page.waitForFunction(async ([id]) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Sessions`, { headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
    const found = (await r.json()).find((x) => x.DeviceId === id);
    return found?.SupportsRemoteControl && found.NowPlayingItem ? found.Id : null;
  }, [phone], { timeout: 15000, polling: 500 }).then((handle) => handle.jsonValue(), () => null);
  // « En ce moment sur Jellyfin » in Mira web, on another device with this administrator's account: the phone's
  // session and how Jellyfin sends it; its « Arrêter » is what stops it here.
  let nowRow = '', stoppedFromList = false;
  if (watching) {
    const admin = await newContext('en ce moment');
    const other = await admin.newPage();
    other.on('pageerror', (e) => errors.push(`en ce moment : ${e.message}`));
    try {
      await signIn(other);
      await other.locator('.tabbar a[data-tab="settings"]').click();
      await other.getByRole('link', { name: /En ce moment sur Jellyfin/ }).click();
      const row = other.locator(`.now-item[data-session="${watching}"]`);
      await row.waitFor({ timeout: 10000 });
      nowRow = (await row.innerText()).trim().replace(/\s*\n\s*/g, ' | ');
      await other.screenshot({ path: `${out}/11b-now.png` });
      await row.getByRole('button', { name: /^Arrêter la lecture sur/ }).click();
      await other.getByRole('menuitem', { name: 'Arrêter la lecture' }).click();
      stoppedFromList = true;
    } catch (error) { nowRow ||= error.message.split('\n')[0]; }
    await admin.close();
  }
  check('en ce moment (Mira web, administrateur) : la lecture du téléphone, comment Jellyfin l’envoie, « Arrêter »',
    stoppedFromList && nowRow.includes(seriesName) && /Lecture directe|Conversion|Fichier remis/.test(nowRow), nowRow || 'session jamais prête');
  if (watching && !stoppedFromList) await api(`Sessions/${watching}/Playing/Stop`, 'POST');
  const stoppedFromPc = await page.waitForFunction((id) => location.hash.startsWith(`#/titre/${id}`), series.Id, { timeout: 8000 }).then(() => true, () => false);
  const stopToast = await page.locator('.toast').innerText().catch(() => '');
  check('arrêt depuis un autre appareil (Playstate Stop) : le lecteur se ferme', !!watching && stoppedFromPc,
    `${watching ? 'session prête à recevoir les commandes' : 'session jamais prête'}, ${stoppedFromPc ? 'lecteur fermé' : 'lecteur resté ouvert'}${stopToast ? ` (« ${stopToast.trim()} »)` : ''}`);
  if (!stoppedFromPc) { await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); }
  await wait(1500);

  // A series under way: the episodes left on its poster (the first one seen, the others to watch).
  await page.locator('.tabbar a[data-tab="series"]').click(); await wait(2000);
  const toWatch = (await api(`Items/${series.Id}`)).UserData.UnplayedItemCount;
  const badge = await page.locator('.card', { hasText: seriesName }).first().locator('.badge-count').innerText().catch(() => '');
  check('séries : épisodes à voir sur l’affiche', toWatch > 0 && badge === String(toWatch), `${badge || 'aucune pastille'} (Jellyfin : ${toWatch})`);
  await shot('07-series-left');
  // Several libraries of series (Séries, Animes in tools/web/setup.mjs): one at a time, or all of them.
  const shelves = page.locator('#view .segmented [role=radio]');
  const names = await shelves.allInnerTexts();
  await shelves.filter({ hasText: 'Animes' }).click();
  await page.waitForFunction(() => document.querySelector('#view .library-count')?.textContent === '1 série', null, { timeout: 10000 }).catch(() => {});
  const animes = await page.locator('#view .grid .card-title').allInnerTexts();
  await shot('07-series-animes');
  await shelves.filter({ hasText: 'Tout' }).click();
  await page.waitForFunction(() => document.querySelectorAll('#view .grid .card').length >= 3, null, { timeout: 10000 }).catch(() => {});
  const everything = await page.locator('#view .grid .card').count();
  check('séries : une bibliothèque à la fois (Séries, Animes) ou toutes', [...names].sort().join('|') === 'Animes|Séries|Tout' && animes.join('|') === 'Courte Anime' && everything >= 3,
    `${names.join(' | ')} ; Animes : ${animes.join(', ') || 'rien'} ; Tout : ${everything} séries`);

  // Tracks chosen on a title page: Lueur Web has French and English audio, and subtitles in both beside it.
  const film = (await api('Items?recursive=true&includeItemTypes=Movie&searchTerm=Lueur')).Items[0];
  const streams = (await api(`Items/${film.Id}`)).MediaSources[0].MediaStreams;
  const english = streams.find((x) => x.Type === 'Audio' && x.Language === 'eng'), frenchSubs = streams.find((x) => x.Type === 'Subtitle' && x.Language === 'fra');
  await api(`UserPlayedItems/${film.Id}`, 'DELETE');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await wait(2000);
  const filmFacts = await facts();
  // Lueur Web: 854 × 480, 40 s long, ends 40 s from now (a minute either way: the clock may turn in between).
  const end = /Fin à (\d{1,2}) h (\d{2})/.exec(filmFacts), expected = new Date(Date.now() + 40_000);
  const late = end ? ((Number(end[1]) * 60 + Number(end[2]) - expected.getHours() * 60 - expected.getMinutes()) % 1440 + 1440) % 1440 : null;
  check('fiche : image du fichier et heure de fin (film, épisode d’une série)', /SD/.test(filmFacts) && late !== null && (late <= 1 || late >= 1439) && /SD\s*Fin à/.test(seriesFacts),
    `${filmFacts.replace(/\s+/g, ' ')} ; série : ${seriesFacts.replace(/\s+/g, ' ')}`);
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

  // Its saga (tools/web/media.sh, setup.mjs): the collection Jellyfin made of the two films, the other one in a row.
  // « Tout voir » opens the saga's page.
  const saga = await page.waitForFunction(() => {
    const head = [...document.querySelectorAll('#view .section h2')].find((x) => x.textContent === 'Saga Test');
    const section = head?.closest('.section');
    return section ? [...section.querySelectorAll('.card-title')].map((x) => x.textContent).concat(section.querySelector('.section-head a')?.getAttribute('href') ?? 'pas de « Tout voir »') : null;
  }, null, { timeout: 10000 }).then((handle) => handle.jsonValue(), () => null);
  check('fiche : saga d’un film (collection faite par Jellyfin), ses autres films', saga?.length === 2 && /^Aube Test/.test(saga[0]) && /^#\/saga\/[0-9a-f]+$/.test(saga[1]),
    saga ? saga.join(', ') : 'aucune rangée « Saga Test »');
  // Bonus videos (tools/web/media.sh): the film's extras folder and its « behind the scenes » one, the series' extras
  // folder. They have no page: a tap on the card plays it.
  const bonus = page.locator('#view .section', { has: page.locator('h2', { hasText: 'Bonus' }) });
  await bonus.locator('.card').first().waitFor({ timeout: 10000 }).catch(() => {});
  const filmExtras = await bonus.locator('.card-sub').allInnerTexts();
  await shot('12-bonus');
  await bonus.locator('.card', { hasText: 'Tournage' }).click({ timeout: 5000 }).catch(() => {});
  const extraPlayed = await page.waitForFunction(() => {
    const v = document.querySelector('video');
    return v && !v.paused && v.currentTime > 0.5 && /^#\/lecture\//.test(location.hash) ? v.currentTime : null;
  }, null, { timeout: 20000, polling: 250 }).then((handle) => handle.jsonValue(), () => null);
  if (extraPlayed) { await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500); }
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, series.Id);
  // The film's page may still be there while the series' slides in: its row comes once Jellyfin answered.
  await page.waitForFunction((name) => document.title.startsWith(name) && document.querySelectorAll('#view .section .wide-card').length > 0
    && [...document.querySelectorAll('#view .section h2')].some((x) => x.textContent === 'Bonus'), seriesName, { timeout: 10000 }).catch(() => {});
  const seriesExtras = await bonus.locator('.card-title').allInnerTexts();
  check('fiche : rangée Bonus (film, série), une vidéo se lit depuis sa carte',
    filmExtras.length === 2 && filmExtras.some((x) => /^Coulisses · (reste )?1 min$/.test(x)) && !!extraPlayed && seriesExtras.join('|') === 'Générique sans texte',
    `film : ${filmExtras.join(' ; ') || 'aucune rangée'}, ${extraPlayed ? 'lue' : 'pas lue'} ; série : ${seriesExtras.join(' ; ') || 'aucune rangée'}`);

  // Genres and studio on a title page (tools/web/media.sh: Lueur Web and Aube Test are « Animation » films of « Studio
  // Test », in a saga; Chapitres Web is of the same studio, outside it). The genre opens Films filtered on it, the studio
  // its own page; « Du même studio » leaves out the film of the saga's row. A search finds the studio by its name.
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id);
  const sameStudio = await page.waitForFunction(() => {
    const head = [...document.querySelectorAll('#view .section h2')].find((x) => x.textContent.startsWith('Du même studio'));
    const section = head?.closest('.section');
    return section ? [head.textContent, ...[...section.querySelectorAll('.card-title')].map((x) => x.textContent), section.querySelector('.section-head a')?.getAttribute('href') ?? ''] : null;
  }, null, { timeout: 10000 }).then((handle) => handle.jsonValue(), () => null);
  const genreLinks = await page.locator('#view .genres a').evaluateAll((links) => links.map((x) => `${x.textContent} ${x.getAttribute('href')}`));
  const studioLink = await page.locator('#view .credits a[href^="#/studio/"]').evaluateAll((links) => links.map((x) => `${x.parentElement.textContent} ${x.getAttribute('href')}`));
  await shot('12-genres-studio');
  // A right click (iPad with a trackpad, Android with a mouse) opens the long press's menu, on a card as on an episode.
  await page.locator('#view .section', { has: page.locator('h2', { hasText: 'Du même studio' }) }).locator('.card').first().click({ button: 'right' }).catch(() => {});
  const cardMenu = await page.locator('.sheet .sheet-item').allInnerTexts().catch(() => []);
  await page.keyboard.press('Escape'); await wait(500);
  const titlesOf = (prefix) => page.waitForFunction((start) => {
    const cards = [...document.querySelectorAll('#view .grid:not([aria-hidden="true"]) .card-title')].map((x) => x.textContent);
    return location.hash.startsWith(start) && cards.length && !document.querySelector('#view .grid .card[aria-hidden="true"]') ? cards.sort() : null;
  }, prefix, { timeout: 10000 }).then((handle) => handle.jsonValue(), () => null);
  await page.locator('#view .genres a', { hasText: 'Animation' }).click();
  const ofGenre = await titlesOf('#/films?genre=');
  const genreChip = await page.locator('#view .library .chip.on').allInnerTexts().catch(() => []);
  await shot('12-genre');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await wait(1500);
  await page.locator('#view .credits a[href^="#/studio/"]').click();
  const ofStudio = await titlesOf('#/studio/');
  const studioHead = await page.locator('#view .studio-page h1').innerText().catch(() => '');
  await shot('12-studio');
  check('fiche : genres et studio cliquables, « Du même studio » sans le film de la saga, clic droit sur une carte',
    genreLinks.join('|') === 'Animation #/films?genre=Animation' && /^Studio : Studio Test #\/studio\/[0-9a-f]+$/.test(studioLink[0] ?? '')
      && sameStudio?.[0] === 'Du même studio · Studio Test' && sameStudio[1] === 'Chapitres Web (2024)' && sameStudio.length === 3 && /^#\/studio\//.test(sameStudio[2])
      && ofGenre?.join('|') === 'Aube Test (2021)|Lueur Web (2020)' && genreChip.includes('Animation')
      && studioHead === 'Studio Test' && ofStudio?.join('|') === 'Aube Test (2021)|Chapitres Web (2024)|Lueur Web (2020)' && cardMenu.some((x) => x.startsWith('Marquer comme')),
    `genres : ${genreLinks.join(', ') || 'aucun lien'} ; ${studioLink.join(', ') || 'pas de studio'} ; rangée : ${sameStudio?.join(', ') ?? 'aucune'} ; `
      + `genre : ${ofGenre?.join(', ') ?? 'rien'} (${genreChip.join(', ')}) ; studio : ${studioHead} ${ofStudio?.join(', ') ?? 'rien'} ; clic droit : ${cardMenu.join(', ') || 'pas de menu'}`);
  await page.locator('.tabbar a[data-tab="search"]').click(); await wait(800);
  await page.fill('input[type=search]', 'studio te');
  const foundStudio = await page.waitForFunction(() => {
    const head = [...document.querySelectorAll('#view .search .section h2')].find((x) => x.textContent === 'Studios');
    return head ? [...head.closest('.section').querySelectorAll('a.chip')].map((x) => `${x.textContent} ${x.getAttribute('href')}`) : null;
  }, null, { timeout: 8000 }).then((handle) => handle.jsonValue(), () => null);
  await shot('12-search-studio');
  check('recherche : un studio par son nom (aucun titre ne le porte)', foundStudio?.length === 1 && /^Studio Test #\/studio\/[0-9a-f]+$/.test(foundStudio[0]), foundStudio?.join(' ; ') ?? 'aucune section « Studios »');
  await page.fill('input[type=search]', '');

  // A series of two seasons (Deux Temps): the first watched, the page opens on the second, where Lecture picks up.
  // Season 1 shown: « À suivre » brings season 2; « Marquer la saison comme non vue » marks season 1 at once on
  // Jellyfin, then « comme vue » again.
  const twice = (await api('Items?recursive=true&includeItemTypes=Series&searchTerm=Deux%20Temps')).Items[0];
  const userId = JSON.parse(await page.evaluate(() => localStorage.getItem('mira.session'))).userId;
  const twoSeasons = async () => (await api(`Shows/${twice.Id}/Episodes?userId=${userId}`)).Items;
  // The page shows the mark at once, and Jellyfin has it a moment later.
  const watchedOnJellyfin = async (expected) => {
    let seen = [];
    for (let i = 0; i < 20; i++) {
      seen = (await twoSeasons()).map((e) => `${e.ParentIndexNumber}:${e.UserData.Played ? 'vu' : 'non vu'}`);
      if (seen.join('|') === expected) break;
      await wait(250);
    }
    return seen;
  };
  for (const e of await twoSeasons()) await api(`UserPlayedItems/${e.Id}`, e.ParentIndexNumber === 1 ? 'POST' : 'DELETE');
  await page.evaluate((id) => { location.hash = `#/titre/${id}`; }, twice.Id);
  await page.waitForFunction(() => document.querySelectorAll('#view .seasons .chip').length === 2 && document.querySelector('#view .episode'), null, { timeout: 10000 }).catch(() => {});
  const opened = await page.locator('#view .seasons .chip.on').innerText().catch(() => '');
  const barOnNext = await page.locator('#view .season-bar').innerText().catch(() => '');
  await page.locator('#view .seasons .chip', { hasText: 'Saison 1' }).click(); await wait(400);
  const hint = await page.locator('#view .next-line').innerText().catch(() => '');
  const markLabel = await page.locator('#view .season-mark').innerText().catch(() => '');
  await shot('12-season-next');
  await page.locator('#view .next-line').click(); await wait(400);
  const brought = await page.locator('#view .seasons .chip.on').innerText().catch(() => '');
  check('série : « À suivre » quand une autre saison est affichée, qui l’amène',
    opened === 'Saison 2' && !/À suivre/.test(barOnNext) && /À suivre : S2 · É1/.test(hint) && /Saison 2/.test(hint) && brought === 'Saison 2',
    `ouverte sur ${opened || '?'} ; Saison 1 : « ${hint.replace(/\s+/g, ' ') || 'rien'} » ; après le clic : ${brought || '?'}`);
  await page.locator('#view .seasons .chip', { hasText: 'Saison 1' }).click(); await wait(400);
  await page.locator('#view .season-mark').click();
  const unmarked = await page.waitForFunction(() => /comme vue$/.test(document.querySelector('#view .season-mark')?.textContent.trim() ?? '') && !document.querySelector('#view .next-line')
    && !document.querySelector('#view .episode .badge-watched'), null, { timeout: 10000 }).then(() => true, () => false);
  const afterUnmark = await watchedOnJellyfin('1:non vu|1:non vu|2:non vu');
  await shot('12-season-unmarked');
  await page.locator('#view .season-mark').click();
  const remarked = await page.waitForFunction(() => /non vue$/.test(document.querySelector('#view .season-mark')?.textContent.trim() ?? ''), null, { timeout: 10000 }).then(() => true, () => false);
  const afterMark = await watchedOnJellyfin('1:vu|1:vu|2:non vu');
  check('série : « Marquer la saison comme non vue », puis « comme vue », d’un coup sur Jellyfin',
    /comme non vue$/.test(markLabel) && unmarked && remarked && afterUnmark.join('|') === '1:non vu|1:non vu|2:non vu' && afterMark.join('|') === '1:vu|1:vu|2:non vu',
    `bouton : « ${markLabel} » ; non vue : ${afterUnmark.join(', ')}${unmarked ? '' : ' (page pas à jour)'} ; vue : ${afterMark.join(', ')}${remarked ? '' : ' (page pas à jour)'}`);

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
  // The bands of the top and bottom bars take their taps: one that misses a button there shows the controls, never
  // hides them, never skips 10 s (a double tap on a side), never presses a button hidden there; one on the picture
  // between them hides the controls.
  const bands = await (async () => {
    await page.evaluate(() => document.querySelector('video').play().catch(() => {})); await wait(600);
    const center = async (selector) => { const b = await page.locator(selector).boundingBox(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 }; };
    const state = () => page.evaluate(() => ({
      idle: document.querySelector('.player').classList.contains('idle'), t: document.querySelector('video').currentTime,
      sheet: !!document.querySelector('.sheet-layer:not(.leaving)'),
    }));
    const said = [];
    await page.evaluate(() => document.querySelector('.player').classList.add('idle'));
    const tracks = await center('.p-top button[aria-label="Audio et sous-titres"]');
    await page.touchscreen.tap(tracks.x, tracks.y); await wait(400);
    let now = await state();
    said.push(!now.idle && !now.sheet ? 'bouton caché : commandes montrées' : `bouton caché : ${now.sheet ? 'feuille ouverte' : 'commandes toujours cachées'}`);
    const ok1 = !now.idle && !now.sheet;
    const row = await center('.p-bottom .p-row .spacer');
    await page.touchscreen.tap(row.x, row.y); await wait(400);
    now = await state();
    const ok2 = !now.idle;
    said.push(ok2 ? 'barre du bas : commandes gardées' : 'barre du bas : commandes cachées');
    const box = await page.locator('.player').boundingBox(), before = now.t, at = Date.now();
    await page.touchscreen.tap(box.x + box.width * 0.08, row.y); await wait(120);
    await page.touchscreen.tap(box.x + box.width * 0.08, row.y); await wait(700);
    now = await state();
    const moved = now.t - before - (Date.now() - at) / 1000;
    const ok3 = Math.abs(moved) < 3 && !now.idle;
    said.push(ok3 ? 'double toucher sur la barre : pas de saut' : `double toucher sur la barre : ${moved.toFixed(1)} s`);
    const heading = await center('.p-top .p-title');
    await page.touchscreen.tap(heading.x, heading.y); await wait(400);
    now = await state();
    const ok4 = !now.idle;
    said.push(ok4 ? 'titre : commandes gardées' : 'titre : commandes cachées');
    const topBar = await page.locator('.p-top').boundingBox(), play = await page.locator('.p-center .round.big').boundingBox();
    await page.touchscreen.tap(box.x + box.width / 2, (topBar.y + topBar.height + play.y) / 2); await wait(400);
    now = await state();
    const ok5 = now.idle;
    said.push(ok5 ? 'image : commandes cachées' : 'image : commandes toujours là');
    return { ok: ok1 && ok2 && ok3 && ok4 && ok5, said };
  })().catch((error) => ({ ok: false, said: [error.message] }));
  check('lecteur : les barres du haut et du bas prennent leurs touchers, l’image entre elles cache les commandes', bands.ok, bands.said.join(' ; '));
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500);

  // Passages the file's chapters name, with no segment provider on Jellyfin (Chapitres Web: « Avant-générique » 0-4 s,
  // « Opening » 4-14 s): the opening is offered as it starts; with « Passer les intros et les récaps », jumped over by
  // itself, the cold open played.
  const chaptered = (await api('Items?recursive=true&includeItemTypes=Movie&searchTerm=Chapitres')).Items[0];
  await page.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, chaptered.Id);
  const offered = await page.waitForFunction(() => {
    const v = document.querySelector('video'), pill = document.querySelector('button.pill');
    return v && (pill || v.currentTime > 14) ? { label: pill?.textContent.trim() ?? '', t: v.currentTime } : null;
  }, null, { timeout: 40000, polling: 100 }).then((handle) => handle.jsonValue(), () => null);
  check('chapitres : « Passer l’intro » quand l’opening commence', offered?.label === 'Passer l’intro' && offered.t >= 3.5 && offered.t < 6,
    offered ? `« ${offered.label} » à ${offered.t.toFixed(1)} s` : 'aucun bouton');
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500);
  const autoSkip = () => page.getByRole('switch', { name: /Passer les intros et les récaps/ });
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1500);
  await autoSkip().click();
  await page.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, chaptered.Id);
  // The playhead sampled every 100 ms: the jump over the opening, and where it started from.
  const jump = await page.evaluate(() => new Promise((resolve) => {
    let last = null; const since = performance.now();
    const timer = setInterval(() => {
      const v = document.querySelector('video'), t = v?.currentTime ?? 0;
      if (last !== null && t - last > 5) { clearInterval(timer); resolve({ from: last, to: t }); }
      last = t;
      if (performance.now() - since > 40000) { clearInterval(timer); resolve(null); }
    }, 100);
  }));
  check('chapitres : l’opening passé tout seul, l’avant-générique joué', !!jump && jump.from >= 3.5 && jump.from < 5 && jump.to >= 13.5,
    jump ? `${jump.from.toFixed(1)} → ${jump.to.toFixed(1)} s` : 'pas de saut');
  await shot('14-chapters');
  await page.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await wait(1500);
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1500);
  await autoSkip().click();

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
  // Opened by Mira for Windows once Tailscale is ready: this PC's Tailscale address first, the home one to switch to.
  // Any other address passed the same way is ignored.
  await page.evaluate(() => { location.hash = '#/partager?partout=http%3A%2F%2F100.101.102.103%3A8096'; }); await wait(1500);
  const everywhere = { address: await page.locator('#view .address').innerText(), chips: await page.locator('#view .chip[aria-pressed]').count() };
  await shot('09-share-tailscale');
  await page.evaluate(() => { location.hash = '#/partager?partout=http%3A%2F%2Fexample.com'; }); await wait(1500);
  const other = await page.locator('#view .address').innerText();
  check('QR code : adresse Tailscale d’abord, jamais un autre site', everywhere.address.startsWith('http://100.101.102.103:8096/') && everywhere.address.endsWith('/Mira/')
    && everywhere.chips === 2 && !other.includes('example.com'), `${everywhere.address} · ${other}`);
  await page.setViewportSize({ width: 1280, height: 800 }); await wait(300);
  await page.evaluate(() => { location.hash = '#/'; }); await wait(2500);
  check('grand écran : barre de navigation à gauche', await page.locator('.tabbar').evaluate((n) => getComputedStyle(n).flexDirection === 'column'));
  await shot('10-wide-home');
  await page.setViewportSize({ width: 393, height: 852 }); await wait(300);
  // After an update of Mira web (a phone that last saw 0.0.1): its points for the phone, once, those of WhatsNew.json.
  const notes = phoneNotes(JSON.parse(fs.readFileSync(new URL('../../src/Mira.Core/WhatsNew.json', import.meta.url), 'utf8')), '0.0.1', version);
  await page.evaluate(() => { const s = JSON.parse(localStorage.getItem('mira.settings') ?? '{}'); s.seenVersion = '0.0.1'; localStorage.setItem('mira.settings', JSON.stringify(s)); });
  await page.reload(); await wait(4500);
  const news = await page.locator('.sheet .news').count() ? await page.locator('.sheet .news').innerText() : '';
  if (news) await shot('10b-whats-new');
  await page.getByRole('button', { name: 'C’est parti' }).click({ timeout: 2000 }).catch(() => {}); await wait(600);
  await page.reload(); await wait(4500);
  check('« Nouveau dans Mira » : les points de Mira web de la nouvelle version, une seule fois',
    (notes.length ? news.includes(`Mira ${notes[0].version}`) && notes[0].items.every((x) => news.includes(x.title)) : !news) && (await seenVersion()) === version && !(await page.locator('.news').count()),
    notes.length ? `${notes[0].version} : ${notes[0].items.map((x) => x.title).join(', ')}` : `aucun point pour Mira web jusqu’à ${version}`);
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1200);
  // What the account left on the phone (its screens, and in the settings what it did with its titles), and a setting
  // of this phone, through Mira's own settings.
  const kept = await page.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { settings } = await import(`${root}session.js`);
    settings.set('recentSearches', ['Lueur']); settings.set('hiddenResume', { x: new Date().toISOString() }); settings.set('seriesTracks', { y: { audio: 'fra' } });
    settings.set('subtitleSize', 125);
    return { keys: Object.keys(localStorage), device: localStorage.getItem('mira.device') };
  });
  await page.getByRole('button', { name: 'Se déconnecter' }).click(); await wait(400);
  await page.locator('.sheet-item.danger').click(); await wait(1500);
  check('déconnexion', (await page.evaluate(() => location.hash)) === '#/connexion' && !(await page.evaluate(() => localStorage.getItem('mira.session'))));
  const left = await page.evaluate(() => ({ keys: Object.keys(localStorage), device: localStorage.getItem('mira.device'), settings: JSON.parse(localStorage.getItem('mira.settings') ?? '{}') }));
  const account = (keys) => keys.filter((k) => /^mira\.(home|library|choices)\./.test(k));
  check('déconnexion : rien du compte ne reste, l’appareil garde son identifiant et ses réglages',
    account(kept.keys).some((k) => k.startsWith('mira.home.')) && account(kept.keys).some((k) => k.startsWith('mira.library.')) && !account(left.keys).length
    && !left.settings.recentSearches?.length && !Object.keys(left.settings.hiddenResume ?? {}).length && !Object.keys(left.settings.seriesTracks ?? {}).length
    && left.settings.subtitleSize === 125 && !!kept.device && left.device === kept.device,
    `avant : ${account(kept.keys).length} écran(s) du compte gardé(s), après : ${account(left.keys).join(', ') || 'aucun'} ; recherches ${left.settings.recentSearches?.length ?? '?'}, `
    + `reprises masquées ${Object.keys(left.settings.hiddenResume ?? {}).length}, pistes par série ${Object.keys(left.settings.seriesTracks ?? {}).length}, `
    + `taille des sous-titres ${left.settings.subtitleSize}, appareil ${left.device === kept.device ? 'gardé' : 'changé'}`);

  // Quick Connect: the phone shows a code, another device signed in approves it; then this phone approves another's.
  const server = base.replace(/\/Mira\/?.*$/i, '');
  const quickAuth = (id, token) => `MediaBrowser Client="check", Device="check", DeviceId="${id}", Version="1"${token ? `, Token="${token}"` : ''}`;
  const elsewhere = await (await fetch(`${server}/Users/AuthenticateByName`, { method: 'POST', headers: { Authorization: quickAuth('check-pc'), 'Content-Type': 'application/json' }, body: JSON.stringify({ Username: user, Pw: password }) })).json();
  await page.getByRole('button', { name: 'Se connecter avec un code' }).click();
  await page.locator('.code').waitFor({ timeout: 8000 });
  const code = (await page.locator('.code').innerText()).replace(/\D/g, '');
  await shot('11-login-code');
  const approved = await fetch(`${server}/QuickConnect/Authorize?code=${code}`, { method: 'POST', headers: { Authorization: quickAuth('check-pc', elsewhere.AccessToken) } });
  const signedIn = await page.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 }).then(() => true, () => false);
  check('connexion par code : le code validé ailleurs connecte le téléphone', code.length === 6 && approved.ok && signedIn && !!(await page.evaluate(() => localStorage.getItem('mira.session'))),
    `code ${code}, validation ${approved.status}, ${signedIn ? 'connecté' : 'pas connecté'}`);
  const tv = await (await fetch(`${server}/QuickConnect/Initiate`, { method: 'POST', headers: { Authorization: quickAuth('check-tv') } })).json();
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(1200);
  await page.getByRole('button', { name: /Autoriser un appareil/ }).click(); await wait(400);
  await page.fill('#approve-code', `${tv.Code.slice(0, 3)} ${tv.Code.slice(3)}`); await page.keyboard.press('Enter'); await wait(1200);
  const approval = await (await fetch(`${server}/QuickConnect/Connect?secret=${tv.Secret}`, { headers: { Authorization: quickAuth('check-tv') } })).json();
  check('autoriser un appareil : le code d’un autre appareil validé depuis Réglages', approval.Authenticated === true && !!(await page.evaluate(() => localStorage.getItem('mira.session'))),
    `${approval.Authenticated ? 'validé' : 'pas validé'}, ${await page.locator('.toast').first().innerText().catch(() => 'aucun message')}`);
  // « Accueil à ta main »: a library's own row, shown and moved to the top, opens the home page; then Mira's order again.
  await page.getByRole('button', { name: /Rangées de l’accueil/ }).click(); await wait(500);
  const shelfKey = await page.locator('.home-row', { hasText: 'Nouveau dans' }).first().getAttribute('data-key');
  const shelfRow = () => page.locator(`.home-row[data-key="${shelfKey}"]`);
  const shelfTitle = (await shelfRow().innerText()).trim();
  await shelfRow().locator('.home-switch').click();
  for (let i = 0; i < 8 && await shelfRow().locator('.up').isEnabled(); i++) { await shelfRow().locator('.up').click(); await wait(100); }
  await shot('12-home-rows');
  await page.keyboard.press('Escape'); await wait(500);
  await page.locator('.tabbar a[data-tab="home"]').click(); await wait(2500);
  const arranged = await page.locator('.view.home .section .h2').allInnerTexts();
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(800);
  await page.getByRole('button', { name: /Rangées de l’accueil/ }).click(); await wait(500);
  await page.getByRole('button', { name: 'Revenir à l’ordre de Mira' }).click(); await wait(300);
  await page.keyboard.press('Escape'); await wait(500);
  await page.locator('.tabbar a[data-tab="home"]').click(); await wait(1500);
  const restored = await page.locator('.view.home .section .h2').allInnerTexts();
  check('accueil à ta main : la rangée d’une bibliothèque en tête, puis l’ordre de Mira', shelfKey?.startsWith('library:') && arranged[0] === shelfTitle && !restored.includes(shelfTitle),
    `${shelfTitle} → ${arranged.join(', ')} → ${restored.join(', ')}`);
  // « Ton bilan »: the month from Jellyfin's record of what was played to the end, then the year.
  await page.locator('.tabbar a[data-tab="settings"]').click(); await wait(800);
  await page.getByRole('button', { name: /^Ton bilan/ }).click();
  await page.locator('.report-time, .report > .meta:not(:first-child), .report .form-error').first().waitFor({ timeout: 10000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.report')?.textContent.includes('Calcul'), null, { timeout: 15000 }).catch(() => {});
  const month = await page.locator('.report').innerText();
  await shot('13-report');
  await page.getByRole('button', { name: 'Cette année', exact: true }).click();
  await page.waitForFunction(() => !document.querySelector('.report')?.textContent.includes('Calcul'), null, { timeout: 15000 }).catch(() => {});
  const yearly = await page.locator('.report').innerText();
  await page.keyboard.press('Escape'); await wait(400);
  const said = (text) => /devant l’écran|Aucun film ni épisode terminé/.test(text) && !text.includes('null');
  check('ton bilan : le mois puis l’année, d’après Jellyfin', said(month) && said(yearly) && yearly.includes('cette année'), `${month.split('\n').slice(0, 2).join(' ')} / ${yearly.split('\n').slice(0, 2).join(' ')}`);
} catch (error) {
  check('déroulé', false, error.message.split('\n')[0]);
  await shot('error').catch(() => {});
}
check('aucune erreur JavaScript', errors.length === 0, errors.slice(0, 3).join(' | '));

// Apple's player, simulated: full screen as WebKit reports it (webkitPresentationMode and its event).
const apple = await newContext('lecteur d’Apple');
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
  await ap.getByRole('button', { name: 'Se connecter', exact: true }).click();
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
  // « Minuterie : fin de l’épisode », chosen on the title page (Apple's player has no room for Mira's menus): the
  // episode ends, the next one does not start, and the timer is gone for the next title.
  await ap.evaluate((id) => { location.hash = `#/titre/${id}`; }, series.Id); await ap.waitForTimeout(2500);
  await ap.locator('.tracks-line', { hasText: 'Minuterie' }).click(); await ap.waitForTimeout(600);
  await ap.getByRole('menuitemradio', { name: /Fin de l’épisode/ }).click(); await ap.waitForTimeout(800);
  const timerLine = await ap.locator('.tracks-line', { hasText: 'Minuterie' }).innerText();
  await ap.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, episodes[0].Id);
  const stopped = await ap.waitForFunction((id) => location.hash.startsWith(`#/titre/${id}`), series.Id, { timeout: 30000 }).then(() => true, () => false);
  await ap.waitForTimeout(2000);
  now = await state();
  const timerAfter = await ap.locator('.tracks-line', { hasText: 'Minuterie' }).innerText().catch(() => '');
  check('minuterie (lecteur d’Apple simulé) : « Fin de l’épisode » choisie sur la fiche, l’épisode suivant ne démarre pas',
    /fin de l’épisode/.test(timerLine) && stopped && !now.hash.includes(episodes[1].Id) && /désactivée/.test(timerAfter),
    `${timerLine.replace(/\s+/g, ' ')} ; puis ${now.hash.split('?')[0]}, ${timerAfter.replace(/\s+/g, ' ')}`);
  // End credits that only the preview follows (chapters « Ending » 12-25 s, « Preview » 25-30 s): the next episode
  // starts by itself 10 s into them (22 s), before the end. Moved into them by the person (past 22 s, here), they play
  // to the end.
  const fin = (await call(`Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent('Courte Fin')}`)).Items[0];
  const parts = (await call(`Shows/${fin.Id}/Episodes?userId=${token.userId}`)).Items;
  const switchedAt = async (moveTo) => {
    await ap.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, parts[0].Id);
    await ap.waitForFunction(() => (document.querySelector('video')?.currentTime ?? 0) > 1, null, { timeout: 20000 });
    // A seek in Apple's player: the video moved by something other than Mira.
    if (moveTo) await ap.evaluate((t) => { document.querySelector('video').currentTime = t; }, moveTo);
    // The last moment of the first episode before the second one's address.
    const at = await ap.evaluate((id) => new Promise((resolve) => {
      let last = 0;
      const timer = setInterval(() => {
        if (location.hash.includes(id)) { clearInterval(timer); resolve(last); return; }
        last = document.querySelector('video')?.currentTime ?? last;
      }, 100);
      setTimeout(() => { clearInterval(timer); resolve(-1); }, 45000);
    }), parts[1].Id);
    await ap.waitForTimeout(1500);
    const shown = await state();
    await ap.evaluate(() => document.querySelector('video')?.webkitExitFullscreen());
    await ap.waitForFunction((id) => location.hash.startsWith(`#/titre/${id}`), fin.Id, { timeout: 8000 }).catch(() => {});
    await ap.waitForTimeout(800);
    return { at, mode: shown.mode };
  };
  const early = await switchedAt(0);
  check('lecteur d’Apple (simulé) : épisode suivant 10 s après le début du générique de fin, avant l’aperçu',
    parts.length === 2 && early.at > 21 && early.at < 25 && early.mode === 'fullscreen', `É1 quitté à ${early.at.toFixed(1)} s sur 30, puis ${early.mode}`);
  const watched = await switchedAt(24);
  check('lecteur d’Apple (simulé) : générique rejoint à la main, regardé jusqu’au bout, puis épisode suivant',
    watched.at > 28 && watched.mode === 'fullscreen', `É1 quitté à ${watched.at.toFixed(1)} s sur 30, puis ${watched.mode}`);
} catch (error) {
  check('lecteur d’Apple (simulé) : déroulé', false, error.message.split('\n')[0]);
  await ap.screenshot({ path: `${out}/error-apple.png` }).catch(() => {});
}
check('lecteur d’Apple (simulé) : aucune erreur JavaScript', appleErrors.length === 0, appleErrors.slice(0, 3).join(' | '));

// An anime for Apple's player, asked for as an iPhone does (its codecs are claimed here: Chromium has neither H.264
// nor HLS). Jellyfin's answers and Mira's plugin are real: the HLS stream lists every text subtitle, ASS included,
// with Mira's names, and its WebVTT comes without the MPEG-TS time map (10 s late on fMP4), at the file's own times.
const iphone = await newContext('profil d’iPhone');
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
const home = await newContext('app');
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
  await hp.getByRole('button', { name: 'Se connecter', exact: true }).click();
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
const tabs = await newContext('onglets');
await tabs.addInitScript(() => Object.defineProperty(Navigator.prototype, 'standalone', { configurable: true, get: () => true }));
const tp = await tabs.newPage();
const tabErrors = [];
let restoreFavorite = null;   // the favourite changed for a check, put back whatever happens
tp.on('pageerror', (e) => tabErrors.push(e.message));
try {
  await tp.goto(`${base}#/connexion`); await tp.waitForTimeout(1500);
  if (await tp.locator('.users').count()) await tp.getByRole('button', { name: 'Autre compte' }).click();
  await tp.fill('#login-name', user); await tp.fill('#login-password', password);
  await tp.getByRole('button', { name: 'Se connecter', exact: true }).click();
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
const fall = await newContext('lecture refusée');
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
      detail: document.querySelector('.p-message:not([hidden]) .detail')?.textContent ?? '',
      tried: [...document.querySelectorAll('.p-message:not([hidden]) ol.tried li')].map((li) => li.textContent) }));
  };
  let state = await play(2);
  check('lecture refusée : demandée à nouveau, vidéo puis tout converti, et elle joue', asks.join(',') === '111,001,000' && state.t > 1.5 && !state.message,
    `demandes ${asks.join(', ')} (lecture directe, vidéo copiée, audio copié), ${state.t.toFixed(1)} s`);
  state = await play(99);
  check('lecture refusée partout : le dit, avec le fichier et l’erreur du navigateur à chaque essai', asks.join(',') === '111,001,000' && /même entièrement converti/.test(state.message)
    && /^MKV · H\.264/.test(state.detail) && state.tried.length === 3 && state.tried.every((line) => / : Erreur/.test(line)),
    `demandes ${asks.join(', ')}, « ${state.detail} » ; ${state.tried.join(' | ')}`);
  await fp.screenshot({ path: `${out}/17-refused.png` });
} catch (error) {
  check('lecture refusée : déroulé', false, error.message.split('\n')[0]);
}
check('lecture refusée : aucune erreur JavaScript', fallErrors.length === 0, fallErrors.slice(0, 3).join(' | '));
await fall.close();

// A stream that stops coming while Jellyfin answers (Wi-Fi that hiccups, a slow relay, a conversion stopped): no error,
// the browser waits forever. Here the film's first quarter arrives, then its next requests are held. Mira opens the
// stream again where it was; held again, « Lecture impossible », and « Réessayer » picks it up there.
const stuck = await newContext('flux bloqué');
const kp = await stuck.newPage();
const stuckErrors = [], held = [];
kp.on('pageerror', (e) => stuckErrors.push(e.message));
let holding = false, served = 0, infos = 0, releaseOnReopen = false;
const releaseHeld = () => { holding = false; for (const r of held.splice(0)) r.continue().catch(() => {}); };
kp.on('request', (r) => { if (/\/PlaybackInfo/i.test(r.url()) && r.method() === 'POST' && ++infos >= 2 && releaseOnReopen) releaseHeld(); });
await kp.route((url) => /\/videos\/[^/]+\/stream\.webm/i.test(url.pathname), async (route) => {
  if (!holding) return route.continue();
  if (served++ > 0) { held.push(route); return; }
  const response = await route.fetch(), body = await response.body(), cut = Math.floor(body.length / 4);
  return route.fulfill({ status: 206, body: body.subarray(0, cut),
    headers: { ...response.headers(), 'content-range': `bytes 0-${cut - 1}/${body.length}`, 'content-length': String(cut), 'accept-ranges': 'bytes' } });
});
try {
  await signIn(kp);
  const film = await kp.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    return (await api.browse({ types: 'Movie', search: 'Lueur Web' })).Items[0];
  });
  const state = () => kp.evaluate(() => {
    const v = document.querySelector('video');
    return { t: v?.currentTime ?? 0, paused: v?.paused ?? true, title: document.querySelector('.p-message:not([hidden]) h2')?.textContent ?? '',
      tried: [...document.querySelectorAll('.p-message:not([hidden]) ol.tried li')].map((li) => li.textContent) };
  });
  const playUntil = async (release, done, seconds) => {
    holding = true; served = 0; infos = 0; releaseOnReopen = release;
    await kp.evaluate(() => { location.hash = '#/'; }); await kp.waitForTimeout(1200);
    await kp.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.Id);
    let s = await state(), stalledAt = 0;
    for (let i = 0; i < seconds; i++) {
      await kp.waitForTimeout(1000);
      s = await state();
      stalledAt = Math.max(stalledAt, infos < 2 ? s.t : 0);
      if (done(s)) break;
    }
    return { ...s, stalledAt, infos };
  };
  // Opened again where it stopped, and then it comes: it plays on from there.
  let s = await playUntil(true, (x) => infos >= 2 && x.t > 12, 60);
  check('flux bloqué : rouvert où il s’était arrêté, il reprend', s.infos === 2 && s.stalledAt > 3 && s.t > 12 && !s.paused && !s.title,
    `arrêté à ${s.stalledAt.toFixed(1)} s, ${s.infos} demandes à Jellyfin, puis ${s.t.toFixed(1)} s`);
  // Held for good: said, with what was tried, and « Réessayer » plays from where it stopped.
  s = await playUntil(false, (x) => !!x.title, 100);
  await kp.screenshot({ path: `${out}/17-stuck.png` });
  check('flux bloqué pour de bon : « Lecture impossible », chaque essai dit', s.title === 'Lecture impossible' && s.infos === 2 && s.tried.length === 2
    && s.tried.every((line) => /Plus rien reçu pendant \d+ s/.test(line)), `${s.title || 'rien'} après ${s.infos} demandes ; ${s.tried.join(' | ')}`);
  const stoppedAt = s.stalledAt;
  releaseHeld();
  await kp.getByRole('button', { name: 'Réessayer' }).click();
  for (let i = 0; i < 30 && !((s = await state()).t > stoppedAt + 1); i++) await kp.waitForTimeout(1000);
  check('flux bloqué : « Réessayer » reprend où il s’était arrêté', s.t > stoppedAt + 1 && s.t < stoppedAt + 20 && !s.title, `arrêté à ${stoppedAt.toFixed(1)} s, puis ${s.t.toFixed(1)} s`);
} catch (error) {
  check('flux bloqué : déroulé', false, error.message.split('\n')[0]);
}
check('flux bloqué : aucune erreur JavaScript', stuckErrors.length === 0, stuckErrors.slice(0, 3).join(' | '));
for (const r of held.splice(0)) await r.abort().catch(() => {});
await stuck.close();

// Jellyfin gone in the middle of a film (restarting: its stream answers 503, System/Ping nothing): the browser pauses the
// video on that error, before or after saying so. That pause is not the person's: Jellyfin does not hear « en pause »
// (nor would the group), and once Jellyfin is back the film plays on where it was, not paused.
const gone = await newContext('serveur parti');
const gonePage = await gone.newPage();
const goneErrors = [], goneHeld = [], pausedReports = [];
gonePage.on('pageerror', (e) => goneErrors.push(e.message));
let goneAway = false, goneBack = false, quarter = 0;
gonePage.on('request', (r) => {
  if (!goneAway || r.method() !== 'POST' || !/Sessions\/Playing\/Progress/i.test(r.url())) return;
  try { if (JSON.parse(r.postData() ?? '{}').IsPaused) pausedReports.push(r.url()); } catch { /* not JSON */ }
});
// Its first quarter at once, the rest only once Jellyfin is back (before, the browser would take it all).
await gonePage.route((url) => /\/videos\/[^/]+\/stream\.webm/i.test(url.pathname), async (route) => {
  if (goneBack) return route.continue();
  if (goneAway) return route.fulfill({ status: 503, body: '' });
  const from = Number(/bytes=(\d+)/.exec(route.request().headers().range ?? '')?.[1] ?? 0);
  if (quarter && from >= quarter) { goneHeld.push(route); return; }
  const response = await route.fetch({ headers: { ...route.request().headers(), range: 'bytes=0-' } }), body = await response.body();
  quarter ||= Math.floor(body.length / 4);
  const part = body.subarray(from, quarter);
  return route.fulfill({ status: 206, body: part,
    headers: { ...response.headers(), 'content-range': `bytes ${from}-${quarter - 1}/${body.length}`, 'content-length': String(part.length), 'accept-ranges': 'bytes' } });
});
await gonePage.route((url) => /\/System\/Ping/i.test(url.pathname), (route) => (goneAway ? route.abort('connectionrefused') : route.continue()));
try {
  await signIn(gonePage);
  const film = await gonePage.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    return (await api.browse({ types: 'Movie', search: 'Lueur Web' })).Items[0];
  });
  const state = () => gonePage.evaluate(() => {
    const v = document.querySelector('video');
    return { t: v?.currentTime ?? 0, paused: v?.paused ?? true, message: document.querySelector('.p-message:not([hidden])')?.textContent ?? '' };
  });
  await gonePage.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.Id);
  let s = await state(), lost = false, goneAt = 0;
  for (let i = 0; i < 30 && !((s = await state()).t > 4); i++) await gonePage.waitForTimeout(1000);
  goneAway = true; goneAt = s.t;
  for (const r of goneHeld.splice(0)) await r.fulfill({ status: 503, body: '' }).catch(() => {});
  for (let i = 0; i < 40 && !(lost = /Connexion au serveur perdue/.test((s = await state()).message)); i++) await gonePage.waitForTimeout(500);
  await gonePage.screenshot({ path: `${out}/17-server-gone.png` });
  await gonePage.waitForTimeout(3000);
  goneAway = false; goneBack = true;
  for (let i = 0; i < 30 && !((s = await state()).t > goneAt + 2 && !s.paused); i++) await gonePage.waitForTimeout(1000);
  check('serveur parti pendant la lecture : « Connexion au serveur perdue », puis la lecture reprend où elle était, pas en pause',
    lost && s.t > goneAt + 2 && s.t < goneAt + 25 && !s.paused && !s.message, `${lost ? 'dit' : 'rien dit'}, parti à ${goneAt.toFixed(1)} s, puis ${s.t.toFixed(1)} s${s.paused ? ', en pause' : ''}`);
  check('serveur parti : la pause du navigateur sur l’erreur n’est pas dite à Jellyfin comme la sienne', pausedReports.length === 0, `${pausedReports.length} rapport(s) « en pause »`);
} catch (error) {
  check('serveur parti : déroulé', false, error.message.split('\n')[0]);
}
check('serveur parti : aucune erreur JavaScript', goneErrors.length === 0, goneErrors.slice(0, 3).join(' | '));
for (const r of goneHeld.splice(0)) await r.abort().catch(() => {});
await gone.close();

// The session ended by Jellyfin (signed out elsewhere, password changed): back to the sign-in screen, and nothing of
// the account stays, as after « Se déconnecter ».
const ended = await newContext('session refusée');
const ep = await ended.newPage();
const endedErrors = [];
ep.on('pageerror', (e) => endedErrors.push(e.message));
try {
  await signIn(ep);
  await ep.waitForTimeout(2500);
  await ep.locator('.tabbar a[data-tab="films"]').click(); await ep.waitForTimeout(1500);
  const before = await ep.evaluate(() => Object.keys(localStorage).filter((k) => /^mira\.(home|library)\./.test(k)));
  await ep.evaluate(async () => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Sessions/Logout`,
      { method: 'POST', headers: { Authorization: `MediaBrowser Client="Mira", Device="check", DeviceId="${localStorage.getItem('mira.device').replace(/"/g, '')}", Version="1", Token="${s.token}"` } });
  });
  // The next request Mira makes (here, back online) gets Jellyfin's 401.
  await ep.evaluate(() => window.dispatchEvent(new Event('online')));
  const out = await ep.waitForFunction(() => location.hash === '#/connexion', null, { timeout: 10000 }).then(() => true, () => false);
  await ep.waitForTimeout(800);
  const after = await ep.evaluate(() => ({ keys: Object.keys(localStorage).filter((k) => /^mira\.(home|library|session)/.test(k)), notice: document.querySelector('.form-error')?.textContent ?? '' }));
  check('session refusée par Jellyfin : retour à la connexion, rien du compte ne reste', out && before.length >= 2 && !after.keys.length && /Reconnecte-toi/.test(after.notice),
    `${before.length} écran(s) du compte avant, ${after.keys.join(', ') || 'aucun'} après, « ${after.notice} »`);
} catch (error) {
  check('session refusée : déroulé', false, error.message.split('\n')[0]);
}
check('session refusée : aucune erreur JavaScript', endedErrors.length === 0, endedErrors.slice(0, 3).join(' | '));
await ended.close();

// Another server at your server's address (another Wi-Fi with the same addresses): simulated by giving the answer of
// System/Info/Public another Id. Mira says so, and no request carries the session's token, nor asks for the page
// (which the phone would keep a month); back to your server, Mira picks up; a session from before Mira kept the
// server's Id takes the first one it meets; « me reconnecter » forgets the session without sending it.
const strange = await newContext('serveur étranger');
const sp = await strange.newPage();
const strangeErrors = [];
sp.on('pageerror', (e) => strangeErrors.push(e.message));
let token = '', carried = [], pageAsked = 0;
sp.on('request', (r) => {
  const url = new URL(r.url());
  if (token && ((r.headers().authorization ?? '').includes(token) || r.url().includes(token))) carried.push(`${r.method()} ${url.pathname.replace(/^.*?\/(?=[A-Z])/, '')}`);
  if (r.resourceType() === 'fetch' && /\/Mira\/$/.test(url.pathname)) pageAsked++;
});
const isPublicInfo = (url) => /\/System\/Info\/Public$/i.test(url.pathname);
const another = async (route) => {
  const response = await route.fetch();
  await route.fulfill({ response, json: { ...(await response.json()), Id: '0123456789abcdef0123456789abcdef' } });
};
const shownForeign = () => sp.locator('.foreign:not([hidden])').isVisible().catch(() => false);
try {
  await signIn(sp);
  await sp.waitForTimeout(2000);
  const session = await sp.evaluate(() => JSON.parse(localStorage.getItem('mira.session')));
  token = session.token;
  await sp.route(isPublicInfo, another);
  carried = []; pageAsked = 0;
  await sp.reload(); await sp.waitForTimeout(4000);
  const text = await sp.locator('.foreign').innerText().catch(() => '');
  check('serveur étranger : « Ce n’est pas ton serveur », aucune requête avec le jeton, page pas redemandée',
    await shownForeign() && /Ce n’est pas ton serveur/.test(text) && !carried.length && !pageAsked,
    `${await shownForeign() ? 'message affiché' : 'pas de message'}, ${carried.length ? `jeton envoyé : ${carried.slice(0, 3).join(', ')}` : 'jeton jamais envoyé'}, page redemandée ${pageAsked} fois`);
  await sp.screenshot({ path: `${out}/18-foreign.png` });
  await sp.locator('.foreign').getByRole('button', { name: 'Réessayer' }).click(); await sp.waitForTimeout(1500);
  const still = await shownForeign() && !carried.length;
  await sp.unroute(isPublicInfo, another);
  await sp.locator('.foreign').getByRole('button', { name: 'Réessayer' }).click(); await sp.waitForTimeout(3000);
  check('serveur étranger : redevenu le tien, Mira reprend', still && !await shownForeign() && carried.length > 0 && await sp.locator('#view .section').count() > 0,
    `${still ? 'toujours refusé tant qu’il est autre' : 'accepté trop tôt'}, puis ${await shownForeign() ? 'message resté' : 'message retiré'}, ${carried.length} requête(s) avec le jeton`);

  // Signed in before Mira kept the server's Id: no Id in the session, which takes this server's at the next start.
  await sp.evaluate(() => { const s = JSON.parse(localStorage.getItem('mira.session')); delete s.serverId; localStorage.setItem('mira.session', JSON.stringify(s)); });
  await sp.reload(); await sp.waitForTimeout(3500);
  const filled = await sp.evaluate(() => JSON.parse(localStorage.getItem('mira.session'))?.serverId ?? '');
  check('session d’avant (sans Id de serveur) : complétée, pas bloquée', filled === session.serverId && !await shownForeign() && await sp.locator('#view .section').count() > 0,
    `Id ${filled ? (filled === session.serverId ? 'retrouvé' : `autre (${filled})`) : 'absent'}`);

  // Back from the background on another network while a title plays: the check asked at once on the return finds
  // another server, the player closes, and the stream's addresses (they carry the session) go to it no more.
  const series = await sp.evaluate(async (name) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent(name)}`,
      { headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
    return (await r.json()).Items[0].Id;
  }, seriesName);
  await sp.evaluate((id) => { location.hash = `#/titre/${id}`; }, series); await sp.waitForTimeout(2500);
  await sp.locator('.detail-actions .btn.primary').click(); await sp.waitForTimeout(3500);
  const played = await sp.evaluate(() => { const v = document.querySelector('video'); return !!v && !v.paused && v.currentTime > 0.5; });
  await sp.route(isPublicInfo, another);
  carried = [];
  await sp.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await sp.waitForTimeout(2000);
  const during = carried.length;
  const closed = await sp.evaluate(() => ({ hash: location.hash, video: !!document.querySelector('video')?.getAttribute('src') }));
  carried = [];
  await sp.waitForTimeout(2500);
  check('serveur étranger pendant la lecture : vérifié au retour, lecteur fermé, plus rien ne lui est demandé',
    played && await shownForeign() && !closed.hash.startsWith('#/lecture/') && !closed.video && !carried.length,
    `${played ? 'lecture en cours' : 'pas de lecture'}, puis ${closed.hash.startsWith('#/lecture/') ? 'lecteur resté ouvert' : 'lecteur fermé'}, `
    + `${await shownForeign() ? 'message affiché' : 'pas de message'}, ${during} requête(s) avec le jeton le temps de la vérification, `
    + `${carried.length ? `puis : ${carried.slice(0, 3).join(', ')}` : 'aucune ensuite'}`);
  await sp.unroute(isPublicInfo, another);
  await sp.locator('.foreign').getByRole('button', { name: 'Réessayer' }).click(); await sp.waitForTimeout(2000);

  // Jellyfin reinstalled: « me reconnecter » forgets the session here, and never sends it to that server.
  await sp.route(isPublicInfo, another);
  await sp.reload(); await sp.waitForTimeout(3000);
  carried = [];
  await sp.locator('.foreign').getByRole('button', { name: /réinstallé Jellyfin/ }).click(); await sp.waitForTimeout(2000);
  const gone = await sp.evaluate(() => ({ hash: location.hash, session: localStorage.getItem('mira.session') }));
  check('serveur étranger : « me reconnecter » oublie la session sans la lui envoyer', gone.hash === '#/connexion' && !gone.session && !carried.length && !await shownForeign(),
    `${gone.hash}, session ${gone.session ? 'gardée' : 'oubliée'}, ${carried.length ? `jeton envoyé : ${carried.join(', ')}` : 'jeton jamais envoyé'}`);
  await sp.unroute(isPublicInfo, another);

  // Signed out while another server answers, Mira having asked nothing since: Jellyfin is not told (it is not yours),
  // and nothing covers the sign-in screen (no session left to protect).
  await signIn(sp);
  await sp.waitForTimeout(1500);
  token = await sp.evaluate(() => JSON.parse(localStorage.getItem('mira.session')).token);
  await sp.locator('.tabbar a[data-tab="settings"]').click(); await sp.waitForTimeout(1200);
  await sp.route(isPublicInfo, another);
  carried = [];
  await sp.getByRole('button', { name: 'Se déconnecter' }).click(); await sp.waitForTimeout(400);
  await sp.locator('.sheet-item.danger').click(); await sp.waitForTimeout(2000);
  const signedOut = await sp.evaluate(() => ({ hash: location.hash, session: localStorage.getItem('mira.session') }));
  check('serveur étranger : déconnexion sans rien lui envoyer, écran de connexion libre', signedOut.hash === '#/connexion' && !signedOut.session && !carried.length && !await shownForeign()
    && await sp.locator('#login-name, .users').first().isVisible().catch(() => false),
    `${signedOut.hash}, ${carried.length ? `jeton envoyé : ${carried.join(', ')}` : 'jeton jamais envoyé'}, ${await shownForeign() ? '« Ce n’est pas ton serveur » par-dessus' : 'rien par-dessus'}`);
  await sp.unroute(isPublicInfo, another);
} catch (error) {
  check('serveur étranger : déroulé', false, error.message.split('\n')[0]);
  await sp.screenshot({ path: `${out}/error-foreign.png` }).catch(() => {});
}
check('serveur étranger : aucune erreur JavaScript', strangeErrors.length === 0, strangeErrors.slice(0, 3).join(' | '));
await strange.close();

// Plain HTTP: the sign-in screen warns only away from home networks and Tailscale. The addresses as Mira sorts them,
// then the screen itself, at an Internet name and at the server's own address.
const plain = await newContext('connexion en clair');
const op = await plain.newPage();
try {
  await op.goto(`${base}#/connexion`); await op.waitForTimeout(1500);
  const sorted = await op.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { isLocalHost } = await import(`${root}api.js`);
    const local = ['localhost', '127.0.0.1', '10.0.0.5', '172.16.0.1', '172.31.255.254', '192.168.1.20', '169.254.10.1', '100.64.0.1', '100.127.255.254',
      '[::1]', '[fd7a:115c:a1e0::1]', '[fc00::1]', '[fe80::1]', 'nas', 'pc.local', 'pc.tail1234.ts.net', 'box.home.arpa', 'app.localhost',
      'livebox.home', 'pc.lan', 'nas.fritz.box', 'jellyfin.internal'];
    const internet = ['8.8.8.8', '11.0.0.1', '172.32.0.1', '100.128.0.1', '192.169.1.1', 'example.com', 'jellyfin.example.fr', 'local.example.com', 'ts.net.example.com',
      'lan.example.com', '[2001:db8::1]', '[2a01:e0a::1]', '[fec0::1]', '[fd::1]', '[fe8::1]'];
    return { wrong: [...local.filter((x) => !isLocalHost(x)), ...internet.filter((x) => isLocalHost(x))], count: local.length + internet.length };
  });
  const atHome = await op.locator('.hint.warn').count();
  const address = new URL(base); address.hostname = internetName;
  await op.goto(`${address.href}#/connexion`); await op.waitForTimeout(2000);
  const warning = await op.locator('.hint.warn').innerText().catch(() => '');
  await op.screenshot({ path: `${out}/19-login-internet.png` });
  check('connexion en HTTP depuis Internet : avertissement, jamais chez toi ni par Tailscale', !sorted.wrong.length && !atHome && /non chiffrée/.test(warning),
    `${sorted.count - sorted.wrong.length}/${sorted.count} adresses bien classées${sorted.wrong.length ? ` (mal : ${sorted.wrong.join(', ')})` : ''}, `
    + `${atHome ? 'averti' : 'pas d’avertissement'} à ${new URL(base).hostname}, ${warning ? 'averti' : 'pas d’avertissement'} à ${internetName}`);
} catch (error) {
  check('connexion en clair : déroulé', false, error.message.split('\n')[0]);
}
await plain.close();

// Through Tailscale (its name, as the QR code gives the PC's address): not « the home », so « Automatique » measures
// the connection, at the same time as the title loads rather than after it; a line that holds the file one and a half
// times over plays it as it is (full quality), whatever its bitrate.
const tailnet = await newContext('Tailscale');
try {
  const tp = await tailnet.newPage();
  const seen = [];
  tp.on('request', (r) => {
    if (/\/Playback\/BitrateTest/.test(r.url())) seen.push({ what: 'test', at: Date.now() });
    if (/\/PlaybackInfo/.test(r.url()) && r.method() === 'POST') seen.push({ what: 'info', at: Date.now(), max: JSON.parse(r.postData() || '{}').MaxStreamingBitrate });
  });
  tp.on('response', (r) => {
    const id = r.url().match(/\/Items\/([0-9a-f]{32})\?/)?.[1];
    if (id && r.request().method() === 'GET') seen.push({ what: 'item', at: Date.now(), id });
  });
  const address = new URL(base); address.hostname = tailscaleName;
  await signIn(tp, `${address.href}#/connexion`);
  const film = await tp.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    return (await api.browse({ types: 'Movie', search: 'Lueur Web' })).Items?.[0]?.Id ?? null;
  });
  // The title answers a second late, as from a phone away from home: measured while it loads, the test starts first.
  await tp.route(/\/Items\/[0-9a-f]{32}\?/, async (route) => {
    if (route.request().method() === 'GET') await new Promise((resolve) => setTimeout(resolve, 1000));
    await route.continue();
  });
  seen.length = 0;
  await tp.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film);
  const played = await tp.waitForFunction(() => (document.querySelector('video')?.currentTime ?? 0) > 0.5, null, { timeout: 20000 }).then(() => true, () => false);
  // The title's own answer: the home behind it may still be asking for its banner's series.
  const test = seen.find((x) => x.what === 'test'), item = seen.find((x) => x.what === 'item' && x.id === film), info = seen.find((x) => x.what === 'info');
  check('par Tailscale : connexion mesurée pendant le chargement du titre, fichier tel quel quand elle le tient',
    played && !!test && !!item && test.at <= item.at && info?.max === 120_000_000 && seen.filter((x) => x.what === 'test').length === 1,
    `${seen.map((x) => (x.id === film ? 'titre' : x.what) + (x.max ? ` ${x.max / 1e6} Mb/s` : '')).join(' → ')}${played ? ', lu' : ', pas lu'}`);
} catch (error) {
  check('par Tailscale : déroulé', false, error.message.split('\n')[0]);
}
await tailnet.close();

// « Lecture impossible »: every stream of a title refused (here, cut off while Jellyfin answers). The screen says what
// the file holds and each stream Mira tried; « Envoyer le détail à Jellyfin » leaves the report among Jellyfin's logs,
// where Mira for Windows reads it (System/Logs), without the token or the file's folders.
const broken = await newContext('lecture impossible');
try {
  const bp = await broken.newPage();
  await signIn(bp);
  const film = await bp.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { api } = await import(`${root}api.js`);
    const found = (await api.browse({ types: 'Movie', search: 'Lueur Web' })).Items?.[0];
    return found ? { id: found.Id, name: found.Name } : null;
  });
  await bp.route(/\/videos\/[0-9a-f]{32}\//i, (route) => route.abort());
  await bp.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.id);
  await bp.waitForSelector('.p-message:not([hidden]) h2', { timeout: 45000 });
  const screen = await bp.evaluate(() => ({
    title: document.querySelector('.p-message h2')?.textContent,
    media: document.querySelector('.p-message p.media')?.textContent ?? '',
    tried: [...document.querySelectorAll('.p-message ol.tried li')].map((li) => li.textContent),
  }));
  check('lecture impossible : ce que contient le fichier, puis chaque flux essayé',
    screen.title === 'Lecture impossible' && /^WebM · VP9 /.test(screen.media) && screen.tried.length >= 2
      && /^Fichier lu tel quel/.test(screen.tried[0]) && /^Tout converti/.test(screen.tried.at(-1)),
    `${screen.media} ; ${screen.tried.join(' | ')}`);
  await bp.screenshot({ path: `${out}/lecture-impossible.png` });
  await bp.getByRole('button', { name: 'Envoyer le détail à Jellyfin' }).click();
  const said = await bp.locator('.p-message p[role=status]').filter({ hasText: /Envoyé|refuse|abouti|Jellyfin a/ }).innerText({ timeout: 10000 }).catch(() => '');
  const logged = await bp.evaluate(async () => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const headers = { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` };
    const root = location.pathname.replace(/\/Mira\/?.*$/i, '');
    const files = await (await fetch(`${root}/System/Logs`, { headers })).json();
    const latest = files.filter((f) => /^upload_Mira_/.test(f.Name)).sort((a, b) => b.DateModified.localeCompare(a.DateModified))[0];
    const text = latest ? await (await fetch(`${root}/System/Logs/Log?name=${encodeURIComponent(latest.Name)}`, { headers })).text() : '';
    return { name: latest?.Name ?? '', text, token: s.token };
  });
  let sent = null;
  try { sent = JSON.parse(logged.text); } catch { /* not a report */ }
  check('lecture impossible : le détail envoyé à Jellyfin, lisible par Mira pour Windows, sans jeton ni dossier',
    /^Envoyé/.test(said) && sent?.mira === 'lecture-impossible' && sent.title === film.name && sent.media === screen.media
      && sent.attempts.length === screen.tried.length && /\.webm$/i.test(sent.file ?? '') && !logged.text.includes(logged.token)
      && !/\/media\/|ApiKey|api_key/i.test(logged.text),
    `${said} ; ${logged.name}`);
} catch (error) {
  check('lecture impossible : déroulé', false, error.message.split('\n')[0]);
}
await broken.close();

// The site's storage blocked (Safari's « Bloquer tous les cookies »): reading it throws. Mira opens on the sign-in
// screen, says what it needs, and still signs in, for as long as it stays open.
const blocked = await newContext('stockage bloqué');
await blocked.addInitScript(() => {
  for (const name of ['localStorage', 'sessionStorage']) {
    Object.defineProperty(window, name, { configurable: true, get() { throw new DOMException('The operation is insecure.', 'SecurityError'); } });
  }
});
const bp = await blocked.newPage();
const blockedErrors = [];
bp.on('pageerror', (e) => blockedErrors.push(e.message));
try {
  await bp.goto(base); await bp.waitForTimeout(2500);
  const gate = { hash: await bp.evaluate(() => location.hash), form: await bp.locator('form.login-form, .users').count(), note: await bp.locator('.hint.warn').innerText().catch(() => '') };
  check('stockage du site bloqué : Mira s’ouvre sur la connexion et dit ce qu’il lui faut', gate.hash === '#/connexion' && gate.form > 0 && /stockage du site/.test(gate.note),
    `${gate.hash || 'aucune adresse'}, ${gate.form ? 'formulaire' : 'pas de formulaire'}, ${gate.note ? 'message affiché' : 'pas de message'}`);
  await bp.screenshot({ path: `${out}/20-storage-blocked.png` });
  await signIn(bp);
  await bp.waitForTimeout(2500);
  const sections = await bp.locator('#view .section').count();
  check('stockage du site bloqué : connexion possible, Accueil affiché, aucune erreur JavaScript', sections > 0 && !blockedErrors.length,
    `${sections} rangée(s)${blockedErrors.length ? `, ${blockedErrors.slice(0, 2).join(' | ')}` : ''}`);
} catch (error) {
  check('stockage du site bloqué : déroulé', false, error.message.split('\n')[0]);
  await bp.screenshot({ path: `${out}/error-storage.png` }).catch(() => {});
}
await blocked.close();

// A page where Jellyfin's answer should be (a Wi-Fi's sign-in page, simulated): said in French, never in the parser's
// English; and ping() does not take that page for Jellyfin back.
const portal = await newContext('portail Wi-Fi');
const wp = await portal.newPage();
const portalErrors = [];
wp.on('pageerror', (e) => portalErrors.push(e.message));
try {
  await signIn(wp);
  await wp.waitForTimeout(1500);
  const wifiPage = '<!doctype html><html><body><h1>Bienvenue sur le Wi-Fi de la gare</h1></body></html>';
  const fake = (route) => route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: wifiPage });
  const searched = (url) => /\/Items$/i.test(url.pathname) && /[?&]searchTerm=/i.test(url.search);
  await wp.route(searched, fake);
  await wp.locator('.tabbar a[data-tab="search"]').click(); await wp.waitForTimeout(800);
  await wp.fill('input[type=search]', 'Lueur'); await wp.waitForTimeout(2000);
  const said = await wp.locator('#view .search [role="alert"]').innerText().catch(() => '');
  await wp.unroute(searched, fake);
  const pinged = () => wp.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { ping } = await import(`${root}api.js`);
    return ping();
  });
  const isPing = (url) => /\/System\/Ping$/i.test(url.pathname);
  await wp.route(isPing, fake);
  const duringPortal = await pinged();
  await wp.unroute(isPing, fake);
  const afterPortal = await pinged();
  await wp.screenshot({ path: `${out}/21-wifi-page.png` });
  check('réponse HTML (portail Wi-Fi) : dite en français, pas prise pour Jellyfin revenu',
    /Wi-Fi/.test(said) && !/JSON|Unexpected|token/i.test(said) && duringPortal === false && afterPortal === true && !portalErrors.length,
    `« ${said.replace(/\s+/g, ' ').trim() || 'aucun message'} », ping ${duringPortal ? 'trompé' : 'pas trompé'} par la page, puis ${afterPortal ? 'Jellyfin reconnu' : 'Jellyfin pas reconnu'}`);
} catch (error) {
  check('réponse HTML (portail Wi-Fi) : déroulé', false, error.message.split('\n')[0]);
}
await portal.close();

// « Nouvel épisode » when Mira opens (news.js): a series followed (Courte Web, made a favourite here) with episodes not
// watched, added after the mark kept on the phone. The first look only sets the mark; an older one announces them,
// « Voir » opens the series at the first of them, and the mark moves on: nothing the next time.
const news = await newContext('nouveaux épisodes');
const np = await news.newPage();
const newsErrors = [];
np.on('pageerror', (e) => newsErrors.push(e.message));
const asNews = (path, method = 'GET') => np.evaluate(async ([p, m]) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { method: m, headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
  const t = await r.text(); return t ? JSON.parse(t) : null;
}, [path, method]);
let followed = null;
try {
  await signIn(np);
  const uid = await np.evaluate(() => JSON.parse(localStorage.getItem('mira.session')).userId);
  followed = (await asNews(`Items?recursive=true&includeItemTypes=Series&searchTerm=${encodeURIComponent(seriesName)}`)).Items[0];
  const all = (await asNews(`Shows/${followed.Id}/Episodes?userId=${uid}`)).Items;
  await asNews(`UserPlayedItems/${all.at(-1).Id}?userId=${uid}`, 'DELETE');
  await asNews(`UserFavoriteItems/${followed.Id}?userId=${uid}`, 'POST');
  const unwatched = (await asNews(`Shows/${followed.Id}/Episodes?userId=${uid}`)).Items.filter((x) => !x.UserData?.Played);
  const key = `mira.home.${uid}.episodes`;
  const mark = () => np.evaluate((k) => localStorage.getItem(k), key);
  const firstMark = await np.waitForFunction((k) => localStorage.getItem(k), key, { timeout: 15000 }).then(() => mark(), () => null);
  const firstSaid = await np.locator('.toast', { hasText: 'épisode' }).count();
  await np.evaluate((k) => localStorage.setItem(k, '0'), key);
  await np.reload();
  const notice = np.locator('.toast', { hasText: 'épisode' });
  await notice.waitFor({ timeout: 15000 }).catch(() => {});
  const said = (await notice.innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
  await np.screenshot({ path: `${out}/24-new-episodes.png` });
  await notice.getByRole('button', { name: 'Voir' }).click({ timeout: 3000 }).catch(() => {});
  // Several series: a sheet of them; one: the series at once.
  await np.locator('.sheet-item', { hasText: followed.Name }).click({ timeout: 2000 }).catch(() => {});
  await np.waitForTimeout(1500);
  const opened = await np.evaluate(() => location.hash);
  const movedOn = Number(await mark()) > 0;
  await np.reload(); await np.waitForTimeout(5000);
  const again = await np.locator('.toast', { hasText: 'épisode' }).count();
  const wanted = `#/titre/${followed.Id}?episode=${unwatched[0]?.Id}`;
  check('nouveaux épisodes : rien au premier passage, puis annoncés une fois, « Voir » ouvre la série à l’épisode',
    !!firstMark && Number(firstMark) > 0 && !firstSaid && /Courte Web|séries que tu suis/.test(said) && opened === wanted && movedOn && !again && !newsErrors.length,
    `premier passage : ${firstMark ? 'repère posé' : 'aucun repère'}${firstSaid ? ', annoncé à tort' : ''} ; « ${said || 'rien'} » ; ${opened}${opened === wanted ? '' : ` au lieu de ${wanted}`} ; ${again ? 'annoncé de nouveau' : 'pas de nouveau'}${newsErrors.length ? ` ; ${newsErrors[0]}` : ''}`);
} catch (error) {
  check('nouveaux épisodes : déroulé', false, error.message.split('\n')[0]);
}
if (followed) await asNews(`UserFavoriteItems/${followed.Id}`, 'DELETE').catch(() => {});
await news.close();

// « Tes choix gardés sur ton Jellyfin »: the languages and the intros chosen on one phone, a series' tracks and a title
// removed there, found on another phone signed in to the same account; what that one changes comes back to the first.
const choiceA = await newContext('choix gardés (1)'), choiceB = await newContext('choix gardés (2)');
const ca = await choiceA.newPage(), cb = await choiceB.newPage();
const choiceErrors = [];
for (const p of [ca, cb]) p.on('pageerror', (e) => choiceErrors.push(e.message));
// What Jellyfin keeps of them, read as the account; and the settings a phone uses.
const keptOn = (p, body) => p.evaluate(async (send) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const address = `${location.pathname.replace(/\/Mira\/?.*$/i, '')}/DisplayPreferences/mira?userId=${s.userId}&client=mira`;
  const headers = { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"`, 'Content-Type': 'application/json' };
  const prefs = await (await fetch(address, { headers })).json();
  if (send) { delete prefs.CustomPrefs['mira.choices']; await fetch(address, { method: 'POST', headers, body: JSON.stringify(prefs) }); return null; }
  return JSON.parse(prefs.CustomPrefs?.['mira.choices'] ?? '{}');
}, !!body);
const settingsOf = (p) => p.evaluate(() => JSON.parse(localStorage.getItem('mira.settings') ?? '{}'));
const until = async (test, seconds = 12) => { for (let i = 0; i < seconds * 4; i++) { if (await test().catch(() => false)) return true; await new Promise((r) => setTimeout(r, 250)); } return false; };
const setOn = (p, changes) => p.evaluate(async (all) => {
  const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
  const { settings } = await import(`${root}session.js`);
  for (const [key, value] of Object.entries(all)) settings.set(key, value);
}, changes);
const FRENCH = 'fre,fra,fr,eng,en', JAPANESE = 'jpn,ja,fre,fra,fr,eng,en';
try {
  await signIn(ca);
  await ca.locator('.tabbar a[data-tab="settings"]').click(); await ca.waitForTimeout(1200);
  await ca.locator('#view .settings .item', { hasText: 'Langue audio' }).click(); await ca.waitForTimeout(500);
  await ca.locator('.sheet-item', { hasText: 'Français, puis anglais' }).click(); await ca.waitForTimeout(300);
  await ca.getByRole('switch', { name: /Passer les intros et les récaps/ }).click();
  // A series' tracks and a title removed from Continuer à regarder, as the player and the home page keep them.
  await setOn(ca, {
    seriesTracks: { 'serie-du-controle': { audio: 'jpn', subtitle: 'fre', forced: false, audioTitle: null, subtitleTitle: 'Complet' } },
    hiddenResume: { 'titre-du-controle': new Date().toISOString() },
  });
  const sent = await until(async () => {
    const k = await keptOn(ca);
    return k.settings?.audioLanguages?.value === FRENCH && k.settings?.autoSkip?.value === true && k.tracks?.['serie-du-controle']?.subtitleTitle === 'Complet' && k.hidden?.['titre-du-controle']?.hidden;
  });
  await signIn(cb);
  const arrived = await until(async () => { const s = await settingsOf(cb); return s.audioLanguages === FRENCH && !!s.hiddenResume?.['titre-du-controle']; });
  await cb.locator('.tabbar a[data-tab="settings"]').click(); await cb.waitForTimeout(1200);
  const audioLine = (await cb.locator('#view .settings .item', { hasText: 'Langue audio' }).innerText()).replace(/\s+/g, ' ');
  const skipOn = await cb.getByRole('switch', { name: /Passer les intros et les récaps/ }).getAttribute('aria-checked');
  const tracksB = (await settingsOf(cb)).seriesTracks?.['serie-du-controle'];
  await cb.screenshot({ path: `${out}/25-choices.png` });
  // The other phone puts the title back and goes back to Japanese: the first finds it when it opens again.
  await setOn(cb, { hiddenResume: {} });
  await cb.locator('#view .settings .item', { hasText: 'Langue audio' }).click(); await cb.waitForTimeout(500);
  await cb.locator('.sheet-item', { hasText: 'Japonais, puis français' }).click(); await cb.waitForTimeout(300);
  const back = await until(async () => { const k = await keptOn(cb); return k.settings?.audioLanguages?.value === JAPANESE && k.hidden?.['titre-du-controle']?.hidden === false; });
  await ca.reload();
  const returned = await until(async () => { const s = await settingsOf(ca); return s.audioLanguages === JAPANESE && !s.hiddenResume?.['titre-du-controle'] && s.autoSkip === true; });
  check('choix gardés sur Jellyfin : langues, intros, pistes d’une série et titre retiré retrouvés sur l’autre téléphone, et retour',
    sent && arrived && /Français, puis anglais/.test(audioLine) && skipOn === 'true' && tracksB?.subtitle === 'fre' && tracksB?.subtitleTitle === 'Complet' && back && returned && !choiceErrors.length,
    `${sent ? 'envoyés' : 'pas envoyés'} ; ${arrived ? 'arrivés' : 'pas arrivés'} (« ${audioLine} », intros ${skipOn === 'true' ? 'passées' : 'jouées'}, pistes ${tracksB ? `${tracksB.audio}/${tracksB.subtitle} ${tracksB.subtitleTitle}` : 'absentes'}) ; `
    + `${back ? 'retour envoyé' : 'retour pas envoyé'} ; ${returned ? 'revenu sur le premier' : 'pas revenu sur le premier'}${choiceErrors.length ? ` ; ${choiceErrors[0]}` : ''}`);
} catch (error) {
  check('choix gardés sur Jellyfin : déroulé', false, error.message.split('\n')[0]);
}
// The next phones of these checks start from Mira's defaults again.
await keptOn(ca, true).catch(() => {});
await choiceA.close(); await choiceB.close();

// A large library (3,000 films, simulated: Jellyfin's answers for Films are made up here). The grid asks for its
// cards without their summaries, page by page as it scrolls; a refresh asks for the first page only, and the grid
// keeps its cards and its scroll.
const big = await newContext('bibliothèque nombreuse');
const gp = await big.newPage();
const bigErrors = [], asked = [];
gp.on('pageerror', (e) => bigErrors.push(e.message));
const films = (url) => /\/Items$/i.test(url.pathname) && /[?&]includeItemTypes=Movie(&|$)/i.test(url.search);
const library = (route) => {
  const query = new URL(route.request().url()).searchParams;
  const from = Number(query.get('startIndex') ?? 0), limit = Number(query.get('limit') ?? 100);
  asked.push({ from, limit, fields: query.get('fields') ?? '' });
  const Items = [];
  for (let i = from; i < Math.min(3000, from + limit); i++) {
    Items.push({ Id: (i + 1).toString(16).padStart(32, 'a'), Name: `Film ${i + 1}`, Type: 'Movie', ProductionYear: 1990 + (i % 30), RunTimeTicks: 60_000_000_000,
      ImageTags: {}, UserData: { Played: false, PlaybackPositionTicks: 0, IsFavorite: false } });
  }
  return route.fulfill({ json: { Items, TotalRecordCount: 3000, StartIndex: from } });
};
try {
  await signIn(gp);
  await gp.waitForTimeout(1500);
  await gp.route(films, library);
  await gp.locator('.tabbar a[data-tab="films"]').click(); await gp.waitForTimeout(2000);
  for (let i = 0; i < 5; i++) { await gp.evaluate(() => scrollTo(0, document.documentElement.scrollHeight)); await gp.waitForTimeout(800); }
  await gp.waitForTimeout(1000);
  const cards = () => gp.locator('#view .library .grid > a.card').count();
  const before = { cards: await cards(), y: await gp.evaluate(() => scrollY), count: await gp.locator('#view .library-count').innerText(), asked: asked.length };
  check('bibliothèque nombreuse (3 000 films) : cartes sans résumé, par pages de 60',
    before.cards >= 240 && asked.length >= 4 && asked.every((x) => x.limit <= 60 && !/Overview|PrimaryImageAspectRatio/i.test(x.fields)) && /3000 films/.test(before.count),
    `${before.cards} cartes, ${asked.length} pages demandées (${[...new Set(asked.map((x) => x.limit))].join(', ')} titres, champs « ${asked[0]?.fields ?? '?'} »), « ${before.count} »`);
  await gp.evaluate(() => window.dispatchEvent(new Event('online')));
  await gp.waitForTimeout(2000);
  const fresh = asked.slice(before.asked), after = { cards: await cards(), y: await gp.evaluate(() => scrollY) };
  const firsts = fresh.filter((x) => x.from === 0);
  check('bibliothèque nombreuse : actualisée par sa première page seulement, cartes et défilement gardés',
    firsts.length === 1 && firsts[0].limit === 60 && fresh.every((x) => x.limit <= 60) && after.cards >= before.cards && Math.abs(after.y - before.y) <= 2 && !bigErrors.length,
    `${fresh.map((x) => `${x.from}+${x.limit}`).join(', ') || 'rien demandé'} ; ${before.cards} → ${after.cards} cartes, défilement ${Math.round(before.y)} → ${Math.round(after.y)} px`
    + `${bigErrors.length ? `, ${bigErrors.slice(0, 2).join(' | ')}` : ''}`);
  await gp.screenshot({ path: `${out}/22-large-library.png` });
} catch (error) {
  check('bibliothèque nombreuse : déroulé', false, error.message.split('\n')[0]);
}
await big.close();

// « Regarder ensemble » (Jellyfin's SyncPlay): two phones, two accounts (the second made here), one starts a group and
// gets its invitation, the other opens the link signed out, signs in and is in the group; Lecture on the first opens the
// title on both, playing together; a pause and a jump on the first reach the second; the second away a few seconds
// (iOS suspends a page in another app: Jellyfin lets its session go) comes back into the group in step; leaving the
// group on the second, after Lecture of another title asked before replacing the group's (joined instead); the group
// on the second's Home, joined from there. Then the host away until Jellyfin ends the empty group: the link waits for
// it, and the group made again by the host on its return is joined.
const togetherA = await newContext('ensemble (1)'), togetherB = await newContext('ensemble (2)');
const ta = await togetherA.newPage(), tb = await togetherB.newPage();
const togetherErrors = [];
for (const p of [ta, tb]) p.on('pageerror', (e) => togetherErrors.push(e.message));
const videoState = (p) => p.evaluate(() => { const v = document.querySelector('video'); return v ? { t: v.currentTime, paused: v.paused } : null; });
// Jellyfin's socket of each phone, through Playwright: closed both ways and refused while the phone is away.
const phones = new Map();
for (const p of [ta, tb]) {
  const state = { open: [], away: false };
  phones.set(p, state);
  await p.routeWebSocket(/\/socket\?/, (ws) => { if (state.away) { ws.close(); return; } state.open.push({ ws, server: ws.connectToServer() }); });
}
const suspend = async (p) => {
  const state = phones.get(p);
  state.away = true; await p.context().setOffline(true);
  for (const { ws, server } of state.open.splice(0)) { server.close(); ws.close(); }
};
const resume = async (p) => { phones.get(p).away = false; await p.context().setOffline(false); };
const as = (who) => (path, method = 'GET', body = null) => who.evaluate(async ([p, m, b]) => {
  const session = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { method: m, body: b ? JSON.stringify(b) : undefined,
    headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${session.token}"` } });
  const t = await r.text(); return t ? JSON.parse(t) : null;
}, [path, method, body]);
try {
  await signIn(ta);
  const asA = as(ta), asB = as(tb);
  // A friend's account (kept from a run before when there is one): all the libraries, as Jellyfin gives a new account.
  await asA('Users/New', 'POST', { Name: 'Ami Mira', Password: 'ami-check' }).catch(() => null);
  // A film long enough for a jump of 10 s (the episodes last 8).
  const film = (await asA('Items?recursive=true&includeItemTypes=Movie&searchTerm=Lueur')).Items[0];
  const line = (p) => p.locator('#view .group-line');
  await ta.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await ta.waitForTimeout(2500);
  await line(ta).click(); await ta.waitForTimeout(800);
  await ta.locator('.sheet-item', { hasText: 'Créer un groupe' }).click();
  // A new group is empty: its invitation opens at once, with the link to send.
  await ta.locator('.invite-link').waitFor({ timeout: 10000 }).catch(() => {});
  const link = (await ta.locator('.invite-link').innerText().catch(() => '')).trim();
  const steps = await ta.locator('.invite-steps li').count();
  await ta.keyboard.press('Escape'); await ta.waitForTimeout(500);
  const created = (await line(ta).innerText()).replace(/\s+/g, ' ').trim();
  const groupId = (await ta.evaluate(() => JSON.parse(sessionStorage.getItem('mira.together') || 'null')?.id).catch(() => null)) ?? '';
  // The friend opens it signed out (here, on this address: the link's own is the server's on the network): Mira asks to
  // sign in, says why, then is in the group.
  await tb.goto(`${base}${link.slice(link.indexOf('#'))}`); await tb.waitForTimeout(1500);
  const invitedLead = (await tb.locator('.lead').first().innerText().catch(() => '')).trim();
  if (await tb.locator('.users').count()) await tb.getByRole('button', { name: 'Autre compte' }).click();
  await tb.fill('#login-name', 'Ami Mira'); await tb.fill('#login-password', 'ami-check');
  await tb.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await tb.waitForFunction(() => /Tu es dans/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 15000 }).catch(() => {});
  const joinPage = (await tb.locator('#view h1').innerText().catch(() => '')).trim();
  await tb.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await tb.waitForTimeout(2500);
  await tb.waitForFunction(() => /2 personnes/.test([...document.querySelectorAll('#view .group-line')].map((x) => x.textContent).join(' ')), null, { timeout: 10000 }).catch(() => {});
  const joinedLine = (await line(tb).innerText()).replace(/\s+/g, ' ').trim();
  check('ensemble : un groupe créé, son lien d’invitation ouvert sur l’autre téléphone, connexion puis dans le groupe',
    /Ensemble : Groupe de .+ · 1 personne/.test(created) && groupId && link.endsWith(`/Mira/#/ensemble/${groupId}?hote=${encodeURIComponent(user)}`) && steps >= 3
      && invitedLead.startsWith(`${user} t’invite à regarder ensemble`) && /^Tu es dans « Groupe de .+ »$/.test(joinPage) && /Ensemble : Groupe de .+ · 2 personnes/.test(joinedLine)
      && created.includes('Personne d’autre pour l’instant') && (joinedLine.includes(`${user} et Ami Mira`) || joinedLine.includes(`Ami Mira et ${user}`)),
    `« ${created} » ; lien ${link || 'absent'} (${steps} étapes) ; « ${invitedLead} » ; « ${joinPage} » ; « ${joinedLine} »`);
  await ta.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.Id);
  const followed = await tb.waitForFunction((id) => location.hash.startsWith(`#/lecture/${id}`), film.Id, { timeout: 15000 }).then(() => true, () => false);
  await ta.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime > 1; }, null, { timeout: 25000 }).catch(() => {});
  await tb.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime > 1; }, null, { timeout: 15000 }).catch(() => {});
  await ta.waitForTimeout(1500);
  let [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  check('ensemble : Lecture sur l’un ouvre le titre sur l’autre, lus au même moment',
    followed && a && b && !a.paused && !b.paused && Math.abs(a.t - b.t) < 1,
    `${followed ? 'titre ouvert sur l’autre' : 'l’autre n’a rien ouvert'} ; ${a ? `${a.t.toFixed(2)} s${a.paused ? ' (en pause)' : ''}` : 'pas de vidéo'} et ${b ? `${b.t.toFixed(2)} s${b.paused ? ' (en pause)' : ''}` : 'pas de vidéo'}`);
  await ta.evaluate(() => document.querySelector('video').pause());
  await tb.waitForFunction(() => document.querySelector('video')?.paused, null, { timeout: 8000 }).catch(() => {});
  await tb.waitForTimeout(800);
  [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  const pill = (p) => p.evaluate(() => { const e = document.querySelector('.group-pill'); return e && !e.hidden ? `${e.classList.contains('alert') ? '!' : ''}${e.textContent.trim()}` : ''; });
  const pausedPill = await pill(tb);
  check('ensemble : une pause sur l’un met l’autre en pause, au même endroit, et le dit', a?.paused && b?.paused && Math.abs(a.t - b.t) < 1 && pausedPill === '!En pause pour le groupe',
    `${a?.paused ? 'en pause' : 'en lecture'} à ${a?.t.toFixed(2)} s, ${b?.paused ? 'en pause' : 'en lecture'} à ${b?.t.toFixed(2)} s ; « ${pausedPill} »`);
  const before = b?.t ?? 0;
  await ta.keyboard.press('ArrowRight');
  await tb.waitForFunction((t) => (document.querySelector('video')?.currentTime ?? 0) > t + 6, before, { timeout: 10000 }).catch(() => {});
  await ta.keyboard.press(' ');
  await tb.waitForFunction(() => !document.querySelector('video')?.paused, null, { timeout: 10000 }).catch(() => {});
  await ta.waitForTimeout(1500);
  [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  check('ensemble : un saut de 10 s puis la lecture sur l’un, suivis par l’autre', a && b && !a.paused && !b.paused && b.t > before + 6 && Math.abs(a.t - b.t) < 1,
    `de ${before.toFixed(2)} s à ${b?.t.toFixed(2)} s ; écart ${a && b ? Math.abs(a.t - b.t).toFixed(2) : '?'} s ; ${b?.paused ? 'l’autre en pause' : 'les deux en lecture'}`);
  const playingPill = await pill(ta);
  // The second phone slow to load (said by its own Mira, as its player does): Jellyfin holds the group for it, and the
  // first, ready, says whom it waits for; the second ready, both play on. Its video stands still meanwhile, as a
  // stalled stream does, without a pause (rate 0): played on, it would be ahead by the wait.
  const sendAsB = (what) => tb.evaluate(async (kind) => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const [{ api }, { together }] = await Promise.all([import(`${root}api.js`), import(`${root}together.js`)]);
    const v = document.querySelector('video');
    v.playbackRate = kind === 'Ready' ? 1 : 0;
    await api.syncSend(kind, { When: new Date().toISOString(), PositionTicks: Math.round(v.currentTime * 1e7), IsPlaying: kind === 'Ready', PlaylistItemId: together.queue.playlistItemId });
  }, what);
  await sendAsB('Buffering');
  await ta.waitForFunction(() => /On attend/.test(document.querySelector('.group-pill')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
  const waitingPill = await pill(ta);
  const held = await videoState(ta);
  await ta.screenshot({ path: `${out}/23-together-pill.png` });
  await sendAsB('Ready');
  await ta.waitForFunction(() => !document.querySelector('video')?.paused, null, { timeout: 10000 }).catch(() => {});
  await tb.waitForFunction(() => !document.querySelector('video')?.paused, null, { timeout: 10000 }).catch(() => {});
  await ta.waitForTimeout(1000);
  [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  const playsPill = await pill(ta);
  check('ensemble : l’autre téléphone qui charge : le premier attend et dit qui, puis les deux reprennent ensemble',
    [`Ensemble · ${user} et Ami Mira`, `Ensemble · Ami Mira et ${user}`].includes(playingPill) && waitingPill === '!On attend Ami Mira…' && held?.paused && a && b && !a.paused && !b.paused && Math.abs(a.t - b.t) < 1 && playsPill === playingPill,
    `« ${playingPill} » ; « ${waitingPill} »${held?.paused ? ' en pause' : ' sans pause'} ; puis ${a ? `${a.t.toFixed(2)} s` : 'pas de vidéo'} et ${b ? `${b.t.toFixed(2)} s` : 'pas de vidéo'}, « ${playsPill} »`);
  // The second phone away a few seconds: Jellyfin lets its session go, then takes it back as a newcomer, holding the
  // group until it is ready where the group is. The first says who left and who came back.
  await ta.evaluate(() => {
    window.seenPills = [];
    new MutationObserver(() => { const t = document.querySelector('.group-pill')?.textContent?.trim(); if (t && window.seenPills.at(-1) !== t) window.seenPills.push(t); })
      .observe(document.documentElement, { subtree: true, childList: true, characterData: true });
  });
  await suspend(tb); await tb.waitForTimeout(3000);
  const without = (await asA('SyncPlay/List'))?.find((g) => g.GroupId === groupId)?.Participants ?? [];
  await resume(tb);
  let back = [];
  for (let i = 0; i < 20 && back.length < 2; i++) { await tb.waitForTimeout(500); back = (await asA('SyncPlay/List'))?.find((g) => g.GroupId === groupId)?.Participants ?? []; }
  await ta.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused; }, null, { timeout: 30000 }).catch(() => {});
  await tb.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused; }, null, { timeout: 30000 }).catch(() => {});
  await ta.waitForTimeout(2000);
  [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  const seenPills = await ta.evaluate(() => window.seenPills ?? []);
  check('ensemble : l’autre téléphone parti quelques secondes revient dans le groupe, au même moment que le premier',
    without.length === 1 && back.length === 2 && a && b && !a.paused && !b.paused && Math.abs(a.t - b.t) < 1 && seenPills.includes('Ami Mira a rejoint le groupe'),
    `${without.length} puis ${back.length} dans le groupe ; ${a ? `${a.t.toFixed(2)} s${a.paused ? ' (en pause)' : ''}` : 'pas de vidéo'} et ${b ? `${b.t.toFixed(2)} s${b.paused ? ' (en pause)' : ''}` : 'pas de vidéo'} ; ${seenPills.join(' | ')}`);
  await tb.keyboard.press('Escape');
  await tb.waitForFunction(() => location.hash.startsWith('#/titre/'), null, { timeout: 8000 }).catch(() => {});
  await tb.waitForTimeout(1500);
  // Jellyfin no longer waits for the one who closed: a jump on the other still plays on.
  const seekFrom = (await videoState(ta))?.t ?? 0;
  await ta.keyboard.press('ArrowLeft');
  // The jump goes to the group, which pauses it there until it is ready, then starts it: the time of that round trip.
  await ta.waitForTimeout(2000);
  await ta.waitForFunction((t) => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime < t - 4; }, seekFrom, { timeout: 10000 }).catch(() => {});
  await ta.waitForTimeout(1000);
  const stillA = await videoState(ta);
  // Lecture in a group: on the group's title it joins it; on another, it would replace it for everyone, so it asks
  // first, and offers to join the group's title instead (taken here: back in step with the first).
  const lecture = (p) => p.locator('#view .detail-actions .btn.primary').innerText().then((t) => t.replace(/\s+/g, ' ').trim(), () => '');
  const onGroupTitle = await lecture(tb);
  const other = (await asB('Items?recursive=true&includeItemTypes=Movie&limit=20')).Items.find((x) => x.Id !== film.Id);
  await tb.evaluate((id) => { location.hash = `#/titre/${id}`; }, other.Id); await tb.waitForTimeout(2500);
  const onOther = await lecture(tb);
  await tb.locator('#view .detail-actions .btn.primary').click();
  await tb.locator('.sheet-question').waitFor({ timeout: 8000 }).catch(() => {});
  const question = (await tb.locator('.sheet-question').textContent().catch(() => '') ?? '').trim();
  await tb.screenshot({ path: `${out}/23-together-replace.png` });
  const stayed = (await tb.evaluate(() => location.hash)).startsWith('#/titre/');
  await tb.locator('.sheet-item', { hasText: 'Regarder avec le groupe' }).click();
  await tb.waitForFunction((id) => location.hash.startsWith(`#/lecture/${id}`) && !document.querySelector('video')?.paused, film.Id, { timeout: 20000 }).catch(() => {});
  await tb.waitForTimeout(2500);
  [a, b] = await Promise.all([videoState(ta), videoState(tb)]);
  const rejoinedTitle = (await tb.evaluate(() => location.hash)).startsWith(`#/lecture/${film.Id}`);
  check('ensemble : Lecture d’un autre titre demande avant de remplacer celui du groupe, et propose de le rejoindre',
    onGroupTitle === 'Regarder avec le groupe' && /^(Lancer|Reprendre) pour le groupe/.test(onOther) && question === `Remplacer « ${film.Name} » pour tout le groupe ?` && stayed
      && rejoinedTitle && a && b && !a.paused && !b.paused && Math.abs(a.t - b.t) < 1,
    `« ${onGroupTitle} », « ${onOther} » ; « ${question} »${stayed ? '' : ' (titre changé sans attendre)'} ; ${rejoinedTitle ? 'titre du groupe rouvert' : 'titre du groupe pas rouvert'}, ${a ? `${a.t.toFixed(2)} s` : 'pas de vidéo'} et ${b ? `${b.t.toFixed(2)} s${b.paused ? ' (en pause)' : ''}` : 'pas de vidéo'}`);
  await tb.keyboard.press('Escape');
  await tb.waitForFunction(() => location.hash.startsWith('#/titre/'), null, { timeout: 8000 }).catch(() => {});
  await tb.waitForTimeout(1500);
  await line(tb).click(); await tb.waitForTimeout(800);
  await tb.locator('.sheet-item', { hasText: 'Quitter le groupe' }).click();
  await tb.waitForTimeout(1500);
  const leftLine = (await line(tb).innerText()).replace(/\s+/g, ' ').trim();
  check('ensemble : fermer le lecteur laisse les autres regarder, et sauter ; quitter le groupe', stillA && !stillA.paused && stillA.t < seekFrom - 4 && leftLine === 'Regarder ensemble' && !togetherErrors.length,
    `${stillA && !stillA.paused ? 'l’autre continue' : 'l’autre s’est arrêté'}, de ${seekFrom.toFixed(2)} s à ${stillA?.t.toFixed(2)} s ; « ${leftLine} »${togetherErrors.length ? ` ; ${togetherErrors.slice(0, 2).join(' | ')}` : ''}`);
  // Home, out of the group: the group open on Jellyfin is a line, a tap from joining it (its title then opens, as
  // the group plays it); once in it, the line goes.
  const homeGroup = tb.locator('#view .home-group');
  await tb.evaluate(() => { location.hash = '#/'; });
  await homeGroup.waitFor({ timeout: 10000 }).catch(() => {});
  const offered = (await homeGroup.innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
  await tb.screenshot({ path: `${out}/23-together-home.png` });
  await homeGroup.click().catch(() => {});
  await tb.waitForFunction((id) => /Tu es dans/.test(document.querySelector('#view h1')?.textContent ?? '') || location.hash.startsWith(`#/lecture/${id}`), film.Id, { timeout: 15000 }).catch(() => {});
  const fromHome = (await tb.evaluate(() => location.hash)).startsWith(`#/lecture/${film.Id}`) ? 'titre du groupe ouvert' : (await tb.locator('#view h1').innerText().catch(() => '')).trim();
  let inGroup = [];
  for (let i = 0; i < 10 && inGroup.length < 2; i++) { inGroup = (await asA('SyncPlay/List'))?.find((g) => g.GroupId === groupId)?.Participants ?? []; if (inGroup.length < 2) await tb.waitForTimeout(500); }
  await tb.evaluate(() => { location.hash = '#/'; }); await tb.waitForTimeout(2500);
  const lineInGroup = await homeGroup.count();
  // Out of it again, for what follows (the host alone, then away).
  await tb.evaluate(async () => {
    const root = document.querySelector('script[type=module]').src.replace(/app\.js.*$/, '');
    const { together } = await import(`${root}together.js`);
    await together.leave();
  });
  await tb.waitForTimeout(1000);
  check('ensemble : l’accueil montre le groupe ouvert sur Jellyfin, à rejoindre d’un toucher, et plus une fois dedans',
    offered.includes(`Groupe de ${user}`) && offered.includes('1 personne') && offered.endsWith('Rejoindre') && (fromHome === 'titre du groupe ouvert' || /^Tu es dans « Groupe de .+ »$/.test(fromHome))
      && inGroup.length === 2 && lineInGroup === 0,
    `« ${offered} » ; ${fromHome || 'rien'} ; ${inGroup.length} dans le groupe ; ligne ${lineInGroup ? 'encore là' : 'partie'} une fois dedans`);
  await ta.screenshot({ path: `${out}/23-together.png` });
  // The host alone, away (its iPhone in Messages to send the link): Jellyfin ends the empty group. A link without its
  // host (sent before) has nothing to find; the link of now waits, then joins the group the host makes again on its return.
  await ta.keyboard.press('Escape');
  await ta.waitForFunction(() => location.hash.startsWith('#/titre/'), null, { timeout: 8000 }).catch(() => {});
  await ta.waitForTimeout(1000);
  await ta.evaluate(() => {
    window.seenToasts = [];
    new MutationObserver(() => { const t = document.querySelector('.toast-layer')?.textContent?.trim(); if (t && window.seenToasts.at(-1) !== t) window.seenToasts.push(t); })
      .observe(document.documentElement, { subtree: true, childList: true, characterData: true });
  });
  await suspend(ta);
  let ended = false;
  for (let i = 0; i < 20 && !ended; i++) { await tb.waitForTimeout(500); ended = !((await asB('SyncPlay/List')) ?? []).some((g) => g.GroupId === groupId); }
  await tb.evaluate((id) => { location.hash = `#/ensemble/${id}`; }, groupId);
  await tb.waitForFunction(() => /pas pu être rejoint/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
  const oldLink = (await tb.locator('#view .lead').innerText().catch(() => '')).trim();
  await tb.evaluate((hash) => { location.hash = hash; }, link.slice(link.indexOf('#')));
  await tb.waitForFunction(() => /pas encore lancé/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 8000 }).catch(() => {});
  const waitingPage = (await tb.locator('#view h1').innerText().catch(() => '')).trim();
  await tb.screenshot({ path: `${out}/24-together-waiting.png` });
  await resume(ta);
  await tb.waitForFunction(() => /Tu es dans/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 40000 }).catch(() => {});
  const rejoinedPage = (await tb.locator('#view h1').innerText().catch(() => '')).trim();
  await ta.waitForFunction(() => /2 personnes/.test(document.querySelector('#view .group-line')?.textContent ?? ''), null, { timeout: 10000 }).catch(() => {});
  const hostLine = (await line(ta).innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
  const hostToasts = await ta.evaluate(() => window.seenToasts ?? []);
  const groupsNow = (await asB('SyncPlay/List')) ?? [];
  check('ensemble : l’hôte parti, Jellyfin finit le groupe ; le lien attend, puis rejoint le groupe refait au retour de l’hôte',
    ended && /n’existe plus/.test(oldLink) && waitingPage === `${user} n’a pas encore lancé la soirée` && /^Tu es dans « Groupe de .+ »$/.test(rejoinedPage)
      && /Ensemble : Groupe de .+ · 2 personnes/.test(hostLine) && !hostToasts.some((t) => /n’existe plus/.test(t)) && groupsNow.length === 1 && groupsNow[0].GroupId !== groupId,
    `${ended ? 'groupe fini par Jellyfin' : 'groupe toujours là'} ; ancien lien : « ${oldLink} » ; « ${waitingPage} » puis « ${rejoinedPage} » ; hôte : « ${hostLine} »${hostToasts.length ? ` (${hostToasts.join(' | ')})` : ''} ; ${groupsNow.length} groupe(s)`);
} catch (error) {
  check('ensemble : déroulé', false, error.message.split('\n')[0]);
}
await togetherA.close(); await togetherB.close();

// « Inviter par lien » (an administrator): a link for someone without an account, made from « Liens d'invités » (the
// address of the links first, the one Tailscale Funnel gives: here a made-up one, the link is opened on this address).
// The guest's phone opens it: Mira reloads on the extension's gateway, joins the host's group and stays there (any
// other screen leads back to it); Lecture on the host's plays on both. Taken back, the guest is out at once, and the
// link no longer opens.
const hostCtx = await newContext('invité (hôte)'), guestCtx = await newContext('invité');
const inviter = await hostCtx.newPage(), invitee = await guestCtx.newPage();
const guestErrors = [];
for (const p of [inviter, invitee]) p.on('pageerror', (e) => guestErrors.push(e.message));
try {
  await signIn(inviter);
  const asHost = as(inviter);
  for (const old of (await asHost('Mira/invites'))?.invites ?? []) await asHost(`Mira/invites/${old.id}`, 'DELETE');
  await inviter.evaluate(() => { location.hash = '#/invites'; }); await inviter.waitForTimeout(2000);
  const addressInput = inviter.locator('.guests-page input[type=url]');
  if (await addressInput.count()) {
    await addressInput.fill('https://mira-check.tail0000.ts.net');
    await inviter.getByRole('button', { name: 'Enregistrer' }).click(); await inviter.waitForTimeout(1000);
  }
  await inviter.fill('.guests-page input[type=text]', 'Léa');
  await inviter.getByRole('button', { name: 'Créer le lien' }).click();
  await inviter.locator('.guests-page .invite-link').waitFor({ timeout: 10000 }).catch(() => {});
  const guestLink = (await inviter.locator('.guests-page .invite-link').innerText().catch(() => '')).trim();
  const listed = (await inviter.locator('.guests-page .item', { hasText: 'Léa' }).innerText().catch(() => '')).replace(/\s+/g, ' ').trim();
  await inviter.screenshot({ path: `${out}/32-guest-link.png` });
  check('invité : un lien fait depuis « Liens d’invités », listé, pas encore ouvert',
    /^https:\/\/[^/]+\/Mira\/#\/invite\/[A-Za-z0-9_-]{22}$/.test(guestLink) && listed.includes('Pas encore ouvert'), `${guestLink || 'pas de lien'} ; « ${listed} »`);
  const film = (await asHost('Items?recursive=true&includeItemTypes=Movie&searchTerm=Lueur')).Items[0];
  await inviter.evaluate((id) => { location.hash = `#/titre/${id}`; }, film.Id); await inviter.waitForTimeout(2500);
  await inviter.locator('#view .group-line').click(); await inviter.waitForTimeout(800);
  await inviter.locator('.sheet-item', { hasText: 'Créer un groupe' }).click();
  await inviter.locator('.invite-link').first().waitFor({ timeout: 10000 }).catch(() => {});
  await inviter.keyboard.press('Escape'); await inviter.waitForTimeout(500);
  const guestHash = guestLink.slice(guestLink.indexOf('#'));
  await invitee.goto(`${base}${guestHash}`);
  await invitee.waitForFunction(() => /Tu es dans/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 20000 }).catch(() => {});
  const guestPage = (await invitee.locator('#view h1').innerText().catch(() => '')).trim();
  const guestSession = await invitee.evaluate(() => JSON.parse(localStorage.getItem('mira.session') ?? 'null'));
  await invitee.screenshot({ path: `${out}/33-guest-joined.png` });
  await invitee.evaluate(() => { location.hash = '#/reglages'; }); await invitee.waitForTimeout(1000);
  const confined = await invitee.evaluate(() => location.hash);
  // Through the gateway, the guest's session sees the host's group only and nothing of the library's lists.
  const gateway = (path) => invitee.evaluate(async ([p, t]) => (await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Mira/guest/${p}`, {
    headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${t}"` } })).status, [path, guestSession?.token ?? '']);
  const [usersStatus, itemsStatus] = [await gateway('Users'), await gateway('Items')];
  check('invité : le lien ouvert sur un autre téléphone, sans compte, entre dans le groupe de l’hôte et y reste',
    /^Tu es dans « Groupe de .+ »$/.test(guestPage) && guestSession?.guest === true && confined === '#/ensemble' && usersStatus === 404 && itemsStatus === 404,
    `« ${guestPage} » ; session ${guestSession?.guest ? 'd’invité' : 'ordinaire'} ; #/reglages → ${confined} ; Users ${usersStatus}, Items ${itemsStatus}`);
  await inviter.evaluate((id) => { location.hash = `#/lecture/${id}?debut=1`; }, film.Id);
  const guestFollowed = await invitee.waitForFunction((id) => location.hash.startsWith(`#/lecture/${id}`), film.Id, { timeout: 20000 }).then(() => true, () => false);
  for (const p of [inviter, invitee]) await p.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused && v.currentTime > 1; }, null, { timeout: 25000 }).catch(() => {});
  await invitee.waitForTimeout(1500);
  const [h, g] = await Promise.all([videoState(inviter), videoState(invitee)]);
  const guestSrc = await invitee.evaluate(() => document.querySelector('video')?.currentSrc ?? '');
  check('invité : Lecture chez l’hôte lance le titre chez l’invité, par la passerelle, au même moment',
    guestFollowed && h && g && !h.paused && !g.paused && Math.abs(h.t - g.t) < 1 && guestSrc.includes('/Mira/guest/'),
    `${guestFollowed ? 'titre ouvert' : 'rien ouvert'} ; ${h ? `${h.t.toFixed(2)} s` : 'pas de vidéo'} et ${g ? `${g.t.toFixed(2)} s` : 'pas de vidéo'} ; ${guestSrc.replace(/\?.*/, '')}`);
  const invite = ((await asHost('Mira/invites'))?.invites ?? []).find((x) => x.name === 'Léa');
  await inviter.evaluate(() => { location.hash = '#/invites'; }); await inviter.waitForTimeout(2000);
  await inviter.getByRole('button', { name: 'Retirer le lien de Léa' }).click(); await inviter.waitForTimeout(600);
  await inviter.locator('.sheet-item', { hasText: 'Retirer le lien' }).click();
  await invitee.waitForFunction(() => /finie/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 20000 }).catch(() => {});
  const endedPage = (await invitee.locator('#view h1').innerText().catch(() => '')).trim();
  await invitee.screenshot({ path: `${out}/34-guest-ended.png` });
  const oldToken = await invitee.evaluate(async (t) => (await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Mira/guest/Users/Me`, { headers: { Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${t}"` } })).status, guestSession?.token ?? '');
  await invitee.goto(`${base}${guestHash}`);
  await invitee.waitForFunction(() => /pas marché/.test(document.querySelector('#view h1')?.textContent ?? ''), null, { timeout: 10000 }).catch(() => {});
  const reopened = (await invitee.locator('#view .lead').first().innerText().catch(() => '')).trim();
  check('invité : le lien retiré, l’invité est dehors tout de suite et le lien ne marche plus',
    invite?.opened && endedPage === 'La soirée est finie pour ce lien' && oldToken === 401 && /ne marche plus/.test(reopened),
    `${invite?.opened ? `ouvert sur ${invite.device}` : 'jamais ouvert'} ; « ${endedPage} » ; ancienne session : ${oldToken} ; rouvert : « ${reopened} »`);
  await asHost('SyncPlay/Leave', 'POST').catch(() => null);
} catch (error) {
  check('invité : déroulé', false, error.message.split('\n')[0]);
}
check('invité : aucune erreur JavaScript', !guestErrors.length, guestErrors.join(' | ') || 'aucune');
await hostCtx.close(); await guestCtx.close();

// « Télécharger sur le PC serveur » (an administrator): Mira for Windows on the PC of Jellyfin is played here by a fake
// agent, which visits the extension's relay as it does (Mira/torlink/agent): it answers the search of each category
// (films, shows, anime: one search of the page asks for the three; a result found twice is shown once), takes
// the download and reports it under way, pauses and cancels it as asked. Another account is refused.
const relayServer = base.replace(/\/Mira\/?.*$/i, '');
const relayAuth = (id, token) => `MediaBrowser Client="check", Device="check", DeviceId="${id}", Version="1"${token ? `, Token="${token}"` : ''}`;
const tokenFor = async (id, name, pw) => (await (await fetch(`${relayServer}/Users/AuthenticateByName`, {
  method: 'POST', headers: { Authorization: relayAuth(id), 'Content-Type': 'application/json' }, body: JSON.stringify({ Username: name, Pw: pw }),
})).json()).AccessToken;
const picked = 'c12fe1c06bba254a9dc9f519b335aa7c1367a88a', pasted = '0123456789abcdef0123456789abcdef01234567', weaker = 'fedcba9876543210fedcba9876543210fedcba98', episode = 'abcdefabcdefabcdefabcdefabcdefabcdefabcd';
const magnetOf = (hash, name) => `magnet:?xt=urn:btih:${hash}&dn=${encodeURIComponent(name)}`;
const fake = { searches: [], requests: [], controls: [], downloads: [], seen: [], stop: false };
const agentToken = await tokenFor('check-agent', user, password).catch(() => null);
const agentLoop = (async () => {
  while (!fake.stop && agentToken) {
    const report = { name: 'PC-CHECK', accepting: true, problem: null, downloads: fake.downloads, placed: [], searches: fake.searches.splice(0), requests: fake.requests.splice(0), controls: fake.controls.splice(0) };
    const answer = await fetch(`${relayServer}/Mira/torlink/agent`, { method: 'POST', headers: { Authorization: relayAuth('check-agent', agentToken), 'Content-Type': 'application/json' }, body: JSON.stringify(report) }).catch(() => null);
    const work = answer?.ok ? await answer.json() : null;
    for (const s of work?.searches ?? []) {
      fake.seen.push(`search ${s.category} ${s.query}`);
      const film = { id: picked, name: 'Mira Check 2026 1080p WEB-DL', size: Math.round(1.4 * 2 ** 30), seeders: 42, leechers: 3, source: 'check', magnet: magnetOf(picked, 'Mira Check 2026 1080p WEB-DL'), added: null };
      fake.searches.push({ id: s.id, state: 'done', results: {
        // Better shared but worse: the 1080p WEB-DL is put forward all the same, the HDTV folded under it.
        movies: [{ id: weaker, name: 'Mira.Check.2026.720p.HDTV.x264-GRP', size: 2 ** 30, seeders: 900, leechers: 3, source: 'check', magnet: magnetOf(weaker, 'Mira.Check.2026.720p.HDTV.x264-GRP'), added: null }, film],
        tv: [{ id: episode, name: 'Mira.Check.S01E02.1080p.WEB-DL.H264-GRP', size: 2 ** 30, seeders: 30, leechers: 1, source: 'check', magnet: magnetOf(episode, 'Mira.Check.S01E02.1080p.WEB-DL.H264-GRP'), added: null }, film],
      }[s.category] ?? [], error: null });
    }
    for (const r of work?.requests ?? []) {
      fake.seen.push(`add ${r.name}`);
      fake.requests.push({ id: r.id, state: 'sent', error: null });
      fake.downloads.push({ id: /btih:([0-9a-f]{40})/i.exec(r.magnet)[1].toLowerCase(), name: r.name, status: 'downloading', progress: 37, speed: 2.5 * 2 ** 20, peers: 4, size: Math.round(1.4 * 2 ** 30), downloaded: Math.round(0.37 * 1.4 * 2 ** 30), eta: 380 });
    }
    for (const c of work?.controls ?? []) {
      fake.seen.push(`${c.action} ${c.target}`);
      if (c.action === 'delete') fake.downloads = fake.downloads.filter((d) => d.id !== c.target);
      else { const d = fake.downloads.find((x) => x.id === c.target); if (d) d.status = c.action === 'pause' ? 'paused' : 'downloading'; }
      fake.controls.push({ id: c.id, state: 'done', error: null });
    }
    await new Promise((resolve) => setTimeout(resolve, 400));
  }
})();
const dlContext = await newContext('télécharger');
const dp = await dlContext.newPage();
const dlErrors = [];
dp.on('pageerror', (e) => dlErrors.push(e.message));
const dl = { ready: '', hit: '', kinds: '', kept: '', sent: false, row: '', paused: '', cancelled: false, refused: '', pasted: '', other: 0, entry: false, failure: '' };
try {
  await signIn(dp);
  await dp.locator('.tabbar a[data-tab="settings"]').click();
  await dp.getByRole('link', { name: /Télécharger sur le PC serveur/ }).click();
  const screen = dp.locator('.torlink-page');
  await dp.waitForFunction(() => /PC-CHECK est prêt/.test(document.querySelector('.torlink-page .page-sub')?.textContent ?? ''), null, { timeout: 15000 });
  dl.ready = (await screen.locator('.page-sub').innerText()).trim();
  await screen.locator('input[type=search]').fill('  --mira   check ');
  await screen.getByRole('button', { name: 'Chercher' }).click();
  // The film, among the results of every category (the episode of the show of the same name comes first).
  const film = screen.locator('.torlink-results .torlink-title[aria-label^="Mira Check (2026)"]');
  const hit = film.locator('.torlink-item').first();
  await hit.waitFor({ timeout: 15000 });
  dl.hit = `${(await film.locator('.torlink-head').innerText()).trim()} | ${(await hit.innerText()).trim()} | ${await screen.locator('.torlink-results .torlink-more').count()}`.replace(/\s*\n\s*/g, ' | ');
  dl.kinds = (await screen.locator('.torlink-results .torlink-title').evaluateAll((all) => all.map((x) => x.getAttribute('aria-label')))).join(' / ');
  await dp.screenshot({ path: `${out}/25-download-search.png` });
  // iOS reloads a Home Screen app left in the background: the search comes back from the relay, not asked again.
  const asked = fake.seen.length;
  await dp.reload();
  await hit.waitFor({ timeout: 15000 });
  dl.kept = `${await screen.locator('input[type=search]').inputValue()} | ${(await screen.locator('.torlink-results .torlink-title').evaluateAll((all) => all.map((x) => x.getAttribute('aria-label')))).join(' / ')} | ${fake.seen.length - asked}`;
  await hit.click();
  await dp.getByRole('menuitem', { name: 'Télécharger', exact: true }).click();
  const row = screen.locator(`.torlink-list [data-key="d:${picked}"]`);
  await row.waitFor({ timeout: 15000 });
  dl.row = (await row.innerText()).trim().replace(/\s*\n\s*/g, ' | ');
  // The result sent says so, and is not sent twice.
  dl.sent = await hit.evaluate((b) => b.disabled && b.classList.contains('torlink-sent') && /Envoyé/.test(b.textContent));
  await dp.screenshot({ path: `${out}/26-download-list.png` });
  await row.getByRole('button', { name: /^Actions pour/ }).click();
  await dp.getByRole('menuitem', { name: 'Mettre en pause' }).click();
  await dp.waitForFunction((key) => /En pause/.test(document.querySelector(`.torlink-page [data-key="${key}"] .torlink-line`)?.textContent ?? ''), `d:${picked}`, { timeout: 15000 });
  dl.paused = (await row.locator('.torlink-line').innerText()).trim();
  await row.getByRole('button', { name: /^Actions pour/ }).click();
  await dp.getByRole('menuitem', { name: /Annuler le téléchargement/ }).click();
  dl.cancelled = await row.waitFor({ state: 'detached', timeout: 15000 }).then(() => true, () => false);
  // A magnet link pasted: refused when it is not one, then sent.
  await screen.getByRole('button', { name: 'Coller un lien magnet' }).click();
  await dp.fill('#torlink-magnet', 'https://example.test/film');
  await dp.getByRole('button', { name: 'Envoyer au PC serveur' }).click();
  dl.refused = (await dp.locator('.sheet .form-error').innerText()).trim();
  await dp.fill('#torlink-magnet', magnetOf(pasted, 'Collé Check'));
  await dp.getByRole('button', { name: 'Envoyer au PC serveur' }).click();
  const sent = screen.locator(`.torlink-list [data-key="d:${pasted}"]`);
  await sent.waitFor({ timeout: 15000 });
  dl.pasted = (await sent.innerText()).trim().replace(/\s*\n\s*/g, ' | ');
} catch (error) { dl.failure = error.message.split('\n')[0]; }
// Another account (the friend of « Regarder ensemble » above, made again when it is missing): the relay refuses it.
const friendName = 'Ami Mira', friendPassword = 'ami-check';
let friendToken = await tokenFor('check-friend', friendName, friendPassword).catch(() => null);
if (!friendToken && agentToken) {
  await fetch(`${relayServer}/Users/New`, { method: 'POST', headers: { Authorization: relayAuth('check-agent', agentToken), 'Content-Type': 'application/json' }, body: JSON.stringify({ Name: friendName, Password: friendPassword }) }).catch(() => null);
  friendToken = await tokenFor('check-friend', friendName, friendPassword).catch(() => null);
}
dl.other = friendToken ? (await fetch(`${relayServer}/Mira/torlink`, { headers: { Authorization: relayAuth('check-friend', friendToken) } })).status : 0;
fake.stop = true; await agentLoop;
await dlContext.close();
check('télécharger sur le PC serveur (administrateur) : recherche faite par le PC dans les films, les séries et les animes à la fois, gardée au rechargement, résultat marqué « Envoyé », téléchargement suivi, pause, annulation, lien magnet collé',
  /PC-CHECK est prêt/.test(dl.ready) && ['movies', 'tv', 'anime'].every((kind) => fake.seen.includes(`search ${kind} mira check`))
    && dl.hit === 'Mira Check (2026) | FILM | 1080p · WEB-DL | Meilleur choix | 1,4 Go · 42 sources · check | Mira Check 2026 1080p WEB-DL | 1' && dl.kinds === 'Mira Check · S01E02 · Série / Mira Check (2026) · Film' && dl.kept === `mira check | ${dl.kinds} | 0`
    && /37 % · 530 Mo sur 1,4 Go · 2,5 Mo\/s · reste 6 min · 4 pairs/.test(dl.row) && dl.sent && /^En pause/.test(dl.paused) && dl.cancelled && fake.seen.includes(`delete ${picked}`)
    && /pas un lien magnet/.test(dl.refused) && /Collé Check/.test(dl.pasted) && !dlErrors.length,
  dl.failure || `« ${dl.ready} » ; « ${dl.hit} » ; « ${dl.kinds} » ; gardée : « ${dl.kept} » ;${dl.sent ? '' : ' pas marqué « Envoyé » ;'} « ${dl.row} » ; « ${dl.paused} » ; ${dl.cancelled ? 'annulé' : 'resté'} ; « ${dl.refused} » ; « ${dl.pasted} » ; ${fake.seen.join(', ')}${dlErrors.length ? ` ; ${dlErrors.join(' | ')}` : ''}`);
check('télécharger sur le PC serveur : refusé à un compte qui n’est pas administrateur', dl.other === 403, String(dl.other));
// « Reprendre un peu avant »: a film left at 34 s starts 5 s before (the default), and exactly there once « Non » is
// chosen in Réglages. Its position is put back to nothing for the rest of the run.
const resumed = await newContext('reprendre un peu avant');
const rp = await resumed.newPage();
let resumeFilm = null;
const asUser = (path, method = 'GET', body) => rp.evaluate(async ([p, m, b]) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p.replace('{user}', s.userId)}`, { method: m, body: b === undefined ? undefined : JSON.stringify(b),
    headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
  const t = await r.text(); return t ? JSON.parse(t) : null;
}, [path, method, body]);
try {
  await signIn(rp);
  resumeFilm = (await asUser('Items?userId={user}&recursive=true&includeItemTypes=Movie&searchTerm=Lueur')).Items[0];
  const startsAt = async () => {
    await asUser(`UserItems/${resumeFilm.Id}/UserData?userId={user}`, 'POST', { PlaybackPositionTicks: 34 * 10_000_000 });
    await rp.evaluate((id) => { location.hash = `#/lecture/${id}`; }, resumeFilm.Id);
    const handle = await rp.waitForFunction(() => { const v = document.querySelector('video'); return v && !v.paused && v.readyState >= 2 ? v.currentTime : null; }, null, { timeout: 20000 });
    const at = await handle.jsonValue();
    await rp.locator('.p-top button[aria-label="Retour"]').click({ force: true }); await rp.waitForTimeout(1200);
    return at;
  };
  const before = await startsAt();
  await rp.evaluate(() => { location.hash = '#/reglages'; });
  await rp.getByRole('button', { name: /Reprendre un peu avant/ }).click();
  await rp.getByRole('menuitemradio', { name: /^Non/ }).click();
  await rp.waitForTimeout(400);
  const exactly = await startsAt();
  check('reprise un peu avant : 5 s avant le point d’arrêt, pile dessus une fois « Non » choisi',
    before >= 28.8 && before < 31 && exactly >= 33.8 && exactly < 36, `${before.toFixed(1)} s, puis ${exactly.toFixed(1)} s (arrêté à 34 s)`);
} catch (error) {
  check('reprise un peu avant : déroulé', false, error.message.split('\n').slice(0, 3).join(' / ')); await rp.screenshot({ path: `${out}/error-resume.png` }).catch(() => {});
} finally {
  if (resumeFilm) await asUser(`UserItems/${resumeFilm.Id}/UserData?userId={user}`, 'POST', { PlaybackPositionTicks: 0 }).catch(() => {});
}
await resumed.close();

// « Chercher des sous-titres »: Jellyfin's subtitle extension simulated (none is installed here, and Open Subtitles
// needs an account), the rest real. The film without subtitles gets the line of its tracks, the search says what
// leaves the server, the file found is sent to Jellyfin, which keeps it (here its media folder is read-only: in its own
// data, the library switched) and Lecture starts with it. Then the file is deleted and the library put back.
const subtitled = await newContext('sous-titres');
const sb = await subtitled.newPage();
const subtitleErrors = [];
sb.on('pageerror', (e) => subtitleErrors.push(e.message));
const asAdmin = (path, method = 'GET', body) => sb.evaluate(async ([p, m, b]) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${p}`, { method: m, body: b === undefined ? undefined : JSON.stringify(b),
    headers: { 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
  const t = await r.text(); return t ? JSON.parse(t) : null;
}, [path, method, body]);
let subtitleFilm = null, subtitleLibrary = null;
try {
  await sb.route(/\/Libraries\/AvailableOptions/i, (route) => route.fulfill({ json: { SubtitleFetchers: [{ Name: 'Open Subtitles', DefaultEnabled: true }] } }));
  await sb.route(/\/RemoteSearch\/Subtitles\//i, (route) => route.fulfill({ json: [
    { Id: 'os-traduit', Name: 'Chapitres.Web.2024.MT', ProviderName: 'Open Subtitles', Format: 'srt', ThreeLetterISOLanguageName: 'fre', DownloadCount: 9000, MachineTranslated: true },
    { Id: 'os-exact', Name: 'Chapitres.Web.2024.WEB', ProviderName: 'Open Subtitles', Format: 'srt', ThreeLetterISOLanguageName: 'fre', DownloadCount: 1234, IsHashMatch: true },
  ] }));
  await sb.route(/\/Providers\/Subtitles\/Subtitles\//i, (route) => route.fulfill({ contentType: 'text/plain', body: '1\n00:00:01,000 --> 00:00:04,000\nBonjour depuis Mira\n\n' }));
  await signIn(sb);
  subtitleFilm = (await asAdmin('Items?recursive=true&includeItemTypes=Movie&searchTerm=Chapitres')).Items[0];
  const userId = JSON.parse(await sb.evaluate(() => localStorage.getItem('mira.session'))).userId;
  const ancestors = await asAdmin(`Items/${subtitleFilm.Id}/Ancestors?userId=${userId}`);
  subtitleLibrary = (await asAdmin('Library/VirtualFolders')).find((f) => ancestors.some((a) => a.Id === f.ItemId));
  await sb.evaluate((id) => { location.hash = `#/titre/${id}`; }, subtitleFilm.Id);
  await sb.locator('.tracks-line').waitFor({ timeout: 10000 });
  await sb.locator('.tracks-line').click();
  await sb.getByRole('menuitem', { name: /Chercher des sous-titres/ }).click();
  await sb.locator('.subtitle-search .sheet-item').first().waitFor({ timeout: 10000 });
  const note = await sb.locator('.subtitle-search .group-note').first().innerText();
  const rows = await sb.locator('.subtitle-search .sheet-item').allInnerTexts();
  const languages = await sb.locator('.subtitle-search .chip').allInnerTexts();
  await sb.screenshot({ path: `${out}/30-subtitle-search.png` });
  await sb.locator('.subtitle-search .sheet-item').first().click();
  await sb.locator('.toast').filter({ hasText: /Sous-titres gardés sur Jellyfin/ }).waitFor({ timeout: 40000 });
  const said = await sb.locator('.toast').last().innerText();
  const line = await sb.locator('.tracks-line').innerText();
  const streams = (await asAdmin(`Items/${subtitleFilm.Id}`)).MediaSources[0].MediaStreams.filter((s) => s.Type === 'Subtitle');
  await sb.screenshot({ path: `${out}/31-subtitle-kept.png` });
  check('sous-titres : chercher avec l’extension de Jellyfin, garder le fichier sur Jellyfin, choisi pour Lecture',
    /Open Subtitles.*le nom du fichier et son empreinte/.test(note) && /^Chapitres\.Web\.2024\.WEB/.test(rows[0] ?? '') && /pour ce fichier/.test(rows[0] ?? '') && /traduction automatique/.test(rows[1] ?? '')
      && languages.join(',') === 'Français,Anglais' && /en français/.test(said) && /Sous-titres : Français/.test(line.replace(/\s+/g, ' ')) && streams.length === 1 && streams[0].IsExternal,
    `${rows.length} résultats (${rows.map((r) => r.split('\n')[0]).join(', ')}), langues ${languages.join(', ')} ; « ${said.split('\n')[0]} » ; ${line.replace(/\s+/g, ' ')} ; ${streams.length} piste(s) sur Jellyfin`);
} catch (error) {
  check('sous-titres : déroulé', false, error.message.split('\n')[0]);
  await sb.screenshot({ path: `${out}/error-subtitles.png` }).catch(() => {});
} finally {
  // Left as it was found, for the next run.
  if (subtitleFilm) {
    const added = (await asAdmin(`Items/${subtitleFilm.Id}`).catch(() => null))?.MediaSources?.[0]?.MediaStreams?.filter((s) => s.Type === 'Subtitle' && s.IsExternal) ?? [];
    for (const s of added.reverse()) await asAdmin(`Videos/${subtitleFilm.Id}/Subtitles/${s.Index}`, 'DELETE').catch(() => {});
  }
  if (subtitleLibrary?.LibraryOptions) await asAdmin('Library/VirtualFolders/LibraryOptions', 'POST', { Id: subtitleLibrary.ItemId, LibraryOptions: subtitleLibrary.LibraryOptions }).catch(() => {});
}
check('sous-titres : aucune erreur JavaScript', subtitleErrors.length === 0, subtitleErrors.slice(0, 3).join(' | '));
await subtitled.close();

// Mira in English, its default: a phone whose browser is in English (Chromium's en-US) gets English everywhere, and
// Réglages → Language switches to French and back, the page reloaded on the same screen.
const inEnglish = await newContext('anglais', 'en-US');
const lp = await inEnglish.newPage();
const englishErrors = [];
lp.on('pageerror', (e) => englishErrors.push(e.message));
// Words of Mira's French that English never shows (the test media's names have none of them).
const FRENCH_WORDS = /(?<!\p{L})(Accueil|Réglages|Recherche|Séries|Reprendre|Lecture|Connexion|Se connecter|Serveur|Regarder|Langue|Épisodes?|Saisons?|Qualité|Déconnecter)(?!\p{L})/u;
// The libraries of the test media are named « Séries » and « Animes » on Jellyfin: its data, not Mira's words.
const frenchLeft = async () => (await lp.locator('#app').innerText()).split('\n').filter((line) => FRENCH_WORDS.test(line) && line.trim() !== 'Séries').slice(0, 4);
try {
  await lp.goto(`${base}#/connexion`); await lp.waitForTimeout(1500);
  const lang = await lp.evaluate(() => document.documentElement.lang);
  if (await lp.locator('.users').count()) await lp.getByRole('button', { name: 'Other account' }).click();
  const signInButton = await lp.getByRole('button', { name: 'Sign in', exact: true }).count();
  const loginFrench = await frenchLeft();
  await lp.screenshot({ path: `${out}/en-01-login.png` });
  check('anglais : connexion en anglais par défaut', lang === 'en' && signInButton === 1 && !loginFrench.length, `lang ${lang}, « Sign in » ${signInButton}${loginFrench.length ? `, français : ${loginFrench.join(' | ')}` : ''}`);
  await lp.fill('#login-name', user); await lp.fill('#login-password', password);
  await lp.getByRole('button', { name: 'Sign in', exact: true }).click();
  await lp.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await lp.waitForTimeout(2500);
  // Dismiss « New in Mira » or a notice if one came up: it is not what is checked here.
  const tabs = await lp.locator('.tabbar a span').allInnerTexts();
  const screens = [];
  for (const [tab, name] of [['home', 'accueil'], ['films', 'films'], ['series', 'séries'], ['settings', 'réglages']]) {
    await lp.locator(`.tabbar a[data-tab="${tab}"]`).click(); await lp.waitForTimeout(1800);
    const left = await frenchLeft();
    if (left.length) screens.push(`${name} : ${left.join(' | ')}`);
    await lp.screenshot({ path: `${out}/en-02-${tab}.png` });
  }
  check('anglais : onglets et écrans en anglais', tabs.join(',') === 'Home,Movies,Shows,Search,Settings' && !screens.length, `${tabs.join(', ')}${screens.length ? ` ; français : ${screens.join(' ; ')}` : ''}`);
  // A title page: its buttons and its facts.
  await lp.locator('.tabbar a[data-tab="films"]').click(); await lp.waitForTimeout(1500);
  await lp.locator('#view .library .grid .card').first().click(); await lp.waitForTimeout(2500);
  const titleFrench = await frenchLeft();
  const play = await lp.locator('#view a, #view button').filter({ hasText: /^(Play|Resume|Watch)\b/ }).count();
  await lp.screenshot({ path: `${out}/en-03-title.png` });
  check('anglais : fiche d’un titre en anglais', play > 0 && !titleFrench.length, `${play} bouton(s) Play${titleFrench.length ? ` ; français : ${titleFrench.join(' | ')}` : ''}`);
  // Réglages → Language → Français: the page reloads on Réglages, in French; English again the same way.
  await lp.locator('.tabbar a[data-tab="settings"]').click(); await lp.waitForTimeout(1500);
  const row = lp.locator('#view .settings button.item', { has: lp.locator('.grow', { hasText: /^Language$/ }) });
  const shown = await row.innerText().catch(() => '');
  await row.click(); await lp.waitForTimeout(500);
  await Promise.all([lp.waitForEvent('load', { timeout: 15000 }), lp.locator('.sheet-item', { hasText: 'Français' }).click()]);
  await lp.waitForTimeout(2500);
  const frenchTabs = await lp.locator('.tabbar a span').allInnerTexts();
  const afterSwitch = { hash: await lp.evaluate(() => location.hash), lang: await lp.evaluate(() => document.documentElement.lang) };
  await lp.screenshot({ path: `${out}/en-04-francais.png` });
  await lp.locator('#view .settings button.item', { has: lp.locator('.grow', { hasText: /^Langue$/ }) }).click(); await lp.waitForTimeout(500);
  await Promise.all([lp.waitForEvent('load', { timeout: 15000 }), lp.locator('.sheet-item', { hasText: 'Automatique' }).click()]);
  await lp.waitForTimeout(2500);
  const backTabs = await lp.locator('.tabbar a span').allInnerTexts();
  check('anglais : Réglages → Language passe en français puis revient à la langue du navigateur',
    /Automatic · English/.test(shown) && frenchTabs.join(',') === 'Accueil,Films,Séries,Recherche,Réglages' && afterSwitch.hash === '#/reglages' && afterSwitch.lang === 'fr'
    && backTabs.join(',') === 'Home,Movies,Shows,Search,Settings',
    `« ${shown.replace(/\s+/g, ' ')} » ; ${frenchTabs.join(', ')} sur ${afterSwitch.hash} (${afterSwitch.lang}) ; puis ${backTabs.join(', ')}`);
  // The plugin's own errors in the language Mira web asks for (Accept-Language), French or English.
  const refusal = (language) => lp.evaluate(async (lang) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/Mira/torlink/search`, { method: 'POST', body: JSON.stringify({ query: ' ', category: 'movies' }),
      headers: { 'Content-Type': 'application/json', 'Accept-Language': lang, Authorization: `MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="${s.token}"` } });
    return (await r.json()).error;
  }, language);
  const refused = { en: await refusal('en'), fr: await refusal('fr'), both: await refusal('de-DE,fr;q=0.8,en;q=0.5') };
  check('anglais : erreurs de l’extension dans la langue demandée', refused.en === 'Type what you’re looking for.' && refused.fr === 'Écris ce que tu cherches.' && refused.fr === refused.both,
    `${refused.en} · ${refused.fr} · ${refused.both}`);
} catch (error) {
  check('anglais : déroulé', false, error.message.split('\n')[0]);
}
check('anglais : aucune erreur JavaScript', englishErrors.length === 0, englishErrors.slice(0, 3).join(' | '));
await inEnglish.close();

// The security policy: inline styles refused but the page's own (allowed by its hash), and the watch above sees a
// refusal (a style attribute and a style element slipped into the page, here on purpose).
const probe = await browser.newContext({ ...devices['iPhone 15 Pro'], locale: 'fr-FR' });
const probed = [];
await watchPolicy(probe, 'contrôle', probed);
const pp = await probe.newPage();
try {
  const response = await pp.goto(`${base}#/connexion`); await pp.waitForTimeout(1000);
  const policy = response.headers()['content-security-policy'] ?? '';
  const clean = probed.length;
  const dark = await pp.evaluate(() => getComputedStyle(document.documentElement).backgroundColor);
  await pp.evaluate(() => {
    document.body.insertAdjacentHTML('beforeend', '<i id="probe" style="color: rgb(1, 2, 3)">x</i>');
    const style = document.createElement('style'); style.textContent = '#probe { outline: 1px solid red; }'; document.head.append(style);
  });
  await pp.waitForTimeout(500);
  const applied = await pp.evaluate(() => getComputedStyle(document.getElementById('probe')).color === 'rgb(1, 2, 3)');
  check('politique de sécurité : styles en ligne refusés, sauf celui de la page (empreinte)',
    !/unsafe-inline/.test(policy) && /style-src 'self' 'sha256-[A-Za-z0-9+/]+=*'/.test(policy) && !clean && dark === 'rgb(7, 7, 8)' && probed.length >= 2 && !applied,
    `${policy.match(/style-src[^;]*/)?.[0] ?? 'pas de style-src'} ; page ${clean ? `refusée (${probed.join(', ')})` : 'acceptée'}, fond ${dark} ; style glissé ${applied ? 'appliqué' : `refusé (${probed.length} refus vus)`}`);
} catch (error) {
  check('politique de sécurité : déroulé', false, error.message.split('\n')[0]);
}
await probe.close();
check('politique de sécurité : aucun refus sur tout le parcours', violations.length === 0, violations.slice(0, 4).join(' | '));
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
await browser.close();
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
