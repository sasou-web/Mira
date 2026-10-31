// « Ta party »: the page of a party link (party.html, written by the extension's PartyController). Mira's app where it
// may be installed (Windows, Mac, iPhone: mira://party/…), Mira web elsewhere, the way chosen last time kept. No
// session, nothing sent: both ways are plain links.
const page = document.querySelector('.party');
const WAY = 'mira.party.way';

const french = (() => {
  for (const language of navigator.languages || [navigator.language || '']) {
    const code = String(language).toLowerCase().split('-')[0];
    if (code === 'fr') return true;
    if (code === 'en') return false;
  }
  return false;
})();

function remembered() { try { return localStorage.getItem(WAY); } catch { return null; } }
function remember(way) { try { localStorage.setItem(WAY, way); } catch { /* private browsing: asked each time */ } }

/** Where Mira's app may be: Windows and Mac (mira:// from the installer, the bundle), iPhone and iPad (the native app). */
function platform() {
  const ua = navigator.userAgent || '';
  if (/iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'ios';
  if (/Android/.test(ua)) return 'android';
  if (/Windows/.test(ua)) return 'windows';
  if (/Macintosh/.test(ua)) return 'mac';
  return 'other';
}

if (page && page.dataset.state !== 'gone') {
  const host = page.dataset.host || '';
  if (french) {
    document.documentElement.lang = 'fr';
    for (const el of page.querySelectorAll('[data-fr]')) el.textContent = el.dataset.fr;
    const lead = page.querySelector('.party-lead');
    if (lead) lead.textContent = `${host} t’invite à regarder ensemble.`;
  }
  const app = page.querySelector('.party-app');
  const web = page.querySelector('.party-web');
  const hint = page.querySelector('.party-hint');
  const where = platform();
  if (where === 'android' || where === 'other') app.remove();
  else if (where === 'ios') {
    // Few iPhones have Mira's app (AltStore): Safari first, the app for those who have it.
    web.classList.add('primary'); app.classList.remove('primary');
    web.after(app);
    app.textContent = french ? 'J’ai l’app Mira' : 'I have the Mira app';
  }
  web.addEventListener('click', () => remember('web'));
  app.addEventListener('click', () => {
    remember('app');
    // An app that opened hides this page; still here after a while, nothing took the link.
    const shown = setTimeout(() => { if (document.visibilityState === 'visible') hint.hidden = false; }, 2500);
    document.addEventListener('visibilitychange', () => clearTimeout(shown), { once: true });
  });
  // The app, at once, when it took the link last time on this device; the page stays, should it be gone since.
  if (remembered() === 'app' && page.contains(app)) app.click();
}
