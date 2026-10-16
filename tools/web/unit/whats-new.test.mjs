// « Nouveau dans Mira » of Mira web (src/Mira.Web/js/whats-new.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { since, phoneNotes, worthAsking, webIcon } from '../../../src/Mira.Web/js/whats-new.js';
import { icons } from '../../../src/Mira.Web/js/icons.js';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/whats-new.json', import.meta.url), 'utf8'));
const notes = JSON.parse(readFileSync(new URL('../../../src/Mira.Core/WhatsNew.json', import.meta.url), 'utf8'));

test('the versions shown after an update, as Windows chooses them (tests/fixtures/whats-new.json)', () => {
  for (const { seen, current, since: expected, phone } of fixture.cases) {
    assert.deepEqual(since(fixture.entries, seen, current).map((x) => x.version), expected, `since ${seen} up to ${current}`);
    assert.deepEqual(phoneNotes(fixture.entries, seen, current).map((x) => x.version), phone, `Mira web, since ${seen} up to ${current}`);
  }
  // Only the points marked for Mira web, in their order.
  assert.deepEqual(phoneNotes(fixture.entries, '0.5.3', '0.5.6').map((x) => x.items.map((i) => i.title)), [['Partout'], ['Pour le téléphone']]);
});

test('looked at once per version, never on a copy Jellyfin did not serve', () => {
  assert.equal(worthAsking('', '0.7.9'), true);
  assert.equal(worthAsking('0.7.8', '0.7.9'), true);
  assert.equal(worthAsking('0.7.9', '0.7.9'), false);
  assert.equal(worthAsking('0.8.0', '0.7.9'), false);
  assert.equal(worthAsking('', '0.0.0'), false);
  assert.equal(worthAsking('', '{{version}}'), false);
});

test('WhatsNew.json: "web" is true, false or absent, and each point for Mira web has an icon it draws', () => {
  const known = new Set(Object.keys(icons));
  for (const entry of notes) {
    for (const item of entry.items) {
      assert.ok(item.web === undefined || item.web === true || item.web === false, `${entry.version} · ${item.title}: web`);
      // A star stands for Mira's own sparkle; for any other icon, it means Mira web has none like it.
      if (item.web && item.icon !== 'sparkle') assert.notEqual(webIcon(item.icon, known), 'star', `${entry.version} · ${item.title}: icon ${item.icon}`);
    }
  }
  assert.equal(webIcon('film', known), 'films');
  assert.equal(webIcon('pas une icône', known), 'star');
});

test('in English, a point\'s "en" title and text, its French without one', () => {
  const entries = [{ version: '0.8.1', items: [
    { title: 'Partout', text: 'Le texte', web: true, en: { title: 'Everywhere', text: 'The text' } },
    { title: 'Sans anglais', text: 'Encore', web: true },
  ] }];
  try {
    setLanguage('en');
    assert.deepEqual(phoneNotes(entries, '0.8.0', '0.8.1')[0].items.map((x) => [x.title, x.text]), [['Everywhere', 'The text'], ['Sans anglais', 'Encore']]);
    setLanguage('fr');
    assert.deepEqual(phoneNotes(entries, '0.8.0', '0.8.1')[0].items.map((x) => x.title), ['Partout', 'Sans anglais']);
  } finally { setLanguage('fr'); }
});
