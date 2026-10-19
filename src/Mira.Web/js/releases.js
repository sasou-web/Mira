// « Télécharger sur le PC serveur »: what a release name says (show or film, episode, resolution, source, codec,
// languages, group), the release Mira puts forward for each title or episode, and the order of the others. Cases
// shared in tests/fixtures/releases.json. Nothing of the browser at load: Node imports it as it is.
import { reportsHealth } from './torlink.js';
import { tr } from './i18n.js';

// The release's own tags, as they are; a version filmed in a theatre gets words of the page (asked when shown).
const SOURCE_LABELS = { remux: 'Remux', bluray: 'BluRay', webdl: 'WEB-DL', webrip: 'WEBRip', hdrip: 'HDRip', hdtv: 'HDTV', dvd: 'DVD' };
// The source first: a 1080p WEB-DL beats a 720p BluRay, a 1080p BluRay beats a 2160p WEB-DL (heavy to convert on a
// small server, and no better on a phone). A version filmed in a theatre is always last.
const SOURCE_SCORE = { remux: 14, bluray: 12, webdl: 10, webrip: 7, hdrip: 5, hdtv: 4, dvd: 2, cam: -100 };
const RESOLUTION_SCORE = { 2160: 20, 1080: 30, 720: 16, 576: 4, 480: 4 };
const LANGUAGE_SCORE = { MULTI: 8, VFF: 5, VFQ: 5, VF: 5, VOSTFR: 3 };

// Words a title never goes past: the first of them ends it.
const TAGS = new Set(`4k 8k uhd hdr hdr10 hdr10+ hdr10plus dv dovi hlg sdr bluray blu-ray bdrip brrip bdremux remux bd bdmv
  web-dl webdl webrip web-rip web hdtv pdtv hdrip dvdrip dvd dvdr dvd5 dvd9 dvdscr screener scr cam camrip hdcam ts hdts
  telesync tc hdtc telecine tvrip xvid divx hevc avc av1 vp9 x264 x265 h264 h265 hi10 hi10p 10bit 8bit aac ac3 eac3 ddp
  dts truehd atmos flac opus mp3 dual dual-audio multi multi-audio multisub multi-subs msubs proper repack rerip internal
  nf amzn dsnp hmax atvp pcok cr french truefrench vff vfq vfi vf vostfr subfrench complete integrale intégrale batch
  extended unrated uncut remastered imax`.split(/\s+/));

const tokenize = (text) => [...text.matchAll(/[^\s[\](){}]+/g)].map((m) => ({ text: m[0], index: m.index }));
const bare = (token) => token.toLowerCase().replace(/^[-.,:;+~]+|[-.,:;~]+$/g, '');
const isTag = (token) => {
  const t = bare(token);
  return TAGS.has(t) || /^\d{3,4}[pi]$/.test(t) || /^[hx]\.?26[45]$/.test(t) || /^(?:dd|ddp|eac3|ac3|aac|dts)[\d.+]*$/.test(t) || /^\d{1,2}-?bits?$/.test(t)
    || (t.includes('-') && TAGS.has(t.slice(0, t.indexOf('-'))));
};
const has = (text, pattern) => new RegExp(`(?<![a-z0-9])(?:${pattern})(?![a-z0-9])`, 'i').test(text);

/** A title to compare: lower case, no accent, no apostrophe, « & » as « and », one space between words. */
export function titleKey(title) {
  return String(title ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/['’`´]/g, '').replace(/&/g, ' and ')
    .toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
}

const tidy = (text) => {
  const title = text.replace(/\s+/g, ' ').trim().replace(/^[\s\-–—.:;,_+~|]+|[\s\-–—.:;,_+~|([]+$/g, '');
  return title === title.toLowerCase() && /\p{L}/u.test(title) ? title.replace(/(^|\s)(\p{L})/gu, (m, s, c) => s + c.toUpperCase()) : title;
};

/**
 * What a release name says: { title, year, alternate (the other name given in parentheses), season, episode,
 * lastEpisode, absolute (numbered without a season), pack (a whole season or more), resolution (2160, 1080…),
 * source (remux, bluray, webdl, webrip, hdrip, hdtv, dvd, cam), codec (HEVC, H.264, AV1, XviD, VP9), tenBit, hdr
 * (HDR10+, HDR, DV, DV HDR), languages (MULTI, VFF, VFQ, VF, VOSTFR), group, repack }; a field it does not say is null.
 */
export function parseRelease(name) {
  let text = String(name ?? '').replace(/[\u0000-\u001f\u007f-\u009f]/g, ' ').trim().replace(/\.(?:mkv|mp4|m4v|avi|webm|ts|torrent)$/i, '');
  text = text.replace(/^\s*\[?\s*(?:www\.)?[\w-]+\.(?:com|org|net|to|se|cc|tv|xyz|io|me|info|ws|mx|lol|nz|ch|la|in|is|am|li|pw|site|club|top|rs|ru|eu|co|uk|fr)\s*\]?\s*[-–—:|]+\s*/i, '');
  let group = null;
  const lead = /^\s*\[([^[\]]{1,40})\]\s*/.exec(text);
  if (lead) { group = lead[1].trim(); text = text.slice(lead[0].length); }

  // The other name, in parentheses after the release's own: « (JoJo no Kimyou na Bouken: Steel Ball Run, Dual-Audio) ».
  let alternate = null;
  for (const m of text.matchAll(/\(([^()]*)\)/g)) {
    let first = m[1].split(',')[0];
    if (first.includes(' - ')) first = first.slice(0, first.indexOf(' - '));
    first = first.trim();
    if (first.length >= 3 && /\p{L}/u.test(first) && !/^(?:19|20)\d\d$/.test(first) && !tokenize(first).some((t) => isTag(t.text))) { alternate = tidy(first); break; }
  }

  // The group at the end of a scene name: « …H.264-VARYG (…) ».
  if (!group) {
    const tail = /-\s?([A-Za-z0-9][A-Za-z0-9_]{1,24})\s*(?:\([^()]*\)|\[[^[\]]*\]|\{[^{}]*\})*\s*$/.exec(text);
    if (tail && !/^\d+$/.test(tail[1]) && !isTag(tail[1]) && !/web$/i.test(text.slice(0, tail.index))) group = tail[1];
  }

  const lower = text.toLowerCase();
  // Scene names put dots or underscores for spaces; « Mr. Robot » keeps its dot.
  let spaced = text.replace(/_/g, ' ');
  // « AAC2.0 » and « 5.1 » keep theirs too, « 2049.2017 » does not.
  if ((spaced.match(/\./g) ?? []).length >= (spaced.match(/ /g) ?? []).length) spaced = spaced.replace(/\./g, (dot, at, all) => /\d/.test(all[at - 1] ?? '') && /^\d(?!\d)/.test(all.slice(at + 1)) ? dot : ' ');
  const plain = spaced.replace(/\[[^\]]*\]|\{[^}]*\}/g, ' ').replace(/\(([^()]*)\)/g, (m, inner) => /^\s*(?:19|20)\d\d\s*$/.test(inner) ? ` ${inner} ` : ' ');

  // Season and episode.
  let season = null, episode = null, lastEpisode = null, absolute = false, pack = false, cut = plain.length;
  const mark = (index) => { if (index >= 0 && index < cut) cut = index; };
  let m = /(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,4})(?:-?e(\d{1,4})|-(\d{1,4})(?![0-9p]))?(?![0-9])/i.exec(plain);
  if (m) { season = +m[1]; episode = +m[2]; lastEpisode = m[3] || m[4] ? +(m[3] || m[4]) : null; mark(m.index); }
  if (!m && (m = /(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])/i.exec(plain))) { season = +m[1]; episode = +m[2]; mark(m.index); }
  if (episode === null) {
    const range = /(?<![a-z0-9])(?:s\d{1,2}\s*[-~]\s*s\d{1,2}|(?:seasons?|saisons?)\s*\d{1,2}\s*[-~]\s*\d{1,2})(?![0-9])/i.exec(plain);
    const single = /(?<![a-z0-9])(?:s(\d{1,2})(?![0-9a-z])|(?:season|saison|temporada)\s*(\d{1,2})(?![0-9])|(\d{1,2})(?:st|nd|rd|th)\s+season)/i.exec(plain);
    if (range) { pack = true; mark(range.index); } else if (single) { season = +(single[1] ?? single[2] ?? single[3]); pack = true; mark(single.index); }
    // Fansub numbering: « Show - 05 », « Show - 01 ~ 12 » (a batch); never a year.
    const number = /(?:^|\s)[-–]\s*(?:ep?\.?\s*|episode\s*|#)?(\d{1,4})(?:v\d)?(?:\s*[-~]\s*(\d{1,4})(?:v\d)?)?(?=\s|$)/i.exec(plain);
    if (number && !/^(?:19|20)\d\d$/.test(number[1]) && (group || season !== null || number[1].length <= 3)) {
      episode = +number[1]; mark(number.index);
      if (number[2]) { lastEpisode = +number[2]; pack = true; }
      if (pack && !number[2]) pack = false;
      absolute = season === null;
    }
  }
  const complete = /(?<![a-z0-9])(?:complete(?:\s+series)?|int[ée]grale|batch)(?![a-z0-9])/i.exec(plain);
  if (complete) { if (episode === null || lastEpisode !== null) pack = true; mark(complete.index); }

  // The title ends at the first marker: an episode, a year after its first word, a tag.
  const tokens = tokenize(plain);
  let year = null;
  for (let i = 1; i < tokens.length && tokens[i].index < cut; i++) {
    const t = bare(tokens[i].text);
    if (/^(?:19|20)\d\d$/.test(t) && +t <= new Date().getUTCFullYear() + 1) { year = +t; continue; }
    if (isTag(tokens[i].text)) { mark(tokens[i].index); break; }
  }
  if (year !== null) {
    // The last plausible year before the tags wins (« Blade Runner 2049 2017 ») and ends the title.
    const years = tokens.filter((t, i) => i > 0 && t.index < cut && /^(?:19|20)\d\d$/.test(bare(t.text)));
    const chosen = years[years.length - 1];
    year = +bare(chosen.text); mark(chosen.index);
  }
  let title = tidy(plain.slice(0, cut));
  if (!title) title = tidy(plain) || String(name ?? '');

  // Picture and sound.
  const resolution = has(lower, '2160p|4k|uhd') ? 2160 : has(lower, '1080[pi]') ? 1080 : has(lower, '720p') ? 720 : has(lower, '576p') ? 576 : has(lower, '480p|360p') ? 480 : null;
  const source = has(lower, 'cam|camrip|hdcam|ts|hdts|telesync|tc|hdtc|telecine|scr|screener|dvdscr|pre-?dvd') ? 'cam'
    : has(lower, 'remux|bdremux') ? 'remux'
      : has(lower, 'blu-?ray|bdrip|brrip|bd|bdmv|bd-?rip') ? 'bluray'
        : has(lower, 'web-?rip|web rip') ? 'webrip'
          : has(lower, 'web-?dl|web dl|web|amzn|nf|dsnp|hmax|atvp|cr') ? 'webdl'
            : has(lower, 'hdrip') ? 'hdrip'
              : has(lower, 'hdtv|pdtv|tvrip|dsr|satrip') ? 'hdtv'
                : has(lower, 'dvdrip|dvd|dvd5|dvd9|dvdr') ? 'dvd' : null;
  const codec = has(lower, 'hevc|x\\.?265|h\\.?\\s?265') ? 'HEVC' : has(lower, 'av1') ? 'AV1' : has(lower, 'avc|x\\.?264|h\\.?\\s?264') ? 'H.264'
    : has(lower, 'xvid|divx') ? 'XviD' : has(lower, 'vp9') ? 'VP9' : null;
  const tenBit = has(lower, '10-?bits?|hi10p?|main10');
  const vision = has(lower, 'dv|dovi|dolby[ .]?vision'), hdr = has(lower, 'hdr10\\+|hdr10plus') ? 'HDR10+' : has(lower, 'hdr|hdr10') ? 'HDR' : null;
  const languages = [];
  if (has(lower, 'multi(?![-. ]?subs?)(?:[-. ]?audio)?|multi[-. ]?vf2?|multilang')) languages.push('MULTI');
  if (has(lower, 'truefrench|vff')) languages.push('VFF');
  if (has(lower, 'vfq')) languages.push('VFQ');
  if (!languages.some((x) => x === 'VFF' || x === 'VFQ') && has(lower, 'french|vfi|vf')) languages.push('VF');
  if (has(lower, 'vostfr|subfrench')) languages.push('VOSTFR');

  return {
    title, year, alternate, season, episode, lastEpisode, absolute, pack, resolution, source, codec, tenBit,
    hdr: vision ? (hdr ? `DV ${hdr}` : 'DV') : hdr, languages, group, repack: has(lower, 'repack|proper|rerip'),
  };
}

/** « 1080p · WEB-DL · HEVC · MULTI · ToonsHub »: what the release is, in the words of the page. */
export function qualityLine(release) {
  return [
    release.resolution === 2160 ? '4K' : release.resolution ? `${release.resolution}p` : '',
    release.source === 'cam' ? tr('Filmé en salle') : SOURCE_LABELS[release.source] ?? '', release.codec ?? '', release.hdr ?? '', ...release.languages, release.group ?? '',
  ].filter(Boolean).join(' · ');
}

/**
 * How good a result is for us, the higher the better: source, then resolution (1080p first), languages (MULTI
 * first), a few points for a repack; then what lowers it: a 10-bit H.264 or an AV1 (converted for an iPhone), a
 * Dolby Vision without HDR (wrong colours once converted), a file too small for what it claims, few seeders, none.
 */
export function releaseScore(hit, release = parseRelease(hit?.name), category = null) {
  let score = (RESOLUTION_SCORE[release.resolution] ?? 12) + (SOURCE_SCORE[release.source] ?? 5);
  score += Math.max(0, ...release.languages.map((x) => LANGUAGE_SCORE[x] ?? 0));
  if (release.repack) score += 1;
  if (release.codec === 'AV1') score -= 2;
  if (release.codec === 'XviD') score -= 4;
  if (release.tenBit && release.codec === 'H.264') score -= 4;
  if (release.hdr === 'DV') score -= 3;
  const bytes = Number(hit?.size) || 0, gib = 2 ** 30;
  const episodic = release.episode !== null || release.pack || category === 'tv' || category === 'anime';
  if (bytes > 0 && !release.pack && release.resolution >= 1080 && bytes < (episodic ? 0.25 : 1.2) * gib) score -= 4;
  const seeders = Math.max(0, Number(hit?.seeders) || 0);
  if (reportsHealth(hit?.source)) {
    if (seeders === 0) score -= 200;
    else if (seeders < 3) score -= 6;
  }
  return score;
}

/** « S06E05 », « S01E01-E03 », « Épisode 5 », « Saison 2 », « Saisons », « Épisodes 1 à 12 »; empty for a film. */
export function episodeLabel(release) {
  const two = (n) => String(n).padStart(2, '0');
  if (release.pack) {
    const { season, episode: first, lastEpisode: last } = release;
    if (first !== null && last !== null) return season !== null ? tr('Saison {season}, épisodes {first} à {last}', { season, first, last }) : tr('Épisodes {first} à {last}', { first, last });
    return season !== null ? tr('Saison {season} complète', { season }) : tr('Toutes les saisons');
  }
  if (release.episode === null) return '';
  if (release.absolute) return release.lastEpisode ? tr('Épisodes {first} à {last}', { first: release.episode, last: release.lastEpisode }) : tr('Épisode {n}', { n: release.episode });
  return `S${two(release.season)}E${two(release.episode)}${release.lastEpisode > release.episode ? `-E${two(release.lastEpisode)}` : ''}`;
}

/**
 * The results of a search gathered by title and episode (or season, or film and year), the best release of each put
 * forward: [{ key, title, label, best, others }], each entry { hit, release, score }. A release giving another name in
 * parentheses meets the releases under that name (« JoJos Bizarre Adventure S06E05 (JoJo no Kimyou na Bouken: Steel
 * Ball Run) » and « [Erai-raws] JoJo no Kimyou na Bouken: Steel Ball Run - 05 »). Titles keep the order of their
 * best-shared result; inside a title, whole seasons first, then the latest episodes.
 */
export function groupReleases(hits, category = null) {
  const entries = (hits ?? []).map((hit, order) => {
    const release = parseRelease(hit?.name);
    return { hit, release, order, score: releaseScore(hit, release, category) };
  });
  // A title known under another name: its other name points to it.
  const names = new Map();
  for (const { release } of entries) {
    const own = titleKey(release.title);
    if (release.alternate && titleKey(release.alternate) !== own) names.set(titleKey(release.alternate), own);
  }
  const showOf = (release) => names.get(titleKey(release.title)) ?? titleKey(release.title);
  const filmLike = (release) => release.episode === null && !release.pack;
  const groups = new Map();
  for (const entry of entries) {
    const { release } = entry;
    const show = showOf(release);
    const key = filmLike(release) ? `${show}|${release.year ?? ''}`
      : release.pack ? `${show}|pack|${release.season ?? ''}|${release.episode ?? ''}-${release.lastEpisode ?? ''}`
        : `${show}|e|${release.absolute ? '' : release.season}|${release.episode}`;
    if (!groups.has(key)) groups.set(key, { key, show, entries: [] });
    groups.get(key).entries.push(entry);
  }
  // An episode numbered without a season meets the same number of the same show with one, when only one season has it.
  for (const group of [...groups.values()]) {
    const [first] = group.entries;
    if (!first.release.absolute || first.release.pack) continue;
    const matches = [...groups.values()].filter((g) => g !== group && g.show === group.show && !g.entries[0].release.absolute && !g.entries[0].release.pack && g.entries[0].release.episode === first.release.episode);
    if (matches.length === 1) { matches[0].entries.push(...group.entries); groups.delete(group.key); }
  }
  const rank = (a, b) => b.score - a.score || (Number(b.hit?.seeders) || 0) - (Number(a.hit?.seeders) || 0) || a.order - b.order;
  const showOrder = new Map();
  for (const entry of entries) { const show = showOf(entry.release); if (!showOrder.has(show)) showOrder.set(show, entry.order); }
  return [...groups.values()].map((group) => {
    const sorted = group.entries.sort(rank);
    // The name of the group: the one of a release giving a season (« JoJos Bizarre Adventure »), else the best's.
    const named = sorted.find((x) => !x.release.absolute && titleKey(x.release.title) === group.show) ?? sorted[0];
    const shown = sorted.find((x) => !x.release.absolute && !x.release.pack && x.release.episode !== null) ?? sorted[0];
    const label = episodeLabel(shown.release);
    const year = filmLike(named.release) && named.release.year ? ` (${named.release.year})` : '';
    return { key: group.key, show: group.show, title: `${named.release.title}${year}`, label, best: sorted[0], others: sorted.slice(1), pack: shown.release.pack, release: shown.release };
  }).sort((a, b) => {
    if (a.show !== b.show) return showOrder.get(a.show) - showOrder.get(b.show);
    if (a.pack !== b.pack) return a.pack ? -1 : 1;
    return (b.release.season ?? 0) - (a.release.season ?? 0) || (b.release.episode ?? 0) - (a.release.episode ?? 0) || a.best.order - b.best.order;
  });
}

/** Under the best release: what makes it worth less, in a few words; empty when nothing does. */
export function warning(entry) {
  const { release, hit } = entry;
  if (release.source === 'cam') return tr('Filmé dans une salle de cinéma : image et son médiocres.');
  const seeders = Math.max(0, Number(hit?.seeders) || 0);
  if (reportsHealth(hit?.source)) {
    if (seeders === 0) return tr('Personne ne partage ce fichier : il ne se téléchargera sans doute pas.');
    if (seeders < 3) return tr('Peu de sources : le téléchargement sera lent.');
  }
  return '';
}
