// A saga (a Jellyfin collection, « Harry Potter - Saga »): its titles in the order they came out, from the search or
// from the page of one of its films. It plays nothing by itself: its films do, from their page.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { posterCard, skeletonGrid, errorState, emptyState, changes, overview, toast } from '../components.js';
import { goBack } from '../app.js';

/** « 3 films · 2001 – 2004 »: films when all are, titles otherwise, and the years they span. */
function about(items) {
  const years = items.map((x) => x.ProductionYear).filter(Boolean);
  const first = Math.min(...years), last = Math.max(...years);
  const count = items.every((x) => x.Type === 'Movie') ? trn(items.length, '{n} film', '{n} films') : trn(items.length, '{n} titre', '{n} titres');
  return [count, years.length ? (first === last ? String(first) : `${first} – ${last}`) : ''].filter(Boolean).join(' · ');
}

export function create({ id }) {
  const el = h('div', { class: 'view saga-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/') } }, icon('back'));
  let grid = null, count = null, stale = false, shown = new Set([id]);

  /** quiet: the page stays as it is while Jellyfin answers, and only its titles change (a refresh, a title marked). */
  async function load(quiet = false) {
    if (!quiet || !grid) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(6)));
    try {
      const [saga, items] = await Promise.all([api.item(id), api.sagaTitles(id)]);
      shown = new Set([id, ...items.map((x) => x.Id)]);
      document.title = `${saga.Name} · Mira`;
      stale = false;
      if (quiet && grid?.isConnected && items.length) {
        grid.replaceChildren(...items.map((x) => posterCard(x)));
        count.textContent = about(items);
        return;
      }
      count = h('p', { class: 'page-sub' }, items.length ? about(items) : '');
      grid = items.length ? h('div', { class: 'grid' }, items.map((x) => posterCard(x))) : null;
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('header', { style: { paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, saga.Name), count),
        saga.Overview ? h('div', { style: { paddingBottom: '20px' } }, overview(saga.Overview)) : null,
        grid ?? emptyState({ symbol: 'films', title: tr('Aucun titre'), text: tr('Cette saga n’a aucun film de ta bibliothèque.') })));
    } catch (error) {
      if (quiet && grid?.isConnected) toast(error.message);
      else clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
    }
  }

  // A title marked watched elsewhere (or the whole saga, from its card's menu) shows it here when the page comes back.
  const onChange = (e) => {
    if (!shown.has(e.detail.item.Id)) return;
    stale = true;
    if (el.isConnected) load(true);
  };
  changes.addEventListener('item', onChange);
  load();
  return {
    el, title: tr('Saga'), keep: true,
    enter() { if (stale) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); },
  };
}
