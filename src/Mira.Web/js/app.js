// Mira web: the shell (tab bar, navigation, sign-in guard) and the router between screens.
import { onUnauthorized, onReachable, onForeignServer, ping, recheckServer, trustServer, isGuest, FOREIGN, UNREACHABLE, UNREACHABLE_SHORT, VERSION } from './api.js';
import { h, icon, clear } from './dom.js';
import { session, signedOut, device, isStandalone, temp, applyTextSize, settings, storageBlocked } from './session.js';
import { toast, sheet } from './components.js';
import { icons } from './icons.js';
import { phoneNotes, worthAsking, webIcon } from './whats-new.js';
import { setupPress } from './press.js';
import { tr, language } from './i18n.js';
import './player/video.js';

const TABS = [
  { key: 'home', href: '#/', label: tr('Accueil'), symbol: 'home' },
  { key: 'films', href: '#/films', label: tr('Films'), symbol: 'films' },
  { key: 'series', href: '#/series', label: tr('Séries'), symbol: 'series' },
  { key: 'search', href: '#/recherche', label: tr('Recherche'), symbol: 'search' },
  { key: 'settings', href: '#/reglages', label: tr('Réglages'), symbol: 'settings' },
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
  { pattern: /^\/studio\/([0-9a-f-]+)$/i, load: () => import('./views/studio.js'), args: (m) => ({ id: m[1] }) },
  { pattern: /^\/saga\/([0-9a-f-]+)$/i, load: () => import('./views/saga.js'), args: (m) => ({ id: m[1] }) },
  // « En ce moment sur Jellyfin », from Réglages (an administrator).
  { pattern: /^\/en-ce-moment$/, load: () => import('./views/now.js') },
  // « Titres à corriger », from Réglages (an administrator): titles Jellyfin recognized badly.
  { pattern: /^\/a-corriger$/, load: () => import('./views/fix.js') },
  // « Sous-titres automatiques », from Réglages (an administrator): the extension's search and alignment of subtitles.
  { pattern: /^\/sous-titres-auto$/, load: () => import('./views/auto-subtitles.js') },
  // « Télécharger sur le PC serveur », from Réglages or Recherche (an administrator); ?q= searches at once.
  { pattern: /^\/telecharger$/, load: () => import('./views/torlink.js') },
  { pattern: /^\/lecture\/([0-9a-f-]+)$/i, bare: true, guest: true, load: () => import('./player/player.js'), args: (m) => ({ id: m[1] }) },
  // An invitation to « Regarder ensemble » (invite.js): signed in first, then in the group. Without a group's id, the
  // group of the host who invited (a guest of « Inviter par lien »).
  { pattern: /^\/ensemble\/([0-9a-f-]+)$/i, bare: true, guest: true, load: () => import('./views/join.js'), args: (m) => ({ id: m[1] }) },
  { pattern: /^\/ensemble$/, bare: true, guest: true, load: () => import('./views/join.js'), args: () => ({ id: '' }) },
  // « Inviter par lien »: a link opened by someone without an account (views/guest.js); without its code, a link that
  // no longer works.
  { pattern: /^\/invite\/([A-Za-z0-9_-]{22})$/, bare: true, open: true, guest: true, load: () => import('./views/guest.js'), args: (m) => ({ code: m[1] }) },
  { pattern: /^\/invite$/, bare: true, open: true, guest: true, load: () => import('./views/guest.js') },
  // « Inviter par lien », the host's side (an administrator): the links made, from Réglages or « Inviter quelqu'un ».
  { pattern: /^\/invites$/, load: () => import('./views/guests.js') },
  { pattern: /^\/connexion$/, bare: true, open: true, load: () => import('./views/login.js') },
  // Opened from Réglages, a screen of its own like the others; opened by Mira for Windows, before any sign-in, alone.
  { pattern: /^\/partager$/, bare: () => !session.current, open: true, load: () => import('./views/share.js') },
];

// A guest of « Inviter par lien » (see api.js base): the host's group, the player and nothing else of Mira.
const GUEST = isGuest();

const app = document.getElementById('app');
const viewHost = h('main', { id: 'view' });
const tabbar = h('nav', { class: 'tabbar', 'aria-label': tr('Navigation') },
  h('div', { class: 'rail-mark', 'aria-hidden': 'true' }, markSvg(40)),
  TABS.map((tab) => h('a', {
    href: tab.href, dataset: { tab: tab.key },
    on: { click: (e) => { e.preventDefault(); openTab(tab.href); } },
  }, icon(tab.symbol), h('span', {}, tab.label))));

const cache = new Map(); // hash → { view, scroll }, the least recently shown first
const MAX_CACHED = 8;
let current = null, currentKey = '', currentBare = false, hiddenAt = 0, navigation = 0, loadFailed = false;
// The screens of the history, by depth: the first one says which tab the current screen belongs to.
let trail = (() => { try { return JSON.parse(temp.get('mira.trail') || '[]'); } catch { return []; } })();
let replacing = null;       // depth kept by an address replaced in place (not a new screen)
let pendingTab = '';        // a tab touched deep in another: Mira first goes back to the bottom of the history
let traversing = false;     // a move in the history is on its way: a second tap on Back or a tab waits for it
let uaTransition = false;   // iOS or Safari has already drawn this move (its swipe from the edge)
let edgeSwipe = 0;          // when a touch from a screen edge last turned into the system's swipe

// While Jellyfin does not answer (PC off or starting), a bar says so and Mira asks again until it does.
const netText = h('span', {});
const netbar = h('div', { class: 'netbar', role: 'status', 'aria-live': 'polite', hidden: true }, h('div', { class: 'spinner' }), netText);
// The bar iOS shows once a screen's large title has scrolled away: the same title, small, on frosted glass.
const topbar = h('div', { class: 'topbar', 'aria-hidden': 'true', on: { click: () => scrollTo({ top: 0, behavior: 'smooth' }) } }, h('span', {}));
let probeTimer = 0, probeDelay = 0;

function markSvg(size) {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('width', size); svg.setAttribute('height', size); svg.setAttribute('class', 'mark');
  // Mira's mark, beside a title that says where you are: nothing for a screen reader to read.
  svg.setAttribute('aria-hidden', 'true');
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
  if (traversing) return;
  if ((history.state?.depth ?? 0) > 0) { traversing = true; history.back(); }
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
  // A second tap while Mira is still going back: the last tab touched wins, without going back further.
  if (pendingTab) { pendingTab = href; return; }
  if (traversing) return;
  const depth = history.state?.depth ?? 0;
  if (depth > 0) {
    pendingTab = href; traversing = true;
    history.go(-depth);
    // Nothing to go back to (a history started elsewhere): the tab simply takes this screen's place, at the bottom.
    setTimeout(() => { if (pendingTab) { const to = pendingTab; pendingTab = ''; traversing = false; replacing = 0; location.replace(to); } }, 500);
    return;
  }
  // The tab of the screen shown, touched again: back to the top, or, at the top, what the screen does then (Recherche
  // opens the keyboard). Touched again while its screen is on its way, it waits: the screen shown is still the last one.
  if ((location.hash || '#/') === href) { if (currentKey === href && !current?.retap?.()) scrollTo({ top: 0, behavior: 'smooth' }); return; }
  replaceRoute(href);
}

/** Forgets every kept screen: after signing in or out, nothing of the previous account stays. */
export function resetScreens() {
  for (const [key, entry] of cache) if (entry.view !== current) { entry.view.dispose?.(); cache.delete(key); }
}

async function route() {
  traversing = false;
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
  const animate = !uaTransition;
  uaTransition = false;
  lastDepth = depth;
  navigation++;
  trail = trail.slice(0, depth); trail[depth] = key;
  temp.set('mira.trail', JSON.stringify(trail));

  const { path, query } = parse(key);
  const match = ROUTES.map((r) => ({ r, m: path.match(r.pattern) })).find((x) => x.m);
  if (!match) { replaceRoute('#/'); return; }
  const { r, m } = match;
  const bare = typeof r.bare === 'function' ? r.bare() : !!r.bare;
  if (!r.open && !session.current) {
    temp.set('mira.next', key);
    replaceRoute('#/connexion');
    return;
  }
  if (GUEST && !r.guest) { replaceRoute('#/ensemble'); return; }
  // « Nouvel épisode » once a session shows a screen of Mira (not the player, an invitation or the sign-in), a moment
  // after it: when Mira opens, and right after signing in (that first time only sets the mark).
  if (session.current && !GUEST && !bare && !newsLooked) { newsLooked = true; setTimeout(lookForNews, 2500); }
  // « Tes choix gardés sur ton Jellyfin »: this phone and Jellyfin compared once a session shows a screen of Mira.
  if (session.current && !GUEST && !bare && !choicesLooked) { choicesLooked = true; setTimeout(() => choices().then(({ syncChoices }) => syncChoices()).catch(() => {}), 1500); }

  const id = ++routeToken;
  let entry = cache.get(key);
  if (entry) {
    // The most recently shown screens are kept longest; a screen opened again starts at its top.
    cache.delete(key); cache.set(key, entry);
    if (move === 'push') entry.scroll = 0;
  } else {
    let module;
    try { module = await r.load(); } catch (error) { if (id === routeToken) showLoadFailure(error); return; }
    temp.remove('mira.reloaded');
    if (id !== routeToken) return;
    const view = module.create({ ...(r.args?.(m) ?? {}), query, key });
    entry = { view, scroll: 0 };
    if (view.keep) cache.set(key, entry);
    trimCache();
  }
  if (id !== routeToken) return;
  // A title or a person belongs to the tab its history started from, which stays lit, as in iOS.
  const tab = r.tab ?? tabOf(trail[0]);
  // A tab's screen never shown yet waits until it can show whole, its first data and first pictures there, 250 ms at
  // most: the screen shown until now stays meanwhile (it still answers touches), and the tab touched lights up at
  // once. At launch, Mira's logo waits the same way, a little longer.
  if (!entry.shown && entry.view.ready && ((move === 'tab' && current) || (!current && bootScreen))) {
    if (!bare) lightTab(tab);
    await Promise.race([entry.view.ready, new Promise((resolve) => setTimeout(resolve, current ? 250 : 700))]);
    if (id !== routeToken) return;
  }

  // The screens change places: in one step, so that the move from one to the other can be drawn between them.
  const swap = () => {
    // A later move (a second tap during this one) has the last word.
    if (id !== routeToken) return;
    endDissolve();
    // Leaving: remember where the page was scrolled (now: it may have moved while the next screen got ready).
    if (current) { const left = cache.get(currentKey); if (left) left.scroll = scrollY; }
    if (current && current !== entry.view) {
      current.leave?.();
      if (!current.keep) { current.dispose?.(); cache.delete(currentKey); }
      if (style === 'tab') dissolve(current.el); else current.el.remove();
    }
    app.classList.toggle('with-tabs', !bare);
    tabbar.hidden = bare;
    lightTab(tab);
    entry.shown = true;
    endBoot();
    current = entry.view; currentKey = key; currentBare = bare;
    document.title = current.title ? `${current.title} · Mira` : 'Mira';
    current.el.classList.remove('enter-push', 'enter-pop');
    if (current.el.parentNode !== viewHost) clear(viewHost).append(current.el);
    else for (const other of [...viewHost.children]) if (other !== current.el) other.remove();
    scrollTo(0, entry.scroll ?? 0);
    updateScrolled();
    current.enter?.({ query });
    // A screen reader, or the keyboard, starts on the new screen's title rather than on what was touched to get here.
    if (move && !bare) focusTitle(current.el);
    // Once the move is drawn (a sheet opened during it would be drawn with it).
    if (newsWaiting && !bare) setTimeout(offerNews, 600);
  };
  // iOS's moves: a screen comes in from the right and goes back to it; a tab's screen dissolves into the next (see
  // dissolve). The player opens on its own (Apple's full screen, or Mira's), the first screen without a move, and a
  // move iOS drew is not drawn again.
  const style = animate && (move === 'push' || move === 'pop' || move === 'tab') && !bare && !wasBare() && !reducedMotion() ? move : '';
  if (style === 'tab') swap();
  else if (style && document.startViewTransition) {
    // A tab's dissolve still running ends first: the move captures the screen of the tab touched, not the one before.
    endDissolve();
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
let lastDepth = 0, routeToken = 0, transitions = 0;
let newsLooked = false, choicesLooked = false;
const lookForNews = () => import('./news.js').then(({ announceNewEpisodes }) => announceNewEpisodes()).catch(() => {});
const choices = () => import('./account-choices.js');
signedOut.addEventListener('out', () => { newsLooked = false; choicesLooked = false; });

/** The screen's title takes the focus, without a ring for a touch, unless something in the screen already has it. */
function focusTitle(el) {
  const active = document.activeElement;
  if (!foreign.hidden || document.querySelector('.sheet-layer:not(.leaving)') || (active && active !== el && el.contains(active))) return;
  const title = el.querySelector('h1, [role="heading"][aria-level="1"]');
  if (!title) return;
  if (!title.hasAttribute('tabindex')) title.tabIndex = -1;
  title.focus({ preventScroll: true });
}

function lightTab(tab) {
  for (const link of tabbar.querySelectorAll('a')) {
    if (link.dataset.tab === tab) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
  }
}

// Tabs: the screen left stays a moment over the new one, as it was scrolled, and dissolves into it (UIKit's cross
// dissolve). Only the screen: the bars stay as they are, frosted, and the new screen takes touches at once. Not a View
// Transition: captured, the frosted bars lose their blur, and the page under it would not answer touches meanwhile.
const leavingLayer = h('div', { class: 'view-leaving', 'aria-hidden': 'true', inert: '' });
let dissolving = null;
/** A dissolve still running ends at once (another tab touched during it): its screen goes, back as it was. */
function endDissolve() {
  if (!dissolving) return;
  const { fade, el } = dissolving;
  dissolving = null;
  fade.cancel();
  el.style.top = '';
  if (el.parentNode === leavingLayer) el.remove();
  leavingLayer.remove();
}
function dissolve(el) {
  endDissolve();
  // Its big title stays as faded as it was under the small title bar (updateScrolled lights the next screen's).
  if (fadedTitle && el.contains(fadedTitle)) fadedTitle = null;
  // Moved, a scrolled row or banner would jump back to its start: it is put back where it was.
  const scrolled = [...el.querySelectorAll('.row, .hero-track, .chips')].filter((x) => x.scrollLeft).map((x) => [x, x.scrollLeft]);
  el.style.top = `${-scrollY}px`;
  leavingLayer.append(el);
  if (!leavingLayer.isConnected) viewHost.after(leavingLayer);
  for (const [x, left] of scrolled) { x.style.scrollBehavior = 'auto'; x.scrollLeft = left; x.style.scrollBehavior = ''; }
  const fade = leavingLayer.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 180, easing: 'ease-in', fill: 'forwards' });
  dissolving = { fade, el };
  fade.finished.then(() => { if (dissolving?.fade === fade) endDissolve(); }, () => {});
}

// Mira's logo, from index.html, over the first screen until it can show whole.
let bootScreen = null;
function endBoot() {
  const boot = bootScreen;
  if (!boot) return;
  bootScreen = null;
  if (reducedMotion()) { boot.remove(); return; }
  boot.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, easing: 'ease-in', fill: 'forwards' }).finished.then(() => boot.remove(), () => boot.remove());
}
const wasBare = () => currentBare;
const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
const tabOf = (key) => ROUTES.find((x) => x.tab && x.pattern.test(parse(key).path))?.tab ?? '';

/** The least recently shown screens go first; never the current one, nor one that Back leads to. */
function trimCache() {
  for (const [key, entry] of cache) {
    if (cache.size <= MAX_CACHED) break;
    if (entry.view === current || trail.includes(key)) continue;
    entry.view.dispose?.(); cache.delete(key);
  }
}

function showLoadFailure() {
  loadFailed = true;
  endBoot();
  clear(viewHost).append(h('div', { class: 'state' }, icon('offline'), h('h2', {}, tr('Mira n’a pas pu se charger')),
    h('p', {}, UNREACHABLE),
    h('button', { class: 'btn small', on: { click: async () => { if (await yours()) location.reload(); } } }, tr('Réessayer'))));
  serverDown('offline');
}

// One failed request during a change of network says nothing: the bar shows once the server has stayed silent a
// moment (at once while Mira is opening, where nothing else would say why the screen stays empty).
let netTimer = 0, netShownAt = 0, wasDown = false;
const openedAt = performance.now();
function serverDown(reason) {
  wasDown = true;
  netText.textContent = reason === 'starting'
    ? tr('Jellyfin démarre… Mira reprend dès qu’il répond.')
    : UNREACHABLE_SHORT;
  if (netbar.hidden && !netTimer) {
    const wait = loadFailed || performance.now() - openedAt < 15_000 ? 0 : 1500;
    netTimer = setTimeout(() => { netTimer = 0; netbar.hidden = false; netShownAt = Date.now(); }, wait);
  }
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
  const unseen = netbar.hidden;
  clearTimeout(netTimer); netTimer = 0;
  netbar.hidden = true;
  // A screen whose code could not load comes back whole: the address and the history survive a reload. Once per
  // address and revision: a screen that still fails then says so, rather than reloading again and again. Not from
  // another server (see yours).
  if (loadFailed) {
    const key = `${REVISION}|${location.hash}`;
    // Without the site's storage the reload could not be told from the next one: the screen says it failed instead.
    if ((temp.get('mira.reloaded') ?? '') !== key) {
      yours().then((mine) => { if (mine && temp.set('mira.reloaded', key)) location.reload(); });
      return;
    }
  }
  // Even a short silence, never shown, may have left a screen on its error: every screen is asked again.
  if (!wasDown) return;
  wasDown = false;
  if (!unseen && Date.now() - netShownAt > 3000) toast(tr('Connexion au serveur rétablie.'));
  current?.refresh?.();
  checkForUpdate();
}

// ---------- Another server at this address ----------
// The server answering is not the one you signed in on (another network, or Jellyfin reinstalled): Mira says so over
// every screen and sends it nothing of your session (api.js). Asked again from here, and when Mira comes back.
const foreign = h('div', { class: 'foreign', role: 'alertdialog', 'aria-modal': 'true', 'aria-labelledby': 'foreign-title', hidden: true });
const again = h('button', { class: 'btn primary', on: { click: () => { recheckServer(); retryServer(); } } }, icon('refresh', { size: 20 }), tr('Réessayer'));
foreign.append(h('div', { class: 'gate' }, icon('warning'),
  h('div', { class: 'gate-text' }, h('h1', { id: 'foreign-title' }, tr('Ce n’est pas ton serveur')), h('p', { class: 'lead' }, FOREIGN)),
  again,
  // Jellyfin reinstalled: a new server, where this session never existed. Forgotten here only, never sent to it.
  h('button', { class: 'btn quiet', on: { click: () => { session.clear(); hideForeign(false); resetScreens(); replaceRoute('#/connexion'); } } },
    tr('J’ai réinstallé Jellyfin : me reconnecter'))));
function showForeign() {
  endBoot();
  if (!foreign.hidden) return;
  foreign.hidden = false;
  again.focus({ preventScroll: true });
}
/** Your server again: the screen shown asks it at once what it could not. */
function hideForeign(refresh = true) {
  if (foreign.hidden) return;
  foreign.hidden = true;
  if (refresh) { current?.refresh?.(); checkForUpdate(); }
}
/** Waits for the check of the server answering (asked for again by Réessayer, or by api.js when Mira comes back). */
async function retryServer() {
  if (again.disabled) return;
  again.disabled = true;
  // Yours again, or no session left to protect: this screen goes. Silent now: the bar says so, and Mira asks again
  // once it answers (still another server: this screen again).
  try { await trustServer(); hideForeign(); } catch (error) { if (!error?.foreign) hideForeign(false); }
  again.disabled = false;
}
/**
 * False only when another server answers here: Mira asks it for nothing, not even its page and its code, which a
 * reload would take from it and the phone keep (see checkForUpdate).
 */
const yours = () => trustServer().then(() => true, (error) => !error?.foreign);

// ---------- Updates of Mira web ----------
// The phone keeps the page for a month (Mira opens even while the server is off). At each start, and when it comes
// back, Mira asks the server for the page: a new revision means Mira web was updated there, and it reloads.
const REVISION = new URL(import.meta.url).pathname.match(/\/v\/([^/]+)\//)?.[1] ?? '';
let pendingUpdate = '';
async function checkForUpdate() {
  // Only from your server, so only signed in (api.js knows yours by the session): another one could otherwise put its
  // own page in place of the copy the phone keeps. Signed out, the next start once signed in asks.
  if (!REVISION || !session.current) return;
  try {
    await trustServer();
    // cache: 'reload' also refreshes the copy of the page that the phone keeps.
    const page = await (await fetch(location.pathname, { cache: 'reload', headers: { Accept: 'text/html' } })).text();
    const revision = page.match(/v\/([0-9a-z]+)\/js\/app\.js/i)?.[1];
    if (revision && revision !== REVISION) { pendingUpdate = revision; applyUpdate(); }
  } catch { /* Server away, or not yours: checked again when it answers. */ }
}
/**
 * The new version replaces this one at once only while Mira is opening and nothing was touched; otherwise when Mira
 * goes to the background, so that no screen reloads under the finger. Never in the middle of a film, and once per
 * revision, should the reload bring the same page back.
 */
async function applyUpdate(hidden = false) {
  if (!pendingUpdate || location.hash.startsWith('#/lecture/')) return;
  if (!hidden && (touched || performance.now() - openedAt > 4000)) return;
  if (temp.get('mira.update') === pendingUpdate) return;
  // The reload asks for the page again: only from your server, and not if Mira came back meanwhile. Never without
  // the site's storage: nothing would stop the same page from reloading again and again.
  if (!(await yours()) || (hidden && !document.hidden)) return;
  if (temp.set('mira.update', pendingUpdate)) location.reload();
}
let touched = false;

// ---------- « Nouveau dans Mira » ----------
// Once Jellyfin serves a new Mira web (see above), its main points for the phone, once (whats-new.js). Never after a
// sign-in (api.opened), in the player or on an invitation: the next screen with the tab bar shows them, and a sheet
// already open goes first.
let newsWaiting = false;
const busyScreen = () => currentBare || !!document.querySelector('.sheet-layer:not(.leaving)');
async function offerNews() {
  newsWaiting = false;
  const seen = settings.get('seenVersion');
  // Without the site's storage, nothing would keep it from coming back at every start.
  if (!session.current || GUEST || pendingUpdate || !worthAsking(seen, VERSION) || storageBlocked()) return;
  if (busyScreen()) { newsWaiting = true; return; }
  let notes;
  try {
    const response = await fetch(new URL('../whats-new.json', import.meta.url));
    if (!response.ok) return;
    notes = phoneNotes(await response.json(), seen, VERSION);
  } catch { return; } // Server away: asked again at the next start.
  if (busyScreen()) { newsWaiting = true; return; }
  settings.set('seenVersion', VERSION);
  if (!notes.length) return;
  const [latest, ...older] = notes, known = new Set(Object.keys(icons));
  let close = () => {};
  const body = h('div', { class: 'news' },
    h('h2', { class: 'news-version' }, `Mira ${latest.version}`),
    h('ul', { class: 'news-list' }, latest.items.map((item) => h('li', { class: 'news-item' },
      h('span', { class: 'news-badge' }, icon(webIcon(item.icon, known))),
      h('div', {}, h('div', { class: 'news-name' }, item.title), h('p', { class: 'news-text' }, item.text))))),
    // Versions skipped by this update: one line each, as Mira for Windows says them.
    older.slice(0, 2).map((entry) => h('p', { class: 'news-also' }, tr('Aussi en {version} : {titles}.', { version: entry.version, titles: entry.items.map((x) => x.title).join(', ') }))),
    h('button', { class: 'btn primary block', on: { click: () => close() } }, tr('C’est parti')));
  close = sheet({ title: tr('Nouveau dans Mira'), body });
}

let fadedTitle = null;
function updateScrolled() {
  document.body.classList.toggle('scrolled', scrollY > 8);
  // As the large title goes under the bar, it fades out and the small one fades in, over 24 px of scroll: never both
  // at once.
  const big = currentBare ? null : current?.el.querySelector('.page-title, .detail-title, .detail-logo');
  let fade = 0;
  if (big) {
    // From where the title rests: a large title that sits under the bar's height at the top of the page is whole there.
    const bottom = big.getBoundingClientRect().bottom, line = Math.min(topbar.offsetHeight - 4, bottom + scrollY);
    fade = Math.max(0, Math.min(1, (line - bottom) / 24));
  }
  if (fade > 0) {
    const text = big.tagName === 'IMG' ? big.alt : big.textContent;
    if (topbar.firstChild.textContent !== text) topbar.firstChild.textContent = text;
  }
  if (fadedTitle && fadedTitle !== big) fadedTitle.style.opacity = '';
  if (big) { big.style.opacity = fade ? String(1 - fade) : ''; fadedTitle = big; }
  topbar.style.opacity = fade ? String(fade) : '';
  topbar.classList.toggle('on', fade > 0);
}

// ---------- Pull to refresh ----------
// The Home Screen app has no Safari around it to reload the page, and Chrome on Android would reload all of Mira:
// pulled down from its top, a screen asks the server again, as native apps do. Only for screens that can refresh,
// and not while a sheet or the player is open.
function setupPullToRefresh() {
  if (!isStandalone() && device.name !== 'Android') return;
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
    if (e.touches[0].clientX <= 24) return; // iOS's swipe back starts there
    start = { x: e.touches[0].clientX, y: e.touches[0].clientY, vertical: false }; armed = false;
  }, { passive: true });
  document.addEventListener('touchmove', (e) => {
    if (start == null) return;
    const distance = e.touches[0].clientY - start.y, across = Math.abs(e.touches[0].clientX - start.x);
    // A sideways swipe (the banner, a row) is not a pull, whatever its slope downwards.
    if (!start.vertical) {
      if (across < 10 && distance < 10) return;
      if (across >= distance) { start = null; hide(); return; }
      start.vertical = true;
    }
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

// ---------- iOS's swipe from the edge ----------
// Safari, and a Home Screen app since iOS 12.2, go back (or forward) with a swipe from the screen's edge and draw the
// move themselves, from a picture of the screen before. Mira must not draw it a second time: the popstate event says
// so where Safari supports it (hasUAVisualTransition); elsewhere, a touch from an edge that iOS took for its own
// gesture (it cancels the touch) or that moved well across tells it.
function watchEdgeSwipes() {
  let from = null;
  document.addEventListener('touchstart', (e) => {
    const x = e.touches[0]?.clientX ?? 0;
    // From the left edge, a swipe goes back; from the right edge, forward again.
    // Not on what scrolls sideways itself (rows, the banner, chips): that finger is scrolling, not going back.
    const sideways = e.target.closest?.('.row, .hero, .chips');
    from = e.touches.length === 1 && !sideways && (x <= 24 || x >= innerWidth - 24) ? { x, way: x <= 24 ? 1 : -1 } : null;
  }, { passive: true });
  document.addEventListener('touchmove', (e) => {
    if (from && ((e.touches[0]?.clientX ?? from.x) - from.x) * from.way > 40) { edgeSwipe = performance.now(); from = null; }
  }, { passive: true });
  document.addEventListener('touchcancel', () => { if (from) edgeSwipe = performance.now(); from = null; }, { passive: true });
  document.addEventListener('touchend', () => { from = null; }, { passive: true });
}

function start() {
  // index.html says English; the language shown (i18n.js) is the page's, for screen readers and hyphenation.
  document.documentElement.lang = language();
  history.scrollRestoration = 'manual';
  applyTextSize();
  setupPress();
  // Chrome on Android reloads the whole page when it is pulled down at its top: Mira refreshes the screen instead.
  document.documentElement.classList.toggle('android', device.name === 'Android');
  bootScreen = app.querySelector('.boot');
  bootScreen?.classList.add('over');
  clear(app).append(h('div', { class: 'status-scrim', 'aria-hidden': 'true' }), viewHost, topbar, tabbar, netbar, foreign);
  if (bootScreen) app.append(bootScreen);
  addEventListener('hashchange', route);
  addEventListener('popstate', (e) => {
    // iOS's own swipe has already shown the move: Mira does not draw a second one.
    // Only for a move in the history (a link followed fires popstate too, on an entry without a depth yet), and once.
    const traversal = history.state?.depth != null;
    uaTransition = !!e.hasUAVisualTransition || (traversal && performance.now() - edgeSwipe < 3000);
    edgeSwipe = 0;
    // A move in the history to an entry with the same address fires no hashchange.
    if ((location.hash || '#/') === currentKey) route();
  });
  // Without a touch listener, Safari on iPhone never shows the :active state of what is touched.
  document.addEventListener('touchstart', () => {}, { passive: true });
  addEventListener('pointerdown', () => { touched = true; }, { capture: true, once: true, passive: true });
  watchEdgeSwipes();
  setupPullToRefresh();
  addEventListener('scroll', updateScrolled, { passive: true });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { hiddenAt = Date.now(); applyUpdate(true); return; }
    // Back on your network perhaps (home Wi-Fi, Tailscale turned on): api.js has just asked again.
    if (!foreign.hidden) { retryServer(); return; }
    if (probeTimer) { clearTimeout(probeTimer); probeDelay = 1000; probe(); }
    else if (hiddenAt && Date.now() - hiddenAt > 60_000) {
      current?.refresh?.(); checkForUpdate();
      // What changed on the other devices meanwhile (the tracks of a series, a title removed).
      if (choicesLooked) choices().then(({ lookAgain }) => lookAgain()).catch(() => {});
      // A Home Screen app stays in memory for days: back after a long while, what came out meanwhile.
      if (Date.now() - hiddenAt > 15 * 60_000) lookForNews();
    }
  });
  addEventListener('offline', () => { if (netbar.hidden) toast(tr('Plus de connexion : Mira reprendra dès le retour du réseau.')); });
  addEventListener('online', () => { if (probeTimer) { clearTimeout(probeTimer); probeDelay = 500; probe(); } else current?.refresh?.(); });
  onReachable((up, reason) => (up ? serverUp() : serverDown(reason)));
  onForeignServer((yes) => (yes ? showForeign() : hideForeign()));
  onUnauthorized(() => {
    resetScreens();
    // A guest's link taken back: no sign-in for them, only what happened.
    if (GUEST) { replaceRoute('#/invite'); return; }
    temp.set('mira.notice', tr('Ta session a pris fin. Reconnecte-toi.'));
    replaceRoute('#/connexion');
  });
  // Signed in when Mira opened: only then is a new version news here (a sign-in shows nothing, see offerNews).
  const signedIn = !!session.current;
  route();
  const updateChecked = checkForUpdate();
  // The other screens' code, fetched while nothing happens (not from another server): the first visit to a screen then
  // shows it at once.
  setTimeout(() => yours().then((mine) => {
    if (!mine) return;
    for (const r of ROUTES) if (!r.open) r.load().catch(() => {});
    // A « Regarder ensemble » group this page was in before a reload: joined again, to follow its titles.
    if (session.current) import('./together.js').then(({ together }) => together.restore()).catch(() => {});
    // What is new, unless a newer Mira web is about to replace this one.
    if (signedIn) updateChecked.then(offerNews);
  }), 2500);
}

start();
