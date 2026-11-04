// The party page as a friend opens it on an iPhone (site/img/web-party-*.webp, a full screen like the README's captures):
// src/Mira.Web/party.html filled the way the extension fills it (PartyPage.Render), with its own style and script, the
// README's demo library, and the film's poster cut from Mira web's film page (docs/screenshots/readme/web-film.jpg).
// Needs Pillow (the same WebP as tools/site/images.py). Run from the repository's root after images.py:
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/site/party.mjs
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const origin = 'http://mira.test';
// Spider-Man: Across the Spider-Verse, as Mira web's film page shows its poster: 2:3 from the top of the capture.
const POSTER = [279, 0, 782, 1173];
const html = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll('\'', '&#39;');

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 402, height: 874 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true, colorScheme: 'dark', locale: 'en-US',
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 26_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Mobile/15E148 Safari/604.1',
});
const page = await context.newPage();
// Mira web's files where the extension serves them (/Mira/v/<revision>/), the README's captures beside them.
await page.route(`${origin}/**`, route => {
  const path = new URL(route.request().url()).pathname;
  if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Mira</title>' });
  const file = path.startsWith('/Mira/v/r/') ? join('src/Mira.Web', path.slice('/Mira/v/r/'.length))
    : path.startsWith('/readme/') ? join('docs/screenshots/readme', path.slice('/readme/'.length)) : null;
  return file ? route.fulfill({ path: resolve(file) }) : route.abort();
});
await page.goto(`${origin}/`);
const picture = await page.evaluate(async ([src, x, y, width, height]) => {
  const image = new Image();
  image.src = src;
  await image.decode();
  const canvas = Object.assign(document.createElement('canvas'), { width: 360, height: 540 });
  const draw = canvas.getContext('2d');
  draw.imageSmoothingQuality = 'high';
  draw.drawImage(image, x, y, width, height, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL('image/jpeg', 0.92);
}, [`${origin}/readme/web-film.jpg`, ...POSTER]);

const host = 'Camille';
const watching = 'Spider-Man: Across the Spider-Verse';
const values = {
  rev: 'r', title: 'Join the Mira party', description: `${host} is watching ${watching}. Join in Mira.`, image: '', url: '',
  state: 'open', host, watching, picture, app: 'mira://party/demo', web: '#/party/demo',
};
let text = readFileSync('src/Mira.Web/party.html', 'utf8');
for (const [name, value] of Object.entries(values)) text = text.replaceAll(`{{${name}}}`, html(value));
await page.route(`${origin}/Mira/party/demo`, route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: text }));
await page.goto(`${origin}/Mira/party/demo`, { waitUntil: 'networkidle' });
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(300);
const scratch = mkdtempSync(join(tmpdir(), 'mira-party-'));
const shot = join(scratch, 'web-party.png');
writeFileSync(shot, await page.screenshot({ type: 'png' }));
await browser.close();

execFileSync('python3', ['-I', '-c', `
import sys
from PIL import Image
image = Image.open(sys.argv[1]).convert("RGB")
for width in (390, 720, 1080):
    copy = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
    copy.save(f"site/img/web-party-{width}.webp", "WEBP", quality=80, method=6)
`, shot], { stdio: 'inherit' });
rmSync(scratch, { recursive: true, force: true });
console.log('site/img/web-party-390.webp, 720 and 1080 written.');
