// Films or Séries: the whole library in a grid, loaded as you scroll, with Mira's filters and sorts.
import { api } from '../api.js';
import { h, icon, clear, plural } from '../dom.js';
import { posterCard, skeletonGrid, emptyState, errorState, sheet, changes, toast, reconcile } from '../components.js';
import { session } from '../session.js';

/** What a card needs of an item, kept on the phone so that the tab opens with its first page at once next time. */
// Every field cardSig reads (components.js): a card drawn from it is kept as it is once the server answers the same.
const slim = (x) => ({
  Id: x.Id, Name: x.Name, Type: x.Type, SeriesId: x.SeriesId, SeriesName: x.SeriesName, SeriesPrimaryImageTag: x.SeriesPrimaryImageTag,
  IndexNumber: x.IndexNumber, ParentIndexNumber: x.ParentIndexNumber, ProductionYear: x.ProductionYear, RunTimeTicks: x.RunTimeTicks,
  ChildCount: x.ChildCount, ImageTags: x.ImageTags, BackdropImageTags: x.BackdropImageTags,
  ParentBackdropImageTags: x.ParentBackdropImageTags, ParentThumbImageTag: x.ParentThumbImageTag,
  ImageBlurHashes: x.ImageBlurHashes?.Primary ? { Primary: x.ImageBlurHashes.Primary } : undefined,
  UserData: x.UserData ? { Played: x.UserData.Played, PlaybackPositionTicks: x.UserData.PlaybackPositionTicks, IsFavorite: x.UserData.IsFavorite } : undefined,
});
/** The cards of a first screenful: their pictures load at once (and the tab waits for them), the others as it scrolls. */
const firstScreen = () => (innerWidth < 600 ? 3 : Math.max(3, Math.floor((innerWidth - 120) / 156))) * Math.ceil(innerHeight / 210);

const SORTS = [
  ['recent', 'Ajouts récents'], ['title', 'Titre'], ['year', 'Année'], ['rating', 'Note'], ['played', 'Vus récemment'],
];
const PAGE = 60;

export function create({ type, query }) {
  const movies = type === 'Movie';
  const noun = movies ? ['film', 'films'] : ['série', 'séries'];
  const state = { sort: 'recent', genre: query.get('genre') ?? '', year: '', unplayed: false, favorite: query.get('favoris') === '1' };
  let total = null, start = 0, done = false, busy = false, controller = null, loadedAt = 0, options = null, stale = false;
  let generation = 0, refreshing = null;   // a reset starts a new generation: the answers of an older one are dropped
  // Resolved once the first page shows, its first screenful of pictures decoded: the tab waits for it (app.js).
  let markReady = () => {};
  const ready = new Promise((resolve) => { markReady = resolve; });
  const storeKey = () => `mira.library.${session.current?.userId}.${type}.${[state.sort, state.genre, state.year, state.unplayed, state.favorite].join('|')}`;
  function readStore() { try { return JSON.parse(localStorage.getItem(storeKey()) ?? 'null'); } catch { return null; } }
  // Kept for each sort, unfiltered only: a few keys per account, never one per genre or year.
  function writeStore(items, all) {
    if (filtered()) return;
    try { localStorage.setItem(storeKey(), JSON.stringify({ items: items.slice(0, PAGE).map(slim), total: all })); } catch { /* full */ }
  }
  /** Cards for a first page: those of the first screenful load their pictures at once. */
  const firstCards = (items) => { const eager = new Set(items.slice(0, firstScreen()).map((x) => x.Id)); return (item) => posterCard(item, { eager: eager.has(item.Id) }); };
  // Only the pictures loading at once: a lazy one in a screen not shown yet never loads, its decode() never ends.
  const decoded = () => Promise.all([...grid.querySelectorAll('img[loading="eager"]')].map((img) => img.decode?.().catch(() => {})));

  const chips = h('div', { class: 'chips', role: 'toolbar', 'aria-label': 'Filtres' });
  const count = h('div', { class: 'library-count', 'aria-live': 'polite' });
  const grid = h('div', { class: 'grid' });
  const sentinel = h('div', { class: 'sentinel' });
  const body = h('div', { class: 'library-grid' }, grid, sentinel);
  const el = h('div', { class: 'view library' },
    h('header', { class: 'library-head' }, h('h1', { class: 'page-title' }, movies ? 'Films' : 'Séries')),
    h('div', { class: 'library-filters' }, chips), count, body);

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

  // Asked for once, and again after a failure (the server away would otherwise leave the lists empty for good).
  function loadOptions() {
    options ??= Promise.all([api.filters(type), api.years(type)])
      .then(([filters, years]) => ({ genres: (filters?.Genres ?? []).map((g) => g.Name), years: (years?.Items ?? []).map((y) => y.Name).filter(Boolean) }))
      .catch((error) => { options = null; toast(error.message); return null; });
    return options;
  }
  async function pickGenre() {
    const { genres } = await loadOptions() ?? {};
    if (!genres) return;
    sheet({ title: 'Genre', items: [{ label: 'Tous les genres', selected: !state.genre, run: () => apply({ genre: '' }) },
      ...genres.map((g) => ({ label: g, selected: state.genre === g, run: () => apply({ genre: g }) }))] });
  }
  async function pickYear() {
    const { years } = await loadOptions() ?? {};
    if (!years) return;
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
    generation++; refreshing = null;
    controller?.abort();
    start = 0; done = false; total = null; busy = false;
    // The first page of the last time shows at once, then changes in place; placeholders only for a first time.
    const stored = readStore();
    if (stored?.items?.length) {
      reconcile(grid, stored.items, firstCards(stored.items));
      count.textContent = stored.total ? plural(stored.total, noun[0], noun[1]) : '';
      decoded().then(markReady);
    } else {
      clear(grid).append(...skeletonGrid(Math.min(18, firstScreen())).children);
      count.textContent = '';
    }
    more();
  }

  const fetchPage = (from, limit, signal) => api.browse({
    types: type, start: from, limit, sort: state.sort, genre: state.genre || undefined, year: state.year || undefined,
    played: state.unplayed ? false : undefined, favorite: state.favorite, signal,
  });

  /**
   * The titles already shown, asked for again and redrawn in place (back from a film, Jellyfin back online): the
   * grid keeps its length and the page its scroll, with no placeholders in between.
   */
  function refresh() {
    // One at a time: a pull while a refresh runs waits for that one.
    refreshing ??= redraw().finally(() => { refreshing = null; });
    return refreshing;
  }
  async function redraw() {
    if (busy) return; // a page on its way brings fresh titles already
    if (start === 0) { reset(); return; }
    const mine = generation;
    busy = true;
    controller = new AbortController();
    try {
      const result = await fetchPage(0, Math.max(PAGE, start), controller.signal);
      if (mine !== generation) return;
      const items = result?.Items ?? [];
      total = result?.TotalRecordCount ?? items.length;
      reconcile(grid, items, (item) => posterCard(item));
      writeStore(items, total);
      start = items.length; done = start >= total; loadedAt = Date.now(); stale = false;
      count.textContent = total ? plural(total, noun[0], noun[1]) : '';
      if (total === 0) showEmpty();
    } catch (error) {
      if (error.name !== 'AbortError' && mine === generation) loadedAt = Date.now();
      return;
    } finally { if (mine === generation) busy = false; }
    keepLoading();
  }

  /** Nothing to show: why, and what to do. */
  function showEmpty() {
    grid.append(filtered()
      ? emptyState({ symbol: 'filter', title: 'Aucun titre ne correspond', text: 'Essaie un autre genre ou une autre année.', action: { label: 'Réinitialiser les filtres', run: () => apply({ genre: '', year: '', unplayed: false, favorite: false }) } })
      : emptyState({ symbol: movies ? 'films' : 'series', title: movies ? 'Aucun film pour l’instant' : 'Aucune série pour l’instant', text: 'Ajoute-en dans les dossiers de Jellyfin : ils apparaîtront ici après l’analyse.' }));
    grid.lastChild.style.gridColumn = '1 / -1';
  }

  /** A tall screen may show the end already, which the observer does not report again: the next page comes. */
  function keepLoading() {
    if (!done && !busy && el.closest('#view') && sentinel.getBoundingClientRect().top < innerHeight + 800) more();
  }

  async function more() {
    if (busy || done) return;
    const mine = generation;
    busy = true;
    controller = new AbortController();
    const first = start === 0;
    try {
      const result = await fetchPage(start, PAGE, controller.signal);
      if (mine !== generation) return;
      const items = result?.Items ?? [];
      total = result?.TotalRecordCount ?? items.length;
      if (first) {
        // In place: the cards already shown (from the last time) stay as they are, pictures and all.
        reconcile(grid, items, firstCards(items));
        writeStore(items, total);
        decoded().then(markReady);
      } else for (const item of items) grid.append(posterCard(item));
      start += items.length;
      done = items.length < PAGE || start >= total;
      loadedAt = Date.now();
      count.textContent = total ? plural(total, noun[0], noun[1]) : '';
      if (total === 0) showEmpty();
    } catch (error) {
      if (error.name === 'AbortError' || mine !== generation) return;
      if (first) {
        markReady();
        // The cards of the last time stay (the server away, they are still worth seeing): asked again on the next visit.
        if (grid.querySelector('[data-sig]')) { loadedAt = 0; return; }
        clear(grid);
      }
      const box = errorState(error, () => { box.remove(); done = false; more(); });
      box.style.gridColumn = '1 / -1';
      grid.append(box);
      done = true;
    } finally { if (mine === generation) busy = false; }
    keepLoading();
  }

  // Only for the screen shown: the one dissolving away (app.js) loads nothing more.
  const observer = new IntersectionObserver((entries) => { if (el.closest('#view') && entries.some((e) => e.isIntersecting)) more(); }, { rootMargin: '900px 0px' });
  observer.observe(sentinel);
  // Watched, favourite: the cards are redrawn when the grid is seen again.
  const onChange = () => { stale = true; };
  changes.addEventListener('item', onChange);

  renderChips();
  reset();
  return {
    el, title: movies ? 'Films' : 'Séries', keep: true, ready,
    enter() {
      if (stale || Date.now() - loadedAt > 10 * 60_000) refresh();
      else requestAnimationFrame(keepLoading);
    },
    refresh,
    dispose() { observer.disconnect(); controller?.abort(); changes.removeEventListener('item', onChange); },
  };
}
