// « Ton bilan » of Mira web (src/Mira.Web/js/report.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { watchReport, reportTime, reportCount, reportStart } from '../../../src/Mira.Web/js/report.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/report.json', import.meta.url), 'utf8'));

test('the summary, as Windows and Mac make it (tests/fixtures/report.json)', () => {
  assert.deepEqual(watchReport(fixture.played, fixture.series, Date.parse(fixture.from)), fixture.expected);
  for (const [minutes, text] of fixture.times) assert.equal(reportTime(minutes), text);
  assert.equal(reportCount(1, 'film', 'films'), '1 film');
  assert.equal(reportCount(0, 'film', 'films'), '0 films');
});

test('the periods start on the 1st of the month or of January, at midnight here', () => {
  const now = new Date(2026, 9, 14, 18, 30).getTime();
  assert.equal(reportStart(now, false), new Date(2026, 9, 1).getTime());
  assert.equal(reportStart(now, true), new Date(2026, 0, 1).getTime());
});
