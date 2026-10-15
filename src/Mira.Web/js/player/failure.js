// « Lecture impossible »: what the file holds, what Mira tried, and the report « Envoyer le détail à Jellyfin » leaves
// on the server (ClientLog/Document: a file upload_Mira_….log among Jellyfin's logs), which Mira for Windows lists for
// its administrator (Mira.Core PlaybackFailures, the same cases in tests/fixtures/failure.json). Nothing of the browser
// at load: Node tests it (tools/web/unit).
import { codecName } from './tracks.js';
import { episodeCode } from '../dom.js';

/** What the report says it is, for whoever reads Jellyfin's logs. */
export const REPORT_KIND = 'lecture-impossible';

const CONTAINERS = { matroska: 'MKV', mkv: 'MKV', webm: 'WebM', mp4: 'MP4', mov: 'MP4', m4v: 'MP4', avi: 'AVI', mpegts: 'TS', ts: 'TS', m2ts: 'M2TS', wmv: 'WMV', asf: 'WMV', flv: 'FLV', ogg: 'OGG' };
const VIDEO_CODECS = { h264: 'H.264', avc: 'H.264', hevc: 'HEVC', h265: 'HEVC', av1: 'AV1', vp9: 'VP9', vp8: 'VP8', mpeg2video: 'MPEG-2', mpeg4: 'MPEG-4', msmpeg4v3: 'DivX', vc1: 'VC-1', wmv3: 'WMV' };
const RANGES = { HDR10: 'HDR10', HDR10Plus: 'HDR10+', HLG: 'HLG' };
const CHANNELS = { 1: 'mono', 2: 'stéréo', 6: '5.1', 8: '7.1' };

/**
 * The file's container. Jellyfin may list a family (« mkv,webm », « mov,mp4,m4a,3gp,3g2,mj2 »): the one of the file's
 * extension when it is among them, else the first.
 */
function containerName(container, path) {
  const family = String(container ?? '').toLowerCase().split(',').map((c) => c.trim()).filter(Boolean);
  const extension = /\.([a-z0-9]+)$/i.exec(String(path ?? ''))?.[1].toLowerCase();
  const key = family.includes(extension) ? extension : family[0];
  return key ? CONTAINERS[key] ?? key.toUpperCase() : null;
}
function videoText(video) {
  if (!video) return null;
  const key = String(video.Codec ?? '').toLowerCase();
  const width = video.Width ?? 0, height = video.Height ?? 0;
  const size = width >= 3800 || height >= 2000 ? '4K' : width >= 2500 || height >= 1400 ? '1440p'
    : width >= 1800 || height >= 1000 ? '1080p' : width >= 1200 || height >= 700 ? '720p' : width > 0 || height > 0 ? 'SD' : '';
  const type = video.VideoRangeType ?? '';
  const range = RANGES[type] ?? (type.startsWith('DOVI') && type !== 'DOVIInvalid' ? 'Dolby Vision' : video.VideoRange === 'HDR' ? 'HDR' : '');
  return [key ? VIDEO_CODECS[key] ?? key.toUpperCase() : 'Vidéo', size, video.BitDepth > 8 ? `${video.BitDepth} bits` : '', range,
    video.IsInterlaced ? 'entrelacée' : ''].filter(Boolean).join(' ');
}
function audioText(audio) {
  if (!audio) return null;
  return [codecName(audio.Codec) ?? 'Audio', audio.Channels > 0 ? CHANNELS[audio.Channels] ?? `${audio.Channels} canaux` : ''].filter(Boolean).join(' ');
}

/**
 * What the file holds, in a line: « MKV · HEVC 4K 10 bits HDR10 · DTS 5.1 · sous-titres PGS ». The audio and
 * subtitles chosen (their Index), else the file's default audio and no subtitles. Empty without a media source.
 */
export function mediaLine(source, { audioIndex = null, subtitleIndex = null } = {}) {
  const streams = source?.MediaStreams ?? [];
  if (!source) return '';
  const audios = streams.filter((s) => s.Type === 'Audio');
  const audio = audios.find((s) => s.Index === audioIndex) ?? audios.find((s) => s.Index === source.DefaultAudioStreamIndex) ?? audios[0];
  const subtitle = subtitleIndex >= 0 ? streams.find((s) => s.Type === 'Subtitle' && s.Index === subtitleIndex) : null;
  return [containerName(source.Container, source.Path), videoText(streams.find((s) => s.Type === 'Video')), audioText(audio),
    subtitle ? `sous-titres ${codecName(subtitle.Codec) ?? '?'}` : null].filter(Boolean).join(' · ');
}

/** Jellyfin's TranscodeReasons in words, as « En ce moment sur Jellyfin » says them (Mira.Core Viewers.Reason). */
const REASONS = {
  ContainerNotSupported: 'format du fichier', VideoCodecNotSupported: 'codec vidéo', AudioCodecNotSupported: 'codec audio',
  SubtitleCodecNotSupported: 'sous-titres à incruster', AudioIsExternal: 'piste audio à part', SecondaryAudioNotSupported: 'piste audio secondaire',
  VideoProfileNotSupported: 'profil vidéo', VideoLevelNotSupported: 'niveau vidéo', VideoResolutionNotSupported: 'définition',
  VideoBitDepthNotSupported: 'vidéo 10 bits', VideoFramerateNotSupported: 'images par seconde', RefFramesNotSupported: 'images de référence',
  AnamorphicVideoNotSupported: 'image anamorphosée', InterlacedVideoNotSupported: 'image entrelacée', AudioChannelsNotSupported: 'canaux audio',
  AudioProfileNotSupported: 'profil audio', AudioSampleRateNotSupported: 'fréquence audio', AudioBitDepthNotSupported: 'audio haute résolution',
  ContainerBitrateExceedsLimit: 'débit au-dessus de la qualité choisie', VideoBitrateNotSupported: 'débit vidéo', AudioBitrateNotSupported: 'débit audio',
  UnknownVideoStreamInfo: 'vidéo mal décrite', UnknownAudioStreamInfo: 'audio mal décrit', DirectPlayError: 'lecture directe en échec',
  VideoRangeTypeNotSupported: 'HDR', VideoCodecTagNotSupported: 'étiquette du codec vidéo', StreamCountExceedsLimit: 'trop de pistes',
};
export const reasonText = (reason) => REASONS[reason] ?? reason;
/** The reasons in a conversion's address (TranscodeReasons=A,B), never the address itself: it carries the token. */
export const transcodeReasons = (address) =>
  (new URLSearchParams(String(address ?? '').split('?')[1] ?? '').get('TranscodeReasons') ?? '').split(',').filter(Boolean);

const STEPS = { DirectPlay: 'Fichier lu tel quel', DirectStream: 'Vidéo copiée par Jellyfin', Transcode: 'Vidéo convertie par Jellyfin' };
/**
 * One stream Mira tried, in words: « Vidéo convertie par Jellyfin (codec vidéo, vidéo 10 bits) : Erreur 4 : … ». attempt:
 * { stage, method, reasons, error }; stage 2 converts everything, whatever Jellyfin copies. From stage 1, Mira itself
 * turns direct play off: Jellyfin's « DirectPlayError » then says nothing of the file.
 */
export function attemptText({ stage = 0, method = '', reasons = [], error = '' }) {
  const step = stage >= 2 ? 'Tout converti par Jellyfin en H.264 et AAC stéréo' : STEPS[method] ?? 'Demandé à Jellyfin';
  const why = [...new Set(reasons.filter((r) => stage === 0 || r !== 'DirectPlayError').map(reasonText))].join(', ');
  return `${step}${why ? ` (${why})` : ''}${error ? ` : ${error}` : ''}`;
}

/** The title as the report names it: « Série · S1 · É2 · Titre », or the film's name. */
export function reportTitle(item) {
  if (!item) return '';
  if (item.Type !== 'Episode') return item.Name ?? '';
  return [item.SeriesName, episodeCode(item), item.Name].filter(Boolean).join(' · ');
}

/** A stream's description for the report: what decides how it plays, nothing of where it lives (no Path, no address). */
const STREAM_FIELDS = ['Type', 'Index', 'Codec', 'Profile', 'Level', 'BitDepth', 'Width', 'Height', 'AverageFrameRate', 'BitRate',
  'VideoRange', 'VideoRangeType', 'IsInterlaced', 'Channels', 'ChannelLayout', 'SampleRate', 'Language', 'IsForced', 'IsExternal',
  'IsTextSubtitleStream', 'DeliveryMethod'];
const pick = (stream) => Object.fromEntries(STREAM_FIELDS.filter((key) => stream[key] != null && stream[key] !== '').map((key) => [key, stream[key]]));
/** The file's own name, without the folders it is in (they may say much of the PC). */
export const fileName = (path) => String(path ?? '').split(/[\\/]/).pop() || null;

/**
 * The report « Envoyer le détail à Jellyfin » sends (JSON, as text): what the screen says, the file's name and
 * streams, what Mira tried, the device and Mira's version. Never the token, a path or a stream's address.
 */
export function failureReport({ item, source, audioIndex = null, subtitleIndex = null, message, attempts = [], device, user, version, browser, at = new Date() }) {
  const file = source ?? item?.MediaSources?.[0] ?? null;
  return JSON.stringify({
    mira: REPORT_KIND,
    at: at.toISOString(),
    title: reportTitle(item),
    itemId: item?.Id ?? null,
    device, user: user ?? null, version, browser: browser ?? null,
    file: fileName(file?.Path),
    media: mediaLine(file, { audioIndex, subtitleIndex }),
    message,
    attempts: attempts.map(attemptText),
    container: file?.Container ?? null,
    bitrate: file?.Bitrate ?? null,
    streams: (file?.MediaStreams ?? []).map(pick),
  }, null, 2);
}
