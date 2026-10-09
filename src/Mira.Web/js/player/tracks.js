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

// ---------- Which tracks a title starts with ----------

/** The language orders of Mira on Windows and Mac (Mira.Core PlayerSettings), with the same defaults. */
export const AUDIO_LANGUAGES = [
  ['jpn,ja,fre,fra,fr,eng,en', 'Japonais, puis français'],
  ['fre,fra,fr,eng,en', 'Français, puis anglais'],
  ['eng,en,fre,fra,fr', 'Anglais, puis français'],
];
export const SUBTITLE_LANGUAGES = [
  ['fre,fra,fr,eng,en', 'Français, puis anglais'],
  ['eng,en', 'Anglais'],
  ['jpn,ja', 'Japonais'],
];

/** One key per language, whatever its code: « fre », « fra » and « fr » are all French. */
function languageKey(code) {
  const main = String(code ?? '').trim().split(/[-_]/)[0].toLowerCase();
  if (!main || ['und', 'zxx', 'mis', 'mul'].includes(main)) return null;
  return byCode.get(main) ?? main;
}
export const sameLanguage = (a, b) => { const key = languageKey(a); return key !== null && key === languageKey(b); };

/** Position of a language in an order (« jpn,ja,fre… »); Infinity when absent. */
function rank(code, order) {
  const key = languageKey(code);
  const position = key === null ? -1 : String(order ?? '').split(',').findIndex((x) => languageKey(x) === key);
  return position < 0 ? Infinity : position;
}

/** The best stream in an order: its language first, then the one the file marks as default, then the file's order. */
function best(streams, order) {
  const ranked = streams.map((s, i) => ({ s, i, r: rank(s.Language, order) })).filter((x) => x.r !== Infinity);
  ranked.sort((a, b) => a.r - b.r || Number(!!b.s.IsDefault) - Number(!!a.s.IsDefault) || a.i - b.i);
  return ranked[0]?.s ?? null;
}

/**
 * The audio and subtitle streams a title starts with, as Jellyfin indexes (-1: no subtitles). In order: the tracks
 * last chosen for this series (same languages), the language orders of the settings, then Jellyfin's own choice.
 * Subtitles in the language of the audio are left off, except forced ones (signs, foreign lines); with an audio track
 * of unknown language, Jellyfin decides.
 */
export function chooseTracks(source, { memory = null, audioOrder = '', subtitleOrder = '' } = {}) {
  const streams = source?.MediaStreams ?? [];
  const audios = streams.filter((s) => s.Type === 'Audio');
  const subtitles = streams.filter((s) => s.Type === 'Subtitle');
  const fallbackAudio = audios.find((s) => s.Index === source?.DefaultAudioStreamIndex) ?? audios[0] ?? null;
  const audio = (memory?.audio && audios.find((s) => sameLanguage(s.Language, memory.audio)))
    || best(audios, audioOrder) || fallbackAudio;
  // Text before pictures: pictures need a conversion that draws them into the video.
  const readable = [...subtitles].sort((a, b) => Number(!a.IsTextSubtitleStream) - Number(!b.IsTextSubtitleStream));
  const forcedIn = (language) => readable.find((s) => s.IsForced && sameLanguage(s.Language, language)) ?? null;

  let subtitle;
  if (memory && 'subtitle' in memory) {
    subtitle = memory.subtitle === null ? -1
      : (readable.find((s) => sameLanguage(s.Language, memory.subtitle) && !!s.IsForced === !!memory.forced)
        ?? readable.find((s) => sameLanguage(s.Language, memory.subtitle)))?.Index;
  }
  if (subtitle === undefined && languageKey(audio?.Language) !== null) {
    const first = String(subtitleOrder).split(',')[0];
    if (sameLanguage(audio.Language, first)) subtitle = forcedIn(audio.Language)?.Index ?? -1;
    else subtitle = (best(readable.filter((s) => !s.IsForced), subtitleOrder) ?? forcedIn(audio.Language))?.Index;
  }
  return {
    audio: audio?.Index ?? null,
    subtitle: subtitle ?? source?.DefaultSubtitleStreamIndex ?? -1,
  };
}

/** What a choice of tracks leaves for the next episodes of the series: languages, not indexes. */
export function trackMemory(source, audioIndex, subtitleIndex) {
  const streams = source?.MediaStreams ?? [];
  const audio = streams.find((s) => s.Type === 'Audio' && s.Index === audioIndex);
  const subtitle = streams.find((s) => s.Type === 'Subtitle' && s.Index === subtitleIndex);
  return { audio: audio?.Language ?? null, subtitle: subtitle ? subtitle.Language ?? 'und' : null, forced: !!subtitle?.IsForced };
}
