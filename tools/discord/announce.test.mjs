// node --test tools/discord/announce.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { payload, isWebhook, bannerUrl } from './announce.mjs';

const notes = JSON.parse(readFileSync('src/Mira.Core/WhatsNew.json', 'utf8'));

test('every version of WhatsNew.json makes an announcement Discord takes', () => {
  for (const entry of notes) {
    const body = payload(entry, { date: '2026-11-05T18:00:00Z' });
    const [embed] = body.embeds;
    assert.equal(embed.title, `Mira ${entry.version} is out`);
    assert.ok(embed.description.startsWith(entry.en.summary));
    if (entry.changes?.length) for (const change of entry.changes) assert.ok(embed.description.includes(`• **${change.en.category}:** ${change.en.text}`));
    else for (const item of entry.items) assert.ok(embed.description.includes(`**${item.en.title}** — ${item.en.text}`));
    assert.ok(embed.description.length <= 4096);
    assert.ok(JSON.stringify(embed).length <= 6000);
    assert.equal(embed.image.url, bannerUrl(entry.version));
    assert.deepEqual(body.allowed_mentions, { parse: [] });
  }
});

test('the banner is the file at the tag, as in the release notes', () => {
  assert.equal(bannerUrl('0.9.4'), 'https://raw.githubusercontent.com/sasou-web/Mira/v0.9.4/docs/releases/0.9.4.jpg');
});

test('no banner, no picture; the release page given is the link', () => {
  const body = payload(notes[0], { banner: false, url: 'https://example.test/r' });
  assert.equal(body.embeds[0].image, undefined);
  assert.equal(body.embeds[0].url, 'https://example.test/r');
});

test('only a Discord webhook URL is posted to', () => {
  assert.ok(isWebhook('https://discord.com/api/webhooks/123456/abc-DEF_9'));
  assert.ok(isWebhook('https://discordapp.com/api/webhooks/123456/abc'));
  assert.ok(!isWebhook('https://discord.com/api/webhooks/123456/abc/github'));
  assert.ok(!isWebhook('http://discord.com/api/webhooks/123456/abc'));
  assert.ok(!isWebhook('https://discord.com.evil.test/api/webhooks/1/a'));
  assert.ok(!isWebhook('not a url'));
});
