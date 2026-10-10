// What this browser plays, told to Jellyfin as a device profile: it then sends the file as it is when it can
// (direct play), or converts it on the fly into HLS that Safari plays natively (hls.js elsewhere).

const probe = document.createElement('video');
const can = (type) => /^(probably|maybe)$/.test(probe.canPlayType(type));
const Source = window.ManagedMediaSource ?? window.MediaSource;
const mse = (type) => !!Source?.isTypeSupported?.(type);
const agent = navigator.userAgent;

export const nativeHls = can('application/vnd.apple.mpegurl') || can('application/x-mpegURL');
/** hls.js is only loaded where HLS is not built in (not Safari) and Media Source Extensions exist. */
export const useHlsJs = !nativeHls && !!Source;
/** Safari lists a file's audio tracks and switches between them; elsewhere, another track means a conversion. */
export const audioTracks = 'audioTracks' in HTMLMediaElement.prototype;
/** WebKit (iPhone, iPad, Safari on Mac): its rules for HEVC are not the other browsers'. */
const webkit = nativeHls && /AppleWebKit/.test(agent) && !/Chrome|Android/.test(agent);

export const support = (() => {
  const h264 = can('video/mp4; codecs="avc1.640029"');
  const hevc = can('video/mp4; codecs="hvc1.1.6.L120.90"') || can('video/mp4; codecs="hev1.1.6.L120.90"');
  // The highest HEVC level the browser says it decodes, in Main 10 then Main (jellyfin-web asks the same way): a GPU
  // without Main 10 still plays 8-bit HEVC at 4.1; 4.0 when it says nothing.
  const hevcLevel = [186, 183, 153, 123].find((l) => can(`video/mp4; codecs="hvc1.2.4.L${l}"`))
    ?? [153, 123].find((l) => can(`video/mp4; codecs="hvc1.1.6.L${l}.90"`) || can(`video/mp4; codecs="hev1.1.6.L${l}.90"`))
    ?? 120;
  return {
    h264, hevc, hevcLevel,
    av1: can('video/mp4; codecs="av01.0.08M.08"'),
    vp9: can('video/webm; codecs="vp9"'),
    vp8: can('video/webm; codecs="vp8"'),
    aac: can('audio/mp4; codecs="mp4a.40.2"'),
    mp3: can('audio/mpeg') || can('audio/mp4; codecs="mp3"'),
    ac3: can('audio/mp4; codecs="ac-3"'),
    eac3: can('audio/mp4; codecs="ec-3"'),
    flac: can('audio/mp4; codecs="flac"'),
    opus: can('audio/webm; codecs="opus"') || can('audio/mp4; codecs="opus"'),
    vorbis: can('audio/webm; codecs="vorbis"'),
    // HDR10 and HLG: Apple's devices show them; desktop Chromium maps them to the screen; phones elsewhere do not.
    hdr: webkit || (/Chrome\//.test(agent) && !/Mobile|Android/.test(agent)),
    // Dolby Vision, profile 5 (no HDR10 picture underneath) and profile 8 (HDR10, HLG or SDR underneath).
    dv5: webkit && can('video/mp4; codecs="dvh1.05.06"'),
    dv8: webkit && can('video/mp4; codecs="dvh1.08.06"'),
    // In HLS: Safari reads HEVC and AC-3 in fMP4 segments; hls.js only what Media Source Extensions accept.
    hlsH264: nativeHls ? h264 : mse('video/mp4; codecs="avc1.640029"'),
    hlsHevc: nativeHls ? hevc : mse('video/mp4; codecs="hvc1.1.6.L120.90"'),
    hlsAc3: nativeHls ? can('audio/mp4; codecs="ac-3"') : mse('audio/mp4; codecs="ac-3"'),
    hlsEac3: nativeHls ? can('audio/mp4; codecs="ec-3"') : mse('audio/mp4; codecs="ec-3"'),
  };
})();

const list = (pairs) => pairs.filter(([ok]) => ok).map(([, name]) => name).join(',');
const condition = (Condition, Property, Value, IsRequired = false) => ({ Condition, Property, Value: String(Value), IsRequired });
const ranges = (pairs) => list(pairs).replace(/,/g, '|');

/** Text subtitles beside the video as WebVTT, ASS and SSA too (Jellyfin would draw them into the picture otherwise). */
const SUBTITLES = [
  { Format: 'vtt', Method: 'External' },
  { Format: 'ass', Method: 'External' },
  { Format: 'ssa', Method: 'External' },
  { Format: 'pgssub', Method: 'Encode' },
  { Format: 'dvdsub', Method: 'Encode' },
  { Format: 'dvbsub', Method: 'Encode' },
];

/**
 * The profile Jellyfin's PlaybackInfo decides with. `bitrate` caps both direct play and conversions. With
 * `hlsSubtitles` (Apple's player), a conversion carries its text subtitles in the HLS stream itself: Apple's menu lists
 * them, and they follow the video over AirPlay.
 */
export function deviceProfile(bitrate, { hlsSubtitles = false } = {}) {
  const s = support;
  const mp4Video = list([[s.h264, 'h264'], [s.hevc, 'hevc'], [s.av1, 'av1']]);
  const mp4Audio = list([[s.aac, 'aac'], [s.mp3, 'mp3'], [s.ac3, 'ac3'], [s.eac3, 'eac3'], [s.flac, 'flac'], [s.opus, 'opus'], [s.aac, 'alac']]);
  const webmVideo = list([[s.vp8, 'vp8'], [s.vp9, 'vp9'], [s.av1, 'av1']]);
  const webmAudio = list([[s.vorbis, 'vorbis'], [s.opus, 'opus']]);
  // H.264 first: when Jellyfin must convert, it encodes the first codec, and H.264 costs a home server far less.
  // HEVC sources are still sent as they are (stream copy) where the phone plays them.
  const hlsVideo = list([[s.hlsH264, 'h264'], [s.hlsHevc, 'hevc']]);
  // MP3 in fMP4 segments: Safari reads it in MPEG-TS only (jellyfin-web says so too), so Jellyfin converts it to AAC.
  const hlsAudio = list([[s.aac, 'aac'], [!nativeHls && mse('audio/mp4; codecs="mp3"'), 'mp3'], [s.hlsAc3, 'ac3'], [s.hlsEac3, 'eac3']]);
  const surround = s.hlsAc3 || s.hlsEac3;
  // The picture ranges sent as they are. Jellyfin copies any range when a profile names none: Dolby Vision profile 5
  // then reaches the player as plain HEVC (wrong colours), and profile 7 with its enhancement layer. Named, Jellyfin
  // tags Dolby Vision for Apple's player (dvh1), strips what the player cannot use (profile 7 → its HDR10 picture),
  // and converts the rest.
  const hevcRanges = ranges([[true, 'SDR'], [s.hdr, 'HDR10'], [s.hdr, 'HDR10Plus'], [s.hdr, 'HLG'], [s.dv5, 'DOVI'],
    [s.dv8, 'DOVIWithHDR10'], [s.dv8, 'DOVIWithHLG'], [s.dv8, 'DOVIWithSDR'], [s.dv8, 'DOVIWithHDR10Plus']]);

  const direct = [];
  if (mp4Video) direct.push({ Container: 'mp4,m4v', Type: 'Video', VideoCodec: mp4Video, AudioCodec: mp4Audio });
  if (mp4Video && (s.hevc || /Safari/.test(agent))) direct.push({ Container: 'mov', Type: 'Video', VideoCodec: mp4Video, AudioCodec: mp4Audio });
  if (webmVideo) direct.push({ Container: 'webm', Type: 'Video', VideoCodec: webmVideo, AudioCodec: webmAudio });

  const transcoding = [];
  if (hlsVideo && (nativeHls || useHlsJs)) {
    // MinSegments and BreakOnNonKeyFrames are left out: Jellyfin 10.9+ reads MinSegments for live TV only, and
    // BreakOnNonKeyFrames no more (marked obsolete, « always false »).
    transcoding.push({
      Container: 'mp4', Type: 'Video', Protocol: 'hls', Context: 'Streaming', VideoCodec: hlsVideo, AudioCodec: hlsAudio || 'aac',
      MaxAudioChannels: surround ? '6' : '2',
    });
  } else if (s.vp9 && s.opus) {
    // Browsers without H.264 (open-source Chromium): a WebM conversion, sent progressively.
    transcoding.push({ Container: 'webm', Type: 'Video', Protocol: 'http', Context: 'Streaming', VideoCodec: 'vp9', AudioCodec: 'opus', MaxAudioChannels: '2' });
  }

  return {
    Name: 'Mira web',
    MaxStreamingBitrate: bitrate,
    MaxStaticBitrate: bitrate,
    MusicStreamingTranscodingBitrate: 384000,
    DirectPlayProfiles: direct,
    TranscodingProfiles: transcoding,
    ContainerProfiles: [],
    CodecProfiles: [
      // Safari plays 8-bit H.264 up to level 5.2; 10-bit H.264 (anime encodes) must be converted. Interlaced video
      // (TV recordings, 1080i discs) is converted and deinterlaced: no browser player deinterlaces.
      { Type: 'Video', Codec: 'h264', Conditions: [
        condition('LessThanEqual', 'VideoBitDepth', 8),
        condition('EqualsAny', 'VideoProfile', 'high|main|baseline|constrained baseline'),
        condition('LessThanEqual', 'VideoLevel', 52),
        condition('EqualsAny', 'VideoRangeType', 'SDR'),
        condition('NotEquals', 'IsInterlaced', true),
      ] },
      { Type: 'Video', Codec: 'hevc', Conditions: [
        condition('EqualsAny', 'VideoProfile', 'main|main 10'),
        condition('EqualsAny', 'VideoRangeType', hevcRanges),
        condition('LessThanEqual', 'VideoLevel', s.hevcLevel),
        condition('NotEquals', 'IsInterlaced', true),
        // WebKit: HEVC up to 60 images per second, tagged hvc1 (or dvh1). Not required: Jellyfin 12.0 and 12.1 never
        // read the tag (always empty); it applies from the versions that do.
        ...(webkit ? [condition('LessThanEqual', 'VideoFramerate', 60, true), condition('EqualsAny', 'VideoCodecTag', 'hvc1|dvh1')] : []),
      ] },
      // Without audioTracks, a file plays its first audio track only: Jellyfin converts for any other.
      ...(audioTracks ? [] : [{ Type: 'VideoAudio', Conditions: [condition('Equals', 'IsSecondaryAudio', false)] }]),
    ],
    SubtitleProfiles: [
      // Text subtitles come as WebVTT: in the HLS stream for Apple's player (Jellyfin tries the profiles in this order,
      // Hls only for a conversion), beside the video otherwise; pictures (PGS, DVD) are drawn into the video.
      // Jellyfin never converts ASS and SSA (anime) by profile: without their own entries it would draw them into the
      // picture, re-encoding the whole video. As files beside the video, Mira asks for them as WebVTT (their styling
      // is lost, the lines are not).
      ...(hlsSubtitles ? [{ Format: 'vtt', Method: 'Hls' }] : []),
      ...SUBTITLES,
    ],
  };
}

/**
 * The last try, after a stream the player refused even with its video converted: nothing sent as it is, the most
 * common pair (8-bit H.264 in SDR, stereo AAC), in the same HLS segments as any conversion (their subtitles too).
 * PlaybackInfo goes with EnableDirectPlay, EnableDirectStream, AllowVideoStreamCopy and AllowAudioStreamCopy false.
 */
export function lastResortProfile(bitrate, { hlsSubtitles = false } = {}) {
  const hls = (nativeHls || useHlsJs) && support.hlsH264;
  return {
    Name: 'Mira web (conversion complète)',
    MaxStreamingBitrate: bitrate,
    MaxStaticBitrate: bitrate,
    MusicStreamingTranscodingBitrate: 384000,
    DirectPlayProfiles: [],
    TranscodingProfiles: hls
      ? [{ Container: 'mp4', Type: 'Video', Protocol: 'hls', Context: 'Streaming', VideoCodec: 'h264', AudioCodec: 'aac', MaxAudioChannels: '2' }]
      : [{ Container: 'webm', Type: 'Video', Protocol: 'http', Context: 'Streaming', VideoCodec: 'vp9', AudioCodec: 'opus', MaxAudioChannels: '2' }],
    ContainerProfiles: [],
    CodecProfiles: [
      { Type: 'Video', Codec: 'h264', Conditions: [
        condition('LessThanEqual', 'VideoBitDepth', 8),
        condition('EqualsAny', 'VideoProfile', 'high|main|baseline|constrained baseline'),
        // 1080p at most, where every player takes the level Jellyfin writes (a 4K H.264 needs 5.1 in Safari's fMP4).
        condition('LessThanEqual', 'Width', 1920),
        condition('LessThanEqual', 'VideoLevel', 51),
        condition('EqualsAny', 'VideoRangeType', 'SDR'),
        condition('NotEquals', 'IsInterlaced', true),
      ] },
    ],
    SubtitleProfiles: [...(hlsSubtitles ? [{ Format: 'vtt', Method: 'Hls' }] : []), ...SUBTITLES],
  };
}
