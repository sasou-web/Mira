// The films' backdrops a release's banner is lit by (tools/banner/banner.mjs), still-<name>.jpg, from a Jellyfin set up
// on the library of tools/banner/media.sh (tools/gallery/setup.mjs), through its API. First one more library scan and
// a wait for the films' genres: Jellyfin lists them (Items/Filters2, the chips of the Films page Mira for Mac's
// gallery captures next) only once a scan has gone past the metadata TMDb gave.
//   node tools/banner/stills.mjs <server> <user> <password> <folder>
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

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
// Not Spirited Away: its first backdrop on TMDb is a photo of Hayao Miyazaki.
for (const [short, name] of [['arcane', 'Arcane (2021)'], ['spider-verse', 'Spider-Man Across the Spider-Verse (2023)'], ['klaus', 'Klaus (2019)'],
  ['coco', 'Coco (2017)'], ['wild-robot', 'The Wild Robot (2024)'], ['puss-in-boots', 'Puss in Boots The Last Wish (2022)'], ['nimona', 'Nimona (2023)'],
  ['your-name', 'Your Name. (2016)']]) {
  const title = find(name);
  const still = title && await fetch(`${base}/Items/${title.Id}/Images/Backdrop?maxWidth=2400&quality=90`);
  if (still?.ok) { writeFileSync(join(out, `still-${short}.jpg`), Buffer.from(await still.arrayBuffer())); console.log(`still-${short}.jpg`); }
  else console.log(`::warning::no backdrop for ${name}`);
}
