// A stand-in Jellyfin for the tests of Mira for iPhone on the simulator (ios.yml): the answers Mira asks for to sign in
// and to draw Home, about the Blender Foundation's free films and Pioneer One, with pictures drawn here (gradients),
// so the captures need neither a real server nor anything from the Internet; Tears of Steel has a real backdrop and a
// logo (media/backdrop.jpg, media/logo.png), to see a title's page as with a real library. Account « ogsas », password
// « mira ».
// Every film and episode plays media/clip.mp4 (SMPTE bars and a tone, 10 s, H.264 and AAC in MP4) as it is; what the
// player reports is listed at /Mock/Reports for the UI tests. Sintel plays media/film.mp4 instead when ios.yml could
// fetch it (its trailer, © Blender Foundation, CC BY 3.0), at its real length, with chapters and the trickplay
// pictures of media/trickplay (tools/ios/trickplay.sh): the player's captures.
// The tests switch it (POST): /Mock/Slow?ms=… answers the account's requests that much later (pictures and the server's
// public answer stay quick), /Mock/Down cuts every connection but /Mock's (a PC switched off), /Mock/Revoke refuses the
// account's token from then on (its session ended from Jellyfin's dashboard; signing in again gives it back),
// /Mock/Admin makes the account an administrator, /Mock/Extension serves the page of Mira's extension at /Mira/ (as a
// server with it; not its HLS playlists: no player test asks for it), /Mock/Up undoes them all;
// /Mock/Mark?name=… notes when a moment of a filmed test came (/Mock/Marks, read by ios.yml to cut the film).
// « Keep on this iPhone »: Items/{id}/Download sends the file (ranges), and Cosmos Laundromat's PlaybackInfo, asked with
// no subtitles from its start (as the app asks to keep a title), is a conversion of an MKV: its HLS playlists cut
// media/clip.mp4 into an init segment and segments, put end to end again by the app; its subtitles as WebVTT; where a
// title kept was watched without the mock, told by UserItems/{id}/UserData (listed with the reports, « /UserData »).
// As Jellyfin: Home's lists (Resume, NextUp, the latest, the favourites, what was watched) carry a title's files
// (MediaSources) only when `fields` asks for them, and PlaybackInfo reads the tracks asked only for the file named
// (MediaSourceId, 12.1); each ask is listed at /Mock/PlaybackInfo.
// Elephants Dream is in two files, as Jellyfin puts « cd1 » and « cd2 » together: one title (PartCount) and its second
// file, an item of its own hidden from the library (Videos/{id}/AdditionalParts); both play media/clip.mp4.
//   node tools/ios/mock-jellyfin.mjs [port]   (18097 by default, on 127.0.0.1)
import http from 'node:http';
import { existsSync, readFileSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import { createHash, randomUUID } from 'node:crypto';

const clip = readFileSync(new URL('media/clip.mp4', import.meta.url));
const filmUrl = new URL('media/film.mp4', import.meta.url), trickplayUrl = new URL('media/trickplay/', import.meta.url);
const filmData = existsSync(filmUrl) ? readFileSync(filmUrl) : null;
const trickplay = filmData && existsSync(new URL('info.json', trickplayUrl)) ? JSON.parse(readFileSync(new URL('info.json', trickplayUrl), 'utf8')) : null;
/** An MP4's length in seconds, from its movie header (mvhd). */
function mp4Seconds(data) {
  const at = data.indexOf('mvhd');
  if (at < 0) return 0;
  const v1 = data[at + 4] === 1;
  const scale = data.readUInt32BE(at + (v1 ? 24 : 16));
  const length = v1 ? Number(data.readBigUInt64BE(at + 28)) : data.readUInt32BE(at + 20);
  return scale ? length / scale : 0;
}
const reports = [];
/** PlaybackInfo's asks: the title, the file named, the tracks asked. */
const asks = [];
let slow = 0, down = false, revoked = false, administrator = false, extension = false;
const marks = [];

const port = Number(process.argv[2] ?? 18097);
const TOKEN = 'mock-token';
// A friend's party on this Jellyfin (Mira's extension): its link's code, and the session of the guest it opens.
const PARTY = 'samsam-party-link-0001';
const GUEST_TOKEN = 'mock-guest-token', GUEST_ID = 'c0ffee00c0ffee00c0ffee00c0ffee00';
let guestName = '';
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
    'In an attempt to save the world from destructive robots, a group of warriors and scientists stage a dramatic breakup scene in Amsterdam.',
    { ImageTags: { ...art('b2e1d4c3f6a75b8e9d0c1f2e3a4b5c6d', 'Primary', 'Thumb'), Logo: 'l-b2e1d4c3' }, CommunityRating: 6.4, OfficialRating: 'PG-13' }),
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
// Cast and crew, as Items/{id} gives them (a person's page: Items?personIds=, Items/{person}, Persons?searchTerm=).
const person = (id, name, extra = {}) => ({ Id: id, Name: name, Type: 'Person', ...extra });
const persons = [
  person('a0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5', 'Derek de Lint', {
    ImageTags: { Primary: 'p-derek' }, PremiereDate: '1950-07-15T00:00:00.0000000Z', ProductionLocations: ['The Hague, Netherlands'],
    Overview: 'Dutch actor, on stage and in films since the 1970s.',
  }),
  person('b1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6', 'Vanja Rukavina'),
  person('c2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7', 'Sergio Hasselbaink', { ImageTags: { Primary: 'p-sergio' } }),
  person('d3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8', 'Ian Hubert', { ImageTags: { Primary: 'p-ian' } }),
  person('e4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9', 'Colin Levy', { ImageTags: { Primary: 'p-colin' } }),
  person('f5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0', 'Halina Reijn'),
];
const credit = (index, type, role) => ({ Id: persons[index].Id, Name: persons[index].Name, Type: type, Role: role, PrimaryImageTag: persons[index].ImageTags?.Primary });
films[0].People = [credit(4, 'Director'), credit(5, 'Actor', 'Sintel')];
films[1].People = [credit(3, 'Director'), credit(0, 'Actor', 'Thom'), credit(1, 'Actor', 'Celia'), credit(2, 'Actor', 'Barley'), credit(0, 'Actor', 'Old Thom')];
const SERIES = 'c9d8e1f0a3b42c5d6e7f8a9b0c1d2e3f';
const SEASON = 'd0e9f2a1b4c53d6e7f8a9b0c1d2e3f40';
// A season announced by TheTVDB that the library has no episode of (virtual).
const MISSING_SEASON = 'd1e0f3a2b5c64d7e8f9a0b1c2d3e4f51';
const MOVIES = 'f1e2d3c4b5a69788796a5b4c3d2e1f00', SHOWS = 'f2e3d4c5b6a7988796a5b4c3d2e1f011';
const series = {
  Id: SERIES, Name: 'Pioneer One', Type: 'Series', ProductionYear: 2010, ChildCount: 1, Genres: ['Drama', 'Science Fiction'],
  // In HTML, as AniList's summaries come through Jellyfin: the app shows it as text, four lines then « More ».
  Overview: 'An object in the sky spreads radiation over North America.<br>\n<br>\nFearing terrorism, U.S. Homeland Security agents are dispatched to investigate. '
    + 'What they find in the wreckage changes the way they see the &laquo;&nbsp;space race&nbsp;&raquo; and the story they were told about it.<br>\n<br>\n'
    + 'They must decide what to tell the world, and how much of it, before somebody else does.<br>\n<br>\n(Source: <i>VODO</i>)<br>',
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
// What TheTVDB's extension adds to a series (virtual episodes with their air day): one aired 5 days ago that the
// library lacks, one coming in 3 days (EpisodeCalendar).
const virtualEpisodes = [
  { ...episode(4, 'Triangle', { PremiereDate: days(5) }), LocationType: 'Virtual' },
  { ...episode(5, 'The Sun Never Sets', { PremiereDate: days(-3) }), LocationType: 'Virtual' },
];
// Collections made by hand, kept until the mock stops: one there from the start, empty.
const boxSets = [{ Id: 'b0c1d2e3f4a5968778695a4b3c2d1e0f', Name: 'Movie nights', Type: 'BoxSet', ChildCount: 0, Path: '/config/data/collections/Movie nights [boxset]', titles: [] }];
const boxSet = (x) => ({ Id: x.Id, Name: x.Name, Type: x.Type, ChildCount: x.titles.length, Path: x.Path });
// The title kept as Jellyfin's conversion, and the pieces of its stream.
const converted = films[3];
const pieces = [clip.subarray(0, 4096)];
for (let at = 4096, size = Math.ceil((clip.length - 4096) / 4); at < clip.length; at += size) pieces.push(clip.subarray(at, Math.min(clip.length, at + size)));
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
// The film of the player's captures: Sintel at its trailer's length, left at 40 %, with named chapters.
if (filmData) {
  const sintel = films[0], seconds = mp4Seconds(filmData), ticks = Math.round(seconds * tick);
  sintel.RunTimeTicks = ticks;
  sintel.UserData = { ...sintel.UserData, PlaybackPositionTicks: Math.round(ticks * 0.4) };
  sintel.Chapters = [['Prologue', 0], ['Scales', 0.18], ['The search', 0.42], ['The cave', 0.7]]
    .map(([Name, at]) => ({ Name, StartPositionTicks: Math.round(ticks * at) }));
  if (trickplay) sintel.Trickplay = { [sintel.Id]: { [String(trickplay.Width)]: trickplay } };
  console.log(`Sintel plays media/film.mp4 (${seconds.toFixed(1)} s)${trickplay ? ', with trickplay pictures' : ''}`);
}
for (const item of everything) if (item.Type !== 'Series') item.MediaSources = [mediaSource(item)];
// The film in two files, and its second file: asked by its id (PlaybackInfo, Download, Items/{id}, UserData), never
// listed.
const parted = films[5];
parted.PartCount = 2;
const secondPart = { Id: 'f6a5b8c7d0e19f2a3b4c5d6e7f8a9b0d', Name: parted.Name, Type: 'Video', RunTimeTicks: parted.RunTimeTicks,
  UserData: { Played: false, PlaybackPositionTicks: 0 } };
secondPart.MediaSources = [mediaSource(secondPart)];
const known = [...everything, secondPart];
const latest = [films[1], series, films[2], films[3], films[4], films[0], films[5], films[6], films[7]];

function send(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(body === undefined ? '' : JSON.stringify(body));
}
const list = (items) => ({ Items: items, TotalRecordCount: items.length, StartIndex: 0 });
/** A list as Jellyfin sends it: the titles' files only when `fields` asks for them. */
const listed = (items, q) => list((q.get('fields') ?? '').split(',').includes('MediaSources') ? items : items.map(({ MediaSources, ...rest }) => rest));

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

// « Watch together »: one group at a time, Jellyfin's messages through its WebSocket (written here by hand: frames of
// text, masked from the app), the requests listed at /Mock/SyncPlay. Ready while the group waits plays it 1 s later.
const sockets = new Set();
const syncLog = [];
let group = null, queue = null;
const jellyfinDate = (ms) => new Date(ms).toISOString().replace('Z', '0000Z');
function frame(text) {
  const data = Buffer.from(text);
  const head = data.length < 126 ? Buffer.from([0x81, data.length]) : Buffer.from([0x81, 126, data.length >> 8, data.length & 255]);
  return Buffer.concat([head, data]);
}
const broadcast = (message) => { for (const socket of sockets) socket.write(frame(JSON.stringify(message))); };
const groupUpdate = (type, data) => broadcast({ MessageType: 'SyncPlayGroupUpdate', Data: { GroupId: group?.GroupId, Type: type, Data: data } });
function command(name, ticks, delay = 0) {
  broadcast({ MessageType: 'SyncPlayCommand', Data: { GroupId: group.GroupId, PlaylistItemId: queue?.PlaylistItemId ?? '', When: jellyfinDate(Date.now() + delay), PositionTicks: ticks, Command: name, EmittedAt: jellyfinDate(Date.now()) } });
}
function state(name) { group.State = name; groupUpdate('StateUpdate', { State: name, Reason: 'Mock' }); }
function syncPlay(what, body, logged = what) {
  syncLog.push({ what: logged, body });
  switch (what) {
    case 'New':
      group = { GroupId: randomUUID().replaceAll('-', ''), GroupName: body.GroupName, State: 'Idle', Participants: ['ogsas'], LastUpdatedAt: jellyfinDate(Date.now()) };
      queue = null;
      groupUpdate('GroupJoined', group); state('Idle');
      return;
    case 'Join':
      if (!group) { broadcast({ MessageType: 'SyncPlayGroupUpdate', Data: { Type: 'GroupDoesNotExist', Data: '' } }); return; }
      groupUpdate('GroupJoined', group);
      return;
    case 'Leave': groupUpdate('GroupLeft', group?.GroupId); group = null; queue = null; return;
    case 'SetNewQueue': {
      if (!group) return;
      queue = { ItemId: body.PlayingQueue[0], PlaylistItemId: randomUUID().replaceAll('-', '') };
      groupUpdate('PlayQueue', { Reason: 'NewPlaylist', LastUpdate: jellyfinDate(Date.now()), Playlist: [queue], PlayingItemIndex: 0, StartPositionTicks: body.StartPositionTicks ?? 0, IsPlaying: false, ShuffleMode: 'Sorted', RepeatMode: 'RepeatNone' });
      state('Waiting');
      return;
    }
    case 'Ready':
      if (group?.State === 'Waiting') { command('Unpause', body.PositionTicks ?? 0, 1000); state('Playing'); }
      return;
    case 'Pause': if (group) { command('Pause', body.PositionTicks ?? 0); state('Paused'); } return;
    case 'Unpause': if (group) { command('Unpause', 0, 500); state('Playing'); } return;
    case 'Seek': if (group) { command('Seek', body.PositionTicks ?? 0); state('Waiting'); } return;
    default: return;
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  // The gateway of a friend's party's guests: Jellyfin's paths under /Mira/guest, with the guest's token.
  const guest = url.pathname.startsWith('/Mira/guest/');
  const path = guest ? url.pathname.slice('/Mira/guest'.length) : url.pathname, q = url.searchParams;
  let raw = ''; for await (const part of req) raw += part;
  console.log(req.method, req.url);
  if (req.method === 'POST' && ['/Mock/Slow', '/Mock/Down', '/Mock/Revoke', '/Mock/Up'].includes(path)) {
    slow = path === '/Mock/Slow' ? Math.min(60_000, Math.max(0, Number(q.get('ms') ?? 3000))) : 0;
    down = path === '/Mock/Down';
    revoked = path === '/Mock/Revoke';
    if (revoked) for (const socket of sockets) socket.destroy();
    if (down) for (const socket of sockets) socket.destroy();
    if (path === '/Mock/Up') administrator = extension = false;
    return send(res, 204, undefined);
  }
  if (req.method === 'POST' && path === '/Mock/Admin') { administrator = true; return send(res, 204, undefined); }
  if (req.method === 'POST' && path === '/Mock/Extension') { extension = true; return send(res, 204, undefined); }
  if (path === '/Mock/Mark' && req.method === 'POST') { marks.push({ name: q.get('name') ?? '', at: Date.now() }); return send(res, 204, undefined); }
  if (path === '/Mock/Marks') return send(res, 200, marks);
  if (down && !path.startsWith('/Mock/')) { req.socket.destroy(); return; }
  // The page of Mira's extension, served to anyone; without the extension, as on most servers, Jellyfin has none (404).
  if (url.pathname === '/Mira/') {
    if (!extension) return send(res, 404, undefined);
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
    return res.end('<!doctype html><title>Mira</title>');
  }
  if (url.pathname === `/Mira/party/${PARTY}`) return send(res, 200, { host: 'Sam' });
  if (url.pathname === '/Mira/guest/open' && req.method === 'POST') {
    const body = JSON.parse(raw || '{}');
    if (body.party !== PARTY || !/^[A-Za-z0-9_-]{16,64}$/.test(body.device ?? '')) return send(res, 404, { error: 'This party link no longer exists.' });
    guestName = body.guest || 'Guest';
    return send(res, 200, { token: GUEST_TOKEN, userId: GUEST_ID, userName: guestName, serverId: SERVER_ID, host: 'Sam' });
  }
  // The host, Sam, opens their group (the test says when).
  if (path === '/Mock/PartyGroup' && req.method === 'POST') {
    group = { GroupId: randomUUID().replaceAll('-', ''), GroupName: "Sam's group", State: 'Idle', Participants: ['Sam'], LastUpdatedAt: jellyfinDate(Date.now()) };
    queue = null;
    return send(res, 204, undefined);
  }
  if (path === '/System/Info/Public') return send(res, 200, { LocalAddress: `http://127.0.0.1:${port}`, ServerName: 'Maison', Version: '12.1.0', ProductName: 'Jellyfin Server', Id: SERVER_ID, StartupWizardCompleted: true });
  if (path === '/Mock/Reports') return send(res, 200, reports);
  if (path === '/Mock/PlaybackInfo') return send(res, 200, asks);
  if (path === '/Mock/SyncPlay') return send(res, 200, { group, queue, requests: syncLog });
  // The stream needs no header: AVPlayer reads it by its address, the session in it (ApiKey).
  const video = /^\/Videos\/([0-9a-f]+)\/stream\.mp4$/.exec(path);
  if (video) {
    if (q.get('ApiKey') !== TOKEN) return send(res, 401, undefined);
    const data = filmData && video[1] === films[0].Id ? filmData : clip;
    const range = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
    if (!range) {
      res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': data.length, 'Accept-Ranges': 'bytes' });
      return res.end(req.method === 'HEAD' ? undefined : data);
    }
    const start = Number(range[1]), end = Math.min(data.length - 1, range[2] ? Number(range[2]) : data.length - 1);
    res.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Length': end - start + 1, 'Content-Range': `bytes ${start}-${end}/${data.length}`, 'Accept-Ranges': 'bytes' });
    return res.end(req.method === 'HEAD' ? undefined : data.subarray(start, end + 1));
  }
  if (path === '/System/Ping') { res.writeHead(200, { 'Content-Type': 'text/plain' }); return res.end('Jellyfin Server'); }
  // Tears of Steel's own pictures, as a title of a real library has.
  const file = path === `/Items/${films[1].Id}/Images/Backdrop/0` ? 'backdrop.jpg' : path === `/Items/${films[1].Id}/Images/Logo` ? 'logo.png' : null;
  if (file) {
    res.writeHead(200, { 'Content-Type': file.endsWith('.png') ? 'image/png' : 'image/jpeg', 'Cache-Control': 'public, max-age=31536000' });
    return res.end(readFileSync(new URL(`media/${file}`, import.meta.url)));
  }
  const tile = /^\/Videos\/([0-9a-f]+)\/Trickplay\/(\d+)\/(\d+)\.jpg$/.exec(path);
  if (tile) {
    if (!(req.headers.authorization ?? '').includes(`Token="${TOKEN}"`)) return send(res, 401, undefined);
    const file = new URL(`${tile[2]}/${tile[3]}.jpg`, trickplayUrl);
    if (!trickplay || tile[1] !== films[0].Id || !existsSync(file)) return send(res, 404, undefined);
    res.writeHead(200, { 'Content-Type': 'image/jpeg' });
    return res.end(readFileSync(file));
  }
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
    revoked = false;
    return send(res, 200, { User: { Id: 'f0e1d2c3b4a5968778695a4b3c2d1e0f', Name: body.Username, ServerId: SERVER_ID }, AccessToken: TOKEN, ServerId: SERVER_ID });
  }
  if ((revoked && !guest) || !(req.headers.authorization ?? '').includes(`Token="${guest ? GUEST_TOKEN : TOKEN}"`)) return send(res, 401, undefined);
  if (slow) await new Promise((resolve) => setTimeout(resolve, slow));
  if (path === '/Sessions/Logout') { if (guest) syncLog.push({ what: 'GuestLogout', body: {} }); return send(res, 204, undefined); }
  if (path === '/Sessions/Capabilities') return send(res, 204, undefined);
  if (path === '/GetUtcTime') { const now = jellyfinDate(Date.now()); return send(res, 200, { RequestReceptionTime: now, ResponseTransmissionTime: now }); }
  if (path === '/SyncPlay/List') return send(res, 200, group ? [group] : []);
  const sync = /^\/SyncPlay\/(\w+)$/.exec(path);
  if (sync && req.method === 'POST') {
    if (guest && sync[1] === 'Join' && group && !group.Participants.includes(guestName)) group.Participants.push(guestName);
    syncPlay(sync[1], JSON.parse(raw || '{}'), guest ? 'Guest' + sync[1] : sync[1]);
    return send(res, 204, undefined);
  }
  // The account's policy: allowed to manage collections, not an administrator (but after /Mock/Admin).
  if (path === '/Users/Me') return send(res, 200, { Id: 'f0e1d2c3b4a5968778695a4b3c2d1e0f', Name: 'ogsas', Policy: { IsAdministrator: administrator, EnableCollectionManagement: true } });
  if (path === '/Items' && q.get('locationTypes') === 'Virtual') return send(res, 200, list(virtualEpisodes));
  if (path === '/Items' && q.get('includeItemTypes') === 'BoxSet') return send(res, 200, list(boxSets.map(boxSet)));
  const holding = /^\/Items\/([0-9a-f]+)\/Collections$/.exec(path);
  if (holding) return send(res, 200, list(boxSets.filter((x) => x.titles.includes(holding[1])).map(boxSet)));
  if (path === '/Collections' && req.method === 'POST') {
    const made = { Id: randomUUID().replaceAll('-', ''), Name: q.get('name') ?? '', Type: 'BoxSet', Path: `/config/data/collections/${q.get('name')} [boxset]`, titles: (q.get('ids') ?? '').split(',').filter(Boolean) };
    boxSets.push(made);
    return send(res, 200, { Id: made.Id });
  }
  const filling = /^\/Collections\/([0-9a-f]+)\/Items$/.exec(path);
  if (filling) {
    const box = boxSets.find((x) => x.Id === filling[1]);
    if (!box) return send(res, 404, undefined);
    const ids = (q.get('ids') ?? '').split(',').filter(Boolean);
    box.titles = req.method === 'DELETE' ? box.titles.filter((x) => !ids.includes(x)) : [...new Set([...box.titles, ...ids])];
    return send(res, 204, undefined);
  }
  if (path === '/UserItems/Resume') return send(res, 200, listed([episodes[1], films[0]], q));
  if (path === '/Shows/NextUp') return send(res, 200, listed([q.get('seriesId') ? episodes[1] : episodes[2]], q));
  // An account that shows missing episodes (Jellyfin's « Display missing episodes within seasons »): as Jellyfin 12.1,
  // its episodes and seasons come with the virtual ones (and a season of missing episodes only) unless isMissing=false.
  const missingToo = q.get('isMissing') !== 'false';
  if (path === `/Shows/${SERIES}/Seasons`) {
    return send(res, 200, list([{ Id: SEASON, Name: 'Season 1', Type: 'Season', IndexNumber: 1, SeriesId: SERIES },
      ...(missingToo ? [{ Id: MISSING_SEASON, Name: 'Season 2', Type: 'Season', IndexNumber: 2, SeriesId: SERIES, LocationType: 'Virtual' }] : [])]));
  }
  if (path === `/Shows/${SERIES}/Episodes`) {
    const shown = missingToo ? [...episodes, ...virtualEpisodes] : episodes;
    const from = Math.max(0, shown.findIndex((x) => x.Id === q.get('startItemId')));
    return send(res, 200, list(shown.slice(from, from + Number(q.get('limit') ?? 100))));
  }
  const parts = /^\/Videos\/([0-9a-f]+)\/AdditionalParts$/.exec(path);
  if (parts) return send(res, 200, list(parts[1] === parted.Id ? [secondPart] : []));
  const playback = /^\/Items\/([0-9a-f]+)\/PlaybackInfo$/.exec(path);
  if (playback && req.method === 'POST') {
    const item = known.find((x) => x.Id === playback[1]);
    if (!item?.MediaSources) return send(res, 404, undefined);
    const body = JSON.parse(raw || '{}'), session = `play-${reports.length}`;
    asks.push({ item: item.Id, source: body.MediaSourceId ?? null, audio: body.AudioStreamIndex ?? null, subtitles: body.SubtitleStreamIndex ?? null });
    // The tracks asked, read for the file named only: Jellyfin's own default ones otherwise.
    const named = body.MediaSourceId === item.MediaSources[0].Id;
    if (item === converted && body.SubtitleStreamIndex === -1 && !body.StartTimeTicks) {
      const source = { ...item.MediaSources[0], Container: 'mkv', SupportsDirectPlay: false, SupportsDirectStream: false, TranscodingSubProtocol: 'hls',
        TranscodingUrl: `/videos/${item.Id}/master.m3u8?MediaSourceId=${item.Id}&PlaySessionId=${session}&AudioStreamIndex=${(named ? body.AudioStreamIndex : null) ?? 1}` };
      return send(res, 200, { MediaSources: [source], PlaySessionId: session });
    }
    return send(res, 200, { MediaSources: item.MediaSources, PlaySessionId: session });
  }
  const download = /^\/Items\/([0-9a-f]+)\/Download$/.exec(path);
  if (download) {
    if (!known.some((x) => x.Id === download[1] && x.MediaSources)) return send(res, 404, undefined);
    const range = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
    if (!range) {
      res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': clip.length, 'Accept-Ranges': 'bytes' });
      return res.end(clip);
    }
    const start = Number(range[1]), end = Math.min(clip.length - 1, range[2] ? Number(range[2]) : clip.length - 1);
    res.writeHead(206, { 'Content-Type': 'video/mp4', 'Content-Length': end - start + 1, 'Content-Range': `bytes ${start}-${end}/${clip.length}`, 'Accept-Ranges': 'bytes' });
    return res.end(clip.subarray(start, end + 1));
  }
  if (path === `/videos/${converted.Id}/master.m3u8`) {
    res.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
    return res.end(`#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000,CODECS="avc1.64001e,mp4a.40.2"\nmain.m3u8?${q}\n`);
  }
  if (path === `/videos/${converted.Id}/main.m3u8`) {
    const lines = ['#EXTM3U', '#EXT-X-VERSION:7', '#EXT-X-TARGETDURATION:3', '#EXT-X-MEDIA-SEQUENCE:0', '#EXT-X-PLAYLIST-TYPE:VOD',
      `#EXT-X-MAP:URI="hls1/main/-1.mp4?${q}"`];
    for (let n = 0; n < pieces.length - 1; n++) lines.push('#EXTINF:2.5,', `hls1/main/${n}.mp4?${q}`);
    lines.push('#EXT-X-ENDLIST', '');
    res.writeHead(200, { 'Content-Type': 'application/vnd.apple.mpegurl' });
    return res.end(lines.join('\n'));
  }
  const segment = new RegExp(`^/videos/${converted.Id}/hls1/main/(-?\\d+)\\.mp4$`).exec(path);
  if (segment) {
    const piece = pieces[Number(segment[1]) + 1];
    if (!piece) return send(res, 404, undefined);
    res.writeHead(200, { 'Content-Type': 'video/mp4', 'Content-Length': piece.length });
    return res.end(piece);
  }
  if (/^\/Videos\/[0-9a-f]+\/[0-9a-f]+\/Subtitles\/3\/0\/Stream\.vtt$/.test(path)) {
    res.writeHead(200, { 'Content-Type': 'text/vtt' });
    return res.end('WEBVTT\n\n00:00:01.000 --> 00:00:04.000\nBonjour, tout le monde.\n\n00:00:05.000 --> 00:00:08.000\nOn regarde sans connexion.\n');
  }
  const userData = /^\/UserItems\/([0-9a-f]+)\/UserData$/.exec(path);
  if (userData && req.method === 'POST') {
    const item = known.find((x) => x.Id === userData[1]);
    if (!item) return send(res, 404, undefined);
    const body = JSON.parse(raw || '{}');
    item.UserData = { ...item.UserData, PlaybackPositionTicks: body.PlaybackPositionTicks ?? 0, ...(body.LastPlayedDate ? { LastPlayedDate: body.LastPlayedDate } : {}) };
    reports.push({ kind: '/UserData', item: item.Id, position: body.PlaybackPositionTicks });
    return send(res, 200, item.UserData);
  }
  if (path.startsWith('/Sessions/Playing')) {
    const body = JSON.parse(raw || '{}');
    reports.push({ kind: path.slice('/Sessions/Playing'.length) || '/Start', item: body.ItemId, method: body.PlayMethod, position: body.PositionTicks, paused: body.IsPaused });
    return send(res, 204, undefined);
  }
  // Watched and favorites, kept until the mock stops.
  const mark = /^\/User(Played|Favorite)Items\/([0-9a-f]+)$/.exec(path);
  if (mark) {
    const item = known.find((x) => x.Id === mark[2]);
    if (!item) return send(res, 404, undefined);
    const on = req.method === 'POST';
    item.UserData = { ...item.UserData, ...(mark[1] === 'Played' ? { Played: on, PlaybackPositionTicks: 0 } : { IsFavorite: on }) };
    return send(res, 200, item.UserData);
  }
  const one = /^\/Items\/([0-9a-f]+)$/.exec(path);
  if (path === '/Persons') {
    const term = (q.get('searchTerm') ?? '').toLowerCase();
    return send(res, 200, list(persons.filter((x) => term && x.Name.toLowerCase().includes(term))));
  }
  if (one) { const item = [...known, ...persons].find((x) => x.Id === one[1]); return item ? send(res, 200, item) : send(res, 404, undefined); }
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
      && (!q.get('personIds') || (x.People ?? []).some((p) => p.Id === q.get('personIds')))
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
    if (q.get('sortBy') === 'DatePlayed') return send(res, 200, listed(everything.filter((x) => x.UserData?.LastPlayedDate), q));
    if (q.get('isFavorite') === 'true') return send(res, 200, listed(everything.filter((x) => x.UserData?.IsFavorite && x.Type !== 'Episode'), q));
    return send(res, 200, listed(latest.slice(0, Number(q.get('limit') ?? 24)), q));
  }
  return send(res, 404, undefined);
});
server.on('upgrade', (req, socket) => {
  if (down) { socket.destroy(); return; }
  const url = new URL(req.url, 'http://localhost');
  const key = req.headers['sec-websocket-key'];
  const guest = url.pathname === '/Mira/guest/socket';
  if ((url.pathname !== '/socket' && !guest) || !key || (revoked && !guest) || !(req.headers.authorization ?? '').includes(`Token="${guest ? GUEST_TOKEN : TOKEN}"`)) { socket.end('HTTP/1.1 401 Unauthorized\r\n\r\n'); return; }
  const accept = createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  sockets.add(socket);
  console.log('socket open');
  socket.write(frame(JSON.stringify({ MessageType: 'ForceKeepAlive', Data: 60 })));
  let pending = Buffer.alloc(0);
  socket.on('data', (chunk) => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 2) {
      const opcode = pending[0] & 15, masked = pending[1] & 128;
      let length = pending[1] & 127, at = 2;
      if (length === 126) { length = pending.readUInt16BE(2); at = 4; } else if (length === 127) { length = Number(pending.readBigUInt64BE(2)); at = 10; }
      const total = at + (masked ? 4 : 0) + length;
      if (pending.length < total) return;
      const mask = masked ? pending.subarray(at, at + 4) : null;
      const data = Buffer.from(pending.subarray(at + (masked ? 4 : 0), total));
      if (mask) for (let i = 0; i < data.length; i++) data[i] ^= mask[i % 4];
      pending = pending.subarray(total);
      if (opcode === 8) { socket.end(); return; }
      if (opcode === 9) socket.write(Buffer.concat([Buffer.from([0x8a, data.length]), data]));
    }
  });
  const drop = () => { sockets.delete(socket); console.log('socket closed'); };
  socket.on('close', drop);
  socket.on('error', drop);
});
server.listen(port, '127.0.0.1', () => console.log(`Mock Jellyfin ready on 127.0.0.1:${port}`));
