// Jellyfin's WebSocket, one for the page, open while something listens: the player (« Arrêter » from Mira for Windows
// or Jellyfin's dashboard) and a « Regarder ensemble » group (together.js). Jellyfin sends those only to a session that
// takes them (api.capabilities) and whose socket is open. Lost (Wi-Fi to 4G, PC asleep), it comes back by itself,
// sooner or later.
import { api } from './api.js';

const listeners = new Set();
let socket = null, retry = 0, beat = 0, failures = 0, opening = false;

const send = (m) => { try { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(m)); } catch { /* closing */ } };
function again() {
  clearTimeout(retry);
  if (!listeners.size) return;
  retry = setTimeout(open, Math.min(30_000, 1000 * 2 ** failures++));
}
async function open() {
  if (socket || opening || !listeners.size) return;
  opening = true;
  let address = null;
  try { await api.capabilities(); address = await api.socketAddress(); } catch { /* another server answers here, or none */ }
  opening = false;
  if (!listeners.size || socket) return;
  if (!address) { again(); return; }
  let mine;
  try { mine = socket = new WebSocket(address); } catch { socket = null; again(); return; }
  mine.onopen = () => { failures = 0; for (const l of listeners) l.opened?.(); };
  mine.onmessage = (e) => {
    let m = null;
    try { m = JSON.parse(e.data); } catch { return; }
    // Jellyfin closes a socket it has not heard from: it says how often (in seconds), Mira answers twice as often.
    if (m?.MessageType === 'ForceKeepAlive') {
      clearInterval(beat);
      beat = setInterval(() => send({ MessageType: 'KeepAlive' }), Math.max(5, (Number(m.Data) || 60) / 2) * 1000);
      send({ MessageType: 'KeepAlive' });
      return;
    }
    for (const l of [...listeners]) {
      try {
        if (m?.MessageType === 'Playstate') l.playstate?.(m.Data?.Command, m.Data);
        else if (m?.MessageType) l.message?.(m);
      } catch { /* one listener's failure does not stop the others */ }
    }
  };
  mine.onclose = () => {
    if (socket !== mine) return;
    clearInterval(beat); socket = null;
    again();
  };
}

/**
 * Listens until `close()`: `playstate(command)` for Jellyfin's Playstate messages (Stop, Pause, Unpause, PlayPause…),
 * `message(m)` for any other, `opened()` each time the socket opens (again).
 */
export function listen(handlers = {}) {
  const mine = { ...handlers };
  listeners.add(mine);
  open();
  return {
    send,
    close() {
      if (!listeners.delete(mine) || listeners.size) return;
      clearTimeout(retry); clearInterval(beat); failures = 0;
      const old = socket; socket = null;
      try { old?.close(); } catch { /* already closed */ }
    },
  };
}
