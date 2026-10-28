// « Sous-titres automatiques » (the extension's own subtitle provider and loop, Mira.Jellyfin Subtitles/): what its
// journal says, in a line, and the languages it can be asked for. As Mira for Windows and Mac say it (Mira.Core
// AutoSubtitles, the same cases in tests/fixtures/subtitles-auto.json). Nothing of the browser at load: Node tests it.
import { tr, trn, number, language as shown } from './i18n.js';

/** The languages offered, by the code Jellyfin understands (ISO 639-2), the most asked first; the extension knows more. */
export const AUTO_LANGUAGES = ['fre', 'eng', 'spa', 'ger', 'ita', 'por', 'jpn', 'dut', 'rus', 'pol', 'tur', 'ara', 'chi', 'kor'];
/** At most this many languages (as the extension keeps). */
export const MAX_LANGUAGES = 5;

// French as Mira.Core writes it (a no-break space between thousands); English as Intl does.
const thousands = (n) => (shown() === 'fr' ? String(n).replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0') : number(n));
const seconds = (value) => tr('{n} s', { n: number(Math.abs(value), { maximumFractionDigits: 2 }) });

/** How a file kept was put on time: on what, by how much, the speed. Empty when it was not aligned. */
function aligned(entry) {
  if (!entry.alignedOn) return '';
  const on = entry.alignedOn === 'track' ? tr('sur la piste du fichier') : tr('sur l’audio');
  const parts = [];
  if (entry.shift) parts.push(entry.shift > 0 ? tr('{n} plus tard', { n: seconds(entry.shift) }) : tr('{n} plus tôt', { n: seconds(entry.shift) }));
  if (entry.speed) parts.push(tr('vitesse corrigée'));
  return parts.length ? tr('recalés {on}, {how}', { on, how: parts.join(', ') }) : tr('vérifiés {on}', { on });
}

/**
 * One thing the journal says, in a line: « Gardés · OpenSubtitles · note 82/100 · recalés sur l’audio, 3,17 s plus
 * tard ». entry: { kind, source, score, detail, alignedOn, shift, speed } (the detail already in the reader's language).
 */
export function journalLine(entry) {
  const score = entry.score != null ? tr('note {n}/100', { n: entry.score }) : '';
  const detail = entry.detail ?? '';
  const parts = {
    kept: [tr('Gardés'), entry.source, score, aligned(entry)],
    none: [tr('Rien d’assez sûr pour l’instant : nouvel essai plus tard'), score && tr('meilleur : {score}', { score }), detail],
    'given-up': [tr('Rien trouvé en 30 jours : Mira n’en cherche plus'), score && tr('meilleur : {score}', { score }), detail],
    rejected: [tr('Écartés'), entry.source, score, detail],
    failed: [entry.source ? tr('Échec chez {source}', { source: entry.source }) : tr('Échec'), detail],
    quota: [entry.source ? tr('Quota épuisé chez {source} : Mira attend sa remise à zéro', { source: entry.source }) : tr('Quota épuisé : Mira attend sa remise à zéro')],
  }[entry.kind] ?? [detail];
  return parts.filter(Boolean).join(' · ');
}

/** What the loop is doing, under the title: how many titles wait, and until when a spent quota holds them. */
export function waitingLine({ settings, waiting = 0, pausedUntil = null }, timeText = String) {
  if (!settings?.enabled) return tr('Arrêtés : Mira ne cherche rien tout seul.');
  if (!settings.languages?.length) return tr('Choisis au moins une langue.');
  if (pausedUntil) return tr('Quota épuisé : reprise à {time}.', { time: timeText(new Date(pausedUntil)) });
  return waiting ? trn(waiting, '{n} recherche en attente.', '{n} recherches en attente.', { n: thousands(waiting) }) : tr('Rien en attente : Mira cherche dès qu’un titre arrive.');
}
