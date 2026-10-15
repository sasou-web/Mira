// « Sous-titres gardés sur Jellyfin » in Mira web (src/Mira.Web/js/subtitles.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { searchCode, searchLanguages, sortRemote, remoteText, privacyText, keptText, arrivedSubtitle } from '../../../src/Mira.Web/js/subtitles.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/subtitles.json', import.meta.url), 'utf8'));

test('the languages searched, from the subtitle preferences (tests/fixtures/subtitles.json)', () => {
  for (const { preferences, languages } of fixture.searchLanguages) assert.deepEqual(searchLanguages(preferences), languages, preferences);
  assert.equal(searchCode('fr-FR'), 'fre');
  assert.equal(searchCode('deu'), 'ger');
  assert.equal(searchCode('und'), null);
});

test('the results, in rows and in order, as Windows says them (tests/fixtures/subtitles.json)', () => {
  for (const { found, label, details } of fixture.remote) assert.deepEqual(remoteText(found), { label, details });
  assert.deepEqual(sortRemote(fixture.sort.found).map((x) => x.Id), fixture.sort.order);
  assert.deepEqual(sortRemote(null), []);
  for (const { providers, text } of fixture.privacy) assert.equal(privacyText(providers), text);
});

test('what Mira says once Jellyfin has the subtitle, and which track it is (tests/fixtures/subtitles.json)', () => {
  for (const { kept, language, text } of fixture.kept) assert.equal(keptText(kept, language), text, kept);
  assert.equal(arrivedSubtitle(fixture.arrived.before, fixture.arrived.now)?.Index, fixture.arrived.index);
  assert.equal(arrivedSubtitle(fixture.arrived.now, fixture.arrived.now), null);
});
