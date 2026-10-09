// One <video> for the whole app. iOS lets a page start sound only from a tap; a video element touched once by a
// tap may play later on its own. Mira touches it at the first tap anywhere, so a title starts as soon as its stream
// is known, and the next episode starts by itself.
import { isIOS, settings } from '../session.js';

/**
 * iPhone and iPad play in Apple's own player, in full screen: its controls, AirPlay, Picture in Picture, and the
 * subtitles and audio tracks of its menu. Never inline, where iOS takes Mira for a page playing in the background.
 * `nativePlayer` in the settings turns it on in Safari on a Mac too, for the Safari check.
 */
export const appleNative = () => {
  const forced = settings.get('nativePlayer');
  if (forced === false) return false;
  return (forced === true || isIOS()) && 'webkitEnterFullscreen' in HTMLVideoElement.prototype && 'webkitPresentationMode' in HTMLVideoElement.prototype;
};

let element = null;

export function sharedVideo() {
  if (!element) {
    element = document.createElement('video');
    // Without playsinline, an iPhone plays a video only in Apple's full screen player.
    if (!appleNative()) {
      element.setAttribute('playsinline', '');
      element.setAttribute('webkit-playsinline', '');
    }
    element.setAttribute('x-webkit-airplay', 'allow');
    element.preload = 'auto';
    element.crossOrigin = null;
  }
  return element;
}

function unlock() {
  const video = sharedVideo();
  if (video.currentSrc) return;
  try {
    video.load();
    const attempt = video.play();
    attempt?.then?.(() => video.pause(), () => {});
  } catch { /* Nothing to unlock on this browser. */ }
}

for (const name of ['touchend', 'click', 'keydown']) {
  document.addEventListener(name, unlock, { capture: true, once: true, passive: true });
}
