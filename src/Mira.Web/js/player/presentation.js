// Apple's full screen player (iPhone, iPad) opened, closed, or gone to Picture in Picture: what Mira does then. A
// plain function of the player's state, tested by Node (tools/web/unit).

/**
 * The presentation went from `was` to `mode` (inline, fullscreen, picture-in-picture). Returns:
 * « none »; « fullscreen » (Apple's player shows: nothing under it); « pip » (Picture in Picture: its controls under
 * it); « reenter » (Picture in Picture closed while playing: back to full screen); « close » or « close-landing »
 * (closed by the person, Terminé or a swipe down, even while the next episode loads: Mira closes, its picture first
 * shrinking back onto the page when it came from full screen).
 * Mira itself only leaves full screen when it closes or fails, both said first (closing, failed). At the very end of
 * a title iOS may leave full screen by itself: the next episode, or the title page, follows (ended; while the next one
 * loads, the video still says the last one ended: that says nothing, hence switching).
 */
export function presentationStep({ was, mode, closing, failed, paused, ended, videoEnded, switching }) {
  if (mode === was) return 'none';
  if (mode === 'fullscreen') return 'fullscreen';
  if (mode === 'picture-in-picture') return 'pip';
  if (closing || failed) return 'none';
  if (was === 'picture-in-picture' && !paused) return 'reenter';
  if (ended || (videoEnded && !switching)) return 'none';
  return was === 'fullscreen' ? 'close-landing' : 'close';
}
