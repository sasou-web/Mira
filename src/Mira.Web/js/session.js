// What Mira keeps on this phone: the Jellyfin session (never the password), this device's id and the settings.
// A home screen app has its own storage, apart from Safari's: one sign-in there, once.

const SESSION = 'mira.session', DEVICE = 'mira.device', SETTINGS = 'mira.settings';

function read(key, fallback) {
  try { const raw = localStorage.getItem(key); return raw ? JSON.parse(raw) : fallback; }
  catch { return fallback; }
}
function write(key, value) {
  try { value == null ? localStorage.removeItem(key) : localStorage.setItem(key, JSON.stringify(value)); }
  catch { /* Private browsing without storage: Mira works until the page is closed. */ }
}

/**
 * Whether this browser lets Mira keep anything (Safari's « Bloquer tous les cookies » refuses even the reading):
 * without it, the session lasts until Mira is closed, and the sign-in screen says so.
 */
export function storageBlocked() {
  try { localStorage.setItem('mira.probe', '1'); localStorage.removeItem('mira.probe'); return false; } catch (error) {
    // Full of Mira's own screens: it does keep things. Refused, or of no size at all (older private browsing): blocked.
    try { return !(error?.name === 'QuotaExceededError' && localStorage.length > 0); } catch { return true; }
  }
}

/**
 * Notes for this visit only (sessionStorage): where to go after signing in, a notice for the sign-in screen, an update
 * already tried. When the site's storage is blocked, they stay in memory until the page reloads, and set() says so
 * (false): what must survive a reload is not kept.
 */
const notes = new Map();
export const temp = {
  get(key) { try { return sessionStorage.getItem(key) ?? notes.get(key) ?? null; } catch { return notes.get(key) ?? null; } },
  set(key, value) { try { sessionStorage.setItem(key, value); return true; } catch { notes.set(key, String(value)); return false; } },
  remove(key) { notes.delete(key); try { sessionStorage.removeItem(key); } catch { /* blocked: only in memory */ } },
};

/** { userId, userName, token, serverId, serverName } once signed in. */
export const session = {
  current: read(SESSION, null),
  set(value) { this.current = value; write(SESSION, value); },
  /** Signed out (here, or by Jellyfin): nothing of the account stays on this phone, see forgetAccount. */
  clear() { this.current = null; write(SESSION, null); forgetAccount(); },
};

/** iPhone, iPad, Android or the browser's name: how Jellyfin's dashboard lists this device. */
function deviceName() {
  const ua = navigator.userAgent;
  if (/iPhone/.test(ua)) return 'iPhone';
  if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
  if (/Android/.test(ua)) return 'Android';
  if (/Firefox\//.test(ua)) return 'Firefox';
  if (/Edg\//.test(ua)) return 'Edge';
  if (/Chrome\//.test(ua)) return 'Chrome';
  if (/Safari\//.test(ua)) return 'Safari';
  return 'Navigateur';
}

function deviceId() {
  let id = read(DEVICE, null);
  if (typeof id !== 'string' || id.length < 16) {
    // crypto.randomUUID needs HTTPS; a home server is reached over plain HTTP.
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    id = Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
    write(DEVICE, id);
  }
  return id;
}

export const device = { id: deviceId(), name: deviceName() };

const defaults = {
  quality: 'auto',      // 'auto', 'max' or a bitrate in bits per second
  autoNext: true,       // the next episode starts by itself
  resume: true,         // a title resumes where it was left
  subtitleSize: 100,    // percent, for subtitles drawn by Mira (Apple's player follows iOS's own settings)
  audioLanguages: 'jpn,ja,fre,fra,fr,eng,en', // as Mira on Windows and Mac
  subtitleLanguages: 'fre,fra,fr,eng,en',
  autoSkip: false,      // openings and recaps skipped without a tap (Apple's player has no room for the button)
  seriesTracks: {},     // series id → { audio, subtitle, forced }: the languages last chosen for it
  nativePlayer: null,   // null: Apple's player on iPhone and iPad; true or false forces it (Safari's check)
  ambientAudio: false,  // iPhone: an « ambient » audio session while playing: no Dynamic Island, but the Silent mode mutes it
  recentSearches: [],
  hiddenResume: {},     // id → time removed from Continuer à regarder
  installHintDismissed: false,
  textSize: 100,        // percent: Mira's text, for whoever finds it small (the bars' labels and the icons keep theirs)
};

export const settings = {
  values: { ...defaults, ...read(SETTINGS, {}) },
  get(key) { return this.values[key]; },
  set(key, value) { this.values[key] = value; write(SETTINGS, this.values); },
};

// What Mira keeps of the account signed in, beside its session: its screens as last shown (home, Films and Séries,
// by user) and, in the settings, what it did with its titles. This device's id and its settings stay.
const ACCOUNT_KEYS = /^mira\.(home|library)\./;
const ACCOUNT_SETTINGS = ['recentSearches', 'hiddenResume', 'seriesTracks'];
function forgetAccount() {
  try {
    const keys = [];
    for (let i = 0; i < localStorage.length; i++) { const key = localStorage.key(i); if (ACCOUNT_KEYS.test(key)) keys.push(key); }
    keys.forEach((key) => localStorage.removeItem(key));
  } catch { /* Private browsing without storage: nothing was kept. */ }
  for (const key of ACCOUNT_SETTINGS) settings.values[key] = Array.isArray(defaults[key]) ? [] : {};
  write(SETTINGS, settings.values);
}

/** The text size of Réglages, on the whole page (app.css multiplies each size by --text). */
export function applyTextSize() {
  const percent = Number(settings.get('textSize'));
  document.documentElement.style.setProperty('--text', String((percent >= 80 && percent <= 200 ? percent : 100) / 100));
}

export const isStandalone = () => matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
export const isIOS = () => device.name === 'iPhone' || device.name === 'iPad';
