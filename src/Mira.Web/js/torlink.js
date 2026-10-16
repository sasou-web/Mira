// « Télécharger sur le PC serveur »: what Mira web checks before sending a search or a magnet link to the relay of
// the extension (Mira.Jellyfin/TorLinkRelay), as Mira.Core's RemoteDownloads checks it again there (cases shared in
// tests/fixtures/torlink-remote.json), and the lines the screen writes (views/torlink.js). Nothing of the browser at
// load: Node imports it as it is.

/** TorLink's search categories Mira offers, in its words: never « games », Jellyfin has no use for them. */
export const CATEGORIES = [['movies', 'Films'], ['tv', 'Séries'], ['anime', 'Animes']];
export const MAX_QUERY = 120, MAX_MAGNET = 8192, MAX_NAME = 300;

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

const french = (value, digits) => value.toFixed(digits).replace('.', ',');

/** « 850 Mo », « 4,2 Go », « 12 Go », as Mira for Windows writes a size (OfflineCopies.SizeText). */
export function size(bytes) {
  const n = Number(bytes) || 0;
  if (n >= 2 ** 30) return `${french(n / 2 ** 30, n >= 10 * 2 ** 30 ? 0 : 1)} Go`;
  return `${Math.max(1, Math.round(n / 2 ** 20))} Mo`;
}

/** « 2,4 Mo/s », « 350 Ko/s »; empty when nothing comes. */
export function speed(bytesPerSecond) {
  const n = Number(bytesPerSecond) || 0;
  if (n <= 0) return '';
  if (n >= 2 ** 20) return `${french(n / 2 ** 20, n >= 10 * 2 ** 20 ? 0 : 1)} Mo/s`;
  return `${Math.max(1, Math.round(n / 2 ** 10))} Ko/s`;
}

const ago = (seconds) => seconds < 90 ? 'à l’instant' : seconds < 5400 ? `il y a ${Math.round(seconds / 60)} min`
  : seconds < 129600 ? `il y a ${Math.round(seconds / 3600)} h` : `il y a ${Math.round(seconds / 86400)} jours`;

/**
 * Where the PC of Jellyfin stands, from the relay's agent ({ name, online, seen, accepting, problem }) at `now`
 * (milliseconds): { text, ready } — ready when it takes searches (online), even while its downloads must wait.
 */
export function agentLine(agent, now = Date.now()) {
  if (!agent) return { text: 'Mira n’a pas encore été ouvert sur le PC serveur : c’est lui qui cherche et télécharge, avec TorLink. Ouvre-le là-bas, connecté avec un compte administrateur.', ready: false };
  if (!agent.online) {
    const seen = Date.parse(agent.seen);
    return { text: `Mira n’est pas ouvert sur ${agent.name}${Number.isFinite(seen) ? ` (vu ${ago(Math.max(0, (now - seen) / 1000))})` : ''} : tes demandes l’attendent, il les fera à son retour.`, ready: false };
  }
  if (agent.problem) return { text: agent.problem, ready: true };
  return { text: `${agent.name} est prêt : TorLink y télécharge, puis Mira range le titre dans ta bibliothèque.`, ready: true };
}

/** A search result: « 1,4 Go · 120 sources · YTS »; nothing of sources for a site that does not count them (SubsPlease). */
export function hitLine(hit) {
  const seeders = Math.max(0, Number(hit?.seeders) || 0);
  const sources = !reportsHealth(hit?.source) && seeders === 0 ? '' : seeders === 0 ? 'aucune source' : seeders === 1 ? '1 source' : `${seeders} sources`;
  return [size(hit?.size), sources, hit?.source].filter(Boolean).join(' · ');
}

/** Whether a site of TorLink counts who shares its results: SubsPlease never does. */
export const reportsHealth = (source) => String(source ?? '').toLowerCase() !== 'subsplease';

/** A request not yet in TorLink's list: waiting for the PC, on its way, or refused (and why). */
export function requestLine(request) {
  switch (request?.state) {
    case 'waiting': return 'En attente du PC serveur';
    case 'claimed': return 'Envoi à TorLink…';
    case 'sent': return request.error ? `Envoyé à TorLink : ${request.error}` : 'Envoyé à TorLink';
    default: return request?.error || 'TorLink a refusé ce téléchargement.';
  }
}

/** « moins d’une minute », « 12 min », « 1 h 05 », « 3 jours »: what is left, in seconds. */
export function remaining(seconds) {
  const n = Math.max(0, Number(seconds) || 0);
  if (n < 60) return 'moins d’une minute';
  const minutes = Math.round(n / 60);
  if (minutes < 60) return `${minutes} min`;
  if (minutes < 1440) return `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')}`;
  const days = Math.round(n / 86400);
  return days === 1 ? '1 jour' : `${days} jours`;
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
  return done > 0 && done < total ? `${size(done)} sur ${size(total)}` : size(total);
}

/**
 * A download in TorLink: « 42 % · 1,2 Go sur 2,9 Go · 2,4 Mo/s · reste 12 min · 12 pairs », « En pause · 42 % »,
 * « Arrêté · 42 % ».
 */
export function downloadLine(download) {
  const progress = `${Math.max(0, Math.min(100, Math.round(Number(download?.progress) || 0)))} %`;
  const peers = Math.max(0, Number(download?.peers) || 0);
  switch (download?.status) {
    case 'downloading': {
      const rate = speed(download.speed), left = timeLeft(download);
      if (!rate) return [progress, amountLine(download), peers === 1 ? '1 pair, en attente de données' : peers > 1 ? `${peers} pairs, en attente de données` : 'recherche de pairs…'].filter(Boolean).join(' · ');
      return [progress, amountLine(download), rate, left !== null && `reste ${remaining(left)}`, peers === 1 ? '1 pair' : peers > 1 ? `${peers} pairs` : ''].filter(Boolean).join(' · ');
    }
    case 'queued': return [`En file d’attente · ${progress}`, amountLine(download)].filter(Boolean).join(' · ');
    case 'paused': return [`En pause · ${progress}`, amountLine(download)].filter(Boolean).join(' · ');
    case 'stopped': return `Arrêté · ${progress} · reprend quand TorLink rouvre sur le PC serveur`;
    case 'failed': return `Échec · ${progress}`;
    case 'completed': case 'seeding': return 'Terminé · rangement dans ta bibliothèque…';
    default: return progress;
  }
}

/** Downloads that can be paused, resumed or cancelled from here: under way, waiting, paused or stopped. */
export const controllable = (download) => ['downloading', 'queued', 'paused', 'stopped', 'failed'].includes(download?.status);
