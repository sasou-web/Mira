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
 * Where the next episode is offered: from the end credits when Jellyfin has them marked, else over the last seconds,
 * so that nothing of the end is cut.
 */
export function nextFrom(segments, end) {
  const outro = segments.find((s) => s.Type === 'Outro');
  return outro ? seconds(outro.StartTicks) : end - COUNTDOWN;
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
