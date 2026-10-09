// Search films, series and people as you type; recent searches stay on this phone.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { settings } from '../session.js';
import { posterCard, personCard, row, skeletonGrid, errorState, emptyState } from '../components.js';

export function create({ query }) {
  const input = h('input', {
    class: 'input', type: 'search', placeholder: 'Films, séries, acteurs…', autocomplete: 'off', autocorrect: 'off', autocapitalize: 'none',
    spellcheck: false, enterkeyhint: 'search', 'aria-label': 'Rechercher', value: query.get('q') ?? '',
  });
  const clearButton = h('button', { class: 'round flat clear', 'aria-label': 'Effacer', hidden: !input.value, on: { click: () => { input.value = ''; input.focus(); update(); } } }, icon('close', { size: 20 }));
  const results = h('div', { 'aria-live': 'polite' });
  const el = h('div', { class: 'view search' },
    h('div', { class: 'search-bar' }, h('h1', { class: 'page-title', style: { marginBottom: '12px' } }, 'Recherche'),
      h('form', { class: 'search-box', role: 'search', on: { submit: (e) => { e.preventDefault(); remember(input.value); input.blur(); } } }, icon('search'), input, clearButton)),
    results);
  let timer = 0, controller = null, lastText = null;

  function remember(text) {
    const value = text.trim();
    if (value.length < 2) return;
    settings.set('recentSearches', [value, ...settings.get('recentSearches').filter((x) => x.toLowerCase() !== value.toLowerCase())].slice(0, 8));
  }

  function recent() {
    const list = settings.get('recentSearches');
    if (!list.length) return emptyState({ symbol: 'search', title: 'Que veux-tu regarder ?', text: 'Cherche un film, une série ou un acteur de ta bibliothèque.' });
    return h('div', {}, h('div', { class: 'section-head' }, h('h2', { class: 'label' }, 'Recherches récentes'),
      h('button', { class: 'btn quiet small', on: { click: () => { settings.set('recentSearches', []); clear(results).append(recent()); } } }, 'Effacer')),
    h('div', { class: 'recent' }, list.map((text) => h('button', { on: { click: () => { input.value = text; update(); } } }, icon('history', { size: 20 }), text))));
  }

  async function search(text) {
    controller?.abort();
    controller = new AbortController();
    clear(results).append(h('div', { style: { padding: '0 var(--gutter)' } }, skeletonGrid(6)));
    try {
      const [titles, people] = await Promise.all([
        api.browse({ search: text, limit: 60, sort: 'title', signal: controller.signal }),
        api.persons(text, controller.signal).catch(() => null),
      ]);
      const items = titles?.Items ?? [], persons = (people?.Items ?? []).slice(0, 12);
      clear(results);
      if (!items.length && !persons.length) {
        results.append(emptyState({ symbol: 'search', title: 'Aucun résultat', text: `Rien ne correspond à « ${text} » dans ta bibliothèque.` }));
        return;
      }
      if (persons.length) results.append(row('Personnes', persons, (p) => personCard(p), { people: true }));
      if (items.length) {
        results.append(h('section', { class: 'section' }, h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, 'Films et séries')),
          h('div', { class: 'grid', role: 'list', style: { padding: '0 var(--gutter) 24px' } }, items.map((x) => { const c = posterCard(x); c.setAttribute('role', 'listitem'); c.addEventListener('click', () => remember(text)); return c; }))));
      }
    } catch (error) {
      if (error.name === 'AbortError') return;
      clear(results).append(errorState(error, () => search(text)));
    }
  }

  function update() {
    const text = input.value.trim();
    clearButton.hidden = !input.value;
    if (text === lastText) return;
    lastText = text;
    clearTimeout(timer);
    if (text.length < 2) { controller?.abort(); clear(results).append(recent()); return; }
    timer = setTimeout(() => search(text), 280);
  }
  input.addEventListener('input', update);
  update();

  return {
    el, title: 'Recherche', keep: true,
    // The keyboard opens when the tab is tapped, as in Apple's apps (only with nothing typed yet).
    enter() { if (!input.value && matchMedia('(pointer: coarse)').matches) input.focus({ preventScroll: true }); },
    dispose() { controller?.abort(); clearTimeout(timer); },
  };
}
