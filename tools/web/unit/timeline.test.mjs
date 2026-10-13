// The web player's timeline (src/Mira.Web/js/player/timeline.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { SKIP_LABELS, COUNTDOWN, skippable, skipsItself, nextFrom, trickCell, chapterMarks, chapterKind, chapterPassages, withChapters, withoutFromStart } from '../../../src/Mira.Web/js/player/timeline.js';

const s = (seconds) => seconds * 10_000_000;
const intro = { Type: 'Intro', StartTicks: s(10), EndTicks: s(40) };
const outro = { Type: 'Outro', StartTicks: s(1300), EndTicks: s(1400) };
const segments = [intro, { Type: 'Unknown', StartTicks: s(50), EndTicks: s(60) }, outro];

test('passages to skip, until their last second', () => {
  assert.equal(SKIP_LABELS.Intro, 'Passer l’intro');
  assert.equal(skippable(segments, 9.9), undefined);
  assert.equal(skippable(segments, 10), intro);
  assert.equal(skippable(segments, 38.9), intro);
  assert.equal(skippable(segments, 39), undefined);
  assert.equal(skippable(segments, 55), undefined);
  // The end credits are no passage to skip: the next episode is offered there.
  assert.equal(skippable(segments, 1350), undefined);
});

test('intro and recap skipped by themselves, once, when the settings say so', () => {
  const skipped = new Set();
  assert.equal(skipsItself(intro, { autoSkip: true, skipped }), true);
  assert.equal(skipsItself(intro, { autoSkip: false, skipped }), false);
  assert.equal(skipsItself({ Type: 'Preview' }, { autoSkip: true, skipped }), false);
  assert.equal(skipsItself(undefined, { autoSkip: true, skipped }), false);
  skipped.add(intro);
  assert.equal(skipsItself(intro, { autoSkip: true, skipped }), false);
  // The same opening, as Jellyfin marks it: already skipped. A recap before it is not.
  assert.equal(skipsItself({ Type: 'Intro', StartTicks: s(11), EndTicks: s(39) }, { autoSkip: true, skipped }), false);
  assert.equal(skipsItself({ Type: 'Recap', StartTicks: 0, EndTicks: s(10) }, { autoSkip: true, skipped }), true);
});

test('the next episode is offered from the end credits, else over the last seconds', () => {
  assert.equal(nextFrom(segments, 1400), 1300);
  assert.equal(nextFrom([intro], 1400), 1400 - COUNTDOWN);
  assert.equal(COUNTDOWN, 10);
  // Credits, then a preview: from the credits. Credits, then a scene of the story: that scene is not cut.
  const credits = { Type: 'Outro', StartTicks: s(1200), EndTicks: s(1290) };
  assert.equal(nextFrom([credits, { Type: 'Preview', StartTicks: s(1290), EndTicks: s(1398) }], 1400), 1200);
  assert.equal(nextFrom([credits], 1400), 1400 - COUNTDOWN);
});

// The same cases as Mira for Windows and Mac (tests/Mira.Core.Tests, PlaybackMarkers): both read them.
const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/chapters.json', import.meta.url), 'utf8'));
const chapter = ([start, name]) => ({ StartPositionTicks: s(start), Name: name });
const plain = (passages) => passages.map((p) => [p.Type, p.StartTicks / 10_000_000, p.EndTicks / 10_000_000]);

test('chapter titles name the same passages as on Windows and Mac', () => {
  for (const [title, kind] of fixture.titles) assert.equal(chapterKind(title), kind, `« ${title} »`);
  for (const [title, start, duration, kind] of fixture.positions) assert.equal(chapterKind(title, start, duration), kind, `« ${title} » at ${start} s of ${duration} s`);
  for (const episode of fixture.episodes) {
    assert.deepEqual(plain(chapterPassages(episode.chapters.map(chapter), s(episode.duration))), episode.passages);
  }
  assert.deepEqual(chapterPassages(undefined, s(100)), []);
  // Without the title's length, the last chapter has no end.
  assert.deepEqual(chapterPassages([chapter([0, 'Partie A']), chapter([1300, 'Ending'])], undefined), []);
});

test('Jellyfin’s segments win over the chapters, which fill what they leave empty', () => {
  const server = [{ Type: 'Intro', StartTicks: s(60), EndTicks: s(150), Id: 'segment' }];
  const guessed = chapterPassages([[0, 'Partie A'], [50, 'Opening'], [140, 'Partie B'], [1290, 'Ending'], [1380, 'Preview'], [1418, 'Fin']].map(chapter), s(1420));
  const merged = withChapters(server, guessed);
  assert.deepEqual(plain(merged), [['Intro', 60, 150], ['Outro', 1290, 1380], ['Preview', 1380, 1418]]);
  assert.equal(merged[0], server[0]);
  // The chapters' own passages stay the same objects: one skipped by itself is not skipped again once the segments come.
  assert.equal(merged[1], guessed[1]);
  assert.deepEqual(withChapters([], guessed), guessed);
  assert.equal(skippable(merged, 1390), merged[2]);
  assert.equal(nextFrom(merged, 1420), 1290);
  // A segment under 3 s, or before the start, is noise, as on Windows and Mac: it hides no chapter of its kind.
  const noise = [{ Type: 'Intro', StartTicks: 0, EndTicks: s(2) }, { Type: 'Outro', StartTicks: s(-5), EndTicks: s(1300) }];
  assert.deepEqual(plain(withChapters(noise, guessed)), plain(guessed));
});

test('trickplay picture at a point', () => {
  const trick = { Interval: 10_000, ThumbnailCount: 250, TileWidth: 10, TileHeight: 10, Width: 320, Height: 180 };
  assert.deepEqual(trickCell(trick, 0), { tile: 0, width: 160, height: 90, x: 0, y: 0 });
  // 1 234 s: picture 123, in the second tile, row 2, column 3.
  assert.deepEqual(trickCell(trick, 1234), { tile: 1, width: 160, height: 90, x: 480, y: 180 });
  // Past the last picture: the last one.
  assert.deepEqual(trickCell(trick, 99_999), { tile: 2, width: 160, height: 90, x: 1440, y: 360 });
});

test('chapter marks', () => {
  const chapters = [{ StartPositionTicks: 0 }, { StartPositionTicks: s(25) }, { StartPositionTicks: s(150) }, { StartPositionTicks: s(75) }];
  assert.deepEqual(chapterMarks(chapters, 100), [0.25, 0.75]);
  assert.deepEqual(chapterMarks(chapters.slice(0, 1), 100), []);
  assert.deepEqual(chapterMarks(chapters, 0), []);
});

test('« from the start » leaves the address once the title plays', () => {
  assert.equal(withoutFromStart('#/lecture/abc?debut=1', 'abc'), '#/lecture/abc');
  assert.equal(withoutFromStart('#/lecture/abc?debut=1&audio=2', 'abc'), '#/lecture/abc?audio=2');
  assert.equal(withoutFromStart('#/lecture/abc?audio=2', 'abc'), null);
  assert.equal(withoutFromStart('#/lecture/def?debut=1', 'abc'), null);
  assert.equal(withoutFromStart('#/lecture/abc', 'abc'), null);
});
