// Mira web in English or in French. English unless the browser puts French first of the two, or Réglages says
// otherwise (« language » of mira.settings, this device only). The French text is written in the code, as it always
// was; English comes from lang/en.js, keyed by that French text: tr('Lecture') is « Play » in English.
// Nothing of the browser is needed to load this module: Node imports it with the player's decisions.
import EN from './lang/en.js';

export const LANGUAGES = ['en', 'fr'];

/** The language of the browser, from its list in order of preference: the first of Mira's; English without one. */
export function browserLanguage(list = globalThis.navigator?.languages ?? [globalThis.navigator?.language]) {
  for (const tag of list ?? []) {
    const code = String(tag ?? '').toLowerCase().split(/[-_]/)[0];
    if (LANGUAGES.includes(code)) return code;
  }
  return 'en';
}

/** The language chosen in Réglages, '' for the browser's (and wherever the storage is blocked). */
export function chosenLanguage() {
  try {
    const value = JSON.parse(globalThis.localStorage?.getItem('mira.settings') || '{}')?.language;
    return LANGUAGES.includes(value) ? value : '';
  } catch { return ''; }
}

let current = chosenLanguage() || browserLanguage();

/** 'en' or 'fr': the language of every text Mira shows. */
export const language = () => current;
/** Switches the texts drawn from now on (the tests do; Réglages reloads the page, so that every screen follows). */
export function setLanguage(code) { current = LANGUAGES.includes(code) ? code : 'en'; }

/** For Intl: the browser's own English (en-GB writes 23:12, en-US 11:12 PM), French as France writes it. */
export function locale() {
  if (current === 'fr') return 'fr-FR';
  const tags = globalThis.navigator?.languages ?? [];
  return tags.find((tag) => /^en(-|$)/i.test(tag)) ?? 'en-US';
}

const fill = (text, params) => (params ? text.replace(/\{(\w+)\}/g, (all, name) => (name in params ? String(params[name]) : all)) : text);

/**
 * A text of Mira in the language shown: the French as written, else its translation (the French itself when there is
 * none: tools/web/unit/i18n.test.mjs finds those). « {name} » is replaced by params.name.
 */
export function tr(french, params) {
  const text = current === 'fr' ? french : EN[french];
  return fill(typeof text === 'string' ? text : french, params);
}

/**
 * The same French that English says two ways (« Annuler »: Cancel a sheet, Undo what was just done): the catalog has
 * it under « context|French ». trc('undo', 'Annuler') is « Annuler », « Undo ».
 */
export function trc(context, french, params) {
  const text = current === 'fr' ? french : EN[`${context}|${french}`];
  return fill(typeof text === 'string' ? text : french, params);
}

/**
 * A count and its noun: trn(3, '{n} épisode', '{n} épisodes') is « 3 épisodes », « 3 episodes ». French says
 * « 0 épisode » and « 1 épisode »; English « 0 episodes ». The catalog gives [one, other] under the French singular.
 * {n} is written as given (number() formats it, when needed).
 */
export function trn(n, one, many, params) {
  const all = { n, ...params };
  if (current === 'fr') return fill(n > 1 ? many : one, all);
  const forms = EN[one];
  const text = Array.isArray(forms) ? forms[n === 1 ? 0 : 1] : (n === 1 ? one : many);
  return fill(text, all);
}

/** « 1 234 » or « 1,234 »; « 1,5 » or « 1.5 ». */
export const number = (value, options) => new Intl.NumberFormat(locale(), options).format(value);

/** A time of day: « 23 h 12 » in French, as Mira writes it everywhere; « 11:12 PM » or « 23:12 » in English. */
export function timeOfDay(date) {
  if (current === 'fr') return `${date.getHours()} h ${String(date.getMinutes()).padStart(2, '0')}`;
  return new Intl.DateTimeFormat(locale(), { hour: 'numeric', minute: '2-digit' }).format(date);
}

/** A date, as Intl writes it in the language shown (options as Intl.DateTimeFormat's). */
export const dateText = (date, options) => new Intl.DateTimeFormat(locale(), options).format(date);

/** For sorting names as people of that language do. */
export const compareText = (a, b) => String(a).localeCompare(String(b), locale());
