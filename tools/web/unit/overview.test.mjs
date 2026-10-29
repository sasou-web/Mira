// A title's summary in plain text (src/Mira.Web/js/overview.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { plainOverview } from '../../../src/Mira.Web/js/overview.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/overview.json', import.meta.url), 'utf8'));

test('a summary in HTML shown as text, as Windows, Mac and the iPhone app (tests/fixtures/overview.json)', () => {
  for (const [name, text, plain, flat] of fixture.cases) {
    assert.equal(plainOverview(text), plain, name);
    assert.equal(plainOverview(text, { flat: true }), flat, `${name} (flat)`);
  }
  assert.equal(plainOverview(null), '');
});
