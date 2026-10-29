// Search films, series, people, studios, episodes and collections as you type; recent searches stay on this phone.
import { api, GRID_FIELDS } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { settings } from '../session.js';
import { posterCard, personCard, wideCard, row, skeletonGrid, errorState, emptyState } from '../components.js';

export function create({ query }) {
  const input = h('input', {
    class: 'input', type: 'search', placeholder: tr('Films, séries, acteurs…'), autocomplete: 'off', autocorrect: 'off', autocapitalize: 'none',
    spellcheck: false, enterkeyhint: 'search', 'aria-label': tr('Rechercher'), value: query.get('q') ?? '',
  });
  const clearButton = h('button', { class: 'round flat clear', 'aria-label': tr('Effacer'), hidden: !input.value, on: { click: () => { input.value = ''; input.focus(); update(); } } }, icon('close', { size: 20 }));
  const results = h('div', {});
  // What a screen reader hears of a search: how many results, not the whole grid at each pause in the typing.
  const status = h('p', { class: 'sr', role: 'status' });
  const el = h('div', { class: 'view search' },
    h('header', { class: 'search-head' }, h('h1', { class: 'page-title' }, tr('Recherche'))),
    h('div', { class: 'search-bar' },
      h('form', { class: 'search-box', role: 'search', on: { submit: (e) => { e.preventDefault(); remember(input.value); input.blur(); } } }, icon('search'), input, clearButton)),
    status, results);
  let timer = 0, controller = null, lastText = null;
  // Asked once, the first time a search shows: an administrator can have a title downloaded (views/torlink.js).
  let adminAsked = null;
  const administrator = () => (adminAsked ??= api.me().then((me) => !!me?.Policy?.IsAdministrator, () => false));

  function remember(text) {
    const value = text.trim();
    if (value.length < 2) return;
    settings.set('recentSearches', [value, ...settings.get('recentSearches').filter((x) => x.toLowerCase() !== value.toLowerCase())].slice(0, 8));
  }

  function recent() {
    const list = settings.get('recentSearches');
    if (!list.length) return emptyState({ symbol: 'search', title: tr('Que veux-tu regarder ?'), text: tr('Cherche un film, une série, un épisode, une collection, un acteur ou un studio de ta bibliothèque.') });
    return h('div', {}, h('div', { class: 'section-head' }, h('h2', { class: 'label' }, tr('Recherches récentes')),
      h('button', { class: 'btn quiet small', on: { click: () => { settings.set('recentSearches', []); clear(results).append(recent()); } } }, tr('Effacer'))),
    h('div', { class: 'recent' }, list.map((text) => h('button', { on: { click: () => { input.value = text; update(); } } }, icon('history', { size: 20 }), text))));
  }

  /**
   * Results for the text. What is shown stays, dimmed, until the answer arrives (no placeholders at each pause in the
   * typing); `quiet` (a refresh) keeps it as it is, and changes it only if the answer differs.
   */
  async function search(text, { quiet = false } = {}) {
    controller?.abort();
    controller = new AbortController();
    const showing = !!results.querySelector('.card');
    if (showing) { if (!quiet) results.classList.add('pending'); }
    else if (!quiet) clear(results).append(h('div', { style: { padding: '0 var(--gutter)' } }, skeletonGrid(6)));
    try {
      // Only the films and series are needed: people, episodes and sagas that cannot be had are left out.
      const [titles, people, makers, found, boxes] = await Promise.all([
        api.browse({ search: text, limit: 60, sort: 'title', fields: GRID_FIELDS, signal: controller.signal }),
        api.persons(text, controller.signal).catch(() => null),
        // « Ghibli », « MAPPA »: a title's search does not look at its studios.
        api.studios(text, controller.signal).catch(() => null),
        api.searchOthers('Episode', text, controller.signal).catch(() => null),
        api.searchOthers('BoxSet', text, controller.signal).catch(() => null),
      ]);
      const items = titles?.Items ?? [], persons = (people?.Items ?? []).slice(0, 12), studios = (makers?.Items ?? []).slice(0, 12);
      const episodes = (found?.Items ?? []).slice(0, 12), sagas = (boxes?.Items ?? []).slice(0, 12);
      results.classList.remove('pending');
      const signature = JSON.stringify([items, persons, studios, episodes, sagas]);
      if (quiet && signature === shownSignature) return;
      shownSignature = signature;
      clear(results);
      const total = items.length + persons.length + studios.length + episodes.length + sagas.length;
      status.textContent = total ? trn(total, '{n} résultat', '{n} résultats') : tr('Aucun résultat');
      if (!total) {
        // An administrator can have it downloaded on the PC of Jellyfin (views/torlink.js).
        const own = controller;
        const admin = await administrator();
        if (own !== controller) return;
        results.append(emptyState({
          symbol: 'search', title: tr('Aucun résultat'), text: tr('Rien ne correspond à « {text} » dans ta bibliothèque.', { text }),
          action: admin ? { label: tr('Le télécharger sur le PC serveur'), href: `#/telecharger?q=${encodeURIComponent(text)}` } : null,
        }));
        return;
      }
      // A result opened keeps the search among the recent ones.
      const kept = (card) => { card.addEventListener('click', () => remember(text)); return card; };
      if (persons.length) results.append(row(tr('Personnes'), persons, (p) => personCard(p), { people: true }));
      // A studio opens its titles in the library.
      if (studios.length) {
        results.append(h('section', { class: 'section' }, h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, tr('Studios'))),
          h('div', { style: { padding: '0 var(--gutter)' } }, h('div', { class: 'chips', role: 'list' },
            studios.map((x) => kept(h('a', { class: 'chip', role: 'listitem', href: `#/studio/${x.Id}` }, icon('films'), x.Name)))))));
      }
      if (items.length) {
        results.append(h('section', { class: 'section' }, h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, tr('Films et séries'))),
          h('div', { class: 'grid', style: { padding: '0 var(--gutter) 24px' } }, items.map((x) => kept(posterCard(x))))));
      }
      // An episode opens its series at it; a collection, its own page with its titles in order.
      if (episodes.length) results.append(row(tr('Épisodes'), episodes, (x) => kept(wideCard(x, { play: false })), { wide: true }));
      if (sagas.length) results.append(row(tr('Collections'), sagas, (x) => kept(posterCard(x))));
      // Found something, but perhaps not the one: an administrator can still have it downloaded.
      const own = controller;
      if (await administrator() && own === controller) {
        results.append(h('div', { class: 'search-download' }, h('p', {}, tr('Pas celui que tu cherches ?')),
          h('a', { class: 'btn small', href: `#/telecharger?q=${encodeURIComponent(text)}` }, icon('download', { size: 16 }), tr('Le télécharger sur le PC serveur'))));
      }
    } catch (error) {
      if (error.name === 'AbortError') return;
      results.classList.remove('pending');
      if (!quiet) { shownSignature = ''; status.textContent = ''; clear(results).append(errorState(error, () => search(text))); }
    }
  }
  let shownSignature = '';

  function update() {
    const text = input.value.trim();
    clearButton.hidden = !input.value;
    if (text === lastText) return;
    lastText = text;
    clearTimeout(timer);
    if (text.length < 2) { controller?.abort(); shownSignature = ''; status.textContent = ''; results.classList.remove('pending'); clear(results).append(recent()); return; }
    timer = setTimeout(() => search(text), 280);
  }
  input.addEventListener('input', update);
  // Scrolling the results puts the keyboard away, as in Apple's apps.
  results.addEventListener('touchmove', () => { if (document.activeElement === input) input.blur(); }, { passive: true });
  update();

  return {
    el, title: tr('Recherche'), keep: true,
    refresh() {
      const text = input.value.trim();
      if (text.length < 2) { results.classList.remove('pending'); clear(results).append(recent()); return undefined; }
      return search(text, { quiet: true });
    },
    // The tab touched again, at the top of the screen: the field takes the keyboard, as in Apple's apps. Never on
    // arrival: iOS opens the keyboard only for a focus given while it handles a touch, a focus from later leaves the
    // field lit without it, and a Home Screen app may keep its screen shorter once the keyboard has been up (the tab
    // bar then floats above the bottom).
    retap() { if (scrollY > 8) return false; input.focus({ preventScroll: true }); return true; },
    dispose() { controller?.abort(); clearTimeout(timer); },
  };
}
