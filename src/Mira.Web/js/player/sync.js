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

/** Who made a group of Mira, from its name (« Groupe de mira » → mira); null for a name Mira does not give. */
export function groupHost(name) {
  const m = /^Groupe de (.+)$/.exec(name ?? '');
  return m ? m[1] : null;
}

/** Jellyfin writes a group's id with or without dashes. */
export const sameGroup = (a, b) => String(a ?? '').replace(/-/g, '').toLowerCase() === String(b ?? '').replace(/-/g, '').toLowerCase();

/**
 * The group an invitation means among Jellyfin's: the one of its id, else the one its host made again since under the
 * same name (Jellyfin ends an empty group with its last device; together.js makes it again), with the host in it
 * first, then the fullest. Null when none is open.
 */
export function findGroup(groups, id, host) {
  const list = Array.isArray(groups) ? groups : [];
  const same = list.find((g) => sameGroup(g.GroupId, id));
  if (same || !host) return same ?? null;
  const there = (g) => (g.Participants ?? []).includes(host);
  const named = list.filter((g) => g.GroupName === groupName(host));
  named.sort((a, b) => Number(there(b)) - Number(there(a)) || (b.Participants?.length ?? 0) - (a.Participants?.length ?? 0));
  return named[0] ?? null;
}

/**
 * Back in a group Jellyfin let this device go from (its session ended with its socket: an iPhone gone to Messages, Wi-Fi
 * to 4G): `{ join: id }` where it still is, `{ create: name }` when this device made it and it ended (empty), so that
 * the invitations sent still find it (findGroup); null when there is nothing to go back to.
 */
export function rejoinPlan(groups, lost, made) {
  const found = findGroup(groups, lost?.GroupId, groupHost(lost?.GroupName));
  if (found) return { join: found.GroupId };
  return made && lost?.GroupName ? { create: lost.GroupName } : null;
}

/** What a friend reads when the host has no group open yet: « mira n’a pas encore lancé la soirée ». */
export const waitingText = (host) => `${host} n’a pas encore lancé la soirée`;
/** What it says under it, while Mira waits for the group. */
export const WAITING_LEAD = 'Garde Mira ouvert : tu entres dans le groupe dès qu’il est ouvert.';

/** « 2 personnes » (Jellyfin names each account of the group once, whatever its devices). */
export const people = (count) => (count <= 1 ? '1 personne' : `${count} personnes`);

/** « Groupe de mira · 2 personnes ». */
export const groupText = (group) => `${group?.GroupName ?? 'Groupe'} · ${people(group?.Participants?.length ?? 1)}`;

/** Who is in a group, as a line says it: « mira et Marc », « mira, Marc et Léa », « mira, Marc et 3 autres ». */
export function namesText(names) {
  const list = [...(names ?? [])];
  if (list.length <= 1) return list.join('');
  if (list.length > 3) return `${list.slice(0, 2).join(', ')} et ${list.length - 2} autres`;
  return `${list.slice(0, -1).join(', ')} et ${list.at(-1)}`;
}
/**
 * What the group is doing, in a line (the player's pill, the title page), and whether it holds the picture (`alert`).
 * Jellyfin says only its state (Idle, Waiting, Paused, Playing), never who caused it: a wait while this device is
 * ready (not `loading`) is for the others, named when there is one. `me`: this account's name, as Participants names
 * each account once. The same as Together.GroupStatus.
 */
export function groupStatus(group, me, loading = false) {
  if (!group) return null;
  const mine = String(me ?? '').toLowerCase();
  const others = (group.Participants ?? []).filter((x) => String(x).toLowerCase() !== mine);
  if (group.State === 'Waiting') {
    if (loading || !others.length) return { text: 'Chargement pour le groupe…', alert: true };
    return { text: others.length === 1 ? `On attend ${others[0]}…` : 'On attend quelqu’un du groupe…', alert: true };
  }
  if (group.State === 'Paused') return { text: 'En pause pour le groupe', alert: true };
  return { text: others.length ? `Ensemble · ${namesText(group.Participants)}` : 'Ensemble · personne d’autre pour l’instant', alert: false };
}
/** « Marc a rejoint le groupe », « Marc a quitté le groupe ». */
export const memberText = (name, joined) => `${name} ${joined ? 'a rejoint' : 'a quitté'} le groupe`;

/**
 * Lecture of `itemId` while the group has another title on, not over (Idle): asked first, rather than changed for
 * everyone unannounced. The same as Together.AsksToReplace.
 */
export const asksToReplace = (group, queue, itemId) => !!group && !!queue?.itemId && queue.itemId !== itemId && group.State !== 'Idle';
/** A title as a question names it: a film by its name, an episode with its series (« Dark · S1 · É3 »). */
export function titleName(item) {
  if (!item) return '';
  if (item.Type !== 'Episode' || !item.SeriesName) return item.Name ?? '';
  const code = [item.ParentIndexNumber != null ? `S${item.ParentIndexNumber}` : '', item.IndexNumber != null ? `É${item.IndexNumber}` : ''].filter(Boolean).join(' · ');
  return code ? `${item.SeriesName} · ${code}` : item.SeriesName;
}
/** « Remplacer « Dune » pour tout le groupe ? », or « Remplacer le titre du groupe ? » when its name is not known. */
export const replaceQuestion = (current) => (current ? `Remplacer « ${current} » pour tout le groupe ?` : 'Remplacer le titre du groupe ?');
/** « à 42 min », « à 1 h 05 », « à 30 s »: where the group starts a title Lecture resumes. */
export function atText(seconds) {
  const s = Math.max(0, Math.floor(seconds)), m = Math.floor(s / 60);
  if (m < 1) return `à ${s} s`;
  return m < 60 ? `à ${m} min` : `à ${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')}`;
}
/**
 * Lecture's label in a group, which starts the title for everyone: « Lancer pour le groupe », « Reprendre pour le
 * groupe à 42 min » (from this account's progress), « Lancer S1 · É3 pour le groupe » (`code`: the episode, as the
 * page writes it), « Regarder avec le groupe » on the group's own title (joined where it is). Together.GroupPlayLabel.
 */
export function groupPlayLabel({ code = '', resumeAt = 0, groupTitle = false } = {}) {
  if (groupTitle) return 'Regarder avec le groupe';
  return [resumeAt > 0 ? 'Reprendre' : 'Lancer', code, 'pour le groupe', resumeAt > 0 && !code ? atText(resumeAt) : ''].filter(Boolean).join(' ');
}

/**
 * The link that opens Mira web on the group: the friend signs in with an account of this Jellyfin, then is in it. Its
 * host (who made the group) keeps it valid when the group is made again, or not open yet (findGroup).
 */
export const inviteLink = (server, groupId, host) =>
  `${String(server).replace(/\/+$/, '')}/Mira/#/ensemble/${encodeURIComponent(groupId)}${host ? `?hote=${encodeURIComponent(host)}` : ''}`;

/** The same invitation for Mira on a computer (« Ouvrir dans Mira »): Mira for Windows or Mac offers to join the group. */
export const appLink = (server, groupId, host) =>
  `mira://ensemble/${encodeURIComponent(groupId)}?server=${encodeURIComponent(String(server).replace(/\/+$/, ''))}${host ? `&hote=${encodeURIComponent(host)}` : ''}`;

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
    'Ton ami ouvre ce lien sur son téléphone ou son ordinateur et se connecte avec son propre compte Jellyfin : il est alors dans le groupe. S’il a Mira sur son ordinateur, « Ouvrir dans Mira » l’y fait rejoindre.',
    'Pas encore de compte ? Crée-le-lui dans Mira pour Windows : Réglages → Jellyfin → Ajouter un ami, avec seulement les bibliothèques que tu choisis (ou dans Jellyfin : tableau de bord → Utilisateurs → +).',
  ];
  const where = reach(host);
  if (where === 'tailnet') steps.push('Hors de chez toi, il lui faut Tailscale : partage-lui l’ordinateur de Jellyfin (login.tailscale.com → Machines → … → Share), il accepte avec son compte Tailscale gratuit.');
  else if (where === 'home') steps.push('Ce lien ne marche que sur ton Wi-Fi. Pour un ami ailleurs, installe Tailscale sur l’ordinateur de Jellyfin (Mira pour Windows : Guide → Mira sur ton téléphone).');
  steps.push('Quand il est là, le groupe compte une personne de plus : lance le titre, il démarre chez vous deux au même moment.');
  return steps;
}
