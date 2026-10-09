// The pieces every screen shares: cards, rows, sheets, notices and the loading, empty and error states.
import { api } from './api.js';
import { h, icon, progress, remaining, episodeCode, plural, clear } from './dom.js';
import { artFor, picture } from './images.js';
import { settings } from './session.js';

/** Item changes (watched, favourite, hidden) that every screen showing the item takes into account. */
export const changes = new EventTarget();
export function changed(item, detail = {}) { changes.dispatchEvent(new CustomEvent('item', { detail: { item, ...detail } })); }

export const titleHref = (item) => item.Type === 'Episode' && item.SeriesId
  ? `#/titre/${item.SeriesId}?episode=${item.Id}`
  : `#/titre/${item.Id}`;
export const playHref = (item, fromStart = false) => `#/lecture/${item.Id}${fromStart ? '?debut=1' : ''}`;

/** « 2023 », « 3 saisons », « S1 · É3 · reste 12 min ». */
export function subtitle(item) {
  if (item.Type === 'Episode') return [episodeCode(item), remaining(item)].filter(Boolean).join(' · ');
  if (item.Type === 'Series') {
    const seasons = item.ChildCount;
    return seasons ? plural(seasons, 'saison', 'saisons') : (item.ProductionYear ? String(item.ProductionYear) : 'Série');
  }
  return [item.ProductionYear, remaining(item)].filter(Boolean).join(' · ');
}

function marks(item, box) {
  const done = progress(item);
  if (done > 0) box.append(h('div', { class: 'progress' }, h('i', { style: { width: `${(done * 100).toFixed(1)}%` } })));
  else if (item.UserData?.Played && item.Type !== 'Series') box.append(h('div', { class: 'badge-watched', title: 'Vu' }, icon('check')));
  else if (item.Type === 'Series' && item.UserData?.Played) box.append(h('div', { class: 'badge-watched', title: 'Toute la série est vue' }, icon('check')));
}

/** A poster with its title: opens the title page; a long press offers the item's actions. */
export function posterCard(item, { width = 156 } = {}) {
  const name = item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name;
  const art = picture(artFor(item, 'poster'), { kind: 'poster', width, label: name });
  marks(item, art);
  const card = h('a', { class: 'card', href: titleHref(item), 'aria-label': name },
    art, h('div', { class: 'card-title clamp-1' }, name), h('div', { class: 'card-sub clamp-1' }, subtitle(item)));
  withActions(card, item);
  return card;
}

/** A 16:9 card for Continuer à regarder: a tap plays from where it was left. */
export function wideCard(item, { width = 340, play = true } = {}) {
  const series = item.Type === 'Episode';
  const art = picture(artFor(item, 'wide'), { kind: 'wide', width, label: series ? item.SeriesName : item.Name });
  marks(item, art);
  const card = h('a', { class: 'card wide-card', href: play ? playHref(item) : titleHref(item), 'aria-label': `${play ? 'Lire ' : ''}${series ? item.SeriesName : item.Name}` },
    art,
    h('div', { class: 'card-title clamp-1' }, series ? item.SeriesName : item.Name),
    h('div', { class: 'card-sub clamp-1' }, series ? [episodeCode(item), item.Name].filter(Boolean).join(' · ') : subtitle(item)));
  withActions(card, item, { resumeRow: play });
  return card;
}

export function personCard(person) {
  const card = h('a', { class: 'card person', href: `#/personne/${person.Id}`, 'aria-label': person.Name },
    picture(artFor(person, 'square'), { kind: 'square', width: 84, label: person.Name }),
    h('div', { class: 'card-title clamp-2' }, person.Name),
    person.Role ? h('div', { class: 'card-sub clamp-1' }, person.Role) : null);
  return card;
}

/** A titled horizontal row; nothing when there is nothing to show. */
export function row(title, items, card, { wide = false, more = '', people = false } = {}) {
  if (!items?.length) return null;
  return h('section', { class: 'section' },
    h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, title), more ? h('a', { href: more }, 'Tout voir') : null),
    h('div', { class: ['row', wide && 'wide', people && 'people'], role: 'list' }, items.map((item) => {
      const el = card(item);
      el.setAttribute('role', 'listitem');
      return el;
    })));
}

export function skeletonRow({ wide = false, count = 6, title = true } = {}) {
  return h('section', { class: 'section', 'aria-hidden': 'true' },
    title ? h('div', { class: 'section-head' }, h('div', { class: 'skeleton', style: { width: '160px', height: '20px' } })) : null,
    h('div', { class: ['row', wide && 'wide'] }, Array.from({ length: count }, () =>
      h('div', {}, h('div', { class: ['skeleton', 'art', wide ? 'wide' : 'poster'] }), h('div', { class: 'skeleton line' })))));
}

export function skeletonGrid(count = 18) {
  return h('div', { class: 'grid', 'aria-hidden': 'true' }, Array.from({ length: count }, () =>
    h('div', {}, h('div', { class: 'skeleton art poster' }), h('div', { class: 'skeleton line' }))));
}

export function emptyState({ symbol = 'films', title, text = '', action = null }) {
  return h('div', { class: 'state' }, icon(symbol), h('h2', {}, title), text ? h('p', {}, text) : null,
    action ? h('a', { class: 'btn small', href: action.href, on: action.run ? { click: (e) => { e.preventDefault(); action.run(); } } : undefined }, action.label) : null);
}

export function errorState(error, retry) {
  return h('div', { class: 'state', role: 'alert' }, icon(error?.offline ? 'offline' : 'warning'),
    h('h2', {}, error?.offline ? 'Pas de connexion au serveur' : 'Ça n’a pas marché'),
    h('p', {}, error?.message ?? 'Une erreur inattendue est survenue.'),
    retry ? h('button', { class: 'btn small', on: { click: retry } }, icon('refresh', { size: 16 }), 'Réessayer') : null);
}

export function spinner(label = 'Chargement') { return h('div', { class: 'state', role: 'status' }, h('div', { class: 'spinner' }), h('span', { class: 'sr' }, label)); }

/** A notice at the bottom of the screen, with an optional action (« Annuler »). */
export function toast(text, { action = null, duration = 4000 } = {}) {
  let layer = document.querySelector('.toast-layer');
  if (!layer) { layer = h('div', { class: 'toast-layer', role: 'status', 'aria-live': 'polite' }); document.body.append(layer); }
  clear(layer);
  const close = () => note.remove();
  const note = h('div', { class: 'toast' }, h('span', {}, text),
    action ? h('button', { on: { click: () => { close(); action.run(); } } }, action.label) : null);
  layer.append(note);
  setTimeout(close, action ? Math.max(duration, 6000) : duration);
  return close;
}

/**
 * A sheet of choices from the bottom of the screen. items: { label, sub, symbol, selected, danger, run }.
 * Closes on a choice, on the backdrop, or with Escape.
 */
export function sheet({ title = '', items = [], body = null }) {
  const previous = document.activeElement;
  const layer = h('div', { class: 'sheet-layer' });
  const close = () => {
    layer.remove(); removeEventListener('keydown', keys); removeEventListener('hashchange', close);
    previous?.focus?.({ preventScroll: true });
  };
  const keys = (e) => { if (e.key === 'Escape') close(); };
  const panel = h('div', { class: 'sheet', role: 'dialog', 'aria-modal': 'true', 'aria-label': title || 'Choix', tabIndex: -1 },
    h('div', { class: 'sheet-grip' }),
    title ? h('div', { class: 'sheet-title' }, title) : null,
    body,
    items.map((item) => h('button', {
      class: ['sheet-item', item.danger && 'danger'], role: item.selected != null ? 'menuitemradio' : 'menuitem',
      'aria-checked': item.selected != null ? String(!!item.selected) : null,
      on: { click: () => { close(); item.run?.(); } },
    }, item.symbol ? icon(item.symbol) : null,
    h('span', { class: 'grow' }, item.label, item.sub ? h('span', { class: 'sub' }, item.sub) : null),
    item.selected ? h('span', { class: 'tick' }, icon('check', { size: 20 })) : null)));
  layer.append(h('div', { class: 'sheet-backdrop', on: { click: close } }), panel);
  document.body.append(layer);
  addEventListener('keydown', keys);
  addEventListener('hashchange', close);
  // The panel takes the focus, so that screen readers and Tab start there, without a ring on touch screens.
  requestAnimationFrame(() => panel.focus({ preventScroll: true }));
  return close;
}

/** Long press (or right click) on a card: play, open, watched, favourite, and out of Continuer à regarder. */
function withActions(card, item, { resumeRow = false } = {}) {
  let timer = 0, startX = 0, startY = 0, fired = false;
  const open = () => { fired = true; itemMenu(item, { resumeRow }); };
  card.addEventListener('touchstart', (e) => {
    fired = false; startX = e.touches[0].clientX; startY = e.touches[0].clientY;
    timer = setTimeout(open, 480);
  }, { passive: true });
  card.addEventListener('touchmove', (e) => {
    if (Math.abs(e.touches[0].clientX - startX) > 10 || Math.abs(e.touches[0].clientY - startY) > 10) clearTimeout(timer);
  }, { passive: true });
  card.addEventListener('touchend', () => clearTimeout(timer));
  card.addEventListener('touchcancel', () => clearTimeout(timer));
  card.addEventListener('click', (e) => { if (fired) { e.preventDefault(); fired = false; } });
  card.addEventListener('contextmenu', (e) => { e.preventDefault(); clearTimeout(timer); if (!fired) open(); });
}

export function itemMenu(item, { resumeRow = false } = {}) {
  const name = item.Type === 'Episode' ? `${item.SeriesName} · ${episodeCode(item)}` : item.Name;
  const played = !!item.UserData?.Played, favorite = !!item.UserData?.IsFavorite;
  const items = [];
  if (item.Type !== 'Series') items.push({ label: progress(item) > 0 ? 'Reprendre' : 'Lecture', symbol: 'play', run: () => { location.hash = playHref(item); } });
  items.push({ label: item.Type === 'Episode' ? 'Voir la série' : 'Voir la fiche', symbol: 'info', run: () => { location.hash = titleHref(item); } });
  items.push({ label: played ? 'Marquer comme non vu' : 'Marquer comme vu', symbol: 'check', run: () => setPlayed(item, !played) });
  items.push({ label: favorite ? 'Retirer des favoris' : 'Ajouter aux favoris', symbol: favorite ? 'heart-fill' : 'heart', run: () => setFavorite(item, !favorite) });
  if (resumeRow) items.push({ label: 'Retirer de Continuer à regarder', symbol: 'close', run: () => hideFromResume(item) });
  sheet({ title: name, items });
}

export async function setPlayed(item, played) {
  try {
    await api.setPlayed(item.Id, played);
    item.UserData = { ...item.UserData, Played: played, PlaybackPositionTicks: played ? 0 : item.UserData?.PlaybackPositionTicks };
    changed(item, { played });
    toast(played ? 'Marqué comme vu.' : 'Marqué comme non vu.');
  } catch (error) { toast(error.message); }
}

export async function setFavorite(item, favorite) {
  try {
    await api.setFavorite(item.Id, favorite);
    item.UserData = { ...item.UserData, IsFavorite: favorite };
    changed(item, { favorite });
    toast(favorite ? 'Ajouté à tes favoris.' : 'Retiré de tes favoris.');
  } catch (error) { toast(error.message); }
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
  toast('Retiré de Continuer à regarder.', {
    action: {
      label: 'Annuler',
      run: async () => {
        const back = { ...settings.get('hiddenResume') }; delete back[group];
        settings.set('hiddenResume', back);
        if (position > 0) await api.setPosition(item.Id, position).catch(() => {});
        changed(item, { hidden: false });
      },
    },
  });
}
