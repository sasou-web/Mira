// « Nouvel épisode » of Mira web (src/Mira.Web/js/new-episodes.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { pickNewEpisodes, newestEpisode, newEpisodesText, follows, dateOf } from '../../../src/Mira.Web/js/new-episodes.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/new-episodes.json', import.meta.url), 'utf8'));
const series = Object.fromEntries(fixture.series.map((x) => [x.Id, x]));

test('the series followed, the mark and the lines, as Windows notifies them (tests/fixtures/new-episodes.json)', () => {
  assert.equal(newestEpisode(fixture.episodes, null), dateOf(fixture.newest));
  assert.deepEqual(fixture.series.map(follows), [true, false, true]);
  for (const { seen, notices, more } of fixture.cases) {
    const picked = pickNewEpisodes(fixture.episodes, series, dateOf(seen));
    assert.deepEqual(picked.notices.map((x) => ({ ...x, episode: x.episode.Id })), notices, seen);
    assert.equal(picked.more, more, seen);
  }
  for (const { episodes, text } of fixture.texts) assert.equal(newEpisodesText(episodes), text);
});

test('three series at most, then a line for the others (tests/fixtures/new-episodes.json)', () => {
  const seen = Date.parse('2026-10-14T12:00:00Z');
  const make = (count) => {
    const episodes = Array.from({ length: count }, (_, i) => ({ Id: `e${i}`, Type: 'Episode', SeriesId: `s${i}`, Name: 'x', DateCreated: new Date(seen + (i + 1) * 60_000).toISOString() }));
    const shows = Object.fromEntries(episodes.map((x) => [x.SeriesId, { Id: x.SeriesId, Type: 'Series', Name: `Série ${x.SeriesId}`, UserData: { IsFavorite: true } }]));
    return pickNewEpisodes(episodes, shows, seen);
  };
  const many = make(fixture.many.series);
  assert.deepEqual(many.notices.map((x) => x.seriesId), fixture.many.notices);
  assert.equal(many.more, fixture.many.more);
  assert.equal(many.series, fixture.many.series);
  assert.equal(make(4).more, fixture.four.more);
  assert.equal(make(3).more, null);
});

test('Jellyfin dates, with their seven decimals; none is no date', () => {
  assert.equal(dateOf('2026-10-14T14:07:20.2982336Z'), Date.parse('2026-10-14T14:07:20.298Z'));
  assert.equal(dateOf('2026-10-14T14:07:20Z'), Date.parse('2026-10-14T14:07:20Z'));
  assert.equal(dateOf(undefined), null);
  assert.equal(dateOf('demain'), null);
});
