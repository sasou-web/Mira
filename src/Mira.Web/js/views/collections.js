// Every collection of the library, from the Collections row of Home (« Tout voir »): the sagas Jellyfin makes
// (« Harry Potter - Saga ») and those made by hand (« Soirées »), by name. For an account that may manage collections,
// « Nouvelle collection » makes an empty one, filled from the menus of its titles (collection-sheet.js).
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { posterCard, skeletonGrid, errorState, emptyState, toast } from '../components.js';
import { collectionChanges, newCollection } from '../collection-sheet.js';
import { sortedCollections } from '../collections.js';
import { goBack } from '../app.js';

export function create() {
  const el = h('div', { class: 'view collections-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/') } }, icon('back'));
  // Shown once Jellyfin says this account may make collections.
  const add = h('button', { class: 'btn small', hidden: true, on: { click: () => newCollection() } }, icon('plus', { size: 16 }), tr('Nouvelle collection'));
  api.collectionRights().then((can) => { add.hidden = !can.manage; }, () => {});
  let grid = null, count = null, stale = false;

  /** quiet: the page stays as it is while Jellyfin answers, and only its collections change. */
  async function load(quiet = false) {
    if (!quiet || !grid) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(6)));
    try {
      const items = sortedCollections(await api.collections());
      stale = false;
      const sub = items.length ? trn(items.length, '{n} collection', '{n} collections') : '';
      if (quiet && grid?.isConnected && items.length) {
        grid.replaceChildren(...items.map((x) => posterCard(x)));
        count.textContent = sub;
        return;
      }
      count = h('p', { class: 'page-sub' }, sub);
      grid = items.length ? h('div', { class: 'grid' }, items.map((x) => posterCard(x))) : null;
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('header', { class: 'collections-head' }, h('div', {}, h('h1', { class: 'page-title' }, tr('Collections')), count), add),
        grid ?? emptyState({ symbol: 'stack', title: tr('Aucune collection'), text: tr('Rassemble des films et des séries depuis leur menu (appui long ou clic droit) : « Ajouter à une collection… ».') })));
    } catch (error) {
      if (quiet && grid?.isConnected) toast(error.message, { tone: 'error' });
      else clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
    }
  }

  const onCollection = () => { stale = true; if (el.isConnected) load(true); };
  collectionChanges.addEventListener('change', onCollection);
  document.title = `${tr('Collections')} · Mira`;
  load();
  return {
    el, title: tr('Collections'), keep: true,
    enter() { if (stale) load(true); },
    refresh: () => load(true),
    dispose() { collectionChanges.removeEventListener('change', onCollection); },
  };
}
