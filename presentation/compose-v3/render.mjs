// node render.mjs stills <t,t,...> <dir>   |   node render.mjs video <fps> <out.mp4> <from> <to>
import { chromium } from '/opt/node22/lib/node_modules/playwright/index.mjs';
import { spawn } from 'node:child_process';
import { writeFileSync, readFileSync } from 'node:fs';
const [mode, a, b] = process.argv.slice(2);
const browser = await chromium.launch({ args: ['--js-flags=--max-old-space-size=6000'] });
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
page.on('pageerror', (e) => console.log('ERR', e.message)); page.on('console', (m) => console.log('console', m.text()));
const types = { html: 'text/html', js: 'text/javascript', json: 'application/json', jpg: 'image/jpeg', png: 'image/png', svg: 'image/svg+xml', woff2: 'font/woff2' };
await page.route('http://compose.local/**', (route) => {
  const p = decodeURIComponent(new URL(route.request().url()).pathname);
  try { route.fulfill({ body: readFileSync('/tmp/claude-0' + p), contentType: types[p.split('.').pop()] ?? 'application/octet-stream' }); }
  catch { route.fulfill({ status: 404, body: '' }); }
});
await page.goto('http://compose.local/v3/index.html');
await page.evaluate(() => window.ready);
if (mode === 'stills') {
  for (const t of a.split(',').map(Number)) { const d = await page.evaluate((t) => window.frame(t), t); writeFileSync(`${b}/s-${t}.jpg`, Buffer.from(d, 'base64')); }
} else {
  const fps = Number(a), from = Number(process.argv[5] ?? 0), to = Number(process.argv[6] ?? 60);
  const ff = spawn('ffmpeg', ['-v', 'error', '-y', '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'mjpeg', '-i', '-', '-c:v', 'libx264', '-preset', 'medium', '-crf', '12', '-pix_fmt', 'yuv420p', '-r', String(fps), b], { stdio: ['pipe', 'inherit', 'inherit'] });
  const n = Math.round((to - from) * fps); const t0 = Date.now();
  for (let i = 0; i < n; i++) {
    const d = await page.evaluate((t) => window.frame(t, 0.96), from + i / fps);
    if (!ff.stdin.write(Buffer.from(d, 'base64'))) await new Promise((r) => ff.stdin.once('drain', r));
    if (i % 300 === 0) console.log(`${i}/${n} ${((Date.now() - t0) / 1000).toFixed(0)}s`);
  }
  ff.stdin.end(); await new Promise((r) => ff.on('close', r));
}
await browser.close();
