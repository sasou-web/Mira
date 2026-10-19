// « Sous-titres gardés sur Jellyfin » in Mira web (src/Mira.Web/js/subtitles.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { searchCode, searchLanguages, sortRemote, remoteText, privacyText, keptText, arrivedSubtitle } from '../../../src/Mira.Web/js/subtitles.js';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';

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

test('the same texts in English', () => {
  try {
    setLanguage('en');
    assert.deepEqual(remoteText(fixture.remote[0].found), { label: 'Aube.Test.2021.1080p.WEB', details: 'Open Subtitles · SRT · 12,345 downloads · for this file' });
    assert.equal(remoteText(fixture.remote[1].found).details, 'Open Subtitles · ASS · 1 download · forced · SDH');
    assert.equal(remoteText({ Id: 'x' }).label, 'Subtitles');
    assert.equal(privacyText(['Open Subtitles', 'Addic7ed']), 'Jellyfin asks Open Subtitles and Addic7ed: the file’s name and its fingerprint are sent to that service.');
    assert.equal(privacyText([]), 'Jellyfin asks its subtitle plugin: the file’s name and its fingerprint are sent to that service.');
    assert.equal(keptText('Kept', 'fre'), 'French subtitles kept on Jellyfin: your other devices offer them too.');
    assert.equal(keptText('Kept', 'und'), 'Subtitles kept on Jellyfin: your other devices offer them too.');
    assert.match(keptText('KeptInJellyfinData', 'eng'), /^English subtitles kept on Jellyfin: .* Jellyfin can only read your videos folder/);
    assert.equal(keptText('NotWritten', 'fre'), 'Jellyfin couldn’t save these subtitles: it isn’t allowed to write to this title’s folder.');
  } finally { setLanguage('fr'); }
});
