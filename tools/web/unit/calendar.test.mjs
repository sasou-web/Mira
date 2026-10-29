// « Épisodes manquants et prochaines sorties » of Mira web (src/Mira.Web/js/calendar.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { calendar, missingText, nextText, dayText, dayTitle, byDay, airDay, today } from '../../../src/Mira.Web/js/calendar.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/calendar.json', import.meta.url), 'utf8'));
const code = (x) => `S${x.ParentIndexNumber}E${x.IndexNumber}`;
const episode = (text) => {
  const [, s, e] = /^S(\d+)E(\d+)$/.exec(text);
  return { Type: 'Episode', LocationType: 'Virtual', ParentIndexNumber: Number(s), IndexNumber: Number(e), PremiereDate: '2026-10-01T00:00:00.0000000Z' };
};

test('missing and coming episodes, and what the series page says, as Windows and Mac (tests/fixtures/calendar.json)', () => {
  const { missing, coming } = calendar(fixture.episodes, fixture.today);
  assert.deepEqual(missing.map(code), fixture.missing);
  assert.deepEqual(coming.map(code), fixture.coming);
  assert.equal(missingText(missing), fixture.texts.missing);
  assert.equal(nextText(coming, fixture.today), fixture.texts.next);
  assert.deepEqual(byDay(coming).map((x) => [x.day, x.episodes.map(code)]), fixture.byDay);
  for (const [list, text] of fixture.ranges) assert.equal(missingText(list.map(episode)), text, list.join());
  for (const [day, text] of fixture.days) assert.equal(dayText(day, fixture.today), text, day);
});

test('a day at the head of its list, an episode without its day, today on this device', () => {
  assert.equal(dayTitle('2026-10-29', '2026-10-29'), 'Aujourd’hui');
  assert.equal(dayTitle('2026-10-31', '2026-10-29'), 'Samedi');
  assert.equal(airDay({ PremiereDate: null }), null);
  assert.equal(nextText([], '2026-10-29'), '');
  assert.equal(today(new Date(2026, 9, 29, 23, 59)), '2026-10-29');
  assert.deepEqual(calendar(null, '2026-10-29'), { missing: [], coming: [] });
});
