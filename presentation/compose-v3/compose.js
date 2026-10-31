// Mira presentation, take 3: Mira for Windows and the iPhone app, from pictures of the real apps (pages drawn whole at
// twice their size, screens after each slow drag), scrolled and zoomed here at 60 frames a second, cut on Nujabes'
// "Counting Stars" (30.39 s → 111.24 s of the song: 95 BPM, the beat comes in at 10.1 s of the film).
const W = 1920, H = 1080, BASE = 'http://compose.local';
const cv = document.getElementById('c');
let ctx = cv.getContext('2d');
const clamp = (v, a = 0, b = 1) => Math.max(a, Math.min(b, v));
const lerp = (a, b, k) => a + (b - a) * k;
const seg = (t, a, b) => clamp((t - a) / (b - a));
const E = {
  lin: (k) => k,
  out: (k) => 1 - Math.pow(1 - k, 3),
  out5: (k) => 1 - Math.pow(1 - k, 5),
  inOut: (k) => (k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2),
  inOut5: (k) => (k < 0.5 ? 16 * k ** 5 : 1 - Math.pow(-2 * k + 2, 5) / 2),
  sine: (k) => 0.5 - Math.cos(Math.PI * k) / 2,
  in: (k) => k * k * k,
  back: (k) => { const c1 = 1.5, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); },
  // A spring that settles: overshoot then rest.
  spring: (k) => (k >= 1 ? 1 : 1 - Math.exp(-6 * k) * Math.cos(10 * k)),
};
const BEAT = 0.6316, BAR = 4 * BEAT;
const q = (n) => n * BEAT;          // a beat
const bar = (n) => n * BAR;         // a bar

// ---------- pictures ----------
const cache = new Map();
function bitmap(url) {
  let p = cache.get(url);
  if (p) { cache.delete(url); cache.set(url, p); return p; }
  p = url.endsWith('.svg')
    ? new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = () => rej(new Error(url)); i.src = url; })
    : fetch(url).then((r) => { if (!r.ok) throw new Error('missing ' + url); return r.blob(); }).then((b) => createImageBitmap(b));
  cache.set(url, p);
  while (cache.size > 90) { const [k, v] = cache.entries().next().value; cache.delete(k); v.then((b) => b.close?.(), () => {}); }
  return p;
}
const pic = (path) => bitmap(`${BASE}/v3/${path}`);

let ITEMS = [];
const art = (name, kind = 'Backdrop') => {
  const it = ITEMS.find((x) => x.Name === name && x.Type !== 'Episode');
  const f = kind === 'Backdrop' ? it?.images.Backdrop?.[0] : it?.images[kind];
  return f ? `${BASE}/c2img/${f}` : null;
};

function rr(x, y, w, h, r) { ctx.beginPath(); ctx.roundRect(x, y, w, h, r); }
function fill(c, a = 1) { if (a <= 0) return; ctx.save(); ctx.globalAlpha = a; ctx.fillStyle = c; ctx.fillRect(0, 0, W, H); ctx.restore(); }

// ---------- backgrounds ----------
const soft = new Map();
async function softOf(url) {
  if (!soft.has(url)) {
    const i = await bitmap(url); const c = document.createElement('canvas'); c.width = 96; c.height = 54;
    const x = c.getContext('2d'); x.filter = 'blur(3px) saturate(1.5)'; x.drawImage(i, -6, -6, 108, 66); soft.set(url, c);
  }
  return soft.get(url);
}
// A title's colours behind everything, very soft, darkened at the edges.
async function glow(name, alpha, drift = 0) {
  if (alpha <= 0 || !art(name)) return;
  const s = await softOf(art(name));
  ctx.save(); ctx.globalAlpha = alpha; ctx.imageSmoothingQuality = 'high';
  const z = 1.25 + drift * 0.08;
  ctx.drawImage(s, (W - W * z) / 2 + Math.sin(drift * 2) * 60, (H - H * z) / 2, W * z, H * z);
  const g = ctx.createRadialGradient(W / 2, H / 2, 120, W / 2, H / 2, 1250);
  g.addColorStop(0, 'rgba(7,7,8,0.35)'); g.addColorStop(1, 'rgba(7,7,8,0.93)');
  ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
  ctx.restore();
}
// The library's posters on a plane tilted away, seen in perspective, drifting.
const WALL = ['Arcane', 'Spirited Away', 'Spider-Man: Across the Spider-Verse', 'Klaus', 'Your Name.', 'The Wild Robot', 'Flow', 'Princess Mononoke',
  'Wolfwalkers', 'Coco', 'Suzume', 'Nimona', 'Robot Dreams', 'BLUE EYE SAMURAI', 'INVINCIBLE', 'The Boy and the Heron', 'My Neighbor Totoro',
  "Howl's Moving Castle", 'Puss in Boots: The Last Wish', 'Isle of Dogs', 'Spring', 'The Mitchells vs. the Machines', 'Love, Death & Robots',
  'Spider-Man: Into the Spider-Verse', 'Teenage Mutant Ninja Turtles: Mutant Mayhem', 'Sprite Fright'];
async function wall(t, alpha, { zoom = 1, tilt = 0.55, turn = -0.25, pan = 0, dim = 0.35 } = {}) {
  if (alpha <= 0) return;
  const ims = await Promise.all(WALL.map((n) => bitmap(art(n, 'Primary'))));
  const pw = 230, ph = 345, gap = 26, D = 1500;
  const ct = Math.cos(tilt), st = Math.sin(tilt), cy = Math.cos(turn), sy = Math.sin(turn);
  const list = [];
  for (let col = -9; col <= 9; col++) {
    const speed = (col % 2 ? -1 : 1) * 22;
    const span = (ph + gap) * 8;
    const off = (((t * speed + pan) + col * 131) % span + span) % span;
    for (let row = -6; row <= 6; row++) {
      const i = ((col * 7 + row * 3) % ims.length + ims.length) % ims.length;
      const X = col * (pw + gap), Y = row * (ph + gap) + off - span / 2;
      // plane rotated: around x by tilt, around y by turn
      const y1 = Y * ct, z1 = Y * st;
      const x2 = X * cy + z1 * sy, z2 = -X * sy + z1 * cy;
      const z = z2 / zoom + D * (1 / zoom - 1) * 0.0;
      const p = D / (D + z2) * zoom;
      if (p <= 0 || p > 6) continue;
      list.push({ i, x: W / 2 + x2 * p, y: H / 2 + y1 * p, p, z: z2 });
    }
  }
  list.sort((a, b) => b.z - a.z);
  ctx.save(); ctx.globalAlpha = alpha; ctx.imageSmoothingQuality = 'high';
  for (const it of list) {
    const w = pw * it.p, h = ph * it.p;
    if (it.x + w < 0 || it.x - w > W || it.y + h < 0 || it.y - h > H) continue;
    ctx.save(); rr(it.x - w / 2, it.y - h / 2, w, h, 12 * it.p); ctx.clip(); ctx.drawImage(ims[it.i], it.x - w / 2, it.y - h / 2, w, h);
    const fog = clamp(0.15 + it.z / 2600) * dim * 2; if (fog > 0) { ctx.fillStyle = `rgba(7,7,8,${fog})`; ctx.fill(); }
    ctx.restore();
  }
  ctx.restore();
}

// ---------- type ----------
const font = (w, s) => `${w} ${s}px N`;
// Words rising out of a mask one after the other.
function words(text, x, y, size, t, tin, tout, { weight = 800, color = '#f5f5f7', align = 'left', stagger = 0.07, rise = 0.55, shadow = 0, track = 0, ease = E.out5 } = {}) {
  if (t < tin || t > tout + 0.5) return;
  ctx.save(); ctx.font = font(weight, size); ctx.textBaseline = 'alphabetic'; ctx.letterSpacing = `${track}px`;
  if (shadow) { ctx.shadowColor = 'rgba(0,0,0,0.65)'; ctx.shadowBlur = shadow; }
  const parts = text.split(' '); const space = ctx.measureText(' ').width;
  const widths = parts.map((p) => ctx.measureText(p).width);
  const total = widths.reduce((a, b) => a + b, 0) + space * (parts.length - 1);
  let cx = align === 'center' ? x - total / 2 : align === 'right' ? x - total : x;
  const out = E.in(seg(t, tout, tout + 0.4));
  parts.forEach((p, i) => {
    const k = ease(seg(t, tin + i * stagger, tin + i * stagger + rise));
    ctx.save();
    ctx.beginPath(); ctx.rect(cx - 30, y - size * 1.25, widths[i] + 60, size * 1.62); ctx.clip();
    ctx.globalAlpha = Math.min(1, k * 1.5) * (1 - out); ctx.fillStyle = color;
    ctx.fillText(p, cx, y + (1 - k) * size * 1.15 - out * size * 0.6);
    ctx.restore();
    cx += widths[i] + space;
  });
  ctx.restore();
}
function line(text, x, y, size, t, tin, tout, { weight = 600, color = '#b4b4ba', align = 'left', track = 0 } = {}) {
  if (t < tin || t > tout + 0.4) return;
  const k = E.out(seg(t, tin, tin + 0.6)), out = E.in(seg(t, tout, tout + 0.4));
  ctx.save(); ctx.font = font(weight, size); ctx.textAlign = align; ctx.fillStyle = color; ctx.letterSpacing = `${track}px`; ctx.globalAlpha = k * (1 - out);
  ctx.fillText(text, x, y + (1 - k) * 18); ctx.restore();
}
// A kicker over one or two big lines.
function title(kicker, lines, x, y, t, tin, tout, { align = 'left', size = 76, sub = null, gap = 1.08 } = {}) {
  line(kicker.toUpperCase(), x, y - size * 1.05, 22, t, tin, tout, { weight: 800, color: '#d9c7a1', align, track: 3 });
  lines.forEach((l, i) => words(l, x, y + i * size * gap, size, t, tin + 0.08 + i * 0.12, tout, { align }));
  if (sub) line(sub, x, y + (lines.length - 1) * size * gap + size * 0.85, 30, t, tin + 0.35, tout, { align, color: '#a1a1a8' });
}
// A dark plate under a caption on a picture.
function plate(x, y, w, h, a) {
  if (a <= 0) return;
  ctx.save(); const g = ctx.createRadialGradient(x + w / 2, y + h / 2, 10, x + w / 2, y + h / 2, Math.max(w, h) * 0.75);
  g.addColorStop(0, `rgba(7,7,8,${0.86 * a})`); g.addColorStop(0.55, `rgba(7,7,8,${0.6 * a})`); g.addColorStop(1, 'rgba(7,7,8,0)');
  ctx.fillStyle = g; ctx.fillRect(x - w * 0.4, y - h, w * 1.8, h * 3); ctx.restore();
}

// ---------- the mark ----------
async function mark(cx, cy, size, alpha = 1, glowing = 0.3) {
  if (alpha <= 0) return;
  const m = await bitmap(`${BASE}/art/mark.svg`).catch(() => null);
  if (!m) return;
  ctx.save(); ctx.globalAlpha = alpha;
  ctx.shadowColor = `rgba(255,255,255,${glowing})`; ctx.shadowBlur = size * 0.5;
  ctx.drawImage(m, cx - size / 2, cy - size / 2, size, size);
  ctx.restore();
}
async function lockup(cx, cy, size, t, tin, alpha = 1) {
  const m = E.back(seg(t, tin, tin + 0.6));
  ctx.save(); ctx.font = font(800, size); const tw = ctx.measureText('Mira').width; ctx.restore();
  const ms = size, gap = size * 0.24, total = ms + gap + tw, x0 = cx - total / 2;
  await mark(x0 + ms / 2, cy - size * 0.04, ms * (0.55 + 0.45 * m), clamp(m * 1.4) * alpha);
  const k = E.out5(seg(t, tin + 0.15, tin + 0.85));
  ctx.save(); ctx.globalAlpha = k * alpha; ctx.fillStyle = '#f5f5f7'; ctx.textBaseline = 'middle'; ctx.font = font(800, size);
  ctx.beginPath(); ctx.rect(x0 + ms + gap * 0.5, cy - size, tw + gap * 2, size * 2); ctx.clip();
  ctx.fillText('Mira', x0 + ms + gap - (1 - k) * size * 0.6, cy); ctx.restore();
}

// ---------- Mira for Windows ----------
// shots.json of --demo-shots: each page's content drawn whole at twice its size, its chrome, its hovered cards.
let WIN = null;
const P = (name) => WIN.pages[name];
const cardRect = (page, name) => (P(page).cards.find((c) => c.name === name) ?? P(page).cards.find((c) => c.name.includes(name)))?.rect;
const hoverAt = (page, name) => P(page).hovers.findIndex((h) => h.name === name || h.name.includes(name));
const scrollFor = (page, r, at = 0.5) => clamp(r.y + r.h / 2 - 1080 * at, 0, Math.max(0, P(page).height - 1080));
const center = (r) => ({ x: r.x + r.w / 2, y: r.y + r.h / 2 });

// One page of the window in units (1920 × 1080): content scrolled, banners, hovered cards, typed search, chrome.
async function pageLayer(st, { chrome = 1 } = {}) {
  const p = P(st.page); const sy = st.scroll ?? 0;
  const content = await pic(`shots/win/${st.contentFile ?? p.content}`);
  const sh = Math.min(2160, content.height - sy * 2);
  ctx.fillStyle = '#070708'; ctx.fillRect(0, 0, 1920, 1080);
  ctx.imageSmoothingQuality = 'high';
  if (sh > 0) ctx.drawImage(content, 0, sy * 2, content.width, sh, 0, 0, content.width / 2, sh / 2);
  for (const [i, a] of st.banners ?? []) {
    if (a <= 0 || !p.banners?.[i]) continue;
    const b = p.banner, im = await pic(`shots/win/${p.banners[i].file}`);
    ctx.save(); ctx.globalAlpha *= a; ctx.drawImage(im, b.x, b.y - sy, im.width / 2, im.height / 2); ctx.restore();
  }
  for (const [i, a] of st.hovers ?? []) {
    const h = p.hovers[i]; if (!h || a <= 0) continue;
    if (shotScrolled(p, h)) { hoverMark(h, sy, a); continue; }
    const im = await pic(`shots/win/${h.file}`);
    ctx.save(); ctx.globalAlpha *= a; ctx.drawImage(im, h.rect.x, h.rect.y - sy, im.width / 2, im.height / 2); ctx.restore();
  }
  if (st.typing && st.typing[1] > 0) {
    const s = P('search'), f = s.field; const n = st.typing[0];
    if (n > 0) { const im = await pic(`shots/win/${s.typing[n - 1].file}`); ctx.save(); ctx.globalAlpha *= st.typing[1]; ctx.drawImage(im, f.x, f.y - sy, im.width / 2, im.height / 2); ctx.restore(); }
  }
  if (chrome > 0) { const c = await pic(`shots/win/${p.chrome}`); ctx.save(); ctx.globalAlpha *= chrome; ctx.drawImage(c, 0, 0, 1920, 1080); ctx.restore(); }
}
// A hovered card shot while its page was scrolled to it came out of the wrong place (the brush followed the scroll):
// those are lit here as Mira lights them, a ring on the picture, a button a shade darker, a row a shade lighter.
const shotScrolled = (p, h) => clamp(h.rect.y + h.rect.h / 2 - 540, 0, Math.max(0, p.height - 1080)) > 1;
function hoverMark(h, sy, a) {
  const r = { x: h.rect.x + 12, y: h.rect.y + 12 - sy, w: h.rect.w - 24, h: h.rect.h - 24 };
  ctx.save(); ctx.globalAlpha *= a;
  if (/^Resume/.test(h.name)) { rr(r.x, r.y, r.w, r.h, 8); ctx.fillStyle = 'rgba(0,0,0,0.13)'; ctx.fill(); }
  else if (r.w > 1000) { rr(r.x, r.y, r.w, r.h, 10); ctx.fillStyle = 'rgba(255,255,255,0.05)'; ctx.fill(); ctx.strokeStyle = 'rgba(255,255,255,0.12)'; ctx.lineWidth = 1.2; ctx.stroke(); }
  else {
    const ah = r.w > r.h ? r.w * 9 / 16 : r.w * 1.478;
    rr(r.x + 1, r.y + 1, r.w - 2, ah - 2, 9); ctx.fillStyle = 'rgba(255,255,255,0.035)'; ctx.fill();
    ctx.strokeStyle = 'rgba(245,245,247,0.92)'; ctx.lineWidth = 2.6; ctx.stroke();
  }
  ctx.restore();
}
// The player in units: Spring's frame letterboxed, the controls over it.
const FILM = { a: { start: 42, n: 432 }, b: { start: 376, n: 624 } };
async function filmFrame(ft) {
  const seg_ = ft >= FILM.b.start ? 'b' : 'a', f = FILM[seg_];
  const i = clamp(Math.round((ft - f.start) * 24), 0, f.n - 1);
  return pic(`spring/${seg_}/${String(i).padStart(4, '0')}.jpg`);
}
async function playerLayer(st) {
  ctx.fillStyle = '#000'; ctx.fillRect(0, 0, 1920, 1080);
  const fr = await filmFrame(st.ft); ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(fr, 0, 138, 1920, 804);
  if (st.overlay && st.overlayA > 0) { const o = await pic(`shots/win/${st.overlay}`); ctx.save(); ctx.globalAlpha *= st.overlayA; ctx.drawImage(o, 0, 0, 1920, 1080); ctx.restore(); }
}
async function layer(st) { if (st.player) await playerLayer(st.player); else await pageLayer(st); }

// The window: units drawn at scale s, unit point (fx, fy) at canvas (x, y), turned by rot; st is a page state, or a
// transition {from, to, k, kind}.
async function win(v, st) {
  const { s = 1, fx = 960, fy = 540, x = 960, y = 540, rot = 0, alpha = 1, radius = 12, shadow = 1 } = v;
  if (alpha <= 0) return;
  ctx.save(); ctx.globalAlpha = alpha;
  ctx.translate(x, y); ctx.rotate(rot); ctx.scale(s, s); ctx.translate(-fx, -fy);
  if (shadow > 0) {
    ctx.save(); ctx.shadowColor = `rgba(0,0,0,${0.75 * shadow})`; ctx.shadowBlur = 120; ctx.shadowOffsetY = 40;
    rr(0, 0, 1920, 1080, radius); ctx.fillStyle = '#000'; ctx.fill(); ctx.restore();
  }
  ctx.save(); rr(0, 0, 1920, 1080, radius); ctx.clip();
  if (st.kind) await mix(st); else await layer(st);
  ctx.restore();
  if (shadow > 0) { rr(0.5, 0.5, 1919, 1079, radius); ctx.strokeStyle = `rgba(255,255,255,${0.16 * shadow})`; ctx.lineWidth = 1.5 / s; ctx.stroke(); }
  ctx.restore();
  const cur = st.kind ? (st.k < 0.5 ? st.from.cursor : st.to.cursor) ?? st.cursor : st.cursor;
  if (cur && cur.a > 0) {
    const cx = x + ((cur.x - fx) * Math.cos(rot) - (cur.y - fy) * Math.sin(rot)) * s, cy = y + ((cur.x - fx) * Math.sin(rot) + (cur.y - fy) * Math.cos(rot)) * s;
    pointer(cx, cy, 1.15 * clamp(s, 0.75, 1.5), cur.a * alpha, cur.press ?? 0, cur.ripple ?? -1);
  }
}
// Two states of the window: the new page rising in (as Mira does), growing from a card, or sliding.
async function mix({ from, to, k, kind, rect }) {
  if (kind === 'rise') {
    await layer({ ...from });
    ctx.save(); ctx.globalAlpha *= E.out(k); ctx.translate(0, (1 - E.out5(k)) * 26); await pageLayer(to, { chrome: 0 }); ctx.restore();
    const c = await pic(`shots/win/${P(to.page).chrome}`); ctx.save(); ctx.globalAlpha *= clamp(k * 2); ctx.drawImage(c, 0, 0, 1920, 1080); ctx.restore();
  } else if (kind === 'grow') {
    await layer(from);
    ctx.fillStyle = `rgba(7,7,8,${0.6 * E.out(k)})`; ctx.fillRect(0, 0, 1920, 1080);
    const e = E.inOut5(k), r = rect;
    const sx = lerp(r.w / 1920, 1, e), sy = lerp(r.h / 1080, 1, e);
    const s = Math.max(sx, sy);
    const cx = lerp(r.x + r.w / 2, 960, e), cy = lerp(r.y + r.h / 2, 540, e);
    ctx.save(); ctx.globalAlpha *= clamp(k * 3);
    rr(cx - 960 * sx, cy - 540 * sy, 1920 * sx, 1080 * sy, lerp(10, 0, e)); ctx.clip();
    ctx.translate(cx, cy); ctx.scale(s, s); ctx.translate(-960, -540);
    await layer(to); ctx.restore();
  } else if (kind === 'film') {
    await layer(from); ctx.fillStyle = `rgba(0,0,0,${E.out(clamp(k * 1.6))})`; ctx.fillRect(0, 0, 1920, 1080);
    const e = E.out5(seg(k, 0.25, 1)); ctx.save(); ctx.globalAlpha *= e;
    ctx.translate(960, 540); ctx.scale(lerp(1.12, 1, e), lerp(1.12, 1, e)); ctx.translate(-960, -540); await layer(to); ctx.restore();
  } else if (kind === 'fade') {
    await layer(from); ctx.save(); ctx.globalAlpha *= k; await layer(to); ctx.restore();
  }
}
// The Windows pointer: an arrow, pressed smaller, a ring on a click.
function pointer(x, y, s, a, press = 0, ripple = -1) {
  ctx.save(); ctx.globalAlpha = a;
  if (ripple >= 0 && ripple < 1) {
    ctx.save(); ctx.globalAlpha = a * (1 - ripple) * 0.85; ctx.strokeStyle = '#ffffff'; ctx.lineWidth = 3 * s;
    ctx.beginPath(); ctx.arc(x, y, (10 + 46 * E.out(ripple)) * s, 0, Math.PI * 2); ctx.stroke(); ctx.restore();
  }
  ctx.translate(x, y); const k = s * (1 - 0.12 * press); ctx.scale(k, k);
  ctx.shadowColor = 'rgba(0,0,0,0.45)'; ctx.shadowBlur = 6; ctx.shadowOffsetY = 2;
  ctx.beginPath(); ctx.moveTo(0, 0); ctx.lineTo(0, 23); ctx.lineTo(5.6, 17.6); ctx.lineTo(9.6, 26.4); ctx.lineTo(13.2, 24.8); ctx.lineTo(9.4, 16.4); ctx.lineTo(17, 16.4); ctx.closePath();
  ctx.fillStyle = '#ffffff'; ctx.fill(); ctx.shadowColor = 'transparent'; ctx.lineWidth = 1.4; ctx.strokeStyle = '#0a0a0a'; ctx.stroke();
  ctx.restore();
}
// A path for the pointer through timed points [t, x, y] (units), eased between them; presses at given times.
function path(t, pts, clicks = []) {
  let x = pts[0][1], y = pts[0][2];
  for (let i = 0; i < pts.length - 1; i++) {
    const [t0, x0, y0] = pts[i], [t1, x1, y1] = pts[i + 1];
    if (t >= t0 && t <= t1) { const k = E.inOut(seg(t, t0, t1)); x = lerp(x0, x1, k); y = lerp(y0, y1, k) - Math.sin(Math.PI * k) * Math.hypot(x1 - x0, y1 - y0) * 0.06; break; }
    if (t > t1) { x = x1; y = y1; }
  }
  let press = 0, ripple = -1;
  for (const c of clicks) { if (t >= c - 0.12 && t < c + 0.12) press = 1 - Math.abs(t - c) / 0.12; if (t >= c && t < c + 0.55) ripple = (t - c) / 0.55; }
  return { x, y, press, ripple, a: 1 };
}
// A camera between framings, eased.
function cam(t, a, b, from, to, ease = E.inOut) {
  const k = ease(seg(t, a, b)); const o = {};
  for (const key of new Set([...Object.keys(from), ...Object.keys(to)])) o[key] = lerp(from[key] ?? to[key], to[key] ?? from[key], k);
  return o;
}
// Keyframed values: [[t, v], ...] eased between neighbours.
function keys(t, list, ease = E.inOut) {
  if (t <= list[0][0]) return list[0][1];
  for (let i = 0; i < list.length - 1; i++) { const [t0, v0] = list[i], [t1, v1] = list[i + 1]; if (t <= t1) return lerp(v0, v1, ease(seg(t, t0, t1))); }
  return list[list.length - 1][1];
}

// ---------- the film ----------
const DURATION = 80.85;
const SECTIONS = [];
const section = (a, b, fn) => SECTIONS.push({ a, b, fn });

// 0. Intro (bars 0-4): the posters drift in depth, the words come on the beats, Mira, then the dive into the drop.
section(0, bar(4) + 0.05, async (t) => {
  const dive = E.in(seg(t, bar(3.55), bar(4)));
  const a = E.out(seg(t, 0, 1.4));
  await wall(t, a, { zoom: 0.92 + t * 0.012 + dive * 2.6, tilt: 0.62 - t * 0.012 - dive * 0.3, turn: -0.28 + t * 0.01, pan: dive * 900, dim: 0.5 });
  const g = ctx.createRadialGradient(W / 2, H / 2, 80, W / 2, H / 2, 1150);
  const veil = lerp(0.68, 0.25, dive);
  g.addColorStop(0, `rgba(7,7,8,${veil})`); g.addColorStop(1, `rgba(7,7,8,${0.95 - dive * 0.4})`); ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
  // the words, one phrase per half bar
  const phrases = [['Your films.', q(4), q(6.35)], ['Your shows.', q(7), q(8.4)], ['Your anime.', q(9), q(10.4)]];
  for (const [p, tin, tout] of phrases) words(p, W / 2, 590, 132, t, tin, tout, { align: 'center', stagger: 0.16, rise: 0.5, shadow: 50 });
  const out = 1 - E.in(seg(t, bar(3.6), bar(3.92)));
  ctx.save(); ctx.globalAlpha = out;
  await lockup(W / 2, 500, 150, t, q(11));
  ctx.restore();
  words('Your Jellyfin, like a cinema.', W / 2, 680, 50, t, q(12.5), bar(3.6), { align: 'center', weight: 700, color: '#d4d4d8', stagger: 0.05 });
  fill('#ffffff', E.in(seg(t, bar(3.8), bar(4))) * 0.9);
});

// 7. End card (bars 28-32): the wall again, Mira, where to find it.
section(bar(28) - 0.6, DURATION, async (t) => {
  const t0 = bar(28);
  const a = E.out(seg(t, t0 - 0.6, t0 + 0.4)), fin = E.in(seg(t, DURATION - 1.6, DURATION - 0.1));
  await wall(t, a * 0.75 * (1 - fin), { zoom: 1.25 - (t - t0) * 0.012, tilt: 0.5, turn: 0.22 - (t - t0) * 0.008, dim: 0.6 });
  const g = ctx.createRadialGradient(W / 2, H / 2, 100, W / 2, H / 2, 1100);
  g.addColorStop(0, `rgba(7,7,8,${0.74 * a})`); g.addColorStop(1, `rgba(7,7,8,${0.96 * a})`); ctx.fillStyle = g; ctx.fillRect(0, 0, W, H);
  ctx.save(); ctx.globalAlpha = 1 - fin;
  await lockup(W / 2, 360, 128, t, t0 + 0.05);
  words('miracinema.app', W / 2, 575, 84, t, t0 + q(2), 999, { align: 'center', stagger: 0.04 });
  line('Free and open source  ·  Windows  ·  Mac  ·  iPhone  ·  any browser', W / 2, 655, 32, t, t0 + q(3), 999, { align: 'center', weight: 700, color: '#c8c8cc' });
  line('Join us on Discord  ·  discord.gg/Bh3F6PDUvB', W / 2, 745, 38, t, t0 + q(4), 999, { align: 'center', weight: 800, color: '#d9c7a1' });
  line('Real Mira for Windows and Mira for iPhone, on a Jellyfin 12.1 demo server. Spring © Blender Foundation, CC BY 4.0. Posters and artwork © their owners, via TMDb. Music: Nujabes, “Counting Stars”.', W / 2, 1035, 17, t, t0 + q(5), 999, { align: 'center', weight: 600, color: '#7d7d84' });
  ctx.restore();
  fill('#000', fin);
});

// ---------- frames ----------
// Fast moves get several moments averaged in one frame (a camera's shutter); still ones one.
const BLUR = [];      // [from, to, samples]
const blurAt = (t) => BLUR.reduce((n, [a, b, s]) => (t >= a && t < b ? Math.max(n, s) : n), 1);
async function render(t) {
  ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.globalAlpha = 1; ctx.filter = 'none';
  ctx.fillStyle = '#070708'; ctx.fillRect(0, 0, W, H);
  for (const s of SECTIONS) if (t >= s.a && t < s.b) { ctx.save(); await s.fn(t); ctx.restore(); }
}
const acc = document.createElement('canvas'); acc.width = W; acc.height = H; const actx = acc.getContext('2d');
window.frame = async (t, quality = 0.93) => {
  const n = blurAt(t);
  if (n <= 1) await render(t);
  else {
    const shutter = 0.75 / 60;
    for (let i = 0; i < n; i++) {
      await render(t + (i / (n - 1) - 0.5) * shutter);
      actx.globalAlpha = 1 / (i + 1); actx.drawImage(cv, 0, 0);
    }
    ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.globalAlpha = 1; ctx.drawImage(acc, 0, 0);
  }
  return cv.toDataURL('image/jpeg', quality).slice(23);
};
window.ready = (async () => {
  await Promise.all(['800', '700', '600'].map((w) => document.fonts.load(`${w} 40px N`)));
  ITEMS = await (await fetch(`${BASE}/v3/items.json`)).json();
  try { WIN = await (await fetch(`${BASE}/v3/shots/win/shots.json`)).json(); } catch { }
  try { IP = await (await fetch(`${BASE}/v3/shots/ip/pages.json`)).json(); } catch { }
  return true;
})();
let IP = null;

// ---------- the iPhone ----------
// A phone (screen 402 × 874 points) at canvas (cx, cy), s canvas pixels per point, turned by rot; screen() draws in
// points, (0, 0) at the screen's top left, upright or sideways (land: the screen is 874 × 402).
async function phone(v, screen) {
  const { cx, cy, s, rot = 0, alpha = 1, land = false, island = true } = v;
  if (alpha <= 0) return;
  const sw = 402, sh = 874, bez = 6.5, rim = 3.6, R = 56;
  ctx.save(); ctx.globalAlpha = alpha; ctx.translate(cx, cy); ctx.rotate(rot); ctx.scale(s, s);
  ctx.save(); ctx.shadowColor = 'rgba(0,0,0,0.8)'; ctx.shadowBlur = 90 * s; ctx.shadowOffsetY = 36 * s;
  rr(-sw / 2 - bez - rim, -sh / 2 - bez - rim, sw + 2 * (bez + rim), sh + 2 * (bez + rim), R + bez + rim);
  const g = ctx.createLinearGradient(-sw / 2, -sh / 2, sw / 2, sh / 2);
  g.addColorStop(0, '#7a7a80'); g.addColorStop(0.3, '#303034'); g.addColorStop(0.65, '#4a4a50'); g.addColorStop(1, '#1d1d20');
  ctx.fillStyle = g; ctx.fill(); ctx.restore();
  rr(-sw / 2 - bez, -sh / 2 - bez, sw + 2 * bez, sh + 2 * bez, R + bez); ctx.fillStyle = '#030304'; ctx.fill();
  ctx.save(); rr(-sw / 2, -sh / 2, sw, sh, R); ctx.clip(); ctx.fillStyle = '#000'; ctx.fillRect(-sw / 2, -sh / 2, sw, sh);
  if (land) { ctx.rotate(-Math.PI / 2); ctx.translate(-sh / 2, -sw / 2); } else ctx.translate(-sw / 2, -sh / 2);
  ctx.imageSmoothingQuality = 'high';
  await screen();
  ctx.restore();
  // the island, and a faint sheen on the glass
  if (island) { rr(-63, -sh / 2 + 11, 126, 37, 18.5); ctx.fillStyle = '#000'; ctx.fill(); }
  ctx.save(); rr(-sw / 2, -sh / 2, sw, sh, R); ctx.clip();
  const sheen = ctx.createLinearGradient(-sw / 2, -sh / 2, sw / 2, sh / 2);
  sheen.addColorStop(0, 'rgba(255,255,255,0.06)'); sheen.addColorStop(0.42, 'rgba(255,255,255,0)'); sheen.addColorStop(1, 'rgba(255,255,255,0.025)');
  ctx.fillStyle = sheen; ctx.fillRect(-sw / 2, -sh / 2, sw, sh); ctx.restore();
  ctx.restore();
}
// The iPhone's screens (pages.json of stitch.py): a screen as captured, or a page scrolled to y points.
const ipFile = (f) => pic(`shots/ip/${f}`);
async function ipShot(f, a = 1) { if (a <= 0) return; const im = await ipFile(f); ctx.save(); ctx.globalAlpha *= a; ctx.drawImage(im, 0, 0, 402, 874); ctx.restore(); }
// The bars of a page at y: the screens shot on either side of y, faded into each other as the page goes.
function around(pg, y) {
  const idx = [];
  pg.offsets.forEach((o, i) => { if (idx.length && Math.abs(pg.offsets[idx[idx.length - 1]] - o) < 0.5) idx[idx.length - 1] = i; else idx.push(i); });
  for (let j = 0; j < idx.length - 1; j++) {
    const a = pg.offsets[idx[j]], b = pg.offsets[idx[j + 1]];
    if (y <= b) return [idx[j], idx[j + 1], E.sine(seg(y, a, b))];
  }
  const l = idx[idx.length - 1]; return [l, l, 0];
}
async function ipPage(name, y) {
  const pg = IP[name]; if (!pg) return;
  const strip = await pic(`shots/ip/${pg.strip}`);
  y = clamp(y, 0, pg.height - 874);
  ctx.drawImage(strip, 0, y * 3, 1206, 2622, 0, 0, 402, 874);
  const [a, b, k] = around(pg, y);
  const A = await ipFile(pg.shots[a]), B = await ipFile(pg.shots[b]);
  const band = (im, from, h, alpha) => { if (alpha <= 0) return; ctx.save(); ctx.globalAlpha *= alpha; ctx.drawImage(im, 0, from * 3, 1206, h * 3, 0, from, 402, h); ctx.restore(); };
  if (name === 'home') {
    // Mira's shade under the status bar, and the bar
    const g = ctx.createLinearGradient(0, 0, 0, 75);
    [[0, 0.75], [0.13, 0.62], [0.27, 0.52], [0.44, 0.4], [0.67, 0.24], [0.84, 0.1], [1, 0]].forEach(([o, v]) => g.addColorStop(o, `rgba(0,0,0,${v})`));
    ctx.fillStyle = g; ctx.fillRect(0, 0, 402, 75);
    ctx.drawImage(await pic('shots/ip/status.png'), 0, 0, 402, 50);
  } else { band(A, 0, pg.top, 1); band(B, 0, pg.top, k); }
  // the tab bar alone, cut out along its pills (the strip is right around them)
  const bar = async (i, alpha) => { if (alpha <= 0) return; const im = await pic(`shots/ip/${pg.bars[i]}`); ctx.save(); ctx.globalAlpha *= alpha; ctx.drawImage(im, 0, pg.barTop, 402, 874 - pg.barTop); ctx.restore(); };
  if (a === b) await bar(a, 1); else { await bar(a, 1 - k); await bar(b, k); }
}
// iOS's push: the new page from the right, the old one a third of the way left, dimmed.
async function push(drawA, drawB, k) {
  const e = E.inOut5(k);
  ctx.save(); ctx.translate(-e * 402 * 0.3, 0); await drawA(); ctx.fillStyle = `rgba(0,0,0,${0.35 * e})`; ctx.fillRect(0, 0, 402, 874); ctx.restore();
  ctx.save(); ctx.translate((1 - e) * 402, 0); ctx.shadowColor = 'rgba(0,0,0,0.5)'; ctx.shadowBlur = 30; ctx.fillStyle = '#000'; ctx.fillRect(0, 0, 402, 874); ctx.shadowColor = 'transparent'; await drawB(); ctx.restore();
}
// A finger's touch on the screen: a soft disc, pressed then lifted.
function touch(x, y, t, at, hold = 0.25) {
  const k = seg(t, at - 0.12, at), out = seg(t, at + hold, at + hold + 0.3);
  if (k <= 0 || out >= 1) return;
  ctx.save(); ctx.globalAlpha = E.out(k) * (1 - out) * 0.9;
  const r = 22 * (1.25 - 0.25 * E.out(k)) * (1 + 0.3 * out);
  ctx.fillStyle = 'rgba(255,255,255,0.35)'; ctx.strokeStyle = 'rgba(255,255,255,0.85)'; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); ctx.restore();
}

// A pill naming a feature, popping in on a beat.
function chip(text, x, y, t, tin, tout, { size = 30, align = 'center' } = {}) {
  if (t < tin || t > tout + 0.4) return;
  const k = E.spring(seg(t, tin, tin + 0.7)), out = E.in(seg(t, tout, tout + 0.35));
  ctx.save(); ctx.font = font(800, size); const tw = ctx.measureText(text).width;
  const w = tw + size * 1.9, h = size * 2;
  const x0 = align === 'center' ? x - w / 2 : align === 'right' ? x - w : x;
  ctx.translate(x0 + w / 2, y); ctx.scale(lerp(0.6, 1, k) * (1 - out * 0.2), lerp(0.6, 1, k) * (1 - out * 0.2)); ctx.globalAlpha = clamp(k * 1.6) * (1 - out);
  ctx.shadowColor = 'rgba(0,0,0,0.55)'; ctx.shadowBlur = 30; ctx.shadowOffsetY = 10;
  rr(-w / 2, -h / 2, w, h, h / 2); ctx.fillStyle = 'rgba(28,28,32,0.92)'; ctx.fill(); ctx.shadowColor = 'transparent';
  ctx.strokeStyle = 'rgba(255,255,255,0.16)'; ctx.lineWidth = 1.5; ctx.stroke();
  ctx.fillStyle = '#d9c7a1'; ctx.beginPath(); ctx.arc(-w / 2 + size * 0.85, 0, size * 0.18, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = '#f5f5f7'; ctx.textBaseline = 'middle'; ctx.fillText(text, -w / 2 + size * 1.25, size * 0.04);
  ctx.restore();
}

// ---------- moves ----------
// Keyframes [t, value, ease of the move that ends there]; values are numbers or framings.
function track(t, list) {
  if (t <= list[0][0]) return list[0][1];
  for (let i = 1; i < list.length; i++) {
    const [t1, v1, e = E.inOut] = list[i], [t0, v0] = list[i - 1];
    if (t <= t1) return typeof v0 === 'number' ? lerp(v0, v1, e(seg(t, t0, t1))) : cam(t, t0, t1, v0, v1, e);
  }
  return list[list.length - 1][1];
}
// The window filling the frame at scale s around unit point (fx, fy), never showing past its edges.
function full(s = 1, fx = 960, fy = 540) {
  const hx = 960 / s, hy = 540 / s;
  return { s, fx: clamp(fx, hx, 1920 - hx), fy: clamp(fy, hy, 1080 - hy), x: 960, y: 540, radius: 0, shadow: 0 };
}
// The cards under the pointer, lit as it comes near them.
function hovered(page, scroll, ptr) {
  if (!ptr || ptr.a <= 0) return [];
  const out = [];
  P(page).hovers.forEach((h, i) => {
    const r = h.rect, x0 = r.x + 12, y0 = r.y + 12 - scroll, x1 = r.x + r.w - 12, y1 = r.y + r.h - 12 - scroll;
    const d = Math.hypot(Math.max(x0 - ptr.x, 0, ptr.x - x1), Math.max(y0 - ptr.y, 0, ptr.y - y1));
    const k = clamp(1 - d / 40);
    if (k > 0) out.push([i, E.inOut(k)]);
  });
  return out;
}
const deflate = (r, n = 12) => ({ x: r.x + n, y: r.y + n, w: r.w - 2 * n, h: r.h - 2 * n });

// ---------- 1-4. Mira for Windows (bars 4-16) ----------
const C1 = bar(7.75), C2 = q(34), C3 = bar(10.75), C4 = q(46), C5 = q(50), C6 = q(57.5), OUT = bar(16) - 0.6;
const WP = [
  [bar(6.5), 1560, 1010], [bar(7), 255, 395], [bar(7) + q(1), 567, 395], [bar(7) + q(2), 879, 395],
  [C1 - 0.12, 34, 261], [C1 + 0.45, 34, 261],                       // Shows
  [bar(8) + q(1.3), 879, 700], [C2 + 0.45, 879, 700],                // Arcane
  [q(36), 430, 356], [q(37.4), 430, 356],                            // its Resume, then down the episodes
  [q(38), 560, 213], [q(39), 560, 344], [q(40), 560, 475], [q(41), 560, 606],
  [C3 - 0.15, 34, 207], [C3 + 0.45, 34, 207],                        // Movies
  [q(44.5), 210, 700], [q(45.5), 433, 700], [C4 + 0.45, 433, 700],   // Sprite Fright, Spring
  [q(48), 392, 333], [C5 + 0.4, 392, 333],                           // Resume
  [q(54.5), 211, 1008], [q(57), 1578, 1008], [C6 + 0.6, 1578, 1008], // along the bar
];
const WCLICKS = [C1, C2, C3, C4, C5, C6];
const PLAYER = () => WIN.player;
const nearest = (list, key, v) => list.reduce((b, x) => (Math.abs(x[key] - v) < Math.abs(b[key] - v) ? x : b));
function windowAt(t) {
  const ptr = path(t, WP, WCLICKS);
  ptr.a = track(t, [[bar(6.4), 0], [bar(6.6), 1], [C5 + 0.12, 1], [C5 + 0.3, 0], [34.2, 0], [34.45, 1], [36.9, 1], [37.2, 0]]);
  const home = () => {
    const scroll = track(t, [[bar(6.3), 0], [bar(7) - 0.12, 520]]);
    const banners = [[0, bar(4.75)], [1, bar(5.5)], [2, bar(6)]].map(([i, at]) => [i, E.inOut(seg(t, at, at + 0.4))]);
    return { page: 'home', scroll, banners, hovers: hovered('home', scroll, ptr) };
  };
  const series = () => ({ page: 'series', hovers: hovered('series', 0, ptr) });
  const arcane = () => { const scroll = track(t, [[23.0, 0], [24.25, 450]]); return { page: 'arcane', scroll, hovers: hovered('arcane', scroll, ptr) }; };
  const movies = () => ({ page: 'movies', hovers: hovered('movies', 0, ptr) });
  const spring = () => ({ page: 'spring', hovers: hovered('spring', 0, ptr) });
  const player = () => {
    const pl = PLAYER(); let ft, overlay = null, overlayA = 0;
    if (t < C6) {
      ft = 46.375 + Math.max(0, t - (C5 + 0.6));
      if (t < 34.3) { overlay = nearest(pl.playing, 'position', ft).file; overlayA = 1 - seg(t, 33.5, 33.9); }
      else { const f = clamp((ptr.x - 24) / 1872, 0.1, 0.83); overlay = nearest(pl.scrub, 'fraction', f).file; overlayA = seg(t, 34.3, 34.55); }
    } else {
      ft = 385.67 + (t - C6);
      overlay = nearest(pl.after, 'position', ft).file; overlayA = 1 - seg(t, 37.0, 37.4);
    }
    return { player: { ft, overlay, overlayA } };
  };
  const card = (page, name) => deflate(P(page).hovers[hoverAt(page, name)].rect);
  let st;
  if (t < C1 + 0.08) st = home();
  else if (t < C1 + 0.6) st = { kind: 'rise', from: home(), to: series(), k: seg(t, C1 + 0.08, C1 + 0.6) };
  else if (t < C2 + 0.05) st = series();
  else if (t < C2 + 0.62) st = { kind: 'grow', from: series(), to: arcane(), k: seg(t, C2 + 0.05, C2 + 0.62), rect: card('series', 'Arcane') };
  else if (t < C3 + 0.05) st = arcane();
  else if (t < C3 + 0.6) st = { kind: 'rise', from: arcane(), to: movies(), k: seg(t, C3 + 0.05, C3 + 0.6) };
  else if (t < C4 + 0.05) st = movies();
  else if (t < C4 + 0.62) st = { kind: 'grow', from: movies(), to: spring(), k: seg(t, C4 + 0.05, C4 + 0.62), rect: card('movies', 'Spring') };
  else if (t < C5 + 0.05) st = spring();
  else if (t < C5 + 0.6) st = { kind: 'film', from: spring(), to: player(), k: seg(t, C5 + 0.05, C5 + 0.6) };
  else st = player();
  st.cursor = ptr;
  // the camera
  const landing = { s: 1.5, fx: 520, fy: 470, x: 960, y: 540, radius: 14, shadow: 1 };
  const small = { s: 0.6, fx: 960, fy: 540, x: 1240, y: 560, radius: 14, shadow: 1 };
  const F = full(1);
  let v = track(t, [
    [bar(4), landing], [bar(4) + 1.5, small, E.out5], [bar(6) - 0.55, { ...small, s: 0.625, x: 1228 }, E.lin], [bar(6), F, E.inOut5],
    [bar(8) + 0.2, F], [C2, full(1.06, 879, 640)], [C2 + 0.62, F, E.inOut5],
    [23.0, F], [24.3, full(1.3, 760, 430)], [26.35, full(1.3, 760, 430), E.lin], [27.0, F],
    [27.8, F], [C4, full(1.08, 433, 700)], [C4 + 0.62, F, E.inOut5],
    [30.32, F], [31.45, full(1.6, 392, 333)], [C5 + 0.62, F, E.inOut5],
    [34.3, full(1.08)], [36.3, full(1.08)], [37.2, F, E.inOut5], [OUT, full(1.12), E.lin],
  ]);
  if (t > 34.3 && t < 37.2) {      // following the pointer along the bar, then back
    const k = E.inOut(seg(t, 34.3, 34.95)) * (1 - E.inOut5(seg(t, 36.45, 37.2)));
    v = full(lerp(1.08, 1.45, k), lerp(960, ptr.x, k), lerp(540, 1080, k));
  }
  if (t > OUT) {                    // thrown away to the left, for the phone
    const k = E.in(seg(t, OUT, bar(16) + 0.05));
    v = { ...full(1.12), x: lerp(960, -1500, k), s: lerp(1.12, 0.8, k), radius: 14 * k, shadow: k };
  }
  return { v, st };
}
section(bar(4), bar(16) + 0.1, async (t) => {
  // behind the small window: the colours of the banner it shows
  const bg = 1 - seg(t, bar(6) - 0.4, bar(6));
  if (bg > 0) {
    const names = ['Arcane', 'Spider-Man: Across the Spider-Verse', 'Spring'];
    const i = t < bar(4.75) + 0.2 ? 0 : t < bar(5.5) + 0.2 ? 1 : 2;
    await glow(names[i], bg * E.out(seg(t, bar(4), bar(4) + 0.6)), t * 0.2);
  }
  const { v, st } = windowAt(t);
  await win(v, st);
  // Mira for Windows, beside the window
  title('Mira for Windows', ['A cinema for', 'your library.'], 120, 500, t, bar(4) + q(1.5), bar(6) - q(1.2), { size: 84 });
  // Shows: the episodes
  if (t > 24 && t < 27) { plate(110, 880, 760, 150, E.out(seg(t, 24.2, 24.8)) * (1 - seg(t, 26.3, 26.8))); }
  title('Every show', ['Every season.', 'Every episode.'], 120, 880, t, 24.4, 26.3, { size: 70 });
  // the player
  if (t > 32.4 && t < 40.4) {
    plate(110, 850, 760, 160, E.out(seg(t, 32.5, 33.1)) * (1 - seg(t, 34.1, 34.6)));
    plate(110, 870, 900, 140, E.out(seg(t, 37.5, 38.1)) * (1 - seg(t, 39.4, 39.8)));
  }
  title('The player', ['Every format,', 'played as it is.'], 120, 850, t, 32.6, 34.1, { size: 72 });
  title('Made for watching', ['Skips the intros.', 'Picks up where you left off.'], 120, 880, t, 37.6, 39.4, { size: 60 });
  fill('#ffffff', 0.9 * (1 - E.out(seg(t, bar(4), bar(4) + 0.55))));
});
BLUR.push([bar(4), bar(4) + 0.5, 5], [C2 + 0.05, C2 + 0.6, 3], [C4 + 0.05, C4 + 0.6, 3], [OUT + 0.2, bar(16) + 0.7, 7]);

// ---------- 5. Mira for iPhone (bars 16-24) ----------
// Taps in points; each screen named with the page it shows and its scroll.
const TAP = {
  shows: [q(69), 213, 818], arcane: [q(70.5), 303, 313], movies: [q(79.5), 83, 818], verse: [q(85.5), 202, 685], search: [q(89), 353, 818],
};
const TYPED = [q(90), q(90.5), q(91), q(91.5), q(92), q(92.5)], FOUND = q(93.5);
const ipScroll = {
  home: [[41.05, 0], [41.95, 400, E.out], [42.3, 400], [43.2, 638, E.out]],
  arcane: [[45.65, 0], [46.85, 520, E.out], [47.15, 520], [48.45, 999, E.out]],
  movies: [[bar(20), 0], [51.6, 760, E.out], [51.85, 760], [53.05, 1408, E.out]],
};
// where a finger drags: the first part of each move
const DRAGS = [[41.05, 41.95], [42.3, 43.2], [45.65, 46.85], [47.15, 48.45], [bar(20), 51.6], [51.85, 53.05]];
async function ipScreenAt(t) {
  const page = (n) => () => ipPage(n, track(t, ipScroll[n] ?? [[0, 0]]));
  const fade = async (a, b, k) => { await a(); if (k > 0) { ctx.save(); ctx.globalAlpha *= k; await b(); ctx.restore(); } };
  const shot = (f) => () => ipShot(f);
  if (t < TAP.shows[0] + 0.04) await page('home')();
  else if (t < TAP.arcane[0] + 0.03) await fade(page('home'), page('shows'), E.out(seg(t, TAP.shows[0] + 0.04, TAP.shows[0] + 0.22)));
  else if (t < TAP.movies[0] + 0.04) await push(page('shows'), page('arcane'), seg(t, TAP.arcane[0] + 0.03, TAP.arcane[0] + 0.58));
  else if (t < TAP.verse[0] + 0.03) await fade(page('arcane'), page('movies'), E.out(seg(t, TAP.movies[0] + 0.04, TAP.movies[0] + 0.22)));
  else if (t < TAP.search[0] + 0.04) await push(page('movies'), page('verse'), seg(t, TAP.verse[0] + 0.03, TAP.verse[0] + 0.58));
  else if (t < TYPED[0]) await fade(page('verse'), shot('search-00.jpg'), E.out(seg(t, TAP.search[0] + 0.04, TAP.search[0] + 0.24)));
  else if (t < FOUND) { let i = 0; while (i < TYPED.length - 1 && t >= TYPED[i + 1]) i++; await shot(`search-${String(i + 1).padStart(2, '0')}.jpg`)(); }
  else await fade(shot('search-06.jpg'), shot('search-final.jpg'), E.inOut(seg(t, FOUND, FOUND + 0.3)));
  // the finger
  for (const [, x, y, at] of Object.values(TAP).map((v) => [0, v[1], v[2], v[0]])) touch(x, y, t, at, 0.12);
  for (const [a, b] of DRAGS) {
    const k = seg(t, a - 0.08, a + 0.38); if (k <= 0 || k >= 1) continue;
    const y = lerp(650, 330, E.inOut(seg(t, a, a + 0.36)));
    ctx.save(); ctx.globalAlpha = 0.85 * Math.sin(Math.PI * k);
    ctx.fillStyle = 'rgba(255,255,255,0.35)'; ctx.strokeStyle = 'rgba(255,255,255,0.85)'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.arc(250, y, 22, 0, Math.PI * 2); ctx.fill(); ctx.stroke(); ctx.restore();
  }
}
// the phone's place on the frame
const PHONE = { cx: 700, cy: 545, s: 1.07, rot: 0 };
function phoneAt(t) {
  const zoomed = { cx: 800, cy: 585, s: 1.34, rot: 0 };
  const side = { cx: 1285, cy: 610, s: 0.7, rot: 0 };
  return track(t, [
    [OUT + 0.15, { ...PHONE, cx: 2700, rot: 0.1 }], [bar(16) + 0.3, PHONE, E.out5],
    [45.3, PHONE], [46.3, zoomed], [48.9, zoomed, E.lin], [49.85, PHONE],
    [54.6, PHONE], [55.9, { ...PHONE, s: 1.12, cy: 560 }], [56.5, PHONE],
    [60.05, PHONE], [60.95, side, E.inOut5], [69.9, { ...side, cy: 590 }, E.lin],
  ]);
}
section(OUT + 0.1, bar(28) - 0.1, async (t) => {
  // the colours of what the phone shows, behind it
  const tint = t < TAP.arcane[0] + 0.3 ? 'Spider-Man: Across the Spider-Verse' : t < TAP.movies[0] ? 'Arcane' : t < TAP.verse[0] + 0.3 ? 'Flow' : 'Spider-Man: Across the Spider-Verse';
  const away = 1 - seg(t, 60.1, 60.9);
  await glow(tint, 0.9 * seg(t, OUT + 0.1, bar(16) + 0.2) * away, t * 0.15);
  if (t > 60) await everywhereBehind(t);
  const v = phoneAt(t); v.alpha = 1 - E.in(seg(t, 69.8, bar(28) - 0.1));
  const late = t > 60.3;
  await phone(v, async () => {
    if (late) { const k = E.inOut(seg(t, 60.3, 60.75)); await ipShot('search-final.jpg'); if (k > 0) { ctx.save(); ctx.globalAlpha *= k; await ipPage('verse', 0); ctx.restore(); } }
    else await ipScreenAt(t);
  });
  // the words beside it
  const x = 1120;
  title('Mira for iPhone', ['The same cinema,', 'in your pocket.'], x, 470, t, bar(16) + q(1), q(69) - 0.2, { size: 76 });
  title('Shows', ['Always the', 'next episode.'], 1180, 470, t, 45.9, 49.0, { size: 76 });
  title('Anywhere', ['Your whole library,', 'home or away.'], x, 470, t, bar(20) + q(0.5), q(85), { size: 76, sub: 'Built-in Tailscale: no other app to install.' });
  title('Search', ['Find anything,', 'as you type.'], x, 470, t, q(89.5), 59.9, { size: 76 });
  if (t > 60) await everywhereFront(t);
});
BLUR.push([TAP.arcane[0], TAP.arcane[0] + 0.6, 3], [TAP.verse[0], TAP.verse[0] + 0.6, 3], [60.05, 60.95, 4]);

// ---------- 6. Everywhere (bars 24-28) ----------
async function everywhereBehind(t) {
  const a = E.out(seg(t, 60.1, 60.8)) * (1 - E.in(seg(t, 69.7, bar(28) - 0.1)));
  await glow('Spider-Man: Across the Spider-Verse', 0.85 * a, t * 0.12);
  const v = track(t, [[60.15, { s: 0.6, fx: 960, fy: 540, x: -900, y: 610, radius: 14, shadow: 1 }], [60.95, { s: 0.6, fx: 960, fy: 540, x: 700, y: 610, radius: 14, shadow: 1 }, E.out5],
    [69.9, { s: 0.62, fx: 960, fy: 540, x: 690, y: 600, radius: 14, shadow: 1 }, E.lin]]);
  v.alpha = 1 - E.in(seg(t, 69.8, bar(28) - 0.1));
  await win(v, { page: 'verse', hovers: [[0, E.inOut(seg(t, 62.4, 62.7))]] });
}
async function everywhereFront(t) {
  const out = E.in(seg(t, 69.7, bar(28) - 0.1));
  ctx.save(); ctx.globalAlpha = 1 - out;
  words('One library. Every screen.', 960, 165, 84, t, 60.9, 69.6, { align: 'center', stagger: 0.08 });
  const chips = ['Watch together', 'Built-in Tailscale', 'Automatic subtitles', 'Skips intros', 'Resumes everywhere', 'Mac and web too'];
  chips.forEach((c, i) => chip(c, 1500, 320 + i * 102, t, q(98 + i * 2), 69.6, { size: 29, align: 'left' }));
  ctx.restore();
}
