// The presentation video's Jellyfin: presentation/media.sh's library in English with its TMDb artwork, an "owner"
// administrator and the viewer Sam (not an administrator: no TorLink, no Fix), with a viewer's traces (resumes,
// favourites, watched titles) so that Home looks lived in.
//   node presentation/library.mjs <server> <films folder> <series folder>
const [base, films, series] = process.argv.slice(2);
const auth = (t) => `MediaBrowser Client="Mira video", Device="CI", DeviceId="mira-video-setup", Version="1.0"${t ? `, Token="${t}"` : ''}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function call(method, path, { body, token } = {}) {
  for (let i = 0; ; i++) {
    let r; try { r = await fetch(`${base}/${path}`, { method, headers: { Authorization: auth(token), 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }); } catch (e) { if (i < 150) { await sleep(2000); continue; } throw e; }
    if ((r.status === 503 || /text\/html/.test(r.headers.get('content-type') ?? '')) && i < 150) { await sleep(2000); continue; }
    if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${(await r.text()).slice(0, 300)}`);
    const t = await r.text(); return t ? JSON.parse(t) : null;
  }
}
const info = await call('GET', 'System/Info/Public');
if (!info.StartupWizardCompleted) {
  await call('POST', 'Startup/Configuration', { body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' } });
  await call('GET', 'Startup/User');
  await call('POST', 'Startup/User', { body: { Name: 'owner', Password: 'owner-pass-x' } });
  await call('POST', 'Startup/RemoteAccess', { body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false } });
  await call('POST', 'Startup/Complete');
}
const admin = (await call('POST', 'Users/AuthenticateByName', { body: { Username: 'owner', Pw: 'owner-pass-x' } })).AccessToken;
const users = await call('GET', 'Users', { token: admin });
if (!users.some((u) => u.Name === 'Sam')) await call('POST', 'Users/New', { token: admin, body: { Name: 'Sam', Password: 'mira-demo' } });
const s = await call('POST', 'Users/AuthenticateByName', { body: { Username: 'Sam', Pw: 'mira-demo' } });
const token = s.AccessToken, uid = s.User.Id;
const folders = await call('GET', 'Library/VirtualFolders', { token: admin });
for (const [name, type, path, trickplay] of [['Movies', 'movies', films, true], ['Shows', 'tvshows', series, false]]) {
  if (folders.some((f) => f.Name === name)) continue;
  await call('POST', `Library/VirtualFolders?name=${name}&collectionType=${type}&paths=${encodeURIComponent(path)}&refreshLibrary=true`,
    { token: admin, body: { LibraryOptions: { PreferredMetadataLanguage: 'en', MetadataCountryCode: 'US', EnableTrickplayImageExtraction: trickplay, ExtractTrickplayImagesDuringLibraryScan: trickplay } } });
}
const q = `Items?userId=${uid}&recursive=true&includeItemTypes=Movie,Series,Episode&fields=Path,Trickplay`;
const want = Number(process.env.TITLES);
let items = [];
for (let i = 0; i < 240; i++) {
  items = (await call('GET', q, { token })).Items;
  const titles = items.filter((x) => x.Type !== 'Episode'), done = titles.filter((x) => x.ImageTags?.Primary && x.BackdropImageTags?.length);
  console.log(`items ${items.length}/${want}, artwork ${done.length}/${titles.length}`);
  if (items.length >= want && done.length === titles.length && i > 5) break;
  if (i % 15 === 3) await call('POST', 'Library/Refresh', { token: admin });
  await sleep(5000);
}
const find = (type, name) => items.find((x) => x.Type === type && x.Name.toLowerCase().includes(name.toLowerCase()));
const ep = (season, number) => items.find((x) => x.Type === 'Episode' && x.SeriesName === 'Arcane' && x.ParentIndexNumber === season && x.IndexNumber === number);
const min = (m) => Math.round(m * 60 * 1e7);
const data = (item, body) => item ? call('POST', `UserItems/${item.Id}/UserData?userId=${uid}`, { token, body }) : console.log('missing title');
for (let e = 1; e <= 9; e++) await data(ep(1, e), { Played: true });
for (let e = 1; e <= 3; e++) await data(ep(2, e), { Played: true });
await data(ep(2, 4), { PlaybackPositionTicks: min(23) });
await data(find('Movie', 'Across the Spider-Verse'), { PlaybackPositionTicks: min(61) });
await data(find('Movie', 'Your Name'), { PlaybackPositionTicks: min(38) });
await data(find('Movie', 'The Wild Robot'), { PlaybackPositionTicks: min(17) });
for (const name of ['Coco', 'My Neighbor Totoro', 'Klaus', 'Into the Spider-Verse']) await data(find('Movie', name), { Played: true });
for (const [type, name] of [['Movie', 'Klaus'], ['Movie', 'Spirited Away'], ['Series', 'Arcane'], ['Movie', 'Flow']]) {
  const item = find(type, name); if (item) await call('POST', `UserFavoriteItems/${item.Id}?userId=${uid}`, { token });
}
// The film played in the video: its scrubbing previews, if Jellyfin makes them in time.
for (let i = 0; i < 36; i++) {
  const spring = (await call('GET', q, { token })).Items.find((x) => x.Name === 'Spring');
  if (spring?.Trickplay && Object.keys(spring.Trickplay).length) { console.log('Spring: trickplay ready'); break; }
  if (i === 0) await call('POST', `ScheduledTasks/Running/${(await call('GET', 'ScheduledTasks', { token: admin })).find((t) => t.Key === 'RefreshTrickplayImages')?.Id}`, { token: admin }).catch((e) => console.log(String(e)));
  await sleep(5000);
}
console.log('ready');
