// « Lecture impossible » (src/Mira.Web/js/player/failure.js): what the file holds, what Mira tried, and the report
// Jellyfin keeps. The same cases as Mira for Windows (tests/Mira.Core.Tests, PlaybackFailures): both read them.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { mediaLine, reasonText, transcodeReasons, attemptText, reportTitle, fileName, failureReport, REPORT_KIND } from '../../../src/Mira.Web/js/player/failure.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/failure.json', import.meta.url), 'utf8'));

test('Jellyfin’s reasons to convert, in the words of « En ce moment sur Jellyfin »', () => {
  for (const [reason, text] of fixture.reasons) assert.equal(reasonText(reason), text, reason);
  // Read from a conversion's address, never the address itself (it carries the token).
  assert.deepEqual(transcodeReasons('/videos/x/master.m3u8?ApiKey=t&TranscodeReasons=ContainerNotSupported,AudioCodecNotSupported'), ['ContainerNotSupported', 'AudioCodecNotSupported']);
  assert.deepEqual(transcodeReasons('/videos/x/master.m3u8?ApiKey=t'), []);
  assert.deepEqual(transcodeReasons(undefined), []);
});

test('what the file holds, in a line: container, video, the audio and subtitles chosen', () => {
  for (const { source, audioIndex, subtitleIndex, line } of fixture.media) assert.equal(mediaLine(source, { audioIndex, subtitleIndex }), line);
});

test('each stream Mira tried, and why Jellyfin converted it', () => {
  for (const [attempt, text] of fixture.attempts) assert.equal(attemptText(attempt), text);
});

test('the report sent to Jellyfin: what the screen says, without token, address or folders', () => {
  const { input, document } = fixture.report;
  const text = failureReport({ ...input, at: new Date(input.at) });
  assert.deepEqual(JSON.parse(text), document);
  assert.equal(document.mira, REPORT_KIND);
  assert.doesNotMatch(text, /secret|ApiKey|Users\\\\|DeliveryUrl|TranscodingUrl|"Path"/);
  assert.equal(reportTitle({ Type: 'Movie', Name: 'Aube Test' }), 'Aube Test');
  assert.equal(fileName('/media/Films/Aube Test (2021)/Aube Test.webm'), 'Aube Test.webm');
  assert.equal(fileName(undefined), null);
  // Before Jellyfin said anything of the title: still a report, with what is known.
  const bare = JSON.parse(failureReport({ item: { Id: 'x', Type: 'Movie', Name: 'Aube Test' }, message: 'Le serveur est injoignable.', device: 'Chrome', version: '0.7.8' }));
  assert.equal(bare.media, '');
  assert.deepEqual(bare.attempts, []);
  assert.deepEqual(bare.streams, []);
});
