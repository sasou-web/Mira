// Whether a title found on « Télécharger sur le PC serveur » is already in the library (src/Mira.Web/js/owned.js).
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { groupReleases } from '../../../src/Mira.Web/js/releases.js';
import { ownedLine, ownedLookups, lookupKey, MAX_LOOKUPS } from '../../../src/Mira.Web/js/owned.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/owned.json', import.meta.url), 'utf8'));
const groupOf = (name) => groupReleases([{ id: 'x', name, size: 2 ** 31, seeders: 10, source: 'check' }])[0];

test('what the library already has of a title found (tests/fixtures/owned.json)', () => {
  for (const { name, items, episodes = [], line } of fixture.cases) {
    const found = ownedLine(groupOf(name),
      items.map(([Type, Name, OriginalTitle, ProductionYear, Id]) => ({ Type, Name, OriginalTitle, ProductionYear, Id })),
      episodes.map(([ParentIndexNumber, IndexNumber, IndexNumberEnd]) => ({ ParentIndexNumber, IndexNumber, IndexNumberEnd })));
    assert.deepEqual(found && [found.text, found.itemId, found.owned], line, name);
  }
});

test('each title looked for once, a dozen at most', () => {
  const groups = groupReleases([
    { id: 'a', name: 'Mira.Check.S01E02.1080p.WEB-DL', size: 2 ** 31, seeders: 9, source: 'x' },
    { id: 'b', name: 'Mira.Check.S01E03.1080p.WEB-DL', size: 2 ** 31, seeders: 9, source: 'x' },
    { id: 'c', name: 'Mira.Check.2026.1080p.WEB-DL', size: 2 ** 31, seeders: 9, source: 'x' },
    ...Array.from({ length: 20 }, (_, i) => ({ id: `f${i}`, name: `Film ${String.fromCharCode(65 + i)} 2020 1080p BluRay`, size: 2 ** 31, seeders: 9, source: 'x' })),
  ]);
  const lookups = ownedLookups(groups);
  assert.deepEqual(lookups.slice(0, 2).map((x) => [x.title, x.film]), [['Mira Check', false], ['Mira Check', true]]);
  assert.equal(lookups.length, MAX_LOOKUPS);
  assert.equal(new Set(groups.slice(0, 2).map(lookupKey)).size, 1, 'two episodes of one series: one lookup');
});

test('in English', () => {
  try {
    setLanguage('en');
    assert.equal(ownedLine(groupOf('The.Bear.S03.1080p.WEB'), [{ Type: 'Series', Name: 'The Bear', Id: 's' }], [{ ParentIndexNumber: 3, IndexNumber: 1 }, { ParentIndexNumber: 3, IndexNumber: 2 }]).text, 'You already have 2 episodes of season 3');
    assert.equal(ownedLine(groupOf('Film.2020.1080p'), [{ Type: 'Movie', Name: 'Film', ProductionYear: 2020, Id: 'f' }]).text, 'Already in your library');
  } finally { setLanguage('fr'); }
});
