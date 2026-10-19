// « Nouveau dans Mira »: once Jellyfin serves a new Mira web, the phone shows that version's main points once, from
// WhatsNew.json (src/Mira.Core), the text of Mira for Windows' « Nouveautés ». Only the points marked "web": the others
// are Windows' or the Mac's. Which versions: as WhatsNew.Since (cases in tests/fixtures/whats-new.json).
// In English, each point's "en" (title, text) when WhatsNew.json has it, else its French.
// Imported by Node too (tools/web/unit): nothing of the browser here.
import { language } from './i18n.js';

/** [major, minor, patch] of « 0.7.9 », null for anything else (the « 0.0.0 » of a copy that Jellyfin did not serve). */
export function parseVersion(text) {
  const m = /^(\d+)\.(\d+)\.(\d+)$/.exec(String(text ?? '').trim());
  return m ? m.slice(1).map(Number) : null;
}
/** Below zero when a comes before b, zero for the same version, above zero after. */
export function compareVersions(a, b) {
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

/**
 * The versions after seen up to current, newest first. Without a known seen version (a phone signed in before Mira web
 * kept one), only current's.
 */
export function since(entries, seen, current) {
  const now = parseVersion(current), before = parseVersion(seen);
  if (!now) return [];
  return entries
    .map((entry) => ({ entry, number: parseVersion(entry.version) }))
    .filter(({ number }) => number && compareVersions(number, now) <= 0 && (before ? compareVersions(number, before) > 0 : compareVersions(number, now) === 0))
    .sort((a, b) => compareVersions(b.number, a.number))
    .map(({ entry }) => entry);
}

/** A point in the language shown: English from its "en" where WhatsNew.json gives one. */
const inLanguage = (item) => (language() === 'en' && item.en ? { ...item, title: item.en.title ?? item.title, text: item.en.text ?? item.text } : item);

/** What the phone shows: each of those versions with its points for Mira web, none without any. */
export function phoneNotes(entries, seen, current) {
  return since(entries, seen, current)
    .map((entry) => ({ version: entry.version, items: (entry.items ?? []).filter((item) => item.web === true).map(inLanguage) }))
    .filter((entry) => entry.items.length > 0);
}

/**
 * Whether to look at all: signed in, a version Jellyfin served, not seen yet. A phone signed in on this version already
 * saw it (the first sign-in shows nothing: everything is new then).
 */
export function worthAsking(seen, current) {
  const now = parseVersion(current), before = parseVersion(seen);
  return !!now && compareVersions(now, [0, 0, 0]) > 0 && (!before || compareVersions(before, now) < 0);
}

// Mira for Windows' icons (Views/Icon.cs) that Mira web draws under another name (js/icons.js); any other: a star.
const ICONS = { film: 'films', skip: 'next', sparkle: 'star', help: 'info' };
export function webIcon(name, known) {
  const mapped = ICONS[name] ?? name;
  return known.has(mapped) ? mapped : 'star';
}
