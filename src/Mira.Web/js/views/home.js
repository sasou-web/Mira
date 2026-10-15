// Home: a banner of titles, then Continuer à regarder, the latest additions and your favourites, as on Windows, in the
// order chosen in the settings (« Accueil à ta main », home-rows.js), with the latest titles of a library if asked.
// A « Regarder ensemble » group open on Jellyfin is a line under the banner, to join it, while this device is in none.
import { api } from '../api.js';
import { h, icon, clear, episodeCode, duration, seconds, progress, reducedMotion } from '../dom.js';
import { artFor, picture, logo } from '../images.js';
import { session, settings, settingsChanged, isIOS, isStandalone } from '../session.js';
import { homeRows, CONTINUE, LATEST, FAVORITES } from '../home-rows.js';
import { together } from '../together.js';
import { people } from '../player/sync.js';
import {
  row, wideCard, posterCard, skeletonRow, emptyState, errorState, changes, titleHref, playHref, reconcile, cardSig,
} from '../components.js';

const group = (x) => x.SeriesId ?? x.Id;
const played = (x) => Date.parse(x.UserData?.LastPlayedDate ?? '') || 0;

/**
 * Continuer à regarder, as Mira orders it on Windows: a real resume point wins over the next episode of the same
 * series, and each film or series stands where its last activity puts it.
 */
export function orderContinue(resume, next, history) {
  const activity = new Map();
  for (const x of history) { const g = group(x), d = played(x); if (d > (activity.get(g) ?? 0)) activity.set(g, d); }
  const resumed = resume.filter((x) => !x.UserData?.Played && (x.UserData?.PlaybackPositionTicks ?? 0) > 0).sort((a, b) => played(b) - played(a));
  const seen = new Set(), candidates = [];
  for (const x of [...resumed, ...next.filter((x) => !x.UserData?.Played)]) {
    if (!seen.has(group(x))) { seen.add(group(x)); candidates.push(x); }
  }
  const key = (x) => Math.max(played(x), activity.get(group(x)) ?? 0);
  return candidates.map((x, i) => ({ x, i })).sort((a, b) => key(b.x) - key(a.x) || a.i - b.i).map(({ x }) => x);
}

/** Without what was taken out of the row, unless played again since. */
export function withoutHidden(items, hidden, history) {
  const removed = (x) => Date.parse(hidden[group(x)] ?? '') || 0;
  return items.filter((x) => {
    const when = removed(x);
    return !when || played(x) > when || history.some((y) => group(y) === group(x) && played(y) > when);
  });
}

// The account Jellyfin does not let watch together (403 on its groups): asked no more.
let refusedFor = null;

function cacheKey() { return `mira.home.${session.current?.userId}`; }
function readCache() { try { return JSON.parse(localStorage.getItem(cacheKey()) ?? 'null'); } catch { return null; } }
// An answer that arrives once signed out is not kept: nothing of the account stays (session.js).
function writeCache(data) { if (!session.current) return; try { localStorage.setItem(cacheKey(), JSON.stringify(data)); } catch { /* full storage: no cache */ } }

function heroMeta(item) {
  const parts = [];
  if (item.ProductionYear) parts.push(String(item.ProductionYear));
  if (item.Type === 'Series' && item.ChildCount) parts.push(`${item.ChildCount} saison${item.ChildCount > 1 ? 's' : ''}`);
  else if (item.RunTimeTicks) parts.push(duration(seconds(item.RunTimeTicks)));
  if (item.Genres?.length) parts.push(item.Genres.slice(0, 2).join(', '));
  return parts.join(' · ');
}

function slide(entry, index) {
  const { item, play } = entry;
  const target = play?.Type === 'Episode' ? play : item;
  const resume = play && progress(play) > 0;
  const art = picture(artFor(item, 'backdrop'), { kind: 'backdrop', width: innerWidth > 900 ? 1600 : 900, eager: index === 0, label: '' });
  const heading = logo(item) ?? h('h2', { class: 'slide-title' }, item.Name);
  if (heading.tagName === 'IMG') heading.addEventListener('error', () => heading.replaceWith(h('h2', { class: 'slide-title' }, item.Name)), { once: true });
  const label = play?.Type === 'Episode' ? `${resume ? 'Reprendre' : 'Regarder'} ${episodeCode(play)}` : resume ? 'Reprendre' : 'Lecture';
  return h('article', { class: 'slide', 'aria-roledescription': 'diapositive', 'aria-label': item.Name },
    h('a', { href: titleHref(item), tabIndex: -1, 'aria-hidden': 'true' }, art),
    h('div', { class: 'slide-body' },
      heading,
      h('div', { class: 'slide-meta' }, heroMeta(item)),
      h('div', { class: 'slide-actions' },
        item.Type === 'Series' && !play
          ? h('a', { class: 'btn primary', href: titleHref(item) }, icon('play', { size: 20 }), 'Regarder')
          : h('a', { class: 'btn primary', href: playHref(target) }, icon('play', { size: 20 }), label),
        h('a', { class: 'round solid', href: titleHref(item), 'aria-label': `Plus d’infos sur ${item.Name}` }, icon('info')))));
}

function hero(entries) {
  if (!entries.length) return null;
  const track = h('div', { class: 'hero-track' }, entries.map(slide));
  const dots = h('div', { class: 'dots', 'aria-hidden': 'true' }, entries.map((_, i) => h('i', { class: i === 0 ? 'on' : '' })));
  const box = h('section', { class: 'hero', 'aria-label': 'À la une' }, track, entries.length > 1 ? dots : null);
  let touched = false, focused = false, timer = 0;
  const at = () => Math.round(track.scrollLeft / Math.max(1, track.clientWidth));
  const light = () => { const i = at(); dots.querySelectorAll('i').forEach((d, j) => d.classList.toggle('on', j === i)); };
  track.addEventListener('scroll', light, { passive: true });
  for (const name of ['touchstart', 'pointerdown', 'wheel']) track.addEventListener(name, () => { touched = true; }, { passive: true });
  // Nor while the keyboard or a screen reader is on it: a slide does not move away under what is being read.
  box.addEventListener('focusin', () => { focused = true; });
  box.addEventListener('focusout', (e) => { if (!box.contains(e.relatedTarget)) focused = false; });
  // The next slide comes by itself while nobody touches the banner and it is on screen; after the last one, the
  // first comes back with a fade rather than a long sweep back through all of them.
  const advance = () => {
    if (touched || focused || document.hidden || !box.isConnected || entries.length < 2 || scrollY > box.offsetHeight / 2) return;
    const next = (at() + 1) % entries.length;
    if (next) { track.scrollTo({ left: next * track.clientWidth, behavior: 'smooth' }); return; }
    track.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 180, easing: 'ease-in' }).finished.then(() => {
      track.style.scrollBehavior = 'auto'; track.scrollLeft = 0; track.style.scrollBehavior = '';
      track.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 240, easing: 'ease-out' });
    }).catch(() => {});
  };
  box.light = light;
  if (!reducedMotion()) timer = setInterval(advance, 9000);
  box.stop = () => clearInterval(timer);
  return box;
}

function installBanner(onClose) {
  if (!isIOS() || isStandalone() || settings.get('installHintDismissed')) return null;
  const banner = h('div', { class: 'banner', role: 'note' }, icon('add-home'),
    h('span', { class: 'grow' }, h('strong', {}, 'Mira comme une app : '), 'Partager, puis « Sur l’écran d’accueil ».'),
    h('button', { class: 'round flat', 'aria-label': 'Masquer', on: { click: () => { settings.set('installHintDismissed', true); banner.remove(); onClose?.(); } } }, icon('close', { size: 20 })));
  return banner;
}

export function create() {
  const el = h('div', { class: 'view home' });
  let heroBox = null, loadedAt = 0, loading = null, data = readCache(), drawn = '';
  // The parts drawn last time, changed in place when the server's answer differs: a row keeps its cards (pictures
  // and all) and its scroll, and only what changed is drawn again.
  let heroSig = '', titleBox = null, banner, emptyBox = null;
  // Jellyfin's « Regarder ensemble » groups, read at each refresh of Home (never on a clock), and the line of the one shown.
  let groups = [], groupBox = null;
  const sections = new Map();
  const spacer = h('div', { style: { height: '24px' } });
  function section(title, items, card, options) {
    const old = sections.get(title);
    if (!items?.length) { sections.delete(title); return null; }
    if (old) { reconcile(old.querySelector('.row'), items, card); return old; }
    const fresh = row(title, items, card, options);
    sections.set(title, fresh);
    return fresh;
  }

  /** Where the banner and each row were scrolled, to find them there again once the screen is drawn anew. */
  function positions() {
    const rows = {};
    for (const section of el.querySelectorAll('.section')) {
      const name = section.querySelector('.h2')?.textContent, list = section.querySelector('.row');
      if (name && list) rows[name] = list.scrollLeft;
    }
    return { rows, hero: el.querySelector('.hero-track')?.scrollLeft ?? 0 };
  }
  function restore({ rows, hero: left }) {
    for (const section of el.querySelectorAll('.section')) {
      const name = section.querySelector('.h2')?.textContent, list = section.querySelector('.row');
      if (list && rows[name]) list.scrollLeft = rows[name];
    }
    const track = el.querySelector('.hero-track');
    if (track && left) { track.style.scrollBehavior = 'auto'; track.scrollLeft = left; track.style.scrollBehavior = ''; }
  }

  /**
   * « Groupe de Marc · 2 personnes · Rejoindre »: the first group open on Jellyfin, a tap from its invitation page
   * (views/join.js), which joins it and opens its title once the group plays one. None while this device is in one.
   */
  function groupLine() {
    const open = together.group ? null : groups[0];
    if (!open) { groupBox = null; return null; }
    const key = JSON.stringify([open.GroupId, open.GroupName, open.Participants?.length]);
    if (groupBox?.key !== key) {
      groupBox = h('a', { class: 'home-group', href: `#/ensemble/${open.GroupId}` }, icon('users', { size: 20 }),
        h('span', { class: 'grow' }, h('b', {}, open.GroupName), ` · ${people(open.Participants?.length ?? 1)}`),
        h('span', { class: 'chip' }, 'Rejoindre'));
      groupBox.key = key;
    }
    return groupBox;
  }
  async function loadGroups() {
    const account = session.current?.userId;
    if (!account || refusedFor === account) return;
    try { groups = (await api.syncGroups()) ?? []; } catch (error) { if (error?.status === 403) { refusedFor = account; groups = []; } return; }
    if (session.current?.userId === account) render();
  }

  function render() {
    // Nothing new from the server: the screen stays as it is, rows where the finger left them.
    const signature = data ? JSON.stringify([data, settings.get('hiddenResume') ?? {}, settings.get('homeRows'), groupLine()?.key ?? '']) : '';
    if (signature && signature === drawn) return;
    drawn = signature;
    const kept = positions();
    if (!data) {
      heroBox?.stop?.(); heroBox = null; heroSig = ''; sections.clear();
      clear(el).append(h('div', { class: 'hero skeleton', style: { borderRadius: 0 } }), skeletonRow({ wide: true }), skeletonRow());
      return;
    }
    const hidden = settings.get('hiddenResume') ?? {};
    const cont = withoutHidden(orderContinue(data.resume, data.next, data.history), hidden, data.history);
    const entries = [];
    const seen = new Set();
    for (const play of cont.slice(0, 2)) {
      const series = play.Type === 'Episode' ? data.series[play.SeriesId] : null;
      const item = series ?? play;
      if (artFor(item, 'backdrop') && !seen.has(item.Id)) { seen.add(item.Id); entries.push({ item, play }); }
    }
    for (const item of data.latest) {
      if (entries.length >= 6) break;
      if (artFor(item, 'backdrop') && !seen.has(item.Id)) { seen.add(item.Id); entries.push({ item, play: item.Type === 'Series' ? null : item }); }
    }
    // The banner is made again only when its titles change (its timer and its position start over then).
    const sig = JSON.stringify(entries.map(({ item, play }) => [cardSig(item), play ? cardSig(play) : '']));
    if (sig !== heroSig) { heroBox?.stop?.(); heroBox = hero(entries); heroSig = sig; }
    if (banner === undefined) banner = installBanner(() => { banner = null; });
    const top = heroBox ?? (titleBox ??= h('div', { class: 'page' }, h('h1', { class: 'page-title' }, 'Accueil')));
    const rows = homeRows(settings.get('homeRows'), data.libraries ?? []).filter((x) => x.shown).map((x) => {
      if (x.key === CONTINUE) return section(x.title, cont, (y) => wideCard(y), { wide: true });
      if (x.key === LATEST) return section(x.title, data.latest, (y) => posterCard(y), { more: '#/films' });
      if (x.key === FAVORITES) return section(x.title, data.favorites, (y) => posterCard(y));
      return section(x.title, data.rows?.[x.library], (y) => posterCard(y));
    });
    const parts = [top, banner, groupLine(), ...rows].filter(Boolean);
    if (!cont.length && !data.latest.length && !data.favorites.length && !Object.values(data.rows ?? {}).some((x) => x.length)) {
      parts.push(emptyBox ??= emptyState({ symbol: 'films', title: 'Ta bibliothèque est vide', text: 'Les films et séries ajoutés à Jellyfin apparaîtront ici.' }));
    }
    parts.push(spacer);
    // Moved, a row loses its scroll: the screen is put together again only when its parts are not the same.
    if (parts.length !== el.children.length || parts.some((part, i) => el.children[i] !== part)) { el.replaceChildren(...parts); restore(kept); }
  }
  /** The banner's first picture and each row's first cards, decoded: the screen can show whole. */
  const firstPictures = () => Promise.all([...el.querySelectorAll('.hero .slide:first-child img, .row img[loading="eager"]')]
    .map((img) => img.decode?.().catch(() => {})));

  async function load() {
    if (loading) return loading;
    loadGroups();
    loading = (async () => {
      try {
        const [resume, next, history, latest, favorites, libraries] = await Promise.all([
          api.resume(), api.nextUp(), api.history(), api.browse({ limit: 24 }), api.browse({ favorite: true, limit: 24, sort: 'title' }),
          api.libraries().catch(() => []),
        ]);
        // « Nouveau dans Animes »: only the libraries whose row is shown are asked.
        const wanted = homeRows(settings.get('homeRows'), libraries).filter((x) => x.shown && x.library);
        const lists = await Promise.all(wanted.map((x) => api.browse({ library: x.library, limit: 24 }).catch(() => null)));
        const rows = Object.fromEntries(wanted.map((x, i) => [x.library, lists[i]?.Items ?? []]));
        // The banner shows a series for its episode: its backdrop and logo.
        const seriesIds = [...new Set([...(resume?.Items ?? []), ...(next?.Items ?? [])].filter((x) => x.SeriesId).map((x) => x.SeriesId))].slice(0, 4);
        // In the server's order, whatever answers first: the same data reads the same, and the screen stays as it is.
        const found = await Promise.all(seriesIds.map((id) => api.item(id).catch(() => null)));
        const series = Object.fromEntries(seriesIds.map((id, i) => [id, found[i]]));
        data = {
          resume: resume?.Items ?? [], next: next?.Items ?? [], history: (history?.Items ?? []).filter((x) => x.UserData?.LastPlayedDate),
          latest: latest?.Items ?? [], favorites: favorites?.Items ?? [], series,
          libraries: libraries.map(({ Id, Name, CollectionType }) => ({ Id, Name, CollectionType })), rows,
        };
        writeCache(data);
        loadedAt = Date.now();
        render();
      } catch (error) {
        if (!data) { drawn = ''; sections.clear(); heroBox = null; heroSig = ''; clear(el).append(h('div', { class: 'page' }, errorState(error, () => { loading = null; load(); }))); }
      } finally { loading = null; }
    })();
    return loading;
  }

  const onChange = () => { loadedAt = 0; if (el.isConnected) load(); };
  changes.addEventListener('item', onChange);
  // A title removed or put back on another device (account-choices.js).
  const onSetting = (event) => { if (event.detail === 'hiddenResume' && el.isConnected) render(); };
  settingsChanged.addEventListener('change', onSetting);
  // Joined or left a group: the line goes, or comes back with the groups as they are now (the one left may be over).
  let inGroup = together.group?.GroupId ?? null;
  const unwatchGroup = together.watch(() => {
    if ((together.group?.GroupId ?? null) === inGroup) return;
    inGroup = together.group?.GroupId ?? null;
    render(); loadGroups();
  });

  render();
  const first = load();
  // A screen taken out of the page loses the scroll of its rows: Home puts them back where they were.
  let kept = null;
  return {
    el, title: 'Accueil', keep: true,
    // Shown from what the phone kept, or once the server answered, with its first pictures (app.js waits for it).
    ready: (data ? Promise.resolve() : first).then(firstPictures),
    leave() { kept = positions(); },
    enter() {
      // The rows chosen again in the settings: in their new order at once, a library's row once Jellyfin answers.
      render();
      if (kept) { restore(kept); heroBox?.light?.(); kept = null; }
      const missing = data && homeRows(settings.get('homeRows'), data.libraries ?? []).some((x) => x.shown && x.library && !data.rows?.[x.library]);
      if (missing || Date.now() - loadedAt > 30_000) load();
      else loadGroups();
    },
    refresh: load,
    dispose() { heroBox?.stop?.(); changes.removeEventListener('item', onChange); settingsChanged.removeEventListener('change', onSetting); unwatchGroup(); },
  };
}
