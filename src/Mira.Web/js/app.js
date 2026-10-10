// Mira web: the shell (tab bar, navigation, sign-in guard) and the router between screens.
import { onUnauthorized, onReachable, ping } from './api.js';
import { h, icon, clear } from './dom.js';
import { session, isIOS, isStandalone } from './session.js';
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
    on: { click: (e) => { e.preventDefault(); openTab(tab.href); } },
  }, icon(tab.symbol), h('span', {}, tab.label))));

const cache = new Map(); // hash → { view, scroll }
const MAX_CACHED = 8;
let current = null, currentKey = '', currentBare = false, hiddenAt = 0, navigation = 0, loadFailed = false;
// The screens under the current one, by depth: what a swipe from the left edge reveals.
let trail = (() => { try { return JSON.parse(sessionStorage.getItem('mira.trail') || '[]'); } catch { return []; } })();
let replacing = null;       // depth kept by an address replaced in place (not a new screen)
let pendingTab = '';        // a tab touched deep in another: Mira first goes back to the bottom of the history
let uaTransition = false;   // Safari already animated this move back (its own swipe, outside the Home Screen app)
let skipTransition = false; // the swipe back already moved the screens

// While Jellyfin does not answer (PC off or starting), a bar says so and Mira asks again until it does.
const netText = h('span', {});
const netbar = h('div', { class: 'netbar', role: 'status', 'aria-live': 'polite', hidden: true }, h('div', { class: 'spinner' }), netText);
// The bar iOS shows once a screen's large title has scrolled away: the same title, small, on frosted glass.
const topbar = h('div', { class: 'topbar', 'aria-hidden': 'true' }, h('span', {}));
let probeTimer = 0, probeDelay = 0;

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
  else replaceRoute(fallback);
}

/** Another screen in place of this one, at the same depth: Back still leads where it did. */
export function replaceRoute(href) {
  replacing = history.state?.depth ?? lastDepth;
  location.replace(href);
}

/**
 * A tab of the bar: its first screen, the way an iOS tab bar does it. History does not pile up (Back on Android, the
 * swipe from the edge on iPhone, go back within a tab, not from tab to tab); touched again, a tab goes back to its
 * first screen, or to the top of it.
 */
function openTab(href) {
  const depth = history.state?.depth ?? 0;
  if (depth > 0) {
    pendingTab = href;
    history.go(-depth);
    // Nothing to go back to (a history started elsewhere): the tab simply takes this screen's place, at the bottom.
    setTimeout(() => { if (pendingTab === href) { pendingTab = ''; replacing = 0; location.replace(href); } }, 500);
    return;
  }
  if ((location.hash || '#/') === href) { scrollTo({ top: 0, behavior: 'smooth' }); return; }
  replaceRoute(href);
}

/** Forgets every kept screen: after signing in or out, nothing of the previous account stays. */
export function resetScreens() {
  for (const [key, entry] of cache) if (entry.view !== current) { entry.view.dispose?.(); cache.delete(key); }
}

async function route() {
  if (pendingUpdate) applyUpdate();
  // Back at the bottom of the history for a tab touched deep in another: the tab's screen takes its place.
  if (pendingTab) {
    if ((history.state?.depth ?? 0) > 0) return;
    const href = pendingTab; pendingTab = '';
    if ((location.hash || '#/') !== href) { lastDepth = 0; replaceRoute(href); return; }
  }
  const key = location.hash || '#/';
  // A new entry (a link followed) gets its depth; going back finds the depth it had; a replaced one keeps it.
  const before = lastDepth;
  if (history.state?.depth == null) history.replaceState({ depth: replacing ?? (navigation === 0 ? 0 : (lastDepth + 1)) }, '');
  replacing = null;
  const depth = history.state.depth;
  const move = navigation === 0 ? '' : depth > before ? 'push' : depth < before ? 'pop' : 'tab';
  const animate = !skipTransition && !uaTransition;
  skipTransition = false; uaTransition = false;
  lastDepth = depth;
  navigation++;
  trail = trail.slice(0, depth); trail[depth] = key;
  try { sessionStorage.setItem('mira.trail', JSON.stringify(trail)); } catch { /* private mode */ }

  const { path, query } = parse(key);
  const match = ROUTES.map((r) => ({ r, m: path.match(r.pattern) })).find((x) => x.m);
  if (!match) { replaceRoute('#/'); return; }
  const { r, m } = match;
  if (!r.open && !session.current) {
    sessionStorage.setItem('mira.next', key);
    replaceRoute('#/connexion');
    return;
  }

  // Leaving: remember where the page was scrolled.
  if (current) { const left = cache.get(currentKey); if (left) left.scroll = scrollY; }

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
  if (id !== routeToken) return;

  // The screens change places: in one step, so that the move from one to the other can be drawn between them.
  const swap = () => {
    // A later move (a second tap during this one) has the last word.
    if (id !== routeToken) return;
    if (current && current !== entry.view) {
      current.leave?.();
      if (!current.keep) { current.dispose?.(); cache.delete(currentKey); }
      current.el.remove();
    }
    underlay?.remove(); underlay = null; app.classList.remove('swipe-active');
    app.classList.toggle('with-tabs', !r.bare);
    tabbar.hidden = !!r.bare;
    for (const link of tabbar.querySelectorAll('a')) {
      if (link.dataset.tab === r.tab) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
    }
    current = entry.view; currentKey = key; currentBare = !!r.bare;
    document.title = current.title ? `${current.title} · Mira` : 'Mira';
    current.el.style.transform = '';
    current.el.classList.remove('enter-push', 'enter-pop', 'enter-tab');
    if (current.el.parentNode !== viewHost) clear(viewHost).append(current.el);
    scrollTo(0, entry.scroll ?? 0);
    updateScrolled();
    current.enter?.({ query });
  };
  // iOS's moves: a screen comes in from the right and goes back to it; tabs change at once, with a short fade.
  // The player opens on its own (Apple's full screen, or Mira's), the first screen without a move.
  const style = animate && move && !r.bare && !wasBare() && !reducedMotion() ? move : '';
  if (style && document.startViewTransition) {
    const mine = ++transitions;
    document.documentElement.dataset.nav = style;
    try {
      const transition = document.startViewTransition(swap);
      transition.finished.finally(() => { if (mine === transitions) delete document.documentElement.dataset.nav; });
    } catch { delete document.documentElement.dataset.nav; swap(); }
  } else {
    swap();
    if (style) { void current.el.offsetWidth; current.el.classList.add(`enter-${style}`); }
  }
}
let lastDepth = 0, routeToken = 0, underlay = null, transitions = 0;
const wasBare = () => currentBare;
const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

function trimCache() {
  while (cache.size > MAX_CACHED) {
    const [oldKey, oldEntry] = cache.entries().next().value;
    if (oldEntry.view === current) { cache.delete(oldKey); cache.set(oldKey, oldEntry); continue; }
    oldEntry.view.dispose?.(); cache.delete(oldKey);
  }
}

function showLoadFailure() {
  loadFailed = true;
  clear(viewHost).append(h('div', { class: 'state' }, icon('offline'), h('h2', {}, 'Mira n’a pas pu se charger'),
    h('p', {}, 'Le serveur ne répond pas : le PC est peut-être éteint ou en train de démarrer. Mira réessaie toute seule.'),
    h('button', { class: 'btn small', on: { click: () => location.reload() } }, 'Réessayer')));
  serverDown('offline');
}

function serverDown(reason) {
  netText.textContent = reason === 'starting'
    ? 'Jellyfin démarre… Mira reprend dès qu’il répond.'
    : 'Serveur injoignable. Mira réessaie toute seule.';
  netbar.hidden = false;
  if (!probeTimer) { probeDelay = 2000; probe(); }
}
function probe() {
  probeTimer = setTimeout(async () => {
    if (await ping()) { serverUp(); return; }
    probeDelay = Math.min(probeDelay * 1.5, 10_000);
    probe();
  }, probeDelay);
}
function serverUp() {
  clearTimeout(probeTimer); probeTimer = 0;
  if (netbar.hidden) return;
  netbar.hidden = true;
  toast('Connexion au serveur rétablie.');
  if (loadFailed) { loadFailed = false; route(); } else current?.refresh?.();
  checkForUpdate();
}

// ---------- Updates of Mira web ----------
// The phone keeps the page for a month (Mira opens even while the server is off). At each start, and when it comes
// back, Mira asks the server for the page: a new revision means Mira web was updated there, and it reloads.
const REVISION = new URL(import.meta.url).pathname.match(/\/v\/([^/]+)\//)?.[1] ?? '';
let pendingUpdate = '';
async function checkForUpdate() {
  if (!REVISION) return;
  try {
    // cache: 'reload' also refreshes the copy of the page that the phone keeps.
    const page = await (await fetch(location.pathname, { cache: 'reload', headers: { Accept: 'text/html' } })).text();
    const revision = page.match(/v\/([0-9a-z]+)\/js\/app\.js/i)?.[1];
    if (revision && revision !== REVISION) { pendingUpdate = revision; applyUpdate(); }
  } catch { /* Server away: checked again when it answers. */ }
}
function applyUpdate() {
  // Never in the middle of a film, and once per revision, should the reload bring the same page back.
  if (!pendingUpdate || location.hash.startsWith('#/lecture/')) return;
  if (sessionStorage.getItem('mira.update') === pendingUpdate) return;
  sessionStorage.setItem('mira.update', pendingUpdate);
  location.reload();
}

function updateScrolled() {
  document.body.classList.toggle('scrolled', scrollY > 8);
  const big = currentBare ? null : current?.el.querySelector('.page-title, .detail-title, .detail-logo');
  const show = !!big && big.getBoundingClientRect().bottom < topbar.offsetHeight;
  if (show) {
    const text = big.tagName === 'IMG' ? big.alt : big.textContent;
    if (topbar.firstChild.textContent !== text) topbar.firstChild.textContent = text;
  }
  topbar.classList.toggle('on', show);
}

// ---------- Pull to refresh ----------
// The Home Screen app has no Safari around it to reload the page: pulled down from its top, a screen asks the server
// again, as Apple's apps do. Only for screens that can refresh, and not while a sheet or the player is open.
function setupPullToRefresh() {
  if (!isStandalone()) return;
  const PULL = 84;
  const mark = h('div', { class: 'pull', 'aria-hidden': 'true' }, icon('refresh'));
  app.append(mark);
  let start = null, armed = false, busy = false;
  const show = (distance) => {
    const p = Math.min(1, distance / PULL);
    mark.style.opacity = String(p);
    mark.style.transform = `translate(-50%, ${Math.min(distance, PULL * 1.4) * 0.45}px) rotate(${Math.round(p * 300)}deg)`;
    mark.classList.toggle('armed', p >= 1);
  };
  const hide = () => { mark.style.opacity = '0'; mark.style.transform = 'translate(-50%, 0)'; mark.classList.remove('armed', 'busy'); };
  document.addEventListener('touchstart', (e) => {
    start = null;
    if (busy || e.touches.length !== 1 || scrollY > 0 || currentBare || !current?.refresh || document.querySelector('.sheet-layer')) return;
    if (e.touches[0].clientX <= 28) return; // the swipe back starts there
    start = e.touches[0].clientY; armed = false;
  }, { passive: true });
  document.addEventListener('touchmove', (e) => {
    if (start == null) return;
    const distance = e.touches[0].clientY - start;
    if (distance <= 0 || scrollY > 0) { start = null; hide(); return; }
    armed = distance >= PULL;
    show(distance);
  }, { passive: true });
  const end = async () => {
    if (start == null) return;
    start = null;
    if (!armed) { hide(); return; }
    busy = true;
    mark.classList.add('busy');
    try { await current?.refresh?.(); } catch { /* the screen says what failed */ }
    busy = false;
    hide();
  };
  document.addEventListener('touchend', end, { passive: true });
  document.addEventListener('touchcancel', () => { start = null; hide(); }, { passive: true });
}

// ---------- Swipe from the left edge to go back ----------
// A Home Screen app on iPhone has no Safari around it, so no swipe back: Mira draws its own, as iOS apps do. The
// screen follows the finger over the one under it (the screen kept from before, or the background), and goes back
// once past a third of the width or thrown.
function setupSwipeBack() {
  if (!isIOS() || !isStandalone()) return;
  const EDGE = 28;
  let gesture = null;
  const reset = (el) => { if (el) { el.style.transform = ''; el.style.transition = ''; el.classList.remove('swiping'); } };

  function begin() {
    const width = innerWidth;
    const top = current.el;
    const below = cache.get(trail[(history.state?.depth ?? 1) - 1] ?? '');
    underlay = h('div', { class: 'swipe-under', 'aria-hidden': 'true' });
    const shade = h('div', { class: 'swipe-shade' });
    let under = null;
    if (below && below.view !== current) {
      under = below.view.el;
      under.style.transform = `translate3d(${-width * 0.3}px, ${-(below.scroll ?? 0)}px, 0)`;
      underlay.append(under);
    }
    underlay.append(shade);
    app.prepend(underlay);
    app.classList.add('swipe-active');
    top.classList.add('swiping');
    // Fixed pieces of the screen (the round Back button) stay where they are on the glass while it moves.
    for (const fixed of top.querySelectorAll('.floating-back')) fixed.style.transform = `translateY(${scrollY}px)`;
    gesture.width = width; gesture.top = top; gesture.under = under; gesture.below = below; gesture.shade = shade;
  }
  function move(dx) {
    const { width, top, under, below, shade } = gesture;
    const p = Math.min(1, Math.max(0, dx / width));
    top.style.transform = `translate3d(${dx}px, 0, 0)`;
    if (under) under.style.transform = `translate3d(${-width * 0.3 * (1 - p)}px, ${-(below.scroll ?? 0)}px, 0)`;
    shade.style.opacity = String(0.5 * (1 - p));
  }
  function finish(commit) {
    const { width, top, under, below, shade } = gesture;
    gesture = null;
    const ease = 'cubic-bezier(0.32, 0.72, 0, 1)';
    const time = commit ? 260 : 300;
    top.style.transition = `transform ${time}ms ${ease}`;
    if (under) under.style.transition = `transform ${time}ms ${ease}`;
    shade.style.transition = `opacity ${time}ms ${ease}`;
    requestAnimationFrame(() => {
      top.style.transform = `translate3d(${commit ? width : 0}px, 0, 0)`;
      if (under) under.style.transform = `translate3d(${commit ? 0 : -width * 0.3}px, ${-(below.scroll ?? 0)}px, 0)`;
      shade.style.opacity = commit ? '0' : '0.5';
    });
    setTimeout(() => {
      for (const fixed of top.querySelectorAll('.floating-back')) fixed.style.transform = '';
      if (commit) {
        // The screens are already in place: the move back shows no second animation.
        skipTransition = true;
        reset(under);
        if (under) under.style.transform = `translate3d(0, ${-(below.scroll ?? 0)}px, 0)`;
        history.back();
        // Normally done by the move back itself; kept in case it never came.
        setTimeout(() => {
          reset(top);
          if (under && under.parentNode === underlay) { reset(under); under.remove(); }
          underlay?.remove(); underlay = null; app.classList.remove('swipe-active');
        }, 700);
      } else {
        reset(top);
        if (under) { reset(under); under.remove(); }
        underlay?.remove(); underlay = null; app.classList.remove('swipe-active');
      }
    }, time + 20);
  }

  document.addEventListener('touchstart', (e) => {
    if (gesture || underlay || e.touches.length !== 1 || currentBare || !current || (history.state?.depth ?? 0) < 1) return;
    if (document.querySelector('.sheet-layer, .player')) return;
    const touch = e.touches[0];
    if (touch.clientX > EDGE) return;
    gesture = { x: touch.clientX, y: touch.clientY, decided: false, lastX: touch.clientX, lastT: performance.now(), speed: 0 };
  }, { passive: true });
  document.addEventListener('touchmove', (e) => {
    if (!gesture) return;
    const touch = e.touches[0];
    const dx = touch.clientX - gesture.x, dy = touch.clientY - gesture.y;
    if (!gesture.decided) {
      // Decided at the first move, before Safari starts scrolling a row or the page.
      if (Math.abs(dy) > Math.abs(dx) || dx <= 0 || !e.cancelable) { gesture = null; return; }
      gesture.decided = true;
      begin();
    }
    e.preventDefault();
    const now = performance.now();
    gesture.speed = (touch.clientX - gesture.lastX) / Math.max(1, now - gesture.lastT);
    gesture.lastX = touch.clientX; gesture.lastT = now;
    move(Math.max(0, dx));
  }, { passive: false });
  const end = (e) => {
    if (!gesture) return;
    if (!gesture.decided) { gesture = null; return; }
    const dx = (e.changedTouches?.[0]?.clientX ?? gesture.lastX) - gesture.x;
    finish(e.type === 'touchend' && (dx > gesture.width / 3 || (gesture.speed > 0.4 && dx > 24)));
  };
  document.addEventListener('touchend', end, { passive: true });
  document.addEventListener('touchcancel', end, { passive: true });
}

function start() {
  history.scrollRestoration = 'manual';
  clear(app).append(h('div', { class: 'status-scrim', 'aria-hidden': 'true' }), viewHost, topbar, tabbar, netbar);
  addEventListener('hashchange', route);
  addEventListener('popstate', (e) => {
    // Safari's own swipe back (in a tab) has already shown the move: Mira does not draw a second one.
    uaTransition = !!e.hasUAVisualTransition;
    // A move in the history to an entry with the same address fires no hashchange.
    if ((location.hash || '#/') === currentKey) route();
  });
  // Without a touch listener, Safari on iPhone never shows the :active state of what is touched.
  document.addEventListener('touchstart', () => {}, { passive: true });
  setupSwipeBack();
  setupPullToRefresh();
  addEventListener('scroll', updateScrolled, { passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { hiddenAt = Date.now(); return; }
    if (probeTimer) { clearTimeout(probeTimer); probeDelay = 1000; probe(); }
    else if (hiddenAt && Date.now() - hiddenAt > 60_000) { current?.refresh?.(); checkForUpdate(); }
  });
  addEventListener('offline', () => toast('Plus de connexion : Mira reprendra dès le retour du réseau.'));
  addEventListener('online', () => { if (probeTimer) { clearTimeout(probeTimer); probeDelay = 500; probe(); } else current?.refresh?.(); });
  onReachable((up, reason) => (up ? serverUp() : serverDown(reason)));
  onUnauthorized(() => {
    resetScreens();
    sessionStorage.setItem('mira.notice', 'Ta session a pris fin. Reconnecte-toi.');
    replaceRoute('#/connexion');
  });
  route();
  checkForUpdate();
}

start();
