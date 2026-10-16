// « Tes choix gardés sur ton Jellyfin »: the choices of the account that follow it from one device to another (the
// tracks of each series, the titles removed from Continuer à regarder, the languages, the quality, the intros), kept
// in Jellyfin's display preferences. Each entry has its time; the newest wins. The same rules as Mira for Windows and
// Mac (AccountChoices in Core), with their cases shared in tests/fixtures/choices.json. Nothing of the browser here:
// Node imports this module.
import { language } from './i18n.js';

/** The custom preference that holds them, in Jellyfin's display preferences « mira » of the client « mira ». */
export const PREFERENCE = 'mira.choices';
/** The orders of the tracks before any choice, in Mira's language (PlayerSettings.DefaultAudioLanguage in Core). */
export const defaultAudioLanguages = () => (language() === 'fr' ? 'jpn,ja,fre,fra,fr,eng,en' : 'jpn,ja,eng,en,fre,fra,fr');
export const defaultSubtitleLanguages = () => (language() === 'fr' ? 'fre,fra,fr,eng,en' : 'eng,en,fre,fra,fr');
/**
 * The settings kept on Jellyfin, with Mira's defaults: a setting never changed anywhere is not written. Those of the
 * tracks follow Mira's language: a device that never chose does not impose its language's order on the others.
 */
export const SETTINGS = Object.freeze({
  get audioLanguages() { return defaultAudioLanguages(); },
  get subtitleLanguages() { return defaultSubtitleLanguages(); },
  autoSkip: false,
  quality: 'auto',
});
/** A value this version of Mira can use for a setting it keeps on Jellyfin. */
export function fits(key, value) {
  if (!Object.hasOwn(SETTINGS, key)) return false;
  if (key === 'quality') return value === 'auto' || value === 'max' || (Number.isFinite(value) && value > 0);
  return typeof value === typeof SETTINGS[key];
}
/** The series whose tracks are kept, and the titles removed or put back, newest first. */
export const LIMITS = Object.freeze({ tracks: 100, hidden: 200 });

const SECTIONS = ['settings', 'tracks', 'hidden'];
const object = (x) => (x && typeof x === 'object' && !Array.isArray(x) ? x : {});
const time = (x) => (Number.isFinite(x) ? x : 0);

/** Equal as JSON, whatever the order of the keys. */
export function same(a, b) {
  if (a === b) return true;
  if (typeof a !== 'object' || typeof b !== 'object' || a === null || b === null || Array.isArray(a) !== Array.isArray(b)) return false;
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every((key) => Object.hasOwn(b, key) && same(a[key], b[key]));
}

/** A series' tracks as both sides keep them; `subtitles` false: subtitles off. */
export function track(x) {
  const text = (v) => (typeof v === 'string' && v ? v : null);
  return {
    audio: text(x?.audio), audioTitle: text(x?.audioTitle), subtitles: !!x?.subtitles,
    subtitle: x?.subtitles ? text(x?.subtitle) : null, subtitleTitle: x?.subtitles ? text(x?.subtitleTitle) : null, forced: !!(x?.subtitles && x?.forced),
  };
}

/** What a document read from Jellyfin (or kept on this device) holds; anything unreadable counts as nothing. */
export function read(text) {
  let doc;
  try { doc = typeof text === 'string' ? JSON.parse(text) : text; } catch { doc = null; }
  doc = object(doc);
  return { ...doc, settings: object(doc.settings), tracks: object(doc.tracks), hidden: object(doc.hidden) };
}

/**
 * The document of this device with what changed here since it was last kept: `local` is what the device uses now
 * ({ settings: { key: value }, tracks: { series: tracks }, hidden: { id: time in ms } }). A change made here takes
 * `now`; the first time (a document never kept), what this device had before is the oldest choice (0), so what
 * Jellyfin already keeps wins over it, and a setting left as Mira's default is not written at all.
 */
export function capture(doc, local, now) {
  doc = read(doc);
  const first = !doc.seen;
  const settings = { ...doc.settings }, tracks = { ...doc.tracks }, hidden = { ...doc.hidden };
  for (const [key, value] of Object.entries(object(local?.settings))) {
    if (value === undefined) continue;
    const base = settings[key] ? settings[key].value : SETTINGS[key];
    if (!same(value, base)) settings[key] = { value, at: first ? 0 : now };
  }
  for (const [id, value] of Object.entries(object(local?.tracks))) {
    const known = tracks[id], mine = track(value);
    if (known && same(track(known), mine)) continue;
    tracks[id] = { ...mine, at: first ? 0 : now };
  }
  const removed = object(local?.hidden);
  for (const [id, at] of Object.entries(removed)) {
    const known = hidden[id];
    if (!known || time(at) > time(known.at)) hidden[id] = { hidden: true, at: time(at) };
  }
  // Put back here: Continuer à regarder shows it again on the other devices too.
  for (const [id, known] of Object.entries(hidden)) if (known?.hidden && !Object.hasOwn(removed, id)) hidden[id] = { hidden: false, at: now };
  return { ...doc, settings, tracks, hidden, seen: true };
}

/** The newest `limit` entries, newest first (then by key, the same on every device). */
function newest(entries, limit) {
  return Object.fromEntries(Object.entries(entries).sort(([a, x], [b, y]) => time(y.at) - time(x.at) || (a < b ? -1 : a > b ? 1 : 0)).slice(0, limit));
}

/**
 * This device's document and Jellyfin's, together: each entry from the side that changed it last (Jellyfin's when
 * both did at the same time). What Jellyfin keeps that Mira does not know (a later version's) stays.
 */
export function merge(doc, remote) {
  doc = read(doc); remote = read(remote);
  const merged = { ...remote };
  delete merged.seen;
  for (const section of SECTIONS) {
    const all = { ...doc[section] };
    for (const [key, theirs] of Object.entries(remote[section])) {
      if (!theirs || typeof theirs !== 'object') continue;
      const mine = all[key];
      if (!mine || time(theirs?.at) >= time(mine.at)) all[key] = theirs;
    }
    merged[section] = section === 'settings' ? all : newest(all, LIMITS[section]);
  }
  if (doc.seen) merged.seen = true;
  return merged;
}

/** Whether Jellyfin must be told: the document differs from what it keeps. */
export function differs(merged, remote) {
  merged = read(merged); remote = read(remote);
  return SECTIONS.some((section) => !same(merged[section], remote[section]));
}

/**
 * What the device uses from a document: the settings it holds, the tracks of each series (oldest first, as the device
 * keeps them) and the titles removed, with when.
 */
export function apply(doc) {
  doc = read(doc);
  const settings = {};
  for (const [key, entry] of Object.entries(doc.settings)) if (fits(key, entry?.value)) settings[key] = entry.value;
  const byTime = (entries) => Object.entries(entries).sort(([a, x], [b, y]) => time(x.at) - time(y.at) || (a < b ? -1 : a > b ? 1 : 0));
  const tracks = Object.fromEntries(byTime(doc.tracks).map(([id, x]) => [id, track(x)]));
  const hidden = Object.fromEntries(byTime(doc.hidden).filter(([, x]) => x?.hidden).map(([id, x]) => [id, time(x.at)]));
  return { settings, tracks, hidden };
}

/** Mira web's memory of a series' tracks ({ audio, subtitle: language, « und » or null for off, forced }) and back. */
export function fromWeb(memory) {
  const on = memory?.subtitle != null;
  return track({ audio: memory?.audio, audioTitle: memory?.audioTitle, subtitles: on, subtitle: memory?.subtitle === 'und' ? null : memory?.subtitle,
    subtitleTitle: memory?.subtitleTitle, forced: memory?.forced });
}
export function toWeb(x) {
  x = track(x);
  return { audio: x.audio, subtitle: x.subtitles ? x.subtitle ?? 'und' : null, forced: x.forced, audioTitle: x.audioTitle, subtitleTitle: x.subtitleTitle };
}
