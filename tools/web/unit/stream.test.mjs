// What the web player asks Jellyfin for and what it does when a stream fails (src/Mira.Web/js/player/stream.js),
// without a browser: node --test tools/web/unit/*.test.mjs
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { readFileSync } from 'node:fs';
import {
  FULL_BITRATE, FULL_MARGIN, UNMEASURED_BITRATE, MEASURE_KEPT, knownBitrate, measuredBitrate, autoBitrate, LAST_STAGE, nextStage, stageRequest,
  stageText, videoErrorText, hlsErrorText, videoErrorStep, hlsErrorStep, afterCheck, resumeTicks, playbackError,
  copiesVideo, chooseStream, uncappedBitrate, burnedIn, subtitleSwitch, webVttAddress, progressFraction, startPosition, RESUME_BACK, RESUME_BACK_FROM, resumeBack,
  STALL_LIMIT, START_LIMIT, STALL_FORGIVEN, STILL, stallLimit, stallWatch, stallStep,
} from '../../../src/Mira.Web/js/player/stream.js';

test('bitrate: chosen, full quality, or a fresh measure; null when a measure is needed', () => {
  const now = 10 * MEASURE_KEPT;
  assert.equal(knownBitrate(6_000_000, { atHome: true, now }), 6_000_000);
  assert.equal(knownBitrate('max', { now }), FULL_BITRATE);
  assert.equal(knownBitrate('auto', { atHome: true, now }), FULL_BITRATE);
  // A fresh measure: what it allows for this file (autoBitrate).
  assert.equal(knownBitrate('auto', { measure: { at: now - MEASURE_KEPT + 1, speed: 10_000_000 }, now }), 7_000_000);
  assert.equal(knownBitrate('auto', { measure: { at: now - MEASURE_KEPT + 1, speed: 10_000_000 }, now, fileBitrate: 5_000_000 }), FULL_BITRATE);
  assert.equal(knownBitrate('auto', { measure: { at: now - MEASURE_KEPT, speed: 10_000_000 }, now }), null);
  assert.equal(knownBitrate('auto', { now }), null);
  assert.equal(knownBitrate(undefined, { now }), null);
  assert.equal(UNMEASURED_BITRATE, 8_000_000);
});

test('bitrate: 70 % of the measure, between 1.5 and 40 Mb/s', () => {
  assert.equal(measuredBitrate(1_000_000), 1_500_000);
  assert.equal(measuredBitrate(10_000_000), 7_000_000);
  assert.equal(measuredBitrate(100_000_000), 40_000_000);
  assert.equal(measuredBitrate(3_333_333), 2_333_333);
});

test('bitrate: the file as it is when the connection holds one and a half times its bitrate, above 40 Mb/s too', () => {
  assert.equal(FULL_MARGIN, 1.5);
  // A 4K remux at 60 Mb/s on a line of 95 Mb/s: as it is. At 85 Mb/s: converted, under what the line allows.
  assert.equal(autoBitrate(95_000_000, 60_000_000), FULL_BITRATE);
  assert.equal(autoBitrate(90_000_000, 60_000_000), FULL_BITRATE);
  assert.equal(autoBitrate(85_000_000, 60_000_000), 40_000_000);
  // A 1080p file at 8 Mb/s on a 4G line of 15 Mb/s: as it is; on 10 Mb/s, converted at 7 Mb/s.
  assert.equal(autoBitrate(15_000_000, 8_000_000), FULL_BITRATE);
  assert.equal(autoBitrate(10_000_000, 8_000_000), 7_000_000);
  // A file whose bitrate Jellyfin does not know: what the measure allows.
  assert.equal(autoBitrate(200_000_000, 0), 40_000_000);
  assert.equal(autoBitrate(200_000_000, undefined), 40_000_000);
});

test('cascade: three stages, each converting more', () => {
  assert.equal(LAST_STAGE, 2);
  assert.deepEqual([0, 1, 2].map(nextStage), [1, 2, null]);
  assert.deepEqual(stageRequest(0), { lastResort: false, EnableDirectPlay: true, EnableDirectStream: true, AllowVideoStreamCopy: true, AllowAudioStreamCopy: true });
  assert.deepEqual(stageRequest(1), { lastResort: false, EnableDirectPlay: false, EnableDirectStream: false, AllowVideoStreamCopy: false, AllowAudioStreamCopy: true });
  assert.deepEqual(stageRequest(2), { lastResort: true, EnableDirectPlay: false, EnableDirectStream: false, AllowVideoStreamCopy: false, AllowAudioStreamCopy: false });
});

test('cascade: what an error of the <video> leads to', () => {
  // A file played as it is that Safari cannot decode (3) or read (4): converted at once, the server not asked.
  assert.equal(videoErrorStep({ playMethod: 'DirectPlay', stage: 0, code: 3 }), 'convert');
  assert.equal(videoErrorStep({ playMethod: 'DirectPlay', stage: 0, code: 4 }), 'convert');
  // A network error (2), or a conversion refused: the server is asked first.
  assert.equal(videoErrorStep({ playMethod: 'DirectPlay', stage: 0, code: 2 }), 'check');
  assert.equal(videoErrorStep({ playMethod: 'DirectPlay', stage: 0, code: undefined }), 'check');
  assert.equal(videoErrorStep({ playMethod: 'DirectStream', stage: 0, code: 3 }), 'check');
  assert.equal(videoErrorStep({ playMethod: 'Transcode', stage: 1, code: 4 }), 'check');
});

test('cascade: what a fatal error of hls.js leads to', () => {
  assert.equal(hlsErrorStep({ fatal: false, mediaError: true, recovered: false }), 'ignore');
  assert.equal(hlsErrorStep({ fatal: true, mediaError: true, recovered: false }), 'recover');
  assert.equal(hlsErrorStep({ fatal: true, mediaError: true, recovered: true }), 'refused');
  assert.equal(hlsErrorStep({ fatal: true, mediaError: false, recovered: false }), 'refused');
});

test('cascade: the server asked, silent or answering', () => {
  // Silent: Mira waits for it, whatever the stage (never « Lecture impossible » for a PC asleep).
  assert.equal(afterCheck({ up: false, canGoOn: true }), 'wait');
  assert.equal(afterCheck({ up: false, canGoOn: false }), 'wait');
  // Answering: the stream is at fault.
  assert.equal(afterCheck({ up: true, canGoOn: true }), 'next');
  assert.equal(afterCheck({ up: true, canGoOn: false }), 'fail');
});

/** The cascade as player.js runs it: each error, the server answering, until the title plays or nothing is left. */
function run({ playMethods, errors }) {
  const asked = [];
  let stage = 0;
  for (let i = 0; ; i++) {
    asked.push(stageRequest(stage));
    const error = errors[i];
    if (!error) return { asked, stage, result: 'plays' };
    const playMethod = playMethods[stage];
    const step = error.hls ? hlsErrorStep({ fatal: true, mediaError: false, recovered: true }) : videoErrorStep({ playMethod, stage, code: error.code });
    if (step === 'convert') { stage = nextStage(stage); continue; }
    const after = afterCheck({ up: true, canGoOn: nextStage(stage) != null });
    if (after === 'fail') return { asked, stage, result: 'fail' };
    stage = nextStage(stage);
  }
}

test('cascade: a title refused at every stage ends in « Lecture impossible », everything converted', () => {
  const { asked, stage, result } = run({ playMethods: ['DirectStream', 'Transcode', 'Transcode'], errors: [{ code: 4 }, { code: 4 }, { code: 4 }] });
  assert.equal(result, 'fail');
  assert.equal(stage, 2);
  assert.deepEqual(asked.map((r) => r.lastResort), [false, false, true]);
});

test('cascade: a file played as it is that Safari refuses plays once its video is converted', () => {
  const { asked, stage, result } = run({ playMethods: ['DirectPlay', 'Transcode'], errors: [{ code: 3 }] });
  assert.equal(result, 'plays');
  assert.equal(stage, 1);
  assert.equal(asked[1].AllowVideoStreamCopy, false);
  assert.equal(asked[1].AllowAudioStreamCopy, true);
});

test('cascade: hls.js refusing twice gets everything converted', () => {
  const { stage, result } = run({ playMethods: ['DirectStream', 'Transcode', 'Transcode'], errors: [{ hls: true }, { hls: true }] });
  assert.equal(result, 'plays');
  assert.equal(stage, 2);
});

test('error details name the stage', () => {
  assert.equal(stageText(0), 'conversion de Jellyfin');
  assert.equal(stageText(1), 'vidéo convertie');
  assert.equal(stageText(2), 'tout converti');
  assert.equal(stageText(0, 'DirectPlay'), 'fichier lu tel quel');
  assert.equal(videoErrorText({ code: 4, message: 'MEDIA_ERR_SRC_NOT_SUPPORTED', playMethod: 'DirectPlay', stage: 0 }), 'Erreur 4 : MEDIA_ERR_SRC_NOT_SUPPORTED (fichier lu tel quel).');
  assert.equal(videoErrorText({ code: undefined, message: '', playMethod: 'Transcode', stage: 2 }), 'Erreur ? (tout converti).');
  assert.equal(hlsErrorText({ details: 'fragParsingError', type: 'mediaError', stage: 1 }), 'Erreur fragParsingError (vidéo convertie).');
  assert.equal(hlsErrorText({ details: undefined, type: 'networkError', stage: 0 }), 'Erreur networkError (conversion de Jellyfin).');
});

test('PlaybackInfo errors in French', () => {
  assert.equal(playbackError('NotAllowed'), 'Ton compte Jellyfin n’a pas le droit de lire ce titre.');
  assert.equal(playbackError('Other'), 'Jellyfin n’a pas pu préparer ce titre pour cet appareil.');
});

test('resume point: where the stream plays, or where it was asked to start', () => {
  assert.equal(resumeTicks({ streamPlaying: true, progressive: false, positionTicks: 50, currentStart: 10 }), 50);
  assert.equal(resumeTicks({ streamPlaying: false, progressive: true, positionTicks: 50, currentStart: 10 }), 50);
  assert.equal(resumeTicks({ streamPlaying: false, progressive: false, positionTicks: 0, currentStart: 10 }), 10);
});

test('copiesVideo reads why Jellyfin converts', () => {
  const hls = '/videos/abc/master.m3u8?VideoCodec=h264&AudioCodec=aac';
  assert.equal(copiesVideo(hls), true);
  assert.equal(copiesVideo(`${hls}&TranscodeReasons=AudioCodecNotSupported`), true);
  assert.equal(copiesVideo(`${hls}&TranscodeReasons=AudioCodecNotSupported%2CVideoRangeTypeNotSupported`), false);
  assert.equal(copiesVideo(`${hls}&TranscodeReasons=ContainerBitrateExceedsLimit`), false);
  assert.equal(copiesVideo(`${hls}&TranscodeReasons=SubtitleCodecNotSupported`), false);
  assert.equal(copiesVideo(`${hls}&TranscodeReasons=RefFramesNotSupported`), false);
  assert.equal(copiesVideo(`${hls}&allowVideoStreamCopy=false&TranscodeReasons=AudioCodecNotSupported`), false);
  assert.equal(copiesVideo('/videos/abc/stream.mp4'), true);
});

const film = (extra = {}) => ({ Id: 'src1', MediaStreams: [], ...extra });
const choose = (source, extra = {}) => chooseStream(source, { itemId: 'item1', deviceId: 'dev1', stage: 0, native: false, subtitleIndex: -1, ...extra });

test('stream: the file as it is at stage 0, MP4 first among its containers', () => {
  assert.deepEqual(choose(film({ SupportsDirectPlay: true, Container: 'mov,mp4,m4a', ETag: 'e1' })), {
    address: 'Videos/item1/stream.mp4?Static=true&mediaSourceId=src1&deviceId=dev1&Tag=e1',
    hlsStream: false, inband: false, playMethod: 'DirectPlay', progressive: false,
  });
  assert.equal(choose(film({ SupportsDirectPlay: true, Container: 'webm' })).address, 'Videos/item1/stream.webm?Static=true&mediaSourceId=src1&deviceId=dev1');
  assert.equal(choose(film({ SupportsDirectPlay: true, Id: 'a b' })).address, 'Videos/item1/stream.mp4?Static=true&mediaSourceId=a%20b&deviceId=dev1');
  // Past stage 0, never the file as it is; with no conversion offered, nothing.
  assert.equal(choose(film({ SupportsDirectPlay: true }), { stage: 1 }), null);
  assert.equal(choose(film({ SupportsDirectPlay: false })), null);
});

test('stream: an account Jellyfin does not let convert asks for the file again without the quality’s limit', () => {
  // Neither the file nor a conversion under 4 Mb/s: the file as it is, at full quality.
  assert.equal(uncappedBitrate(film({ SupportsDirectPlay: false }), { stage: 0, bitrate: 4_000_000 }), FULL_BITRATE);
  // Already at full quality (the file is above it, or the browser does not read it), past stage 0, or something offered: as it is.
  assert.equal(uncappedBitrate(film({ SupportsDirectPlay: false }), { stage: 0, bitrate: FULL_BITRATE }), null);
  assert.equal(uncappedBitrate(film({ SupportsDirectPlay: false }), { stage: 1, bitrate: 4_000_000 }), null);
  assert.equal(uncappedBitrate(film({ SupportsDirectPlay: true }), { stage: 0, bitrate: 4_000_000 }), null);
  assert.equal(uncappedBitrate(film({ TranscodingUrl: '/videos/item1/master.m3u8' }), { stage: 0, bitrate: 4_000_000 }), null);
});

test('stream: Jellyfin’s conversion, HLS or progressive', () => {
  const hls = choose(film({ SupportsDirectPlay: true, TranscodingUrl: '/videos/item1/master.m3u8?TranscodeReasons=AudioCodecNotSupported', TranscodingSubProtocol: 'hls' }));
  assert.deepEqual(hls, { address: '/videos/item1/master.m3u8?TranscodeReasons=AudioCodecNotSupported', hlsStream: true, inband: false, playMethod: 'DirectStream', progressive: false });
  const progressive = choose(film({ TranscodingUrl: '/videos/item1/stream.mp4?TranscodeReasons=VideoCodecNotSupported', TranscodingSubProtocol: 'http' }), { stage: 2 });
  assert.equal(progressive.playMethod, 'Transcode');
  assert.equal(progressive.progressive, true);
  assert.equal(progressive.hlsStream, false);
  // An address in .m3u8 is HLS even when the protocol is not said.
  assert.equal(choose(film({ TranscodingUrl: '/videos/item1/main.M3U8?x=1' })).hlsStream, true);
});

const subtitles = [
  { Type: 'Video', Index: 0 },
  { Type: 'Audio', Index: 1, Language: 'jpn' },
  { Type: 'Subtitle', Index: 2, Language: 'fre', Codec: 'ass', IsTextSubtitleStream: true },
  { Type: 'Subtitle', Index: 3, Language: 'eng', Codec: 'subrip', IsTextSubtitleStream: true },
  { Type: 'Subtitle', Index: 4, Language: 'fre', Codec: 'PGSSUB', IsTextSubtitleStream: false },
];

test('stream: Apple’s player gets the text subtitles in the HLS stream', () => {
  const ts = film({ MediaStreams: subtitles, TranscodingUrl: '/videos/item1/master.m3u8?a=1&SubtitleMethod=Encode', TranscodingSubProtocol: 'hls' });
  // Not Apple's player: the address as Jellyfin wrote it.
  assert.equal(choose(ts, { subtitleIndex: 2 }).address, ts.TranscodingUrl);
  const apple = choose(ts, { native: true, subtitleIndex: 2 });
  assert.equal(apple.inband, true);
  assert.equal(apple.address, '/videos/item1/master.m3u8?a=1&SubtitleStreamIndex=2&SubtitleMethod=Hls');
  // fMP4: through Mira's plugin, with Mira's names.
  const id = '0123456789abcdef0123456789abcdef';
  const mp4 = choose({ ...ts, TranscodingUrl: `/videos/${id}/master.m3u8?SegmentContainer=mp4&SubtitleMethod=Encode` }, { native: true, subtitleIndex: -1 });
  assert.equal(mp4.address, `/Mira/hls/${id}/master.m3u8?SegmentContainer=mp4&SubtitleMethod=Hls&MiraNames=${encodeURIComponent('2:Français|3:Anglais')}`);
  // Pictures chosen: Jellyfin draws them into the video, the address stays.
  const pictures = choose(ts, { native: true, subtitleIndex: 4 });
  assert.equal(pictures.inband, false);
  assert.equal(pictures.address, ts.TranscodingUrl);
  // A progressive conversion is not rewritten.
  assert.equal(choose({ ...ts, TranscodingUrl: '/videos/item1/stream.mp4?a=1', TranscodingSubProtocol: 'http' }, { native: true, subtitleIndex: 2 }).inband, false);
  // Whether the video is copied is read on Jellyfin's address, not the rewritten one.
  assert.equal(choose({ ...ts, TranscodingUrl: `${ts.TranscodingUrl}&TranscodeReasons=VideoCodecNotSupported` }, { native: true, subtitleIndex: 2 }).playMethod, 'Transcode');
});

test('subtitles burned into the video', () => {
  assert.equal(burnedIn(subtitles[4], '/videos/x/master.m3u8?SubtitleMethod=Encode'), true);
  assert.equal(burnedIn(subtitles[4], '/videos/x/master.m3u8?SubtitleMethod=Hls'), false);
  // Jellyfin writes SubtitleMethod=Encode even for text or none: not a burn-in.
  assert.equal(burnedIn(subtitles[2], '/videos/x/master.m3u8?SubtitleMethod=Encode'), false);
  assert.equal(burnedIn(null, '/videos/x/master.m3u8?SubtitleMethod=Encode'), false);
  assert.equal(burnedIn(subtitles[4], undefined), false);
});

test('other subtitles: at once, or a new conversion', () => {
  const external = { DeliveryMethod: 'External', IsTextSubtitleStream: true };
  const inStream = { DeliveryMethod: 'Hls', IsTextSubtitleStream: true };
  const picture = { DeliveryMethod: 'Encode', IsTextSubtitleStream: false };
  assert.deepEqual(subtitleSwitch({ subtitle: -1, stream: undefined, inband: false, wasBurned: false }), { show: true, reopen: false });
  assert.deepEqual(subtitleSwitch({ subtitle: -1, stream: undefined, inband: false, wasBurned: true }), { show: true, reopen: true });
  assert.deepEqual(subtitleSwitch({ subtitle: 3, stream: external, inband: false, wasBurned: false }), { show: true, reopen: false });
  assert.deepEqual(subtitleSwitch({ subtitle: 3, stream: inStream, inband: true, wasBurned: false }), { show: true, reopen: false });
  assert.deepEqual(subtitleSwitch({ subtitle: 3, stream: inStream, inband: false, wasBurned: false }), { show: false, reopen: true });
  assert.deepEqual(subtitleSwitch({ subtitle: 4, stream: picture, inband: true, wasBurned: false }), { show: false, reopen: true });
});

test('ASS and SSA beside the video are asked as WebVTT', () => {
  assert.equal(webVttAddress('/Videos/x/x/Subtitles/2/0/Stream.ass?api_key=k'), '/Videos/x/x/Subtitles/2/0/Stream.vtt?api_key=k');
  assert.equal(webVttAddress('/Videos/x/x/Subtitles/2/0/Stream.SSA'), '/Videos/x/x/Subtitles/2/0/Stream.vtt');
  assert.equal(webVttAddress('/Videos/x/x/Subtitles/3/0/Stream.srt'), '/Videos/x/x/Subtitles/3/0/Stream.srt');
  assert.equal(webVttAddress('/Videos/x/x/Subtitles/3/0/Stream.assets'), '/Videos/x/x/Subtitles/3/0/Stream.assets');
});

test('where a title starts', () => {
  const half = { RunTimeTicks: 1000, UserData: { PlaybackPositionTicks: 500 } };
  assert.equal(progressFraction(half), 0.5);
  assert.equal(progressFraction({ RunTimeTicks: 0 }), 0);
  assert.equal(progressFraction({ RunTimeTicks: 1000 }), 0);
  assert.equal(startPosition(half, { fromStart: false, resume: true }), 500);
  assert.equal(startPosition(half, { fromStart: true, resume: true }), 0);
  assert.equal(startPosition(half, { fromStart: false, resume: false }), 0);
  assert.equal(startPosition({ RunTimeTicks: 1000, UserData: { PlaybackPositionTicks: 950 } }, { fromStart: false, resume: true }), 0);
  assert.equal(startPosition({ RunTimeTicks: 1000, UserData: { PlaybackPositionTicks: 949 } }, { fromStart: false, resume: true }), 949);
  assert.equal(startPosition({ RunTimeTicks: 1000 }, { fromStart: false, resume: true }), 0);
  // « Reprendre un peu avant »: 5 s before, from 30 s watched.
  const twentyMinutes = { RunTimeTicks: 36_000_000_000, UserData: { PlaybackPositionTicks: 12_000_000_000 } };
  assert.equal(startPosition(twentyMinutes, { fromStart: false, resume: true, back: 5 }), 11_950_000_000);
  assert.equal(startPosition(twentyMinutes, { fromStart: true, resume: true, back: 5 }), 0);
  assert.equal(startPosition(twentyMinutes, { fromStart: false, resume: true, back: 0 }), 12_000_000_000);
  assert.equal(startPosition({ RunTimeTicks: 36_000_000_000, UserData: { PlaybackPositionTicks: 200_000_000 } }, { fromStart: false, resume: true, back: 5 }), 200_000_000);
});

// The same cases as Mira for Windows and Mac (tests/Mira.Core.Tests, PlaybackSession.ResumeBack).
test('« Reprendre un peu avant »: the cases Mira for Windows and Mac read', () => {
  const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/resume.json', import.meta.url), 'utf8'));
  assert.deepEqual(RESUME_BACK, fixture.choices);
  assert.equal(RESUME_BACK_FROM, fixture.from);
  for (const [position, back, at] of fixture.cases) assert.ok(Math.abs(resumeBack(position, back) - at) < 1e-9, `${position} s, ${back} s back`);
});

test('the messages of a failed stream in English', () => {
  try {
    setLanguage('en');
    assert.equal(videoErrorText({ code: 4, message: 'MEDIA_ERR_SRC_NOT_SUPPORTED', playMethod: 'DirectPlay', stage: 0 }), 'Error 4: MEDIA_ERR_SRC_NOT_SUPPORTED (file played as is).');
    assert.equal(hlsErrorText({ details: 'fragParsingError', type: 'mediaError', stage: 1 }), 'Error fragParsingError (video converted).');
    assert.equal(playbackError('NotAllowed'), 'Your Jellyfin account isn’t allowed to play this title.');
    assert.equal(playbackError('Other'), 'Jellyfin couldn’t prepare this title for this device.');
  } finally { setLanguage('fr'); }
});

/** The looks player.js takes, one a second: { t (s), at, expecting, ready, seeking }; the second the stream is stuck, or null. */
function watch(looks) {
  let last = STILL;
  for (const { t, at, expecting = true, ready = true, seeking = false } of looks) {
    last = stallWatch(last, { now: t * 1000, at, expecting, ready, seeking });
    if (last.stuck) return t;
  }
  return null;
}
const seconds = (from, to, look) => Array.from({ length: to - from + 1 }, (_, i) => ({ t: from + i, ...look(from + i) }));

test('stall: a stream that plays, or that should not, is never stuck', () => {
  assert.equal(watch(seconds(1, 120, (t) => ({ at: t }))), null);
  // Paused by the person (or by Mira): nothing is expected of it, however long.
  assert.equal(watch(seconds(1, 300, () => ({ at: 12, expecting: false }))), null);
});

test('stall: played, then still for 15 s', () => {
  const looks = [...seconds(1, 10, (t) => ({ at: t })), ...seconds(11, 60, () => ({ at: 10, ready: false }))];
  assert.equal(watch(looks), 11 + STALL_LIMIT / 1000);
  assert.equal(stallLimit(true), STALL_LIMIT);
});

test('stall: opened, or moved, with nothing played since: 45 s', () => {
  // A conversion that starts slowly: its time set while it loads is not playing on.
  assert.equal(watch(seconds(1, 120, (t) => ({ at: t < 3 ? 0 : 600, ready: false }))), 1 + START_LIMIT / 1000);
  assert.equal(watch(seconds(1, 40, () => ({ at: 600, ready: false }))), null);
  // A jump: its time changes while seeking, which is not playing either.
  assert.equal(watch(seconds(1, 120, (t) => ({ at: t < 5 ? t : 300, seeking: t >= 5 }))), 5 + START_LIMIT / 1000);
});

test('stall: a pause, then the same stream played again, gets the short limit; paused time does not count', () => {
  const looks = [...seconds(1, 10, (t) => ({ at: t })), ...seconds(11, 200, () => ({ at: 10, expecting: false })), ...seconds(201, 260, () => ({ at: 10, ready: false }))];
  assert.equal(watch(looks), 201 + STALL_LIMIT / 1000);
});

test('stall: the server asked, silent or answering', () => {
  assert.equal(stallStep({ up: false, reopened: false }), 'wait');
  assert.equal(stallStep({ up: false, reopened: true }), 'wait');
  assert.equal(stallStep({ up: true, reopened: false }), 'reopen');
  assert.equal(stallStep({ up: true, reopened: true }), 'fail');
  assert.ok(STALL_FORGIVEN > STALL_LIMIT / 1000);
});
