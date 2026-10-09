// Mira web: the shell (tab bar, navigation, sign-in guard) and the router between screens.
import { onUnauthorized } from './api.js';
import { h, icon, clear } from './dom.js';
import { session } from './session.js';
import { toast } from './components.js';
import './player/video.js';

const TABS = [
  { key: 'home', href: '#/', label: 'Accueil', symbol: 'home' },
  { key: 'films', href: '#/films', label: 'Films', symbol: 'films' },
  { key: 'series', href: '#/series', label: 'Séries', symbol: 'series' },
  { key: 'search', href: '#/recherche', label: 'Recherche', symbol: 'search' },
  { key: 'settings', href: '#/reglages', label: 'Réglages', symbol: 'settings' },
];

// Each screen is loaded the first time it opens, so the first one shows sooner.
const ROUTES = [
  { pattern: /^\/$/, tab: 'home', load: () => import('./views/home.js') },
  { pattern: /^\/films$/, tab: 'films', load: () => import('./views/library.js'), args: () => ({ type: 'Movie' }) },
  { pattern: /^\/series$/, tab: 'series', load: () => import('./views/library.js'), args: () => ({ type: 'Series' }) },
  { pattern: /^\/recherche$/, tab: 'search', load: () => import('./views/search.js') },
  { pattern: /^\/reglages$/, tab: 'settings', load: () => import('./views/settings.js') },
  { pattern: /^\/titre\/([0-9a-f-]+)$/i, load: () => import('./views/detail.js'), args: (m) => ({ id: m[1] }) },
  { pattern: /^\/personne\/([0-9a-f-]+)$/i, load: () => import('./views/person.js'), args: (m) => ({ id: m[1] }) },
  { pattern: /^\/lecture\/([0-9a-f-]+)$/i, bare: true, load: () => import('./player/player.js'), args: (m) => ({ id: m[1] }) },
  { pattern: /^\/connexion$/, bare: true, open: true, load: () => import('./views/login.js') },
  { pattern: /^\/partager$/, bare: true, open: true, load: () => import('./views/share.js') },
];

const app = document.getElementById('app');
const viewHost = h('main', { id: 'view' });
const tabbar = h('nav', { class: 'tabbar', 'aria-label': 'Navigation' },
  h('div', { class: 'rail-mark', 'aria-hidden': 'true' }, markSvg(40)),
  TABS.map((tab) => h('a', {
    href: tab.href, dataset: { tab: tab.key },
    on: { click: (e) => { if (location.hash === tab.href || (tab.href === '#/' && !location.hash)) { e.preventDefault(); scrollTo({ top: 0, behavior: 'smooth' }); } } },
  }, icon(tab.symbol), h('span', {}, tab.label))));

const cache = new Map(); // hash → { view, scroll }
const MAX_CACHED = 8;
let current = null, currentKey = '', hiddenAt = 0, navigation = 0;

function markSvg(size) {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('width', size); svg.setAttribute('height', size); svg.setAttribute('class', 'mark');
  svg.innerHTML = '<path fill="#f5f5f7" d="M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z"/><path fill="none" stroke="#101013" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round" d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/>';
  return svg;
}
export { markSvg };

function parse(hash) {
  const raw = (hash || '#/').replace(/^#/, '') || '/';
  const [path, search = ''] = raw.split('?');
  return { path: path || '/', query: new URLSearchParams(search) };
}

/** Back to the previous screen of Mira, or to `fallback` when Mira was opened right here. */
export function goBack(fallback = '#/') {
  if ((history.state?.depth ?? 0) > 0) history.back();
  else location.replace(fallback);
}

/** Forgets every kept screen: after signing in or out, nothing of the previous account stays. */
export function resetScreens() {
  for (const [key, entry] of cache) if (entry.view !== current) { entry.view.dispose?.(); cache.delete(key); }
}

async function route() {
  const key = location.hash || '#/';
  // A new entry (a link followed) gets its depth; going back finds the depth it had.
  if (history.state?.depth == null) history.replaceState({ depth: navigation === 0 ? 0 : (lastDepth + 1) }, '');
  lastDepth = history.state.depth;
  navigation++;

  const { path, query } = parse(key);
  const match = ROUTES.map((r) => ({ r, m: path.match(r.pattern) })).find((x) => x.m);
  if (!match) { location.replace('#/'); return; }
  const { r, m } = match;
  if (!r.open && !session.current) {
    sessionStorage.setItem('mira.next', key);
    location.replace('#/connexion');
    return;
  }

  // Leaving: remember where the page was scrolled; screens not worth keeping go away.
  if (current) {
    const entry = cache.get(currentKey);
    if (entry) entry.scroll = scrollY;
    current.leave?.();
    if (!current.keep) { current.dispose?.(); cache.delete(currentKey); }
    current.el.remove();
  }

  app.classList.toggle('with-tabs', !r.bare);
  tabbar.hidden = !!r.bare;
  for (const link of tabbar.querySelectorAll('a')) {
    if (link.dataset.tab === r.tab) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  }

  const id = ++routeToken;
  let entry = cache.get(key);
  if (!entry) {
    let module;
    try { module = await r.load(); } catch (error) { showLoadFailure(error); return; }
    if (id !== routeToken) return;
    const view = module.create({ ...(r.args?.(m) ?? {}), query, key });
    entry = { view, scroll: 0 };
    if (view.keep) cache.set(key, entry);
    trimCache();
  }
  current = entry.view; currentKey = key;
  document.title = current.title ? `${current.title} · Mira` : 'Mira';
  clear(viewHost).append(current.el);
  current.el.classList.remove('entering'); void current.el.offsetWidth; current.el.classList.add('entering');
  scrollTo(0, entry.scroll ?? 0);
  updateScrolled();
  current.enter?.({ query });
}
let lastDepth = 0, routeToken = 0;

function trimCache() {
  while (cache.size > MAX_CACHED) {
    const [oldKey, oldEntry] = cache.entries().next().value;
    if (oldEntry.view === current) { cache.delete(oldKey); cache.set(oldKey, oldEntry); continue; }
    oldEntry.view.dispose?.(); cache.delete(oldKey);
  }
}

function showLoadFailure() {
  clear(viewHost).append(h('div', { class: 'state' }, icon('offline'), h('h2', {}, 'Mira n’a pas pu se charger'),
    h('p', {}, 'Le serveur ne répond pas. Vérifie le Wi-Fi, ou Tailscale hors de chez toi.'),
    h('button', { class: 'btn small', on: { click: () => location.reload() } }, 'Réessayer')));
}

function updateScrolled() { document.body.classList.toggle('scrolled', scrollY > 8); }

function start() {
  history.scrollRestoration = 'manual';
  clear(app).append(h('div', { class: 'status-scrim', 'aria-hidden': 'true' }), viewHost, tabbar);
  addEventListener('hashchange', route);
  addEventListener('scroll', updateScrolled, { passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) hiddenAt = Date.now();
    else if (hiddenAt && Date.now() - hiddenAt > 60_000) current?.refresh?.();
  });
  addEventListener('offline', () => toast('Plus de connexion : Mira reprendra dès le retour du réseau.'));
  addEventListener('online', () => current?.refresh?.());
  onUnauthorized(() => {
    resetScreens();
    sessionStorage.setItem('mira.notice', 'Ta session a pris fin. Reconnecte-toi.');
    location.replace('#/connexion');
  });
  route();
}

start();
