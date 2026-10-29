// The pieces every screen shares: cards, rows, sheets, notices and the loading, empty and error states.
import { api } from './api.js';
import { h, icon, progress, remaining, episodeCode, clear, duration, seconds, extraKind } from './dom.js';
import { tr, trn, trc } from './i18n.js';
import { artFor, picture } from './images.js';
import { settings } from './session.js';
import { qualityText } from './player/tracks.js';
import { holds } from './collections.js';
import { collectionSheet, takeOut } from './collection-sheet.js';
import { upgradeEntry } from './identify-sheet.js';
import { plainOverview } from './overview.js';

/** Item changes (watched, favourite, hidden) that every screen showing the item takes into account. */
export const changes = new EventTarget();
export function changed(item, detail = {}) { changes.dispatchEvent(new CustomEvent('item', { detail: { item, ...detail } })); }

/** A title's page; an episode's is its series', open at it; a collection (a saga or one made by hand) has a page of its own. */
export function titleHref(item) {
  if (item.Type === 'Episode' && item.SeriesId) return `#/titre/${item.SeriesId}?episode=${item.Id}`;
  return item.Type === 'BoxSet' ? `#/saga/${item.Id}` : `#/titre/${item.Id}`;
}
/** The player for an item: from the start, or with tracks chosen on its page ({ audio, subtitle } as Jellyfin indexes). */
export function playHref(item, fromStart = false, tracks = null) {
  const params = new URLSearchParams();
  if (fromStart) params.set('debut', '1');
  if (tracks?.audio != null) params.set('audio', String(tracks.audio));
  if (tracks?.subtitle != null) params.set('sous-titres', String(tracks.subtitle));
  const text = params.toString();
  return `#/lecture/${item.Id}${text ? `?${text}` : ''}`;
}

/** « 2023 », « 3 saisons », « S1 · É3 · reste 12 min ». */
export function subtitle(item) {
  if (item.Type === 'Episode') return [episodeCode(item), remaining(item)].filter(Boolean).join(' · ');
  if (item.Type === 'Series') {
    const seasons = item.ChildCount;
    return seasons ? trn(seasons, '{n} saison', '{n} saisons') : (item.ProductionYear ? String(item.ProductionYear) : tr('Série'));
  }
  if (item.Type === 'BoxSet') return item.ChildCount ? trn(item.ChildCount, '{n} titre', '{n} titres') : tr('Collection');
  return [item.ProductionYear, remaining(item)].filter(Boolean).join(' · ');
}

function marks(item, box) {
  const done = progress(item);
  if (done > 0) box.append(h('div', { class: 'progress' }, h('i', { style: { width: `${(done * 100).toFixed(1)}%` } })));
  else if (item.UserData?.Played && item.Type !== 'Series') box.append(h('div', { class: 'badge-watched', title: tr('Vu') }, icon('check')));
  else if (item.Type === 'Series' && item.UserData?.Played) box.append(h('div', { class: 'badge-watched', title: tr('Toute la série est vue') }, icon('check')));
  else if (toWatch(item)) box.append(h('div', { class: 'badge-count', 'aria-hidden': 'true' }, toWatch(item) > 99 ? '99+' : String(toWatch(item))));
}
/**
 * Episodes left in a series already started: the count that shows new ones came out (MediaItem.EpisodesToWatch on
 * Windows and Mac). None before its first episode is watched, where it would only repeat its length.
 */
export const toWatch = (item) => (item.Type === 'Series' && !item.UserData?.Played && item.UserData?.PlayedPercentage > 0
  && item.UserData?.UnplayedItemCount > 0 ? item.UserData.UnplayedItemCount : 0);

/**
 * The items cards were drawn from, by id: a title page opened from a card draws at once with what is already known
 * (name, pictures), while its details load.
 */
const known = new Map();
export const knownItem = (id) => known.get(id) ?? null;
function remember(item) {
  if (!item?.Id) return;
  known.delete(item.Id); known.set(item.Id, item);
  if (known.size > 300) known.delete(known.keys().next().value);
}

// Sizes that the cards are drawn at on a phone, in CSS pixels: the server sends pictures at that size, not larger.
const posterWidth = () => (innerWidth < 600 ? 120 : 156);
const wideWidth = () => Math.min(340, Math.round(innerWidth * 0.74));

/**
 * A poster with its title: opens the title page; a long press offers the item's actions. collection: the collection
 * whose page shows it ({ Id, Name }), which its menu offers to take it out of.
 */
export function posterCard(item, { width = posterWidth(), eager = false, collection = null } = {}) {
  const name = item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name;
  const art = picture(artFor(item, 'poster'), { kind: 'poster', width, label: name, eager });
  marks(item, art);
  const card = h('a', { class: 'card', href: titleHref(item), 'aria-label': toWatch(item) ? trn(toWatch(item), '{name}, {n} épisode à voir', '{name}, {n} épisodes à voir', { name }) : name },
    art, h('div', { class: 'card-title clamp-2 lines-2' }, name), h('div', { class: 'card-sub clamp-1' }, subtitle(item)));
  card.dataset.sig = cardSig(item);
  // The item the card shows now (reconcile keeps a card and gives it the item as the server just sent it).
  card.item = item;
  card.addEventListener('click', () => remember(card.item));
  withActions(card, item, { collection });
  return card;
}

/** A 16:9 card for Continuer à regarder: a tap plays from where it was left. */
export function wideCard(item, { width = wideWidth(), play = true, eager = false } = {}) {
  const series = item.Type === 'Episode';
  const art = picture(artFor(item, 'wide'), { kind: 'wide', width, label: series ? item.SeriesName : item.Name, eager });
  marks(item, art);
  const card = h('a', { class: 'card wide-card', href: play ? playHref(item) : titleHref(item), 'aria-label': play ? tr('Lire {name}', { name: series ? item.SeriesName : item.Name }) : (series ? item.SeriesName : item.Name) },
    art,
    h('div', { class: 'card-title clamp-1' }, series ? item.SeriesName : item.Name),
    h('div', { class: 'card-sub clamp-1' }, series ? [episodeCode(item), item.Name].filter(Boolean).join(' · ') : subtitle(item)));
  card.dataset.sig = cardSig(item);
  card.item = item;
  withActions(card, item, { resumeRow: play });
  return card;
}

/** A bonus video of a title (« Coulisses · 4 min »): it has no page of its own, a tap plays it. */
export function extraCard(item, { width = wideWidth() } = {}) {
  const art = picture(artFor(item, 'wide'), { kind: 'wide', width, label: item.Name });
  marks(item, art);
  const card = h('a', { class: 'card wide-card', href: playHref(item), 'aria-label': tr('Lire {name}', { name: item.Name }) },
    art,
    h('div', { class: 'card-title clamp-1' }, item.Name),
    h('div', { class: 'card-sub clamp-1' }, [extraKind(item.ExtraType), remaining(item) || (item.RunTimeTicks ? duration(seconds(item.RunTimeTicks)) : '')].filter(Boolean).join(' · ')));
  card.dataset.sig = cardSig(item);
  card.item = item;
  return card;
}

/** All that a card shows of an item: a card whose item still reads the same is kept as it is, its picture too. */
export const cardSig = (x) => JSON.stringify([x.Id, x.Type, x.Name, x.SeriesName, x.IndexNumber, x.ParentIndexNumber, x.ProductionYear,
  x.ChildCount, x.RunTimeTicks, x.ImageTags, x.SeriesPrimaryImageTag, x.ParentThumbImageTag, x.ParentBackdropImageTags,
  x.BackdropImageTags, x.UserData?.Played, x.UserData?.PlaybackPositionTicks, x.UserData?.IsFavorite, toWatch(x)]);
/**
 * The container's cards made to match `items`, in their order: a card still showing its item as it is stays (no
 * second flash of its picture), the others are made by `make`. Nothing moves when nothing changed.
 */
export function reconcile(container, items, make) {
  const old = new Map();
  for (const el of container.children) if (el.dataset.sig && !old.has(el.dataset.sig)) old.set(el.dataset.sig, el);
  const next = items.map((item) => {
    const kept = old.get(cardSig(item));
    if (kept) { old.delete(kept.dataset.sig); kept.item = item; return kept; }
    return make(item);
  });
  const same = next.length === container.children.length && next.every((el, i) => container.children[i] === el);
  if (!same) container.replaceChildren(...next);
}

export function personCard(person) {
  const card = h('a', { class: 'card person', href: `#/personne/${person.Id}`, 'aria-label': person.Name },
    picture(artFor(person, 'square'), { kind: 'square', width: 84, label: person.Name }),
    h('div', { class: 'card-title clamp-2' }, person.Name),
    person.Role ? h('div', { class: 'card-sub clamp-1' }, person.Role) : null);
  return card;
}

/**
 * A titled horizontal row; nothing when there is nothing to show. The pictures on screen at first load at once, the
 * others as the row scrolls (VoiceOver reads its cards as links).
 */
export function row(title, items, card, { wide = false, more = '', people = false } = {}) {
  if (!items?.length) return null;
  const first = wide ? 2 : 3;
  return h('section', { class: 'section' },
    h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, title), more ? h('a', { href: more }, tr('Tout voir')) : null),
    h('div', { class: ['row', wide && 'wide', people && 'people'] }, items.map((item, i) => {
      const el = card(item);
      if (i < first) { const img = el.querySelector('img'); if (img) { img.loading = 'eager'; img.fetchPriority = 'high'; } }
      return el;
    })));
}

/** A card's place while it loads, with the card's own lines: nothing moves when the card takes it. */
const skeletonCard = (kind) => h('div', { class: ['card', kind === 'wide' && 'wide-card'], 'aria-hidden': 'true' }, h('div', { class: ['skeleton', 'art', kind] }),
  h('div', { class: ['card-title', kind === 'poster' && 'lines-2'] }, h('span', { class: 'skeleton' })), h('div', { class: 'card-sub' }, h('span', { class: 'skeleton' })));

export function skeletonRow({ wide = false, count = 6, title = true } = {}) {
  return h('section', { class: 'section', 'aria-hidden': 'true' },
    title ? h('div', { class: 'section-head' }, h('div', { class: 'skeleton', style: { width: '160px', height: '20px' } })) : null,
    h('div', { class: ['row', wide && 'wide'] }, Array.from({ length: count }, () => skeletonCard(wide ? 'wide' : 'poster'))));
}

export function skeletonGrid(count = 18) {
  return h('div', { class: 'grid', 'aria-hidden': 'true' }, Array.from({ length: count }, () => skeletonCard('poster')));
}

export function emptyState({ symbol = 'films', title, text = '', action = null }) {
  return h('div', { class: 'state' }, icon(symbol), h('h2', {}, title), text ? h('p', {}, text) : null,
    action ? h('a', { class: 'btn small', href: action.href, on: action.run ? { click: (e) => { e.preventDefault(); action.run(); } } : undefined }, action.label) : null);
}

export function errorState(error, retry) {
  return h('div', { class: 'state', role: 'alert' }, icon(error?.unreachable ? 'offline' : 'warning'),
    h('h2', {}, error?.unreachable ? tr('Le serveur ne répond pas') : tr('Ça n’a pas marché')),
    h('p', {}, error?.message ?? tr('Une erreur inattendue est survenue.')),
    retry ? h('button', { class: 'btn small', on: { click: retry } }, icon('refresh', { size: 16 }), tr('Réessayer')) : null);
}

export function spinner(label = tr('Chargement')) { return h('div', { class: 'state', role: 'status' }, h('div', { class: 'spinner' }), h('span', { class: 'sr' }, label)); }

/**
 * Where notices and sheets go: in the element shown full screen when there is one (Mira's player on Android or a
 * computer), since nothing outside it shows then.
 */
const overlayHost = () => document.fullscreenElement ?? document.webkitFullscreenElement ?? document.body;

/** A notice at the bottom of the screen, with an optional action (« Annuler »). */
export function toast(text, { action = null, duration = 4000 } = {}) {
  let layer = document.querySelector('.toast-layer');
  if (!layer) layer = h('div', { class: 'toast-layer', role: 'status', 'aria-live': 'polite' });
  if (layer.parentNode !== overlayHost()) overlayHost().append(layer);
  clear(layer);
  let timer = 0;
  // It sinks away rather than vanishing; a swipe down sends it away sooner.
  const close = () => {
    clearTimeout(timer);
    if (!note.isConnected || note.classList.contains('leaving')) return;
    note.classList.add('leaving');
    note.addEventListener('animationend', () => note.remove(), { once: true });
    setTimeout(() => note.remove(), 400);
  };
  const note = h('div', { class: 'toast' }, h('span', {}, text),
    action ? h('button', { on: { click: () => { close(); action.run(); } } }, action.label) : null);
  let startY = null;
  note.addEventListener('touchstart', (e) => { startY = e.touches[0].clientY; }, { passive: true });
  note.addEventListener('touchmove', (e) => { if (startY != null && e.touches[0].clientY - startY > 24) { startY = null; close(); } }, { passive: true });
  layer.append(note);
  timer = setTimeout(close, action ? Math.max(duration, 6000) : duration);
  return close;
}

/**
 * A sheet of choices from the bottom of the screen. items: { label, sub, symbol, selected, checkbox, danger, run }, or
 * { heading } above a group of them. Closes on a choice, on the backdrop, or with Escape.
 */
/** focus: a field of `body` that takes the focus at once, within the touch that opened the sheet (iOS's keyboard). */
export function sheet({ title = '', items = [], body = null, held = false, focus = null, at = null }) {
  // A second tap on what opens a sheet (while the first one is still arriving) does not stack another.
  if (document.querySelector('.sheet-layer:not(.leaving)')) return () => {};
  const previous = document.activeElement;
  // A right click with a mouse or a trackpad (at: where it was) opens a menu there, as a computer does; a finger, the sheet.
  const anchored = !!at && matchMedia('(pointer: fine)').matches;
  const layer = h('div', { class: ['sheet-layer', anchored && 'anchored'] });
  const openedAt = performance.now();
  // A touch held to open a menu ends with a click where the finger lifts: too soon to be a choice in the sheet. A
  // sheet opened by a plain tap takes the next tap at once.
  const early = () => held && performance.now() - openedAt < 350;
  // Back on Android (Chrome 120 and later) closes the sheet rather than the screen under it.
  let watcher = null;
  try { if ('CloseWatcher' in window) { watcher = new CloseWatcher(); watcher.onclose = () => close(); } } catch { /* not here */ }
  let closing = false;
  // It slides back down before it goes, as iOS sheets do; the choice made runs once it has started to.
  const close = () => {
    if (closing) return;
    closing = true;
    try { watcher?.destroy(); } catch { /* already gone */ }
    resized?.disconnect();
    removeEventListener('keydown', keys); removeEventListener('hashchange', close);
    layer.classList.add('leaving');
    panel.style.transform = '';
    const done = () => { layer.remove(); };
    panel.addEventListener('animationend', done, { once: true });
    setTimeout(done, 400);
    for (const el of stilled) el.inert = false;
    previous?.focus?.({ preventScroll: true });
  };
  const keys = (e) => { if (e.key === 'Escape') close(); };
  const choice = (item) => h('button', {
    class: ['sheet-item', item.danger && 'danger'], role: item.selected != null ? (item.checkbox ? 'menuitemcheckbox' : 'menuitemradio') : 'menuitem',
    'aria-checked': item.selected != null ? String(!!item.selected) : null,
    on: { click: () => { if (closing || early()) return; close(); item.run?.(); } },
  }, item.symbol ? icon(item.symbol) : null,
  h('span', { class: 'grow' }, item.label, item.sub ? h('span', { class: 'sub' }, item.sub) : null),
  item.selected ? h('span', { class: 'tick' }, icon('check', { size: 20 })) : null);
  // The choices form a menu, as screen readers know it; a heading starts a group of its own (Audio, Sous-titres),
  // named by it, which is the set its radio choices belong to.
  const groups = [];
  for (const item of items) {
    if (item.heading) groups.push({ heading: item.heading, items: [] });
    else { if (!groups.length) groups.push({ heading: '', items: [] }); groups.at(-1).items.push(item); }
  }
  const menu = items.length ? h('div', { role: 'menu', 'aria-label': title || tr('Choix') }, groups.map((group) => (group.heading
    ? h('div', { role: 'group', 'aria-label': group.heading }, h('div', { class: 'sheet-heading', 'aria-hidden': 'true' }, group.heading), group.items.map(choice))
    : group.items.map(choice)))) : null;
  const panel = h('div', { class: 'sheet', role: 'dialog', 'aria-modal': 'true', 'aria-label': title || tr('Choix'), tabIndex: -1 },
    h('div', { class: 'sheet-grip' }),
    title ? h('div', { class: 'sheet-title', 'aria-hidden': 'true' }, title) : null,
    body,
    menu);
  // A right click beside the menu closes it, without the browser's own menu.
  layer.append(h('div', { class: 'sheet-backdrop', on: { click: () => { if (!early()) close(); }, contextmenu: (e) => { e.preventDefault(); close(); } } }), panel);
  if (!anchored) dragToClose(panel, close);
  overlayHost().append(layer);
  // Its list scrolls when the screen is short (a phone on its side): its foot fades while more is below.
  const more = () => panel.classList.toggle('more', panel.scrollHeight - panel.scrollTop - panel.clientHeight > 4);
  panel.addEventListener('scroll', more, { passive: true });
  // The menu by the pointer, inside the window: to its left or above it when there is no room, placed again when
  // what it says arrives (a summary).
  const place = () => {
    if (anchored) {
      const { width, height } = panel.getBoundingClientRect();
      const x = at.x + width > innerWidth - 8 ? Math.max(8, at.x - width) : at.x;
      const y = at.y + height > innerHeight - 8 ? Math.max(8, Math.min(at.y - height, innerHeight - 8 - height)) : at.y;
      panel.style.left = `${x}px`; panel.style.top = `${y}px`;
      panel.style.transformOrigin = `${at.x - x}px ${at.y - y}px`;
    }
    more();
  };
  const resized = 'ResizeObserver' in window ? new ResizeObserver(place) : null;
  resized?.observe(panel);
  place();
  // Tab and a screen reader stay in the sheet while it is open: what is under it cannot be reached (aria-modal alone
  // is not enough for Tab), and takes them back once it closes.
  const stilled = [...overlayHost().children].filter((el) => el !== layer && !el.inert);
  for (const el of stilled) el.inert = true;
  addEventListener('keydown', keys);
  addEventListener('hashchange', close);
  // The panel takes the focus, so that screen readers and Tab start there, without a ring on touch screens.
  if (focus) focus.focus({ preventScroll: true });
  else requestAnimationFrame(() => panel.focus({ preventScroll: true }));
  return close;
}

/** A sheet follows the finger down from its top (or from anywhere once its list is at the top), and closes past a third. */
function dragToClose(panel, close) {
  let start = null, dy = 0, lastY = 0, lastT = 0, speed = 0;
  panel.addEventListener('touchstart', (e) => {
    if (e.touches.length !== 1) return;
    start = { y: e.touches[0].clientY, scrolled: panel.scrollTop > 0, dragging: false };
    dy = 0; lastY = start.y; lastT = performance.now(); speed = 0;
  }, { passive: true });
  panel.addEventListener('touchmove', (e) => {
    if (!start || start.scrolled) return;
    const y = e.touches[0].clientY;
    dy = y - start.y;
    // From the first move down, before iOS starts the list's own bounce.
    if (dy > 0 && e.cancelable) e.preventDefault();
    if (!start.dragging) {
      if (dy <= 4) { if (dy < -4) start = null; return; }
      start.dragging = true;
      panel.classList.add('dragging');
    }
    const now = performance.now();
    speed = (y - lastY) / Math.max(1, now - lastT); lastY = y; lastT = now;
    panel.style.transform = `translate3d(0, ${Math.max(0, dy)}px, 0)`;
  }, { passive: false });
  const end = () => {
    if (!start?.dragging) { start = null; return; }
    start = null;
    panel.classList.remove('dragging');
    if (dy > panel.offsetHeight / 3 || (speed > 0.5 && dy > 30)) { panel.style.setProperty('--from', `${Math.max(0, dy)}px`); close(); }
    else panel.style.transform = '';
  };
  panel.addEventListener('touchend', end, { passive: true });
  panel.addEventListener('touchcancel', end, { passive: true });
}

/**
 * Long press (or right click) on a card: play, open, watched, favourite, and out of Continuer à regarder. inTitle:
 * the card is on its own title's page, which the menu does not offer to open again; collection: the collection whose
 * page shows the card ({ Id, Name }), which the menu offers to take it out of.
 */
export function withActions(card, item, { resumeRow = false, inTitle = false, collection = null } = {}) {
  let timer = 0, lift = 0, startX = 0, startY = 0, fired = false;
  const stop = () => { clearTimeout(timer); clearTimeout(lift); card.classList.remove('holding'); };
  const open = (at = null) => { stop(); if (fired) return; fired = true; itemMenu(card.item ?? item, { resumeRow, inTitle, collection, held: !at, at }); };
  card.addEventListener('touchstart', (e) => {
    fired = false; startX = e.touches[0].clientX; startY = e.touches[0].clientY;
    lift = setTimeout(() => card.classList.add('holding'), 180);
    timer = setTimeout(open, 480);
  }, { passive: true });
  card.addEventListener('touchmove', (e) => {
    if (Math.abs(e.touches[0].clientX - startX) > 10 || Math.abs(e.touches[0].clientY - startY) > 10) stop();
  }, { passive: true });
  // After a long press, WebKit clicks where the touch began once the finger lifts, which would now hit the menu:
  // cancelling the touch's end stops that click.
  card.addEventListener('touchend', (e) => { stop(); if (fired && e.cancelable) e.preventDefault(); }, { passive: false });
  card.addEventListener('touchcancel', stop);
  card.addEventListener('click', (e) => { if (fired) { e.preventDefault(); fired = false; } });
  // A right click opens the menu where it was; the menu key of a keyboard, at the card.
  card.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (e.pointerType === 'touch') { open(); return; }
    const box = card.getBoundingClientRect();
    open(e.clientX || e.clientY ? { x: e.clientX, y: e.clientY } : { x: box.left + 12, y: box.top + 12 });
  });
  // A mouse or a pen: only the click after a long touch is swallowed, never the next real one.
  card.addEventListener('pointerdown', (e) => { if (e.pointerType !== 'touch') fired = false; });
}

/** The player on `href`, unless something asks first (a watch group about to change its title, together.js). */
export function openPlayer(href) {
  if (dispatchEvent(new CustomEvent('mira-play', { cancelable: true, detail: href }))) location.hash = href;
}

/**
 * The menu of a card. « Ajouter à une collection… » and « Retirer de la collection » for an account Jellyfin lets
 * manage collections: asked once per session, the first menu waits for it.
 */
export async function itemMenu(item, { resumeRow = false, inTitle = false, collection = null, held = false, at = null } = {}) {
  const rights = holds(item) || collection ? await api.collectionRights().catch(() => null) : null;
  // « Chercher une meilleure version », an administrator's (asked once a session, as « Corriger »).
  const admin = item.Type === 'Movie' || item.Type === 'Episode' ? await api.isAdministrator().catch(() => false) : false;
  const name = item.Type === 'Episode' ? `${item.SeriesName} · ${episodeCode(item)}` : item.Name;
  const played = !!item.UserData?.Played, favorite = !!item.UserData?.IsFavorite;
  const items = [];
  // A collection plays nothing by itself: its films do, from its page.
  const plays = item.Type !== 'Series' && item.Type !== 'BoxSet';
  if (plays) items.push({ label: progress(item) > 0 ? tr('Reprendre') : tr('Lecture'), symbol: 'play', run: () => openPlayer(playHref(item)) });
  // A title under way starts again from its beginning, wherever its card is (Windows has it on a right click).
  if (plays && progress(item) > 0) items.push({ label: tr('Lire depuis le début'), symbol: 'back10', run: () => openPlayer(playHref(item, true)) });
  const see = { Episode: tr('Voir la série'), BoxSet: tr('Voir la collection') }[item.Type] ?? tr('Voir la fiche');
  if (!inTitle) items.push({ label: see, symbol: 'info', run: () => { location.hash = titleHref(item); } });
  items.push({ label: played ? tr('Marquer comme non vu') : tr('Marquer comme vu'), symbol: 'check', run: () => setPlayed(item, !played) });
  items.push({ label: favorite ? tr('Retirer des favoris') : tr('Ajouter aux favoris'), symbol: favorite ? 'heart-fill' : 'heart', run: () => setFavorite(item, !favorite) });
  if (resumeRow) items.push({ label: tr('Retirer de Continuer à regarder'), symbol: 'close', run: () => hideFromResume(item) });
  if (rights?.manage && holds(item)) items.push({ label: tr('Ajouter à une collection…'), symbol: 'stack', run: () => collectionSheet(item) });
  if (rights?.manage && collection) items.push({ label: tr('Retirer de la collection'), symbol: 'close', run: () => takeOut(item, collection) });
  if (admin) items.push(upgradeEntry(item));
  sheet({ title: name, items, held, at, body: about(item) });
}

/**
 * What the menu of a film or an episode says above its actions: the episode's name, the length, the picture of its
 * file and its whole summary, which a card leaves out and the episode list cuts at two lines. Only here, asked for:
 * nothing unfolds in a list, where a summary would give away what comes. What the card does not know yet (summary,
 * file) is asked for and comes in place.
 */
function about(item) {
  if (item.Type !== 'Movie' && item.Type !== 'Episode') return null;
  const facts = (x) => [x.Type === 'Movie' ? x.ProductionYear : '', x.RunTimeTicks ? duration(seconds(x.RunTimeTicks)) : '', remaining(x),
    qualityText(x.MediaSources?.[0])].filter(Boolean).join(' · ');
  const meta = h('div', { class: 'sheet-about-meta' }, facts(item));
  let text = item.Overview ? h('p', { class: 'sheet-about-text' }, plainOverview(item.Overview, { flat: true }))
    : h('div', { class: 'sheet-about-text', 'aria-hidden': 'true' }, h('div', { class: 'skeleton line', style: { width: '100%', marginTop: '0' } }), h('div', { class: 'skeleton line', style: { marginTop: '0' } }));
  const box = h('div', { class: 'sheet-about' },
    item.Type === 'Episode' && item.Name ? h('div', { class: 'sheet-about-name' }, `${item.IndexNumber != null ? `${item.IndexNumber}. ` : ''}${item.Name}`) : null,
    meta, text);
  if (!item.MediaSources?.[0]) {
    api.item(item.Id).then((full) => {
      // Where the title is now stays the card's: what the menu acts on.
      meta.textContent = facts({ ...full, UserData: item.UserData ?? full.UserData });
      const summary = full.Overview ? h('p', { class: 'sheet-about-text' }, plainOverview(full.Overview, { flat: true })) : null;
      if (summary) { text.replaceWith(summary); text = summary; } else if (!item.Overview) text.remove();
    }, () => { if (!item.Overview) text.remove(); });
  }
  return box;
}

/**
 * Watched or not, favourite or not: the item says so at once (a screen redrawn right after the call shows it), and
 * goes back as it was if Jellyfin refuses. The other screens hear of it once Jellyfin has it. quiet: the button
 * touched shows the change itself, without a notice. Resolves to true once Jellyfin has it.
 */
export async function setPlayed(item, played, { quiet = false } = {}) {
  const before = item.UserData;
  // Jellyfin forgets the resume point both ways, watched or not.
  item.UserData = { ...before, Played: played, PlaybackPositionTicks: 0 };
  try {
    await api.setPlayed(item.Id, played);
    changed(item, { played });
    if (!quiet) toast(played ? tr('Marqué comme vu.') : tr('Marqué comme non vu.'));
    return true;
  } catch (error) {
    // Only what this call changed goes back, and only if nothing changed it since.
    if (item.UserData?.Played === played) item.UserData = { ...item.UserData, Played: before?.Played, PlaybackPositionTicks: before?.PlaybackPositionTicks };
    toast(error.message);
    return false;
  }
}

export async function setFavorite(item, favorite, { quiet = false } = {}) {
  const before = item.UserData;
  item.UserData = { ...before, IsFavorite: favorite };
  try {
    await api.setFavorite(item.Id, favorite);
    changed(item, { favorite });
    if (!quiet) toast(favorite ? tr('Ajouté à tes favoris.') : tr('Retiré de tes favoris.'));
    return true;
  } catch (error) {
    if (item.UserData?.IsFavorite === favorite) item.UserData = { ...item.UserData, IsFavorite: before?.IsFavorite };
    toast(error.message);
    return false;
  }
}

/**
 * A summary in plain text (Jellyfin's HTML read: overview.js) cut at four lines, with « Plus » once it really is cut (measured whenever its box changes size, not once:
 * a screen still sliding in has no size yet). A tap on the text opens it too.
 */
export function overview(text, className = 'overview') {
  text = plainOverview(text);
  if (!text) return null;
  const body = h('p', { class: [className, 'clamp-4'] }, text);
  const open = () => { if (!body.classList.contains('clamp-4')) return; body.classList.remove('clamp-4'); more.remove(); watcher?.disconnect(); };
  const more = h('button', { class: 'more-link', hidden: true, on: { click: open } }, tr('Plus'));
  const fits = () => { more.hidden = body.scrollHeight <= body.clientHeight + 2; };
  const watcher = typeof ResizeObserver === 'function' ? new ResizeObserver(fits) : null;
  if (watcher) watcher.observe(body); else requestAnimationFrame(fits);
  body.addEventListener('click', open);
  return h('div', { class: 'overview-box' }, body, more);
}

/**
 * Takes a film or series out of Continuer à regarder until it is played again, as on Windows: its resume point
 * goes on Jellyfin too, so no other device offers it. « Annuler » puts both back.
 */
export async function hideFromResume(item) {
  const group = item.SeriesId ?? item.Id;
  const position = item.UserData?.PlaybackPositionTicks ?? 0;
  const hidden = { ...settings.get('hiddenResume'), [group]: new Date().toISOString() };
  settings.set('hiddenResume', Object.fromEntries(Object.entries(hidden).slice(-200)));
  if (position > 0) await api.setPosition(item.Id, 0).catch(() => {});
  changed(item, { hidden: true });
  toast(tr('Retiré de Continuer à regarder.'), {
    action: {
      label: trc('undo', 'Annuler'),
      run: async () => {
        const back = { ...settings.get('hiddenResume') }; delete back[group];
        settings.set('hiddenResume', back);
        if (position > 0) await api.setPosition(item.Id, position).catch(() => {});
        changed(item, { hidden: false });
      },
    },
  });
}
