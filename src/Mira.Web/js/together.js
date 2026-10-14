// « Regarder ensemble »: Jellyfin's SyncPlay. A group on the server plays one title at the same moment on every device
// in it (Mira web, Mira for Windows, Jellyfin's own pages): Lecture puts a title on for the group, and pause, play and
// seeks go to Jellyfin, which tells each device what to do and when, on its clock. One group at a time, kept while Mira
// is open and through a reload of the page (the group's id in `temp`), left from the title page or the player's menu.
// Its messages come through Jellyfin's WebSocket (remote.js), open while in a group.
import { api } from './api.js';
import { listen } from './remote.js';
import { session, temp, signedOut } from './session.js';
import { toast } from './components.js';
import { clockOffset, commandPlan, queueItem, groupName, serverTime } from './player/sync.js';

const KEY = 'mira.together';
const events = new EventTarget();
let group = null;        // Jellyfin's group: { GroupId, GroupName, Participants, State }
let socket = null;       // listen() while in a group (or joining one)
let offset = 0;          // Jellyfin's clock minus this device's, in ms
let clockTimer = 0;
let queue = null;        // the group's title: { itemId, playlistItemId, startTicks }
let player = null;       // the player's hooks while it shows the group's title
let joined = null;       // resolves the GroupJoined a create or a join waits for
let away = false;        // the player closed here: Jellyfin is told not to wait for this device
let restored = false;

const serverNow = () => new Date(Date.now() + offset).toISOString();
const changed = () => events.dispatchEvent(new Event('change'));
function remember() {
  if (group) temp.set(KEY, JSON.stringify({ user: session.current?.userId, id: group.GroupId }));
  else temp.remove(KEY);
}

async function syncClock() {
  const samples = [];
  for (let i = 0; i < 3; i++) {
    const sent = Date.now();
    try {
      const answer = await api.utcTime();
      samples.push({ sent, received: Date.now(), serverIn: serverTime(answer?.RequestReceptionTime), serverOut: serverTime(answer?.ResponseTransmissionTime) });
    } catch { break; }
  }
  const best = clockOffset(samples);
  if (!best || !group) return;
  offset = best.offset;
  // Jellyfin waits for the slowest device of the group before it says when to play.
  api.syncSend('Ping', { Ping: Math.max(1, best.trip) }).catch(() => {});
}

/** The socket open, then `then`: Jellyfin's answer (GroupJoined) comes through it. */
function connect() {
  return new Promise((resolve) => {
    if (socket) { resolve(); return; }
    let done = false;
    const ready = () => { if (!done) { done = true; resolve(); } };
    socket = listen({
      message,
      opened: () => {
        ready();
        // Opened again after a break (Wi-Fi to 4G): Jellyfin may have let the session go, with its place in the group.
        if (group) api.syncJoin(group.GroupId).catch(() => {});
      },
    });
    setTimeout(ready, 6000);
  });
}
/** Out of the group here: the socket closes unless the player still listens to it. */
function gone(text = '') {
  const was = group;
  group = null; queue = null; offset = 0; away = false;
  clearInterval(clockTimer); clockTimer = 0;
  socket?.close(); socket = null;
  remember();
  if (was) { player?.left(); changed(); }
  if (text) toast(text);
}

function message(m) {
  if (m.MessageType === 'SyncPlayGroupUpdate') groupUpdate(m.Data ?? {});
  else if (m.MessageType === 'SyncPlayCommand') command(m.Data ?? {});
}
function groupUpdate({ Type: type, Data: data }) {
  switch (type) {
    case 'GroupJoined':
      group = { ...data, Participants: [...(data?.Participants ?? [])] };
      remember(); changed();
      syncClock();
      clearInterval(clockTimer); clockTimer = setInterval(syncClock, 60_000);
      joined?.(); joined = null;
      break;
    case 'UserJoined': case 'UserLeft':
      if (!group) return;
      toast(`${data} ${type === 'UserJoined' ? 'a rejoint' : 'a quitté'} le groupe.`);
      // Jellyfin names each account once, whatever its devices: its own list, rather than a count kept here.
      api.syncGroup(group.GroupId).then((info) => {
        if (!group || info?.GroupId !== group.GroupId) return;
        group.Participants = [...(info.Participants ?? [])];
        changed();
      }).catch(() => {});
      break;
    case 'GroupLeft': gone(); break;
    case 'NotInGroup': case 'GroupDoesNotExist': gone(group ? 'Le groupe n’existe plus.' : ''); break;
    case 'StateUpdate': if (group) { group.State = data?.State ?? group.State; changed(); } break;
    case 'LibraryAccessDenied': toast('Un membre du groupe n’a pas accès à ce titre dans Jellyfin.'); break;
    case 'PlayQueue': playQueue(data); break;
    default: break;
  }
}
function playQueue(data) {
  const next = queueItem(data);
  if (!next) { queue = null; return; }
  // Shuffle, repeat or the list changed around the same title: nothing to load again.
  if (queue?.playlistItemId === next.playlistItemId) return;
  queue = next;
  setAway(false);
  // Loading: Jellyfin holds the others until this device says it is ready.
  report('Buffering', next.startTicks / 1e7, false);
  if (player?.itemId() === next.itemId) player.prepare(next.startTicks);
  else if (player) player.switchTo(next.itemId, next.startTicks);
  else location.hash = `#/lecture/${next.itemId}?ensemble=1`;
}
function command(data) {
  // The Stop Jellyfin sends on joining names no title; a command for another title is not for this one.
  if (!player || !queue || data.PlaylistItemId !== queue.playlistItemId) return;
  player.command(commandPlan(data, { offset, now: Date.now() }));
}
/** Out of the player, the group plays on: without this, Jellyfin would hold every seek until this device is ready. */
function setAway(value) {
  if (!group || away === value) return;
  away = value;
  api.syncSend('SetIgnoreWait', { IgnoreWait: value }).catch(() => {});
}
function report(what, seconds, playing) {
  if (!group || !queue) return;
  api.syncSend(what, { When: serverNow(), PositionTicks: Math.max(0, Math.round(seconds * 1e7)), IsPlaying: !!playing, PlaylistItemId: queue.playlistItemId }).catch(() => {});
}
async function enter(start) {
  await connect();
  const wait = new Promise((resolve) => { joined = resolve; setTimeout(resolve, 6000); });
  try { await start(); } catch (error) { joined = null; if (!group) gone(); throw error; }
  await wait;
  if (!group) { gone(); throw new Error('Jellyfin n’a pas répondu. Réessaie dans un instant.'); }
}

// Signed out: the group is the account's (Jellyfin drops the session from it).
signedOut.addEventListener('out', () => gone());

export const together = {
  get group() { return group; },
  get queue() { return queue; },
  /** `fn` at each change of the group (joined, left, someone came or went); returns what stops it. */
  watch(fn) { events.addEventListener('change', fn); return () => events.removeEventListener('change', fn); },
  groups: () => api.syncGroups(),
  create: () => enter(() => api.syncNew(groupName(session.current?.userName ?? 'Mira'))),
  join: (id) => enter(() => api.syncJoin(id)),
  async leave() {
    try { await api.syncLeave(); } catch { /* left here anyway */ }
    gone();
  },
  /** The group this page was in before a reload, joined again (once). */
  restore() {
    if (restored || group) return;
    restored = true;
    let kept = null;
    try { kept = JSON.parse(temp.get(KEY) || 'null'); } catch { /* none */ }
    if (!kept?.id || kept.user !== session.current?.userId) { temp.remove(KEY); return; }
    enter(() => api.syncJoin(kept.id)).catch(() => temp.remove(KEY));
  },
  /**
   * The player's link to the group, while it is open: `hooks` = { itemId(), prepare(startTicks), switchTo(id,
   * startTicks), command(plan), left() }. What it returns asks the group: start a title, pause, play, seek; and tells
   * it this device's state: buffering, ready.
   */
  bind(hooks) {
    player = hooks;
    setAway(false);
    return {
      start(itemId, startTicks) {
        if (!group) return;
        // Every device reaches the end of an episode: the next one is put on once.
        if (queue?.itemId === itemId && hooks.itemId() !== itemId) return;
        api.syncQueue(itemId, startTicks).catch((error) => toast(error.message));
      },
      pause: () => { if (group) api.syncSend('Pause').catch(() => {}); },
      unpause: () => { if (group) api.syncSend('Unpause').catch(() => {}); },
      seek: (seconds) => { if (group) api.syncSend('Seek', { PositionTicks: Math.max(0, Math.round(seconds * 1e7)) }).catch(() => {}); },
      buffering: (seconds, playing) => report('Buffering', seconds, playing),
      ready: (seconds, playing) => report('Ready', seconds, playing),
      unbind() { if (player === hooks) { player = null; setAway(true); } },
    };
  },
};
