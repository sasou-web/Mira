// The web player's sleep timer (src/Mira.Web/js/player/sleep.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { readFileSync } from 'node:fs';
import { SLEEP_MINUTES, sleepDuration, sleepTimer, sleepChoices } from '../../../src/Mira.Web/js/player/sleep.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/sleep.json', import.meta.url), 'utf8'));

test('durations, as Windows and Mac write them (tests/fixtures/sleep.json)', () => {
  for (const [minutes, label] of fixture.durations) assert.equal(sleepDuration(minutes), label);
  assert.deepEqual(SLEEP_MINUTES, [15, 30, 60]);
});

test('the status, and the pause once the minutes are over (tests/fixtures/sleep.json)', () => {
  for (const [choice, elapsed, status, due] of fixture.status) {
    const timer = sleepTimer();
    const start = 1_000_000;
    timer.choose(choice, start);
    const now = start + elapsed * 1000;
    assert.equal(timer.due(now), due, `${choice} after ${elapsed} s`);
    assert.equal(timer.status(now), status, `${choice} after ${elapsed} s`);
    // Due once: the timer is off after it.
    if (due) assert.equal(timer.chosen, null);
  }
});

test('the end of the episode: no pause, the next episode held back until cleared', () => {
  const timer = sleepTimer();
  timer.choose(0, 0);
  assert.ok(timer.atEnd);
  assert.equal(timer.due(10 * 3600_000), false);
  timer.clear();
  assert.equal(timer.atEnd, false);
  assert.equal(timer.status(0), null);
});

test('the choices of the sheet: the end of the episode only for an episode, the chosen one ticked with its time left', () => {
  const timer = sleepTimer();
  assert.deepEqual(sleepChoices(timer, { episode: false, now: 0 }).map((c) => c.label), ['Désactivée', '15 min', '30 min', '1 h']);
  timer.choose(30, 0);
  const choices = sleepChoices(timer, { episode: true, now: 60_000 });
  assert.deepEqual(choices.map((c) => c.label), ['Désactivée', '15 min', '30 min', '1 h', 'Fin de l’épisode']);
  assert.deepEqual(choices.filter((c) => c.selected).map((c) => [c.label, c.sub]), [['30 min', 'Pause dans 29 min']]);
  timer.clear();
  assert.deepEqual(sleepChoices(timer, { episode: true, now: 0 }).filter((c) => c.selected).map((c) => c.label), ['Désactivée']);
});

test('the sleep timer in English', () => {
  try {
    setLanguage('en');
    assert.equal(sleepDuration(90), '1h 30m');
    const timer = sleepTimer();
    timer.choose(15, 0);
    assert.equal(timer.status(61_000), 'Pauses in 14 min');
    timer.choose(0, 0);
    assert.equal(timer.status(0), 'Stops at the end of the episode');
    assert.deepEqual(sleepChoices(timer, { episode: true, now: 0 }).map((c) => c.label), ['Off', '15 min', '30 min', '1h', 'End of episode']);
  } finally { setLanguage('fr'); }
});
