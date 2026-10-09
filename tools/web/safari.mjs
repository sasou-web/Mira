// Mira web in Safari itself (WebKit, Apple's video engine, HLS built in): the path an iPhone takes. Driven through
// safaridriver's WebDriver API, without dependencies. Checks HLS playback from Jellyfin's conversion, French
// subtitles beside the video, the 10-second skip, the position Jellyfin keeps, and the next episode starting alone.
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
// Safari may want a tap before sound: Mira then shows a play button, clicked here as a person would.
const startPlayback = async () => { await sleep(3500); if (await find('.p-message:not([hidden]) .round.big')) { await click('.p-message .round.big'); await sleep(1500); } };

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

  await go(`#/lecture/${movie.Id}?debut=1`);
  await startPlayback();
  await sleep(5000);
  let state = await video();
  check('Safari : flux HLS converti par Jellyfin', /\.m3u8/.test(state?.src ?? ''), (state?.src ?? '').split('?')[0]);
  check('Safari : lecture', state && !state.paused && state.t > 2 && !state.error, `${state?.t?.toFixed(1)} s, readyState ${state?.ready}, erreur ${state?.error ?? 'aucune'}`);
  check('Safari : sous-titres à côté de la vidéo', state?.tracks >= 1, `${state?.tracks} piste(s)`);
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
  await startPlayback();
  let moved = false;
  for (let i = 0; i < 30 && !moved; i++) { await sleep(1000); moved = (await run('return location.hash;')).includes(episodes[1].Id); }
  check('Safari : épisode suivant automatique', moved);
  await sleep(3500);
  state = await video();
  check('Safari : l’épisode suivant joue', state && !state.paused && state.t > 1, `${state?.t?.toFixed(1)} s`);
  await shot('06-next-episode');
  await tapControl('.p-top button[aria-label="Retour"]'); await sleep(1000);
} catch (error) {
  check('déroulé', false, String(error.message ?? error).split('\n')[0]);
  await shot('error').catch(() => {});
} finally {
  await wd('DELETE', s('')).catch(() => {});
}
fs.writeFileSync(`${out}/results.txt`, results.join('\n') + '\n');
process.exit(results.some((r) => r.startsWith('FAIL')) ? 1 : 0);
