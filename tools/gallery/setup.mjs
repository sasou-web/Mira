// A fresh Jellyfin ready for the README's captures, through its own API: first-run wizard in French, an account, the
// Films and Séries libraries of tools/gallery/media.sh scanned with their TMDb artwork, then a viewer's traces (titles
// started, seen, favourites) for « Continuer à regarder », and Mira web installed from a repository when one is given.
//   node tools/gallery/setup.mjs <server> <media folder as the server sees it> <user> <password> [repository url]
// MIRA_GALLERY_ARTWORK=0: no wait for TMDb's artwork (a machine without the Internet).
const [server, media, user, password, repository] = process.argv.slice(2);
if (!password) { console.error('usage: setup.mjs <server> <media> <user> <password> [repository]'); process.exit(2); }
const base = server.replace(/\/$/, '');
const authorization = (token) => `MediaBrowser Client="Mira gallery", Device="CI", DeviceId="mira-gallery", Version="1.0"${token ? `, Token="${token}"` : ''}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function call(method, path, { body, token } = {}) {
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
    if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${(await response.text()).slice(0, 200)}`);
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }
}
async function until(what, test, seconds) {
  for (const limit = Date.now() + seconds * 1000; Date.now() < limit; await sleep(3000)) {
    try { const value = await test(); if (value) return value; } catch { /* not yet */ }
  }
  console.log(`::warning::Timed out waiting for ${what}`);
  return null;
}

const info = await until('Jellyfin', async () => { const value = await call('GET', 'System/Info/Public'); return value?.Version ? value : null; }, 180);
if (!info) process.exit(1);
console.log(`Jellyfin ${info.Version}`);
if (!info.StartupWizardCompleted) {
  await call('POST', 'Startup/Configuration', { body: { UICulture: 'fr-FR', MetadataCountryCode: 'FR', PreferredMetadataLanguage: 'fr' } });
  await call('GET', 'Startup/User');
  await call('POST', 'Startup/User', { body: { Name: user, Password: password } });
  await call('POST', 'Startup/RemoteAccess', { body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false } });
  await call('POST', 'Startup/Complete');
}
const signIn = await call('POST', 'Users/AuthenticateByName', { body: { Username: user, Pw: password } });
const token = signIn.AccessToken, userId = signIn.User.Id;

const folders = await call('GET', 'Library/VirtualFolders', { token });
for (const [name, type, folder] of [['Films', 'movies', 'Films'], ['Séries', 'tvshows', 'Séries']]) {
  if (folders.some((f) => f.Name === name)) continue;
  const separator = media.includes('\\') ? '\\' : '/';
  const path = `${media.replace(/[\\/]$/, '')}${separator}${folder}`;
  // As Mira for Windows sets up Jellyfin (JellyfinSetup.ConfigureAsync): films gathered into sagas.
  await call('POST', `Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=${type}&paths=${encodeURIComponent(path)}&refreshLibrary=true`,
    { token, body: { LibraryOptions: { AutomaticallyAddToCollection: type === 'movies', PreferredMetadataLanguage: 'fr', MetadataCountryCode: 'FR' } } });
}
// Every film, episode and series of media.sh; fewer when a caller keeps only some (MIRA_GALLERY_TITLES).
const titleCount = Number(process.env.MIRA_GALLERY_TITLES ?? 32);
const query = `Items?userId=${userId}&recursive=true&includeItemTypes=Movie,Series,Episode`;
let scans = 0;
await until('the library scan', async () => {
  const result = await call('GET', query, { token });
  if ((result.Items ?? []).length >= titleCount) return true;
  if (scans++ % 10 === 0) await call('POST', 'Library/Refresh', { token });
  return false;
}, 300);
// TMDb's artwork comes after the scan, title by title.
const artwork = process.env.MIRA_GALLERY_ARTWORK !== '0';
if (artwork) {
  await until('the artwork', async () => {
    const result = await call('GET', query, { token });
    const titles = (result.Items ?? []).filter((x) => x.Type !== 'Episode');
    const missing = titles.filter((x) => !x.ImageTags?.Primary || !(x.BackdropImageTags?.length));
    console.log(`Artwork: ${titles.length - missing.length}/${titles.length} titles`);
    return missing.length === 0;
  }, 600);
}
const { Items: items } = await call('GET', `${query}&fields=ProviderIds,Path`, { token });
for (const item of items.filter((x) => x.Type !== 'Episode')) console.log(`${item.Type} « ${item.Name} »${item.ProductionYear ? ` (${item.ProductionYear})` : ''}${item.ImageTags?.Primary ? '' : ' — no poster'}`);

// A viewer's traces, by file name: started (« Continuer à regarder »), seen, favourites. Films only: the series'
// episodes have no TMDb still, and a black card would open « Continuer à regarder ».
const byFile = (name) => items.find((x) => x.Path?.replace(/\\/g, '/').split('/').pop()?.startsWith(name));
const series = items.find((x) => x.Type === 'Series');
const ticks = (item, share) => Math.round((item.RunTimeTicks ?? 0) * share);
const hour = 3_600_000;
const traces = [
  ['Spring (2019)', { share: 0.42, favorite: true, ago: 1 }],
  ['Metropolis (1927)', { share: 0.61, ago: 26 }],
  ['Tears of Steel (2012)', { share: 0.18, ago: 50 }],
  ['Big Buck Bunny (2008)', { played: true, ago: 80 }],
  ['Sintel (2010)', { played: true, ago: 100 }],
  ['Nosferatu (1922)', { played: true, ago: 140 }],
  ['Charade (1963)', { favorite: true }],
  ['Sprite Fright (2021)', { favorite: true }],
  ['The General (1926)', { favorite: true }],
];
for (const [name, trace] of traces) {
  const item = byFile(name);
  if (!item) { console.log(`::warning::No item for ${name}`); continue; }
  const data = { IsFavorite: !!trace.favorite };
  if (trace.share) Object.assign(data, { PlaybackPositionTicks: ticks(item, trace.share), Played: false });
  if (trace.played) Object.assign(data, { Played: true, PlaybackPositionTicks: 0 });
  if (trace.ago) data.LastPlayedDate = new Date(Date.now() - trace.ago * hour).toISOString();
  await call('POST', `UserItems/${item.Id}/UserData?userId=${userId}`, { token, body: data });
}
if (series) await call('POST', `UserItems/${series.Id}/UserData?userId=${userId}`, { token, body: { IsFavorite: true } });
console.log(`Library: ${items.length} titles and episodes`);

if (repository) {
  const repositories = await call('GET', 'Repositories', { token });
  if (!repositories.some((r) => r.Url === repository)) await call('POST', 'Repositories', { token, body: [...repositories, { Name: 'Mira', Url: repository, Enabled: true }] });
  await call('POST', `Packages/Installed/Mira?assemblyGuid=4ea89259-3350-45b0-8045-f1ab627214dd&repositoryUrl=${encodeURIComponent(repository)}`, { token });
  await call('POST', 'System/Restart', { token });
  await sleep(3000);
  if (!await until('Mira web at /Mira', async () => (await fetch(`${base}/Mira/manifest.webmanifest`)).ok, 120)) process.exit(1);
  console.log('Mira web installed from the repository and served at /Mira');
}
