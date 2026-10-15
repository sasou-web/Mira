// « En ce moment sur Jellyfin »: who plays what on the server (its Sessions), and how: the file as it is, or converted,
// on which hardware and why. Written as Mira for Windows writes it (Mira.Core/Viewers.cs), with shared cases in
// tests/fixtures/viewers.json. Imported by Node: nothing of the browser here.
import { codecName } from './player/tracks.js';

/**
 * The sessions playing something, as Jellyfin 12.1 lists them (Sessions?activeWithinSeconds=960): its PlayState and
 * TranscodingInfo. `ownDevice` is this device's id: its own session, if it is there.
 */
export function parseViewers(sessions, ownDevice) {
  if (!Array.isArray(sessions)) return [];
  const found = [];
  for (const session of sessions) {
    const item = session?.NowPlayingItem;
    if (!isObject(session) || !isObject(item) || !text(session.Id)) continue;
    const state = isObject(session.PlayState) ? session.PlayState : {};
    const t = isObject(session.TranscodingInfo) ? session.TranscodingInfo : null;
    found.push({
      sessionId: session.Id,
      own: !!ownDevice && text(session.DeviceId) === ownDevice,
      user: text(session.UserName) ?? '',
      device: text(session.DeviceName) ?? '',
      client: text(session.Client) ?? '',
      address: text(session.RemoteEndPoint),
      type: text(item.Type) ?? '',
      name: text(item.Name) ?? '',
      series: text(item.SeriesName),
      season: Number.isInteger(item.ParentIndexNumber) ? item.ParentIndexNumber : null,
      episode: Number.isInteger(item.IndexNumber) ? item.IndexNumber : null,
      position: seconds(state.PositionTicks),
      duration: seconds(item.RunTimeTicks),
      paused: state.IsPaused === true,
      method: text(state.PlayMethod) ?? '',
      conversion: t && {
        videoDirect: t.IsVideoDirect === true, audioDirect: t.IsAudioDirect === true,
        videoCodec: text(t.VideoCodec), audioCodec: text(t.AudioCodec),
        bitrate: Number.isInteger(t.Bitrate) ? t.Bitrate : null,
        reasons: Array.isArray(t.TranscodeReasons) ? t.TranscodeReasons.filter((x) => typeof x === 'string') : [],
        hardware: text(t.HardwareAccelerationType),
      },
      canStop: session.SupportsRemoteControl === true,
    });
  }
  return found;
}
const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const text = (value) => (typeof value === 'string' && value.length > 0 ? value : null);
const seconds = (ticks) => (Number.isInteger(ticks) ? ticks / 1e7 : 0);

/** « ogsas · iPhone · Tailscale »; `own` for this device's own session (« ce PC » on Windows). */
export function who(viewer, own = 'cet appareil') {
  if (viewer.own) return own;
  return [viewer.user, viewer.device || viewer.client, place(viewer.address)].filter(Boolean).join(' · ');
}

/** Where the device is: Tailscale, the home network, the Internet; null for the server's own PC or no address. */
export function place(address) {
  const raw = String(address ?? '').trim().replace(/^\[|\]$/g, '').replace(/%.*$/, '');
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(raw);
  const v4 = ipv4(mapped ? mapped[1] : raw);
  if (v4) {
    const [a, b] = v4;
    if (a === 127) return null;
    if (a === 100 && b >= 64 && b < 128) return 'Tailscale';
    const home = a === 10 || (a === 172 && b >= 16 && b < 32) || (a === 192 && b === 168) || (a === 169 && b === 254);
    return home ? 'réseau de la maison' : 'Internet';
  }
  const v6 = ipv6(raw);
  if (!v6) return null;
  if (v6.every((x, i) => x === (i === 7 ? 1 : 0))) return null;
  // Tailscale's own IPv6 range, fd7a:115c:a1e0::/48, before the private ranges it belongs to.
  if (v6[0] === 0xfd7a && v6[1] === 0x115c && v6[2] === 0xa1e0) return 'Tailscale';
  const home = (v6[0] & 0xffc0) === 0xfe80 || (v6[0] & 0xfe00) === 0xfc00;
  return home ? 'réseau de la maison' : 'Internet';
}
function ipv4(value) {
  const parts = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(value);
  if (!parts) return null;
  const bytes = parts.slice(1).map(Number);
  return bytes.every((x) => x <= 255) ? bytes : null;
}
/** The 8 groups of an IPv6 address, or null. */
function ipv6(value) {
  if (!value.includes(':') || (value.match(/::/g) ?? []).length > 1) return null;
  const read = (part) => (part ? part.split(':') : []);
  const [head, tail] = value.includes('::') ? value.split('::') : [value, null];
  let groups = read(head), after = tail === null ? [] : read(tail);
  // An IPv4 address written at the end (« ::1.2.3.4 ») counts for two groups.
  const last = (tail === null ? groups : after).at(-1);
  const v4 = last && ipv4(last);
  if (v4) {
    const words = [(v4[0] << 8 | v4[1]).toString(16), (v4[2] << 8 | v4[3]).toString(16)];
    if (tail === null) groups = [...groups.slice(0, -1), ...words]; else after = [...after.slice(0, -1), ...words];
  }
  if (![...groups, ...after].every((x) => /^[0-9a-f]{1,4}$/i.test(x))) return null;
  const missing = 8 - groups.length - after.length;
  if (tail === null ? missing !== 0 : missing < 1) return null;
  return [...groups, ...Array(tail === null ? 0 : missing).fill('0'), ...after].map((x) => parseInt(x, 16));
}

/** « Courte HLS · S1 É2 · Le départ », « Signes Test (2022) ». */
export function titleOf(viewer) {
  const code = viewer.episode == null ? null : viewer.season == null ? `É${viewer.episode}` : `S${viewer.season} É${viewer.episode}`;
  return viewer.type === 'Episode' && viewer.series ? [viewer.series, code, viewer.name].filter(Boolean).join(' · ') : viewer.name;
}

/** « Courte HLS · S1 É2 · Le départ — 12:34 / 40:00, en pause ». */
export function what(viewer) {
  const title = titleOf(viewer);
  const time = viewer.duration > 0 ? `${clock(viewer.position)} / ${clock(viewer.duration)}` : clock(viewer.position);
  return `${title} — ${time}${viewer.paused ? ', en pause' : ''}`;
}

/** « 1:02:03 », « 12:34 ». */
export function clock(value) {
  const total = Math.floor(Math.max(0, Number.isFinite(value) ? value : 0));
  const h = Math.floor(total / 3600), m = Math.floor(total / 60) % 60, s = total % 60;
  const two = (n) => String(n).padStart(2, '0');
  return h >= 1 ? `${h}:${two(m)}:${two(s)}` : `${Math.floor(total / 60)}:${two(s)}`;
}

const MAKERS = { nvenc: 'NVIDIA', qsv: 'Intel', amf: 'AMD' };

/**
 * How it plays: « Lecture directe : le fichier tel quel », « Fichier remis dans un autre format, sans conversion »,
 * or « Conversion : image en VP9, son en Opus, par le processeur · 1,6 Mb/s », and why (null when Jellyfin says nothing).
 */
export function how(viewer) {
  const c = viewer.conversion;
  if (viewer.method === 'DirectPlay' || (!viewer.method && !c)) return { how: 'Lecture directe : le fichier tel quel', why: null };
  if (viewer.method === 'DirectStream' || (c?.videoDirect && c?.audioDirect)) return { how: 'Fichier remis dans un autre format, sans conversion', why: why(c) };
  if (!c) return { how: 'Conversion', why: null };
  const parts = [
    c.videoDirect ? 'image copiée' : `image en ${codecName(c.videoCodec) ?? '?'}`,
    c.audioDirect ? 'son copié' : `son en ${codecName(c.audioCodec) ?? '?'}`,
  ];
  const on = c.videoDirect ? '' : c.hardware && c.hardware !== 'none' ? `, par la carte graphique (${MAKERS[c.hardware] ?? c.hardware})` : ', par le processeur';
  const rate = c.bitrate > 0 ? ` · ${(c.bitrate / 1e6).toFixed(1).replace('.', ',')} Mb/s` : '';
  return { how: `Conversion : ${parts.join(', ')}${on}${rate}`, why: why(c) };
}
const why = (c) => (c?.reasons.length ? `Pourquoi : ${[...new Set(c.reasons.map(reason))].join(', ')}` : null);

const REASONS = {
  ContainerNotSupported: 'format du fichier',
  VideoCodecNotSupported: 'codec vidéo',
  AudioCodecNotSupported: 'codec audio',
  SubtitleCodecNotSupported: 'sous-titres à incruster',
  AudioIsExternal: 'piste audio à part',
  SecondaryAudioNotSupported: 'piste audio secondaire',
  VideoProfileNotSupported: 'profil vidéo',
  VideoLevelNotSupported: 'niveau vidéo',
  VideoResolutionNotSupported: 'définition',
  VideoBitDepthNotSupported: 'vidéo 10 bits',
  VideoFramerateNotSupported: 'images par seconde',
  RefFramesNotSupported: 'images de référence',
  AnamorphicVideoNotSupported: 'image anamorphosée',
  InterlacedVideoNotSupported: 'image entrelacée',
  AudioChannelsNotSupported: 'canaux audio',
  AudioProfileNotSupported: 'profil audio',
  AudioSampleRateNotSupported: 'fréquence audio',
  AudioBitDepthNotSupported: 'audio haute résolution',
  ContainerBitrateExceedsLimit: 'débit au-dessus de la qualité choisie',
  VideoBitrateNotSupported: 'débit vidéo',
  AudioBitrateNotSupported: 'débit audio',
  UnknownVideoStreamInfo: 'vidéo mal décrite',
  UnknownAudioStreamInfo: 'audio mal décrit',
  DirectPlayError: 'lecture directe en échec',
  VideoRangeTypeNotSupported: 'HDR',
  VideoCodecTagNotSupported: 'étiquette du codec vidéo',
  StreamCountExceedsLimit: 'trop de pistes',
};
/** Jellyfin's TranscodeReasons in words; the name itself for one Mira does not know. */
export const reason = (name) => (Object.hasOwn(REASONS, name) ? REASONS[name] : name);
