// The films' backdrops a release's banner is lit by (tools/banner/banner.mjs), still-<name>.jpg (every title's, by
// plan.mjs's slug of its folder; the eight films of « film » by their short names too), from a Jellyfin set up
// on the library of tools/banner/media.sh (tools/gallery/setup.mjs), through its API. First one more library scan and
// a wait for the films' genres: Jellyfin lists them (Items/Filters2, the chips of the Films page Mira for Mac's
// gallery captures next) only once a scan has gone past the metadata TMDb gave.
//   node tools/banner/stills.mjs <server> <user> <password> <folder>
// MIRA_STILLS_ALL=1: every film's and series' backdrop and poster too, backdrop-<slug>.jpg and poster-<slug>.jpg (the
// slug of its folder without the year: blade-runner-2049), for gallery.yml's player film and the captures' sets.
import { mkdirSync, writeFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { slug } from './plan.mjs';

const [server, user, password, out] = process.argv.slice(2);
if (!out) { console.error('usage: stills.mjs <server> <user> <password> <folder>'); process.exit(2); }
mkdirSync(out, { recursive: true });
const base = server.replace(/\/$/, '');
const header = (token) => ({ 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="banner", Device="banner", DeviceId="banner-setup", Version="1"${token ? `, Token="${token}"` : ''}` });
const get = async (path, token) => {
  const response = await fetch(`${base}/${path}`, { headers: header(token) });
  if (!response.ok) throw new Error(`GET ${path}: ${response.status}`);
  return response;
};

const signed = await (await fetch(`${base}/Users/AuthenticateByName`, { method: 'POST', headers: header(), body: JSON.stringify({ Username: user, Pw: password }) })).json();
const token = signed.AccessToken, userId = signed.User.Id;
await fetch(`${base}/Library/Refresh`, { method: 'POST', headers: header(token) });
let genres = 0;
for (const limit = Date.now() + 180_000; Date.now() < limit && !genres; await new Promise((r) => setTimeout(r, 3000))) {
  genres = (await (await get(`Items/Filters2?userId=${userId}&includeItemTypes=Movie&recursive=true`, token)).json()).Genres?.length ?? 0;
}
console.log(genres ? `${genres} genres` : '::warning::no genre listed by Jellyfin');

const { Items: titles } = await (await get(`Items?userId=${userId}&recursive=true&includeItemTypes=Movie,Series&fields=Path`, token)).json();
const find = (name) => titles.find((x) => (x.Path ?? '').replace(/\\/g, '/').includes(name));
const backdrop = async (title, file) => {
  const still = await fetch(`${base}/Items/${title.Id}/Images/Backdrop?maxWidth=2400&quality=90`);
  if (!still.ok) return false;
  writeFileSync(join(out, file), Buffer.from(await still.arrayBuffer()));
  return true;
};
// « Gros plan » (closeup): every title's, by its folder's name as plan.mjs names the library (slug).
for (const title of titles) {
  const path = (title.Path ?? '').replace(/\\/g, '/');
  const folder = title.Type === 'Movie' ? dirname(path) : path;
  const name = slug(basename(folder));
  if (!name) continue;
  console.log(await backdrop(title, `still-${name}.jpg`) ? `still-${name}.jpg` : `no backdrop for ${title.Name}`);
}
// « film » (0.9.7 to 0.9.12), by their short names. Not Spirited Away: its first backdrop on TMDb is a photo of Hayao Miyazaki.
for (const [short, name] of [['arcane', 'Arcane (2021)'], ['spider-verse', 'Spider-Man Across the Spider-Verse (2023)'], ['klaus', 'Klaus (2019)'],
  ['coco', 'Coco (2017)'], ['wild-robot', 'The Wild Robot (2024)'], ['puss-in-boots', 'Puss in Boots The Last Wish (2022)'], ['nimona', 'Nimona (2023)'],
  ['your-name', 'Your Name. (2016)']]) {
  const title = find(name);
  if (title && await backdrop(title, `still-${short}.jpg`)) console.log(`still-${short}.jpg`);
  else console.log(`::warning::no backdrop for ${name}`);
}

// The slug of a title's folder: « Blade Runner 2049 (2017) » → blade-runner-2049.
const slugOf = (folder) => folder.replace(/\s*\(\d{4}\)\s*$/, '').normalize('NFD').replace(/[\u0300-\u036f]/g, '')
  .toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
if (process.env.MIRA_STILLS_ALL === '1') {
  for (const title of titles) {
    const parts = (title.Path ?? '').replace(/\\/g, '/').split('/');
    // A film's path is its file (its folder before it), a series' its folder.
    const folder = title.Type === 'Movie' ? parts.at(-2) : parts.at(-1);
    if (!folder) continue;
    const slug = slugOf(folder);
    for (const [kind, path] of [['backdrop', 'Backdrop?maxWidth=3840&quality=92'], ['poster', 'Primary?maxHeight=1500&quality=92']]) {
      const image = await fetch(`${base}/Items/${title.Id}/Images/${path}`);
      if (image.ok) writeFileSync(join(out, `${kind}-${slug}.jpg`), Buffer.from(await image.arrayBuffer()));
      else console.log(`::warning::no ${kind} for ${folder}`);
    }
  }
  console.log(`${titles.length} titles' backdrops and posters`);
}
