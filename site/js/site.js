// miracinema.app: the page is complete without this script. It only puts this device's download first: the
// hero's buttons and, in Download, a "Your device" mark on its door.
const touchMac = /Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1;
const device = /iPhone|iPad|iPod|Android/.test(navigator.userAgent) || touchMac ? 'phone'
  : /Mac OS X|Macintosh/.test(navigator.userAgent) ? 'mac' : 'windows';
if (device !== 'windows') {
  document.querySelectorAll('.actions [data-for]').forEach(link => { link.hidden = link.dataset.for !== device; });
}
if (/Windows|Mac OS X|Macintosh|iPhone|iPad|iPod|Android/.test(navigator.userAgent)) {
  const door = document.querySelector(`.door[data-for="${device}"]`);
  door?.classList.add('yours');
  door?.querySelector('.needs b')?.removeAttribute('hidden');
}
