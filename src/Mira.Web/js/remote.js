// Jellyfin's WebSocket while a title plays: what another device asks this one, « Arrêter » from Mira for Windows
// (« En ce moment sur Jellyfin ») or Jellyfin's dashboard. Jellyfin sends those only to a session that takes them
// (api.capabilities) and whose socket is open, so it is open only in the player. Lost (Wi-Fi to 4G, PC asleep), it
// comes back by itself, sooner or later.
import { api } from './api.js';

/**
 * Listens until `close()`: `playstate(command)` for Jellyfin's Playstate messages (Stop, Pause, Unpause, PlayPause…),
 * `message(m)` for any other.
 */
export function listen({ playstate, message } = {}) {
  let socket = null, closed = false, retry = 0, beat = 0, failures = 0;
  const send = (m) => { try { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(m)); } catch { /* closing */ } };
  const again = () => {
    if (closed) return;
    clearTimeout(retry);
    retry = setTimeout(open, Math.min(30_000, 1000 * 2 ** failures++));
  };
  async function open() {
    let address = null;
    try { address = await api.socketAddress(); } catch { /* another server answers here, or none */ }
    if (closed) return;
    if (!address) { again(); return; }
    try { socket = new WebSocket(address); } catch { again(); return; }
    socket.onopen = () => { failures = 0; };
    socket.onmessage = (e) => {
      let m = null;
      try { m = JSON.parse(e.data); } catch { return; }
      // Jellyfin closes a socket it has not heard from: it says how often (in seconds), Mira answers twice as often.
      if (m?.MessageType === 'ForceKeepAlive') {
        clearInterval(beat);
        beat = setInterval(() => send({ MessageType: 'KeepAlive' }), Math.max(5, (Number(m.Data) || 60) / 2) * 1000);
        send({ MessageType: 'KeepAlive' });
      } else if (m?.MessageType === 'Playstate') playstate?.(m.Data?.Command, m.Data);
      else if (m?.MessageType) message?.(m);
    };
    socket.onclose = () => { clearInterval(beat); socket = null; again(); };
  }
  api.capabilities().then(() => { if (!closed) open(); });
  return {
    send,
    close() {
      closed = true;
      clearTimeout(retry); clearInterval(beat);
      try { socket?.close(); } catch { /* already closed */ }
      socket = null;
    },
  };
}
