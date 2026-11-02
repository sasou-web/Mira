// « Télécharger sur le PC serveur »: what Mira web checks before sending a search or a magnet link to the relay of
// the extension (Mira.Jellyfin/TorLinkRelay), as Mira.Core's RemoteDownloads checks it again there (cases shared in
// tests/fixtures/torlink-remote.json), and the lines the screen writes (views/torlink.js). Nothing of the browser at
// load: Node imports it as it is.
import { tr, trn, language } from './i18n.js';
import { airDay, dayText, today } from './calendar.js';

/** TorLink's search categories Mira offers, in its words: never « games », Jellyfin has no use for them. */
/** What a title found in a category of TorLink is, on its card (RemoteLines.KindLabel); empty when the relay does not say. */
export const kindLabel = (category) => ({ movies: tr('Film'), tv: tr('Série'), anime: tr('Anime') })[category] ?? '';
export const MAX_QUERY = 120, MAX_MAGNET = 8192, MAX_NAME = 300;
/** A .torrent sent to the relay: 4 MiB at most, as RemoteDownloads.MaxTorrent of Mira.Core. */
export const MAX_TORRENT = 4 << 20;

/**
 * What stops a file from going as a .torrent (a bencoded dictionary with an « info » key, as the relay checks it
 * again): null when it can go. Read from its bytes, never its name: a site may serve one as « download.php ».
 */
export function torrentProblem(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length === 0) return tr('Ce n’est pas un fichier .torrent.');
  if (bytes.length > MAX_TORRENT) return tr('Ce fichier .torrent est trop gros (4 Mo au plus).');
  const head = new TextDecoder('latin1').decode(bytes.subarray(0, Math.min(bytes.length, 4096)));
  return bytes[0] === 0x64 && bytes[bytes.length - 1] === 0x65 && head.includes('4:info') ? null : tr('Ce n’est pas un fichier .torrent.');
}

/** A file's bytes in base64, as the relay takes a .torrent. */
export function base64(bytes) {
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}

// .NET's char.IsControl: C0, DEL and C1.
const CONTROL = /[\u0000-\u001f\u007f-\u009f]/g;

/**
 * A search as TorLink takes it: spaces gathered, no dash in front (TorLink would read « --x » as one of its options),
 * at most MAX_QUERY characters; null when nothing is left. « -x » too, after spaces.
 */
export function cleanQuery(query) {
  if (typeof query !== 'string') return null;
  let clean = query.replace(CONTROL, ' ').replace(/\s+/g, ' ').replace(/^[ -]+/, '').trimEnd();
  if (clean.length > MAX_QUERY) clean = clean.slice(0, MAX_QUERY).trimEnd();
  return clean || null;
}

const BTIH = /[?&]xt=urn:btih:([0-9a-f]{40}|[a-z2-7]{32})(?:&|$)/i;

/**
 * The info hash of a magnet link, 40 hexadecimal digits in lower case (a base32 one converted, as TorLink does), or
 * null when it is not one.
 */
export function infoHash(magnet) {
  if (typeof magnet !== 'string') return null;
  const text = magnet.trim();
  if (text.length > MAX_MAGNET || !/^magnet:\?/i.test(text) || /[\u0000-\u001f\u007f-\u009f]/.test(text)) return null;
  const match = BTIH.exec(text);
  if (!match) return null;
  return match[1].length === 40 ? match[1].toLowerCase() : base32ToHex(match[1]);
}

function base32ToHex(value) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let hex = '', buffer = 0, bits = 0;
  for (const c of value.toUpperCase()) {
    const index = alphabet.indexOf(c);
    if (index < 0) return null;
    buffer = (buffer << 5) | index; bits += 5;
    if (bits < 8) continue;
    bits -= 8;
    hex += ((buffer >> bits) & 0xff).toString(16).padStart(2, '0');
    buffer &= (1 << bits) - 1;
  }
  return hex.length === 40 ? hex : null;
}

/** The name a magnet link carries (its « dn »), cleaned as cleanName; null without one. */
export function displayName(magnet) {
  const text = typeof magnet === 'string' ? magnet.trim() : '';
  const start = text.indexOf('?');
  if (start < 0) return null;
  for (const pair of text.slice(start + 1).split('&')) {
    if (!/^dn=/i.test(pair)) continue;
    const raw = pair.slice(3).replace(/\+/g, ' ');
    try { return cleanName(decodeURIComponent(raw)); } catch { return cleanName(raw); }
  }
  return null;
}

/** A name to show: without control characters, spaces gathered, at most MAX_NAME characters; null when empty. */
export function cleanName(name) {
  if (typeof name !== 'string') return null;
  let clean = name.replace(CONTROL, ' ').replace(/\s+/g, ' ').trim();
  if (clean.length > MAX_NAME) clean = `${clean.slice(0, MAX_NAME).trimEnd()}…`;
  return clean || null;
}

// « 4,2 » in French, « 4.2 » in English (no grouping either way: Mira.Core writes « 1500 Go »).
const decimal = (value, digits) => { const text = value.toFixed(digits); return language() === 'fr' ? text.replace('.', ',') : text; };

/** « 850 Mo », « 4,2 Go », « 12 Go », as Mira for Windows writes a size (OfflineCopies.SizeText); « 4.2 GB » in English. */
export function size(bytes) {
  const n = Number(bytes) || 0;
  if (n >= 2 ** 30) return tr('{n} Go', { n: decimal(n / 2 ** 30, n >= 10 * 2 ** 30 ? 0 : 1) });
  return tr('{n} Mo', { n: Math.max(1, Math.round(n / 2 ** 20)) });
}

/** « 2,4 Mo/s », « 350 Ko/s »; empty when nothing comes. */
export function speed(bytesPerSecond) {
  const n = Number(bytesPerSecond) || 0;
  if (n <= 0) return '';
  if (n >= 2 ** 20) return tr('{n} Mo/s', { n: decimal(n / 2 ** 20, n >= 10 * 2 ** 20 ? 0 : 1) });
  return tr('{n} Ko/s', { n: Math.max(1, Math.round(n / 2 ** 10)) });
}

const ago = (seconds) => seconds < 90 ? tr('à l’instant') : seconds < 5400 ? tr('il y a {n} min', { n: Math.round(seconds / 60) })
  : seconds < 129600 ? tr('il y a {n} h', { n: Math.round(seconds / 3600) }) : tr('il y a {n} jours', { n: Math.round(seconds / 86400) });

/**
 * Where the PC of Jellyfin stands, from the relay's agent ({ name, online, seen, accepting, problem }) at `now`
 * (milliseconds): { text, ready } — ready when it takes searches (online), even while its downloads must wait.
 */
export function agentLine(agent, now = Date.now()) {
  if (!agent) return { text: tr('Mira n’a pas encore été ouvert sur le PC serveur : c’est lui qui cherche et télécharge, avec TorLink. Ouvre-le là-bas, connecté avec un compte administrateur.'), ready: false };
  if (!agent.online) {
    const seen = Date.parse(agent.seen), name = agent.name;
    return {
      text: Number.isFinite(seen)
        ? tr('Mira n’est pas ouvert sur {name} (vu {ago}) : tes demandes l’attendent, il les fera à son retour.', { name, ago: ago(Math.max(0, (now - seen) / 1000)) })
        : tr('Mira n’est pas ouvert sur {name} : tes demandes l’attendent, il les fera à son retour.', { name }),
      ready: false,
    };
  }
  if (agent.problem) return { text: agent.problem, ready: true };
  return { text: tr('{name} est prêt : TorLink y télécharge, puis Mira range le titre dans ta bibliothèque.', { name: agent.name }), ready: true };
}

/** A search result: « 1,4 Go · 120 sources · YTS »; nothing of sources for a site that does not count them (SubsPlease). */
// What Mira for Windows on the server PC says of a title it placed, in its own language (RemoteLines.Placed of Mira.Core):
// read back in this page's when it is one of those fixed words, as it is else.
const PLACED = [
  ['meilleure version en place, l’ancienne dans la Corbeille', 'better version in place, the old one in the Recycle Bin'], ['en attente des fichiers', 'waiting for the files'],
  ['dossier de bibliothèque à choisir', 'library folder to choose'], ['rangement en cours…', 'sorting…'], ['disponible dans Jellyfin', 'available in Jellyfin'],
  ['Jellyfin l’ajoute', 'Jellyfin is adding it'], ['rangé', 'in place'], ['rangé en partie', 'partly in place'], ['déjà dans la bibliothèque', 'already in the library'],
  ['échec', 'failed'], ['ignoré', 'skipped'],
];
export function placedLine(status = '') {
  const pick = (pair) => pair[language() === 'fr' ? 0 : 1];
  const lower = status.toLowerCase();
  const word = PLACED.find((pair) => pair.some((x) => x.toLowerCase() === lower));
  if (word) return pick(word);
  for (const pair of [['pas remplacé : ', 'not replaced: ']]) {
    const prefix = pair.find((x) => lower.startsWith(x.toLowerCase()));
    if (prefix) return pick(pair) + status.slice(prefix.length);
  }
  return status;
}

export function hitLine(hit) {
  const seeders = Math.max(0, Number(hit?.seeders) || 0);
  const sources = !reportsHealth(hit?.source) && seeders === 0 ? '' : seeders === 0 ? tr('aucune source') : trn(seeders, '{n} source', '{n} sources');
  return [size(hit?.size), sources, hit?.source].filter(Boolean).join(' · ');
}

/** Whether a site of TorLink counts who shares its results: SubsPlease never does. */
export const reportsHealth = (source) => String(source ?? '').toLowerCase() !== 'subsplease';

/** A request not yet in TorLink's list: waiting for the PC, on its way, or refused (and why). */
export function requestLine(request) {
  switch (request?.state) {
    case 'waiting': return tr('En attente du PC serveur');
    case 'claimed': return tr('Envoi à TorLink…');
    case 'sent': return request.error ? tr('Envoyé à TorLink : {error}', { error: request.error }) : tr('Envoyé à TorLink');
    default: return request?.error || tr('TorLink a refusé ce téléchargement.');
  }
}

/** « moins d’une minute », « 12 min », « 1 h 05 », « 3 jours »: what is left, in seconds. */
export function remaining(seconds) {
  const n = Math.max(0, Number(seconds) || 0);
  if (n < 60) return tr('moins d’une minute');
  const minutes = Math.round(n / 60);
  if (minutes < 60) return tr('{m} min', { m: minutes });
  if (minutes < 1440) return tr('{h} h {mm}', { h: Math.floor(minutes / 60), mm: String(minutes % 60).padStart(2, '0') });
  return trn(Math.round(n / 86400), '{n} jour', '{n} jours');
}

/**
 * Seconds a download still needs: TorLink's own guess (eta), or what is left of its size at its speed (an agent of
 * 0.7.9 sends neither); null when nothing comes or the size is unknown.
 */
export function timeLeft(download) {
  if (download?.status !== 'downloading') return null;
  const eta = Number(download.eta);
  if (eta > 0) return eta;
  const total = Number(download.size) || 0, rate = Number(download.speed) || 0;
  if (total <= 0 || rate <= 0) return null;
  return Math.max(0, total - (Number(download.downloaded) || 0)) / rate;
}

/** « 1,2 Go sur 2,9 Go », or « 2,9 Go » before anything came; empty when TorLink does not know the size yet. */
export function amountLine(download) {
  const total = Number(download?.size) || 0;
  if (total <= 0) return '';
  const done = Math.min(total, Math.max(0, Number(download.downloaded) || 0));
  return done > 0 && done < total ? tr('{done} sur {total}', { done: size(done), total: size(total) }) : size(total);
}

/**
 * A download in TorLink: « 42 % · 1,2 Go sur 2,9 Go · 2,4 Mo/s · reste 12 min · 12 pairs », « En pause · 42 % »,
 * « Arrêté · 42 % ».
 */
export function downloadLine(download) {
  const progress = tr('{n} %', { n: Math.max(0, Math.min(100, Math.round(Number(download?.progress) || 0))) });
  const peers = Math.max(0, Number(download?.peers) || 0);
  switch (download?.status) {
    case 'downloading': {
      const rate = speed(download.speed), left = timeLeft(download);
      if (!rate) return [progress, amountLine(download), peers > 0 ? trn(peers, '{n} pair, en attente de données', '{n} pairs, en attente de données') : tr('recherche de pairs…')].filter(Boolean).join(' · ');
      return [progress, amountLine(download), rate, left !== null && tr('reste {time}', { time: remaining(left) }), peers > 0 ? trn(peers, '{n} pair', '{n} pairs') : ''].filter(Boolean).join(' · ');
    }
    case 'queued': return [tr('En file d’attente · {progress}', { progress }), amountLine(download)].filter(Boolean).join(' · ');
    case 'paused': return [tr('En pause · {progress}', { progress }), amountLine(download)].filter(Boolean).join(' · ');
    case 'stopped': return tr('Arrêté · {progress} · reprend quand TorLink rouvre sur le PC serveur', { progress });
    case 'failed': return [tr('Échec · {progress}', { progress }), failure(download.error)].filter(Boolean).join(' · ');
    case 'completed': case 'seeding': return tr('Terminé · rangement dans ta bibliothèque…');
    default: return progress;
  }
}

/**
 * Why TorLink gave up (RemoteLines.Failure): a full disk or a folder it cannot write to in words, anything else in
 * TorLink's own; empty when it did not say (an agent of 0.8.2 or before).
 */
export function failure(error) {
  const text = typeof error === 'string' ? error.trim() : '';
  if (!text) return '';
  if (/ENOSPC|no space/i.test(text)) return tr('plus de place sur le disque du PC serveur');
  if (/EACCES|EPERM/.test(text)) return tr('TorLink ne peut pas écrire dans son dossier');
  return text;
}

/** Downloads that can be paused, resumed or cancelled from here: under way, waiting, paused or stopped. */
export const controllable = (download) => ['downloading', 'queued', 'paused', 'stopped', 'failed'].includes(download?.status);

// « Suivre une série »: a series followed, its releases chosen and its episodes taken (RemoteLines.FollowNote,
// Follow, Pick, Taken; cases shared in tests/fixtures/torlink-lines.json).

/** What the last check of a series followed found; empty before any. */
export function followNote(note) {
  const episode = note?.episode;
  switch (note?.kind) {
    case 'found': return tr('Trouvé : {episode}', { episode });
    case 'held': switch (note.reason) {
      case 'pack': return tr('Seulement en pack pour l’instant ({episode}) : Mira attend l’épisode seul.', { episode });
      case 'language': return tr('{episode} trouvé, mais pas dans la langue des précédents.', { episode });
      case 'health': return tr('{episode} trouvé, mais trop peu de sources pour l’instant.', { episode });
      case 'size': return tr('{episode} trouvé, mais bien plus gros qu’un épisode.', { episode });
      default: return tr('{episode} trouvé, mais pas pris.', { episode });
    }
    case 'nothing': return episode ? tr('Rien de nouveau après {episode}.', { episode }) : tr('Rien de nouveau.');
    case 'unknown': return tr('Aucun résultat sous ce nom : essaie un autre nom cherché.');
    case 'missing': return tr('Cette série n’est plus dans Jellyfin.');
    case 'failed': return note.detail ? tr('Recherche impossible : {why}', { why: note.detail }) : tr('Recherche impossible pour l’instant.');
    default: return '';
  }
}

// « moins d’une minute », « 8 min », « 3 h »: how long until something, never cut between the number and its unit.
const within = (seconds) => seconds < 60 ? tr('moins d’une minute')
  : (seconds < 3570 ? tr('{m} min', { m: Math.round(seconds / 60) }) : tr('{h} h', { h: Math.round(seconds / 3600) })).replace(' ', '\u00a0');

/** « Dernier épisode : S01E12 · S01E13 sort samedi · prochaine recherche dans 3 h », or « Recherche en cours sur le PC serveur… ». */
export function followLine(follow, now = Date.now()) {
  if (follow?.checking) return tr('Recherche en cours sur le PC serveur…');
  const next = Date.parse(follow?.next);
  return [
    follow?.latest ? tr('Dernier épisode : {episode}', { episode: follow.latest }) : '',
    airsText(follow?.airs, now),
    Number.isFinite(next) && next > now ? tr('prochaine recherche dans {time}', { time: within((next - now) / 1000) }) : tr('prochaine recherche bientôt'),
  ].filter(Boolean).join(' · ');
}

/** « S01E13 sort samedi », « S01E13 sorti hier »: the next episode TheTVDB dates (calendar.js); empty without one. */
export function airsText(airs, now = Date.now()) {
  const day = airDay({ PremiereDate: airs?.day });
  if (!airs?.episode || !day) return '';
  const day0 = today(new Date(now));
  const when = dayText(day, day0);
  return day < day0 ? tr('{episode} sorti {day}', { episode: airs.episode, day: when }) : tr('{episode} sort {day}', { episode: airs.episode, day: when });
}

/** A release chosen, not yet in TorLink: « S01E13 · 1,4 Go · part dans 8 min », on its way, or refused. */
export function pickLine(pick, now = Date.now()) {
  switch (pick?.state) {
    case 'waiting': {
      const at = Date.parse(pick.sendAt);
      return [pick.episode, size(pick.size), at > now ? tr('part dans {time}', { time: within((at - now) / 1000) }) : tr('part maintenant')].filter(Boolean).join(' · ');
    }
    case 'claimed': return [pick.episode, tr('envoi à TorLink…')].filter(Boolean).join(' · ');
    default: return [pick?.episode, pick?.error || tr('TorLink a refusé ce téléchargement.')].filter(Boolean).join(' · ');
  }
}

/** An episode taken: « S01E13 · pris il y a 2 h ». */
export const takenLine = (taken, now = Date.now()) =>
  tr('{episode} · pris {ago}', { episode: taken?.episode, ago: ago(Math.max(0, (now - Date.parse(taken?.at)) / 1000) || 0) });
