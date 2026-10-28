// « Sous-titres automatiques » in Mira web (src/Mira.Web/js/subtitles-auto.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { journalLine, waitingLine, AUTO_LANGUAGES, MAX_LANGUAGES } from '../../../src/Mira.Web/js/subtitles-auto.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/subtitles-auto.json', import.meta.url), 'utf8'));

test('each line of the journal, as Windows and Mac say it (tests/fixtures/subtitles-auto.json)', () => {
  for (const { entry, line } of fixture.journal) assert.equal(journalLine(entry), line, entry.kind);
});

test('what the loop is doing, under the title (tests/fixtures/subtitles-auto.json)', () => {
  for (const { state, line } of fixture.waiting) assert.equal(waitingLine(state), line);
  assert.match(waitingLine({ settings: { enabled: true, languages: ['fre'] }, pausedUntil: '2026-10-28T12:00:00Z' }, () => '13:00'), /^Quota épuisé : reprise à 13:00\.$/);
  assert.equal(new Set(AUTO_LANGUAGES).size, AUTO_LANGUAGES.length);
  assert.ok(MAX_LANGUAGES === 5);
});
