// Jellyfin's API, from the same address as Mira web: no server to type, no cross-site request.
import { session, device, settings } from './session.js';
import { SUBTITLE_FORMATS } from './subtitles.js';
import { FIELDS as CHECK_FIELDS, imagePath } from './identify.js';
import { tr, language } from './i18n.js';

export const VERSION = document.querySelector('meta[name="mira-version"]')?.content.replace('{{version}}', '0.0.0') || '0.0.0';

/** This server's root, with its base URL if it has one: « » or « /jellyfin ». */
const root = location.pathname.replace(/\/Mira\/?.*$/i, '');
/**
 * Jellyfin's address for this page: the server's root, or for a guest of « Inviter par lien » the extension's gateway
 * (/Mira/guest), the only part of Jellyfin a link opened from the Internet reaches.
 */
export const base = session.current?.guest ? `${root}/Mira/guest` : root;
/** A guest of « Inviter par lien »: no account of their own, only the host's group (views/invite.js). */
export const isGuest = () => !!session.current?.guest;

/** A Tailscale address (100.64.0.0/10): Mira opened through Tailscale. */
export const isTailnet = (host) => /^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$/.test(host);
const isHomeNetwork = (host) => /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host);

/**
 * An address that never goes through the Internet: this device, a home network (private and link-local ranges, in
 * IPv4 and IPv6), Tailscale (its addresses and its names), or a name only a home network knows (« nas », « pc.local »,
 * « livebox.home », « pc.lan », « nas.fritz.box »).
 */
export function isLocalHost(hostname) {
  const host = String(hostname).replace(/^\[|\]$/g, '').replace(/\.$/, '').toLowerCase();
  // IPv6: loopback, unique local (fc00::/7, Tailscale's among them), link-local (fe80::/10).
  if (host.includes(':')) return /^(::1$|f[cd][0-9a-f]{2}:|fe[89ab][0-9a-f]:)/.test(host);
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) return /^(127|10)\./.test(host) || /^(169\.254|192\.168)\./.test(host) || isHomeNetwork(host) || isTailnet(host);
  // Names only a home network or Tailscale knows: reserved (localhost, local, home.arpa, internal), given by home
  // routers (localdomain, home, lan, fritz.box), or Tailscale's (ts.net).
  return !host.includes('.') || /(^|\.)(localhost|local|localdomain|lan|home|internal|home\.arpa|fritz\.box|ts\.net)$/.test(host);
}
/**
 * Tailscale's own addresses and names: reached from anywhere (the QR code gives this one), so not a sign of home, where
 * Mira would ask for full quality without measuring the connection (see player.js).
 */
export function viaTailscale(hostname) {
  const host = String(hostname).replace(/^\[|\]$/g, '').replace(/\.$/, '').toLowerCase();
  return isTailnet(host) || /^fd7a:115c:a1e0:/.test(host) || /\.ts\.net$/.test(host);
}
/** Mira opened over plain HTTP from the Internet: what it sends, the password first, can be read on the way. */
export const exposed = () => location.protocol === 'http:' && !isLocalHost(location.hostname);
/**
 * Why nothing answers, by the address Mira was opened at: through Tailscale, it is off on the phone or on the PC; at
 * the home network's, the phone may have left it; otherwise the PC is off or starting. In full for a screen, short for
 * the bar that says so while Mira asks again.
 */
const where = isTailnet(location.hostname) ? 'tailnet' : isHomeNetwork(location.hostname) ? 'home' : '';
export const UNREACHABLE = {
  tailnet: tr('Serveur injoignable : vérifie que Tailscale est activé sur ce téléphone et que le PC est allumé. Mira réessaie toute seule.'),
  home: tr('Serveur injoignable : le PC est éteint, ou ce téléphone n’est pas sur le Wi-Fi de la maison. Mira réessaie toute seule.'),
}[where] ?? tr('Serveur injoignable : le PC est peut-être éteint ou en train de démarrer. Mira réessaie toute seule.');
export const UNREACHABLE_SHORT = { tailnet: tr('Serveur injoignable. Tailscale est-il activé ?'), home: tr('Serveur injoignable. Hors du Wi-Fi de la maison ?') }[where]
  ?? tr('Serveur injoignable. Mira réessaie toute seule.');
/** What to check when another server answers at this address (see checkServer). */
export const FOREIGN = {
  tailnet: tr('Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie que Tailscale est activé sur ce téléphone, avec ton compte.'),
  home: tr('Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie que ce téléphone est bien sur le Wi-Fi de ta maison.'),
}[where] ?? tr('Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie l’adresse de Mira.');

export class ApiError extends Error {
  constructor(status, message, { foreign = false } = {}) { super(message); this.status = status; this.foreign = foreign; }
  get offline() { return this.status === 0; }
  /** Nothing answers (PC off, no network) or Jellyfin is still starting: Mira waits for it. */
  get unreachable() { return this.status === 0 || this.status === 503; }
}

const listeners = new Set();
/** Called when Jellyfin refuses the session (signed out elsewhere, password changed). */
export const onUnauthorized = (fn) => listeners.add(fn);
const strangers = new Set();
/**
 * Called with (true) when the server answering here is not the one the session was opened on (see checkServer), and
 * with (false) once it is again. Returns what stops the calls.
 */
export const onForeignServer = (fn) => { strangers.add(fn); return () => strangers.delete(fn); };

const STARTING = tr('Jellyfin ne répond pas encore : il est sans doute en train de démarrer. Mira réessaie toute seule.');
// A page where Jellyfin's answer should be: a Wi-Fi's sign-in page (hotel, train), or a proxy's error page.
const NOT_JELLYFIN = tr('Une page web a répondu à la place de Jellyfin : ce Wi-Fi demande peut-être d’accepter ses conditions. Accepte-les dans le navigateur, ou change de réseau, puis réessaie.');
const watchers = new Set();
let reachable = true;
/**
 * Called with (false, 'offline' | 'starting') when Jellyfin stops answering, and with (true) once it answers again.
 * 'starting': Jellyfin answers 503 while it starts, or while it turns away a network it does not count as local.
 */
export const onReachable = (fn) => watchers.add(fn);
function setReachable(value, reason = '') {
  // Once the server went silent, the one that answers next may be another (another network): checked again.
  if (!value) recheckServer();
  if (value === reachable) return;
  reachable = value;
  watchers.forEach((fn) => fn(value, reason));
}
export const isReachable = () => reachable;

// ---------- Is this your server? ----------
// Over plain HTTP, any server can answer at your server's address (another Wi-Fi with the same addresses, a network
// that is not yours): the session's token, often an administrator's, goes only to the server that gave it. Before the
// first request that carries it, Mira reads the Id of the server that answers, public and asked without the token,
// and compares it with the one the session was opened on; again after a silence or a stay in the background, when the
// phone may have changed networks, and a minute after the last check (it may change networks with Mira on screen,
// and nothing tells the page).
let identity = null;   // { expected, promise, at }: the check of the server answering here; at: when it said yours
let foreign = false;   // the last check concluded: another server
const FRESH = 60_000;
// 421, « Misdirected Request »: the request would go to a server that is not the one it is meant for.
const notYours = () => new ApiError(421, tr('Ce n’est pas ton serveur. {why}', { why: FOREIGN }), { foreign: true });
const sameId = (a, b) => String(a ?? '').replace(/-/g, '').toLowerCase() === String(b ?? '').replace(/-/g, '').toLowerCase();

/** The next request that carries the token checks the server first. */
export function recheckServer() { identity = null; }
// Back from the background or from a loss of network, the phone may be on another network: checked at once, so that
// the answer is there before the next request, a report sent as Mira goes away again (it would wait for it) or a tap
// on Lecture (another server: the player closes, see player.js).
const recheckNow = () => { recheckServer(); trustServer().catch(() => {}); };
document.addEventListener('visibilitychange', () => { if (!document.hidden) recheckNow(); });
addEventListener('online', recheckNow);

/**
 * Resolves with the Id of the server answering here once it is `expected` (any Jellyfin when nothing is expected);
 * rejects otherwise: unreachable or starting (nothing is concluded, Mira waits for it as for any request), or foreign.
 * `aged`: a server found yours more than a minute ago still counts (see request).
 */
export function checkServer(expected = '', { aged = false } = {}) {
  if (identity && identity.expected === expected && (aged || !identity.at || Date.now() - identity.at < FRESH)) return identity.promise;
  const check = { expected, at: 0 };
  check.promise = api.publicInfo().then((info) => {
    if (expected && !sameId(info.Id, expected)) throw notYours();
    return info.Id;
  }, (error) => {
    if (error?.unreachable || error?.status >= 500) throw error;
    // Anything else answers here (a page, a refusal): not a Jellyfin server, so not yours either.
    throw notYours();
  });
  identity = check;
  check.promise.then(() => {
    check.at = Date.now();
    if (foreign && identity === check) { foreign = false; strangers.forEach((fn) => fn(false)); }
  }, (error) => {
    if (identity !== check) return;
    if (error.foreign) { foreign = true; strangers.forEach((fn) => fn(true)); }
    else identity = null;   // asked again by the next request
  });
  return check.promise;
}

/**
 * Waits until the server answering here is the session's; throws, without sending anything, when it is not. `aged`:
 * see checkServer.
 */
export async function trustServer({ aged = false } = {}) {
  const current = session.current;
  if (!current) return;
  const id = await checkServer(current.serverId ?? '', { aged });
  // A session opened before Mira kept the server's Id: the first Jellyfin it meets is taken as its own, from now on.
  if (!current.serverId && id && session.current === current) {
    session.set({ ...current, serverId: id });
    if (identity?.expected === '') identity.expected = id;
  }
}

export function authorization(token = session.current?.token) {
  const quote = (value) => String(value).replace(/["\\]/g, '');
  return `MediaBrowser Client="Mira", Device="${quote(device.name)}", DeviceId="${quote(device.id)}", Version="${quote(VERSION)}"` +
    (token ? `, Token="${quote(token)}"` : '');
}

export function query(params) {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params ?? {})) {
    if (value == null || value === '' || (Array.isArray(value) && value.length === 0)) continue;
    if (Array.isArray(value)) value.forEach((v) => search.append(key, v));
    else search.set(key, String(value));
  }
  const text = search.toString();
  return text ? `?${text}` : '';
}

export const url = (path, params) => `${base}/${path}${query(params)}`;

/**
 * A request to Jellyfin, with a JSON body or plain text (plain); errors come back in French, ready to show. With the
 * session's token unless `token` says otherwise (null: none), and only once the server is known to be the session's
 * (trustServer).
 */
export async function request(method, path, { params, body, plain, signal, timeout = 20000, keepalive = false, token = session.current?.token } = {}) {
  if (token && token === session.current?.token) {
    // A report sent as Mira may go away (keepalive) does not wait for a new check: a server found yours a while ago
    // still is, unless the network may have changed since (a silence, a return: recheckServer).
    const refused = await trustServer({ aged: keepalive }).then(() => null, (error) => error);
    // Cancelled meanwhile (a search typed further): cancelled, whatever the check found.
    if (signal?.aborted) throw signal.reason ?? new DOMException('Aborted', 'AbortError');
    if (refused) throw refused;
  }
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new DOMException('timeout', 'TimeoutError')), timeout);
  signal?.addEventListener('abort', () => controller.abort(signal.reason), { once: true });
  const address = url(path, params);
  let response;
  try {
    response = await fetch(address, {
      method,
      keepalive,
      signal: controller.signal,
      headers: {
        Authorization: authorization(token), Accept: 'application/json',
        // Mira's own routes answer their errors in Mira's language.
        ...(path.startsWith('Mira/') ? { 'Accept-Language': language() } : {}),
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : plain !== undefined ? { 'Content-Type': 'text/plain' } : {}),
      },
      body: body !== undefined ? JSON.stringify(body) : plain,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    setReachable(false, 'offline');
    throw new ApiError(0, controller.signal.aborted
      ? tr('Le serveur met trop de temps à répondre. Mira réessaie dès qu’il répond.')
      : UNREACHABLE);
  } finally { clearTimeout(timer); }

  if (response.status === 503) {
    setReachable(false, 'starting');
    throw new ApiError(503, STARTING);
  }
  // Back up is for ping() to say: while Jellyfin starts, its startup page answers some routes too.
  if (response.status === 401) {
    // Only the session in use ends: a sign-out still on its way with an older token leaves a new sign-in alone.
    if (token && session.current && token === session.current.token) { session.clear(); listeners.forEach((fn) => fn()); }
    throw new ApiError(401, tr('Ta session a pris fin. Reconnecte-toi.'));
  }
  if (!response.ok) {
    // Mira's own routes (the extension's) say why, in the language asked for (Accept-Language): { error }.
    const said = path.startsWith('Mira/') ? await response.json().then((x) => (typeof x?.error === 'string' && x.error ? x.error : null), () => null) : null;
    if (said) throw new ApiError(response.status, said);
    throw new ApiError(response.status, response.status === 403
      ? tr('Ton compte Jellyfin n’a pas accès à ceci.')
      : response.status === 404 ? tr('Ce titre n’existe plus sur le serveur.') : tr('Jellyfin a répondu avec une erreur ({status}).', { status: response.status }));
  }
  let text;
  try { text = await response.text(); } catch (error) {
    // Cut off while it answered: as for no answer at all.
    if (signal?.aborted) throw error;
    setReachable(false, 'offline');
    throw new ApiError(0, UNREACHABLE);
  }
  if (!text) return null;
  // Not Jellyfin's JSON: said in Mira's words, never the parser's. Its status stays the page's (a 200 from
  // System/Info/Public is then « not your server », see checkServer).
  try { return JSON.parse(text); } catch { throw new ApiError(response.status, NOT_JELLYFIN); }
}

const get = (path, params, options) => request('GET', path, { params, ...options });
const post = (path, body, params, options) => request('POST', path, { body, params, ...options });
const user = () => session.current?.userId;

// « Sous-titres gardés sur Jellyfin »: the providers asked once per session (api.subtitleProviders).
let providers = null;   // { token, list }
// « Identifier… » and « Titres à corriger »: whether this account administers Jellyfin (api.isAdministrator).
let administrator = null;   // { token, answer }
/** The bytes of a file, as the base64 Jellyfin's subtitle upload takes. */
function base64(buffer) {
  const bytes = new Uint8Array(buffer);
  let text = '';
  for (let i = 0; i < bytes.length; i += 0x8000) text += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(text);
}
/**
 * The library holding this title keeps the subtitles Jellyfin is given in its own data (SaveSubtitlesWithMedia off),
 * its other options sent back as they were. False when it already does, or the account may not change it.
 */
async function keepSubtitlesInJellyfinData(itemId) {
  const ancestors = await get(`Items/${itemId}/Ancestors`, { userId: user() });
  const refused = (error) => { if (error instanceof ApiError && error.status === 403) return null; throw error; };
  const folders = await get('Library/VirtualFolders').catch(refused);
  const library = (folders ?? []).find((folder) => (ancestors ?? []).some((a) => a.Id === folder.ItemId));
  if (!library?.LibraryOptions?.SaveSubtitlesWithMedia) return false;
  const saved = await post('Library/VirtualFolders/LibraryOptions', { Id: library.ItemId, LibraryOptions: { ...library.LibraryOptions, SaveSubtitlesWithMedia: false } })
    .then(() => true, refused);
  return !!saved;
}

// Fields the cards need: child counts for series (RecursiveItemCount: Jellyfin 10.9 then says how much of a series
// was watched, for the episodes left), and genres for the banner (Home). A grid asks for less still: GRID_FIELDS,
// without the genres. No summary: a title page asks for its own.
const CARD_FIELDS = 'Genres,ChildCount,RecursiveItemCount';
export const GRID_FIELDS = 'ChildCount,RecursiveItemCount';
const CARD_IMAGES = { enableImageTypes: 'Primary,Backdrop,Thumb,Logo', imageTypeLimit: 1 };

/**
 * True once Jellyfin itself answers: while it starts, a page about it answers in its place, but not to Ping; nor does
 * a Wi-Fi's sign-in page, which answers anything with itself (Jellyfin's answer is its name: "Jellyfin Server").
 */
export async function ping() {
  try {
    const response = await fetch(url('System/Ping'), { cache: 'no-store', signal: AbortSignal.timeout?.(6000) });
    const up = response.ok && /^\s*"?Jellyfin\b/i.test(await response.text());
    if (up) setReachable(true);
    return up;
  } catch { return false; }
}

export const api = {
  /** What any server says of itself, asked without the token: it may not be yours yet (checkServer). */
  async publicInfo() {
    const info = await get('System/Info/Public', null, { timeout: 8000, token: null });
    // While Jellyfin starts, its startup page answers here too (in camelCase): that is not the server yet.
    if (!info?.Id) { setReachable(false, 'starting'); throw new ApiError(503, STARTING); }
    return info;
  },
  publicUsers: () => get('Users/Public', null, { token: null }),

  /**
   * « Inviter par lien »: the link's code (#/invite/<code>) opened for this device, which it serves from now on. A
   * session of the guest's account, kept as any other with what says it is one: the gateway is then this page's
   * Jellyfin (base). Refused ({ error }) for a link taken back or already used on another device.
   */
  async openInvite(code) {
    let response;
    try {
      response = await fetch(`${root}/Mira/guest/open`, {
        method: 'POST', cache: 'no-store',
        headers: { 'Content-Type': 'application/json', Accept: 'application/json', 'Accept-Language': language() },
        body: JSON.stringify({ code, device: device.id, name: device.name }),
      });
    } catch { throw new ApiError(0, UNREACHABLE); }
    const answer = await response.json().catch(() => null);
    if (!response.ok || !answer?.token) {
      throw new ApiError(response.status, typeof answer?.error === 'string' && answer.error ? answer.error : tr('Jellyfin a répondu avec une erreur ({status}).', { status: response.status }));
    }
    recheckServer();
    session.set({ guest: true, userId: answer.userId, userName: answer.userName, token: answer.token, serverId: answer.serverId, serverName: '', host: answer.host });
    settings.set('seenVersion', VERSION);
    return answer;
  },
  // « Inviter par lien », the host's side (an administrator): the links made, one more, one taken back, and the
  // address on the Internet they start with (Tailscale Funnel's).
  /** { address, invites: [{ id, name, created, used, ends, opened, device, link }] }. */
  invites: () => get('Mira/invites', null, { timeout: 15000 }),
  /** A new link for a guest named `name` (« Invité » when empty): its line of invites(). */
  invite: (name) => post('Mira/invites', { name }, null, { timeout: 20000 }),
  revokeInvite: (id) => request('DELETE', `Mira/invites/${encodeURIComponent(id)}`, { timeout: 20000 }),
  setInviteAddress: (address) => request('PUT', 'Mira/invites/address', { body: { address } }),

  async signIn(username, password) {
    return this.opened(await post('Users/AuthenticateByName', { Username: username, Pw: password }, null, { token: null }));
  },
  /** The session Jellyfin just gave, by a password or by a code: kept, with the server that gave it. */
  async opened(result) {
    const info = await this.publicInfo().catch(() => ({}));
    // A new session: the server that gave it is checked anew, whatever was concluded of the one before.
    recheckServer();
    session.set({ userId: result.User.Id, userName: result.User.Name, token: result.AccessToken, serverId: result.ServerId, serverName: info.ServerName ?? '' });
    // Just signed in: this version is no news here, « Nouveau dans Mira » waits for the next one (whats-new.js).
    settings.set('seenVersion', VERSION);
    // Lets Jellyfin's dashboard show what this phone can play.
    api.capabilities();
    return result;
  },
  /**
   * What this phone takes from elsewhere: video, and Stop or Pause while it plays (remote.js listens then). Told again
   * as a title opens: Jellyfin forgets it when it restarts.
   */
  capabilities: () => post('Sessions/Capabilities', undefined, { playableMediaTypes: 'Video', supportsMediaControl: true }).catch(() => {}),
  /**
   * The address of Jellyfin's WebSocket for this session (its token in it, as Jellyfin 12.1 takes it: ApiKey), once the
   * server is known to be the session's; null without a session.
   */
  async socketAddress() {
    await trustServer();
    const current = session.current;
    if (!current?.token) return null;
    const address = new URL(url('socket', { ApiKey: current.token, deviceId: device.id }), location.href);
    address.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    return address.href;
  },

  // Quick Connect, Jellyfin's sign-in by a code: this device shows six digits, a device already signed in approves
  // them (Mira on a PC, a Mac, another phone, or Jellyfin's own pages), and no password is typed here.
  /** Off when Jellyfin's dashboard turned it off (Général → Quick Connect), or when the answer is not a plain yes. */
  quickConnectEnabled: () => get('QuickConnect/Enabled', null, { timeout: 8000, token: null }).then((on) => on === true, () => false),
  /** A code for this device: { Code, Secret }. The secret stays here; it is what asks whether the code was approved. */
  quickConnectStart: () => post('QuickConnect/Initiate', undefined, null, { token: null }),
  /** { Authenticated } once approved; 404 once Jellyfin forgot the code (it keeps one a few minutes). */
  quickConnectState: (secret, signal) => get('QuickConnect/Connect', { secret }, { token: null, signal, timeout: 8000 }),
  async signInWithCode(secret) {
    return this.opened(await post('Users/AuthenticateWithQuickConnect', { Secret: secret }, null, { token: null }));
  },
  /**
   * Approves another device's code for this account: true when it was; 404 for a code Jellyfin does not know. Asked
   * first whether Quick Connect is on: turned off, Jellyfin answers 401, which would end this session (request).
   */
  async approveCode(code) {
    if (!await this.quickConnectEnabled()) throw new ApiError(403, tr('Quick Connect est désactivé sur ce serveur : active-le dans le tableau de bord de Jellyfin (Général).'));
    return post('QuickConnect/Authorize', undefined, { code, userId: user() });
  },
  /**
   * Signed out here at once; Jellyfin is told with the token that was used, so a new sign-in is never undone, and
   * only if it is the server that gave it: compared here, not by checkServer, whose « not your server » would cover
   * the sign-in screen, with no session left to protect.
   */
  signOut() {
    const { token, serverId = '' } = session.current ?? {};
    session.clear();
    if (!token) return Promise.resolve();
    return this.publicInfo().then((info) => {
      if (!serverId || sameId(info.Id, serverId)) return post('Sessions/Logout', undefined, null, { timeout: 5000, token, keepalive: true });
    }).catch(() => {});
  },
  me: () => get('Users/Me'),
  /** Whether this account administers Jellyfin, asked once per session (« Identifier… », « Titres à corriger »). */
  isAdministrator() {
    const token = session.current?.token;
    if (administrator?.token !== token) {
      administrator = { token, answer: get('Users/Me').then((me) => !!me?.Policy?.IsAdministrator, () => { administrator = null; return false; }) };
    }
    return administrator.answer;
  },
  // Titles recognized badly (identify.js), for an administrator: what Jellyfin's metadata sources propose, searched by
  // Jellyfin (it speaks to TMDb, the phone never does), the one chosen given to the title, a refresh.
  /** Every film and series, with what « Titres à corriger » judges. */
  titlesToCheck: () => get('Items', {
    userId: user(), recursive: true, includeItemTypes: 'Movie,Series', fields: CHECK_FIELDS, enableImageTypes: 'Primary', imageTypeLimit: 1, enableUserData: false, sortBy: 'SortName',
  }, { timeout: 30000 }).then((r) => r?.Items ?? []),
  /** Jellyfin's RemoteSearch for a film or series: its proposals, as it gives them (sent back as they are by applyMatch). */
  findMatches: (item, query) => post(`Items/RemoteSearch/${item.Type === 'Series' ? 'Series' : 'Movie'}`, {
    SearchInfo: { Name: query.name, ProviderIds: query.providerIds, ...(query.year ? { Year: query.year } : {}) }, ItemId: item.Id, IncludeDisabledProviders: true,
  }, null, { timeout: 30000 }).then((r) => (Array.isArray(r) ? r.filter((x) => x?.Name) : [])),
  /**
   * Gives the title the proposal chosen: Jellyfin replaces its metadata and pictures, and reads it again from its
   * source before it answers (a long series takes a while).
   */
  applyMatch: (itemId, match) => post(`Items/RemoteSearch/Apply/${encodeURIComponent(itemId)}`, match, { replaceAllImages: true }, { timeout: 120000 }),
  /** « Actualiser les métadonnées »: read again from its sources, what is missing filled (queued, answered at once). */
  refreshMetadata: (itemId) => post(`Items/${encodeURIComponent(itemId)}/Refresh`, undefined, {
    metadataRefreshMode: 'FullRefresh', imageRefreshMode: 'FullRefresh', replaceAllMetadata: false, replaceAllImages: false,
  }),
  /** A proposal's poster, fetched by the extension: an object URL to revoke, null without one. */
  async matchImage(address, signal) {
    await trustServer();
    try {
      const response = await fetch(url(imagePath(address)), { headers: { Authorization: authorization() }, signal });
      if (!response.ok || !/^image\//.test(response.headers.get('content-type') ?? '')) return null;
      return URL.createObjectURL(await response.blob());
    } catch { return null; }
  },
  /** « En ce moment sur Jellyfin »: the sessions Jellyfin heard from lately, as Mira for Windows reads them (all of them for an administrator). */
  sessions: () => get('Sessions', { activeWithinSeconds: 960 }),
  /** Asks a session to stop playing: one whose WebSocket takes commands (Mira web while it plays, remote.js). */
  stopSession: (id) => post(`Sessions/${encodeURIComponent(id)}/Playing/Stop`),

  // « Télécharger sur le PC serveur » (an administrator): the relay of the extension, which Mira for Windows on the
  // PC of Jellyfin visits every few seconds (views/torlink.js).
  /** { agent, requests, controls, downloads, placed }. */
  torlink: () => get('Mira/torlink', null, { timeout: 10000 }),
  /** { id } of a search the PC runs with TorLink; refused ({ error }) while Mira is not open there. */
  torlinkSearch: (query, category) => post('Mira/torlink/search', { query, category }),
  /** { state: waiting, running, done or failed, results (without their magnet links), error }. */
  torlinkSearchState: (id) => get(`Mira/torlink/search/${encodeURIComponent(id)}`, null, { timeout: 10000 }),
  /** A result of a search ({ search, result, name, size }) or a pasted magnet link ({ magnet }): { id }. */
  torlinkRequest: (body) => post('Mira/torlink/requests', body),
  /** A request not yet taken by the PC (409 once it has it). */
  torlinkCancel: (id) => request('DELETE', `Mira/torlink/requests/${encodeURIComponent(id)}`),
  /** pause, resume or delete (its files with it) a download TorLink has. */
  torlinkControl: (id, action) => post('Mira/torlink/control', { id, action }),
  // « Suivre une série »: the series whose new episodes the PC of Jellyfin downloads on its own.
  /** The series followed, with their releases chosen and episodes taken (as in torlink().follows). */
  torlinkFollows: () => get('Mira/torlink/follows', null, { timeout: 10000 }),
  /** Follows a series, or changes the name searched for it (query: null for its own): { id } of its follow. */
  torlinkFollow: (seriesId, name, query = null) => post('Mira/torlink/follows', { seriesId, name, query }),
  torlinkUnfollow: (id) => request('DELETE', `Mira/torlink/follows/${encodeURIComponent(id)}`),
  /** « Chercher maintenant »: the PC checks it at its next visit. */
  torlinkCheckFollow: (id) => post(`Mira/torlink/follows/${encodeURIComponent(id)}/check`),
  /**
   * « Chercher des sous-titres »: the subtitle extensions of Jellyfin (« Open Subtitles »), for an account that may use
   * them (an administrator, or one allowed to manage subtitles); [] otherwise. Asked once per session.
   */
  subtitleProviders() {
    const token = session.current?.token;
    if (providers?.token !== token) {
      providers = {
        token,
        list: (async () => {
          const me = await get('Users/Me');
          if (!me?.Policy?.IsAdministrator && !me?.Policy?.EnableSubtitleManagement) return [];
          const options = await get('Libraries/AvailableOptions', { libraryContentType: 'movies' });
          return (options?.SubtitleFetchers ?? []).map((fetcher) => fetcher?.Name).filter(Boolean);
        })().catch(() => { providers = null; return []; }),
      };
    }
    return providers.list;
  },
  /** What Jellyfin's subtitle extensions find for a title in a language (« fre »), as Jellyfin gives it. */
  searchSubtitles: (itemId, language) => get(`Items/${itemId}/RemoteSearch/Subtitles/${encodeURIComponent(language)}`),
  /**
   * Keeps a subtitle found by the search: fetched through Jellyfin, then sent back to it as a file (keepSubtitle).
   * Jellyfin's own download (POST RemoteSearch/Subtitles/{id}) answers 204 even when it could not write the file.
   */
  async keepRemoteSubtitle(itemId, found) {
    await trustServer();
    let response;
    try {
      response = await fetch(url(`Providers/Subtitles/Subtitles/${encodeURIComponent(found.Id)}`), { headers: { Authorization: authorization() }, cache: 'no-store' });
    } catch { throw new ApiError(0, UNREACHABLE); }
    if (response.status === 403) return 'Refused';
    if (!response.ok) throw new ApiError(response.status, tr('Jellyfin a répondu avec une erreur ({status}).', { status: response.status }));
    const format = String(found.Format ?? '').toLowerCase();
    return this.keepSubtitle(itemId, {
      Format: SUBTITLE_FORMATS.includes(format) ? format : 'srt', Language: found.ThreeLetterISOLanguageName || 'und',
      IsForced: !!found.Forced, IsHearingImpaired: !!found.HearingImpaired, Data: base64(await response.arrayBuffer()),
    });
  },
  /**
   * Sends a subtitle file to Jellyfin, which keeps it for every device: Kept, KeptInJellyfinData, Refused or NotWritten
   * (subtitles.js keptText). Jellyfin writes it beside the video unless its library says otherwise; when it cannot
   * (500: a media folder it only reads, as a Mira install leaves them), the library of an administrator is switched to
   * Jellyfin's own data and the file sent again, as Mira for Windows does (Mira.Core JellyfinClient.KeepSubtitleAsync).
   */
  async keepSubtitle(itemId, subtitle) {
    const send = () => post(`Videos/${itemId}/Subtitles`, subtitle).then(() => 204, (error) => {
      if (error instanceof ApiError && (error.status === 403 || error.status === 500)) return error.status;
      throw error;
    });
    const first = await send();
    if (first === 403) return 'Refused';
    if (first !== 500) return 'Kept';
    if (!await keepSubtitlesInJellyfinData(itemId)) return 'NotWritten';
    const again = await send();
    return again === 403 ? 'Refused' : again === 500 ? 'NotWritten' : 'KeptInJellyfinData';
  },

  resume: (limit = 16) => get('UserItems/Resume', { userId: user(), limit, mediaTypes: 'Video', fields: CARD_FIELDS, ...CARD_IMAGES, enableUserData: true }),
  nextUp: (limit = 16) => get('Shows/NextUp', { userId: user(), limit, fields: CARD_FIELDS, ...CARD_IMAGES, enableResumable: false, enableRewatching: false }),
  /**
   * The 50 latest episodes not watched yet, with when Jellyfin added them, and their series (favourite, part watched):
   * what « Nouvel épisode » looks at (news.js), as Mira for Windows does (RecentEpisodesAsync).
   */
  async recentEpisodes() {
    const found = await get('Items', {
      userId: user(), recursive: true, includeItemTypes: 'Episode', isMissing: false, isPlayed: false, sortBy: 'DateCreated', sortOrder: 'Descending', limit: 50,
      fields: 'DateCreated', enableImageTypes: 'Primary', imageTypeLimit: 1,
    });
    const episodes = found?.Items ?? [];
    const ids = [...new Set(episodes.map((x) => x.SeriesId).filter(Boolean))];
    if (!ids.length) return { episodes, series: {} };
    const shows = await get('Items', { userId: user(), ids: ids.join(','), enableImages: false });
    return { episodes, series: Object.fromEntries((shows?.Items ?? []).filter((x) => x.Type === 'Series').map((x) => [x.Id, x])) };
  },
  /** What was watched lately, to order Continuer à regarder by the last activity on each film or series. */
  history: () => get('Items', { userId: user(), recursive: true, includeItemTypes: 'Movie,Episode', sortBy: 'DatePlayed', sortOrder: 'Descending', limit: 40, fields: 'SeriesInfo', enableImages: false, enableUserData: true }),

  /** Films and series, newest first unless sorted otherwise; filters as Mira on Windows. */
  /** startsWith: titles whose sort name starts with it (« The Batman » under B); before: those that sort before it. */
  browse({ types = 'Movie,Series', start = 0, limit = 60, sort = 'recent', genre, year, played, favorite, person, studio, search, library, startsWith, before, fields = CARD_FIELDS, signal } = {}) {
    const sorts = {
      title: ['SortName', 'Ascending'], year: ['ProductionYear,SortName', 'Descending'], rating: ['CommunityRating,SortName', 'Descending'],
      played: ['DatePlayed,SortName', 'Descending'], recent: ['DateCreated,SortName', 'Descending'],
    };
    const [sortBy, sortOrder] = sorts[sort] ?? sorts.recent;
    return get('Items', {
      userId: user(), recursive: true, includeItemTypes: types, startIndex: start, limit, sortBy, sortOrder,
      fields, ...CARD_IMAGES, enableTotalRecordCount: true,
      genres: genre, years: year, isPlayed: played, isFavorite: favorite ? true : undefined, personIds: person, studioIds: studio, searchTerm: search,
      parentId: library, nameStartsWith: startsWith, nameLessThan: before,
    }, { signal });
  },
  /**
   * « Ton bilan »: the films and episodes played to the end since `from` (ms, by their last play), newest first, a few
   * pages at most, and the series of those episodes (their genres, whether all is watched).
   */
  async playedSince(from) {
    const page = 300, played = [];
    while (played.length < 3000) {
      const result = await get('Items', {
        userId: user(), recursive: true, includeItemTypes: 'Movie,Episode', isPlayed: true, sortBy: 'DatePlayed', sortOrder: 'Descending',
        fields: 'Genres', enableImages: false, enableUserData: true, enableTotalRecordCount: false, startIndex: played.length, limit: page,
      });
      const items = result?.Items ?? [];
      played.push(...items);
      const last = Date.parse(items.at(-1)?.UserData?.LastPlayedDate ?? '');
      if (items.length < page || !(last >= from)) break;
    }
    const recent = played.filter((x) => Date.parse(x.UserData?.LastPlayedDate ?? '') >= from);
    const ids = [...new Set(recent.filter((x) => x.Type === 'Episode' && x.SeriesId).map((x) => x.SeriesId))];
    const series = [];
    for (let i = 0; i < ids.length; i += 100) {
      const result = await get('Items', { userId: user(), ids: ids.slice(i, i + 100).join(','), fields: 'Genres', enableImages: false, enableUserData: true });
      series.push(...(result?.Items ?? []));
    }
    return { played: recent, series };
  },
  /** The account's libraries (Films, Séries, Animes…), with their kind: CollectionType « movies », « tvshows »… */
  libraries: () => get('UserViews', { userId: user() }).then((r) => r?.Items ?? []),
  filters: (types = 'Movie,Series') => get('Items/Filters2', { userId: user(), includeItemTypes: types, recursive: true }),
  years: (types = 'Movie,Series') => get('Years', { userId: user(), includeItemTypes: types, recursive: true, enableImages: false, sortBy: 'SortName', sortOrder: 'Descending' }),
  persons: (search, signal) => get('Persons', { userId: user(), searchTerm: search, limit: 12, enableImageTypes: 'Primary' }, { signal }),
  /** The studios whose name holds the search (« Ghibli », « MAPPA »), among those of the titles this account sees. */
  studios: (search, signal) => get('Studios', { userId: user(), searchTerm: search, limit: 12, enableImages: false }, { signal }),
  /**
   * What the search finds beside films and series: episodes by their own name (a dozen, those Jellyfin lists as missing
   * left out) or sagas, Jellyfin's collections (« Harry Potter - Saga »).
   */
  searchOthers: (types, search, signal) => get('Items', {
    userId: user(), recursive: true, includeItemTypes: types, searchTerm: search, limit: 12, isMissing: types === 'Episode' ? false : undefined,
    fields: GRID_FIELDS, ...CARD_IMAGES,
  }, { signal }),

  item: (id) => get(`Items/${encodeURIComponent(id)}`, { userId: user() }),
  /** « Télécharger sur le PC serveur »: the films and series of a title found, to say it is already here (owned.js). */
  owned: (title, signal) => get('Items', {
    userId: user(), recursive: true, includeItemTypes: 'Movie,Series', searchTerm: title, limit: 20, fields: 'OriginalTitle,ProductionYear', enableImages: false, enableUserData: false,
  }, { signal }),
  /** The episodes a series has, their numbers only. */
  ownedEpisodes: (seriesId, signal) => get(`Shows/${encodeURIComponent(seriesId)}/Episodes`, { userId: user(), isMissing: false, enableImages: false, enableUserData: false }, { signal }),
  /** Every episode of a series, without those Jellyfin lists as missing; long series have more than 500. */
  episodes: (seriesId) => get(`Shows/${encodeURIComponent(seriesId)}/Episodes`, { userId: user(), fields: 'Overview', isMissing: false, enableImageTypes: 'Primary,Thumb', imageTypeLimit: 1 }),
  /** The episode after this one, if any. */
  async nextEpisode(item) {
    if (!item.SeriesId) return null;
    const result = await get(`Shows/${encodeURIComponent(item.SeriesId)}/Episodes`, { userId: user(), startItemId: item.Id, limit: 2, isMissing: false, fields: 'Overview', enableImageTypes: 'Primary,Thumb', imageTypeLimit: 1 });
    return result?.Items?.find((x) => x.Id !== item.Id) ?? null;
  },
  /** Where a series picks up: the episode in progress, or the next one to watch, with its tracks for « Audio et sous-titres ». */
  seriesNext: (seriesId) => get('Shows/NextUp', { userId: user(), seriesId, limit: 1, enableResumable: true, fields: 'Overview,MediaSources' }).then((r) => r?.Items?.[0] ?? null),
  /** A title's bonus videos (making-of, deleted scenes, textless credits from its Extras folders), played from their card. */
  extras: (id) => get(`Items/${encodeURIComponent(id)}/SpecialFeatures`, { userId: user() }).then((r) => (Array.isArray(r) ? r : [])),
  similar: (id) => get(`Items/${encodeURIComponent(id)}/Similar`, { userId: user(), limit: 16, fields: CARD_FIELDS, ...CARD_IMAGES }),
  /**
   * The saga a film belongs to (its first Jellyfin collection, as on Windows and Mac), with its other titles in the
   * order they came out. None from a Jellyfin that cannot say which collections hold a title (404: older than 12.1, where it was checked).
   */
  async sagas(id) {
    const found = await get(`Items/${encodeURIComponent(id)}/Collections`, { userId: user(), limit: 1 }).catch(() => null);
    return Promise.all((found?.Items ?? []).filter((box) => box.Type === 'BoxSet').map(async (box) => {
      const titles = await this.sagaTitles(box.Id);
      return { id: box.Id, name: box.Name, items: titles.filter((x) => x.Id !== id) };
    }));
  },
  /** A saga's titles in the order they came out (its page, and the row on the page of one of its films). */
  sagaTitles: (id) => get('Items', { userId: user(), parentId: id, sortBy: 'ProductionYear,PremiereDate,SortName', fields: CARD_FIELDS, ...CARD_IMAGES, enableUserData: true })
    .then((r) => r?.Items ?? []),

  setPlayed: (id, played) => request(played ? 'POST' : 'DELETE', `UserPlayedItems/${encodeURIComponent(id)}`, { params: { userId: user() } }),
  setFavorite: (id, favorite) => request(favorite ? 'POST' : 'DELETE', `UserFavoriteItems/${encodeURIComponent(id)}`, { params: { userId: user() } }),
  /** 0 takes a title out of « Reprendre » on every device, its other user data kept. */
  setPosition: (id, positionTicks) => post(`UserItems/${encodeURIComponent(id)}/UserData`, { PlaybackPositionTicks: Math.max(0, positionTicks) }, { userId: user() }),

  playbackInfo: (id, body) => post(`Items/${encodeURIComponent(id)}/PlaybackInfo`, { UserId: user(), ...body }, { userId: user() }),
  segments: (id) => get(`MediaSegments/${encodeURIComponent(id)}`, { includeSegmentTypes: ['Intro', 'Outro', 'Recap', 'Preview', 'Commercial'] }).then((r) => r?.Items ?? []).catch(() => []),
  report: (kind, body, keepalive = false) => post({ start: 'Sessions/Playing', progress: 'Sessions/Playing/Progress', stop: 'Sessions/Playing/Stopped' }[kind], body, null, { keepalive, timeout: 10000 }),
  // « Tes choix gardés sur ton Jellyfin » (account-choices.js): Mira's display preferences of the account, sent back whole.
  choices: () => get('DisplayPreferences/mira', { userId: user(), client: 'mira' }),
  saveChoices: (preferences) => post('DisplayPreferences/mira', preferences, { userId: user(), client: 'mira' }),
  // « Regarder ensemble », Jellyfin's SyncPlay (together.js): 403 when Jellyfin does not let this account use it.
  utcTime: () => get('GetUtcTime', null, { timeout: 5000 }),
  syncGroups: () => get('SyncPlay/List'),
  // From the list: `SyncPlay/{id}` answers 404 on Jellyfin 10.9 and 10.10.
  syncGroup: (groupId) => {
    const same = (id) => String(id ?? '').replace(/-/g, '').toLowerCase() === String(groupId ?? '').replace(/-/g, '').toLowerCase();
    return get('SyncPlay/List').then((groups) => (groups ?? []).find((g) => same(g.GroupId)) ?? null);
  },
  syncNew: (name) => post('SyncPlay/New', { GroupName: name }),
  syncJoin: (groupId) => post('SyncPlay/Join', { GroupId: groupId }),
  syncLeave: () => post('SyncPlay/Leave'),
  syncQueue: (itemId, startTicks) => post('SyncPlay/SetNewQueue', { PlayingQueue: [itemId], PlayingItemPosition: 0, StartPositionTicks: Math.max(0, Math.round(startTicks || 0)) }),
  /** Pause, Unpause, Seek ({ PositionTicks }), Buffering and Ready ({ When, PositionTicks, IsPlaying, PlaylistItemId }), Ping. */
  syncSend: (what, body) => post(`SyncPlay/${what}`, body),
  /**
   * A text for Jellyfin's logs (ClientLog/Document: a file upload_Mira_<version>_….log, kept as long as its logs);
   * its name. A 403 when Jellyfin takes none (AllowClientLogUpload).
   */
  sendLog: async (text) => (await request('POST', 'ClientLog/Document', { plain: text }))?.FileName ?? null,
  stopEncoding: (playSessionId) => request('DELETE', 'Videos/ActiveEncodings', { params: { deviceId: device.id, playSessionId }, keepalive: true }).catch(() => {}),
  /**
   * Downloads up to 4 MB to estimate the connection when away from home, for `limit` ms at most once Jellyfin starts
   * sending: cut there, what came by then gives the speed. The wait for its answer is not counted: on a fast line, a
   * short download timed from the request would mostly measure that wait. Throws when Jellyfin refuses (a 503 while it
   * starts is not a slow line), does not answer within 5 s, or sends nothing.
   */
  async bitrateTest(size = 4_000_000, limit = 1500) {
    await trustServer();
    const controller = new AbortController();
    let timer = setTimeout(() => controller.abort(), 5000);
    let started = performance.now(), bytes = 0;
    try {
      const response = await fetch(url('Playback/BitrateTest', { size }), { headers: { Authorization: authorization() }, cache: 'no-store', signal: controller.signal });
      if (!response.ok) throw new ApiError(response.status, tr('Jellyfin a répondu avec une erreur ({status}).', { status: response.status }));
      clearTimeout(timer);
      started = performance.now();
      timer = setTimeout(() => controller.abort(), limit);
      const reader = response.body?.getReader?.();
      if (!reader) bytes = (await response.arrayBuffer()).byteLength;
      else for (let part = await reader.read(); !part.done; part = await reader.read()) bytes += part.value.byteLength;
    } catch (error) {
      if (!controller.signal.aborted || !bytes) throw error;
    } finally { clearTimeout(timer); }
    return (bytes * 8) / Math.max(0.05, (performance.now() - started) / 1000);
  },
};

/** A URL Jellyfin accepts without an Authorization header (video, subtitles, trickplay images). */
export function signed(path) {
  const token = session.current?.token;
  if (!token) return `${base}/${path.replace(/^\//, '')}`;
  const separator = path.includes('?') ? '&' : '?';
  return `${base}/${path.replace(/^\//, '')}${/[?&]api_key=|[?&]ApiKey=/i.test(path) ? '' : `${separator}ApiKey=${encodeURIComponent(token)}`}`;
}
