// A fresh Jellyfin ready for Mira web's checks, through its own API: first-run wizard, an account, Films and Séries
// libraries scanned, and Mira web installed from a repository as Mira for Windows installs it (plugin catalogue,
// installation, restart). Resume points are kept for short test videos (Jellyfin's minimum is 5 minutes).
//   node tools/web/setup.mjs <server> <media folder as the server sees it> <user> <password> [repository url]
const [server, media, user, password, repository] = process.argv.slice(2);
if (!password) { console.error('usage: setup.mjs <server> <media> <user> <password> [repository]'); process.exit(2); }
const base = server.replace(/\/$/, '');
const authorization = (token) => `MediaBrowser Client="Mira check", Device="CI", DeviceId="mira-check", Version="1.0"${token ? `, Token="${token}"` : ''}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(method, path, { body, token, allow = [] } = {}) {
  // While Jellyfin starts (database migrations), it answers every request with a page about it: wait.
  for (const limit = Date.now() + 120_000; ; await sleep(1500)) {
    let response;
    try {
      response = await fetch(`${base}/${path}`, {
        method, headers: { Authorization: authorization(token), 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body),
      });
    } catch (error) { if (Date.now() < limit) continue; throw error; }
    const starting = response.status === 503 || /text\/html/.test(response.headers.get('content-type') ?? '');
    if (starting && Date.now() < limit) continue;
    if (!response.ok && !allow.includes(response.status)) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }
}
async function until(what, test, seconds = 120) {
  for (const limit = Date.now() + seconds * 1000; Date.now() < limit; await sleep(1500)) {
    try { const value = await test(); if (value) return value; } catch { /* not yet */ }
  }
  throw new Error(`Timed out waiting for ${what}`);
}

const info = await until('Jellyfin', async () => { const value = await call('GET', 'System/Info/Public'); return value?.Version ? value : null; });
console.log(`Jellyfin ${info.Version}`);
if (!info.StartupWizardCompleted) {
  await call('POST', 'Startup/Configuration', { body: { UICulture: 'fr-FR', MetadataCountryCode: 'FR', PreferredMetadataLanguage: 'fr' } });
  await call('GET', 'Startup/User');
  await call('POST', 'Startup/User', { body: { Name: user, Password: password } });
  await call('POST', 'Startup/RemoteAccess', { body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false } });
  await call('POST', 'Startup/Complete');
}
const signIn = await call('POST', 'Users/AuthenticateByName', { body: { Username: user, Pw: password } });
const token = signIn.AccessToken;

const configuration = await call('GET', 'System/Configuration', { token });
await call('POST', 'System/Configuration', { token, body: { ...configuration, MinResumeDurationSeconds: 1 } });

const folders = await call('GET', 'Library/VirtualFolders', { token });
for (const [name, type, folder] of [['Films', 'movies', 'Films'], ['Séries', 'tvshows', 'Séries']]) {
  if (folders.some((f) => f.Name === name)) continue;
  const path = `${media.replace(/\/$/, '')}/${folder}`;
  await call('POST', `Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=${type}&paths=${encodeURIComponent(path)}&refreshLibrary=true`, { token, body: { LibraryOptions: {} } });
}
// A library added while Jellyfin is still starting may be scanned empty: scans again until every file is in, and
// read (a title without its length yet has no HLS subtitles: Jellyfin refuses their playlists).
let scans = 0;
const items = await until('the library scan', async () => {
  const result = await call('GET', `Items?userId=${signIn.User.Id}&recursive=true&includeItemTypes=Movie,Episode&fields=MediaSources`, { token });
  const read = (result.Items ?? []).every((x) => x.RunTimeTicks > 0 && x.MediaSources?.[0]?.RunTimeTicks > 0);
  if (result.TotalRecordCount >= 8 && read) return result.Items;
  if (scans++ % 15 === 0) await call('POST', 'Library/Refresh', { token });
  return null;
}, 240);
console.log(`Library: ${items.length} films and episodes`);

if (repository) {
  const repositories = await call('GET', 'Repositories', { token });
  if (!repositories.some((r) => r.Url === repository)) await call('POST', 'Repositories', { token, body: [...repositories, { Name: 'Mira', Url: repository, Enabled: true }] });
  await call('POST', `Packages/Installed/Mira?assemblyGuid=4ea89259-3350-45b0-8045-f1ab627214dd&repositoryUrl=${encodeURIComponent(repository)}`, { token });
  await call('POST', 'System/Restart', { token });
  await sleep(3000);
  await until('Mira web at /Mira', async () => (await fetch(`${base}/Mira/manifest.webmanifest`)).ok, 120);
  console.log('Mira web installed from the repository and served at /Mira');
}
