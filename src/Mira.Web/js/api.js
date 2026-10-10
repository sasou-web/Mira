// Jellyfin's API, from the same address as Mira web: no server to type, no cross-site request.
import { session, device } from './session.js';

export const VERSION = document.querySelector('meta[name="mira-version"]')?.content.replace('{{version}}', '0.0.0') || '0.0.0';

/** Jellyfin's address on this server, with its base URL if it has one: « » or « /jellyfin ». */
export const base = location.pathname.replace(/\/Mira\/?.*$/i, '');

/** A Tailscale address (100.64.0.0/10): Mira opened through Tailscale. */
export const isTailnet = (host) => /^100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d{1,3}\.\d{1,3}$/.test(host);
const isHomeNetwork = (host) => /^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/.test(host);

/**
 * An address that never goes through the Internet: this device, a home network (private and link-local ranges, in
 * IPv4 and IPv6), Tailscale (its addresses and its names), or a name only a home network knows (« nas », « pc.local »).
 */
export function isLocalHost(hostname) {
  const host = String(hostname).replace(/^\[|\]$/g, '').replace(/\.$/, '').toLowerCase();
  // IPv6: loopback, unique local (fc00::/7, Tailscale's among them), link-local (fe80::/10).
  if (host.includes(':')) return /^(::1$|f[cd][0-9a-f]{0,2}:|fe[89ab][0-9a-f]?:)/.test(host);
  if (/^\d+\.\d+\.\d+\.\d+$/.test(host)) return /^(127|10)\./.test(host) || /^(169\.254|192\.168)\./.test(host) || isHomeNetwork(host) || isTailnet(host);
  return !host.includes('.') || /(^|\.)(localhost|local|ts\.net|home\.arpa)$/.test(host);
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
  tailnet: 'Serveur injoignable : vérifie que Tailscale est activé sur ce téléphone et que le PC est allumé. Mira réessaie toute seule.',
  home: 'Serveur injoignable : le PC est éteint, ou ce téléphone n’est pas sur le Wi-Fi de la maison. Mira réessaie toute seule.',
}[where] ?? 'Serveur injoignable : le PC est peut-être éteint ou en train de démarrer. Mira réessaie toute seule.';
export const UNREACHABLE_SHORT = { tailnet: 'Serveur injoignable. Tailscale est-il activé ?', home: 'Serveur injoignable. Hors du Wi-Fi de la maison ?' }[where]
  ?? 'Serveur injoignable. Mira réessaie toute seule.';
/** What to check when another server answers at this address (see checkServer). */
export const FOREIGN = {
  tailnet: 'Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie que Tailscale est activé sur ce téléphone, avec ton compte.',
  home: 'Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie que ce téléphone est bien sur le Wi-Fi de ta maison.',
}[where] ?? 'Un autre serveur répond à cette adresse, alors Mira ne lui envoie pas ta session. Vérifie l’adresse de Mira.';

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
 * with (false) once it is again.
 */
export const onForeignServer = (fn) => strangers.add(fn);

const STARTING = 'Jellyfin ne répond pas encore : il est sans doute en train de démarrer. Mira réessaie toute seule.';
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
// phone may have changed networks.
let identity = null;   // { expected, promise }: the check of the server answering here
let foreign = false;   // the last check concluded: another server
// 421, « Misdirected Request »: the request would go to a server that is not the one it is meant for.
const notYours = () => new ApiError(421, `Ce n’est pas ton serveur. ${FOREIGN}`, { foreign: true });
const sameId = (a, b) => String(a ?? '').replace(/-/g, '').toLowerCase() === String(b ?? '').replace(/-/g, '').toLowerCase();

/** The next request that carries the token checks the server first. */
export function recheckServer() { identity = null; }
// Back from the background or from a loss of network, the phone may be on another network. Not when it goes to the
// background: the reports sent then (keepalive) could not wait for a check.
document.addEventListener('visibilitychange', () => { if (!document.hidden) recheckServer(); });
addEventListener('online', () => recheckServer());

/**
 * Resolves with the Id of the server answering here once it is `expected` (any Jellyfin when nothing is expected);
 * rejects otherwise: unreachable or starting (nothing is concluded, Mira waits for it as for any request), or foreign.
 */
export function checkServer(expected = '') {
  if (identity && identity.expected === expected) return identity.promise;
  const check = { expected };
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
    if (foreign && identity === check) { foreign = false; strangers.forEach((fn) => fn(false)); }
  }, (error) => {
    if (identity !== check) return;
    if (error.foreign) { foreign = true; strangers.forEach((fn) => fn(true)); }
    else identity = null;   // asked again by the next request
  });
  return check.promise;
}

/** Waits until the server answering here is the session's; throws, without sending anything, when it is not. */
export async function trustServer() {
  const current = session.current;
  if (!current) return;
  const id = await checkServer(current.serverId ?? '');
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
 * A request to Jellyfin; errors come back in French, ready to show. With the session's token unless `token` says
 * otherwise (null: none), and only once the server is known to be the session's (trustServer).
 */
export async function request(method, path, { params, body, signal, timeout = 20000, keepalive = false, token = session.current?.token } = {}) {
  if (token && token === session.current?.token) {
    await trustServer();
    if (signal?.aborted) throw signal.reason ?? new DOMException('Aborted', 'AbortError');
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
      headers: { Authorization: authorization(token), Accept: 'application/json', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    setReachable(false, 'offline');
    throw new ApiError(0, controller.signal.aborted
      ? 'Le serveur met trop de temps à répondre. Mira réessaie dès qu’il répond.'
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
    throw new ApiError(401, 'Ta session a pris fin. Reconnecte-toi.');
  }
  if (!response.ok) {
    throw new ApiError(response.status, response.status === 403
      ? 'Ton compte Jellyfin n’a pas accès à ceci.'
      : response.status === 404 ? 'Ce titre n’existe plus sur le serveur.' : `Jellyfin a répondu avec une erreur (${response.status}).`);
  }
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

const get = (path, params, options) => request('GET', path, { params, ...options });
const post = (path, body, params, options) => request('POST', path, { body, params, ...options });
const user = () => session.current?.userId;

// Fields every card needs: overview and genres for the banner, child counts for series.
const CARD_FIELDS = 'Overview,Genres,ChildCount,PrimaryImageAspectRatio';
const CARD_IMAGES = { enableImageTypes: 'Primary,Backdrop,Thumb,Logo', imageTypeLimit: 1 };

/** True once Jellyfin itself answers: while it starts, a page about it answers in its place, but not to Ping. */
export async function ping() {
  try {
    const response = await fetch(url('System/Ping'), { cache: 'no-store', signal: AbortSignal.timeout?.(6000) });
    if (response.ok) setReachable(true);
    return response.ok;
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

  async signIn(username, password) {
    const result = await post('Users/AuthenticateByName', { Username: username, Pw: password }, null, { token: null });
    const info = await this.publicInfo().catch(() => ({}));
    // A new session: the server that gave it is checked anew, whatever was concluded of the one before.
    recheckServer();
    session.set({ userId: result.User.Id, userName: result.User.Name, token: result.AccessToken, serverId: result.ServerId, serverName: info.ServerName ?? '' });
    // Lets Jellyfin's dashboard show what this phone can play.
    post('Sessions/Capabilities', undefined, { playableMediaTypes: 'Video', supportsMediaControl: false }).catch(() => {});
    return result;
  },
  /**
   * Signed out here at once; Jellyfin is told with the token that was used, so a new sign-in is never undone, and
   * only if it is the server that gave it.
   */
  signOut() {
    const { token, serverId = '' } = session.current ?? {};
    session.clear();
    if (!token) return Promise.resolve();
    return checkServer(serverId).then(() => post('Sessions/Logout', undefined, null, { timeout: 5000, token, keepalive: true })).catch(() => {});
  },
  me: () => get('Users/Me'),

  resume: (limit = 16) => get('UserItems/Resume', { userId: user(), limit, mediaTypes: 'Video', fields: CARD_FIELDS, ...CARD_IMAGES, enableUserData: true }),
  nextUp: (limit = 16) => get('Shows/NextUp', { userId: user(), limit, fields: CARD_FIELDS, ...CARD_IMAGES, enableResumable: false, enableRewatching: false }),
  /** What was watched lately, to order Continuer à regarder by the last activity on each film or series. */
  history: () => get('Items', { userId: user(), recursive: true, includeItemTypes: 'Movie,Episode', sortBy: 'DatePlayed', sortOrder: 'Descending', limit: 40, fields: 'SeriesInfo', enableImages: false, enableUserData: true }),

  /** Films and series, newest first unless sorted otherwise; filters as Mira on Windows. */
  browse({ types = 'Movie,Series', start = 0, limit = 60, sort = 'recent', genre, year, played, favorite, person, search, signal } = {}) {
    const sorts = {
      title: ['SortName', 'Ascending'], year: ['ProductionYear,SortName', 'Descending'], rating: ['CommunityRating,SortName', 'Descending'],
      played: ['DatePlayed,SortName', 'Descending'], recent: ['DateCreated,SortName', 'Descending'],
    };
    const [sortBy, sortOrder] = sorts[sort] ?? sorts.recent;
    return get('Items', {
      userId: user(), recursive: true, includeItemTypes: types, startIndex: start, limit, sortBy, sortOrder,
      fields: CARD_FIELDS, ...CARD_IMAGES, enableTotalRecordCount: true,
      genres: genre, years: year, isPlayed: played, isFavorite: favorite ? true : undefined, personIds: person, searchTerm: search,
    }, { signal });
  },
  filters: (types = 'Movie,Series') => get('Items/Filters2', { userId: user(), includeItemTypes: types, recursive: true }),
  years: (types = 'Movie,Series') => get('Years', { userId: user(), includeItemTypes: types, recursive: true, enableImages: false, sortBy: 'SortName', sortOrder: 'Descending' }),
  persons: (search, signal) => get('Persons', { userId: user(), searchTerm: search, limit: 12, enableImageTypes: 'Primary' }, { signal }),

  item: (id) => get(`Items/${encodeURIComponent(id)}`, { userId: user() }),
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
  similar: (id) => get(`Items/${encodeURIComponent(id)}/Similar`, { userId: user(), limit: 16, fields: CARD_FIELDS, ...CARD_IMAGES }),

  setPlayed: (id, played) => request(played ? 'POST' : 'DELETE', `UserPlayedItems/${encodeURIComponent(id)}`, { params: { userId: user() } }),
  setFavorite: (id, favorite) => request(favorite ? 'POST' : 'DELETE', `UserFavoriteItems/${encodeURIComponent(id)}`, { params: { userId: user() } }),
  /** 0 takes a title out of « Reprendre » on every device, its other user data kept. */
  setPosition: (id, positionTicks) => post(`UserItems/${encodeURIComponent(id)}/UserData`, { PlaybackPositionTicks: Math.max(0, positionTicks) }, { userId: user() }),

  playbackInfo: (id, body) => post(`Items/${encodeURIComponent(id)}/PlaybackInfo`, { UserId: user(), ...body }, { userId: user() }),
  segments: (id) => get(`MediaSegments/${encodeURIComponent(id)}`, { includeSegmentTypes: ['Intro', 'Outro', 'Recap', 'Preview', 'Commercial'] }).then((r) => r?.Items ?? []).catch(() => []),
  report: (kind, body, keepalive = false) => post({ start: 'Sessions/Playing', progress: 'Sessions/Playing/Progress', stop: 'Sessions/Playing/Stopped' }[kind], body, null, { keepalive, timeout: 10000 }),
  stopEncoding: (playSessionId) => request('DELETE', 'Videos/ActiveEncodings', { params: { deviceId: device.id, playSessionId }, keepalive: true }).catch(() => {}),
  /** Downloads about 1 MB to estimate the connection when away from home. */
  async bitrateTest(size = 1_000_000) {
    await trustServer();
    const started = performance.now();
    const response = await fetch(url('Playback/BitrateTest', { size }), { headers: { Authorization: authorization() }, cache: 'no-store' });
    const bytes = (await response.arrayBuffer()).byteLength;
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
