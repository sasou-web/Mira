// An invitation to « Regarder ensemble » opened (invite.js, #/ensemble/<id>?hote=<who>): signed in first (app.js keeps
// the link through the sign-in), Mira joins the group, then says to wait here: the player opens on its own once the
// group starts a title (together.js). The group of the link gone, its host's group made again since is joined
// (findGroup); none open yet, the page says so and joins it as soon as it opens.
import { h, icon, clear } from '../dom.js';
import { tr } from '../i18n.js';
import { markSvg, replaceRoute } from '../app.js';
import { together } from '../together.js';
import { findGroup, groupText, waitingText, WAITING_LEAD, groupLabel } from '../player/sync.js';

const NO_SYNCPLAY = tr('Jellyfin ne permet pas à ton compte de regarder ensemble. Demande à la personne qui t’invite de l’autoriser : tableau de bord → Utilisateurs → ton compte → SyncPlay.');
// How often Jellyfin's groups are read while the host has none open, the page in sight.
const EVERY = 4000;

export function create({ id, query }) {
  const host = query?.get('hote') || null;
  const el = h('div', { class: 'view gate' });
  let unwatch = () => {};
  let timer = 0, disposed = false, waited = false, looking = false;
  // The group of this page: the link's, or the one its host made again.
  let target = id;

  const page = (title, lead, ...rest) => clear(el).append(markSvg(56),
    h('div', { class: 'gate-text' }, h('h1', {}, title), lead ? h('p', { class: 'lead' }, lead) : ''), ...rest);
  const home = () => h('button', { class: 'btn primary', on: { click: () => replaceRoute('#/') } }, tr('Aller à l’accueil'));
  const spinner = (label) => h('div', { class: 'spinner', role: 'status', 'aria-label': label });

  function joined() {
    const group = together.group;
    if (!group || !findGroup([group], target, host)) { failed(tr('Tu n’es plus dans ce groupe.')); return; }
    page(tr('Tu es dans « {name} »', { name: groupLabel(group.GroupName) }),
      tr('Le titre démarre ici dès qu’il est lancé pour le groupe, au même moment chez chacun. Tu peux parcourir Mira en attendant.'),
      h('div', { class: 'hint' },
        h('div', { class: 'step' }, icon('users'), h('span', {}, h('b', {}, groupText(group)), group.Participants.length ? tr(' : {names}', { names: group.Participants.join(', ') }) : ''))),
      home(),
      h('button', { class: 'btn quiet small', on: { click: async () => { await together.leave(); replaceRoute('#/'); } } }, tr('Quitter le groupe')));
  }
  function failed(text) {
    stopWaiting();
    unwatch(); unwatch = () => {};
    page(tr('Le groupe n’a pas pu être rejoint'), text, home());
  }

  async function enterGroup(found) {
    page(tr('Tu rejoins le groupe…'), '', spinner(tr('Chargement')));
    try { await together.join(found.GroupId); } catch (error) { if (!disposed) failed(error?.status === 403 ? NO_SYNCPLAY : error.message); return; }
    if (disposed) return;
    target = together.group?.GroupId ?? found.GroupId;
    unwatch = together.watch(joined);
    joined();
  }

  /** The host has no group open yet (or Jellyfin ended it while the host's phone was away): read again until it opens. */
  function waiting() {
    if (!waited) {
      waited = true;
      page(waitingText(host), WAITING_LEAD, spinner(tr('En attente du groupe')), home());
      document.addEventListener('visibilitychange', look);
    }
    clearTimeout(timer);
    timer = setTimeout(look, EVERY);
  }
  function stopWaiting() {
    clearTimeout(timer);
    document.removeEventListener('visibilitychange', look);
  }
  async function look() {
    if (disposed || looking) return;
    clearTimeout(timer);
    // A phone locked or another app: read again once Mira is in sight.
    if (document.hidden) return;
    let groups;
    looking = true;
    try { groups = await together.groups(); } catch (error) {
      if (error?.status === 403) failed(NO_SYNCPLAY); else if (!disposed) waiting();
      return;
    } finally { looking = false; }
    if (disposed) return;
    const found = findGroup(groups, id, host);
    if (!found) { waiting(); return; }
    stopWaiting();
    enterGroup(found);
  }

  async function join() {
    page(tr('Tu rejoins le groupe…'), '', spinner(tr('Chargement')));
    const current = together.group;
    if (current && !findGroup([current], id, host)) await together.leave();
    if (together.group) {
      target = together.group.GroupId;
      unwatch = together.watch(joined);
      joined();
      return;
    }
    let groups;
    try { groups = await together.groups(); } catch (error) { failed(error?.status === 403 ? NO_SYNCPLAY : error.message); return; }
    if (disposed) return;
    const found = findGroup(groups, id, host);
    if (found) enterGroup(found);
    else if (host) waiting();
    else failed(tr('Ce groupe n’existe plus : demande un nouveau lien à la personne qui t’a invité.'));
  }

  join();
  return { el, title: tr('Regarder ensemble'), dispose: () => { disposed = true; stopWaiting(); unwatch(); } };
}
