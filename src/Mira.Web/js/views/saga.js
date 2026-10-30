// A collection: a saga Jellyfin made (« Harry Potter - Saga ») or one made by hand (« Soirées »), its titles in the
// order they came out, from Home, the search or the page of one of its films. It plays nothing by itself: its films
// do, from their page. For an account that may manage collections (collection-sheet.js): « … » renames it (an
// administrator) or deletes it, a card's menu takes its title out.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { posterCard, skeletonGrid, errorState, emptyState, changes, overview, toast } from '../components.js';
import { collectionChanges, collectionActions } from '../collection-sheet.js';
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
  let grid = null, count = null, title = null, stale = false, shown = new Set([id]), collection = null;
  // Shown once Jellyfin says this account may manage collections.
  const more = h('button', {
    class: 'round floating-more', hidden: true, 'aria-label': tr('Gérer la collection'), 'aria-haspopup': 'dialog',
    on: { click: () => collection && collectionActions(collection, { renamed, gone: () => goBack('#/') }) },
  }, icon('more'));
  api.collectionRights().then((can) => { more.hidden = !can.manage; }, () => {});
  /** A card whose menu also takes its title out of this collection. */
  const card = (item) => posterCard(item, { collection: { Id: id, Name: collection?.Name ?? '' } });
  function renamed(name) {
    if (collection) collection = { ...collection, Name: name };
    if (title) title.textContent = name;
    document.title = `${name} · Mira`;
  }

  /** quiet: the page stays as it is while Jellyfin answers, and only its titles change (a refresh, a title marked). */
  async function load(quiet = false) {
    if (!quiet || !grid) clear(el).append(back, more, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(6)));
    try {
      const [saga, items] = await Promise.all([api.item(id), api.sagaTitles(id)]);
      collection = { Id: saga.Id, Name: saga.Name };
      shown = new Set([id, ...items.map((x) => x.Id)]);
      document.title = `${saga.Name} · Mira`;
      stale = false;
      if (quiet && grid?.isConnected && items.length) {
        grid.replaceChildren(...items.map(card));
        count.textContent = about(items);
        title.textContent = saga.Name;
        return;
      }
      count = h('p', { class: 'page-sub' }, items.length ? about(items) : '');
      title = h('h1', { class: 'page-title' }, saga.Name);
      grid = items.length ? h('div', { class: 'grid' }, items.map(card)) : null;
      clear(el).append(back, more, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('header', { style: { paddingBottom: '16px' } }, title, count),
        saga.Overview ? h('div', { style: { paddingBottom: '20px' } }, overview(saga.Overview)) : null,
        grid ?? emptyState({ symbol: 'stack', title: tr('Aucun titre'), text: tr('Cette collection est vide. Ajoute-y un film ou une série depuis son menu (appui long ou clic droit).') })));
    } catch (error) {
      if (quiet && grid?.isConnected) toast(error.message, { tone: 'error' });
      else clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
    }
  }

  // A title marked watched elsewhere (or the whole saga, from its card's menu) shows it here when the page comes back.
  const onChange = (e) => {
    if (!shown.has(e.detail.item.Id)) return;
    stale = true;
    if (el.isConnected) load(true);
  };
  // A title put in or taken out, the collection renamed: read again; deleted, the page has nothing left to show.
  const onCollection = (e) => {
    if (e.detail.collection !== id || e.detail.deleted) return;
    stale = true;
    if (el.isConnected) load(true);
  };
  changes.addEventListener('item', onChange);
  collectionChanges.addEventListener('change', onCollection);
  load();
  return {
    el, title: tr('Collection'), keep: true,
    enter() { if (stale) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); collectionChanges.removeEventListener('change', onCollection); },
  };
}
