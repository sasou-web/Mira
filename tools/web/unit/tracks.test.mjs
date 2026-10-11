// Audio and subtitle tracks of the web player (src/Mira.Web/js/player/tracks.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import {
  trackText, menuNames, chooseTracks, trackMemory, chosenTracks, startTracks, fileTextSubtitles, carriedIndex,
} from '../../../src/Mira.Web/js/player/tracks.js';

const source = {
  DefaultAudioStreamIndex: 1,
  MediaStreams: [
    { Type: 'Video', Index: 0 },
    { Type: 'Audio', Index: 1, Language: 'jpn', Codec: 'aac', Channels: 2 },
    { Type: 'Audio', Index: 2, Language: 'fre', Codec: 'eac3', Channels: 6 },
    { Type: 'Subtitle', Index: 3, Language: 'fre', Codec: 'ass', IsTextSubtitleStream: true, DisplayTitle: 'French - ASS' },
    { Type: 'Subtitle', Index: 4, Language: 'fre', Codec: 'ass', IsTextSubtitleStream: true, IsForced: true, DisplayTitle: 'French - ASS - Forced' },
    { Type: 'Subtitle', Index: 5, Language: 'eng', Codec: 'subrip', IsTextSubtitleStream: true, IsExternal: true, DisplayTitle: 'English' },
    { Type: 'Subtitle', Index: 6, Language: 'fre', Codec: 'PGSSUB', IsTextSubtitleStream: false },
  ],
};

test('track names in French', () => {
  assert.deepEqual(trackText(source.MediaStreams[2], 2), { label: 'Français', details: 'Dolby Digital Plus · 5.1' });
  assert.deepEqual(trackText(source.MediaStreams[4], 2), { label: 'Français', details: 'ASS · forcés' });
  assert.deepEqual(trackText({ Type: 'Audio', Index: 9 }, 3), { label: 'Piste 3', details: '' });
});

test('names in Apple’s menu tell apart the same language', () => {
  assert.deepEqual([...menuNames(source)], [[3, 'Français (ASS)'], [4, 'Français (ASS · forcés)'], [5, 'Anglais']]);
});

test('tracks a title starts with', () => {
  // Japanese then French audio; French subtitles first: Japanese audio, French dialogue subtitles (not the forced ones).
  assert.deepEqual(chooseTracks(source, { audioOrder: 'jpn,ja,fre,fra,fr', subtitleOrder: 'fre,fra,fr,eng,en' }), { audio: 1, subtitle: 3 });
  // French audio with French subtitles first: only the forced ones.
  assert.deepEqual(chooseTracks(source, { audioOrder: 'fre,fra,fr', subtitleOrder: 'fre,fra,fr,eng,en' }), { audio: 2, subtitle: 4 });
  // The series' last choice: none.
  assert.deepEqual(chooseTracks(source, { memory: { audio: 'jpn', subtitle: null } }), { audio: 1, subtitle: -1 });
  assert.deepEqual(trackMemory(source, 1, 4), { audio: 'jpn', subtitle: 'fre', forced: true });
});

test('tracks chosen on the title page, from the player’s address', () => {
  assert.equal(chosenTracks(new URLSearchParams('')), null);
  assert.equal(chosenTracks(new URLSearchParams('debut=1')), null);
  assert.deepEqual(chosenTracks(new URLSearchParams('audio=2&sous-titres=-1')), { audio: 2, subtitle: -1 });
  assert.deepEqual(chosenTracks(new URLSearchParams('sous-titres=5')), { audio: null, subtitle: 5 });
  assert.equal(chosenTracks(new URLSearchParams('audio=deux&sous-titres=1.5')), null);
});

test('startTracks: the chosen ones when the file has them, else the automatic choice', () => {
  const preferences = { audioOrder: 'jpn,ja', subtitleOrder: 'fre,fra,fr' };
  assert.deepEqual(startTracks(source, null, preferences), { audio: 1, subtitle: 3 });
  assert.deepEqual(startTracks(source, { audio: 2, subtitle: -1 }, preferences), { audio: 2, subtitle: -1 });
  assert.deepEqual(startTracks(source, { audio: 9, subtitle: 5 }, preferences), { audio: 1, subtitle: 5 });
  // A subtitle index that is an audio track, or absent: the automatic choice.
  assert.deepEqual(startTracks(source, { audio: null, subtitle: 2 }, preferences), { audio: 1, subtitle: 3 });
  // No streams known yet: the chosen ones as they are, Jellyfin deciding the rest.
  assert.deepEqual(startTracks({ MediaStreams: [] }, { audio: 2, subtitle: null }, preferences), { audio: 2, subtitle: null });
  assert.deepEqual(startTracks(null, null, preferences), { audio: null, subtitle: null });
});

const mp4 = {
  MediaStreams: [
    { Type: 'Subtitle', Index: 2, Language: 'fre', Codec: 'mov_text', IsTextSubtitleStream: true },
    { Type: 'Subtitle', Index: 3, Language: 'eng', Codec: 'subrip', IsTextSubtitleStream: true, IsExternal: true },
    { Type: 'Subtitle', Index: 4, Language: 'eng', Codec: 'tx3g', IsTextSubtitleStream: true },
  ],
};

test('the file’s own text subtitles, only when Safari plays the file as it is', () => {
  assert.deepEqual(fileTextSubtitles(mp4, 'DirectPlay').map((s) => s.Index), [2, 4]);
  assert.deepEqual(fileTextSubtitles(mp4, 'DirectStream'), []);
  assert.deepEqual(fileTextSubtitles(null, 'DirectPlay'), []);
});

test('Jellyfin’s index of a subtitle track found in the video', () => {
  const own = fileTextSubtitles(mp4, 'DirectPlay');
  // The file's own: by place, when the counts agree.
  assert.equal(carriedIndex({ at: 1, count: 2, label: '', inband: false, own, source: mp4 }), 4);
  assert.equal(carriedIndex({ at: 1, count: 3, label: '', inband: false, own, source: mp4 }), null);
  assert.equal(carriedIndex({ at: -1, count: 2, label: '', inband: false, own, source: mp4 }), null);
  // In the HLS stream: by Mira's name, then Jellyfin's, then by place.
  assert.equal(carriedIndex({ at: 0, count: 3, label: 'Anglais', inband: true, own: null, source }), 5);
  assert.equal(carriedIndex({ at: 0, count: 3, label: 'French - ASS - Forced', inband: true, own: null, source }), 4);
  assert.equal(carriedIndex({ at: 1, count: 3, label: 'Sous-titres', inband: true, own: null, source }), 4);
  assert.equal(carriedIndex({ at: 1, count: 2, label: 'Sous-titres', inband: true, own: null, source }), null);
});
