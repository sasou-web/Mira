// « Qualité » as Mira web (player/stream.js) and Mira for Windows and Mac (StreamQuality of Mira.Core) ask it: the
// same cases for both, in tests/fixtures/bitrate.json. node --test tools/web/unit/*.test.mjs
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { knownBitrate } from '../../../src/Mira.Web/js/player/stream.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/bitrate.json', import.meta.url), 'utf8'));

test('the bitrate of « Qualité », as Windows and Mac ask it (tests/fixtures/bitrate.json)', () => {
  const now = 1_800_000_000_000;
  for (const [choice, atHome, measure, fileBitrate, expected] of fixture.known) {
    const known = knownBitrate(choice, { atHome, measure: measure && { at: now - measure[0] * 60_000, speed: measure[1] }, now, fileBitrate });
    assert.equal(known, expected, JSON.stringify([choice, atHome, measure, fileBitrate]));
  }
});
