// « À venir », from the Séries tab and a series' page: the next episodes of the series of the library, by day, then
// those missing, by series, as Jellyfin's TheTVDB extension tells them (calendar.js). Each row opens its series.
import { api } from '../api.js';
import { h, icon, clear, episodeCode } from '../dom.js';
import { tr } from '../i18n.js';
import { artFor, picture } from '../images.js';
import { emptyState, errorState, skeletonGrid, toast } from '../components.js';
import { goBack } from '../app.js';
import { calendar, byDay, dayTitle, missingText, today } from '../calendar.js';

/** A row: the series' poster, its name, then what the line says of it. */
function row(episode, text) {
  return h('a', { class: 'item upcoming-item', href: `#/titre/${episode.SeriesId}` },
    // The name is beside it: no initials read twice.
    picture(artFor(episode, 'poster'), { kind: 'poster', width: 44 }),
    h('span', { class: 'grow' }, h('span', { class: 'upcoming-name' }, episode.SeriesName ?? ''), h('span', { class: 'sub' }, text)),
    icon('right', { size: 18 }));
}

export function create() {
  const el = h('div', { class: 'view upcoming-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/series') } }, icon('back'));
  const head = h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } },
    h('h1', { class: 'page-title' }, tr('À venir')),
    h('p', { class: 'page-sub' }, tr('Les prochains épisodes de tes séries et ceux qui manquent, d’après TheTVDB.')));
  let shown = '', loadedAt = 0;

  async function load(quiet = false) {
    if (!quiet) clear(el).append(back, head, h('div', { class: 'page' }, skeletonGrid(3)));
    try {
      const list = await api.virtualEpisodes();
      const day = today();
      const sig = list.map((x) => `${x.Id}:${x.PremiereDate}`).join() + day;
      loadedAt = Date.now();
      if (quiet && sig === shown) return;
      shown = sig;
      const { missing, coming } = calendar(list, day);
      const parts = byDay(coming).map(({ day: on, episodes }) => [
        h('h2', { class: 'h2 group-title' }, dayTitle(on, day)),
        h('div', { class: 'group' }, episodes.map((x) => row(x, [episodeCode(x), x.Name].filter(Boolean).join(' · ')))),
      ]);
      // Missing: a row for each series, its episodes gathered (« S1 · É3–5 »).
      const bySeries = new Map();
      for (const x of missing) bySeries.set(x.SeriesId, [...(bySeries.get(x.SeriesId) ?? []), x]);
      const gaps = [...bySeries.values()].sort((a, b) => (a[0].SeriesName ?? '').localeCompare(b[0].SeriesName ?? ''));
      if (gaps.length) {
        parts.push(h('h2', { class: 'h2 group-title' }, tr('Épisodes manquants')),
          h('div', { class: 'group' }, gaps.map((episodes) => row(episodes[0], missingText(episodes)))));
      }
      clear(el).append(back, head, ...(parts.length ? parts.flat() : [list.length ? emptyState({ symbol: 'calendar', title: tr('Rien d’annoncé'), text: tr('Aucun nouvel épisode annoncé pour tes séries, et aucun ne manque.') })
        : emptyState({ symbol: 'calendar', title: tr('Rien d’annoncé'), text: tr('Jellyfin ne connaît que les épisodes de tes fichiers. Son extension TheTVDB lui apprend ceux qui manquent et les prochains, avec leur date : un administrateur l’active dans Mira pour Windows, Réglages → Jellyfin.') })]));
    } catch (error) {
      if (quiet && shown) toast(error.message);
      else clear(el).append(back, head, h('div', { class: 'page' }, errorState(error, () => load())));
    }
  }

  load();
  return {
    el, title: tr('À venir'), keep: true,
    // Days move on: back after a while, read again.
    enter() { if (loadedAt && Date.now() - loadedAt > 60_000) load(true); },
    refresh: () => load(true),
  };
}
