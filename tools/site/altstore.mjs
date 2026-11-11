// The AltStore source of Mira for iPhone, served by the site at https://miracinema.app/altstore.json: AltStore Classic
// reads it to install Mira and to offer each new version. Every published release that has the iPhone's .ipa (built
// unsigned by ios.yml, signed on the iPhone by AltStore) is a version, newest first, with its size from GitHub and its
// notes from WhatsNew.json; without any, the source lists no app yet (AltStore wants a version for each). In English, as
// the site that gives the source's address and the app itself: AltStore shows one language only.
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

// A version's notes: its summary, then one line per change (« Category: text »), else per point, as the release notes,
// Discord and winget give them; the French only where an entry has no English.
const english = (x) => x.en ?? x;
function describe(entry) {
  const lines = entry.changes?.length
    ? entry.changes.map((x) => `• ${english(x).category}: ${english(x).text}`)
    : entry.items.map((x) => `• ${english(x).title}: ${english(x).text}`);
  return [english(entry).summary, ...lines].join('\n');
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
    localizedDescription: entry ? describe(entry) : `Mira ${version}`,
    downloadURL: ipa.browser_download_url,
    size: ipa.size,
    minOSVersion: '26.0',
  });
}

const source = {
  name: 'Mira',
  subtitle: 'Your Jellyfin, like at the movies.',
  description: 'The source of Mira for iPhone and iPad: a free Jellyfin client, with no account and no statistics, that talks only to your server.',
  iconURL: `${site}/img/icon-512.png`,
  website: site,
  tintColor: '#5E5E66',
  apps: versions.length ? [{
    name: 'Mira',
    bundleIdentifier: 'app.miracinema.Mira',
    developerName: 'sasou-web',
    subtitle: 'Your Jellyfin, like at the movies.',
    localizedDescription: 'Mira for iPhone and iPad: your Jellyfin library in a native app, at home and away with Tailscale. Mira talks only to your Jellyfin server: no account with us, no statistics, no ads. Source code: https://github.com/sasou-web/Mira',
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
