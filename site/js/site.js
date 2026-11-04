// miracinema.app: the page is complete without this script (the tour's button is then a link to its file). It puts this
// device's download first, and plays the tour in the page: its band opens to the film's size, the rest of the page goes
// dark while it plays, and a click outside it, Escape or scrolling it away pauses it.
const ua = navigator.userAgent;
const touchMac = /Macintosh/.test(ua) && navigator.maxTouchPoints > 1;
const device = /iPhone|iPad|iPod|Android/.test(ua) || touchMac ? 'phone' : /Mac OS X|Macintosh/.test(ua) ? 'mac' : 'windows';
if (device !== 'windows') {
  document.querySelectorAll('.actions [data-for]').forEach(link => { link.hidden = !link.dataset.for.split(' ').includes(device); });
}
if (/Windows|Mac OS X|Macintosh|iPhone|iPad|iPod|Android/.test(ua)) {
  document.querySelector(`.doors [data-for="${device}"]`)?.classList.add('yours');
}

const lights = document.createElement('div');
lights.className = 'lights';
document.body.append(lights);
const pauseAll = () => document.querySelectorAll('.tour video').forEach(video => video.pause());
lights.addEventListener('click', pauseAll);
document.addEventListener('keydown', event => { if (event.key === 'Escape') pauseAll(); });

for (const tour of document.querySelectorAll('.tour')) {
  const video = tour.querySelector('video');
  const source = video.querySelector('source');
  const play = tour.querySelector('.play');
  const poster = tour.querySelector('.poster');
  const failed = tour.parentElement.querySelector('.tour-failed');
  play.setAttribute('role', 'button');
  // Not played (a browser without H.264, the file not reached): the still comes back, the button is a link to the
  // file again, and the line under the band says so, with the file to download.
  const fail = () => {
    video.pause();
    document.documentElement.classList.remove('lights-down');
    tour.classList.remove('playing');
    tour.dataset.failed = '';
    video.controls = false;
    play.removeAttribute('role');
    if (failed) failed.hidden = false;
  };
  const start = event => {
    if ('failed' in tour.dataset) return;
    event.preventDefault();
    if (source && !video.canPlayType(source.type)) { fail(); return; }
    // While the film loads, the still already on screen (no other download).
    if (poster?.currentSrc) video.poster = poster.currentSrc;
    tour.classList.add('playing');
    video.controls = true;
    video.play().catch(() => {});
    video.focus();
  };
  play.addEventListener('click', start);
  play.addEventListener('keydown', event => { if (event.key === ' ') start(event); });
  video.addEventListener('play', () => document.documentElement.classList.add('lights-down'));
  for (const name of ['pause', 'ended', 'emptied']) {
    video.addEventListener(name, () => document.documentElement.classList.remove('lights-down'));
  }
  source?.addEventListener('error', () => { if (tour.classList.contains('playing')) fail(); });
  video.addEventListener('error', fail);
  // Scrolled away while it plays (less than a quarter of it left on screen): paused, the lights back on.
  new IntersectionObserver(([entry]) => {
    if (entry.intersectionRatio < 0.25 && !video.paused) video.pause();
  }, { threshold: 0.25 }).observe(tour);
}
