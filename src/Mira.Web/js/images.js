// Jellyfin's artwork for a title, sized for the phone's screen, with its blurred placeholder.
import { base, onReachable } from './api.js';
import { placeholder } from './blurhash.js';
import { h, initials } from './dom.js';

const ASPECT = { poster: 2 / 3, wide: 16 / 9, square: 1, backdrop: 16 / 9 };

function ref(itemId, type, tag, hashes, index) {
  return itemId && tag ? { itemId, type, tag, index, hash: hashes?.[type]?.[tag] ?? '' } : null;
}

/**
 * The image of an item for a use: poster (2:3), wide (16:9 card), backdrop (banner), still (an episode's own
 * picture), logo (the title's logo), or square (a person). Falls back like Mira on Windows: an episode shows its
 * own picture first, then its series' artwork.
 */
export function artFor(item, kind) {
  if (!item) return null;
  const tags = item.ImageTags ?? {}, hashes = item.ImageBlurHashes;
  const backdrop = () => item.BackdropImageTags?.length
    ? ref(item.Id, 'Backdrop', item.BackdropImageTags[0], hashes, 0)
    : ref(item.ParentBackdropItemId, 'Backdrop', item.ParentBackdropImageTags?.[0], hashes, 0);
  switch (kind) {
    case 'poster':
      return ref(item.Id, 'Primary', tags.Primary, hashes) ?? ref(item.SeriesId, 'Primary', item.SeriesPrimaryImageTag, hashes);
    case 'still':
      return ref(item.Id, 'Primary', tags.Primary, hashes) ?? ref(item.Id, 'Thumb', tags.Thumb, hashes);
    case 'wide':
      if (item.Type === 'Episode') return ref(item.Id, 'Primary', tags.Primary, hashes) ?? ref(item.ParentThumbItemId, 'Thumb', item.ParentThumbImageTag, hashes) ?? backdrop();
      return ref(item.Id, 'Thumb', tags.Thumb, hashes) ?? backdrop() ?? ref(item.Id, 'Primary', tags.Primary, hashes);
    case 'backdrop':
      return backdrop() ?? ref(item.Id, 'Thumb', tags.Thumb, hashes) ?? ref(item.ParentThumbItemId, 'Thumb', item.ParentThumbImageTag, hashes);
    case 'logo':
      return ref(item.Id, 'Logo', tags.Logo, hashes) ?? ref(item.ParentLogoItemId, 'Logo', item.ParentLogoImageTag, hashes);
    case 'square':
      return ref(item.Id, 'Primary', item.PrimaryImageTag ?? tags.Primary, hashes);
    default:
      return null;
  }
}

/** Width in device pixels, by steps, so that the server and the phone keep fewer sizes. */
function pixels(cssWidth) {
  const scale = Math.min(window.devicePixelRatio || 1, 3);
  return Math.min(3840, Math.ceil((cssWidth * scale) / 80) * 80);
}

export function imageUrl(art, cssWidth, quality = 85) {
  if (!art) return '';
  const index = art.index != null && art.type === 'Backdrop' ? `/${art.index}` : '';
  return `${base}/Items/${encodeURIComponent(art.itemId)}/Images/${art.type}${index}?maxWidth=${pixels(cssWidth)}&quality=${quality}&tag=${encodeURIComponent(art.tag)}`;
}

// Images already shown once: drawn again (a screen redrawn, a row refreshed), they appear at once, without fading in
// from their placeholder a second time.
const shown = new Set();
// Pictures that failed while the server was away are asked for again once it answers.
const failed = new Set();
onReachable((up) => {
  if (!up) return;
  for (const img of failed) { failed.delete(img); if (img.isConnected) { const src = img.src; img.removeAttribute('src'); img.src = src; } }
});

/**
 * A picture box: the placeholder at once, the image fading in once loaded (at once when already shown), or the
 * title's initials when the server has none. `eager` for what is on screen at first (banner, title page).
 */
export function picture(art, { kind = 'poster', width = 160, label = '', eager = false, className = '' } = {}) {
  const box = h('div', { class: ['art', kind === 'backdrop' ? 'wide' : kind, className] });
  if (!art) {
    box.append(h('span', { class: 'initials', text: label ? (kind === 'square' ? initials(label) : label) : '' }));
    return box;
  }
  const blur = placeholder(art.hash, ASPECT[kind] ?? 1);
  if (blur) box.style.backgroundImage = `url("${blur}")`;
  const img = h('img', { alt: '', decoding: 'async', loading: eager ? 'eager' : 'lazy', draggable: false });
  // What shows at first goes before the rest: a home server over HTTP/1.1 serves a few pictures at a time.
  img.fetchPriority = eager ? 'high' : 'low';
  const born = performance.now();
  img.addEventListener('load', () => {
    // Loaded before its screen showed (a tab waits for its first pictures), or at once from the phone's cache: part of
    // the screen's first picture, without a fade of its own.
    if (!box.isConnected || performance.now() - born < 100) img.classList.add('instant');
    img.classList.add('ready'); if (shown.size > 2000) shown.clear(); shown.add(img.src);
  }, { once: true });
  img.addEventListener('error', () => {
    // The placeholder stays; the picture comes once the server answers again.
    if (blur) { failed.add(img); return; }
    img.remove();
    box.append(h('span', { class: 'initials', text: label ? (kind === 'square' ? initials(label) : label) : '' }));
  });
  img.src = imageUrl(art, width);
  if (shown.has(img.src) || (img.complete && img.naturalWidth)) img.classList.add('instant', 'ready');
  box.append(img);
  return box;
}

/** A title's logo as an image element, or null. */
export function logo(item, width = 380, className = 'slide-logo') {
  const art = artFor(item, 'logo');
  if (!art) return null;
  // It fades in like the picture under it, instead of popping in a moment later.
  const img = h('img', { class: [className, 'fade'], alt: item.SeriesName ?? item.Name ?? '', decoding: 'async' });
  img.addEventListener('load', () => img.classList.add('ready'), { once: true });
  img.src = imageUrl(art, width, 90);
  if (img.complete && img.naturalWidth) img.classList.add('ready');
  return img;
}
