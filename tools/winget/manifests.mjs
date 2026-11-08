// Mira's manifests for winget (`winget install sasou-web.Mira`): the four files a version of a package needs in the
// community repository microsoft/winget-pkgs (version, installer, en-US and fr-FR locales, schema 1.10.0: the newest the winget of GitHub's Windows runners, 1.11, reads without a warning), written
// from a published release: its Windows installer (Mira-x.y.z-win-x64-setup.exe, Inno Setup, for the current user,
// without administrator rights) and its SHA-256, and the words of WhatsNew.json for the release notes. Run by
// winget.yml when a release is published; submitted only with the WINGET_TOKEN secret (wingetcreate submit).
//   node tools/winget/manifests.mjs <x.y.z> <sha256> <yyyy-mm-dd> <out-dir>
// Writes <out-dir>/manifests/s/sasou-web/Mira/<x.y.z>/*.yaml, the folder layout of winget-pkgs.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const identifier = 'sasou-web.Mira';
export const manifestVersion = '1.10.0';
const repository = 'https://github.com/sasou-web/Mira';
const root = fileURLToPath(new URL('../..', import.meta.url));

/** Inno Setup's AppId, read from installer/Mira.iss: Windows lists Mira under "<AppId>_is1". */
export function productCode(iss = readFileSync(join(root, 'installer/Mira.iss'), 'utf8')) {
  const id = /^AppId=\{(\{[0-9A-F-]{36}\})\s*$/im.exec(iss);
  if (!id) throw new Error('No AppId in installer/Mira.iss.');
  return `${id[1].toUpperCase()}_is1`;
}

/** The oldest Windows Mira installs on (MinVersion of installer/Mira.iss), as winget writes it. */
export function minimumOs(iss = readFileSync(join(root, 'installer/Mira.iss'), 'utf8')) {
  const min = /^MinVersion=(\d+\.\d+\.\d+)\s*$/im.exec(iss);
  if (!min) throw new Error('No MinVersion in installer/Mira.iss.');
  return `${min[1]}.0`;
}

/** A YAML scalar: JSON's double-quoted strings are YAML too, so anything that is not a plain word is written that way. */
function scalar(value) {
  return /^[A-Za-z0-9][\w.\-/]*$/.test(value) && !/^(true|false|yes|no|on|off|null|~)$/i.test(value) ? value : JSON.stringify(value);
}

function yaml(kind, fields) {
  const lines = [`# yaml-language-server: $schema=https://aka.ms/winget-manifest.${kind}.${manifestVersion}.schema.json`, ''];
  const write = (value, indent, key) => {
    const pad = ' '.repeat(indent);
    if (Array.isArray(value)) {
      lines.push(`${pad}${key}:`);
      for (const item of value) {
        if (item && typeof item === 'object') {
          const [first, ...rest] = Object.entries(item);
          const start = lines.length;
          write(first[1], indent + 2, first[0]);
          lines[start] = `${pad}- ${lines[start].slice(indent + 2)}`;
          for (const [k, v] of rest) write(v, indent + 2, k);
        } else lines.push(`${pad}- ${scalar(String(item))}`);
      }
    } else if (value && typeof value === 'object') {
      lines.push(`${pad}${key}:`);
      for (const [k, v] of Object.entries(value)) write(v, indent + 2, k);
    } else lines.push(`${pad}${key}: ${scalar(String(value))}`);
  };
  for (const [key, value] of Object.entries(fields)) if (value !== undefined) write(value, 0, key);
  return `${lines.join('\n')}\n`;
}

/** The release notes of one language: the summary, then a line per point of the version's entry in WhatsNew.json. */
export function notes(entry, language) {
  const words = (item) => (language === 'en' ? item.en : item);
  return [words(entry).summary, ...entry.items.map((item) => `- ${words(item).title}: ${words(item).text}`)].join('\n');
}

const texts = {
  'en-US': {
    short: 'A cinema-style Jellyfin client for Windows, with mpv built in.',
    description: 'Mira plays your Jellyfin library in a native Windows app with mpv: direct play of almost any file, '
      + 'skip intros, watch together with friends, subtitles kept on your server, and, on the PC that runs Jellyfin, '
      + 'its setup in a few clicks. Also for Mac and iPhone. Free and open source, no account but your Jellyfin.',
    url: 'https://miracinema.app/en/',
    guide: { label: 'Guide', url: 'https://wiki.miracinema.app/windows/' },
    privacy: 'https://wiki.miracinema.app/privacy/',
  },
  'fr-FR': {
    short: 'Un client Jellyfin « cinéma » pour Windows, avec mpv intégré.',
    description: 'Mira lit ta bibliothèque Jellyfin dans une vraie app Windows avec mpv : lecture directe de presque '
      + 'tous les fichiers, intros passées, regarder ensemble avec des amis, sous-titres gardés sur ton serveur et, sur le '
      + 'PC de Jellyfin, son installation en quelques clics. Aussi pour Mac et iPhone. Libre et gratuit, aucun compte à '
      + 'part ton Jellyfin.',
    url: 'https://miracinema.app/',
    guide: { label: 'Guide', url: 'https://wiki.miracinema.app/fr/windows/' },
    privacy: 'https://wiki.miracinema.app/fr/privacy/',
  },
};

/** The four manifests of one version, by file name. */
export function manifests({ version, sha256, date, entry, iss }) {
  if (!/^\d+\.\d+\.\d+$/.test(version)) throw new Error(`${version} is not a version of Mira (x.y.z).`);
  if (!/^[0-9a-f]{64}$/i.test(sha256)) throw new Error(`${sha256} is not a SHA-256.`);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) throw new Error(`${date} is not a date (yyyy-mm-dd).`);
  if (entry?.version !== version) throw new Error(`WhatsNew.json has no entry for ${version}.`);
  const code = productCode(iss);
  const base = { PackageIdentifier: identifier, PackageVersion: version };
  const release = `${repository}/releases/tag/v${version}`;
  const locale = (tag, kind, language) => {
    const t = texts[tag];
    return yaml(kind, {
      ...base,
      PackageLocale: tag,
      Publisher: 'sasou-web',
      PublisherUrl: 'https://github.com/sasou-web',
      PublisherSupportUrl: `${repository}/issues`,
      PrivacyUrl: t.privacy,
      Author: 'sasou-web',
      PackageName: 'Mira',
      PackageUrl: t.url,
      License: 'MIT',
      LicenseUrl: `${repository}/blob/HEAD/LICENSE`,
      Copyright: 'Copyright (c) 2026 sasou-web and Mira contributors',
      CopyrightUrl: `${repository}/blob/HEAD/LICENSE`,
      ShortDescription: t.short,
      Description: t.description,
      Tags: ['jellyfin', 'media-player', 'mpv', 'movies', 'video', 'watch-together'],
      ReleaseNotes: notes(entry, language),
      ReleaseNotesUrl: release,
      Documentations: [{ DocumentLabel: t.guide.label, DocumentUrl: t.guide.url }],
      ManifestType: kind === 'defaultLocale' ? 'defaultLocale' : 'locale',
      ManifestVersion: manifestVersion,
    });
  };
  return {
    [`${identifier}.yaml`]: yaml('version', { ...base, DefaultLocale: 'en-US', ManifestType: 'version', ManifestVersion: manifestVersion }),
    [`${identifier}.installer.yaml`]: yaml('installer', {
      ...base,
      InstallerLocale: 'en-US',
      InstallerType: 'inno',
      Scope: 'user',
      InstallModes: ['interactive', 'silent', 'silentWithProgress'],
      UpgradeBehavior: 'install',
      MinimumOSVersion: minimumOs(iss),
      ReleaseDate: date,
      Installers: [{
        Architecture: 'x64',
        InstallerUrl: `${repository}/releases/download/v${version}/Mira-${version}-win-x64-setup.exe`,
        InstallerSha256: sha256.toUpperCase(),
        ProductCode: code,
        AppsAndFeaturesEntries: [{ DisplayName: 'Mira', Publisher: 'sasou-web', DisplayVersion: version, ProductCode: code }],
        InstallationMetadata: { DefaultInstallLocation: '%LocalAppData%\\Programs\\Mira' },
      }],
      ManifestType: 'installer',
      ManifestVersion: manifestVersion,
    }),
    [`${identifier}.locale.en-US.yaml`]: locale('en-US', 'defaultLocale', 'en'),
    [`${identifier}.locale.fr-FR.yaml`]: locale('fr-FR', 'locale', 'fr'),
  };
}

/** Where winget-pkgs keeps a version: manifests/<first letter>/<publisher>/<name>/<version>. */
export function folder(version) {
  return join('manifests', 's', 'sasou-web', 'Mira', version);
}

function main() {
  const [version, sha256, date, out] = process.argv.slice(2);
  if (!out) {
    console.error('node tools/winget/manifests.mjs <x.y.z> <sha256> <yyyy-mm-dd> <out-dir>');
    process.exit(2);
  }
  const whatsNew = JSON.parse(readFileSync(join(root, 'src/Mira.Core/WhatsNew.json'), 'utf8'));
  const files = manifests({ version, sha256, date, entry: whatsNew.find((e) => e.version === version) });
  const dir = join(out, folder(version));
  mkdirSync(dir, { recursive: true });
  for (const [name, text] of Object.entries(files)) writeFileSync(join(dir, name), text);
  console.log(dir);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) main();
