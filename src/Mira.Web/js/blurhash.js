// BlurHash decoder (algorithm by Wolt, https://blurha.sh): Jellyfin sends a few characters per image,
// which Mira paints as a soft placeholder until the image itself arrives.

const DIGITS = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~';
const cache = new Map();

function decode83(text) {
  let value = 0;
  for (const char of text) {
    const digit = DIGITS.indexOf(char);
    if (digit < 0) throw new Error('blurhash');
    value = value * 83 + digit;
  }
  return value;
}
const toLinear = (v) => { const c = v / 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; };
const toSrgb = (v) => {
  const c = Math.max(0, Math.min(1, v));
  return Math.round((c <= 0.0031308 ? c * 12.92 : 1.055 * c ** (1 / 2.4) - 0.055) * 255);
};
const signPow = (v, exp) => Math.sign(v) * Math.abs(v) ** exp;

function pixels(hash, width, height) {
  const size = decode83(hash[0]);
  const ny = Math.floor(size / 9) + 1, nx = (size % 9) + 1;
  if (hash.length !== 4 + 2 * nx * ny) throw new Error('blurhash');
  const max = (decode83(hash[1]) + 1) / 166;
  const colors = [];
  const dc = decode83(hash.slice(2, 6));
  colors.push([toLinear(dc >> 16), toLinear((dc >> 8) & 255), toLinear(dc & 255)]);
  for (let i = 1; i < nx * ny; i++) {
    const v = decode83(hash.slice(4 + i * 2, 6 + i * 2));
    colors.push([Math.floor(v / 361), Math.floor(v / 19) % 19, v % 19].map((q) => signPow((q - 9) / 9, 2) * max));
  }
  const out = new Uint8ClampedArray(width * height * 4);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let r = 0, g = 0, b = 0;
      for (let j = 0; j < ny; j++) {
        const cy = Math.cos((Math.PI * y * j) / height);
        for (let i = 0; i < nx; i++) {
          const basis = Math.cos((Math.PI * x * i) / width) * cy;
          const c = colors[i + j * nx];
          r += c[0] * basis; g += c[1] * basis; b += c[2] * basis;
        }
      }
      const p = 4 * (x + y * width);
      out[p] = toSrgb(r); out[p + 1] = toSrgb(g); out[p + 2] = toSrgb(b); out[p + 3] = 255;
    }
  }
  return out;
}

/** A small data URL for the hash, or '' when it cannot be read. */
export function placeholder(hash, aspect = 1) {
  if (!hash) return '';
  const key = `${hash}|${aspect.toFixed(2)}`;
  if (cache.has(key)) return cache.get(key);
  let result = '';
  try {
    const width = aspect >= 1 ? 32 : Math.max(8, Math.round(32 * aspect));
    const height = aspect >= 1 ? Math.max(8, Math.round(32 / aspect)) : 32;
    const canvas = document.createElement('canvas');
    canvas.width = width; canvas.height = height;
    const context = canvas.getContext('2d');
    context.putImageData(new ImageData(pixels(hash, width, height), width, height), 0, 0);
    result = canvas.toDataURL();
  } catch { result = ''; }
  if (cache.size > 400) cache.delete(cache.keys().next().value);
  cache.set(key, result);
  return result;
}
