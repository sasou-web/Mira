// A profile's photo (Réglages, « Profils »): a picture of the phone or the computer, cut square around its middle and
// made a JPEG of 512 px, sent to Jellyfin in base64 (api.setPhoto). Nothing leaves the device but that JPEG: no name,
// no place or date of the original (a canvas keeps none of its EXIF).
import { h } from './dom.js';
import { tr } from './i18n.js';

const SIZE = 512;

/** Asks for a picture; resolves to its JPEG in base64, or null when none was chosen. */
export function choosePhoto() {
  return new Promise((resolve, reject) => {
    const input = h('input', { type: 'file', accept: 'image/*', hidden: true });
    input.addEventListener('change', async () => {
      const file = input.files?.[0];
      input.remove();
      if (!file) { resolve(null); return; }
      try { resolve(await squareJpeg(file)); } catch (error) { reject(error); }
    }, { once: true });
    input.addEventListener('cancel', () => { input.remove(); resolve(null); }, { once: true });
    document.body.append(input);
    input.click();
  });
}

async function squareJpeg(file) {
  let picture;
  try {
    // Turned as the camera held it (imageOrientation): iOS and recent browsers do it themselves, the others are told.
    picture = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    throw new Error(tr('Cette image ne s’ouvre pas. Choisis une photo en JPEG, PNG ou HEIC.'));
  }
  const side = Math.min(picture.width, picture.height);
  const size = Math.min(SIZE, side);
  const canvas = h('canvas', { width: size, height: size });
  const context = canvas.getContext('2d');
  context.imageSmoothingQuality = 'high';
  context.drawImage(picture, (picture.width - side) / 2, (picture.height - side) / 2, side, side, 0, 0, size, size);
  picture.close?.();
  const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.9));
  if (!blob) throw new Error(tr('Cette image ne s’ouvre pas. Choisis une photo en JPEG, PNG ou HEIC.'));
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}
