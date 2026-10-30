// Films or Séries: the whole library in a grid, loaded as you scroll, with Mira's filters and sorts.
import { api, GRID_FIELDS } from '../api.js';
import { h, icon, clear, append, keepFocus } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { posterCard, skeletonGrid, emptyState, errorState, sheet, changes, toast, reconcile, cardSig } from '../components.js';
import { session } from '../session.js';
import { calendar, today } from '../calendar.js';

/** What a card needs of an item, kept on the phone so that the tab opens with its first page at once next time. */
// Every field cardSig reads (components.js): a card drawn from it is kept as it is once the server answers the same.
const slim = (x) => ({
  Id: x.Id, Name: x.Name, Type: x.Type, SeriesId: x.SeriesId, SeriesName: x.SeriesName, SeriesPrimaryImageTag: x.SeriesPrimaryImageTag,
  IndexNumber: x.IndexNumber, ParentIndexNumber: x.ParentIndexNumber, ProductionYear: x.ProductionYear, RunTimeTicks: x.RunTimeTicks,
  ChildCount: x.ChildCount, ImageTags: x.ImageTags, BackdropImageTags: x.BackdropImageTags,
  ParentBackdropImageTags: x.ParentBackdropImageTags, ParentThumbImageTag: x.ParentThumbImageTag,
  ImageBlurHashes: x.ImageBlurHashes?.Primary ? { Primary: x.ImageBlurHashes.Primary } : undefined,
  UserData: x.UserData ? {
    Played: x.UserData.Played, PlaybackPositionTicks: x.UserData.PlaybackPositionTicks, IsFavorite: x.UserData.IsFavorite,
    PlayedPercentage: x.UserData.PlayedPercentage, UnplayedItemCount: x.UserData.UnplayedItemCount,
  } : undefined,
});
/** The cards of a first screenful: their pictures load at once (and the tab waits for them), the others as it scrolls. */
const firstScreen = () => (innerWidth < 600 ? 3 : Math.max(3, Math.floor((innerWidth - 120) / 156))) * Math.ceil(innerHeight / 210);

const SORTS = [
  ['recent', tr('Ajouts récents')], ['title', tr('Titre')], ['year', tr('Année')], ['rating', tr('Note')], ['played', tr('Vus récemment')],
];
const PAGE = 60;
/** « Lettre »: « # » for the titles before A (a number first, « 2001 »), then A to Z. */
const LETTERS = ['#', ...'ABCDEFGHIJKLMNOPQRSTUVWXYZ'];
/** The kind of Jellyfin library that holds each tab's titles. */
const KINDS = { Movie: 'movies', Series: 'tvshows' };

export function create({ type, query }) {
  const movies = type === 'Movie';
  const counted = (n) => (movies ? trn(n, '{n} film', '{n} films') : trn(n, '{n} série', '{n} séries'));
  // library: one of several libraries of this kind (Séries or Animes), '' for all of them.
  const state = { sort: 'recent', genre: query.get('genre') ?? '', year: '', unplayed: false, favorite: query.get('favoris') === '1', library: '', letter: '' };
  let total = null, start = 0, done = false, busy = false, controller = null, loadedAt = 0, options = null, stale = false;
  let generation = 0, refreshing = null;   // a reset starts a new generation: the answers of an older one are dropped
  // Resolved once the first page shows, its first screenful of pictures decoded: the tab waits for it (app.js).
  let markReady = () => {};
  const ready = new Promise((resolve) => { markReady = resolve; });
  const storeKey = () => `mira.library.${session.current?.userId}.${type}.${[state.sort, state.genre, state.year, state.unplayed, state.favorite, state.library, state.letter].join('|')}`;
  // The account's libraries, kept on the phone so that the choice between them shows at once, without moving the grid.
  const viewsKey = () => `mira.library.${session.current?.userId}.views`;
  const ofKind = (views) => views.filter((v) => v.type === KINDS[type]);
  let libraries = (() => { try { return ofKind(JSON.parse(localStorage.getItem(viewsKey()) ?? '[]')); } catch { return []; } })();
  function readStore() { try { return JSON.parse(localStorage.getItem(storeKey()) ?? 'null'); } catch { return null; } }
  // Kept for each sort, unfiltered only: a few keys per account, never one per genre or year; never once signed out.
  function writeStore(items, all) {
    if (filtered() || !session.current) return;
    try { localStorage.setItem(storeKey(), JSON.stringify({ items: items.slice(0, PAGE).map(slim), total: all })); } catch { /* full */ }
  }
  /** Cards for a first page: those of the first screenful load their pictures at once. */
  const firstCards = (items) => { const eager = new Set(items.slice(0, firstScreen()).map((x) => x.Id)); return (item) => posterCard(item, { eager: eager.has(item.Id) }); };
  // Only the pictures loading at once: a lazy one in a screen not shown yet never loads, its decode() never ends.
  const decoded = () => Promise.all([...grid.querySelectorAll('img[loading="eager"]')].map((img) => img.decode?.().catch(() => {})));

  const shelves = h('div', { class: 'segmented', role: 'radiogroup', 'aria-label': tr('Bibliothèque') });
  const chips = h('div', { class: 'chips', role: 'toolbar', 'aria-label': tr('Filtres') });
  const count = h('div', { class: 'library-count', 'aria-live': 'polite' });
  const grid = h('div', { class: 'grid' });
  const sentinel = h('div', { class: 'sentinel' });
  const body = h('div', { class: 'library-grid' }, grid, sentinel);
  const el = h('div', { class: 'view library' },
    h('header', { class: 'library-head' }, h('h1', { class: 'page-title' }, movies ? tr('Films') : tr('Séries')), shelves),
    h('div', { class: 'library-filters' }, chips), count, body);

  // Séries: « À venir » once Jellyfin knows of episodes coming or missing (its TheTVDB extension: calendar.js).
  let upcoming = false;
  function loadUpcoming() {
    if (movies) return;
    api.virtualEpisodes().then((list) => {
      const { missing, coming } = calendar(list, today());
      const any = missing.length + coming.length > 0;
      if (any !== upcoming) { upcoming = any; renderChips(); }
    }, () => {});
  }

  const filtered = () => state.genre || state.year || state.unplayed || state.favorite || state.letter;
  const noFilters = { genre: '', year: '', unplayed: false, favorite: false, letter: '' };

  function chip(label, { on = false, menu = false, run }) {
    return h('button', { class: ['chip', on && 'on'], 'aria-pressed': menu ? null : String(on), 'aria-haspopup': menu ? 'dialog' : null, on: { click: run } },
      label, menu ? icon('down', { size: 16 }) : null);
  }

  function renderChips() {
    // append of dom.js skips the chips not shown (Element.append would write « null »). A chip used from the keyboard
    // keeps the focus where it was.
    keepFocus(chips, () => append(clear(chips), [
      upcoming ? h('a', { class: 'chip', href: '#/a-venir' }, icon('calendar', { size: 16 }), tr('À venir')) : null,
      chip(tr('Trier : {sort}', { sort: SORTS.find(([k]) => k === state.sort)[1] }), { menu: true, run: () => sheet({
        // A letter goes with the sort by title only.
        title: tr('Trier par'), items: SORTS.map(([key, label]) => ({ label, selected: state.sort === key, run: () => apply({ sort: key, letter: key === 'title' ? state.letter : '' }) })),
      }) }),
      state.sort === 'title' ? chip(state.letter ? tr('Lettre : {letter}', { letter: state.letter }) : tr('Lettre'), { on: !!state.letter, menu: true, run: pickLetter }) : null,
      chip(state.genre || tr('Genre'), { on: !!state.genre, menu: true, run: pickGenre }),
      chip(state.year || tr('Année'), { on: !!state.year, menu: true, run: pickYear }),
      chip(tr('Non vus'), { on: state.unplayed, run: () => apply({ unplayed: !state.unplayed }) }),
      chip(tr('Favoris'), { on: state.favorite, run: () => apply({ favorite: !state.favorite }) }),
      filtered() ? h('button', { class: 'chip', on: { click: () => apply(noFilters) } }, icon('close', { size: 16 }), tr('Réinitialiser')) : null,
    ]));
  }

  /** « Tout | Séries | Animes »: only with several libraries of this kind. A view rather than a filter: kept by Réinitialiser. */
  function renderShelves() {
    shelves.hidden = libraries.length < 2;
    keepFocus(shelves, () => clear(shelves).append(...(shelves.hidden ? [] : [{ id: '', name: tr('Tout') }, ...libraries].map(({ id, name }) =>
      h('button', { role: 'radio', 'aria-checked': String(state.library === id), on: { click: () => { if (state.library !== id) apply({ library: id }); } } }, name)))));
  }
  function loadShelves() {
    api.libraries().then((views) => {
      const all = views.filter((v) => Object.values(KINDS).includes(v.CollectionType)).map((v) => ({ id: v.Id, name: v.Name, type: v.CollectionType }));
      try { localStorage.setItem(viewsKey(), JSON.stringify(all)); } catch { /* full or blocked: asked again next time */ }
      if (JSON.stringify(ofKind(all)) === JSON.stringify(libraries)) return;
      libraries = ofKind(all);
      // A library gone (removed from Jellyfin, or no longer shared with this account): all of them again.
      if (state.library && !libraries.some((v) => v.id === state.library)) apply({ library: '' }); else renderShelves();
    }, () => {});
  }

  // Asked for once, and again after a failure (the server away would otherwise leave the lists empty for good).
  function loadOptions() {
    options ??= Promise.all([api.filters(type), api.years(type)])
      .then(([filters, years]) => ({ genres: (filters?.Genres ?? []).map((g) => g.Name), years: (years?.Items ?? []).map((y) => y.Name).filter(Boolean) }))
      .catch((error) => { options = null; toast(error.message, { tone: 'error' }); return null; });
    return options;
  }
  async function pickGenre() {
    const { genres } = await loadOptions() ?? {};
    if (!genres) return;
    sheet({ title: tr('Genre'), items: [{ label: tr('Tous les genres'), selected: !state.genre, run: () => apply({ genre: '' }) },
      ...genres.map((g) => ({ label: g, selected: state.genre === g, run: () => apply({ genre: g }) }))] });
  }
  async function pickYear() {
    const { years } = await loadOptions() ?? {};
    if (!years) return;
    sheet({ title: tr('Année'), items: [{ label: tr('Toutes les années'), selected: !state.year, run: () => apply({ year: '' }) },
      ...years.map((y) => ({ label: y, selected: state.year === y, run: () => apply({ year: y }) }))] });
  }

  /**
   * « Lettre », with the sort by title: only the titles under one letter, as Jellyfin sorts them, by their sort name
   * (« The Batman » under B). Rather than a column of letters on the right edge, where iOS's swipe back starts.
   */
  function pickLetter() {
    let close = () => {};
    const pick = (letter) => { close(); if (letter !== state.letter) apply({ letter }); };
    const letters = h('div', { class: 'letters', role: 'group', 'aria-label': tr('Lettres') }, LETTERS.map((letter) => h('button', {
      class: ['letter', state.letter === letter && 'on'], 'aria-pressed': String(state.letter === letter),
      'aria-label': letter === '#' ? tr('Chiffres et symboles') : letter, on: { click: () => pick(letter) },
    }, letter)));
    close = sheet({ title: tr('Lettre'), body: letters, items: [{ label: tr('Toutes les lettres'), selected: !state.letter, run: () => pick('') }] });
  }

  function apply(change) {
    Object.assign(state, change);
    renderShelves(); renderChips();
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
      showCount(stored.total);
      decoded().then(markReady);
    } else {
      clear(grid).append(...skeletonGrid(Math.min(18, firstScreen())).children);
      showCount(0);
    }
    more();
  }

  const fetchPage = (from, limit, signal) => api.browse({
    types: type, start: from, limit, sort: state.sort, genre: state.genre || undefined, year: state.year || undefined,
    played: state.unplayed ? false : undefined, favorite: state.favorite, library: state.library || undefined, fields: GRID_FIELDS, signal,
    startsWith: state.letter !== '#' ? state.letter || undefined : undefined, before: state.letter === '#' ? 'A' : undefined,
  });

  /**
   * The titles shown, brought up to date in place (back from a film, Jellyfin back online): the grid keeps its length
   * and the page its scroll, with no placeholders in between. Only the first page is asked for again: what changes in
   * a library (a title added, watched, a favourite) shows there in this order, and a card further down hears of its
   * own changes (onChange). Only when the first page is no longer the same titles in the same order are all the
   * titles shown asked for again, so that the grid stays in one order.
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
      let result = await fetchPage(0, PAGE, controller.signal);
      if (mine !== generation) return;
      let items = result?.Items ?? [];
      const cards = [...grid.children].filter((card) => card.item);
      const whole = items.length < PAGE || (result?.TotalRecordCount ?? 0) <= items.length;
      const same = !whole && cards.length >= items.length && items.every((item, i) => cards[i].item.Id === item.Id);
      if (same) {
        // The same titles first: only the cards whose title changed are made again; the others, and the rest, stay.
        items.forEach((item, i) => { if (cards[i].dataset.sig === cardSig(item)) cards[i].item = item; else cards[i].replaceWith(posterCard(item)); });
        // A page that failed to load said so at the end: the next one is asked for again (keepLoading).
        for (const other of [...grid.children]) if (!other.item) other.remove();
        writeStore(items, result?.TotalRecordCount ?? items.length);
      } else {
        if (!whole && start > PAGE) {
          result = await fetchPage(0, start, controller.signal);
          if (mine !== generation) return;
          items = result?.Items ?? [];
        }
        reconcile(grid, items, (item) => posterCard(item));
        writeStore(items, result?.TotalRecordCount ?? items.length);
        start = items.length;
      }
      total = result?.TotalRecordCount ?? items.length;
      done = start >= total; loadedAt = Date.now(); stale = false;
      showCount(total);
      if (total === 0) showEmpty();
    } catch (error) {
      if (error.name !== 'AbortError' && mine === generation) loadedAt = Date.now();
      return;
    } finally { if (mine === generation) busy = false; }
    keepLoading();
  }

  /** The filters on, by name (« Favoris », « Animation », « 2021 »), in the order of their chips. */
  const activeFilters = () => [
    state.letter ? { label: tr('Lettre : {letter}', { letter: state.letter }), off: { letter: '' } } : null,
    state.genre ? { label: state.genre, off: { genre: '' } } : null,
    state.year ? { label: state.year, off: { year: '' } } : null,
    state.unplayed ? { label: tr('Non vus'), off: { unplayed: false } } : null,
    state.favorite ? { label: tr('Favoris'), off: { favorite: false } } : null,
  ].filter(Boolean);
  /** The count, and the filters on once their chips have scrolled away: why the grid shows what it shows. */
  const showCount = (n) => { count.textContent = [n ? counted(n) : '', activeFilters().map((f) => f.label).join(', ')].filter(Boolean).join(' · '); };

  /** Nothing to show: which filter empties the grid, in words, and the button that takes that one off. */
  function showEmpty() {
    const on = activeFilters();
    if (!on.length) {
      grid.append(emptyState({ symbol: movies ? 'films' : 'series', title: movies ? tr('Aucun film pour l’instant') : tr('Aucune série pour l’instant'), text: tr('Ajoute-en dans les dossiers de Jellyfin : ils apparaîtront ici après l’analyse.') }));
    } else if (on.length === 1) {
      const [filter] = on;
      const words = state.letter ? { title: state.letter === '#' ? tr('Aucun titre avant A') : tr('Aucun titre en {letter}', { letter: state.letter }), text: tr('Choisis une autre lettre.') }
        : state.favorite ? { title: movies ? tr('Aucun film en favori') : tr('Aucune série en favori'), text: tr('Touche le cœur sur la fiche d’un titre pour le retrouver ici.') }
        : state.unplayed ? { title: movies ? tr('Tu as vu tous ces films') : tr('Tu as vu toutes ces séries'), text: tr('Rien de nouveau à voir ici pour l’instant.') }
        : state.genre ? { title: movies ? tr('Aucun film « {genre} »', { genre: state.genre }) : tr('Aucune série « {genre} »', { genre: state.genre }), text: tr('Choisis un autre genre.') }
        : { title: movies ? tr('Aucun film de {year}', { year: state.year }) : tr('Aucune série de {year}', { year: state.year }), text: tr('Choisis une autre année.') };
      grid.append(emptyState({ symbol: 'filter', ...words, action: { label: tr('Retirer « {filter} »', { filter: filter.label }), run: () => apply(filter.off) } }));
    } else {
      // Several: each can be taken off on its own, or all of them at once.
      const box = emptyState({ symbol: 'filter', title: tr('Aucun titre avec ces filtres'), text: tr('Retire un filtre pour en voir plus.'), action: { label: tr('Réinitialiser les filtres'), run: () => apply(noFilters) } });
      box.querySelector('.btn').before(h('div', { class: 'state-filters', role: 'group', 'aria-label': tr('Filtres') }, on.map((filter) =>
        h('button', { class: 'chip', 'aria-label': tr('Retirer « {filter} »', { filter: filter.label }), on: { click: () => apply(filter.off) } }, icon('close', { size: 16 }), filter.label))));
      grid.append(box);
    }
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
      showCount(total);
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
  // Watched, favourite: the card says so at once, wherever it is in the grid; the first page is asked for again when
  // the grid is seen again (the title may move in a sort or leave a filter).
  const onChange = (event) => {
    stale = true;
    const changedItem = event.detail?.item;
    if (!changedItem?.UserData) return;
    for (const card of grid.children) {
      if (card.item?.Id !== changedItem.Id) continue;
      const next = { ...card.item, UserData: { ...card.item.UserData, ...changedItem.UserData } };
      if (cardSig(next) !== card.dataset.sig) card.replaceWith(posterCard(next));
      break;
    }
  };
  changes.addEventListener('item', onChange);

  renderShelves(); renderChips();
  loadShelves(); loadUpcoming();
  reset();
  return {
    el, title: movies ? tr('Films') : tr('Séries'), keep: true, ready,
    enter() {
      loadUpcoming();
      if (stale || Date.now() - loadedAt > 10 * 60_000) refresh();
      else requestAnimationFrame(keepLoading);
    },
    refresh,
    dispose() { observer.disconnect(); controller?.abort(); changes.removeEventListener('item', onChange); },
  };
}
