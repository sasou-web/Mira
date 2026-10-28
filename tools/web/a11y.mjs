// Accessibility of Mira web, checked on each screen of check.mjs without anything of a third party in the page:
// - every button, link, field and switch has a name for a screen reader (Chromium's own accessibility tree);
// - no text under 4.5:1 on what is drawn behind it (3:1 from 24 px, or 18.7 px bold), as Legibility of Mira.Core counts
//   it; text over artwork or a gradient is not judged (what is behind it is not a colour);
// - Tab goes through the screen in the order it is read, never to something hidden or covered by a bar, never down a
//   column and back up it, and shows where it is;
// - with Windows' contrast themes (forced-colors), every control drawn by its fill alone keeps an edge, and a chosen chip,
//   switch or tab still looks chosen.
// Each function returns the faults found, in words; [] when there is none.

/** Interactive roles that a screen reader reads by their name: one without a name is « bouton », nothing else. */
const NAMED = new Set(['button', 'link', 'textbox', 'searchbox', 'checkbox', 'switch', 'combobox', 'slider', 'menuitem',
  'menuitemradio', 'menuitemcheckbox', 'tab', 'radio', 'spinbutton', 'image', 'img', 'dialog', 'alertdialog', 'menu']);

/** What has no name in the page shown (hidden screens and aria-hidden parts are out of Chromium's tree). */
export async function unnamed(page) {
  const cdp = await page.context().newCDPSession(page);
  try {
    const { nodes } = await cdp.send('Accessibility.getFullAXTree');
    const faults = [];
    for (const node of nodes) {
      if (node.ignored || !NAMED.has(node.role?.value) || `${node.name?.value ?? ''}`.trim()) continue;
      let what = node.role.value;
      if (node.backendDOMNodeId) {
        const { node: dom } = await cdp.send('DOM.describeNode', { backendNodeId: node.backendDOMNodeId }).catch(() => ({ node: null }));
        const attributes = Object.fromEntries((dom?.attributes ?? []).reduce((pairs, x, i, all) => (i % 2 ? pairs : [...pairs, [x, all[i + 1]]]), []));
        what = `${node.role.value} <${dom?.localName ?? '?'}${attributes.class ? ` class="${attributes.class}"` : ''}${attributes.href ? ` href="${attributes.href}"` : ''}>`;
      }
      faults.push(what);
    }
    return [...new Set(faults)];
  } finally { await cdp.detach().catch(() => {}); }
}

/** Text under its floor of contrast, once per colour, background and size: « 3,9:1 #8a8a90 sur #242427 (« Studio », 13 px) ». */
export const contrast = (page, floor = 4.5) => page.evaluate((floor) => {
  const parse = (value) => {
    let m = value.match(/^rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)$/);
    if (m) return { r: +m[1] / 255, g: +m[2] / 255, b: +m[3] / 255, a: m[4] == null ? 1 : +m[4] };
    m = value.match(/^color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: \/ ([\d.]+))?\)$/);
    if (m) return { r: +m[1], g: +m[2], b: +m[3], a: m[4] == null ? 1 : +m[4] };
    return null;
  };
  const over = (top, under) => ({ r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1 });
  const linear = (c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  const luminance = (c) => 0.2126 * linear(c.r) + 0.7152 * linear(c.g) + 0.0722 * linear(c.b);
  const ratio = (a, b) => { const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
  const hex = (c) => `#${[c.r, c.g, c.b].map((x) => Math.round(x * 255).toString(16).padStart(2, '0')).join('')}`;
  const page = parse(getComputedStyle(document.documentElement).backgroundColor) ?? { r: 7 / 255, g: 7 / 255, b: 8 / 255, a: 1 };
  // What is drawn behind the element: its own fill and its parents', down to the first opaque one; null over an image
  // or a gradient. The opacity of what lies between the text and that fill fades the text.
  const behind = (el) => {
    const layers = []; let fade = 1;
    for (let e = el; e; e = e.parentElement) {
      const s = getComputedStyle(e);
      if (s.backgroundImage !== 'none' || ['IMG', 'VIDEO', 'CANVAS', 'PICTURE'].includes(e.tagName)) return null;
      const fill = parse(s.backgroundColor);
      if (fill && fill.a > 0) { layers.push(fill); if (fill.a >= 1) break; }
      if (!layers.length) fade *= parseFloat(s.opacity);
    }
    let paper = page;
    for (const layer of layers.reverse()) paper = over(layer, paper);
    return { paper, fade };
  };
  const seen = new Map();
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const el = node.parentElement, text = node.textContent.trim();
    if (!el || !text || ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TITLE'].includes(el.tagName)) continue;
    // Inactive controls are out of WCAG's count; placeholders are shapes, not text.
    if (el.closest('[disabled], [aria-disabled="true"], .skeleton, [hidden]')) continue;
    if (!el.checkVisibility({ opacityProperty: true, visibilityProperty: true }) || !el.getClientRects().length) continue;
    const style = getComputedStyle(el), ink = parse(style.color);
    if (!ink || ink.a === 0) continue;
    const under = behind(el);
    if (!under) continue;
    const shown = over({ ...ink, a: ink.a * under.fade }, under.paper);
    const size = parseFloat(style.fontSize), bold = parseInt(style.fontWeight, 10) >= 700;
    const needed = size >= 24 || (size >= 18.66 && bold) ? Math.min(floor, 3 + (floor - 4.5)) : floor;
    const value = ratio(shown, under.paper);
    if (value + 0.005 >= needed) continue;
    const key = `${hex(shown)} ${hex(under.paper)} ${Math.round(size)}`;
    if (!seen.has(key)) seen.set(key, `${value.toFixed(2).replace('.', ',')}:1 ${hex(shown)} sur ${hex(under.paper)} (« ${text.slice(0, 24)} », ${Math.round(size)} px, .${[...el.classList].join('.') || el.tagName.toLowerCase()})`);
  }
  return [...seen.values()];
}, floor);

/**
 * Tab from the top of the screen, `limit` times at most: where the focus goes (what it names), and the faults: focus
 * on something hidden or covered, no visible ring, a positive tabindex, back up the same column.
 */
export async function keyboard(page, { limit = 80 } = {}) {
  await page.evaluate(() => { document.activeElement?.blur?.(); scrollTo(0, 0); window.miraTabIds = new WeakMap(); window.miraTabNext = 0; });
  const faults = [], stops = [];
  const positive = await page.evaluate(() => [...document.querySelectorAll('[tabindex]')].filter((e) => e.tabIndex > 0).map((e) => e.outerHTML.slice(0, 60)));
  if (positive.length) faults.push(`tabindex positif : ${positive.slice(0, 3).join(', ')}`);
  let last = null;
  for (let i = 0; i < limit; i++) {
    await page.keyboard.press('Tab');
    const at = await page.evaluate(() => {
      const el = document.activeElement;
      if (!el || el === document.body || el === document.documentElement) return null;
      if (!window.miraTabIds.has(el)) window.miraTabIds.set(el, ++window.miraTabNext);
      const name = (el.getAttribute('aria-label') || el.textContent || el.getAttribute('placeholder') || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 30);
      const r = el.getBoundingClientRect();
      const visible = el.checkVisibility({ opacityProperty: true, visibilityProperty: true }) && r.width > 0 && r.height > 0 && !el.closest('[aria-hidden="true"], [inert]');
      // Covered: what is drawn at its centre is something else (a bar over it).
      const cx = Math.min(Math.max(r.left + r.width / 2, 0), innerWidth - 1), cy = Math.min(Math.max(r.top + r.height / 2, 0), innerHeight - 1);
      const hit = document.elementFromPoint(cx, cy);
      const cover = visible && hit && hit !== el && !el.contains(hit) && !hit.contains(el)
        ? hit.closest('.tabbar, .topbar, .netbar, .floating-back') ?? hit.closest(':not(svg, svg *)[class]') ?? hit : null;
      const covered = cover ? `.${[...cover.classList].join('.') || cover.tagName.toLowerCase()}` : null;
      // A ring: an outline, a shadow, or a field's border, on the element or what it holds.
      const ring = [el, ...el.querySelectorAll('*')].slice(0, 40).some((e) => {
        const s = getComputedStyle(e);
        return (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 && !/rgba\(.*, 0\)$/.test(s.outlineColor))
          || s.boxShadow !== 'none' || (e === el && /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName) && parseFloat(s.borderTopWidth) > 0);
      });
      // Its place on the page as laid out, whatever is scrolled: the page's and every scrolled box's offsets added.
      let x = r.left + scrollX, y = r.top + scrollY, fixed = false;
      for (let e = el.parentElement; e; e = e.parentElement) {
        x += e.scrollLeft; y += e.scrollTop;
        if (/fixed|sticky/.test(getComputedStyle(e).position)) fixed = true;
      }
      if (/fixed|sticky/.test(getComputedStyle(el).position)) fixed = true;
      return { id: window.miraTabIds.get(el), name, visible, covered, ring, x, y, w: r.width, h: r.height, fixed };
    });
    if (!at || stops.some((s) => s.id === at.id)) break;
    // What fades in when the focus comes (the player's controls) is given the time to.
    if (!at.visible) {
      await page.waitForTimeout(400);
      at.visible = await page.evaluate(() => { const el = document.activeElement; return !!el?.checkVisibility({ opacityProperty: true, visibilityProperty: true }) && !el.closest('[aria-hidden="true"], [inert]'); });
    }
    stops.push(at);
    if (!at.visible) faults.push(`focus sur un élément caché « ${at.name} »`);
    else if (at.covered) faults.push(`« ${at.name} » sous ${at.covered}`);
    else if (!at.ring) faults.push(`« ${at.name} » sans anneau de focus`);
    if (last && !at.fixed && !last.fixed && at.y + at.h / 2 < last.y && at.x < last.x + last.w && last.x < at.x + at.w) faults.push(`retour en arrière : « ${last.name} » puis « ${at.name} »`);
    if (!at.fixed) last = at;
  }
  return { stops: stops.map((s) => s.name), faults: [...new Set(faults)] };
}

/**
 * Windows' contrast themes: a control drawn by its fill alone (a button, a chip, a switch, a bar of progress) has an edge
 * once the theme takes the fills away, and a chosen one looks unlike the others. Read with and without forced colours.
 */
export async function forcedColors(page) {
  const CONTROLS = 'button, a[href], [role=button], [role=switch], [role=tab], [role=radio], [role=menuitemradio], input, select, textarea, .switch, [role=progressbar], .progress';
  await page.emulateMedia({ forcedColors: 'none' });
  const filled = await page.evaluate((selector) => {
    const transparent = (c) => c === 'transparent' || /rgba\(.*, 0\)$/.test(c);
    const list = [];
    document.querySelectorAll(selector).forEach((el) => {
      // A switch's track is hidden from screen readers (its row is the switch), not from the eyes: only placeholders are skipped.
      if (!el.checkVisibility({ visibilityProperty: true, opacityProperty: true }) || !el.getClientRects().length || el.closest('.skeleton, .card[aria-hidden="true"]')) return;
      const s = getComputedStyle(el);
      if (transparent(s.backgroundColor) || parseFloat(s.borderTopWidth) > 0) return;
      el.dataset.miraFilled = '1'; list.push(el);
    });
    return list.length;
  }, CONTROLS);
  await page.emulateMedia({ forcedColors: 'active' }); await page.waitForTimeout(200);
  const faults = await page.evaluate(() => {
    const out = [];
    const looks = (el) => { const s = getComputedStyle(el); return [s.backgroundColor, s.color, s.borderTopColor, s.borderTopWidth, s.outlineStyle, s.outlineColor].join('|'); };
    const label = (el) => (el.getAttribute('aria-label') || el.textContent || el.className || el.tagName).trim().replace(/\s+/g, ' ').slice(0, 24);
    document.querySelectorAll('[data-mira-filled]').forEach((el) => {
      const s = getComputedStyle(el);
      const edge = (parseFloat(s.borderTopWidth) > 0 && s.borderTopStyle !== 'none') || (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || s.forcedColorAdjust === 'none';
      if (!edge) out.push(`sans bord : « ${label(el)} » (.${[...el.classList].join('.') || el.tagName.toLowerCase()})`);
      delete el.dataset.miraFilled;
    });
    // Chosen and not chosen side by side (the chips of a row, the tabs, the switches of Réglages).
    for (const attribute of ['aria-pressed', 'aria-checked', 'aria-selected', 'aria-current']) {
      const groups = new Map();
      document.querySelectorAll(`[${attribute}]`).forEach((el) => {
        if (!el.checkVisibility() || !el.getClientRects().length) return;
        const key = `${el.parentElement?.className}|${el.className}`;
        if (!groups.has(key)) groups.set(key, { on: null, off: null });
        const group = groups.get(key), on = !['false', ''].includes(el.getAttribute(attribute));
        const target = el.querySelector('.switch') ?? el;
        if (on) group.on ??= { el, look: looks(target) + looks(el) + (target !== el ? getComputedStyle(target, '::after').transform : '') };
        else group.off ??= { el, look: looks(target) + looks(el) + (target !== el ? getComputedStyle(target, '::after').transform : '') };
      });
      for (const { on, off } of groups.values()) if (on && off && on.look === off.look) out.push(`choisi pareil que les autres : « ${label(on.el)} » (${attribute})`);
    }
    return out;
  });
  // Back to Mira's colours, once their transitions have ended.
  await page.emulateMedia({ forcedColors: 'none' }); await page.waitForTimeout(400);
  return { filled, faults: [...new Set(faults)] };
}

/**
 * A sheet opened from the keyboard: the focus goes into it, Tab stays in it, Escape closes it and gives the focus back
 * to what opened it. `opener` is a locator; `choose`, the label of a choice to make instead of Escape (the screen is
 * drawn again behind it: the focus must still come back to the same control).
 */
export async function sheetFocus(page, opener, { choose = null } = {}) {
  const faults = [];
  // Past the last choice, Tab may leave the page for the browser's own bar (the document keeps no focus): only another
  // control of the page is a way out.
  const inside = () => page.evaluate(() => { const el = document.activeElement; return !el || el === document.body || !!el.closest('.sheet-layer:not(.leaving) .sheet'); });
  const first = (text) => text.trim().split('\n')[0].split(' :')[0].trim();
  const name = (await opener.getAttribute('aria-label')) ?? first(await opener.innerText());
  await opener.focus(); await page.keyboard.press('Enter');
  await page.waitForSelector('.sheet-layer:not(.leaving) .sheet', { timeout: 5000 }); await page.waitForTimeout(400);
  if (!(await page.evaluate(() => !!document.activeElement?.closest('.sheet')))) faults.push('le focus ne va pas dans la feuille');
  const items = await page.locator('.sheet-layer:not(.leaving) .sheet :is(button, a[href], input)').count();
  for (let i = 0; i <= items + 1; i++) {
    await page.keyboard.press('Tab');
    if (!(await inside())) { faults.push(`Tab sort de la feuille au ${i + 1}e appui`); break; }
  }
  if (choose) {
    const item = page.locator('.sheet-layer:not(.leaving) .sheet-item', { hasText: choose }).first();
    await item.focus(); await page.keyboard.press('Enter');
  } else await page.keyboard.press('Escape');
  await page.waitForTimeout(700);
  const back = await page.evaluate((label) => {
    const el = document.activeElement;
    return !!el && el !== document.body && (el.getAttribute('aria-label') ?? el.innerText).trim().startsWith(label);
  }, name);
  if (await page.locator('.sheet-layer').count()) faults.push('la feuille reste ouverte');
  if (!back) faults.push(`le focus ne revient pas sur « ${name} » (${await page.evaluate(() => document.activeElement?.outerHTML.slice(0, 60) ?? 'rien')})`);
  return faults;
}

/** The whole audit of the screen shown: names, contrast, keyboard. One line per kind of fault. */
export async function audit(page, { floor = 4.5, tabs = 80 } = {}) {
  const [names, low] = [await unnamed(page), await contrast(page, floor)];
  const keys = await keyboard(page, { limit: tabs });
  const faults = [];
  if (names.length) faults.push(`sans nom : ${names.slice(0, 6).join(', ')}`);
  if (low.length) faults.push(`contraste : ${low.slice(0, 6).join(' ; ')}`);
  if (keys.faults.length) faults.push(`clavier : ${keys.faults.slice(0, 6).join(' ; ')}`);
  return { faults, stops: keys.stops };
}
