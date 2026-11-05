// The extension's own pages (party.html, index.html): in French when the browser puts French before English, and, on
// a party's page, « Open in Mira » where Mira's app may be (Windows, Mac, iPhone: mira://party/…). No session,
// nothing sent: the way into Mira is a plain link.
const french = (() => {
  for (const language of navigator.languages || [navigator.language || '']) {
    const code = String(language).toLowerCase().split('-')[0];
    if (code === 'fr') return true;
    if (code === 'en') return false;
  }
  return false;
})();

if (french) {
  document.documentElement.lang = 'fr';
  for (const el of document.querySelectorAll('[data-fr]')) el.textContent = el.dataset.fr;
}

/** Where Mira's app may be: Windows and Mac (mira:// from the installer, the bundle), iPhone and iPad (the native app). */
function platform() {
  const ua = navigator.userAgent || '';
  if (/iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'ios';
  if (/Android/.test(ua)) return 'android';
  if (/Windows/.test(ua)) return 'windows';
  if (/Macintosh/.test(ua)) return 'mac';
  return 'other';
}

const party = document.querySelector('.party');
if (party && party.dataset.state !== 'gone') {
  const host = party.dataset.host || '';
  const lead = party.querySelector('.party-lead');
  if (french && lead) lead.textContent = `${host} t’invite à regarder ensemble.`;
  const app = party.querySelector('.party-app');
  const hint = party.querySelector('.party-hint');
  if (platform() === 'android' || platform() === 'other') {
    app.remove();
    party.querySelector('.party-elsewhere').hidden = false;
  } else {
    app.addEventListener('click', () => {
      // An app that opened hides this page; still here after a while, nothing took the link.
      const shown = setTimeout(() => { if (document.visibilityState === 'visible') hint.hidden = false; }, 2500);
      document.addEventListener('visibilitychange', () => clearTimeout(shown), { once: true });
    });
  }
}
