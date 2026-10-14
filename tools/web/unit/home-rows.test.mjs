// The rows of Mira web's home page (src/Mira.Web/js/home-rows.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { homeRows, moveRow, showRow } from '../../../src/Mira.Web/js/home-rows.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/home.json', import.meta.url), 'utf8'));
const line = (rows) => rows.map((x) => (x.shown ? '' : '-') + x.key);

test('the rows and their order, as Windows and Mac read them (tests/fixtures/home.json)', () => {
  for (const [saved, rows] of fixture.rows) assert.deepEqual(line(homeRows(saved, fixture.libraries)), rows, saved);
  const titles = homeRows('', fixture.libraries);
  for (const [key, title] of fixture.titles) assert.equal(titles.find((x) => x.key === key)?.title, title);
  assert.equal(titles.find((x) => x.key === 'library:a1').library, 'a1');
});

test('a row moved or put away (tests/fixtures/home.json)', () => {
  for (const [saved, key, step, after] of fixture.moves) assert.equal(moveRow(saved, fixture.libraries, key, step), after, `${saved} ${key} ${step}`);
  for (const [saved, key, shown, after] of fixture.shows) assert.equal(showRow(saved, fixture.libraries, key, shown), after, `${saved} ${key} ${shown}`);
});
