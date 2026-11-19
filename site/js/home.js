// A home link of « Le foyer »: https://miracinema.app/home#<data>. The data (the server's name and address on its
// Tailscale network, a single-use key) stays in this browser: nothing here sends it anywhere. The page reads only what
// it shows (the name, the end), then hands the whole of it to Mira through mira://home#<data>.
(() => {
  'use strict';
  const french = (navigator.languages || [navigator.language || 'en']).find(x => /^(fr|en)\b/i.test(x))?.toLowerCase().startsWith('fr') ?? false;
  if (french) {
    document.documentElement.lang = 'fr';
    document.title = 'Rejoindre le foyer · Mira';
    for (const node of document.querySelectorAll('[data-fr]')) node.textContent = node.dataset.fr;
    for (const link of document.querySelectorAll('a[data-get]')) link.href = link.getAttribute('href').replace(/^\//, '/fr/');
  }

  const data = location.hash.slice(1);
  let link = null;
  try {
    const text = atob(data.replace(/-/g, '+').replace(/_/g, '/'));
    const parsed = JSON.parse(new TextDecoder().decode(Uint8Array.from(text, c => c.charCodeAt(0))));
    if (parsed && parsed.v === 1 && typeof parsed.key === 'string' && typeof parsed.url === 'string' && typeof parsed.name === 'string') link = parsed;
  } catch {
    link = null;
  }

  const show = state => {
    for (const block of document.querySelectorAll('[data-state]')) block.hidden = block.dataset.state !== state;
  };
  if (!link) return show('broken');
  const until = new Date(Number(link.until) * 1000);
  if (!(until > new Date())) return show('spent');

  document.querySelector('[data-name]').textContent = link.name.slice(0, 80) || 'Jellyfin';
  document.querySelector('[data-open]').href = 'mira://home#' + data;
  const note = document.querySelector('.note');
  note.textContent += until.toLocaleString(french ? 'fr-FR' : 'en-GB', { weekday: 'long', hour: '2-digit', minute: '2-digit' }) + '.';
  show('ready');
})();
