// An actor's or director's titles in the library, from a title page's cast.
import { api } from '../api.js';
import { h, icon, clear, plural } from '../dom.js';
import { artFor, picture } from '../images.js';
import { posterCard, skeletonGrid, errorState, emptyState, changes, overview, toast } from '../components.js';
import { goBack } from '../app.js';

export function create({ id }) {
  const el = h('div', { class: 'view person-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': 'Retour', on: { click: () => goBack('#/') } }, icon('back'));
  let grid = null, count = null, stale = false, shown = new Set();

  /** quiet: the page stays as it is while Jellyfin answers, and only its titles change (a refresh, a title marked). */
  async function load(quiet = false) {
    if (!quiet || !grid) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(9)));
    try {
      const [person, titles] = await Promise.all([api.item(id), api.browse({ person: id, sort: 'year', limit: 200 })]);
      const items = titles?.Items ?? [];
      shown = new Set(items.map((x) => x.Id));
      document.title = `${person.Name} · Mira`;
      stale = false;
      const sub = items.length ? `${plural(items.length, 'titre', 'titres')} dans ta bibliothèque` : '';
      if (quiet && grid?.isConnected && items.length) {
        grid.replaceChildren(...items.map((x) => posterCard(x)));
        count.textContent = sub;
        return;
      }
      count = h('p', { class: 'page-sub' }, sub);
      grid = items.length ? h('div', { class: 'grid' }, items.map((x) => posterCard(x))) : null;
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('div', { class: 'profile', style: { padding: '0 0 16px' } },
          h('div', { class: 'avatar', style: { width: '88px', height: '88px' } }, picture(artFor(person, 'square'), { kind: 'square', width: 88, label: person.Name, eager: true })),
          h('div', {}, h('h1', { class: 'page-title' }, person.Name), count)),
        person.Overview ? h('div', { style: { paddingBottom: '20px' } }, overview(person.Overview)) : null,
        grid ?? emptyState({ symbol: 'person', title: 'Aucun titre', text: 'Aucun film ni série de ta bibliothèque ne cite cette personne.' })));
    } catch (error) {
      if (quiet && grid?.isConnected) toast(error.message);
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
    el, title: 'Personne', keep: true,
    enter() { if (stale) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); },
  };
}
