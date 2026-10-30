// The captures of a release's banner (tools/banner/banner.mjs): the screens of Mira web that this version changed,
// listed in tools/banner/scenes.json, on the library of tools/banner/media.sh (the README's). A phone scene is a whole
// iPhone 17 Pro screen (402 × 874 points, ×3, as tools/gallery/web.mjs); a screen scene, Mira web in a computer's
// window (1440 × 900, ×2). Each scene also gives the box of what changed (focus), lit on the banner; then backdrops
// of a few titles, still-1.jpg to still-N.jpg, among which the banner takes its light.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/banner/capture.mjs <x.y.z> <url of /Mira/> <user> <password> <folder>
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const [version, base, user, password, out] = process.argv.slice(2);
if (!out) { console.error('usage: capture.mjs <x.y.z> <url /Mira/> <user> <password> <folder>'); process.exit(2); }
const scenesOf = JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'scenes.json'), 'utf8'));
const wanted = scenesOf[version];
if (!wanted?.length) { console.error(`tools/banner/scenes.json has no scene for ${version}`); process.exit(1); }
mkdirSync(out, { recursive: true });
const { chromium, devices } = await import(process.env.PLAYWRIGHT ?? 'playwright');
const browser = await chromium.launch();
const errors = [];

async function open(kind) {
  const context = kind === 'phone'
    ? await browser.newContext({
      ...devices['iPhone 15 Pro'], viewport: { width: 402, height: 874 }, screen: { width: 402, height: 874 }, deviceScaleFactor: 3,
      locale: 'fr-FR', reducedMotion: 'reduce',
    })
    : await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, locale: 'fr-FR', reducedMotion: 'reduce' });
  if (kind === 'phone') await context.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
  const page = await context.newPage();
  if (kind === 'phone') await (await context.newCDPSession(page)).send('Emulation.setSafeAreaInsetsOverride', { insets: { top: 62, bottom: 34, left: 0, right: 0 } });
  page.on('pageerror', (e) => errors.push(e.message));
  await page.goto(base); await page.waitForTimeout(1500);
  if (await page.locator('.users').count()) await page.getByRole('button', { name: 'Autre compte' }).click();
  await page.fill('#login-name', user); await page.fill('#login-password', password);
  await page.getByRole('button', { name: 'Se connecter', exact: true }).click();
  await page.waitForFunction(() => location.hash === '#/' || location.hash === '', null, { timeout: 15000 });
  await page.waitForTimeout(3000);
  for (const name of ['Fermer', 'Plus tard', 'Pas maintenant', 'OK']) {
    const button = page.getByRole('button', { name, exact: true });
    if (await button.count() && await button.first().isVisible()) await button.first().click().catch(() => {});
  }
  return { context, page };
}

// Every image on screen drawn, then the screen at rest.
const settle = async (page) => {
  await page.waitForFunction(() => [...document.images].filter((i) => {
    const r = i.getBoundingClientRect(); return r.bottom > 0 && r.top < innerHeight && r.width > 0;
  }).every((i) => i.complete), null, { timeout: 15000 }).catch(() => console.log('::warning::images still loading'));
  await page.waitForTimeout(1500);
};
// The box around these elements, a little wider, kept on screen.
const box = (page, selectors, pad = 10) => page.evaluate(({ selectors, pad }) => {
  const rects = selectors.flatMap((s) => [...document.querySelectorAll(s)]).map((e) => e.getBoundingClientRect()).filter((r) => r.width && r.height);
  if (!rects.length) return null;
  const x = Math.max(0, Math.min(...rects.map((r) => r.left)) - pad), y = Math.max(0, Math.min(...rects.map((r) => r.top)) - pad);
  const right = Math.min(innerWidth, Math.max(...rects.map((r) => r.right)) + pad), bottom = Math.min(innerHeight, Math.max(...rects.map((r) => r.bottom)) + pad);
  return { x, y, width: right - x, height: bottom - y };
}, { selectors, pad });
const api = (page, method, path, body, type = 'application/json') => page.evaluate(async ({ method, path, body, type }) => {
  const s = JSON.parse(localStorage.getItem('mira.session'));
  const r = await fetch(`${location.pathname.replace(/\/Mira\/?.*$/i, '')}/${path}`, {
    method, body, headers: { Authorization: `MediaBrowser Client="banner", Device="banner", DeviceId="banner", Version="1", Token="${s.token}"`, 'Content-Type': type },
  });
  const text = await r.text();
  return text && r.headers.get('content-type')?.includes('json') ? JSON.parse(text) : null;
}, { method, path, body, type });

// A square JPEG cut from a title's image, the way Mira web makes a profile photo (photo.js), sent as one's photo.
async function photo(page, userId, itemId, kind, at) {
  return page.evaluate(async ({ userId, itemId, kind, at }) => {
    const s = JSON.parse(localStorage.getItem('mira.session'));
    const root = location.pathname.replace(/\/Mira\/?.*$/i, '');
    const r = await fetch(`${root}/Items/${itemId}/Images/${kind}?maxWidth=1280`);
    if (!r.ok) return false;
    const bitmap = await createImageBitmap(await r.blob());
    const side = Math.min(bitmap.width, bitmap.height) * at.size, canvas = new OffscreenCanvas(512, 512);
    canvas.getContext('2d').drawImage(bitmap, (bitmap.width - side) * at.x, (bitmap.height - side) * at.y, side, side, 0, 0, 512, 512);
    const bytes = new Uint8Array(await (await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.9 })).arrayBuffer());
    let text = ''; for (const b of bytes) text += String.fromCharCode(b);
    const sent = await fetch(`${root}/Users/${userId}/Images/Primary`, {
      method: 'POST', body: btoa(text), headers: { 'Content-Type': 'image/jpeg', Authorization: `MediaBrowser Client="banner", Device="banner", DeviceId="banner", Version="1", Token="${s.token}"` },
    });
    return sent.ok;
  }, { userId, itemId, kind, at });
}

const titles = async (page) => {
  const session = await page.evaluate(() => JSON.parse(localStorage.getItem('mira.session')));
  const { Items } = await api(page, 'GET', `Items?userId=${session.userId}&recursive=true&includeItemTypes=Movie,Series&fields=Path`);
  return { session, find: (name) => Items.find((x) => (x.Path ?? '').replace(/\\/g, '/').includes(name)) };
};

// Each scene: where to go, what to do, what changed.
const scenes = {
  // Films: the genres in chips, and the film started on top.
  async films(page) {
    await page.locator('.tabbar a[data-tab="films"], .rail a[data-tab="films"]').first().click();
    await page.locator('.genre-chips:not([hidden]) .chip').first().waitFor({ timeout: 10000 }).catch(() => console.log('::warning::no genre chips'));
    await settle(page);
    return box(page, ['.genre-chips:not([hidden])', '.pickup']);
  },
  async 'films-wide'(page) { return scenes.films(page); },
  // Profiles with their photos.
  async profiles(page) {
    const { session, find } = await titles(page);
    const users = await api(page, 'GET', 'Users');
    const faces = [['Arcane (2021)', 'Backdrop', { x: 0.5, y: 0.4, size: 0.8 }], ['Klaus (2019)', 'Backdrop', { x: 0.45, y: 0.4, size: 0.8 }],
      ['Nimona (2023)', 'Backdrop', { x: 0.55, y: 0.4, size: 0.8 }], ['Puss in Boots The Last Wish (2022)', 'Backdrop', { x: 0.6, y: 0.4, size: 0.8 }]];
    const names = ['Léa', 'Sam', 'Noé'];
    for (const name of names) {
      if (users.some((u) => u.Name === name)) continue;
      await api(page, 'POST', 'Users/New', JSON.stringify({ Name: name, Password: crypto.randomUUID() }));
    }
    const all = await api(page, 'GET', 'Users');
    const people = [all.find((u) => u.Id === session.userId), ...names.map((n) => all.find((u) => u.Name === n))].filter(Boolean);
    for (const [i, person] of people.entries()) {
      const [film, kind, at] = faces[i % faces.length], item = find(film);
      if (item && !person.PrimaryImageTag) await photo(page, person.Id, item.Id, kind, at);
    }
    await page.goto(`${base}#/profils`); await settle(page);
    return box(page, ['.profile-item']);
  },
  // A notice: the new card, with its icon, its close button and its time left.
  // A title of the Home's rows put in the favourites from its menu (a right click opens the long press's).
  async notice(page) {
    await settle(page);
    await page.evaluate(() => scrollBy(0, 420)); await settle(page);
    // The first card from the second on whose menu offers it (a favourite's offers to remove it).
    let id = null;
    for (let i = 1; i < 8; i++) {
      const card = page.locator('.row .card').nth(i);
      await card.click({ button: 'right' }); await page.waitForTimeout(500);
      const add = page.getByRole('menuitem', { name: /Ajouter aux favoris/ }).first();
      if (await add.isVisible()) {
        id = await card.evaluate((c) => (c.getAttribute('href') ?? c.querySelector('a')?.getAttribute('href'))?.match(/titre\/([^/?]+)/)?.[1] ?? null).catch(() => null);
        await add.click(); break;
      }
      await page.keyboard.press('Escape'); await page.waitForTimeout(400);
    }
    await page.locator('.toast').waitFor({ timeout: 5000 });
    await page.locator('.toast').hover().catch(() => {});
    await page.waitForTimeout(700);
    const focus = await box(page, ['.toast']);
    if (id) after.push(() => api(page, 'DELETE', `UserFavoriteItems/${id}`).catch(() => {}));
    return focus;
  },
  // The Home's banner and its pills.
  async home(page) { await settle(page); return box(page, ['.hero .dots'], 40); },
};

// Jellyfin lists a genre (Items/Filters2, the Films page's chips) only once a library scan has gone past the
// metadata TMDb gave: one more scan, then wait for the genres.
{
  const server = new URL('..', base).href;
  const header = (token) => ({ 'Content-Type': 'application/json', Authorization: `MediaBrowser Client="banner", Device="banner", DeviceId="banner-setup", Version="1"${token ? `, Token="${token}"` : ''}` });
  const signed = await (await fetch(`${server}Users/AuthenticateByName`, { method: 'POST', headers: header(), body: JSON.stringify({ Username: user, Pw: password }) })).json();
  await fetch(`${server}Library/Refresh`, { method: 'POST', headers: header(signed.AccessToken) });
  let genres = 0;
  for (const limit = Date.now() + 180_000; Date.now() < limit && !genres; await new Promise((r) => setTimeout(r, 3000))) {
    const filters = await (await fetch(`${server}Items/Filters2?userId=${signed.User.Id}&includeItemTypes=Movie&recursive=true`, { headers: header(signed.AccessToken) })).json();
    genres = filters.Genres?.length ?? 0;
  }
  console.log(genres ? `${genres} genres` : '::warning::no genre listed by Jellyfin');
}
const listed = [], after = [];
try {
  for (const name of wanted) {
    const kind = name.endsWith('-wide') ? 'screen' : 'phone';
    const scene = scenes[name];
    if (!scene) throw new Error(`unknown scene: ${name}`);
    const { context, page } = await open(kind);
    try {
      const focus = await scene(page);
      if (!focus) console.log(`::warning::${name}: nothing lit`);
      // The notice is already on screen: no wait that would let it go.
      if (name !== 'notice') await settle(page);
      await page.screenshot({ path: join(out, `${name}.png`) });
      for (const undo of after.splice(0)) await undo();
      listed.push({ file: `${name}.png`, kind, focus: focus ?? undefined, captureWidth: kind === 'phone' ? 402 : 1440 });
      console.log(`${name}.png`);
    } finally { await context.close(); }
  }
  // Backdrops for the banner's light: the version picks one.
  const { context, page } = await open('phone');
  try {
    const { find } = await titles(page);
    let n = 0;
    // Not Spirited Away: its first backdrop on TMDb is a photo of Hayao Miyazaki.
    for (const name of ['Arcane (2021)', 'Spider-Man Across the Spider-Verse (2023)', 'Klaus (2019)', 'Coco (2017)', 'The Wild Robot (2024)',
      'Puss in Boots The Last Wish (2022)', 'Nimona (2023)', 'Your Name. (2016)']) {
      const title = find(name);
      const still = title && await page.request.get(new URL(`../Items/${title.Id}/Images/Backdrop?maxWidth=2400&quality=90`, base).href);
      if (still?.ok()) writeFileSync(join(out, `still-${++n}.jpg`), await still.body());
      else console.log(`::warning::no backdrop for ${name}`);
    }
  } finally { await context.close(); }
  writeFileSync(join(out, 'banner.json'), `${JSON.stringify(listed, null, 2)}\n`);
  if (errors.length) console.log(`::warning::page errors: ${errors.join(' | ')}`);
} finally { await browser.close(); }
