// « Chercher une meilleure version »: which results of TorLink are this title in a better picture (src/Mira.Web/js/upgrade.js).
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { parseRelease } from '../../../src/Mira.Web/js/releases.js';
import { quality, currentQuality, better, fits, upgradeQuery, upgradeTitle, candidates, sameLength, qualityText } from '../../../src/Mira.Web/js/upgrade.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/upgrade.json', import.meta.url), 'utf8'));
const item = ([Type, ...rest]) => Type === 'Movie'
  ? { Type, Name: rest[0], OriginalTitle: rest[1], ProductionYear: rest[2] }
  : { Type, SeriesName: rest[0], ParentIndexNumber: rest[1], IndexNumber: rest[2], IndexNumberEnd: rest[3] };

test('the picture of a title’s file, and how the page says it (tests/fixtures/upgrade.json)', () => {
  for (const { width, height, path, quality: expected, line } of fixture.current) {
    const found = quality(width, height, path);
    assert.deepEqual([found.resolution, found.source], expected, path);
    assert.equal(qualityText(found), line, path);
  }
  const film = { Type: 'Movie', MediaSources: [{ Path: fixture.current[0].path, MediaStreams: [{ Type: 'Audio' }, { Type: 'Video', Width: 1280, Height: 720 }] }] };
  assert.deepEqual(currentQuality(film), { resolution: 720, source: 'webrip' });
});

test('a better picture than the file’s, never a whole season nor a version filmed in a theatre', () => {
  for (const [resolution, source, name, expected] of fixture.better) assert.equal(better({ resolution, source }, parseRelease(name)), expected, `${resolution} ${source} → ${name}`);
});

test('this very title: the film of that name and year, the episode of that season and number', () => {
  for (const [title, name, expected] of fixture.fits) assert.equal(fits(item(title), parseRelease(name)), expected, `${title.join(' ')} → ${name}`);
});

test('what TorLink searches, and the title said', () => {
  for (const [title, expected] of fixture.queries) assert.equal(upgradeQuery(item(title)), expected);
  for (const [title, expected] of fixture.titles) assert.equal(upgradeTitle(item(title)), expected);
});

test('only the better versions of this title among the results, as they came', () => {
  const { item: title, file: [Width, Height, Path], hits, kept } = fixture.candidates;
  const film = { ...item(title), MediaSources: [{ Path, MediaStreams: [{ Type: 'Video', Width, Height }] }] };
  assert.deepEqual(candidates(film, hits.map((name, i) => ({ id: `h${i}`, name }))).map((x) => x.name), kept);
});

test('the new file lasts as long as the old one, within 2 %', () => {
  for (const [old, found, expected] of fixture.lengths) assert.equal(sameLength(old, found), expected, `${old} → ${found}`);
});

test('in English', () => {
  try {
    setLanguage('en');
    assert.equal(qualityText({ resolution: null, source: null }), 'Unknown resolution · unknown source');
    assert.equal(qualityText({ resolution: 1080, source: 'bluray' }), '1080p · BluRay');
  } finally { setLanguage('fr'); }
});
