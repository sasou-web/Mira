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

/** { userId, userName, token, serverId, serverName } once signed in. */
export const session = {
  current: read(SESSION, null),
  set(value) { this.current = value; write(SESSION, value); },
  clear() { this.current = null; write(SESSION, null); },
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
};

export const settings = {
  values: { ...defaults, ...read(SETTINGS, {}) },
  get(key) { return this.values[key]; },
  set(key, value) { this.values[key] = value; write(SETTINGS, this.values); },
};

export const isStandalone = () => matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
export const isIOS = () => device.name === 'iPhone' || device.name === 'iPad';
