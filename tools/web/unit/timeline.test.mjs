// The web player's timeline (src/Mira.Web/js/player/timeline.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { SKIP_LABELS, COUNTDOWN, skippable, skipsItself, nextFrom, trickCell, chapterMarks, withoutFromStart } from '../../../src/Mira.Web/js/player/timeline.js';

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
});

test('the next episode is offered from the end credits, else over the last seconds', () => {
  assert.equal(nextFrom(segments, 1400), 1300);
  assert.equal(nextFrom([intro], 1400), 1400 - COUNTDOWN);
  assert.equal(COUNTDOWN, 10);
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
