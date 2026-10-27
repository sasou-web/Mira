// Titles recognized badly: « Titres à corriger » and the search of « Identifier… » (src/Mira.Web/js/identify.js).
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { checkTitles, entryText, searchQuery, prefill, matchLine, imagePath } from '../../../src/Mira.Web/js/identify.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/identify.json', import.meta.url), 'utf8'));
const titles = fixture.titles.map(([Type, Id, Name, ProductionYear, ProviderIds, poster, Overview]) => ({
  Type, Id, Name, ProductionYear, ProviderIds, Overview, ImageTags: poster ? { Primary: 'tag' } : {},
}));

test('the titles to fix, in order, and what is said of each (tests/fixtures/identify.json)', () => {
  assert.deepEqual(checkTitles(titles).map((x) => [x.item.Id, entryText(x)]), fixture.problems);
});

test('what « Chercher » sends: a name and a year, or the id of a pasted link', () => {
  for (const [text, year, name, sentYear, ids] of fixture.queries) {
    assert.deepEqual(searchQuery(text, year), { name, year: sentYear, providerIds: ids }, text);
  }
});

test('the search « Identifier… » opens with', () => {
  for (const [Name, ProductionYear, name, year] of fixture.prefill) assert.deepEqual(prefill({ Name, ProductionYear }), { name, year }, Name);
});

test('a proposal of Jellyfin, and its poster through the extension only', () => {
  for (const [match, , line] of fixture.matches) assert.equal(matchLine(match), line);
  assert.equal(imagePath('https://image.tmdb.org/t/p/w500/a b.jpg'), 'Mira/identify/image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw500%2Fa%20b.jpg');
});
