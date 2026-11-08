// node --test tools/winget/manifests.test.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { folder, manifests, minimumOs, notes, productCode } from './manifests.mjs';

const whatsNew = JSON.parse(readFileSync(new URL('../../src/Mira.Core/WhatsNew.json', import.meta.url), 'utf8'));
const entry = whatsNew[0];
const sha = 'ab'.repeat(32);
const files = manifests({ version: entry.version, sha256: sha, date: '2026-11-05', entry });

/** The value of a top-level or nested `Key: value` line, JSON-quoted or plain. */
function field(text, key) {
  const line = text.split('\n').find((l) => l.trimStart().replace(/^- /, '').startsWith(`${key}: `));
  assert.ok(line, `${key} is missing`);
  const value = line.slice(line.indexOf(': ') + 2);
  return value.startsWith('"') ? JSON.parse(value) : value;
}

test('Le code produit est l’AppId de l’installateur, comme Windows le liste', () => {
  assert.match(productCode(), /^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}_is1$/);
  assert.equal(productCode('AppId={{7d5e2c41-9b8a-4f63-a1e7-3c5b9d2f8a64}\r\n'), '{7D5E2C41-9B8A-4F63-A1E7-3C5B9D2F8A64}_is1');
  assert.throws(() => productCode('AppName=Mira'));
  assert.equal(minimumOs('MinVersion=10.0.19041\n'), '10.0.19041.0');
});

test('Quatre manifestes, dans le dossier de winget-pkgs', () => {
  assert.deepEqual(Object.keys(files).sort(), [
    'sasou-web.Mira.installer.yaml', 'sasou-web.Mira.locale.en-US.yaml', 'sasou-web.Mira.locale.fr-FR.yaml', 'sasou-web.Mira.yaml',
  ]);
  assert.equal(folder('1.2.3').replaceAll('\\', '/'), 'manifests/s/sasou-web/Mira/1.2.3');
  for (const [name, text] of Object.entries(files)) {
    assert.equal(field(text, 'PackageIdentifier'), 'sasou-web.Mira', name);
    assert.equal(field(text, 'PackageVersion'), entry.version, name);
    assert.equal(field(text, 'ManifestVersion'), '1.10.0', name);
    assert.doesNotMatch(text, /\t|\r/, name);
  }
});

test('L’installateur : celui de la release, son empreinte, pour l’utilisateur seul', () => {
  const installer = files['sasou-web.Mira.installer.yaml'];
  assert.equal(field(installer, 'InstallerUrl'), `https://github.com/sasou-web/Mira/releases/download/v${entry.version}/Mira-${entry.version}-win-x64-setup.exe`);
  assert.equal(field(installer, 'InstallerSha256'), sha.toUpperCase());
  assert.equal(field(installer, 'InstallerType'), 'inno');
  assert.equal(field(installer, 'Scope'), 'user');
  assert.equal(field(installer, 'DisplayVersion'), entry.version);
  assert.equal(field(installer, 'ProductCode'), productCode());
  assert.equal(field(installer, 'DefaultInstallLocation'), '%LocalAppData%\\Programs\\Mira');
  assert.equal(field(installer, 'ReleaseDate'), '2026-11-05');
});

test('Notes et descriptions dans les limites de winget, en anglais et en français', () => {
  const en = files['sasou-web.Mira.locale.en-US.yaml'];
  const fr = files['sasou-web.Mira.locale.fr-FR.yaml'];
  assert.equal(field(en, 'ManifestType'), 'defaultLocale');
  assert.equal(field(fr, 'ManifestType'), 'locale');
  assert.ok(field(en, 'ReleaseNotes').startsWith(entry.en.summary));
  assert.ok(field(fr, 'ReleaseNotes').startsWith(entry.summary));
  for (const text of [en, fr]) {
    assert.ok(field(text, 'ShortDescription').length <= 256);
    assert.ok(field(text, 'Description').length <= 10000);
    assert.ok(field(text, 'ReleaseNotes').length <= 10000);
    // Like the site: nothing about torrents in the public words.
    assert.doesNotMatch(text, /torlink|torrent|qbittorrent/i);
  }
  assert.equal(notes({ en: { summary: 'S' }, items: [{ en: { title: 'T', text: 'X' } }] }, 'en'), 'S\n- T: X');
  assert.equal(notes({ en: { summary: 'S' }, items: [{ en: { title: 'T', text: 'X' } }], changes: [{ category: 'Mac', text: 'Y', en: { category: 'Mac', text: 'Z' } }] }, 'en'), 'S\n- Mac: Z');
});

test('Refuse une version, une empreinte, une date ou une entrée qui ne vont pas', () => {
  assert.throws(() => manifests({ version: '1.0', sha256: sha, date: '2026-11-05', entry }));
  assert.throws(() => manifests({ version: entry.version, sha256: 'abc', date: '2026-11-05', entry }));
  assert.throws(() => manifests({ version: entry.version, sha256: sha, date: '5/11/2026', entry }));
  assert.throws(() => manifests({ version: '9.9.9', sha256: sha, date: '2026-11-05', entry }));
});
