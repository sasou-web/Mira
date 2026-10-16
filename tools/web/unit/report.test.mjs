// « Ton bilan » of Mira web (src/Mira.Web/js/report.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { watchReport, reportTime, reportStart } from '../../../src/Mira.Web/js/report.js';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/report.json', import.meta.url), 'utf8'));

test('the summary, as Windows and Mac make it (tests/fixtures/report.json)', () => {
  assert.deepEqual(watchReport(fixture.played, fixture.series, Date.parse(fixture.from)), fixture.expected);
  for (const [minutes, text] of fixture.times) assert.equal(reportTime(minutes), text);
});

test('the periods start on the 1st of the month or of January, at midnight here', () => {
  const now = new Date(2026, 9, 14, 18, 30).getTime();
  assert.equal(reportStart(now, false), new Date(2026, 9, 1).getTime());
  assert.equal(reportStart(now, true), new Date(2026, 0, 1).getTime());
});

test('the time in English, and a series without a name', () => {
  try {
    setLanguage('en');
    assert.equal(reportTime(45), '45 min');
    assert.equal(reportTime(180), '3h');
    assert.equal(reportTime(725), '12h 05m');
    const episode = { Id: 'e', Type: 'Episode', SeriesId: 's', RunTimeTicks: 600_000_000, UserData: { LastPlayedDate: '2026-10-02T10:00:00Z' } };
    assert.equal(watchReport([episode], [], 0).series[0].name, 'Show');
  } finally { setLanguage('fr'); }
});
