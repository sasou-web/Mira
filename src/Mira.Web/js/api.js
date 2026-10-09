// Jellyfin's API, from the same address as Mira web: no server to type, no cross-site request.
import { session, device } from './session.js';

export const VERSION = document.querySelector('meta[name="mira-version"]')?.content.replace('{{version}}', '0.0.0') || '0.0.0';

/** Jellyfin's address on this server, with its base URL if it has one: « » or « /jellyfin ». */
export const base = location.pathname.replace(/\/Mira\/?.*$/i, '');

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
  get offline() { return this.status === 0; }
  /** Nothing answers (PC off, no network) or Jellyfin is still starting: Mira waits for it. */
  get unreachable() { return this.status === 0 || this.status === 503; }
}

const listeners = new Set();
/** Called when Jellyfin refuses the session (signed out elsewhere, password changed). */
export const onUnauthorized = (fn) => listeners.add(fn);

const STARTING = 'Jellyfin ne répond pas encore : il est sans doute en train de démarrer. Mira réessaie toute seule.';
const watchers = new Set();
let reachable = true;
/**
 * Called with (false, 'offline' | 'starting') when Jellyfin stops answering, and with (true) once it answers again.
 * 'starting': Jellyfin answers 503 while it starts, or while it turns away a network it does not count as local.
 */
export const onReachable = (fn) => watchers.add(fn);
function setReachable(value, reason = '') {
  if (value === reachable) return;
  reachable = value;
  watchers.forEach((fn) => fn(value, reason));
}
export const isReachable = () => reachable;

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

/** A request to Jellyfin; errors come back in French, ready to show. */
export async function request(method, path, { params, body, signal, timeout = 20000, keepalive = false } = {}) {
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
      headers: { Authorization: authorization(), Accept: 'application/json', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    setReachable(false, 'offline');
    throw new ApiError(0, controller.signal.aborted
      ? 'Le serveur met trop de temps à répondre. Mira réessaie dès qu’il répond.'
      : 'Serveur injoignable : le PC est peut-être éteint ou en train de démarrer. Mira réessaie toute seule.');
  } finally { clearTimeout(timer); }

  if (response.status === 503) {
    setReachable(false, 'starting');
    throw new ApiError(503, STARTING);
  }
  // Back up is for ping() to say: while Jellyfin starts, its startup page answers some routes too.
  if (response.status === 401) {
    if (session.current) { session.clear(); listeners.forEach((fn) => fn()); }
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
  async publicInfo() {
    const info = await get('System/Info/Public', null, { timeout: 8000 });
    // While Jellyfin starts, its startup page answers here too (in camelCase): that is not the server yet.
    if (!info?.Id) { setReachable(false, 'starting'); throw new ApiError(503, STARTING); }
    return info;
  },
  publicUsers: () => get('Users/Public'),

  async signIn(username, password) {
    const result = await post('Users/AuthenticateByName', { Username: username, Pw: password });
    const info = await this.publicInfo().catch(() => ({}));
    session.set({ userId: result.User.Id, userName: result.User.Name, token: result.AccessToken, serverId: result.ServerId, serverName: info.ServerName ?? '' });
    // Lets Jellyfin's dashboard show what this phone can play.
    post('Sessions/Capabilities', undefined, { playableMediaTypes: 'Video', supportsMediaControl: false }).catch(() => {});
    return result;
  },
  async signOut() {
    await post('Sessions/Logout', undefined, null, { timeout: 5000 }).catch(() => {});
    session.clear();
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
  /** Where a series picks up: the episode in progress, or the next one to watch. */
  seriesNext: (seriesId) => get('Shows/NextUp', { userId: user(), seriesId, limit: 1, enableResumable: true, fields: 'Overview' }).then((r) => r?.Items?.[0] ?? null),
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
