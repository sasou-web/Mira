// « Tes choix gardés sur ton Jellyfin » in Mira web: the tracks of each series, the titles removed from Continuer à
// regarder, the languages, the quality and the intros go to Jellyfin and come back from it (the rules: choices.js).
// The text size, the home rows and the recent searches stay this phone's own.
import { api } from './api.js';
import { session, settings, settingsChanged, signedOut } from './session.js';
import { PREFERENCE, SETTINGS, read, capture, merge, differs, apply, same, fromWeb, toWeb } from './choices.js';

const KEPT = ['seriesTracks', 'hiddenResume', ...Object.keys(SETTINGS)];
// This phone's document, with the time of each choice: per account, gone at sign-out (session.js).
const key = () => `mira.choices.${session.current?.userId}`;
function load() { try { return read(localStorage.getItem(key())); } catch { return read(null); } }
function keep(doc) { try { localStorage.setItem(key(), JSON.stringify(doc)); } catch { /* blocked: each look starts again from Jellyfin */ } }

/** What this phone uses now, in the shape of capture(). */
function mine() {
  const tracks = {}, hidden = {};
  for (const [id, memory] of Object.entries(settings.get('seriesTracks') ?? {})) tracks[id] = fromWeb(memory);
  for (const [id, at] of Object.entries(settings.get('hiddenResume') ?? {})) {
    const time = typeof at === 'number' ? at : Date.parse(at);
    if (Number.isFinite(time)) hidden[id] = time;
  }
  return { settings: Object.fromEntries(Object.keys(SETTINGS).map((k) => [k, settings.get(k)])), tracks, hidden };
}

let applying = false;
/** What Jellyfin brought, in this phone's settings: each one changed tells the screens (settingsChanged). */
function use(doc) {
  const used = apply(doc);
  const tracks = Object.fromEntries(Object.entries(used.tracks).map(([id, x]) => [id, toWeb(x)]));
  const hidden = Object.fromEntries(Object.entries(used.hidden).map(([id, at]) => [id, new Date(at).toISOString()]));
  const next = { ...used.settings, seriesTracks: tracks, hiddenResume: hidden };
  applying = true;
  try {
    for (const [name, value] of Object.entries(next)) {
      const now = settings.get(name);
      // The order counts too: the oldest go first when there are too many.
      const unchanged = same(now, value) && (typeof value !== 'object' || JSON.stringify(Object.keys(now ?? {})) === JSON.stringify(Object.keys(value)));
      if (!unchanged) settings.set(name, value);
    }
  } finally { applying = false; }
}

let running = null, again = false, timer = 0, lastAt = 0;
/** This phone and Jellyfin compared: what changed here goes, what changed elsewhere comes. Never throws. */
export function syncChoices() {
  if (!session.current) return Promise.resolve();
  if (running) { again = true; return running; }
  const account = session.current.userId;
  lastAt = Date.now();
  running = (async () => {
    const doc = capture(load(), mine(), Date.now());
    keep(doc);
    const preferences = await api.choices();
    if (session.current?.userId !== account) return;
    const remote = read(preferences?.CustomPrefs?.[PREFERENCE] ?? null);
    // A choice made here while Jellyfin answered counts too.
    const merged = merge(capture(doc, mine(), Date.now()), remote);
    use(merged);
    keep(merged);
    if (differs(merged, remote)) {
      const shared = { ...merged }; delete shared.seen;
      await api.saveChoices({ ...preferences, CustomPrefs: { ...(preferences?.CustomPrefs ?? {}), [PREFERENCE]: JSON.stringify(shared) } });
    }
  })().catch(() => { /* Jellyfin not answering now: what changed here keeps its time for the next look. */ }).finally(() => {
    running = null;
    if (again) { again = false; soon(); }
  });
  return running;
}

/** A change here goes a moment later: several together go at once. */
function soon(delay = 2000) { clearTimeout(timer); timer = setTimeout(syncChoices, delay); }
/** Back on the screen after a while: what changed on the other devices meanwhile. */
export function lookAgain() { if (Date.now() - lastAt > 60_000) soon(0); }

settingsChanged.addEventListener('change', (event) => { if (!applying && KEPT.includes(event.detail)) soon(); });
signedOut.addEventListener('out', () => { clearTimeout(timer); again = false; lastAt = 0; });
