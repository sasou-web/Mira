// Films or Séries: the whole library in a grid, loaded as you scroll, with Mira's filters and sorts.
import { api } from '../api.js';
import { h, icon, clear, plural } from '../dom.js';
import { posterCard, skeletonGrid, emptyState, errorState, sheet, changes } from '../components.js';

const SORTS = [
  ['recent', 'Ajouts récents'], ['title', 'Titre'], ['year', 'Année'], ['rating', 'Note'], ['played', 'Vus récemment'],
];
const PAGE = 60;

export function create({ type, query }) {
  const movies = type === 'Movie';
  const noun = movies ? ['film', 'films'] : ['série', 'séries'];
  const state = { sort: 'recent', genre: query.get('genre') ?? '', year: '', unplayed: false, favorite: query.get('favoris') === '1' };
  let total = null, start = 0, done = false, busy = false, controller = null, loadedAt = 0, options = null;

  const chips = h('div', { class: 'chips', role: 'toolbar', 'aria-label': 'Filtres' });
  const count = h('div', { class: 'library-count', 'aria-live': 'polite' });
  const grid = h('div', { class: 'grid', role: 'list' });
  const sentinel = h('div', { class: 'sentinel' });
  const body = h('div', { class: 'library-grid' }, grid, sentinel);
  const el = h('div', { class: 'view library' },
    h('header', { class: 'library-head' }, h('h1', { class: 'page-title' }, movies ? 'Films' : 'Séries'), chips), count, body);

  const filtered = () => state.genre || state.year || state.unplayed || state.favorite;

  function chip(label, { on = false, menu = false, run }) {
    return h('button', { class: ['chip', on && 'on'], 'aria-pressed': menu ? null : String(on), 'aria-haspopup': menu ? 'dialog' : null, on: { click: run } },
      label, menu ? icon('down', { size: 16 }) : null);
  }

  function renderChips() {
    clear(chips).append(
      chip(`Trier : ${SORTS.find(([k]) => k === state.sort)[1]}`, { menu: true, run: () => sheet({
        title: 'Trier par', items: SORTS.map(([key, label]) => ({ label, selected: state.sort === key, run: () => apply({ sort: key }) })),
      }) }),
      chip(state.genre || 'Genre', { on: !!state.genre, menu: true, run: pickGenre }),
      chip(state.year || 'Année', { on: !!state.year, menu: true, run: pickYear }),
      chip('Non vus', { on: state.unplayed, run: () => apply({ unplayed: !state.unplayed }) }),
      chip('Favoris', { on: state.favorite, run: () => apply({ favorite: !state.favorite }) }),
      filtered() ? h('button', { class: 'chip', on: { click: () => apply({ genre: '', year: '', unplayed: false, favorite: false }) } }, icon('close', { size: 16 }), 'Réinitialiser') : null,
    );
  }

  async function loadOptions() {
    if (options) return options;
    const [filters, years] = await Promise.all([api.filters(type).catch(() => null), api.years(type).catch(() => null)]);
    options = { genres: (filters?.Genres ?? []).map((g) => g.Name), years: (years?.Items ?? []).map((y) => y.Name).filter(Boolean) };
    return options;
  }
  async function pickGenre() {
    const { genres } = await loadOptions();
    sheet({ title: 'Genre', items: [{ label: 'Tous les genres', selected: !state.genre, run: () => apply({ genre: '' }) },
      ...genres.map((g) => ({ label: g, selected: state.genre === g, run: () => apply({ genre: g }) }))] });
  }
  async function pickYear() {
    const { years } = await loadOptions();
    sheet({ title: 'Année', items: [{ label: 'Toutes les années', selected: !state.year, run: () => apply({ year: '' }) },
      ...years.map((y) => ({ label: y, selected: state.year === y, run: () => apply({ year: y }) }))] });
  }

  function apply(change) {
    Object.assign(state, change);
    renderChips();
    reset();
    scrollTo({ top: 0 });
  }

  function reset() {
    controller?.abort();
    start = 0; done = false; total = null; busy = false;
    clear(grid).append(...skeletonGrid(18).children);
    count.textContent = '';
    more();
  }

  async function more() {
    if (busy || done) return;
    busy = true;
    controller = new AbortController();
    const first = start === 0;
    try {
      const result = await api.browse({
        types: type, start, limit: PAGE, sort: state.sort, genre: state.genre || undefined, year: state.year || undefined,
        played: state.unplayed ? false : undefined, favorite: state.favorite, signal: controller.signal,
      });
      if (first) clear(grid);
      const items = result?.Items ?? [];
      total = result?.TotalRecordCount ?? items.length;
      for (const item of items) { const card = posterCard(item); card.setAttribute('role', 'listitem'); grid.append(card); }
      start += items.length;
      done = items.length < PAGE || start >= total;
      loadedAt = Date.now();
      count.textContent = total ? plural(total, noun[0], noun[1]) : '';
      if (total === 0) {
        grid.append(filtered()
          ? emptyState({ symbol: 'filter', title: 'Aucun titre ne correspond', text: 'Essaie un autre genre ou une autre année.', action: { label: 'Réinitialiser les filtres', run: () => apply({ genre: '', year: '', unplayed: false, favorite: false }) } })
          : emptyState({ symbol: movies ? 'films' : 'series', title: movies ? 'Aucun film pour l’instant' : 'Aucune série pour l’instant', text: 'Ajoute-en dans les dossiers de Jellyfin : ils apparaîtront ici après l’analyse.' }));
        grid.lastChild.style.gridColumn = '1 / -1';
      }
    } catch (error) {
      if (error.name === 'AbortError') return;
      if (first) clear(grid);
      const box = errorState(error, () => { box.remove(); busy = false; more(); });
      box.style.gridColumn = '1 / -1';
      grid.append(box);
      done = true;
    } finally { busy = false; }
    // A tall screen may show the end of the first page already.
    if (!done && sentinel.getBoundingClientRect().top < innerHeight + 800) more();
  }

  const observer = new IntersectionObserver((entries) => { if (entries.some((e) => e.isIntersecting)) more(); }, { rootMargin: '900px 0px' });
  observer.observe(sentinel);
  const onChange = () => { loadedAt = 0; };
  changes.addEventListener('item', onChange);

  renderChips();
  reset();
  return {
    el, title: movies ? 'Films' : 'Séries', keep: true,
    enter() { if (Date.now() - loadedAt > 10 * 60_000) reset(); },
    refresh: reset,
    dispose() { observer.disconnect(); controller?.abort(); changes.removeEventListener('item', onChange); },
  };
}
