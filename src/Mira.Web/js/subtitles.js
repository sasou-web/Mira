// « Sous-titres gardés sur Jellyfin »: the search of Jellyfin's subtitle extension (Open Subtitles…), shown only when one
// is installed, and what Mira says of a subtitle kept. As Mira for Windows and Mac say it (Mira.Core JellyfinSubtitles,
// the same cases in tests/fixtures/subtitles.json). Nothing of the browser at load: Node tests it (tools/web/unit).
import { languageName } from './player/tracks.js';

/** The formats Mira sends to Jellyfin, all among its subtitle extensions. */
export const SUBTITLE_FORMATS = ['srt', 'ass', 'ssa', 'vtt', 'sub'];

/** The languages searched, by the code Jellyfin understands (ISO 639-2, as its own dashboard sends). */
const KNOWN = [
  ['fre', 'fre fra fr french francais français vf vff vfq vostfr'],
  ['eng', 'eng en english anglais vosta'],
  ['jpn', 'jpn ja jp japanese japonais'],
  ['spa', 'spa es spanish espanol español'],
  ['ger', 'ger deu de german deutsch allemand'],
  ['ita', 'ita it italian italiano'],
  ['por', 'por pt portuguese portugues português'],
].map(([code, names]) => [code, names.split(' ')]);

/** The code Jellyfin searches with for a language code (« fr », « fra » → « fre »); null when Mira does not know it. */
export function searchCode(code) {
  const main = String(code ?? '').trim().split(/[-_]/)[0].toLowerCase();
  return (main && KNOWN.find(([, names]) => names.includes(main))?.[0]) || null;
}
/** The languages offered to search: those of the subtitle preferences, then French and English, three at most. */
export const searchLanguages = (preferences) =>
  [...new Set([...String(preferences ?? '').split(',').map(searchCode), 'fre', 'eng'].filter(Boolean))].slice(0, 3);

/** A Jellyfin result (RemoteSubtitleInfo) put by translation: an AI's or a machine's. */
const machine = (found) => !!(found.MachineTranslated || found.AiTranslated);
/** The search's results: the version of this very file first (its hash), then those people wrote, the most downloaded first. */
export const sortRemote = (found) => [...(found ?? [])].filter((x) => x?.Id)
  .sort((a, b) => Number(!!b.IsHashMatch) - Number(!!a.IsHashMatch) || Number(machine(a)) - Number(machine(b)) || (b.DownloadCount ?? 0) - (a.DownloadCount ?? 0));
const thousands = (n) => String(n).replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
/** A result as a row: its name, then « OpenSubtitles · SRT · 1 234 téléchargements · pour ce fichier ». */
export function remoteText(found) {
  const downloads = found.DownloadCount ?? 0;
  return {
    label: found.Name || 'Sous-titres',
    details: [found.ProviderName, String(found.Format ?? '').toUpperCase(),
      downloads > 0 ? `${thousands(downloads)} ${downloads > 1 ? 'téléchargements' : 'téléchargement'}` : '',
      found.IsHashMatch ? 'pour ce fichier' : '', found.Forced ? 'forcés' : '', found.HearingImpaired ? 'pour sourds et malentendants' : '',
      machine(found) ? 'traduction automatique' : ''].filter(Boolean).join(' · '),
  };
}
/** The sentence said before any search: what leaves the server, and for whom. */
export const privacyText = (providers) =>
  `Jellyfin demande à ${providers?.length ? providers.join(' et ') : 'son extension de sous-titres'} : le nom du fichier et son empreinte partent chez ce service.`;

/** What happened to a subtitle sent to Jellyfin (Kept, KeptInJellyfinData, Refused, NotWritten), in a sentence. */
export function keptText(kept, language) {
  const known = languageName(language);
  const name = known ? `, en ${known.toLowerCase()}` : '';
  return {
    Kept: `Sous-titres gardés sur Jellyfin${name} : tes autres appareils les proposent aussi.`,
    KeptInJellyfinData: `Sous-titres gardés sur Jellyfin${name} : tes autres appareils les proposent aussi. Jellyfin n’a que le droit de lire le dossier de tes vidéos : il range ceux de cette bibliothèque dans ses propres données.`,
    Refused: 'Ton compte Jellyfin n’a pas le droit de gérer les sous-titres : son administrateur peut le lui donner (Tableau de bord → Utilisateurs).',
  }[kept] ?? 'Jellyfin n’a pas pu enregistrer ces sous-titres : il n’a pas le droit d’écrire dans le dossier de ce titre.';
}
/**
 * The subtitle file a title did not have at a previous look: the one just kept, once Jellyfin has read it. By its path,
 * since a new file may move the indexes of the others.
 */
export function arrivedSubtitle(before, now) {
  const known = new Set((before ?? []).filter((s) => s.Type === 'Subtitle' && s.IsExternal).map((s) => s.Path));
  return (now ?? []).find((s) => s.Type === 'Subtitle' && s.IsExternal && s.Path && !known.has(s.Path)) ?? null;
}
