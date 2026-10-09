// Names of audio and subtitle tracks, in French, as Mira for Windows and Mac write them (Mira.Core/PlayerText.cs):
// Jellyfin's own names are in English ("French - SUBRIP - External").
const LANGUAGES = {
  Japonais: 'jpn ja', Français: 'fra fre fr', Anglais: 'eng en', Allemand: 'deu ger de', Espagnol: 'spa es', Italien: 'ita it',
  Portugais: 'por pt', Russe: 'rus ru', Coréen: 'kor ko', Chinois: 'zho chi zh', Arabe: 'ara ar', Hindi: 'hin hi', Polonais: 'pol pl',
  Néerlandais: 'nld dut nl', Suédois: 'swe sv', Norvégien: 'nor nob no nb', Danois: 'dan da', Finnois: 'fin fi', Turc: 'tur tr',
  Hébreu: 'heb he', Thaï: 'tha th', Vietnamien: 'vie vi', Indonésien: 'ind id', Ukrainien: 'ukr uk', Tchèque: 'ces cze cs',
  Hongrois: 'hun hu', Grec: 'ell gre el', Roumain: 'ron rum ro', Catalan: 'cat ca', Malais: 'msa may ms', Filipino: 'fil', Tagalog: 'tgl',
};
const byCode = new Map(Object.entries(LANGUAGES).flatMap(([name, codes]) => codes.split(' ').map((code) => [code, name])));
const names = (() => { try { return new Intl.DisplayNames(['fr'], { type: 'language' }); } catch { return null; } })();

/** French name of an ISO 639 code ("jpn", "fre", "pt-BR"); null when unknown or undetermined. */
export function languageName(code) {
  const main = String(code ?? '').trim().split(/[-_]/)[0].toLowerCase();
  if (!main || ['und', 'zxx', 'mis', 'mul'].includes(main)) return null;
  if (byCode.has(main)) return byCode.get(main);
  try { const name = names?.of(main); return name && name.toLowerCase() !== main ? capitalize(name) : null; } catch { return null; }
}

const CODECS = {
  aac: 'AAC', ac3: 'Dolby Digital', eac3: 'Dolby Digital Plus', truehd: 'Dolby TrueHD', dts: 'DTS', flac: 'FLAC', opus: 'Opus',
  vorbis: 'Vorbis', mp3: 'MP3', subrip: 'SRT', srt: 'SRT', ass: 'ASS', ssa: 'ASS', hdmv_pgs_subtitle: 'PGS', pgs: 'PGS', pgssub: 'PGS',
  dvd_subtitle: 'VobSub', vobsub: 'VobSub', dvdsub: 'VobSub', webvtt: 'WebVTT', vtt: 'WebVTT', mov_text: 'Texte',
};
export function codecName(codec) {
  const key = String(codec ?? '').trim().toLowerCase();
  if (!key) return null;
  return CODECS[key] ?? (key.startsWith('pcm') ? 'PCM' : key.toUpperCase());
}

const channelText = (n) => ({ 1: 'mono', 2: 'stéréo', 6: '5.1', 8: '7.1' })[n] ?? `${n} canaux`;
const capitalize = (text) => (text ? text[0].toLocaleUpperCase('fr') + text.slice(1) : text);

/**
 * Menu text of a Jellyfin stream: its title, else its language ("Japonais"), else "Piste 2", numbered among the tracks
 * of its kind. Details: the language when a title is shown, the format, the channels of audio, and whether subtitles
 * are forced or in a file beside.
 */
export function trackText(stream, number = stream.Index) {
  const name = languageName(stream.Language);
  const title = String(stream.Title ?? '').trim();
  const label = title || name || `Piste ${number}`;
  const details = [];
  if (title && name && !title.toLowerCase().includes(name.toLowerCase())) details.push(name);
  const format = codecName(stream.Codec);
  if (format) details.push(format);
  if (stream.Type === 'Audio' && stream.Channels > 0) details.push(channelText(stream.Channels));
  if (stream.IsForced) details.push('forcés');
  if (stream.IsExternal) details.push('fichier externe');
  return { label: capitalize(label), details: details.join(' · ') };
}
