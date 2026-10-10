// Mira web in Safari itself (WebKit, Apple's video engine, HLS built in): the path an iPhone takes. Driven through
// safaridriver's WebDriver API, without dependencies. Checks HLS playback from Jellyfin's conversion, French
// subtitles beside the video and on the picture, the 10-second skip, the position Jellyfin keeps, and the next
// episode starting alone; then Apple's full screen player, as iPhones and iPads use it (turned on here by the
// nativePlayer setting): subtitles chosen on the title page, the next episode without leaving full screen, and back
// to the title page once it is closed.
//   safaridriver -p 4444 &   then   node tools/web/safari.mjs <Mira web url> <user> <password> <out folder>
import fs from 'node:fs';
const [base = 'http://127.0.0.1:8096/Mira/', user = 'mira', password = 'mira-check', out = 'safari'] = process.argv.slice(2);
const driver = process.env.WEBDRIVER ?? 'http://127.0.0.1:4444';
const ELEMENT = 'element-6066-11e4-a52e-4f735466cecf';
fs.mkdirSync(out, { recursive: true });
const results = [];
const check = (name, ok, detail = '') => { results.push(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' — ' + detail : ''}`); console.log(results.at(-1)); };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function wd(method, path, body) {
  const response = await fetch(`${driver}${path}`, { method, headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${data?.value?.error ?? ''} ${data?.value?.message ?? ''}`.trim());
  return data.value;
}
const { sessionId: id } = await wd('POST', '/session', { capabilities: { alwaysMatch: { browserName: 'safari' } } });
const s = (path) => `/session/${id}${path}`;
const run = (script, ...args) => wd('POST', s('/execute/sync'), { script, args });
const runAsync = (body, ...args) => wd('POST', s('/execute/async'), {
  script: `const done = arguments[arguments.length - 1]; (async () => { ${body} })().then(done, (e) => done('error: ' + e));`, args,
});
const go = (hash) => run('location.hash = arguments[0];', hash);
const find = async (css) => { try { return (await wd('POST', s('/element'), { using: 'css selector', value: css }))[ELEMENT]; } catch { return null; } };
const click = async (css) => { const el = await find(css); if (el) await wd('POST', s(`/element/${el}/click`), {}); return !!el; };
// The player's controls fade out after 3 s of playback: the mouse passing over the picture, as on a Mac, brings them
// back before each button is clicked (Safari refuses a click on a hidden control).
const tapControl = async (css) => {
  await run("document.querySelector('.player')?.dispatchEvent(new PointerEvent('pointermove', { pointerType: 'mouse', bubbles: true }));");
  await sleep(300);
  return click(css);
};
const shot = async (name) => fs.writeFileSync(`${out}/${name}.png`, Buffer.from(await wd('GET', s('/screenshot')), 'base64'));
const video = () => run(`const v = document.querySelector('video'); return v ? { t: v.currentTime, paused: v.paused, ready: v.readyState, error: v.error && v.error.code,
  src: v.currentSrc, tracks: v.textTracks.length, hash: location.hash } : null;`);
// Waits for the picture to move, up to 20 s: the first video on a fresh Mac takes a few seconds to start. If Safari
// wants a tap before sound, Mira shows a play button, touched here as a person would.
const playing = async (seconds = 20) => {
  const start = Date.now();
  let tapped = false;
  for (; Date.now() - start < seconds * 1000; await sleep(500)) {
    if (!tapped && await find('.p-message:not([hidden]) .round.big')) { tapped = await click('.p-message .round.big'); continue; }
    const state = await video();
    if (state && !state.paused && state.t > 1) return { ...state, after: (Date.now() - start) / 1000, tapped };
  }
  return { ...(await video()), after: null, tapped };
};
// The shared video's events, caught on their way down (media events do not bubble), to tell where a start stalls.
const recordEvents = () => run(`window.miraEvents = []; const t0 = performance.now();
  for (const name of ['loadstart', 'loadedmetadata', 'loadeddata', 'canplay', 'play', 'playing', 'waiting', 'stalled', 'pause', 'error'])
    document.addEventListener(name, (e) => { if (e.target.tagName === 'VIDEO') miraEvents.push(name + ' ' + Math.round(performance.now() - t0)); }, true);`);
const events = () => run('return (window.miraEvents || []).join(', ');');
const started = (state) => `${state?.t?.toFixed(1)} s${state?.after != null ? `, partie en ${state.after.toFixed(1)} s` : ''}${state?.tapped ? ' après un toucher' : ''}`
  + `, readyState ${state?.ready}, ${state?.paused ? 'en pause' : 'en lecture'}, erreur ${state?.error ?? 'aucune'}`;

try {
  await wd('POST', s('/window/rect'), { width: 430, height: 932, x: 0, y: 0 }).catch(() => {});
  await wd('POST', s('/url'), { url: `${base}#/connexion` });
  await sleep(2000);
  const signed = await runAsync(`const { api } = await import(document.querySelector('script[type=module]').src.replace('app.js', 'api.js'));
    await api.signIn(arguments[0], arguments[1]); return JSON.parse(localStorage.getItem('mira.session'));`, user, password);
  check('connexion', !!signed?.token, signed?.userName ?? String(signed));
  const api = (path, method = 'GET') => runAsync(`const r = await fetch(arguments[0], { method: arguments[1], headers: { Authorization: 'MediaBrowser Client="check", Device="check", DeviceId="check", Version="1", Token="' + arguments[2] + '"' } });
    const t = await r.text(); return t ? JSON.parse(t) : null;`, `${new URL(base).pathname.replace(/\/Mira\/?$/i, '')}/${path}`, method, signed.token);

  const movie = (await api(`Items?recursive=true&includeItemTypes=Movie&searchTerm=Aube%20Test&userId=${signed.userId}`)).Items[0];
  const series = (await api(`Items?recursive=true&includeItemTypes=Series&searchTerm=Courte%20HLS&userId=${signed.userId}`)).Items[0];
  const episodes = (await api(`Shows/${series.Id}/Episodes?userId=${signed.userId}`)).Items;
  for (const item of [movie, ...episodes]) await api(`UserPlayedItems/${item.Id}?userId=${signed.userId}`, 'DELETE');

  await go('#/'); await sleep(3000); await shot('01-home');
  await go(`#/titre/${movie.Id}`); await sleep(2500); await shot('02-title');

  // Lecture, touched on the title page as a person would: the tap also lets Safari start the sound.
  await recordEvents();
  check('fiche : bouton Lecture', await click('a.btn.primary[href*="lecture"]'));
  let state = await playing();
  check('Safari : flux HLS converti par Jellyfin', /\.m3u8/.test(state?.src ?? ''), (state?.src ?? '').split('?')[0]);
  check('Safari : lecture', state && !state.paused && state.t > 1 && !state.error, state?.after != null ? started(state) : `${started(state)} — ${await events()}`);
  check('Safari : sous-titres à côté de la vidéo', state?.tracks >= 1, `${state?.tracks} piste(s)`);
  // Safari draws subtitles at the bottom of the video's box: fitted to the picture, they sit on it.
  const box = await run(`const v = document.querySelector('video'), b = v.getBoundingClientRect();
    return { w: b.width, h: b.height, vw: v.videoWidth, vh: v.videoHeight };`);
  check('Safari : cadre de la vidéo ajusté à l’image', box.vw > 0 && Math.abs(box.w / box.h - box.vw / box.vh) < 0.02,
    `${Math.round(box.w)}×${Math.round(box.h)} pour une image ${box.vw}×${box.vh}`);
  await shot('03-player');

  // Subtitles: chosen in the tracks sheet, then read from the cue Safari shows.
  await tapControl('button[aria-label="Audio et sous-titres"]'); await sleep(700);
  await shot('04-tracks');
  const picked = await run(`const item = [...document.querySelectorAll('.sheet-item')].find((b) => /fr|fran/i.test(b.textContent) && /Sous-titres/.test(b.textContent));
    if (item) item.click(); return item ? item.textContent : '';`);
  await sleep(1500);
  const cue = await run(`const t = [...document.querySelector('video').textTracks].find((x) => x.mode === 'showing');
    return t && t.activeCues && t.activeCues.length ? t.activeCues[0].text : '';`);
  check('Safari : sous-titres français affichés', /Bonjour depuis Mira web/.test(cue), `${picked.trim()} → « ${cue} »`);
  await shot('05-subtitles');

  const before = (await video()).t;
  await tapControl('button[aria-label="Avancer de 10 secondes"]'); await sleep(2500);
  const after = (await video()).t;
  check('Safari : avance de 10 s', after - before >= 9, `${before.toFixed(1)} → ${after.toFixed(1)} s`);
  await tapControl('.p-top button[aria-label="Retour"]'); await sleep(2500);
  const saved = await api(`Items/${movie.Id}?userId=${signed.userId}`);
  check('Jellyfin : position du film', saved.UserData.PlaybackPositionTicks > 100_000_000, `${(saved.UserData.PlaybackPositionTicks / 1e7).toFixed(1)} s`);

  await go(`#/lecture/${episodes[0].Id}?debut=1`);
  await playing();
  let moved = false;
  for (let i = 0; i < 30 && !moved; i++) { await sleep(1000); moved = (await run('return location.hash;')).includes(episodes[1].Id); }
  check('Safari : épisode suivant automatique', moved);
  await sleep(3500);
  state = await video();
  check('Safari : l’épisode suivant joue', state && !state.paused && state.t > 1, `${state?.t?.toFixed(1)} s`);
  await shot('06-next-episode');
  await tapControl('.p-top button[aria-label="Retour"]');
  // Retour reports the stop to Jellyfin, then goes back: done once the player's address is gone.
  for (let i = 0; i < 20 && (await run('return location.hash;')).startsWith('#/lecture/'); i++) await sleep(500);
  await sleep(1000);

  // ---------- Apple's player, in full screen ----------
  // The setting is read when Mira starts: the page is loaded again (another query: not a move within the page), its
  // <video> made without playsinline.
  await run(`const s = JSON.parse(localStorage.getItem('mira.settings') || '{}'); s.nativePlayer = true; localStorage.setItem('mira.settings', JSON.stringify(s));`);
  for (const item of [movie, ...episodes]) await api(`UserPlayedItems/${item.Id}?userId=${signed.userId}`, 'DELETE');
  await wd('POST', s('/url'), { url: `${base}?lecteur=apple#/titre/${movie.Id}` });
  await sleep(3000);
  check('lecteur d’Apple : réglage pris en compte', await runAsync(`const { appleNative } = await import(document.querySelector('script[type=module]').src.replace('app.js', 'player/video.js'));
    return appleNative();`) === true);
  // French subtitles chosen under Lecture, before playback: the only place for them with a conversion.
  await click('.tracks-line'); await sleep(700);
  const chose = await run(`const items = [...document.querySelectorAll('.sheet-item')]; const item = items.reverse().find((b) => /Français/.test(b.textContent));
    if (item) item.click(); return item ? item.textContent : '';`);
  await sleep(500);
  const line = await run("return document.querySelector('.tracks-line')?.textContent ?? '';");
  check('fiche : sous-titres choisis avant la lecture', /Sous-titres : Français/.test(line), `${chose.trim()} → ${line}`);
  await shot('07-tracks');
  // Full screen without a tap is iOS's way; Safari on a Mac asks for one: Mira's play button is touched then.
  const fullscreen = async (seconds = 25) => {
    const start = Date.now();
    for (; Date.now() - start < seconds * 1000; await sleep(500)) {
      if (await find('.n-status .n-play')) { await click('.n-status .n-play'); await sleep(1500); continue; }
      const state = await run(`const v = document.querySelector('video'); return v ? { full: v.webkitDisplayingFullscreen, mode: v.webkitPresentationMode, t: v.currentTime, paused: v.paused,
        src: v.currentSrc, showing: ([...v.textTracks].find((x) => x.mode === 'showing') || {}).label || '', tracks: v.querySelectorAll('track').length, hash: location.hash } : null;`);
      if (state?.full && !state.paused && state.t > 1) return state;
    }
    return run(`const v = document.querySelector('video'); return v ? { full: v.webkitDisplayingFullscreen, mode: v.webkitPresentationMode, t: v.currentTime, paused: v.paused, hash: location.hash } : { hash: location.hash };`);
  };
  check('fiche : bouton Lecture (lecteur d’Apple)', await click('a.btn.primary[href*="lecture"]'));
  state = await fullscreen();
  check('lecteur d’Apple : lecture en plein écran', state?.full && state.mode === 'fullscreen' && !state.paused, `${state?.mode}, ${state?.t?.toFixed(1)} s, ${(state?.src ?? '').split('?')[0].split('/').pop()}`);
  // A conversion carries its subtitles in the HLS stream, the only ones Apple's player lists in its menu: no <track>
  // beside it, and the French ones showing, in time with the picture (a shift would leave no cue before 10 s).
  const carried = async () => run(`const v = document.querySelector('video'), list = [...v.textTracks].filter((t) => ['subtitles', 'captions', 'forced'].includes(t.kind));
    const on = list.find((t) => t.mode === 'showing');
    return { hls: /SubtitleMethod=Hls/i.test(v.currentSrc) && v.currentSrc.includes('/Mira/hls/'), beside: v.querySelectorAll('track').length, count: list.length, labels: list.map((t) => t.label + ' (' + t.language + ')').join(', '),
      showing: on ? on.label : '', cue: on && on.activeCues && on.activeCues.length ? on.activeCues[0].text : '', t: v.currentTime, src: v.currentSrc };`);
  // The cue runs from 1 s to 30 s: watched until 20 s, its first appearance tells any shift of the subtitles' clock.
  let inStream = await carried();
  const firstSeen = inStream.t;
  for (let i = 0; i < 60 && !inStream.cue && inStream.t < 20; i++) { await sleep(250); inStream = await carried(); }
  check('lecteur d’Apple : sous-titres dans le flux HLS, nommés en français', inStream.hls && inStream.beside === 0 && inStream.count >= 1 && /^Français \(/.test(inStream.labels),
    `${inStream.count} piste(s) dans le flux (${inStream.labels || 'aucune'}), ${inStream.beside} à côté, ${inStream.src.split('?')[0].replace(/^https?:\/\/[^/]+/, '')}`);
  check('lecteur d’Apple : sous-titres français affichés à temps', /Bonjour depuis Mira web/.test(inStream.cue) && inStream.t < Math.max(3, firstSeen + 1.5),
    `« ${inStream.cue || 'aucun'} » (${inStream.showing || 'aucune piste affichée'}) à ${inStream.t.toFixed(1)} s, attendu dès 1 s (vu à partir de ${firstSeen.toFixed(1)} s)`);
  await shot('08-apple-player').catch(() => {});
  // Off, then French again, as in Apple's menu (it sets the tracks' modes): Mira follows without a new stream.
  const sourceBefore = inStream.src;
  await run("for (const t of document.querySelector('video').textTracks) t.mode = 'disabled';");
  await sleep(1500);
  const off = await carried();
  await run(`const t = [...document.querySelector('video').textTracks].filter((x) => ['subtitles', 'captions', 'forced'].includes(x.kind))[0]; if (t) t.mode = 'showing';`);
  await sleep(1500);
  const again = await carried();
  check('lecteur d’Apple : sous-titres changés dans son menu', !off.showing && !!again.showing && again.src === sourceBefore,
    `sans : ${off.showing || 'aucune'}, puis : ${again.showing || 'aucune'}, ${again.src === sourceBefore ? 'même flux' : 'flux relancé'}`);
  // Jellyfin keeps a position past 5 % of the title only (2 s of this 40 s film): a few seconds played first.
  for (let i = 0; i < 30 && ((await run("return document.querySelector('video')?.currentTime ?? 99;")) < 8); i++) await sleep(500);
  await run("document.querySelector('video')?.webkitExitFullscreen?.();");
  let back = false;
  for (let i = 0; i < 16 && !back; i++) { await sleep(500); back = (await run('return location.hash;')).startsWith(`#/titre/${movie.Id}`); }
  await sleep(1500);
  const kept = await api(`Items/${movie.Id}?userId=${signed.userId}`);
  check('lecteur d’Apple : fermé, retour à la fiche, position gardée', back && kept.UserData.PlaybackPositionTicks > 50_000_000,
    `${(await run('return location.hash;')).split('?')[0]}, ${(kept.UserData.PlaybackPositionTicks / 1e7).toFixed(1)} s`);

  await go(`#/lecture/${episodes[0].Id}?debut=1`);
  state = await fullscreen();
  check('lecteur d’Apple : épisode en plein écran', state?.full && !state.paused, `${state?.t?.toFixed(1)} s`);
  moved = false;
  for (let i = 0; i < 40 && !moved; i++) { await sleep(1000); moved = (await run('return location.hash;')).includes(episodes[1].Id); }
  await sleep(4000);
  state = await run("const v = document.querySelector('video'); return v ? { full: v.webkitDisplayingFullscreen, t: v.currentTime, paused: v.paused } : { full: false, t: 0, paused: true };");
  check('lecteur d’Apple : épisode suivant sans quitter le plein écran', moved && state.full && !state.paused && state.t > 1, `${moved ? 'É2' : 'resté sur É1'}, ${state.full ? 'plein écran' : 'dans la page'}, ${state.t.toFixed(1)} s`);
  await run("document.querySelector('video')?.webkitExitFullscreen?.();"); await sleep(2000);
  check('lecteur d’Apple : fermé, retour à l’écran d’avant', (await run('return location.hash;')).startsWith('#/titre/'), (await run('return location.hash;')).split('?')[0]);
} catch (error) {
  check('déroulé', false, String(error.message ?? error).split('\n')[0]);
  await shot('error').catch(() => {});
} finally {
  await run(`const s = JSON.parse(localStorage.getItem('mira.settings') || '{}'); delete s.nativePlayer; localStorage.setItem('mira.settings', JSON.stringify(s));`).catch(() => {});
  await wd('DELETE', s('')).catch(() => {});
}
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
