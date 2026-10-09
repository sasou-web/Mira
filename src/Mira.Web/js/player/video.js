// One <video> for the whole app. iOS lets a page start sound only from a tap; a video element touched once by a
// tap may play later on its own. Mira touches it at the first tap anywhere, so a title starts as soon as its stream
// is known, and the next episode starts by itself.

let element = null;

export function sharedVideo() {
  if (!element) {
    element = document.createElement('video');
    element.setAttribute('playsinline', '');
    element.setAttribute('webkit-playsinline', '');
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
