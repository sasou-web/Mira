// A studio's titles in the library (« Studio Ghibli », « MAPPA »), from a title page or a search.
import { api, GRID_FIELDS } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { posterCard, skeletonGrid, errorState, emptyState, changes, toast } from '../components.js';
import { goBack } from '../app.js';

export function create({ id }) {
  const el = h('div', { class: 'view studio-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/') } }, icon('back'));
  let grid = null, count = null, stale = false, shown = new Set();

  /** quiet: the page stays as it is while Jellyfin answers, and only its titles change (a refresh, a title marked). */
  async function load(quiet = false) {
    if (!quiet || !grid) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(9)));
    try {
      const [studio, titles] = await Promise.all([api.item(id), api.browse({ studio: id, sort: 'year', limit: 200, fields: GRID_FIELDS })]);
      const items = titles?.Items ?? [];
      shown = new Set(items.map((x) => x.Id));
      document.title = `${studio.Name} · Mira`;
      stale = false;
      const sub = items.length ? trn(items.length, '{n} titre dans ta bibliothèque', '{n} titres dans ta bibliothèque') : '';
      if (quiet && grid?.isConnected && items.length) {
        grid.replaceChildren(...items.map((x) => posterCard(x)));
        count.textContent = sub;
        return;
      }
      count = h('p', { class: 'page-sub' }, sub);
      grid = items.length ? h('div', { class: 'grid' }, items.map((x) => posterCard(x))) : null;
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('div', { style: { padding: '0 0 16px' } }, h('p', { class: 'label' }, tr('Studio')), h('h1', { class: 'page-title' }, studio.Name), count),
        grid ?? emptyState({ symbol: 'films', title: tr('Aucun titre'), text: tr('Aucun film ni série de ta bibliothèque ne vient de ce studio.') })));
    } catch (error) {
      if (quiet && grid?.isConnected) toast(error.message, { tone: 'error' });
      else clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
    }
  }

  // A title marked watched elsewhere shows it here when the page comes back.
  const onChange = (e) => {
    const changed = e.detail.item;
    if (!shown.has(changed.Id) && !shown.has(changed.SeriesId)) return;
    stale = true;
    if (el.isConnected) load(true);
  };
  changes.addEventListener('item', onChange);
  load();
  return {
    el, title: tr('Studio'), keep: true,
    enter() { if (stale) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); },
  };
}
