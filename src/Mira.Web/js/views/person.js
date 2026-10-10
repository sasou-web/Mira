// An actor's or director's titles in the library, from a title page's cast.
import { api } from '../api.js';
import { h, icon, clear, plural } from '../dom.js';
import { artFor, picture } from '../images.js';
import { posterCard, skeletonGrid, errorState, emptyState } from '../components.js';
import { goBack } from '../app.js';

export function create({ id }) {
  const el = h('div', { class: 'view person-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': 'Retour', on: { click: () => goBack('#/') } }, icon('back'));

  async function load() {
    clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 72px)' } }, skeletonGrid(9)));
    try {
      const [person, titles] = await Promise.all([api.item(id), api.browse({ person: id, sort: 'year', limit: 200 })]);
      const items = titles?.Items ?? [];
      document.title = `${person.Name} · Mira`;
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)' } },
        h('div', { class: 'profile', style: { padding: '0 0 24px' } },
          h('div', { class: 'avatar', style: { width: '88px', height: '88px' } }, picture(artFor(person, 'square'), { kind: 'square', width: 88, label: person.Name, eager: true })),
          h('div', {}, h('h1', { class: 'page-title' }, person.Name), h('p', { class: 'page-sub' }, items.length ? `${plural(items.length, 'titre', 'titres')} dans ta bibliothèque` : ''))),
        items.length
          ? h('div', { class: 'grid' }, items.map((x) => posterCard(x)))
          : emptyState({ symbol: 'person', title: 'Aucun titre', text: 'Aucun film ni série de ta bibliothèque ne cite cette personne.' })));
    } catch (error) {
      clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, load)));
    }
  }
  load();
  return { el, title: 'Personne', keep: true, refresh: load };
}
