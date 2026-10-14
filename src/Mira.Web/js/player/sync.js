// « Regarder ensemble » (Jellyfin's SyncPlay), the part that needs no browser: the server's clock, where and when a
// command of the group puts the player, the group's words. tests/fixtures/syncplay.json holds the cases.

/** Jellyfin's dates carry seven decimals; Safari reads three at most (NaN otherwise). Milliseconds since 1970. */
export function serverTime(iso) {
  if (typeof iso !== 'string') return NaN;
  return Date.parse(iso.replace(/(\.\d{3})\d+/, '$1'));
}

/**
 * Jellyfin's clock minus this device's, in milliseconds, from GetUtcTime answers ({ sent, received } here,
 * { serverIn, serverOut } there): the answer with the shortest round trip, the least delayed. Null without one.
 */
export function clockOffset(samples) {
  let best = null;
  for (const s of samples ?? []) {
    const trip = (s.received - s.sent) - (s.serverOut - s.serverIn);
    if (!Number.isFinite(trip) || trip < 0) continue;
    if (!best || trip < best.trip) best = { trip, offset: Math.round(((s.serverIn - s.sent) + (s.serverOut - s.received)) / 2) };
  }
  return best;
}

/**
 * What a SyncPlayCommand asks of this player, on its clock: play in `wait` ms from `position` (in seconds; a command
 * that arrives late starts further on, where the others are), or pause, seek, stop at `position`. Null for another.
 */
export function commandPlan(data, { offset = 0, now }) {
  const position = (Number(data?.PositionTicks) || 0) / 1e7;
  switch (data?.Command) {
    case 'Unpause': {
      const wait = Math.round(serverTime(data.When) - offset - now);
      if (!Number.isFinite(wait)) return { action: 'play', wait: 0, position };
      return { action: 'play', wait: Math.max(0, wait), position: wait < 0 ? position - wait / 1000 : position };
    }
    case 'Pause': return { action: 'pause', wait: 0, position };
    case 'Seek': return { action: 'seek', wait: 0, position };
    case 'Stop': return { action: 'stop', wait: 0, position };
    default: return null;
  }
}

/** Under half a second apart, a jump would be seen more than the gap. */
export const needsSeek = (current, target) => Math.abs(current - target) >= 0.5;

/** The title a PlayQueue update puts on, where it starts; null when it plays nothing. */
export function queueItem(data) {
  const entry = data?.Playlist?.[data?.PlayingItemIndex];
  if (!entry?.ItemId) return null;
  return { itemId: entry.ItemId, playlistItemId: entry.PlaylistItemId ?? '', startTicks: Number(data.StartPositionTicks) || 0 };
}

/** The name a new group takes: « Groupe de mira ». */
export const groupName = (user) => `Groupe de ${user}`;

/** « 2 personnes » (Jellyfin names each account of the group once, whatever its devices). */
export const people = (count) => (count <= 1 ? '1 personne' : `${count} personnes`);

/** « Groupe de mira · 2 personnes ». */
export const groupText = (group) => `${group?.GroupName ?? 'Groupe'} · ${people(group?.Participants?.length ?? 1)}`;

/** The link that opens Mira web on the group: the friend signs in with an account of this Jellyfin, then is in it. */
export const inviteLink = (server, groupId) => `${String(server).replace(/\/+$/, '')}/Mira/#/ensemble/${encodeURIComponent(groupId)}`;

/** Where a host is reached from: Tailscale, the home network only, or the Internet. */
function reach(host) {
  const ip = /^(\d{1,3})\.(\d{1,3})\.\d{1,3}\.\d{1,3}$/.exec(host);
  if (ip) {
    const [a, b] = [Number(ip[1]), Number(ip[2])];
    if (a === 100 && b >= 64 && b < 128) return 'tailnet';
    return a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b < 32) || a === 127 ? 'home' : 'internet';
  }
  if (/\.ts\.net$/i.test(host)) return 'tailnet';
  return !host.includes('.') || /\.(local|lan)$/i.test(host) ? 'home' : 'internet';
}

/** What the invitation says under its link: who may follow it, and from where (Tailscale, the home network). */
export function inviteSteps(link) {
  let host = '';
  try { host = new URL(link).hostname; } catch { /* not an address */ }
  const steps = [
    'Ton ami ouvre ce lien sur son téléphone ou son ordinateur et se connecte avec son propre compte Jellyfin : il est alors dans le groupe.',
    'Pas encore de compte ? Crée-le-lui dans Jellyfin : tableau de bord → Utilisateurs → +, avec l’accès à tes bibliothèques.',
  ];
  const where = reach(host);
  if (where === 'tailnet') steps.push('Hors de chez toi, il lui faut Tailscale : partage-lui l’ordinateur de Jellyfin (login.tailscale.com → Machines → … → Share), il accepte avec son compte Tailscale gratuit.');
  else if (where === 'home') steps.push('Ce lien ne marche que sur ton Wi-Fi. Pour un ami ailleurs, installe Tailscale sur l’ordinateur de Jellyfin (Mira pour Windows : Guide → Mira sur ton téléphone).');
  steps.push('Quand il est là, le groupe compte une personne de plus : lance le titre, il démarre chez vous deux au même moment.');
  return steps;
}
