// A fresh Jellyfin, in English, scanning presentation/media.sh's library with its TMDb artwork; then each title's
// metadata and images exported to a folder, for the presentation video to be recorded on a Jellyfin without TMDb.
//   node presentation/export.mjs <server> <out folder>
import { mkdirSync, writeFileSync } from 'node:fs';
const [base, out] = process.argv.slice(2);
const auth = (t) => `MediaBrowser Client="Mira video", Device="CI", DeviceId="mira-video", Version="1.0"${t ? `, Token="${t}"` : ''}`;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function call(method, path, { body, token } = {}) {
  for (let i = 0; ; i++) {
    let r; try { r = await fetch(`${base}/${path}`, { method, headers: { Authorization: auth(token), 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) }); } catch (e) { if (i < 90) { await sleep(2000); continue; } throw e; }
    if ((r.status === 503 || /text\/html/.test(r.headers.get('content-type') ?? '')) && i < 90) { await sleep(2000); continue; }
    if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${(await r.text()).slice(0, 300)}`);
    const t = await r.text(); return t ? JSON.parse(t) : null;
  }
}
const info = await call('GET', 'System/Info/Public');
if (!info.StartupWizardCompleted) {
  await call('POST', 'Startup/Configuration', { body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' } });
  await call('GET', 'Startup/User');
  await call('POST', 'Startup/User', { body: { Name: 'Sam', Password: 'mira-demo' } });
  await call('POST', 'Startup/Complete');
}
const s = await call('POST', 'Users/AuthenticateByName', { body: { Username: 'Sam', Pw: 'mira-demo' } });
const token = s.AccessToken, uid = s.User.Id;
for (const [name, type, path] of [['Movies', 'movies', '/media/Films'], ['Shows', 'tvshows', '/media/Séries']])
  await call('POST', `Library/VirtualFolders?name=${name}&collectionType=${type}&paths=${encodeURIComponent(path)}&refreshLibrary=true`,
    { token, body: { LibraryOptions: { PreferredMetadataLanguage: 'en', MetadataCountryCode: 'US' } } });
const q = `Items?userId=${uid}&recursive=true&includeItemTypes=Movie,Series,Episode&fields=Path,Overview,Genres,Taglines,Studios,People,ProviderIds,OfficialRating,CommunityRating,ProductionYear,PremiereDate`;
const want = Number(process.env.TITLES);
for (let i = 0; i < 200; i++) {
  const items = (await call('GET', q, { token })).Items;
  const titles = items.filter((x) => x.Type !== 'Episode'), done = titles.filter((x) => x.ImageTags?.Primary && x.BackdropImageTags?.length);
  console.log(`items ${items.length}/${want}, artwork ${done.length}/${titles.length}`);
  if (items.length >= want && done.length === titles.length && i > 5) break;
  if (i % 15 === 3) await call('POST', 'Library/Refresh', { token });
  await sleep(5000);
}
await sleep(20000); // episode stills come last
const items = (await call('GET', q, { token })).Items;
mkdirSync(`${out}/img`, { recursive: true });
const save = async (id, type, index, file, width) => {
  const r = await fetch(`${base}/Items/${id}/Images/${type}/${index}?maxWidth=${width}&quality=90&format=${file.endsWith('.png') ? 'Png' : 'Jpg'}`);
  if (!r.ok) return false;
  writeFileSync(`${out}/img/${file}`, Buffer.from(await r.arrayBuffer())); return true;
};
const meta = [];
for (const it of items) {
  const m = { Type: it.Type, Name: it.Name, File: it.Path?.split('/').pop(), Overview: it.Overview, Genres: it.Genres, Taglines: it.Taglines, Studios: it.Studios?.map((x) => x.Name), OfficialRating: it.OfficialRating, CommunityRating: it.CommunityRating, ProductionYear: it.ProductionYear, PremiereDate: it.PremiereDate, IndexNumber: it.IndexNumber, ParentIndexNumber: it.ParentIndexNumber, SeriesName: it.SeriesName, People: it.People?.slice(0, 8).map((p) => ({ Name: p.Name, Role: p.Role, Type: p.Type })), images: {} };
  const key = it.Id;
  if (it.ImageTags?.Primary && await save(key, 'Primary', 0, `${key}-primary.jpg`, it.Type === 'Episode' ? 960 : 800)) m.images.Primary = `${key}-primary.jpg`;
  if (it.ImageTags?.Logo && await save(key, 'Logo', 0, `${key}-logo.png`, 800)) m.images.Logo = `${key}-logo.png`;
  if (it.ImageTags?.Thumb && await save(key, 'Thumb', 0, `${key}-thumb.jpg`, 960)) m.images.Thumb = `${key}-thumb.jpg`;
  m.images.Backdrop = [];
  for (let i = 0; i < Math.min(2, it.BackdropImageTags?.length ?? 0); i++) if (await save(key, 'Backdrop', i, `${key}-backdrop${i}.jpg`, 1920)) m.images.Backdrop.push(`${key}-backdrop${i}.jpg`);
  meta.push(m);
}
writeFileSync(`${out}/items.json`, JSON.stringify(meta, null, 1));
console.log(`exported ${meta.length} items`);
