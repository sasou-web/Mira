// The title's timeline: passages to skip, when the next episode is offered, trickplay previews, chapter marks. Plain
// functions of their inputs (times in seconds, Jellyfin's in ticks), tested by Node (tools/web/unit).
import { seconds } from '../dom.js';

export const SKIP_LABELS = { Intro: 'Passer l’intro', Recap: 'Passer le récap', Preview: 'Passer l’aperçu', Commercial: 'Passer la pub' };
/** Skipped by themselves when the settings say so. */
const AUTO_SKIP = ['Intro', 'Recap'];
/** Seconds the next episode is offered before it starts by itself. */
export const COUNTDOWN = 10;

/** The passage that can be skipped at this point (until its last second), if any. */
export const skippable = (segments, at) =>
  segments.find((s) => SKIP_LABELS[s.Type] && at >= seconds(s.StartTicks) && at < seconds(s.EndTicks) - 1);
/** A passage skipped by itself (intro, recap) when the settings say so, once: going back into it plays it. */
export const skipsItself = (segment, { autoSkip, skipped }) => !!segment && !!autoSkip && AUTO_SKIP.includes(segment.Type) && !skipped.has(segment);

/**
 * Where the next episode is offered: from the end credits when they are marked and only credits or a preview follow
 * them, else over the last seconds, so that nothing of the end is cut (a scene after the credits stays).
 */
export function nextFrom(segments, end) {
  const outro = segments.find((s) => s.Type === 'Outro' && endsPlayback(segments, s, end));
  return outro ? seconds(outro.StartTicks) : end - COUNTDOWN;
}
/** True when skipping the credits would only leave further credits, a preview or a few seconds (PlaybackMarkers.EndsPlayback). */
function endsPlayback(segments, segment, end) {
  const from = seconds(segment.StartTicks);
  let last = seconds(segment.EndTicks);
  for (const s of [...segments].sort((a, b) => a.StartTicks - b.StartTicks)) {
    if (seconds(s.StartTicks) >= from && seconds(s.StartTicks) <= last + 2 && (s.Type === 'Outro' || s.Type === 'Preview')) last = Math.max(last, seconds(s.EndTicks));
  }
  return end - last < 5;
}

// Passages named by the file's chapters ("Opening", "ED", "Générique de fin"…), as Mira for Windows and Mac reads them
// (PlaybackMarkers in Mira.Core): the same expressions and limits, kept in step by tests/fixtures/chapters.json.
/** Shorter passages are noise; a chapter longer than the second is no opening, recap or preview, whatever its title. */
const MIN_PASSAGE = 3, MAX_SHORT_PASSAGE = 360;
// .NET's \b and \d know every alphabet and digit: so do these, with the joiners .NET's \b counts as letters, and its
// '.' (anything but a line feed). Only characters outside the BMP (an emoji, a math digit) are read differently: .NET
// sees their two halves.
const WORD = '[\\p{L}\\p{Nd}\\p{Pc}\\u200C\\u200D]';
const words = (alternatives) => new RegExp(`(?<!${WORD})(?:${alternatives})(?!${WORD})`, 'u');
const RECAP = words('recap|recapitulatif|previously|precedemment|resume de l[^\\n]episode precedent');
const PREVIEW = words('preview|next episode|prochain episode|apercu|bande annonce|yokoku');
const INTRO = words('opening|intro|introduction|ouverture|theme song|op\\s?\\p{Nd}{0,2}|generique (?:de )?(?:debut|d[^\\n]ouverture)');
const OUTRO = words('ending|outro|credits|end credits|ed\\s?\\p{Nd}{0,2}|generique (?:de )?fin');
const GENERIC = words('generique');
const plain = (text) => text.normalize('NFD').replace(/\p{Mn}/gu, '').replace(/[_\-’]/g, ' ').toLowerCase();

/** The kind of passage a chapter title names, if any; a bare « Générique » is the opening before the middle, the credits after. */
export function chapterKind(title, start = 0, duration = 0) {
  if (!title || !title.trim()) return null;
  const text = plain(title);
  if (RECAP.test(text)) return 'Recap';
  if (PREVIEW.test(text)) return 'Preview';
  if (INTRO.test(text)) return 'Intro';
  if (OUTRO.test(text)) return 'Outro';
  // « Avant-générique » is the cold open: part of the story, never skipped.
  if (GENERIC.test(text) && !text.includes('avant generique')) return duration > 0 && start > duration / 2 ? 'Outro' : 'Intro';
  return null;
}

/** Jellyfin's chapters (Name, StartPositionTicks) as passages shaped like its segments ({ Type, StartTicks, EndTicks }). */
export function chapterPassages(chapters, totalTicks) {
  const ordered = (chapters ?? []).filter((c) => Number.isFinite(c.StartPositionTicks) && c.StartPositionTicks >= 0)
    .sort((a, b) => a.StartPositionTicks - b.StartPositionTicks);
  const duration = seconds(totalTicks), passages = [];
  ordered.forEach((chapter, i) => {
    const start = seconds(chapter.StartPositionTicks);
    const endTicks = i + 1 < ordered.length ? ordered[i + 1].StartPositionTicks : totalTicks;
    const end = seconds(endTicks);
    if (!Number.isFinite(end) || end - start < MIN_PASSAGE) return;
    const kind = chapterKind(chapter.Name, start, duration);
    if (!kind || (kind !== 'Outro' && end - start > MAX_SHORT_PASSAGE)) return;
    passages.push({ Type: kind, StartTicks: chapter.StartPositionTicks, EndTicks: endTicks });
  });
  return passages;
}

/**
 * Jellyfin's segments are authoritative, those of 3 s or more as Mira for Windows and Mac keeps them
 * (PlaybackMarkers.FromJellyfin): a chapter's passage only fills a kind and a time they leave empty.
 */
export function withChapters(server, fromChapters) {
  const kept = server.filter((s) => s.StartTicks >= 0 && seconds(s.EndTicks) - seconds(s.StartTicks) >= MIN_PASSAGE);
  return [...kept, ...fromChapters.filter((c) => kept.every((s) => s.Type !== c.Type && (c.EndTicks <= s.StartTicks || c.StartTicks >= s.EndTicks)))]
    .sort((a, b) => a.StartTicks - b.StartTicks);
}

/**
 * The trickplay picture at a point, in Jellyfin's tiles: which tile, and where in it, at a width of `width` pixels.
 * trick: Jellyfin's TrickplayInfo (Interval in ms, ThumbnailCount, TileWidth, TileHeight, Width, Height).
 */
export function trickCell(trick, at, width = 160) {
  const index = Math.min(trick.ThumbnailCount - 1, Math.floor((at * 1000) / trick.Interval));
  const perTile = trick.TileWidth * trick.TileHeight, tile = Math.floor(index / perTile), cell = index % perTile;
  const height = Math.round((width * trick.Height) / trick.Width);
  return { tile, width, height, x: (cell % trick.TileWidth) * width, y: Math.floor(cell / trick.TileWidth) * height };
}

/** Where the chapters after the first begin, as fractions of the title; none for fewer than two chapters. */
export function chapterMarks(chapters, total) {
  if (!total || chapters.length < 2) return [];
  return chapters.slice(1).map((c) => seconds(c.StartPositionTicks)).filter((at) => at > 0 && at < total).map((at) => at / total);
}

/**
 * The player's address without « from the start » (debut=1), once the title plays: a reload of the page later
 * resumes where the title is. Null when the address has no such thing, or is another title's.
 */
export function withoutFromStart(hash, itemId) {
  const [path, search = ''] = hash.split('?');
  const params = new URLSearchParams(search);
  if (!params.has('debut') || path !== `#/lecture/${itemId}`) return null;
  params.delete('debut');
  return `${path}${params.toString() ? `?${params}` : ''}`;
}
