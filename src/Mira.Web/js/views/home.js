// Home: a banner of titles, then Continuer à regarder, the latest additions and your favourites, as on Windows.
import { api } from '../api.js';
import { h, icon, clear, episodeCode, duration, seconds, progress, reducedMotion } from '../dom.js';
import { artFor, picture, logo } from '../images.js';
import { session, settings, isIOS, isStandalone } from '../session.js';
import {
  row, wideCard, posterCard, skeletonRow, emptyState, errorState, changes, titleHref, playHref,
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

function cacheKey() { return `mira.home.${session.current?.userId}`; }
function readCache() { try { return JSON.parse(localStorage.getItem(cacheKey()) ?? 'null'); } catch { return null; } }
function writeCache(data) { try { localStorage.setItem(cacheKey(), JSON.stringify(data)); } catch { /* full storage: no cache */ } }

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
  let touched = false, timer = 0;
  const at = () => Math.round(track.scrollLeft / Math.max(1, track.clientWidth));
  const light = () => { const i = at(); dots.querySelectorAll('i').forEach((d, j) => d.classList.toggle('on', j === i)); };
  track.addEventListener('scroll', light, { passive: true });
  for (const name of ['touchstart', 'pointerdown', 'wheel']) track.addEventListener(name, () => { touched = true; }, { passive: true });
  // The next slide comes by itself while nobody touches the banner and it is on screen; after the last one, the
  // first comes back with a fade rather than a long sweep back through all of them.
  const advance = () => {
    if (touched || document.hidden || !box.isConnected || entries.length < 2 || scrollY > box.offsetHeight / 2) return;
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

  function render() {
    // Nothing new from the server: the screen stays as it is, rows where the finger left them.
    const signature = data ? JSON.stringify([data, settings.get('hiddenResume') ?? {}]) : '';
    if (signature && signature === drawn) return;
    drawn = signature;
    const kept = positions();
    heroBox?.stop?.();
    clear(el);
    if (!data) {
      el.append(h('div', { class: 'hero skeleton', style: { borderRadius: 0 } }), skeletonRow({ wide: true }), skeletonRow());
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
    heroBox = hero(entries);
    const banner = installBanner();
    if (!heroBox) el.append(h('div', { class: 'page' }, h('h1', { class: 'page-title' }, 'Accueil')));
    el.append(heroBox ?? '', banner ?? '',
      row('Continuer à regarder', cont, (x) => wideCard(x), { wide: true }) ?? '',
      row('Ajouts récents', data.latest, (x) => posterCard(x), { more: '#/films' }) ?? '',
      row('Tes favoris', data.favorites, (x) => posterCard(x)) ?? '');
    if (!cont.length && !data.latest.length && !data.favorites.length) {
      el.append(emptyState({ symbol: 'films', title: 'Ta bibliothèque est vide', text: 'Les films et séries ajoutés à Jellyfin apparaîtront ici.' }));
    }
    el.append(h('div', { style: { height: '24px' } }));
    restore(kept);
  }

  async function load() {
    if (loading) return loading;
    loading = (async () => {
      try {
        const [resume, next, history, latest, favorites] = await Promise.all([
          api.resume(), api.nextUp(), api.history(), api.browse({ limit: 24 }), api.browse({ favorite: true, limit: 24, sort: 'title' }),
        ]);
        // The banner shows a series for its episode: its backdrop and logo.
        const seriesIds = [...new Set([...(resume?.Items ?? []), ...(next?.Items ?? [])].filter((x) => x.SeriesId).map((x) => x.SeriesId))].slice(0, 4);
        // In the server's order, whatever answers first: the same data reads the same, and the screen stays as it is.
        const found = await Promise.all(seriesIds.map((id) => api.item(id).catch(() => null)));
        const series = Object.fromEntries(seriesIds.map((id, i) => [id, found[i]]));
        data = {
          resume: resume?.Items ?? [], next: next?.Items ?? [], history: (history?.Items ?? []).filter((x) => x.UserData?.LastPlayedDate),
          latest: latest?.Items ?? [], favorites: favorites?.Items ?? [], series,
        };
        writeCache(data);
        loadedAt = Date.now();
        render();
      } catch (error) {
        if (!data) { clear(el).append(h('div', { class: 'page' }, errorState(error, () => { loading = null; load(); }))); }
      } finally { loading = null; }
    })();
    return loading;
  }

  const onChange = () => { loadedAt = 0; if (el.isConnected) load(); };
  changes.addEventListener('item', onChange);

  render();
  load();
  // A screen taken out of the page loses the scroll of its rows: Home puts them back where they were.
  let kept = null;
  return {
    el, title: 'Accueil', keep: true,
    leave() { kept = positions(); },
    enter() {
      if (kept) { restore(kept); heroBox?.light?.(); kept = null; }
      if (Date.now() - loadedAt > 30_000) load();
    },
    refresh: load,
    dispose() { heroBox?.stop?.(); changes.removeEventListener('item', onChange); },
  };
}
