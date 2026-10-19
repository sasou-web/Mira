// « Nouvel épisode » when Mira web opens: the episodes Jellyfin added since the last visit, of the series this account
// follows (new-episodes.js, as Mira for Windows notifies them). Safari sends no notification to a site in plain HTTP:
// a notice in Mira itself, which needs none. The mark is kept per account and forgotten at sign-out (session.js); the
// first visit only sets it, and a phone that keeps nothing never announces anything (each visit would be the first).
import { api } from './api.js';
import { session, storageBlocked } from './session.js';
import { toast, sheet, titleHref } from './components.js';
import { pickNewEpisodes, newestEpisode } from './new-episodes.js';
import { tr } from './i18n.js';

const markKey = (userId) => `mira.home.${userId}.episodes`;
// Not over the player, an invitation, the sign-in or the QR code: the news waits for the next opening there.
const elsewhere = () => /^#\/(lecture|ensemble|connexion|partager)\b/.test(location.hash);
// Nor over a sheet (« Nouveau dans Mira » after an update, a menu): a moment after it closes.
const sheetOpen = () => !!document.querySelector('.sheet-layer:not(.leaving)');
const later = () => setTimeout(announceNewEpisodes, 3000);
let looking = false;

const open = (notice) => { location.hash = titleHref({ Type: 'Episode', Id: notice.episode.Id, SeriesId: notice.seriesId }); };

function show({ notices, more, series }) {
  if (!notices.length) return;
  if (notices.length === 1 && !more) {
    const [notice] = notices;
    toast(`${notice.title} · ${notice.text}`, { action: { label: tr('Voir'), run: () => open(notice) }, duration: 8000 });
    return;
  }
  // Several series: the three latest in a sheet (the others show their count of episodes to watch on their card).
  const items = notices.map((notice) => ({ label: notice.title, sub: notice.text, symbol: 'series', run: () => open(notice) }));
  toast(tr('{n} séries que tu suis ont de nouveaux épisodes.', { n: series }), {
    duration: 8000, action: { label: tr('Voir'), run: () => sheet({ title: tr('Nouveaux épisodes'), items }) },
  });
}

/** Looks once, when Mira opens or comes back after a long while; nothing when Jellyfin does not answer. */
export async function announceNewEpisodes() {
  const userId = session.current?.userId;
  if (looking || !userId || elsewhere() || storageBlocked()) return;
  if (sheetOpen()) { later(); return; }
  looking = true;
  try {
    const { episodes, series } = await api.recentEpisodes();
    // Signed out, or gone to the player meanwhile: the mark stays where it was, for the next time.
    if (session.current?.userId !== userId || elsewhere() || document.hidden) return;
    if (sheetOpen()) { later(); return; }
    const raw = localStorage.getItem(markKey(userId));
    const seen = raw == null ? null : Number(raw);
    if (seen != null && Number.isFinite(seen)) show(pickNewEpisodes(episodes, series, seen));
    // A library without an episode to watch yet: the next one is news.
    const mark = newestEpisode(episodes, Number.isFinite(seen) ? seen : null) ?? 0;
    if (mark !== seen) localStorage.setItem(markKey(userId), String(mark));
  } catch { /* Jellyfin away, or the storage refused: asked again at the next opening. */ }
  finally { looking = false; }
}
