// Collections made by hand (src/Mira.Web/js/collections.js), without a browser: the same cases as TitleCollections of
// Mira for Windows and Mac (tests/fixtures/collections.json).
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { nameProblem, problemText, folderOf, collectionRights, holds, sortedCollections, MAX_NAME } from '../../../src/Mira.Web/js/collections.js';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/collections.json', import.meta.url), 'utf8'));

test('the names refused: empty, too long, unusable, or already a collection\'s name or folder (tests/fixtures/collections.json)', () => {
  for (const [name, renamed, problem, taken] of fixture.names) {
    const refusal = nameProblem(name, fixture.collections, renamed);
    assert.equal(refusal?.problem ?? null, problem, JSON.stringify(name));
    assert.equal(refusal?.taken ?? null, taken, JSON.stringify(name));
  }
});

test('a collection\'s folder, from Linux or Windows (tests/fixtures/collections.json)', () => {
  for (const [path, folder] of fixture.folders) assert.equal(folderOf(path), folder, JSON.stringify(path));
});

test('who may manage collections and rename them (tests/fixtures/collections.json)', () => {
  for (const [policy, manage, rename] of fixture.rights) assert.deepEqual(collectionRights(policy), { manage, rename }, JSON.stringify(policy));
  assert.deepEqual(collectionRights(undefined), { manage: false, rename: false });
});

test('what goes in a collection, in which order they are listed, and what a refusal says', () => {
  assert.ok(holds({ Type: 'Movie' }) && holds({ Type: 'Series' }));
  assert.ok(!holds({ Type: 'Episode' }) && !holds({ Type: 'BoxSet' }) && !holds(null));
  assert.deepEqual(sortedCollections([{ Name: 'soirées' }, { Name: 'Ghibli' }, { Name: 'À montrer' }]).map((x) => x.Name), ['À montrer', 'Ghibli', 'soirées']);
  assert.equal(problemText({ problem: 'taken', taken: 'Soirées' }), 'Ce nom est déjà pris par la collection « Soirées ».');
  assert.equal(problemText({ problem: 'long' }), `Ce nom est trop long : ${MAX_NAME} caractères au plus.`);
  try {
    setLanguage('en');
    assert.equal(problemText({ problem: 'taken', taken: 'Soirées' }), 'This name is already taken by the collection “Soirées”.');
    assert.equal(problemText({ problem: 'empty' }), 'Give the collection a name.');
  } finally { setLanguage('fr'); }
});
