// Small helpers to build the interface: elements, icons and French formatting.
import { icons } from './icons.js';

const SVG = 'http://www.w3.org/2000/svg';

/**
 * Creates an element. props: class, text, html-free attributes, style (object), on (events), dataset.
 * Children may be nodes, strings, arrays, or null/false (skipped).
 */
export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value == null || value === false) continue;
    if (key === 'class') el.className = Array.isArray(value) ? value.filter(Boolean).join(' ') : value;
    else if (key === 'text') el.textContent = value;
    else if (key === 'style' && typeof value === 'object') Object.assign(el.style, value);
    else if (key === 'on') for (const [name, fn] of Object.entries(value)) el.addEventListener(name, fn);
    else if (key === 'dataset') Object.assign(el.dataset, value);
    else if (key in el && typeof value !== 'string' && key !== 'list') el[key] = value;
    else el.setAttribute(key, value === true ? '' : value);
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

/** An icon from Phosphor's set; decorative unless a label is given. */
export function icon(name, { size = 0, label = '' } = {}) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 256 256');
  svg.setAttribute('class', size ? `icon s${size}` : 'icon');
  if (label) { svg.setAttribute('role', 'img'); svg.setAttribute('aria-label', label); }
  else svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(SVG, 'path');
  path.setAttribute('d', icons[name] ?? icons.info);
  svg.append(path);
  return svg;
}

export function clear(el) { while (el.firstChild) el.firstChild.remove(); return el; }

const TICKS = 10_000_000;
export const seconds = (ticks) => (ticks ?? 0) / TICKS;
export const ticks = (secondsValue) => Math.max(0, Math.round(secondsValue * TICKS));

/** « 1 h 52 », « 48 min ». */
export function duration(totalSeconds) {
  const minutes = Math.round(totalSeconds / 60);
  if (minutes < 1) return '1 min';
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60), m = minutes % 60;
  return m ? `${h} h ${String(m).padStart(2, '0')}` : `${h} h`;
}

/** « 1:02:03 » or « 4:05 ». */
export function clock(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds || 0));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`;
}

/** What is left of a title in progress: « reste 32 min ». */
export function remaining(item) {
  const total = seconds(item.RunTimeTicks), done = seconds(item.UserData?.PlaybackPositionTicks);
  return total > 0 && done > 0 ? `reste ${duration(total - done)}` : '';
}

export function progress(item) {
  const total = item.RunTimeTicks ?? 0, done = item.UserData?.PlaybackPositionTicks ?? 0;
  return total > 0 && done > 0 && !item.UserData?.Played ? Math.min(1, done / total) : 0;
}

/** « S1 · É3 », or « É3 » when the season number is unknown. */
export function episodeCode(item) {
  const e = item.IndexNumber != null ? `É${item.IndexNumber}${item.IndexNumberEnd ? '–' + item.IndexNumberEnd : ''}` : '';
  return [item.ParentIndexNumber != null ? `S${item.ParentIndexNumber}` : '', e].filter(Boolean).join(' · ');
}

export const plural = (n, one, many) => `${n} ${n > 1 ? many : one}`;

export function initials(name = '') {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((w) => w[0].toUpperCase()).join('') || '?';
}

/** Runs fn at most once per frame while scrolling. */
export function onScrollFrame(fn) {
  let pending = false;
  const handler = () => { if (!pending) { pending = true; requestAnimationFrame(() => { pending = false; fn(); }); } };
  addEventListener('scroll', handler, { passive: true });
  return () => removeEventListener('scroll', handler);
}

export const reducedMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
export const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
