// What this browser plays, told to Jellyfin as a device profile: it then sends the file as it is when it can
// (direct play), or converts it on the fly into HLS that Safari plays natively (hls.js elsewhere).

const probe = document.createElement('video');
const can = (type) => /^(probably|maybe)$/.test(probe.canPlayType(type));
const Source = window.ManagedMediaSource ?? window.MediaSource;
const mse = (type) => !!Source?.isTypeSupported?.(type);

export const nativeHls = can('application/vnd.apple.mpegurl') || can('application/x-mpegURL');
/** hls.js is only loaded where HLS is not built in (not Safari) and Media Source Extensions exist. */
export const useHlsJs = !nativeHls && !!Source;
/** Safari lists a file's audio tracks and switches between them; elsewhere, another track means a conversion. */
export const audioTracks = 'audioTracks' in HTMLMediaElement.prototype;

export const support = (() => {
  const h264 = can('video/mp4; codecs="avc1.640029"');
  const hevc = can('video/mp4; codecs="hvc1.1.6.L120.90"') || can('video/mp4; codecs="hev1.1.6.L120.90"');
  return {
    h264, hevc,
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
    // In HLS: Safari reads HEVC and AC-3 in fMP4 segments; hls.js only what Media Source Extensions accept.
    hlsH264: nativeHls ? h264 : mse('video/mp4; codecs="avc1.640029"'),
    hlsHevc: nativeHls ? hevc : mse('video/mp4; codecs="hvc1.1.6.L120.90"'),
    hlsAc3: nativeHls ? can('audio/mp4; codecs="ac-3"') : mse('audio/mp4; codecs="ac-3"'),
    hlsEac3: nativeHls ? can('audio/mp4; codecs="ec-3"') : mse('audio/mp4; codecs="ec-3"'),
  };
})();

const list = (pairs) => pairs.filter(([ok]) => ok).map(([, name]) => name).join(',');
const condition = (Condition, Property, Value, IsRequired = false) => ({ Condition, Property, Value: String(Value), IsRequired });

/** The profile Jellyfin's PlaybackInfo decides with. `bitrate` caps both direct play and conversions. */
export function deviceProfile(bitrate) {
  const s = support;
  const mp4Video = list([[s.h264, 'h264'], [s.hevc, 'hevc'], [s.av1, 'av1']]);
  const mp4Audio = list([[s.aac, 'aac'], [s.mp3, 'mp3'], [s.ac3, 'ac3'], [s.eac3, 'eac3'], [s.flac, 'flac'], [s.opus, 'opus'], [s.aac, 'alac']]);
  const webmVideo = list([[s.vp8, 'vp8'], [s.vp9, 'vp9'], [s.av1, 'av1']]);
  const webmAudio = list([[s.vorbis, 'vorbis'], [s.opus, 'opus']]);
  // H.264 first: when Jellyfin must convert, it encodes the first codec, and H.264 costs a home server far less.
  // HEVC sources are still sent as they are (stream copy) where the phone plays them.
  const hlsVideo = list([[s.hlsH264, 'h264'], [s.hlsHevc, 'hevc']]);
  const hlsAudio = list([[s.aac, 'aac'], [s.mp3, 'mp3'], [s.hlsAc3, 'ac3'], [s.hlsEac3, 'eac3']]);
  const surround = s.hlsAc3 || s.hlsEac3;

  const direct = [];
  if (mp4Video) direct.push({ Container: 'mp4,m4v', Type: 'Video', VideoCodec: mp4Video, AudioCodec: mp4Audio });
  if (mp4Video && (s.hevc || /Safari/.test(navigator.userAgent))) direct.push({ Container: 'mov', Type: 'Video', VideoCodec: mp4Video, AudioCodec: mp4Audio });
  if (webmVideo) direct.push({ Container: 'webm', Type: 'Video', VideoCodec: webmVideo, AudioCodec: webmAudio });

  const transcoding = [];
  if (hlsVideo && (nativeHls || useHlsJs)) {
    transcoding.push({
      Container: 'mp4', Type: 'Video', Protocol: 'hls', Context: 'Streaming', VideoCodec: hlsVideo, AudioCodec: hlsAudio || 'aac',
      MaxAudioChannels: surround ? '6' : '2', MinSegments: 1, BreakOnNonKeyFrames: true,
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
      // Safari plays 8-bit H.264 up to level 5.2; 10-bit H.264 (anime encodes) must be converted.
      { Type: 'Video', Codec: 'h264', Conditions: [
        condition('LessThanEqual', 'VideoBitDepth', 8),
        condition('EqualsAny', 'VideoProfile', 'high|main|baseline|constrained baseline'),
        condition('LessThanEqual', 'VideoLevel', 52),
      ] },
      { Type: 'Video', Codec: 'hevc', Conditions: [condition('EqualsAny', 'VideoProfile', 'main|main 10')] },
      // Without audioTracks, a file plays its first audio track only: Jellyfin converts for any other.
      ...(audioTracks ? [] : [{ Type: 'VideoAudio', Conditions: [condition('Equals', 'IsSecondaryAudio', false)] }]),
    ],
    SubtitleProfiles: [
      // Text subtitles come as WebVTT beside the video (Apple's player lists them in its menu);
      // pictures (PGS, DVD) are drawn into the video by the conversion.
      { Format: 'vtt', Method: 'External' },
      { Format: 'pgssub', Method: 'Encode' },
      { Format: 'dvdsub', Method: 'Encode' },
      { Format: 'dvbsub', Method: 'Encode' },
    ],
  };
}
