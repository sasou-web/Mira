// A title's page: backdrop and logo, play or resume, watched and favourite, overview, genres, cast and studios, for a
// series its seasons (seasons.js) and episodes, then its saga, its studio's other titles, bonus and similar titles.
import { api } from '../api.js';
import { h, icon, clear, duration, seconds, progress, remaining, endsAt, episodeCode, WIDE, wideScreen } from '../dom.js';
import { tr, trn, number, language as shownLanguage } from '../i18n.js';
import { artFor, picture, logo } from '../images.js';
import {
  row, posterCard, extraCard, cardSig, personCard, errorState, spinner, changes, playHref, titleHref, setPlayed, setFavorite, toast, sheet, withActions,
  knownItem, overview, changed,
} from '../components.js';
import { goBack, replaceRoute } from '../app.js';
import { settings, session } from '../session.js';
import { trackText, chooseTracks, qualityText, languageName } from '../player/tracks.js';
import { searchLanguages, sortRemote, remoteText, privacyText, keptText, arrivedSubtitle } from '../subtitles.js';
import { appleNative } from '../player/video.js';
import { sleep, sleepChoices } from '../player/sleep.js';
import { together } from '../together.js';
import { groupText, people, groupStatus, namesText, groupPlayLabel, groupLabel } from '../player/sync.js';
import { startPosition } from '../player/stream.js';
import { inviteSheet } from '../invite.js';
import { follows, followSheet, followedSheet } from '../follow.js';
import { followLine as followText, followNote, pickLine } from '../torlink.js';
import { seasonOf, episodePages, pageEpisodes, pageHolding, marksSeasons, seasonMark, nextElsewhere } from '../seasons.js';
import { titleToolsSheet } from '../identify-sheet.js';
import { calendar, missingText, nextText, dayText, dayTitle, today } from '../calendar.js';
import { plainOverview } from '../overview.js';

/** Year, length, ratings, then what the page adds: the picture of the file Lecture plays and when it ends. */
function meta(item, episodes, extra = []) {
  const parts = [];
  if (item.ProductionYear) parts.push(h('span', {}, String(item.ProductionYear)));
  if (item.Type === 'Series') {
    const seasons = new Set(episodes.map(seasonOf).filter((s) => s > 0)).size || item.ChildCount;
    if (seasons) parts.push(h('span', {}, trn(seasons, '{n} saison', '{n} saisons')));
  } else if (item.RunTimeTicks) parts.push(h('span', {}, duration(seconds(item.RunTimeTicks))));
  if (item.CommunityRating) parts.push(h('span', { class: 'rating' }, icon('star'), number(item.CommunityRating, { minimumFractionDigits: 1, maximumFractionDigits: 1 })));
  if (item.OfficialRating) parts.push(h('span', { class: 'cert' }, item.OfficialRating));
  for (const fact of extra.filter(Boolean)) parts.push(h('span', {}, fact));
  return h('div', { class: 'detail-meta' }, parts.map((p, i) => (i ? [h('span', { class: 'dot', 'aria-hidden': 'true' }), p] : p)));
}

/** Who made it, each name a link to their titles in the library: directors, actors, studios (« Studio Ghibli »). */
function credits(item) {
  const people = item.People ?? [];
  const link = (p, href) => (p.Id ? h('a', { href: `${href}${p.Id}` }, p.Name) : h('span', {}, p.Name));
  const join = (list, href = '#/personne/') => list.flatMap((p, i) => (i ? [', ', link(p, href)] : [link(p, href)]));
  const directors = people.filter((p) => p.Type === 'Director').slice(0, 3);
  const actors = people.filter((p) => p.Type === 'Actor').slice(0, 4);
  const studios = (item.Studios ?? []).filter((x) => x.Name).slice(0, 3);
  if (!directors.length && !actors.length && !studios.length) return null;
  return h('div', { class: 'credits' },
    directors.length ? h('div', {}, directors.length > 1 ? tr('Réalisation : ') : tr('Réalisé par '), join(directors)) : null,
    actors.length ? h('div', {}, tr('Avec '), join(actors)) : null,
    studios.length ? h('div', {}, studios.length > 1 ? tr('Studios : ') : tr('Studio : '), join(studios, '#/studio/')) : null);
}

/** Its genres, each one the library of its kind (Films or Séries) filtered on it. */
function genres(item) {
  if (!item.Genres?.length) return null;
  const tab = item.Type === 'Series' ? '#/series' : '#/films';
  return h('div', { class: 'genres', role: 'list', 'aria-label': tr('Genres') },
    item.Genres.map((name) => h('a', { class: 'chip', role: 'listitem', href: `${tab}?genre=${encodeURIComponent(name)}` }, name)));
}

/** What an episode's row shows: when it changes, the row is drawn again, and only then. */
const rowState = (e, current) => [e.UserData?.Played ? 1 : 0, e.UserData?.PlaybackPositionTicks ?? 0, current ? 1 : 0, e.Name ?? '', e.ImageTags?.Primary ?? '', e.RunTimeTicks ?? 0].join('|');

function episodeRow(episode, current) {
  const art = picture(artFor(episode, 'still'), { kind: 'wide', width: 200, label: `${episode.IndexNumber ?? ''}` });
  const done = progress(episode);
  if (done > 0) art.append(h('div', { class: 'progress' }, h('i', { style: { width: `${(done * 100).toFixed(1)}%` } })));
  else if (episode.UserData?.Played) art.append(h('div', { class: 'badge-watched', title: tr('Vu') }, icon('check')));
  const facts = [episode.RunTimeTicks ? duration(seconds(episode.RunTimeTicks)) : '', remaining(episode)].filter(Boolean).join(' · ');
  const link = h('a', {
    class: ['episode', 'card', current && 'current'], href: playHref(episode), 'aria-label': tr('Lire {name}', { name: `${episodeCode(episode)} ${episode.Name ?? ''}` }),
    dataset: { id: episode.Id, state: rowState(episode, current) },
  },
    art,
    h('div', {}, h('div', { class: 'ep-title clamp-2' }, `${episode.IndexNumber != null ? `${episode.IndexNumber}. ` : ''}${episode.Name ?? ''}`), h('div', { class: 'ep-meta' }, facts)),
    episode.Overview ? h('p', { class: 'ep-overview clamp-2' }, plainOverview(episode.Overview, { flat: true })) : null);
  // A long press offers the episode's actions (watched, favourite) instead of playing it, as on the cards.
  withActions(link, episode, { inTitle: true });
  return link;
}

/**
 * The poster beside the title on a tablet or a computer; on a phone only without a backdrop, where it stands above the
 * title instead of an empty banner.
 */
const posterFor = (item, backdrop, wide) => (wide || !backdrop ? artFor(item, 'poster') : null);

export function create({ id, query }) {
  const el = h('div', { class: 'view detail' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/') } }, icon('back'));
  let item = null, episodes = [], next = null, similar = [], sagas = null, studio = null, extras = [], page = null, stale = false, loadedAt = 0, loadSeq = 0;
  let followed = null; // the series this administrator follows (follow.js), null until known or for anyone else
  let actionsBox = null, toolsBox = null, sagaBox = null, studioBox = null, extrasBox = null, similarBox = null, metaBox = null, seasonsBox = null, listBox = null, shownIds = '';
  // Above the episodes: the season's mark (« Marquer la saison comme vue ») and, another season shown, « À suivre ».
  let seasonBar = null, marking = false, pages = [], showPage = () => {};
  // The tracks Lecture starts with: the streams of the title it plays, and the choice made here, if any.
  let streams = null, chosen = null;
  // The subtitle extensions of Jellyfin (« Open Subtitles »), when this account may search with them.
  let finders = [];
  // An administrator of Jellyfin: « … » on a film or series, for « Identifier… » and « Actualiser les métadonnées ».
  let admin = false;
  // The episode the page was opened for (« Voir la série » from it): marked, its season shown, until Lecture moves on.
  let wanted = query.get('episode');
  // A series' episodes missing and coming (calendar.js), once Jellyfin's TheTVDB extension told it; above its episodes.
  let schedule = null;
  const calendarBar = h('div', { class: 'calendar-bar' });

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
    if (!target) return h('div', { class: 'detail-actions' }, h('button', { class: 'btn primary block', disabled: true }, tr('Aucun épisode à lire')));
    const resume = progress(target) > 0;
    let label = resume ? tr('Reprendre') : tr('Lecture');
    if (item.Type === 'Series') label = resume ? tr('Reprendre {code}', { code: episodeCode(target) }) : tr('Regarder {code}', { code: episodeCode(target) });
    // In a group, Lecture starts the title for everyone, from where this account left it; on the group's own title,
    // it joins it where it is.
    if (together.group) {
      label = groupPlayLabel({
        code: item.Type === 'Series' ? episodeCode(target) : '',
        resumeAt: startPosition(target, { fromStart: false, resume: settings.get('resume') }) / 1e7,
        groupTitle: together.queue?.itemId === target.Id,
      });
    }
    const left = resume ? remaining(target) : '';
    return h('div', { class: 'detail-actions' },
      h('a', { class: 'btn primary block', href: playHref(target, false, chosen?.for === target.Id ? chosen : null) }, icon('play', { size: 20 }), label),
      resume ? h('div', { class: 'detail-progress' },
        h('div', { class: 'progress-line' }, h('i', { style: { width: `${(progress(target) * 100).toFixed(1)}%` } })),
        h('div', { class: 'meta' }, left)) : null,
      tracksLine(target), sleepLine(), togetherLine(), followLine());
  }

  /**
   * « Télécharger les nouveaux épisodes » (an administrator, follow.js): the PC of Jellyfin downloads them on its own.
   * Once followed, where it stands: an episode about to go first (time to cancel it), else what the last search found.
   */
  function followLine() {
    if (item.Type !== 'Series' || !followed) return null;
    const follow = followed.find((x) => x.seriesId?.toLowerCase() === item.Id.toLowerCase());
    const now = Date.now();
    const waiting = follow?.picks?.find((x) => x.state === 'waiting');
    const sub = !follow ? '' : waiting ? pickLine(waiting, now) : followNote(follow.note) || followText(follow, now);
    return h('button', { class: 'group-line', 'aria-haspopup': 'dialog', on: { click: () => (follow ? followedSheet(follow, loadFollows) : followSheet(item, loadFollows)) } },
      icon('download', { size: 20 }),
      h('span', { class: 'grow' }, follow ? h('span', {}, tr('Nouveaux épisodes : '), h('b', {}, tr('suivis'))) : h('span', {}, tr('Télécharger les nouveaux épisodes')),
        sub ? h('span', { class: waiting ? 'group-state alert' : 'group-state' }, sub) : ''),
      icon('right', { size: 18 }));
  }
  /** The series followed, read again with the page: the line shows only once the extension answered. */
  function loadFollows() {
    follows().then((list) => {
      if (JSON.stringify(list) === JSON.stringify(followed)) return;
      followed = list;
      refreshActions();
    });
  }

  /**
   * « Minuterie : désactivée », for Apple's player, which has no room for Mira's menus: chosen here, it pauses the film
   * after its minutes, or stops at the end of the episode. Mira's own player has it in its menu.
   */
  function sleepLine() {
    if (!appleNative()) return null;
    const status = sleep.status(Date.now());
    return h('button', { class: 'tracks-line', 'aria-haspopup': 'dialog', on: { click: sleepSheet } },
      icon('clock', { size: 20 }),
      h('span', { class: 'grow' }, h('span', {}, tr('Minuterie : '), h('b', {}, status ? status.replace(/^./, (c) => c.toLowerCase()) : tr('désactivée')))),
      icon('right', { size: 18 }));
  }
  function sleepSheet() {
    sheet({
      title: tr('Minuterie'),
      items: sleepChoices(sleep, { episode: item.Type !== 'Movie', now: Date.now() })
        .map(({ label, sub, selected, minutes }) => ({ label, sub, selected, run: () => { sleep.choose(minutes, Date.now()); refreshActions(); } })),
    });
  }

  /**
   * « Regarder ensemble »: a group of Jellyfin (SyncPlay) to join or start; in one, Lecture puts the title on for the
   * whole group (together.js).
   */
  function togetherLine() {
    const group = together.group;
    // Under it, what the group is doing (a wait, a pause), else who is there: back from Apple's player too.
    const status = groupStatus(group, session.current?.userName);
    const others = (group?.Participants?.length ?? 0) > 1;
    const sub = !status ? '' : status.alert ? status.text : others ? namesText(group.Participants) : tr('Personne d’autre pour l’instant : invite quelqu’un.');
    return h('button', { class: 'group-line', 'aria-haspopup': 'dialog', on: { click: togetherSheet } },
      icon('users', { size: 20 }),
      h('span', { class: 'grow' }, group ? h('span', {}, tr('Ensemble : '), h('b', {}, groupText(group))) : h('span', {}, tr('Regarder ensemble')),
        sub ? h('span', { class: status.alert ? 'group-state alert' : 'group-state' }, sub) : ''),
      icon('right', { size: 18 }));
  }
  async function togetherSheet() {
    const group = together.group;
    const about = (text) => h('p', { class: 'sheet-note' }, text);
    if (group) {
      sheet({
        title: tr('Regarder ensemble'),
        body: about(tr('Lecture lance un titre pour tout le groupe, au même moment sur chaque appareil. Pause, lecture et déplacements sont partagés.')),
        items: [
          { label: groupText(group), sub: group.Participants.join(', '), selected: true },
          { symbol: 'link', label: tr('Inviter quelqu’un'), sub: tr('Un lien à envoyer : ton ami l’ouvre et rejoint le groupe.'), run: () => inviteSheet(together.group) },
          { label: tr('Quitter le groupe'), danger: true, run: () => together.leave() },
        ],
      });
      return;
    }
    let groups;
    try { groups = await together.groups(); } catch (error) {
      toast(error?.status === 403 ? tr('Jellyfin ne permet pas à ce compte de regarder ensemble (tableau de bord → Utilisateurs → SyncPlay).') : error.message, { tone: 'error' });
      return;
    }
    const enter = async (run) => { try { await run(); } catch (error) { toast(error?.message || tr('Le groupe n’a pas pu être rejoint.'), { tone: 'error' }); } };
    sheet({
      title: tr('Regarder ensemble'),
      body: about(tr('Regardez le même titre au même moment, chacun chez soi. Crée un groupe et envoie le lien d’invitation, ou rejoins celui d’un ami. Pause, lecture et déplacements sont partagés.')),
      items: [
        ...(groups ?? []).map((g) => ({ symbol: 'users', label: tr('Rejoindre « {name} »', { name: groupLabel(g.GroupName) }), sub: tr('{people} : {names}', { people: people(g.Participants?.length ?? 1), names: (g.Participants ?? []).join(', ') }), run: () => enter(() => together.join(g.GroupId)) })),
        // A new group is empty: the invitation comes at once, the way to bring someone in.
        { symbol: 'play', label: tr('Créer un groupe'), sub: tr('Puis invite tes amis avec un lien : Lecture lance le titre pour tous.'), run: () => enter(async () => { await together.create(); inviteSheet(together.group); }) },
      ],
    });
  }

  /** « Audio : Japonais · Sous-titres : Français », a tap away from the other tracks. */
  function tracksLine(target) {
    const pick = tracksFor(target);
    if (!pick) return null;
    const all = streams.source.MediaStreams ?? [];
    const audios = all.filter((s) => s.Type === 'Audio'), subs = all.filter((s) => s.Type === 'Subtitle');
    if (audios.length < 2 && !subs.length && !finders.length) return null;
    const audio = audios.find((s) => s.Index === pick.audio), sub = subs.find((s) => s.Index === pick.subtitle);
    const name = (s, list) => (s.IsForced ? tr('{track} (forcés)', { track: trackText(s, list.indexOf(s) + 1).label }) : trackText(s, list.indexOf(s) + 1).label);
    // A single audio track of unknown language says nothing worth a line.
    const showAudio = audio && (audios.length > 1 || audio.Language);
    return h('button', { class: 'tracks-line', 'aria-haspopup': 'dialog', on: { click: () => tracksSheet(target, pick) } },
      icon('subtitles', { size: 20 }),
      h('span', { class: 'grow' },
        showAudio ? h('span', {}, tr('Audio : '), h('b', {}, name(audio, audios))) : null,
        h('span', {}, tr('Sous-titres : '), h('b', {}, sub ? name(sub, subs) : tr('aucun')))),
      icon('right', { size: 18 }));
  }

  function tracksSheet(target, pick) {
    const all = streams.source.MediaStreams ?? [];
    const audios = all.filter((s) => s.Type === 'Audio'), subs = all.filter((s) => s.Type === 'Subtitle');
    const choose = (change) => { chosen = { audio: pick.audio, subtitle: pick.subtitle, ...change, for: target.Id }; refreshActions(); };
    const details = (s, list) => [trackText(s, list.indexOf(s) + 1).details, s.Type === 'Subtitle' && !s.IsTextSubtitleStream ? tr('incrustés dans l’image') : '']
      .filter(Boolean).join(' · ');
    sheet({
      title: item.Type === 'Series' ? tr('Audio et sous-titres · {code}', { code: episodeCode(target) }) : tr('Audio et sous-titres'),
      items: [
        ...(audios.length > 1 ? [{ heading: tr('Audio') }, ...audios.map((s) => ({
          label: trackText(s, audios.indexOf(s) + 1).label, sub: details(s, audios), selected: s.Index === pick.audio, run: () => choose({ audio: s.Index }),
        }))] : []),
        { heading: tr('Sous-titres') },
        { label: tr('Aucun'), selected: pick.subtitle === -1 || !subs.some((s) => s.Index === pick.subtitle), run: () => choose({ subtitle: -1 }) },
        ...subs.map((s) => ({ label: trackText(s, subs.indexOf(s) + 1).label, sub: details(s, subs), selected: s.Index === pick.subtitle, run: () => choose({ subtitle: s.Index }) })),
        ...(finders.length ? [{ symbol: 'search', label: tr('Chercher des sous-titres'), sub: tr('Avec {names}, gardés sur Jellyfin', { names: finders.join(tr(' et ')) }), run: () => searchSheet(target) }] : []),
      ],
    });
  }

  // « Chercher des sous-titres », once Jellyfin says an extension of its own can (asked once per session).
  function findSubtitles() {
    if (finders.length) return;
    api.subtitleProviders().then((list) => { if (!list.length || finders.length) return; finders = list; refreshActions(); });
  }
  /**
   * « Chercher des sous-titres »: what Jellyfin's extension finds in a language. A tap keeps one on Jellyfin, for every
   * device, and Lecture starts with it once Jellyfin has read it.
   */
  function searchSheet(target) {
    const languages = searchLanguages(settings.get('subtitleLanguages'));
    let language = languages[0], version = 0, keeping = false;
    const chips = h('div', { class: 'report-periods' });
    const status = h('p', { class: 'group-note', role: 'status' });
    const results = h('div', { role: 'menu', 'aria-label': tr('Sous-titres trouvés') });
    const busy = (on) => results.querySelectorAll('button').forEach((button) => { button.disabled = on; });
    async function search() {
      const mine = ++version;
      chips.replaceChildren(...languages.map((code) => h('button', {
        class: 'chip', 'aria-pressed': String(code === language),
        on: { click: () => { if (!keeping && code !== language) { language = code; search(); } } },
      }, languageName(code) ?? code)));
      results.replaceChildren(); status.textContent = tr('Recherche…');
      try {
        const found = sortRemote(await api.searchSubtitles(target.Id, language)).slice(0, 30);
        if (mine !== version) return;
        const named = languageName(language) ?? language;
        status.textContent = found.length ? '' : tr('Aucun sous-titre trouvé en {language}.', { language: shownLanguage() === 'fr' ? named.toLowerCase() : named });
        results.replaceChildren(...found.map((x) => {
          const { label, details } = remoteText(x);
          return h('button', { class: 'sheet-item', role: 'menuitem', on: { click: () => keep(x) } },
            icon('subtitles'), h('span', { class: 'grow' }, label, h('span', { class: 'sub' }, details)));
        }));
      } catch (error) { if (mine === version) status.textContent = error.message; }
    }
    async function keep(found) {
      if (keeping) return;
      keeping = true; busy(true); status.textContent = tr('Ajout sur Jellyfin…');
      try {
        const before = (streams?.for === target.Id ? streams.source : (await api.item(target.Id)).MediaSources?.[0])?.MediaStreams ?? [];
        const kept = await api.keepRemoteSubtitle(target.Id, found);
        const said = keptText(kept, found.ThreeLetterISOLanguageName || language);
        if (kept === 'Refused' || kept === 'NotWritten') { status.textContent = said; return; }
        status.textContent = tr('Jellyfin lit le nouveau fichier…');
        const arrived = await waitForSubtitle(target, before);
        close();
        toast(arrived ? said : tr('{said} Jellyfin ne le montre pas encore : rouvre la fiche dans un moment.', { said }), { duration: 7000 });
      } catch (error) { status.textContent = error.message; } finally { keeping = false; busy(false); }
    }
    const close = sheet({
      title: tr('Chercher des sous-titres'),
      body: h('div', { class: 'subtitle-search' }, h('div', { class: 'sheet-form' }, h('p', { class: 'group-note' }, privacyText(finders)), chips, status), results),
    });
    search();
  }
  /** Jellyfin reads a kept subtitle after a moment: once there, Lecture starts with it. Null when it is still not, half a minute on. */
  async function waitForSubtitle(target, before) {
    for (let waited = 0; waited < 30_000; waited += 1500) {
      await new Promise((resolve) => { setTimeout(resolve, 1500); });
      const source = (await api.item(target.Id).catch(() => null))?.MediaSources?.[0];
      const arrived = arrivedSubtitle(before, source?.MediaStreams);
      if (!arrived) continue;
      const pick = tracksFor(target);
      streams = { for: target.Id, source };
      chosen = { audio: pick?.audio ?? source.DefaultAudioStreamIndex ?? null, subtitle: arrived.Index, for: target.Id };
      refreshMeta(); refreshActions();
      return arrived;
    }
    return null;
  }

  /** Lecture, its tracks and Du début again, without rebuilding the page. */
  /** The picture of the file Lecture plays (« 4K · HDR10 ») and when it ends: the film's, or the episode's it picks up at. */
  function playFacts() {
    const target = playTarget();
    if (!target) return [];
    return [qualityText(streams?.for === target.Id ? streams.source : null), endsAt(target)];
  }
  function refreshMeta() {
    if (!item || !metaBox || !el.contains(metaBox)) return;
    const facts = meta(item, episodes, playFacts());
    metaBox.replaceWith(facts); metaBox = facts;
  }

  function refreshActions() {
    if (!item || !actionsBox || !el.contains(actionsBox)) return;
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
    refreshMeta(); refreshActions();
  }

  function tools() {
    const target = playTarget();
    const played = !!item.UserData?.Played, favorite = !!item.UserData?.IsFavorite;
    const tool = (key, symbol, label, on, run) => h('button', { class: ['tool', on && 'on'], dataset: { tool: key }, 'aria-pressed': String(!!on), on: { click: run } }, icon(symbol), label);
    // The button changes at once, with a little bounce, as in iOS; Jellyfin is told behind it, and the button goes back
    // if it refuses.
    const flip = (key, set) => {
      const done = set();
      refreshActions();
      toolsBox.querySelector(`[data-tool="${key}"]`)?.classList.add('pop');
      done.then((ok) => { if (!ok) refreshActions(); });
    };
    return h('div', { class: 'detail-secondary' },
      tool('favorite', favorite ? 'heart-fill' : 'heart', favorite ? tr('Favori') : tr('Favoris'), favorite, () => flip('favorite', () => setFavorite(item, !favorite, { quiet: true }))),
      tool('played', 'check', played ? tr('Vu') : tr('Marquer vu'), played, () => flip('played', () => setPlayed(item, !played, { quiet: true }))),
      target && progress(target) > 0
        ? h('a', { class: 'tool', href: playHref(target, true, chosen?.for === target.Id ? chosen : null) }, icon('back10'), tr('Du début'))
        : null,
      admin && (item.Type === 'Movie' || item.Type === 'Series')
        ? h('button', { class: 'tool', dataset: { tool: 'more' }, 'aria-haspopup': 'dialog', on: { click: () => titleToolsSheet(item, (full) => load(!full)) } }, icon('more'), tr('Corriger'))
        : null);
  }

  function seasonsSection() {
    if (item.Type !== 'Series') return null;
    if (!episodes.length) return h('div', { class: 'state' }, icon('series'), h('h2', {}, tr('Aucun épisode')), h('p', {}, tr('Jellyfin n’a encore trouvé aucun épisode de cette série.')));
    pages = episodePages(episodes);
    const focus = episodes.find((x) => x.Id === wanted) ?? next;
    page ??= pageHolding(pages, episodes, focus) ?? pages[0];
    const list = h('div', { class: 'episodes' });
    listBox = list; shownIds = episodes.map((x) => x.Id).join();
    // Buttons that say which season is shown (pressed), as the library's filters: not tabs, which would need a panel
    // and arrow keys of their own.
    const chips = h('div', { class: 'chips', role: 'group', 'aria-label': tr('Saisons') });
    const show = (p) => {
      page = p;
      for (const c of chips.querySelectorAll('.chip')) {
        const on = c.dataset.key === `${p.season}:${p.skip}`;
        c.setAttribute('aria-pressed', String(on));
        c.classList.toggle('on', on);
      }
      clear(list).append(...pageEpisodes(episodes, p).map((e) => episodeRow(e, e.Id === (wanted ?? next?.Id))));
      drawSeasonBar();
    };
    showPage = show;
    for (const p of pages) {
      chips.append(h('button', { class: 'chip', dataset: { key: `${p.season}:${p.skip}` }, on: { click: () => show(p) } }, p.label));
    }
    show(pages.find((p) => p.season === page.season && p.skip === page.skip) ?? pages[0]);
    // The chosen season comes into view in its strip; the page itself does not move (scrollIntoView would scroll it).
    requestAnimationFrame(() => {
      const on = chips.querySelector('.chip.on');
      if (on) chips.scrollLeft += on.getBoundingClientRect().left - chips.getBoundingClientRect().left - (chips.clientWidth - on.offsetWidth) / 2;
    });
    return h('section', { class: 'seasons' },
      h('div', { class: 'section-head' }, h('h2', { class: 'h2' }, tr('Épisodes')), h('span', { class: 'meta' }, trn(episodes.length, '{n} épisode', '{n} épisodes'))),
      calendarBar,
      pages.length > 1 ? h('div', { style: { padding: '0 var(--gutter) 8px' } }, chips) : null,
      seasonBar, list);
  }

  /**
   * « 2 épisodes manquants : S1 · É3, S2 · É2 » and « Prochain épisode : S2 · É3, samedi », from the episodes Jellyfin's
   * TheTVDB extension adds without their file: a tap lists them, with what to do (« À venir », the PC server).
   */
  function drawCalendar() {
    const { missing = [], coming = [] } = schedule ?? {};
    const day = today();
    const first = missing.length ? missingText(missing) : nextText(coming, day);
    const line = !first ? null : h('button', { class: 'next-line', 'aria-haspopup': 'dialog', on: { click: calendarSheet } },
      icon(missing.length ? 'warning' : 'calendar', { size: 20 }),
      h('span', { class: 'grow' }, h('span', {}, first), missing.length && coming.length ? h('span', { class: 'sub' }, nextText(coming, day)) : ''),
      icon('right', { size: 18 }));
    calendarBar.replaceChildren(...(line ? [line] : []));
  }
  function calendarSheet() {
    const { missing = [], coming = [] } = schedule ?? {};
    const day = today();
    const named = (x) => [episodeCode(x), x.Name].filter(Boolean).join(' · ');
    // An administrator: TorLink on the PC server looks for the first one missing (or the series, for several).
    const search = missing.length === 1 ? `${item.Name} S${String(missing[0].ParentIndexNumber).padStart(2, '0')}E${String(missing[0].IndexNumber).padStart(2, '0')}` : item.Name;
    sheet({
      title: tr('Épisodes de « {name} »', { name: item.Name }),
      body: h('p', { class: 'sheet-note' }, tr('D’après TheTVDB, que ton serveur Jellyfin consulte avec son extension : ces épisodes ne sont pas dans ta bibliothèque.')),
      items: [
        ...(missing.length ? [{ heading: trn(missing.length, 'Manquant', 'Manquants') }, ...missing.slice(0, 30).map((x) => ({ symbol: 'warning', label: named(x), sub: tr('Sorti {day}', { day: dayText(x.PremiereDate.slice(0, 10), day) }) }))] : []),
        ...(missing.length > 30 ? [{ label: trn(missing.length - 30, 'Et {n} autre', 'Et {n} autres') }] : []),
        ...(missing.length && admin ? [{ symbol: 'download', label: tr('Le télécharger sur le PC serveur'), sub: tr('Cherche « {query} » avec TorLink.', { query: search }), run: () => { location.hash = `#/telecharger?q=${encodeURIComponent(search)}`; } }] : []),
        ...(coming.length ? [{ heading: tr('À venir') }, ...coming.slice(0, 12).map((x) => ({ symbol: 'calendar', label: named(x), sub: dayTitle(x.PremiereDate.slice(0, 10), day) }))] : []),
        { symbol: 'right', label: tr('Toutes les sorties de tes séries'), run: () => { location.hash = '#/a-venir'; } },
      ],
    });
  }
  /** The episodes missing and coming of this series, read again with the page (kept a minute by api.js). */
  function loadCalendar() {
    if (item?.Type !== 'Series') return;
    const id = item.Id;
    api.virtualEpisodes().then((list) => {
      if (item?.Id !== id) return;
      const next = calendar(list.filter((x) => x.SeriesId === id), today());
      const sig = (x) => [...x.missing, ...x.coming].map((e) => `${e.Id}:${e.PremiereDate}:${e.Name}`).join();
      if (schedule && sig(schedule) === sig(next)) return;
      schedule = next; drawCalendar();
    }, () => {});
  }

  /**
   * Above the episodes of the season shown: « À suivre » when Lecture plays one of another season (a long anime opened
   * on its first), which brings that season; « Marquer la saison comme vue », or « non vue » once all are, when the
   * series has another season (with one, the series' own button does it).
   */
  function drawSeasonBar() {
    const bar = h('div', { class: 'season-bar' });
    const elsewhere = wanted ? null : nextElsewhere(pages, episodes, page, next);
    if (elsewhere) {
      bar.append(h('button', { class: 'next-line', on: { click: () => showPage(elsewhere) } },
        icon('play', { size: 20 }),
        h('span', { class: 'grow' }, h('span', {}, tr('À suivre : '), h('b', {}, episodeCode(next))), next.Name ? h('span', { class: 'sub' }, next.Name) : ''),
        h('span', { class: 'go' }, elsewhere.label, icon('right', { size: 18 }))));
    }
    if (marksSeasons(episodes)) {
      const { played } = seasonMark(episodes, page.season);
      bar.append(h('button', { class: 'btn quiet small season-mark', disabled: marking, on: { click: () => markSeason(page.season, !played) } },
        icon(played ? 'refresh' : 'check', { size: 18 }), played ? tr('Marquer la saison comme non vue') : tr('Marquer la saison comme vue')));
    }
    if (seasonBar?.isConnected) seasonBar.replaceWith(bar);
    seasonBar = bar;
    return bar;
  }

  /**
   * A whole season watched or not: its episodes say so at once, Jellyfin is told (the season in one call, else each
   * episode), and the page follows what Jellyfin then says (where the series picks up).
   */
  async function markSeason(season, played) {
    if (marking) return;
    const { ids } = seasonMark(episodes, season);
    const before = new Map(episodes.map((e) => [e.Id, e.UserData]));
    for (const e of episodes) if (seasonOf(e) === season) e.UserData = { ...e.UserData, Played: played, PlaybackPositionTicks: 0 };
    marking = true; update();
    try {
      for (const id of ids) await api.setPlayed(id, played);
      toast(played ? tr('Saison marquée comme vue.') : tr('Saison marquée comme non vue.'), { tone: 'done' });
    } catch (error) {
      for (const e of episodes) if (before.has(e.Id)) e.UserData = before.get(e.Id);
      toast(error.message, { tone: 'error' });
    } finally { marking = false; }
    // This page follows what Jellyfin says now (where the series picks up), and so do the series' poster and
    // Continuer à regarder on the other screens.
    changed(item);
  }

  function render() {
    // Read before the page is emptied: reading it after would lay out an empty page, and send the scroll to the top.
    const wide = wideScreen();
    const headTitle = logo(item, 420, 'detail-logo') ?? h('h1', { class: 'detail-title' }, item.Name);
    if (headTitle.tagName === 'IMG') {
      headTitle.addEventListener('error', () => headTitle.replaceWith(h('h1', { class: 'detail-title' }, item.Name)), { once: true });
      headTitle.setAttribute('role', 'heading'); headTitle.setAttribute('aria-level', '1');
    }
    const cast = (item.People ?? []).filter((p) => p.Type === 'Actor' && p.Id).slice(0, 20);
    const backdrop = artFor(item, 'backdrop'), poster = posterFor(item, backdrop, wide);
    // The title given the focus as the screen opened (app.js) keeps it in the page drawn whole.
    const focused = el.contains(document.activeElement) && document.activeElement.matches('.detail-title, .detail-logo');
    clear(el).append(back,
      h('div', { class: 'detail-top' },
        h('div', { class: ['detail-backdrop', !backdrop && 'none'] }, backdrop ? picture(backdrop, { kind: 'backdrop', width: wide ? 1600 : 900, eager: true }) : null),
        h('div', { class: ['detail-head', poster && 'with-poster'] },
          poster ? picture(poster, { kind: 'poster', width: wide ? 240 : 140, eager: true, className: 'detail-poster' }) : null,
          h('div', { class: 'detail-info' },
            headTitle, (metaBox = meta(item, episodes, playFacts())),
            h('div', { class: 'detail-play' }, (actionsBox = primaryAction()), (toolsBox = tools())),
            overview(item.Overview), genres(item), credits(item)))),
      (seasonsBox = h('div', {}, seasonsSection())),
      row(tr('Distribution'), cast, (p) => personCard(p), { people: true }) ?? '',
      (sagaBox = h('div', {}, sagaRows())),
      (studioBox = h('div', {}, studioRow() ?? '')),
      (extrasBox = h('div', {}, row(tr('Bonus'), extras, (x) => extraCard(x), { wide: true }) ?? '')),
      (similarBox = h('div', {}, row(tr('Titres similaires'), similar, (x) => posterCard(x)) ?? '')),
      h('div', { style: { height: '32px' } }));
    if (focused) { headTitle.tabIndex = -1; headTitle.focus({ preventScroll: true }); }
  }

  /**
   * What the card that was touched already knows (name, pictures, year), drawn at once: the screen slides in with its
   * picture and title rather than a spinner, and the rest arrives in place.
   */
  function seed(known) {
    const wide = wideScreen();
    const backdrop = artFor(known, 'backdrop'), poster = posterFor(known, backdrop, wide);
    const headTitle = logo(known, 420, 'detail-logo') ?? h('h1', { class: 'detail-title' }, known.Name);
    if (headTitle.tagName === 'IMG') {
      headTitle.addEventListener('error', () => headTitle.replaceWith(h('h1', { class: 'detail-title' }, known.Name)), { once: true });
      headTitle.setAttribute('role', 'heading'); headTitle.setAttribute('aria-level', '1');
    }
    clear(el).append(back,
      h('div', { class: 'detail-top' },
        h('div', { class: ['detail-backdrop', !backdrop && 'none'] }, backdrop ? picture(backdrop, { kind: 'backdrop', width: wide ? 1600 : 900, eager: true }) : null),
        h('div', { class: ['detail-head', poster && 'with-poster'] },
          poster ? picture(poster, { kind: 'poster', width: wide ? 240 : 140, eager: true, className: 'detail-poster' }) : null,
          h('div', { class: 'detail-info' },
            headTitle, meta(known, []),
            h('div', { class: 'detail-play' }, h('div', { class: 'detail-actions' }, h('div', { class: 'skeleton detail-waiting' }))),
            h('div', { class: 'skeleton line', style: { width: '90%' } }), h('div', { class: 'skeleton line', style: { width: '75%' } })))));
  }

  /**
   * A quiet reload (back from the player, a title marked watched, the server back): what changed is updated where it
   * is. The page does not move, an opened summary stays open, the rows stay where they were scrolled.
   */
  function update() {
    refreshMeta();
    refreshActions();
    if (item.Type !== 'Series') return;
    if (!page || episodes.map((x) => x.Id).join() !== shownIds || !listBox || !el.contains(listBox)) {
      clear(seasonsBox).append(seasonsSection() ?? '');
      return;
    }
    const byId = new Map(episodes.map((x) => [x.Id, x]));
    const currentId = wanted ?? next?.Id;
    for (const old of [...listBox.children]) {
      const e = byId.get(old.dataset.id);
      if (e && old.dataset.state !== rowState(e, e.Id === currentId)) old.replaceWith(episodeRow(e, e.Id === currentId));
    }
    drawSeasonBar();
  }

  /** The row of the film's saga, named as Jellyfin names it (« Harry Potter - Saga »); « Tout voir » opens its page. */
  const sagaRows = () => (sagas ?? []).map((saga) => row(saga.name, saga.items, (x) => posterCard(x), { more: `#/saga/${saga.id}` }) ?? '');
  /** « Du même studio · Studio Ghibli »: its other titles, but those already in the saga's row; « Tout voir » opens the studio's page. */
  const studioRow = () => (studio ? row(tr('Du même studio · {name}', { name: studio.name }), studio.items, (x) => posterCard(x), { more: `#/studio/${studio.id}` }) : null);
  /** The other titles of its studios (three at most, newest first): the first studio that has some. */
  const studioTitles = (of) => Promise.all((of.Studios ?? []).filter((x) => x.Id).slice(0, 3)
    .map((x) => api.browse({ studio: x.Id, sort: 'year', limit: 21 }).then((r) => ({ id: x.Id, name: x.Name, items: r?.Items ?? [] }), () => null)));

  async function load(quiet = false) {
    // Only the latest load draws: an older answer, read before a mark or the end of the player, never wins.
    const seq = ++loadSeq;
    const known = !item && !quiet ? knownItem(id) : null;
    if (known && known.Type !== 'Episode') seed(known);
    else if (!quiet && !item) clear(el).append(back, h('div', { style: { paddingTop: '40vh' } }, spinner()));
    // A series' episodes and where it picks up are asked for with it when the card already said it is a series.
    const seriesParts = () => Promise.all([api.episodes(id).then((r) => r?.Items ?? []), api.seriesNext(id).catch(() => null)]);
    const early = (item ?? known)?.Type === 'Series' ? seriesParts() : null;
    early?.catch(() => {});
    try {
      const fresh = await api.item(id);
      if (seq !== loadSeq) return;
      let eps = [], nextUp = null;
      if (fresh.Type === 'Series') {
        [eps, nextUp] = await (early ?? seriesParts());
        if (seq !== loadSeq) return;
      } else if ((fresh.Type === 'Episode' && fresh.SeriesId) || fresh.Type === 'BoxSet') {
        replaceRoute(titleHref(fresh));
        return;
      }
      const inPlace = quiet && item?.Type === fresh.Type && !!actionsBox && el.contains(actionsBox);
      // Episodes played since (one after the other in the player): the page follows the one Lecture now plays.
      if (quiet && next && nextUp && nextUp.Id !== next.Id) { wanted = null; page = null; }
      item = fresh; episodes = eps; next = nextUp; stale = false; loadedAt = Date.now();
      document.title = `${item.Name} · Mira`;
      // A film's streams come with it, fresh; a series' come with the episode it picks up at (else they are asked for).
      if (item.Type !== 'Series') streams = null;
      else if (next?.MediaSources?.[0]) streams = { for: next.Id, source: next.MediaSources[0] };
      loadStreams(); findSubtitles();
      if (!admin) api.isAdministrator().then((yes) => { if (yes && !admin) { admin = true; refreshActions(); } });
      if (inPlace) update(); else render();
      if (item.Type === 'Series') { loadFollows(); loadCalendar(); }
      if (!similar.length && !sagas) {
        // Only their rows are drawn when they arrive: the page does not jump or flash under the finger. A film of a
        // saga has its other films in a row of their own, not again among the similar titles.
        const ofSaga = item.Type === 'Movie' ? api.sagas(id).catch(() => []) : Promise.resolve([]);
        Promise.all([api.similar(id).then((r) => r?.Items ?? [], () => []), ofSaga, studioTitles(item)]).then(([alike, found, studios]) => {
          sagas = found.filter((saga) => saga.items.length);
          const inSaga = new Set(sagas.flatMap((saga) => saga.items.map((x) => x.Id)));
          studio = studios.filter(Boolean).map((x) => ({ ...x, items: x.items.filter((t) => t.Id !== id && !inSaga.has(t.Id)).slice(0, 20) })).find((x) => x.items.length) ?? null;
          // Nor again what « Du même studio » shows just above: in a small library, both rows would be the same titles.
          const inStudio = new Set(studio?.items.map((x) => x.Id));
          similar = alike.filter((x) => !inSaga.has(x.Id) && !inStudio.has(x.Id));
          if (sagas.length && sagaBox) clear(sagaBox).append(...sagaRows());
          if (studio && studioBox) clear(studioBox).append(studioRow());
          if (similar.length && similarBox) clear(similarBox).append(row(tr('Titres similaires'), similar, (x) => posterCard(x)) ?? '');
        });
      }
      // Bonus videos come again with each load: one just watched shows it.
      if (item.SpecialFeatureCount > 0) {
        api.extras(id).then((found) => {
          if (seq !== loadSeq || !extrasBox) return;
          const before = extras.map(cardSig).join(), now = found.map(cardSig).join();
          extras = found;
          if (now !== before) clear(extrasBox).append(row(tr('Bonus'), extras, (x) => extraCard(x), { wide: true }) ?? '');
        }, () => {});
      }
    } catch (error) {
      if (seq !== loadSeq) return;
      if (!item) clear(el).append(back, h('div', { class: 'page', style: { paddingTop: '30vh' } }, errorState(error, () => load())));
      else toast(error.message, { tone: 'error' });
    }
  }

  const onChange = (e) => {
    const changed = e.detail.item;
    if (!item) return;
    // This page's own buttons already show a film's watched state and the favourite (and are bouncing): nothing to do.
    if (changed === item && ('favorite' in e.detail || ('played' in e.detail && item.Type !== 'Series'))) return;
    const own = episodes.find((x) => x.Id === changed.Id);
    if (changed.Id === item.Id || changed.SeriesId === item.Id || own) {
      stale = true;
      if (!el.isConnected) return;
      // An episode marked from its long-press menu shows it at once (its row may hold an older copy of it); the rest,
      // where the series picks up, follows.
      if (own) {
        if (own !== changed) own.UserData = changed.UserData;
        if (metaBox && el.contains(metaBox)) update();
      }
      load(true);
    }
  };
  changes.addEventListener('item', onChange);
  // Joined, left, someone came or went: the line under Lecture says so.
  const unwatchGroup = together.watch(() => refreshActions());
  together.restore();
  // A window made wider or narrower than a tablet: the page drawn again in the other layout (with or without its poster).
  const wideQuery = matchMedia(WIDE);
  const onWide = () => { if (item) render(); };
  wideQuery.addEventListener('change', onWide);

  load();
  return {
    el, title: tr('Titre'), keep: true,
    // Not at the first showing: the page is loading already.
    enter() { if (stale || (loadedAt && Date.now() - loadedAt > 5 * 60_000)) load(true); },
    refresh: () => load(true),
    dispose() { changes.removeEventListener('item', onChange); unwatchGroup(); wideQuery.removeEventListener('change', onWide); },
  };
}
