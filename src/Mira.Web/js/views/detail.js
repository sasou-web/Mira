// A title's page: backdrop and logo, play or resume, watched and favourite, overview, cast and, for a series, its
// seasons (long ones in slices of 100, as on Windows) and episodes.
import { api } from '../api.js';
import { h, icon, clear, duration, seconds, progress, remaining, episodeCode, plural } from '../dom.js';
import { artFor, picture, logo } from '../images.js';
import {
  row, posterCard, personCard, errorState, spinner, changes, playHref, setPlayed, setFavorite, toast, sheet, withActions, knownItem,
} from '../components.js';
import { goBack, replaceRoute } from '../app.js';
import { settings } from '../session.js';
import { trackText, chooseTracks } from '../player/tracks.js';

const SLICE = 100;
const seasonOf = (x) => x.ParentIndexNumber ?? 1;

/** The season list: a season, or slices of a long one (« Saison 1 · 101–200 »). */
export function episodePages(episodes) {
  const seasons = new Map();
  for (const e of episodes) { const s = seasonOf(e); if (!seasons.has(s)) seasons.set(s, []); seasons.get(s).push(e); }
  const pages = [];
  for (const [season, list] of [...seasons].sort((a, b) => a[0] - b[0])) {
    const name = season === 0 ? 'Épisodes spéciaux' : `Saison ${season}`;
    if (list.length <= SLICE) { pages.push({ season, skip: 0, take: list.length, label: name }); continue; }
    for (let skip = 0; skip < list.length; skip += SLICE) {
      const take = Math.min(SLICE, list.length - skip);
      const first = list[skip].IndexNumber ?? skip + 1, last = list[skip + take - 1].IndexNumber ?? skip + take;
      pages.push({ season, skip, take, label: first === last ? `${name} · ${first}` : `${name} · ${first}–${last}` });
    }
  }
  return pages;
}
const pageEpisodes = (episodes, page) => episodes.filter((x) => seasonOf(x) === page.season).slice(page.skip, page.skip + page.take);
function pageHolding(pages, episodes, episode) {
  if (!episode) return null;
  const position = episodes.filter((x) => seasonOf(x) === seasonOf(episode)).findIndex((x) => x.Id === episode.Id);
  return position < 0 ? null : pages.find((p) => p.season === seasonOf(episode) && position >= p.skip && position < p.skip + p.take) ?? null;
}

function meta(item, episodes) {
  const parts = [];
  if (item.ProductionYear) parts.push(h('span', {}, String(item.ProductionYear)));
  if (item.Type === 'Series') {
    const seasons = new Set(episodes.map(seasonOf).filter((s) => s > 0)).size || item.ChildCount;
    if (seasons) parts.push(h('span', {}, plural(seasons, 'saison', 'saisons')));
  } else if (item.RunTimeTicks) parts.push(h('span', {}, duration(seconds(item.RunTimeTicks))));
  if (item.CommunityRating) parts.push(h('span', { class: 'rating' }, icon('star'), item.CommunityRating.toFixed(1).replace('.', ',')));
  if (item.OfficialRating) parts.push(h('span', { class: 'cert' }, item.OfficialRating));
  return h('div', { class: 'detail-meta' }, parts.map((p, i) => (i ? [h('span', { class: 'dot', 'aria-hidden': 'true' }), p] : p)));
}

function overview(text) {
  if (!text) return null;
  const body = h('p', { class: 'overview clamp-4' }, text);
  const more = h('button', { class: 'more-link', hidden: true, on: { click: () => { body.classList.remove('clamp-4'); more.remove(); } } }, 'Plus');
  requestAnimationFrame(() => { if (body.scrollHeight > body.clientHeight + 2) more.hidden = false; });
  return h('div', {}, body, more);
}

function credits(item) {
  const people = item.People ?? [];
  const link = (p) => (p.Id ? h('a', { href: `#/personne/${p.Id}` }, p.Name) : h('span', {}, p.Name));
  const join = (list) => list.flatMap((p, i) => (i ? [', ', link(p)] : [link(p)]));
  const directors = people.filter((p) => p.Type === 'Director').slice(0, 3);
  const actors = people.filter((p) => p.Type === 'Actor').slice(0, 4);
  if (!directors.length && !actors.length) return null;
  return h('div', { class: 'credits' },
    directors.length ? h('div', {}, directors.length > 1 ? 'Réalisation : ' : 'Réalisé par ', join(directors)) : null,
    actors.length ? h('div', {}, 'Avec ', join(actors)) : null);
}

function episodeRow(episode, current) {
  const art = picture(artFor(episode, 'still'), { kind: 'wide', width: 200, label: `${episode.IndexNumber ?? ''}` });
  const done = progress(episode);
  if (done > 0) art.append(h('div', { class: 'progress' }, h('i', { style: { width: `${(done * 100).toFixed(1)}%` } })));
  else if (episode.UserData?.Played) art.append(h('div', { class: 'badge-watched', title: 'Vu' }, icon('check')));
  const facts = [episode.RunTimeTicks ? duration(seconds(episode.RunTimeTicks)) : '', remaining(episode)].filter(Boolean).join(' · ');
  const link = h('a', { class: ['episode', 'card', current && 'current'], href: playHref(episode), 'aria-label': `Lire ${episodeCode(episode)} ${episode.Name ?? ''}` },
    art,
    h('div', {}, h('div', { class: 'ep-title clamp-2' }, `${episode.IndexNumber != null ? `${episode.IndexNumber}. ` : ''}${episode.Name ?? ''}`), h('div', { class: 'ep-meta' }, facts)),
    episode.Overview ? h('p', { class: 'ep-overview clamp-2' }, episode.Overview) : null);
  // A long press offers the episode's actions (watched, favourite) instead of playing it, as on the cards.
  withActions(link, episode);
  return link;
}

export function create({ id, query }) {
  const el = h('div', { class: 'view detail' });
  const back = h('button', { class: 'round floating-back', 'aria-label': 'Retour', on: { click: () => goBack('#/') } }, icon('back'));
  let item = null, episodes = [], next = null, similar = [], page = null, stale = false, loadedAt = 0;
  let actionsBox = null, toolsBox = null, similarBox = null;
  // The tracks Lecture starts with: the streams of the title it plays, and the choice made here, if any.
  let streams = null, chosen = null;
  const wanted = query.get('episode');

  const playTarget = () => (item.Type === 'Series' ? next ?? episodes[0] : item);

  /** The tracks the player will start with: the choice made here, else its own (the series' last, the settings). */
  function tracksFor(target) {
    if (!streams || streams.for !== target.Id) return null;
    if (chosen?.for === target.Id) return chosen;
    const memory = target.SeriesId ? settings.get('seriesTracks')?.[target.SeriesId] ?? null : null;
    return chooseTracks(streams.source, { memory, audioOrder: settings.get('audioLanguages'), subtitleOrder: settings.get('subtitleLanguages') });
  }

  function primaryAction() {
    const target = playTarget();
    if (!target) return h('div', { class: 'detail-actions' }, h('button', { class: 'btn primary block', disabled: true }, 'Aucun épisode à lire'));
    const resume = progress(target) > 0;
    let label = resume ? 'Reprendre' : 'Lecture';
    if (item.Type === 'Series') label = `${resume ? 'Reprendre' : 'Regarder'} ${episodeCode(target)}`;
    const left = resume ? remaining(target) : '';
    return h('div', { class: 'detail-actions' },
      h('a', { class: 'btn primary block', href: playHref(target, false, chosen?.for === target.Id ? chosen : null) }, icon('play', { size: 20 }), label),
      resume ? h('div', { style: { display: 'grid', gap: '6px' } },
        h('div', { class: 'progress-line' }, h('i', { style: { width: `${(progress(target) * 100).toFixed(1)}%` } })),
        h('div', { class: 'meta' }, left)) : null,
      tracksLine(target));
  }

  /** « Audio : Japonais · Sous-titres : Français », a tap away from the other tracks. */
  function tracksLine(target) {
    const pick = tracksFor(target);
    if (!pick) return null;
    const all = streams.source.MediaStreams ?? [];
    const audios = all.filter((s) => s.Type === 'Audio'), subs = all.filter((s) => s.Type === 'Subtitle');
    if (audios.length < 2 && !subs.length) return null;
    const audio = audios.find((s) => s.Index === pick.audio), sub = subs.find((s) => s.Index === pick.subtitle);
    const name = (s, list) => trackText(s, list.indexOf(s) + 1).label + (s.IsForced ? ' (forcés)' : '');
    // A single audio track of unknown language says nothing worth a line.
    const showAudio = audio && (audios.length > 1 || audio.Language);
    return h('button', { class: 'tracks-line', 'aria-haspopup': 'dialog', on: { click: () => tracksSheet(target, pick) } },
      icon('subtitles', { size: 20 }),
      h('span', { class: 'grow' },
        showAudio ? h('span', {}, 'Audio : ', h('b', {}, name(audio, audios))) : null,
        h('span', {}, 'Sous-titres : ', h('b', {}, sub ? name(sub, subs) : 'aucun'))),
      icon('right', { size: 18 }));
  }

  function tracksSheet(target, pick) {
    const all = streams.source.MediaStreams ?? [];
    const audios = all.filter((s) => s.Type === 'Audio'), subs = all.filter((s) => s.Type === 'Subtitle');
    const choose = (change) => { chosen = { audio: pick.audio, subtitle: pick.subtitle, ...change, for: target.Id }; refreshActions(); };
    const details = (s, list) => [trackText(s, list.indexOf(s) + 1).details, s.Type === 'Subtitle' && !s.IsTextSubtitleStream ? 'incrustés dans l’image' : '']
      .filter(Boolean).join(' · ');
    sheet({
      title: item.Type === 'Series' ? `Audio et sous-titres · ${episodeCode(target)}` : 'Audio et sous-titres',
      items: [
        ...(audios.length > 1 ? [{ heading: 'Audio' }, ...audios.map((s) => ({
          label: trackText(s, audios.indexOf(s) + 1).label, sub: details(s, audios), selected: s.Index === pick.audio, run: () => choose({ audio: s.Index }),
        }))] : []),
        { heading: 'Sous-titres' },
        { label: 'Aucun', selected: pick.subtitle === -1 || !subs.some((s) => s.Index === pick.subtitle), run: () => choose({ subtitle: -1 }) },
        ...subs.map((s) => ({ label: trackText(s, subs.indexOf(s) + 1).label, sub: details(s, subs), selected: s.Index === pick.subtitle, run: () => choose({ subtitle: s.Index }) })),
      ],
    });
  }

  /** Lecture, its tracks and Du début again, without rebuilding the page. */
  function refreshActions() {
    if (!item || !actionsBox?.isConnected) return;
    const actions = primaryAction(), secondary = tools();
    actionsBox.replaceWith(actions); toolsBox.replaceWith(secondary);
    actionsBox = actions; toolsBox = secondary;
  }

  /** The streams of the title Lecture plays: a film's are in its details, an episode's are asked for. */
  async function loadStreams() {
    const target = playTarget();
    if (!target || streams?.for === target.Id) return;
    if (target === item) { streams = item.MediaSources?.[0] ? { for: item.Id, source: item.MediaSources[0] } : null; return; }
    const full = await api.item(target.Id).catch(() => null);
    if (!full?.MediaSources?.[0] || playTarget()?.Id !== target.Id) return;
    streams = { for: target.Id, source: full.MediaSources[0] };
    refreshActions();
  }

  function tools() {
    const target = playTarget();
    const played = !!item.UserData?.Played, favorite = !!item.UserData?.IsFavorite;
    const tool = (symbol, label, on, run) => h('button', { class: ['tool', on && 'on'], 'aria-pressed': String(!!on), on: { click: run } }, icon(symbol), label);
    return h('div', { class: 'detail-secondary' },
      tool(favorite ? 'heart-fill' : 'heart', favorite ? 'Favori' : 'Favoris', favorite, () => setFavorite(item, !favorite)),
      tool('check', played ? 'Vu' : 'Marquer vu', played, async () => { await setPlayed(item, !played); if (item.Type === 'Series') load(true); }),
      target && progress(target) > 0
        ? h('a', { class: 'tool', href: playHref(target, true, chosen?.for === target.Id ? chosen : null) }, icon('back10'), 'Du début')
        : null);
  }

  function seasonsSection() {
    if (item.Type !== 'Series') return null;
    if (!episodes.length) return h('div', { class: 'state' }, icon('series'), h('h2', {}, 'Aucun épisode'), h('p', {}, 'Jellyfin n’a encore trouvé aucun épisode de cette série.'));
    const pages = episodePages(episodes);
    const focus = episodes.find((x) => x.Id === wanted) ?? next;
    page ??= pageHolding(pages, episodes, focus) ?? pages[0];
    const list = h('div', { role: 'list' });
    const chips = h('div', { class: 'chips', role: 'tablist', 'aria-label': 'Saisons' });
    const show = (p) => {
      page = p;
      chips.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-selected', String(c.dataset.key === `${p.season}:${p.skip}`)));
      chips.querySelectorAll('.chip').forEach((c) => c.classList.toggle('on', c.dataset.key === `${p.season}:${p.skip}`));
      clear(list).append(...pageEpisodes(episodes, p).map((e) => { const r = episodeRow(e, e.Id === (wanted ?? next?.Id)); r.setAttribute('role', 'listitem'); return r; }));
    };
    for (const p of pages) {
      chips.append(h('button', { class: 'chip', role: 'tab', dataset: { key: `${p.season}:${p.skip}` }, on: { click: () => show(p) } }, p.label));
    }
    show(pages.find((p) => p.season === page.season && p.skip === page.skip) ?? pages[0]);
    // The chosen season comes into view in its strip; the page itself does not move (scrollIntoView would scroll it).
    requestAnimationFrame(() => {
      const on = chips.querySelector('.chip.on');
      if (on) chips.scrollLeft += on.getBoundingClientRect().left - chips.getBoundingClientRect().left - (chips.clientWidth - on.offsetWidth) / 2;
    });
    return h('section', { class: 'seasons' },
      h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, 'Épisodes'), h('span', { class: 'meta' }, plural(episodes.length, 'épisode', 'épisodes'))),
      pages.length > 1 ? h('div', { style: { padding: '0 var(--gutter) 8px' } }, chips) : null,
      list);
  }

  function render() {
    const headTitle = logo(item, 420, 'detail-logo') ?? h('h1', { class: 'detail-title' }, item.Name);
    if (headTitle.tagName === 'IMG') {
      headTitle.addEventListener('error', () => headTitle.replaceWith(h('h1', { class: 'detail-title' }, item.Name)), { once: true });
      headTitle.setAttribute('role', 'heading'); headTitle.setAttribute('aria-level', '1');
    }
    const cast = (item.People ?? []).filter((p) => p.Type === 'Actor' && p.Id).slice(0, 20);
    // Without a backdrop, the poster stands above the title instead of an empty banner.
    const backdrop = artFor(item, 'backdrop'), poster = backdrop ? null : artFor(item, 'poster');
    clear(el).append(back,
      h('div', { class: 'detail-top' },
        h('div', { class: ['detail-backdrop', !backdrop && 'none'] }, backdrop ? picture(backdrop, { kind: 'backdrop', width: innerWidth > 900 ? 1600 : 900, eager: true }) : null),
        h('div', { class: 'detail-head' },
          poster ? picture(poster, { kind: 'poster', width: 140, eager: true, className: 'detail-poster' }) : null,
          headTitle, meta(item, episodes), (actionsBox = primaryAction()), (toolsBox = tools()), overview(item.Overview),
          item.Genres?.length ? h('div', { class: 'genres' }, item.Genres.join(' · ')) : null,
          credits(item))),
      seasonsSection() ?? '',
      row('Distribution', cast, (p) => personCard(p), { people: true }) ?? '',
      (similarBox = h('div', {}, row('Titres similaires', similar, (x) => posterCard(x)) ?? '')),
      h('div', { style: { height: '32px' } }));
  }

  /**
   * What the card that was touched already knows (name, pictures, year), drawn at once: the screen slides in with its
   * picture and title rather than a spinner, and the rest arrives in place.
   */
  function seed(known) {
    const backdrop = artFor(known, 'backdrop'), poster = backdrop ? null : artFor(known, 'poster');
    const headTitle = logo(known, 420, 'detail-logo') ?? h('h1', { class: 'detail-title' }, known.Name);
    if (headTitle.tagName === 'IMG') headTitle.addEventListener('error', () => headTitle.replaceWith(h('h1', { class: 'detail-title' }, known.Name)), { once: true });
    clear(el).append(back,
      h('div', { class: 'detail-top' },
        h('div', { class: ['detail-backdrop', !backdrop && 'none'] }, backdrop ? picture(backdrop, { kind: 'backdrop', width: innerWidth > 900 ? 1600 : 900, eager: true }) : null),
        h('div', { class: 'detail-head' },
          poster ? picture(poster, { kind: 'poster', width: 140, eager: true, className: 'detail-poster' }) : null,
          headTitle, meta(known, []),
          h('div', { class: 'detail-actions' }, h('div', { class: 'skeleton', style: { height: '48px', borderRadius: 'var(--r-m)' } })),
          h('div', { class: 'skeleton line', style: { width: '90%' } }), h('div', { class: 'skeleton line', style: { width: '75%' } }))));
  }

  async function load(quiet = false) {
    const known = !item && !quiet ? knownItem(id) : null;
    if (known && known.Type !== 'Episode') seed(known);
    else if (!quiet && !item) clear(el).append(back, h('div', { style: { paddingTop: '40vh' } }, spinner()));
    try {
      const fresh = await api.item(id);
      let eps = [], nextUp = null;
      if (fresh.Type === 'Series') {
        [eps, nextUp] = await Promise.all([api.episodes(id).then((r) => r?.Items ?? []), api.seriesNext(id).catch(() => null)]);
      } else if (fresh.Type === 'Episode' && fresh.SeriesId) {
        replaceRoute(`#/titre/${fresh.SeriesId}?episode=${fresh.Id}`);
        return;
      }
      item = fresh; episodes = eps; next = nextUp; stale = false; loadedAt = Date.now();
      document.title = `${item.Name} · Mira`;
      // A film's streams come with it, fresh; an episode's are asked for once its page is drawn.
      if (item.Type !== 'Series') streams = null;
      loadStreams();
      render();
      if (!similar.length) {
        // Only its row is drawn when it arrives: the page does not jump or flash under the finger.
        api.similar(id).then((r) => {
          similar = r?.Items ?? [];
          if (similar.length && similarBox) clear(similarBox).append(row('Titres similaires', similar, (x) => posterCard(x)) ?? '');
        }).catch(() => {});
      }
    } catch (error) {
      if (!item) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
      else toast(error.message);
    }
  }

  const onChange = (e) => {
    const changed = e.detail.item;
    if (!item) return;
    if (changed.Id === item.Id || changed.SeriesId === item.Id || episodes.some((x) => x.Id === changed.Id)) {
      stale = true;
      if (el.isConnected) load(true);
    }
  };
  changes.addEventListener('item', onChange);

  load();
  return {
    el, title: 'Titre', keep: true,
    enter() { if (stale || Date.now() - loadedAt > 5 * 60_000) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); },
  };
}
