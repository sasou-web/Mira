// A join link: https://miracinema.app/join#<data> (JoinLink of Mira.Core). The data (what the link is, the server's
// Tailscale address, a single-use key) stays in this browser: nothing here sends it anywhere. The page reads only
// whether it is a friend's invite or a home link, then hands the whole of it to Mira through mira://join#<data>.
(() => {
  'use strict';
  const french = (navigator.languages || [navigator.language || 'en']).find(x => /^(fr|en)\b/i.test(x))?.toLowerCase().startsWith('fr') ?? false;
  if (french) {
    document.documentElement.lang = 'fr';
    document.title = 'Tu es invité · Mira';
    for (const node of document.querySelectorAll('[data-fr]')) node.textContent = node.dataset.fr;
    for (const link of document.querySelectorAll('a[data-get]')) link.href = link.getAttribute('href').replace(/^\//, '/fr/');
  }

  const show = (...states) => {
    for (const block of document.querySelectorAll('[data-state]')) block.hidden = !states.includes(block.dataset.state);
  };
  const data = location.hash.slice(1).trim();
  let kind = 0;
  if (/^[A-Za-z0-9_-]{12,1200}={0,2}$/.test(data)) {
    try {
      const bytes = Uint8Array.from(atob(data.replace(/=+$/, '').replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
      // [kind][IPv4 ×4][port ×2][key length][key][path]: the same checks as Mira's, enough to say what it is.
      if (bytes.length >= 9 && bytes[7] > 0 && bytes.length >= 8 + bytes[7] && (bytes[5] << 8 | bytes[6]) > 0) kind = bytes[0] & 0x7f;
    } catch {
      kind = 0;
    }
  }
  if (kind !== 1 && kind !== 2) return show('broken');
  document.querySelector('[data-open]').href = 'mira://join#' + data;
  show(kind === 2 ? 'friend' : 'home', 'open');
})();
