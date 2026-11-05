// The announcement of a release in the #releases channel of Mira's Discord: one message, posted by a Discord webhook,
// with the version's banner (docs/releases/x.y.z.jpg, drawn by tools/banner) and its English summary and highlights
// from WhatsNew.json, the same words as the release page. Run by discord.yml when a release is published, and by hand
// (Run workflow) to announce one again. Nothing but this public text goes to Discord.
//   node tools/discord/announce.mjs <x.y.z> [--dry-run]
// Environment: DISCORD_WEBHOOK (the channel's webhook URL; without it, or with --dry-run, the message is only printed),
// RELEASE_URL and RELEASE_DATE (the release page and its publication time, read from GitHub by discord.yml).
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const repository = 'https://github.com/sasou-web/Mira';
const raw = 'https://raw.githubusercontent.com/sasou-web/Mira';

// Discord's limits for an embed (developer docs, "Embed Limits"), kept well under.
const limits = { title: 256, description: 4096, total: 6000 };

/** Where GitHub serves a version's banner: the file at its tag, as in the release notes (WhatsNew.BannerUrl). */
export function bannerUrl(version) {
  return `${raw}/v${version}/docs/releases/${version}.jpg`;
}

/** The webhook's message for one entry of WhatsNew.json. */
export function payload(entry, { url, date, banner = true } = {}) {
  const version = entry.version;
  const page = url ?? `${repository}/releases/tag/v${version}`;
  const lines = entry.items.map((item) => `• **${item.en.title}** — ${item.en.text}`);
  const links = `**[Download Mira ${version}](${page})** · [What’s new in detail](${repository}/blob/v${version}/CHANGELOG.md) · [miracinema.app](https://miracinema.app)`;
  const description = [entry.en.summary, lines.join('\n'), links].join('\n\n');
  if (description.length > limits.description) throw new Error(`The announcement of ${version} is ${description.length} characters long: Discord takes ${limits.description}.`);
  const embed = {
    title: `Mira ${version} is out`,
    url: page,
    description,
    color: 0xf5f5f5,
    footer: { text: 'Windows · Mac · iPhone' },
  };
  if (date) embed.timestamp = new Date(date).toISOString();
  if (banner) embed.image = { url: bannerUrl(version) };
  return {
    username: 'Mira',
    avatar_url: `${raw}/v${version}/site/img/icon-512.png`,
    embeds: [embed],
    // Never a ping, whatever the text holds.
    allowed_mentions: { parse: [] },
  };
}

/** Only a Discord webhook URL is ever posted to. */
export function isWebhook(address) {
  try {
    const url = new URL(address);
    return url.protocol === 'https:' && ['discord.com', 'discordapp.com', 'ptb.discord.com', 'canary.discord.com'].includes(url.hostname)
      && /^\/api\/webhooks\/\d+\/[\w-]+\/?$/.test(url.pathname);
  } catch {
    return false;
  }
}

async function main() {
  const args = process.argv.slice(2);
  const version = args.find((x) => !x.startsWith('--'));
  const dryRun = args.includes('--dry-run');
  if (!version || !/^\d+\.\d+\.\d+$/.test(version)) {
    console.error('Usage: node tools/discord/announce.mjs <x.y.z> [--dry-run]');
    process.exit(1);
  }
  const notes = JSON.parse(readFileSync('src/Mira.Core/WhatsNew.json', 'utf8'));
  const entry = notes.find((x) => x.version === version);
  if (!entry) {
    console.error(`WhatsNew.json has no entry for ${version}.`);
    process.exit(1);
  }
  // A release without a banner is still announced, without the picture (Discord would show a broken one).
  // Asked a few times: the tag is only minutes old when a release is published.
  let banner = false;
  for (let attempt = 1; attempt <= (dryRun ? 1 : 4) && !banner; attempt++) {
    if (attempt > 1) await new Promise((resolve) => setTimeout(resolve, 15000));
    try {
      banner = (await fetch(bannerUrl(version), { method: 'HEAD' })).ok;
    } catch {
      banner = false;
    }
  }
  if (!banner) console.log(`::warning::No banner at ${bannerUrl(version)}: the announcement goes without a picture.`);
  const body = payload(entry, { url: process.env.RELEASE_URL || undefined, date: process.env.RELEASE_DATE || undefined, banner });
  const size = JSON.stringify(body.embeds[0]).length;
  if (size > limits.total) throw new Error(`The announcement of ${version} is too long for Discord (${size} characters).`);
  const webhook = process.env.DISCORD_WEBHOOK ?? '';
  if (dryRun || !webhook) {
    if (!dryRun) console.log('::notice::No DISCORD_RELEASES_WEBHOOK secret: the announcement is only printed.');
    console.log(JSON.stringify(body, null, 2));
    return;
  }
  if (!isWebhook(webhook)) throw new Error('DISCORD_RELEASES_WEBHOOK is not a Discord webhook URL (https://discord.com/api/webhooks/<id>/<token>).');
  // wait=true: Discord answers with the message, or says why not. Tried 4 times (a Discord hiccup, a rate limit).
  for (let attempt = 1; ; attempt++) {
    const response = await fetch(`${webhook.replace(/\/$/, '')}?wait=true`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (response.ok) {
      const message = await response.json();
      console.log(`Announced Mira ${version} on Discord (message ${message.id}).`);
      return;
    }
    const answer = (await response.text()).slice(0, 500);
    if (attempt >= 4 || (response.status < 500 && response.status !== 429)) throw new Error(`Discord answered ${response.status}: ${answer}`);
    const wait = Number(response.headers.get('retry-after')) || 2 ** attempt;
    console.log(`Discord answered ${response.status}; again in ${wait} s.`);
    await new Promise((resolve) => setTimeout(resolve, wait * 1000));
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
}
