// A stand-in Jellyfin for the tests of Mira for iPhone on the simulator (ios.yml): the answers Mira asks for to sign in
// and to draw Home, about the Blender Foundation's free films and Pioneer One, with pictures drawn here (gradients),
// so the captures need neither a real server nor anything from the Internet. Account « ogsas », password « mira ».
// Every film and episode plays media/clip.mp4 (SMPTE bars and a tone, 10 s, H.264 and AAC in MP4) as it is; what the
// player reports is listed at /Mock/Reports for the UI tests.
//   node tools/ios/mock-jellyfin.mjs [port]   (18097 by default, on 127.0.0.1)
import http from 'node:http';
import { readFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';

const clip = readFileSync(new URL('media/clip.mp4', import.meta.url));
const reports = [];

const port = Number(process.argv[2] ?? 18097);
const TOKEN = 'mock-token';
const SERVER_ID = 'b6c2f1a0d4e94c0a9f3e2d1c0b9a8f7e';
const tick = 10_000_000;
const minutes = (m) => m * 60 * tick;
const days = (n) => new Date(Date.now() - n * 86_400_000).toISOString().replace('Z', '0000Z');

const art = (id, ...types) => Object.fromEntries(types.map((t) => [t, `${t[0].toLowerCase()}-${id}`]));
function film(id, name, year, length, genres, overview, extra = {}) {
  return {
    Id: id, Name: name, Type: 'Movie', ProductionYear: year, RunTimeTicks: minutes(length), Genres: genres, Overview: overview,
    ImageTags: art(id, 'Primary', 'Thumb'), BackdropImageTags: [`b-${id}`], UserData: { Played: false, IsFavorite: false, PlaybackPositionTicks: 0 }, ...extra,
  };
}
const films = [
  film('a1f0c3d2e5b64a7f8c9d0e1f2a3b4c5d', 'Sintel', 2010, 15, ['Animation', 'Fantasy'],
    'A lonely young woman, Sintel, helps and befriends a dragon, whom she calls Scales. When he is kidnapped by an adult dragon, she sets out on a quest to find him.',
    { UserData: { Played: false, IsFavorite: true, PlaybackPositionTicks: minutes(6), LastPlayedDate: days(1) } }),
  film('b2e1d4c3f6a75b8e9d0c1f2e3a4b5c6d', 'Tears of Steel', 2012, 12, ['Science Fiction'],
    'In an attempt to save the world from destructive robots, a group of warriors and scientists stage a dramatic breakup scene in Amsterdam.'),
  film('c3d2e5f4a7b86c9d0e1f2a3b4c5d6e7f', 'Spring', 2019, 8, ['Animation', 'Fantasy'],
    'The story of a shepherd girl and her dog, who face ancient spirits in order to continue the cycle of life.', { UserData: { Played: true, IsFavorite: true, PlaybackPositionTicks: 0 } }),
  film('d4e3f6a5b8c97d0e1f2a3b4c5d6e7f8a', 'Cosmos Laundromat', 2015, 12, ['Animation', 'Comedy'],
    'On a desolate island, a suicidal sheep named Franck meets his fate in a quirky salesman, who offers him the gift of a lifetime.'),
  film('e5f4a7b6c9d08e1f2a3b4c5d6e7f8a9b', 'Big Buck Bunny', 2008, 10, ['Animation', 'Comedy'],
    'A large and lovable rabbit deals with three tiny bullies, led by a flying squirrel, who are determined to squelch his happiness.'),
  film('f6a5b8c7d0e19f2a3b4c5d6e7f8a9b0c', 'Elephants Dream', 2006, 11, ['Animation', 'Science Fiction'],
    'Two strange characters explore a capricious and seemingly infinite machine.'),
  film('a7b6c9d8e1f20a3b4c5d6e7f8a9b0c1d', 'Agent 327: Operation Barbershop', 2017, 4, ['Animation', 'Action'],
    'Agent 327 is investigating a clue that leads him to a shady barbershop in Amsterdam.', { UserData: { Played: false, IsFavorite: true, PlaybackPositionTicks: 0 } }),
  film('b8c7d0e9f2a31b4c5d6e7f8a9b0c1d2e', 'Sprite Fright', 2021, 10, ['Animation', 'Horror', 'Comedy'],
    'When a group of rowdy teenagers trek into an isolated forest, they discover peaceful mushroom creatures that turn out to be an unexpected force of nature.'),
];
const SERIES = 'c9d8e1f0a3b42c5d6e7f8a9b0c1d2e3f';
const SEASON = 'd0e9f2a1b4c53d6e7f8a9b0c1d2e3f40';
const MOVIES = 'f1e2d3c4b5a69788796a5b4c3d2e1f00', SHOWS = 'f2e3d4c5b6a7988796a5b4c3d2e1f011';
const series = {
  Id: SERIES, Name: 'Pioneer One', Type: 'Series', ProductionYear: 2010, ChildCount: 1, Genres: ['Drama', 'Science Fiction'],
  Overview: 'An object in the sky spreads radiation over North America. Fearing terrorism, U.S. Homeland Security agents are dispatched to investigate.',
  ImageTags: art(SERIES, 'Primary', 'Thumb'), BackdropImageTags: [`b-${SERIES}`], UserData: { Played: false, IsFavorite: true, UnplayedItemCount: 4 },
};
function episode(number, name, extra = {}) {
  const id = `e${number}`.padEnd(32, '0');
  return {
    Id: id, Name: name, Type: 'Episode', SeriesId: SERIES, SeasonId: SEASON, SeriesName: 'Pioneer One', ParentIndexNumber: 1, IndexNumber: number,
    RunTimeTicks: minutes(36), ImageTags: art(id, 'Primary'), ParentThumbItemId: SERIES, ParentThumbImageTag: `t-${SERIES}`,
    ParentBackdropItemId: SERIES, ParentBackdropImageTags: [`b-${SERIES}`], SeriesPrimaryImageTag: `p-${SERIES}`,
    Overview: 'Agent Tom Taylor brings a man found in the wreckage of a Soviet capsule in for questioning.',
    UserData: { Played: false, PlaybackPositionTicks: 0 }, ...extra,
  };
}
const episodes = [
  episode(1, 'Earthfall', { UserData: { Played: true, PlaybackPositionTicks: 0, LastPlayedDate: days(3) } }),
  episode(2, 'The Man from Mars', { UserData: { Played: false, PlaybackPositionTicks: minutes(21), LastPlayedDate: days(0) } }),
  episode(3, 'Alone in the Night'),
];
const everything = [...films, series, ...episodes];
// Each playable title: one MP4 file, its tracks.
const mediaSource = (item) => ({
  Id: item.Id, Container: 'mov,mp4,m4a', Bitrate: 30_000, RunTimeTicks: item.RunTimeTicks, SupportsDirectPlay: true, SupportsDirectStream: true,
  DefaultAudioStreamIndex: 1, MediaStreams: [
    { Index: 0, Type: 'Video', Codec: 'h264', Width: 640, Height: 360 },
    { Index: 1, Type: 'Audio', Codec: 'aac', Language: 'eng', Channels: 1, IsDefault: true },
    { Index: 2, Type: 'Audio', Codec: 'aac', Language: 'fre', Channels: 1 },
    { Index: 3, Type: 'Subtitle', Codec: 'subrip', Language: 'fre', IsExternal: true, IsTextSubtitleStream: true },
  ],
});
for (const item of everything) if (item.Type !== 'Series') item.MediaSources = [mediaSource(item)];
const latest = [films[1], series, films[2], films[3], films[4], films[0], films[5], films[6], films[7]];

function send(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(body === undefined ? '' : JSON.stringify(body));
}
const list = (items) => ({ Items: items, TotalRecordCount: items.length, StartIndex: 0 });

// Pictures: a gradient of two colours taken from the item's id, at the asked width, as a PNG.
const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc = (buffer) => { let c = 0xffffffff; for (const byte of buffer) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };
function chunk(type, data) {
  const head = Buffer.alloc(8); head.writeUInt32BE(data.length, 0); head.write(type, 4, 'ascii');
  const tail = Buffer.alloc(4); tail.writeUInt32BE(crc(Buffer.concat([head.subarray(4), data])), 0);
  return Buffer.concat([head, data, tail]);
}
function hue(text, shift) {
  let h = 0; for (const c of text) h = (h * 31 + c.charCodeAt(0)) % 360;
  const a = ((h + shift) % 360) / 60, x = 1 - Math.abs((a % 2) - 1), [r, g, b] = [[1, x, 0], [x, 1, 0], [0, 1, x], [0, x, 1], [x, 0, 1], [1, 0, x]][Math.floor(a) % 6];
  return [r, g, b];
}
function png(id, width, height) {
  const top = hue(id, 0).map((v) => 40 + v * 150), bottom = hue(id, 140).map((v) => 10 + v * 70);
  const rows = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3);
    for (let x = 0; x < width; x++) {
      const t = y / height, glow = Math.max(0, 1 - Math.hypot(x / width - 0.3, y / height - 0.35) * 1.6) * 60;
      for (let c = 0; c < 3; c++) row[1 + x * 3 + c] = Math.min(255, top[c] * (1 - t) + bottom[c] * t + glow);
    }
    rows.push(row);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(Buffer.concat(rows))), chunk('IEND', Buffer.alloc(0))]);
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const path = url.pathname, q = url.searchParams;
  let raw = ''; for await (const part of req) raw += part;
  console.log(req.method, req.url);
  if (path === '/System/Info/Public') return send(res, 200, { LocalAddress: `http://127.0.0.1:${port}`, ServerName: 'Maison', Version: '12.1.0', ProductName: 'Jellyfin Server', Id: SERVER_ID, StartupWizardCompleted: true });
  if (path === '/Mock/Reports') return send(res, 200, reports);
  // The stream needs no header: AVPlayer reads it by its address, the session in it (ApiKey).
  const video = /^\/Videos\/([0-9a-f]+)\/stream\.mp4$/.exec(path);
  if (video) {
    if (q.get('ApiKey') !== TOKEN) return send(res, 401, undefined);
    const range = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
    if (!range) {
      res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': clip.length, 'Accept-Ranges': 'bytes' });
      return res.end(req.method === 'HEAD' ? undefined : clip);
    }
    const start = Number(range[1]), end = Math.min(clip.length - 1, range[2] ? Number(range[2]) : clip.length - 1);
    res.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Length': end - start + 1, 'Content-Range': `bytes ${start}-${end}/${clip.length}`, 'Accept-Ranges': 'bytes' });
    return res.end(req.method === 'HEAD' ? undefined : clip.subarray(start, end + 1));
  }
  if (path === '/System/Ping') { res.writeHead(200, { 'Content-Type': 'text/plain' }); return res.end('Jellyfin Server'); }
  const image = /^\/Items\/([0-9a-f]+)\/Images\/(Primary|Thumb|Backdrop)(?:\/\d+)?$/.exec(path);
  if (image) {
    const width = Math.min(1200, Math.max(40, Number(q.get('maxWidth') ?? 400)));
    const poster = image[2] === 'Primary' && !episodes.some((x) => x.Id === image[1]);
    const height = Math.round(poster ? width * 1.5 : width * 9 / 16);
    res.writeHead(200, { 'Content-Type': 'image/png', 'Cache-Control': 'public, max-age=31536000' });
    return res.end(png(image[1] + image[2], width, height));
  }
  if (path === '/Users/AuthenticateByName' && req.method === 'POST') {
    const body = JSON.parse(raw || '{}');
    if (body.Pw !== 'mira') return send(res, 401, undefined);
    return send(res, 200, { User: { Id: 'f0e1d2c3b4a5968778695a4b3c2d1e0f', Name: body.Username, ServerId: SERVER_ID }, AccessToken: TOKEN, ServerId: SERVER_ID });
  }
  if (!(req.headers.authorization ?? '').includes(`Token="${TOKEN}"`)) return send(res, 401, undefined);
  if (path === '/Sessions/Logout') return send(res, 204, undefined);
  if (path === '/UserItems/Resume') return send(res, 200, list([episodes[1], films[0]]));
  if (path === '/Shows/NextUp') return send(res, 200, list([q.get('seriesId') ? episodes[1] : episodes[2]]));
  if (path === `/Shows/${SERIES}/Seasons`) return send(res, 200, list([{ Id: SEASON, Name: 'Season 1', Type: 'Season', IndexNumber: 1, SeriesId: SERIES }]));
  if (path === `/Shows/${SERIES}/Episodes`) {
    const from = Math.max(0, episodes.findIndex((x) => x.Id === q.get('startItemId')));
    return send(res, 200, list(episodes.slice(from, from + Number(q.get('limit') ?? 100))));
  }
  const playback = /^\/Items\/([0-9a-f]+)\/PlaybackInfo$/.exec(path);
  if (playback && req.method === 'POST') {
    const item = everything.find((x) => x.Id === playback[1]);
    if (!item?.MediaSources) return send(res, 404, undefined);
    return send(res, 200, { MediaSources: item.MediaSources, PlaySessionId: `play-${reports.length}` });
  }
  if (path.startsWith('/Sessions/Playing')) {
    const body = JSON.parse(raw || '{}');
    reports.push({ kind: path.slice('/Sessions/Playing'.length) || '/Start', item: body.ItemId, method: body.PlayMethod, position: body.PositionTicks, paused: body.IsPaused });
    return send(res, 204, undefined);
  }
  const one = /^\/Items\/([0-9a-f]+)$/.exec(path);
  if (one) { const item = everything.find((x) => x.Id === one[1]); return item ? send(res, 200, item) : send(res, 404, undefined); }
  if (path === '/UserViews') {
    return send(res, 200, list([
      { Id: MOVIES, Name: 'Movies', Type: 'CollectionFolder', CollectionType: 'movies' },
      { Id: SHOWS, Name: 'Shows', Type: 'CollectionFolder', CollectionType: 'tvshows' },
    ]));
  }
  if (path === '/Items/Filters2') {
    const kinds = (q.get('includeItemTypes') ?? '').split(',');
    const genres = [...new Set(everything.filter((x) => kinds.includes(x.Type)).flatMap((x) => x.Genres ?? []))].sort();
    return send(res, 200, { Genres: genres.map((name) => ({ Name: name, Id: name })) });
  }
  if (path === '/Years') {
    const kinds = (q.get('includeItemTypes') ?? '').split(',');
    const years = [...new Set(everything.filter((x) => kinds.includes(x.Type)).map((x) => x.ProductionYear))].sort((a, b) => b - a);
    return send(res, 200, list(years.map((year) => ({ Id: String(year), Name: String(year), Type: 'Year' }))));
  }
  // A library's grid (startIndex) and Search (searchTerm): filtered, sorted and cut as Jellyfin does.
  if (path === '/Items' && (q.has('startIndex') || q.has('searchTerm'))) {
    const kinds = (q.get('includeItemTypes') ?? '').split(',');
    const term = (q.get('searchTerm') ?? '').toLowerCase();
    let found = everything.filter((x) => kinds.includes(x.Type)
      && (!term || x.Name.toLowerCase().includes(term))
      && (!q.get('genres') || (x.Genres ?? []).includes(q.get('genres')))
      && (!q.get('years') || String(x.ProductionYear) === q.get('years'))
      && (q.get('isPlayed') !== 'false' || !x.UserData?.Played)
      && (q.get('isFavorite') !== 'true' || x.UserData?.IsFavorite));
    const by = (q.get('sortBy') ?? 'SortName').split(',')[0];
    const key = { SortName: (x) => x.Name, ProductionYear: (x) => x.ProductionYear, CommunityRating: (x) => x.CommunityRating ?? 0,
      DatePlayed: (x) => x.UserData?.LastPlayedDate ?? '', DateCreated: (x) => everything.length - everything.indexOf(x) }[by] ?? ((x) => x.Name);
    found = [...found].sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0) || a.Name.localeCompare(b.Name));
    if (q.get('sortOrder') === 'Descending') found.reverse();
    const start = Number(q.get('startIndex') ?? 0), limit = Number(q.get('limit') ?? 60);
    return send(res, 200, { Items: found.slice(start, start + limit), TotalRecordCount: found.length, StartIndex: start });
  }
  if (path === '/Items') {
    if (q.get('sortBy') === 'DatePlayed') return send(res, 200, list(everything.filter((x) => x.UserData?.LastPlayedDate)));
    if (q.get('isFavorite') === 'true') return send(res, 200, list(everything.filter((x) => x.UserData?.IsFavorite && x.Type !== 'Episode')));
    return send(res, 200, list(latest.slice(0, Number(q.get('limit') ?? 24))));
  }
  return send(res, 404, undefined);
});
server.listen(port, '127.0.0.1', () => console.log(`Mock Jellyfin ready on 127.0.0.1:${port}`));
