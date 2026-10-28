// The AltStore source of Mira for iPhone, served by the site at https://miracinema.app/altstore.json: AltStore Classic
// reads it to install Mira and to offer each new version. Every published release that has the iPhone's .ipa (built
// unsigned by ios.yml, signed on the iPhone by AltStore) is a version, newest first, with its size from GitHub and its
// summary from WhatsNew.json; without any, the source lists no app yet (AltStore wants a version for each).
//   gh api "repos/<owner>/<repo>/releases?per_page=30" > releases.json
//   node tools/site/altstore.mjs releases.json dist/site
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [releasesFile, out = 'dist/site'] = process.argv.slice(2);
if (!releasesFile) {
  console.error('Usage: node tools/site/altstore.mjs <releases.json> [out]');
  process.exit(1);
}
const site = 'https://miracinema.app';
const releases = JSON.parse(readFileSync(releasesFile, 'utf8'));
const notes = JSON.parse(readFileSync('src/Mira.Core/WhatsNew.json', 'utf8'));
// AltStore refuses an app whose Info.plist asks for something its source does not list: these are Mira's own
// (src/Mira.iOS/project.yml), and it asks for no entitlement.
const project = readFileSync('src/Mira.iOS/project.yml', 'utf8');
const localNetwork = /NSLocalNetworkUsageDescription: (.+)/.exec(project)?.[1].trim();
if (!localNetwork) {
  console.error('src/Mira.iOS/project.yml: NSLocalNetworkUsageDescription not found');
  process.exit(1);
}

const versions = [];
for (const release of releases) {
  if (release.draft || release.prerelease || !/^v\d+\.\d+\.\d+$/.test(release.tag_name)) continue;
  const version = release.tag_name.slice(1);
  const ipa = release.assets?.find((asset) => asset.name === `Mira-${version}-ios.ipa` && asset.state === 'uploaded');
  if (!ipa) continue;
  const entry = notes.find((x) => x.version === version);
  versions.push({
    version,
    buildVersion: version,
    date: release.published_at,
    localizedDescription: entry ? [entry.summary, ...entry.items.map((x) => `• ${x.title} : ${x.text}`)].join('\n') : `Mira ${version}`,
    downloadURL: ipa.browser_download_url,
    size: ipa.size,
    minOSVersion: '26.0',
  });
}

const source = {
  name: 'Mira',
  subtitle: 'Ton Jellyfin, comme au cinéma.',
  description: 'La source de Mira pour iPhone et iPad : un client Jellyfin libre, sans compte ni statistiques, qui ne parle qu’à ton serveur.',
  iconURL: `${site}/img/icon-512.png`,
  website: site,
  tintColor: '#5E5E66',
  apps: versions.length ? [{
    name: 'Mira',
    bundleIdentifier: 'app.miracinema.Mira',
    developerName: 'sasou-web',
    subtitle: 'Ton Jellyfin, comme au cinéma.',
    localizedDescription: 'Mira pour iPhone et iPad : ta bibliothèque Jellyfin dans une app native, à la maison et ailleurs avec Tailscale. Mira ne parle qu’à ton serveur Jellyfin : pas de compte chez nous, pas de statistiques, pas de pub. Code source : https://github.com/sasou-web/Mira',
    iconURL: `${site}/img/icon-512.png`,
    tintColor: '#5E5E66',
    category: 'entertainment',
    versions,
    appPermissions: { entitlements: [], privacy: { NSLocalNetworkUsageDescription: localNetwork } },
  }] : [],
  news: [],
};
writeFileSync(join(out, 'altstore.json'), JSON.stringify(source, null, 2) + '\n');
console.log(`${join(out, 'altstore.json')}: ${versions.length ? versions.map((x) => x.version).join(', ') : 'no version with an .ipa yet'}`);
