// « Regarder ensemble »: Jellyfin's SyncPlay. A group on the server plays one title at the same moment on every device
// in it (Mira web, Mira for Windows, Jellyfin's own pages): Lecture puts a title on for the group, and pause, play and
// seeks go to Jellyfin, which tells each device what to do and when, on its clock. One group at a time, kept while Mira
// is open and through a reload of the page (the group's id in `temp`), left from the title page or the player's menu.
// Its messages come through Jellyfin's WebSocket (remote.js), open while in a group. Jellyfin lets a device go with its
// socket (an iPhone gone to Messages to send the invitation): back, it joins the group again, or makes it again under
// the same name when it made it and the group ended empty, so that the invitation sent still finds it (views/join.js).
// Lecture of another title than the one the group has on asks first (askFirst).
import { api, isGuest } from './api.js';
import { listen } from './remote.js';
import { session, temp, signedOut } from './session.js';
import { h } from './dom.js';
import { tr } from './i18n.js';
import { toast, sheet } from './components.js';
import { clockOffset, commandPlan, queueItem, groupName, serverTime, rejoinPlan, sameGroup, memberText, asksToReplace, titleName, replaceQuestion } from './player/sync.js';

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
let made = false;        // the group was made here: made again if Jellyfin ends it (rejoinPlan)
let recovering = null;   // going back to the group after Jellyfin let this device go, once at a time
let rejoined = false;    // back in the group: Jellyfin sends its title again, and holds the others until this device is ready

const serverNow = () => new Date(Date.now() + offset).toISOString();
const changed = () => events.dispatchEvent(new Event('change'));
function remember() {
  if (group) temp.set(KEY, JSON.stringify({ user: session.current?.userId, id: group.GroupId, name: group.GroupName, made }));
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
  send('Ping', { Ping: Math.max(1, best.trip) }).catch(() => {});
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
        if (group) recover();
      },
    });
    setTimeout(ready, 6000);
  });
}
/** Out of the group here: the socket closes unless the player still listens to it. */
function gone(text = '') {
  const was = group;
  group = null; queue = null; offset = 0; away = false; made = false; rejoined = false;
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
    case 'GroupJoined': {
      // The same group again (back after a break): its title stays until Jellyfin sends it again (playQueue). Jellyfin
      // takes this device as a newcomer, which it does not tell to wait or not: said again with the title.
      const again = !!group && sameGroup(group.GroupId, data?.GroupId);
      group = { ...data, Participants: [...(data?.Participants ?? [])] };
      if (!again) queue = null;
      rejoined = again && !!queue; away = false;
      remember(); changed();
      syncClock();
      clearInterval(clockTimer); clockTimer = setInterval(syncClock, 60_000);
      joined?.(); joined = null;
      break;
    }
    case 'UserJoined': case 'UserLeft': {
      if (!group) return;
      const said = memberText(data, type === 'UserJoined');
      // In the player, its pill says it (player/group-pill.js).
      if (!player) toast(`${said}.`);
      events.dispatchEvent(new CustomEvent('member', { detail: said }));
      // Jellyfin names each account once, whatever its devices: its own list, rather than a count kept here.
      api.syncGroup(group.GroupId).then((info) => {
        if (!group || info?.GroupId !== group.GroupId) return;
        group.Participants = [...(info.Participants ?? [])];
        changed();
      }).catch(() => {});
      break;
    }
    case 'GroupLeft': gone(); break;
    // Jellyfin let this device go (its session ended): back in the group, or out of it when there is none to go back to.
    case 'NotInGroup': case 'GroupDoesNotExist': if (group) recover(); else gone(); break;
    case 'StateUpdate': if (group) { group.State = data?.State ?? group.State; changed(); } break;
    case 'LibraryAccessDenied': toast(tr('Un membre du groupe n’a pas accès à ce titre dans Jellyfin.')); break;
    case 'PlayQueue': playQueue(data); break;
    default: break;
  }
}
function playQueue(data) {
  const next = queueItem(data);
  if (!next) { if (queue) { queue = null; changed(); } return; }
  const same = queue?.playlistItemId === next.playlistItemId;
  // Shuffle, repeat or the list changed around the same title: nothing to load again. Unless this device is back in the
  // group: Jellyfin holds the others until it is ready there, or told that its player is closed.
  if (same && !rejoined) return;
  rejoined = false;
  queue = next;
  // Lecture's label on the title page says whether it joins the group's title or replaces it.
  if (!same) changed();
  if (same && !player) { setAway(true); return; }
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
  send('SetIgnoreWait', { IgnoreWait: value }).catch(() => {});
}
/**
 * What this device tells the group, one request at a time, in order: two at once may reach Jellyfin crossed, and a
 * Buffering taken after the Ready that follows it holds the whole group for this device (for good on Jellyfin 10.x,
 * 30 s on 12.x). A request that fails lets the next one go.
 */
let sending = Promise.resolve();
function send(what, body) {
  const sent = sending.then(() => api.syncSend(what, body));
  sending = sent.catch(() => {});
  return sent;
}
function report(what, seconds, playing) {
  if (!group || !queue) return;
  send(what, { When: serverNow(), PositionTicks: Math.max(0, Math.round(seconds * 1e7)), IsPlaying: !!playing, PlaylistItemId: queue.playlistItemId }).catch(() => {});
}
/**
 * Back in the group after Jellyfin let this device go (its session ended with the socket): joined again where it is,
 * or made again under its name when this device made it and it ended empty (rejoinPlan). Out of it when there is none
 * to go back to, or when Jellyfin twice says nothing; still in it while Jellyfin cannot be reached (the socket's next
 * opening tries again).
 */
function recover() {
  if (!group || recovering) return;
  const lost = group;
  recovering = (async () => {
    for (let attempt = 0; attempt < 2; attempt++) {
      let plan;
      try { plan = rejoinPlan(await api.syncGroups(), lost, made); } catch { return; }
      if (group !== lost) return;
      if (!plan) { gone(tr('Le groupe n’existe plus.')); return; }
      const answer = new Promise((resolve) => { joined = resolve; setTimeout(resolve, 6000); });
      try { await (plan.join ? api.syncJoin(plan.join) : api.syncNew(plan.create)); } catch { joined = null; return; }
      await answer;
      if (group !== lost) return;
    }
    gone(tr('Le groupe n’existe plus.'));
  })().finally(() => { recovering = null; });
}
async function enter(start) {
  await connect();
  const wait = new Promise((resolve) => { joined = resolve; setTimeout(resolve, 6000); });
  try { await start(); } catch (error) { joined = null; if (!group) gone(); throw error; }
  await wait;
  if (!group) { gone(); throw new Error(tr('Jellyfin n’a pas répondu. Réessaie dans un instant.')); }
}

// Signed out: the group is the account's (Jellyfin drops the session from it).
signedOut.addEventListener('out', () => gone());

/** The title a player link opens (#/lecture/<id>?…). */
const linkedItem = (href) => decodeURIComponent(/^#\/lecture\/([^?]+)/.exec(href ?? '')?.[1] ?? '');
/**
 * Lecture of another title than the one the group has on, which would replace it for everyone (asksToReplace): a sheet
 * asks first, naming that title, and offers to join it instead. A tap on a choice is a gesture, from which Apple's
 * player may open. True when it asks.
 */
function askFirst(href) {
  const itemId = linkedItem(href);
  if (!itemId || !asksToReplace(group, queue, itemId)) return false;
  const current = queue;
  api.item(current.itemId).then(titleName, () => '').then((name) => {
    // The group moved on meanwhile (another title, or none): asked again on what it has now.
    if (queue !== current) { if (!askFirst(href)) location.hash = href; return; }
    sheet({
      title: tr('Regarder ensemble'),
      body: h('div', {}, h('p', { class: 'sheet-question' }, replaceQuestion(name)),
        h('p', { class: 'sheet-note' }, tr('Le groupe le regarde en ce moment. Lecture le remplace chez chacun, au même moment.'))),
      items: [
        { symbol: 'play', label: tr('Remplacer pour le groupe'), run: () => { location.hash = href; } },
        { symbol: 'users', label: tr('Regarder avec le groupe'), sub: name ? tr('« {title} », là où il en est.', { title: name }) : tr('Là où il en est.'), run: () => { location.hash = `#/lecture/${encodeURIComponent(current.itemId)}`; } },
      ],
    });
  });
  return true;
}
// Lecture through a link (a title page, a card) or a menu (components.js openPlayer).
document.addEventListener('click', (event) => {
  if (event.defaultPrevented || event.button || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  const link = event.target instanceof Element ? event.target.closest('a[href^="#/lecture/"]') : null;
  if (link && askFirst(link.getAttribute('href'))) event.preventDefault();
});
addEventListener('mira-play', (event) => { if (askFirst(event.detail)) event.preventDefault(); });

export const together = {
  get group() { return group; },
  get queue() { return queue; },
  /** Lecture of `itemId` would replace the title the group has on (and asks first). */
  replaces: (itemId) => asksToReplace(group, queue, itemId),
  /** `fn` at each change of the group (joined, left, someone came or went); returns what stops it. */
  watch(fn) { events.addEventListener('change', fn); return () => events.removeEventListener('change', fn); },
  /** `fn(words)` when someone joins or leaves the group (« Marc a rejoint le groupe »); returns what stops it. */
  onMember(fn) { const f = (e) => fn(e.detail); events.addEventListener('member', f); return () => events.removeEventListener('member', f); },
  groups: () => api.syncGroups(),
  create: () => { made = true; return enter(() => api.syncNew(groupName(session.current?.userName ?? 'Mira'))); },
  join: (id) => { made = false; return enter(() => api.syncJoin(id)); },
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
    made = !!kept.made;
    enter(async () => {
      const plan = rejoinPlan(await api.syncGroups(), { GroupId: kept.id, GroupName: kept.name ?? '' }, made);
      if (!plan) throw new Error(tr('Le groupe n’existe plus.'));
      await (plan.join ? api.syncJoin(plan.join) : api.syncNew(plan.create));
    }).catch(() => temp.remove(KEY));
  },
  /**
   * The player's link to the group, while it is open: `hooks` = { itemId(), position(), prepare(startTicks), switchTo(id,
   * startTicks), command(plan), left() }. What it returns asks the group: start a title, pause, play, seek; and tells
   * it this device's state: buffering, ready.
   */
  bind(hooks) {
    player = hooks;
    setAway(false);
    return {
      start(itemId, startTicks) {
        // A guest of « Inviter par lien » puts nothing on: the host's devices do (the gateway refuses the queue).
        if (!group || isGuest()) return;
        // Every device reaches the end of an episode: the next one is put on once.
        if (queue?.itemId === itemId && hooks.itemId() !== itemId) return;
        api.syncQueue(itemId, startTicks).catch((error) => toast(error.message));
      },
      pause: () => { if (group) send('Pause').catch(() => {}); },
      unpause: () => {
        if (!group) return;
        // The group made again has no title (Jellyfin ended the old one with it): the one here is put on, from here.
        if (!queue && hooks.itemId() && !isGuest()) { api.syncQueue(hooks.itemId(), Math.round(hooks.position() * 1e7)).catch((error) => toast(error.message)); return; }
        send('Unpause').catch(() => {});
      },
      seek: (seconds) => { if (group) send('Seek', { PositionTicks: Math.max(0, Math.round(seconds * 1e7)) }).catch(() => {}); },
      buffering: (seconds, playing) => report('Buffering', seconds, playing),
      ready: (seconds, playing) => report('Ready', seconds, playing),
      unbind() { if (player === hooks) { player = null; setAway(true); } },
    };
  },
};
