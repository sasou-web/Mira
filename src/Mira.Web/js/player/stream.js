// What the player asks Jellyfin for, and what it does when a stream fails: plain functions of their inputs, no page,
// no video, no timer, so that Node can test them (tools/web/unit). player.js holds the state and does the rest.
import { appleStream } from './tracks.js';

// ---------- Bitrate ----------

/** Full quality: the file as it is whenever Jellyfin can send it. */
export const FULL_BITRATE = 120_000_000;
/** Asked when the connection could not be measured. */
export const UNMEASURED_BITRATE = 8_000_000;
/** How long a measure of the connection is kept. */
export const MEASURE_KEPT = 60 * 60_000;

/** A connection that holds this many times the file's bitrate plays the file as it is. */
export const FULL_MARGIN = 1.5;

/**
 * The bitrate known without measuring: the one chosen in the settings, full quality (« Maximale », or at home on
 * « Automatique »), or what a measure still fresh allows for this file (autoBitrate); null when the connection must
 * be measured. measure: { at, speed } (bits per second).
 */
export function knownBitrate(choice, { atHome = false, measure = null, now = Date.now(), fileBitrate = 0 } = {}) {
  if (typeof choice === 'number') return choice;
  if (choice === 'max' || atHome) return FULL_BITRATE;
  if (measure && now - measure.at < MEASURE_KEPT) return autoBitrate(measure.speed, fileBitrate);
  return null;
}
/** What a measured speed (bits per second) allows for a conversion: 70 % of it, between 1.5 and 40 Mb/s. */
export const measuredBitrate = (measured) => Math.max(1_500_000, Math.min(40_000_000, Math.round(measured * 0.7)));
/**
 * « Automatique » with a measure: full quality, the file as it is (a 4K film or a remux above 40 Mb/s too), when the
 * connection holds about one and a half times its bitrate; else what the measure allows (measuredBitrate), converted.
 */
export const autoBitrate = (speed, fileBitrate) => (fileBitrate > 0 && speed >= fileBitrate * FULL_MARGIN ? FULL_BITRATE : measuredBitrate(speed));

// ---------- The cascade: a stream the player refused, asked again with more of it converted ----------
// Stage 0: as Jellyfin decides (the file as it is when it can, its video or audio copied into HLS); 1: its video
// converted; 2: everything converted (lastResortProfile). Kept for the title (seeks, other tracks), 0 for the next one.

export const LAST_STAGE = 2;
/** The stage after this one; null once everything is converted. */
export const nextStage = (stage) => (stage >= LAST_STAGE ? null : stage + 1);

/** What PlaybackInfo is asked at a stage (lastResort: lastResortProfile instead of deviceProfile). */
export const stageRequest = (stage) => ({
  lastResort: stage >= 2,
  EnableDirectPlay: stage === 0, EnableDirectStream: stage === 0,
  AllowVideoStreamCopy: stage === 0, AllowAudioStreamCopy: stage < 2,
});

/** The stage in the message of a failed stream. */
export function stageText(stage, playMethod = '') {
  if (playMethod === 'DirectPlay') return 'fichier lu tel quel';
  return stage === 0 ? 'conversion de Jellyfin' : stage === 1 ? 'vidéo convertie' : 'tout converti';
}
/** An error of the <video>: « Erreur 4 : … ». */
export const videoError = ({ code, message }) => `Erreur ${code ?? '?'}${message ? ` : ${message}` : ''}`;
/** The detail under « Lecture impossible » for an error of the <video>, with the stage it came at. */
export const videoErrorText = ({ code, message, playMethod, stage }) => `${videoError({ code, message })} (${stageText(stage, playMethod)}).`;
/** The same for a fatal error of hls.js. */
export const hlsError = ({ details, type }) => `Erreur ${details ?? type}`;
export const hlsErrorText = ({ details, type, stage }) => `${hlsError({ details, type })} (${stageText(stage)}).`;

/**
 * An error of the <video>. A file Safari was thought to play directly but cannot (3: decode, 4: format): converted
 * at once (« convert »). Anything else: the server is asked first (« check », see afterCheck).
 */
export const videoErrorStep = ({ playMethod, stage, code }) =>
  (playMethod === 'DirectPlay' && stage === 0 && (code === 3 || code === 4) ? 'convert' : 'check');

/**
 * A fatal error of hls.js: a decoding hiccup has the decoder started again, once (« recover »); twice, or anything
 * else, the stream is refused (« refused »: the server is asked, see afterCheck). Not fatal: « ignore ».
 */
export function hlsErrorStep({ fatal, mediaError, recovered }) {
  if (!fatal) return 'ignore';
  return mediaError && !recovered ? 'recover' : 'refused';
}

/**
 * The server asked after a stream broke. Silent (PC asleep, Wi-Fi to 4G): Mira waits for it (« wait »). Answering,
 * the stream itself is at fault: the next stage (« next ») while there is one (canGoOn: nextStage of the stage that
 * failed), else « Lecture impossible » (« fail »). A lower quality alone would change nothing: Jellyfin copies what
 * fits under it.
 */
export const afterCheck = ({ up, canGoOn }) => (!up ? 'wait' : canGoOn ? 'next' : 'fail');

/** Where to pick the title up again: where it plays, or, before it has played, where it was asked to start (ticks). */
export const resumeTicks = ({ streamPlaying, progressive, positionTicks, currentStart }) =>
  (streamPlaying || progressive ? positionTicks : currentStart);

// ---------- The stream Jellyfin offers ----------

const ERRORS = {
  NotAllowed: 'Ton compte Jellyfin n’a pas le droit de lire ce titre.',
  NoCompatibleStream: 'Jellyfin ne sait pas convertir ce fichier pour cet appareil.',
  RateLimitExceeded: 'Le serveur a atteint sa limite de lectures simultanées.',
};
/** The message for PlaybackInfo's ErrorCode. */
export const playbackError = (code) => ERRORS[code] ?? 'Jellyfin n’a pas pu préparer ce titre pour cet appareil.';

/**
 * Why Jellyfin converts, as its address says (TranscodeReasons): none of these, and the video is copied as it is
 * (Jellyfin 10.9 and later write VideoCodec= even then).
 */
const VIDEO_REASONS = /Video|RefFrames|ContainerBitrate|SubtitleCodec/;
export function copiesVideo(address) {
  if (/[?&]allowVideoStreamCopy=false/i.test(address)) return false;
  const reasons = new URLSearchParams(address.split('?')[1] ?? '').get('TranscodeReasons') ?? '';
  return !VIDEO_REASONS.test(reasons);
}

/**
 * The stream to play from a media source of PlaybackInfo, before it is signed: the file as it is (MP4 first among
 * its containers) at stage 0 when Jellyfin allows it, else its conversion, rewritten for Apple's player (native) when
 * it is HLS. { address, hlsStream, inband, playMethod, progressive }; null when Jellyfin offers nothing.
 */
export function chooseStream(source, { itemId, deviceId, stage, native, subtitleIndex }) {
  if (source.SupportsDirectPlay && !source.TranscodingUrl && stage === 0) {
    const container = (source.Container ?? 'mp4').split(',').find((c) => c === 'mp4') ?? (source.Container ?? 'mp4').split(',')[0];
    return {
      address: `Videos/${itemId}/stream.${container}?Static=true&mediaSourceId=${encodeURIComponent(source.Id)}&deviceId=${deviceId}${source.ETag ? `&Tag=${source.ETag}` : ''}`,
      hlsStream: false, inband: false, playMethod: 'DirectPlay', progressive: false,
    };
  }
  if (!source.TranscodingUrl) return null;
  let address = source.TranscodingUrl, inband = false;
  const hlsStream = source.TranscodingSubProtocol === 'hls' || /\.m3u8/i.test(address);
  // Apple's player: every text subtitle in the HLS stream, for its menu (see appleStream).
  if (native && hlsStream) ({ address, inband } = appleStream(address, source, subtitleIndex));
  return { address, hlsStream, inband, playMethod: copiesVideo(source.TranscodingUrl) ? 'DirectStream' : 'Transcode', progressive: !hlsStream };
}

/** The chosen subtitles are pictures, drawn into the video by Jellyfin (Apple's menu cannot turn those off). */
export const burnedIn = (stream, transcodingUrl) => !!stream && !stream.IsTextSubtitleStream && /SubtitleMethod=Encode/i.test(transcodingUrl ?? '');

/**
 * Other subtitles chosen: text subtitles already beside the video, or in the HLS stream, switch at once (« show »),
 * leaving burned-in ones asks for a new conversion too (« reopen »); pictures need a new conversion.
 */
export function subtitleSwitch({ subtitle, stream, inband, wasBurned }) {
  const show = subtitle === -1 || stream?.DeliveryMethod === 'External' || !!(inband && stream?.IsTextSubtitleStream);
  return { show, reopen: !show || wasBurned };
}

/** An ASS or SSA file beside the video, asked as WebVTT: the only text a browser reads, which Jellyfin makes of it. */
export const webVttAddress = (deliveryUrl) => deliveryUrl.replace(/\/Stream\.(ass|ssa)(?=\?|$)/i, '/Stream.vtt');

// ---------- Where a title starts ----------

/** How far a title was watched, 0 to 1. */
export const progressFraction = (x) => (x.RunTimeTicks ? (x.UserData?.PlaybackPositionTicks ?? 0) / x.RunTimeTicks : 0);
/** The choices of « Reprendre un peu avant », in seconds; 0 resumes exactly where the title was left. */
export const RESUME_BACK = [0, 5, 10, 15];
/** Under this much watched (seconds), a title resumes exactly where it was left. */
export const RESUME_BACK_FROM = 30;
/**
 * « Reprendre un peu avant » (seconds): `back` seconds before where the title was left, to pick up the thread rather
 * than land mid-sentence; exactly there under RESUME_BACK_FROM. As Mira.Core PlaybackSession.ResumeBack
 * (tests/fixtures/resume.json).
 */
export const resumeBack = (position, back) => (!(back > 0) || position < RESUME_BACK_FROM ? position : Math.max(0, position - back));
/**
 * Where a title starts (ticks): where it was left, `back` seconds before (resumeBack), unless asked from the start,
 * resuming is off, or it was nearly done.
 */
export function startPosition(item, { fromStart, resume, back = 0 }) {
  const fraction = progressFraction(item);
  if (fromStart || !resume || fraction <= 0 || fraction >= 0.95) return 0;
  const position = item.UserData.PlaybackPositionTicks, at = resumeBack(position / 10_000_000, back);
  return at === position / 10_000_000 ? position : Math.round(at * 10_000_000);
}
