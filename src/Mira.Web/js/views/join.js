// An invitation to « Regarder ensemble » opened (invite.js, #/ensemble/<id>): signed in first (app.js keeps the link
// through the sign-in), Mira joins the group, then says to wait here: the player opens on its own once the group
// starts a title (together.js).
import { h, icon, clear } from '../dom.js';
import { markSvg, replaceRoute } from '../app.js';
import { together } from '../together.js';
import { groupText } from '../player/sync.js';

const NO_SYNCPLAY = 'Jellyfin ne permet pas à ton compte de regarder ensemble. Demande à la personne qui t’invite de l’autoriser : tableau de bord → Utilisateurs → ton compte → SyncPlay.';
// Jellyfin writes a group's id with or without dashes.
const sameId = (a, b) => String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();

export function create({ id }) {
  const el = h('div', { class: 'view gate' });
  let unwatch = () => {};

  const page = (title, lead, ...rest) => clear(el).append(markSvg(56),
    h('div', { class: 'gate-text' }, h('h1', {}, title), lead ? h('p', { class: 'lead' }, lead) : ''), ...rest);
  const home = () => h('button', { class: 'btn primary', on: { click: () => replaceRoute('#/') } }, 'Aller à l’accueil');

  function joined() {
    const group = together.group;
    if (!group || !sameId(group.GroupId, id)) { failed('Tu n’es plus dans ce groupe.'); return; }
    page(`Tu es dans « ${group.GroupName} »`,
      'Le titre démarre ici dès qu’il est lancé pour le groupe, au même moment chez chacun. Tu peux parcourir Mira en attendant.',
      h('div', { class: 'hint' },
        h('div', { class: 'step' }, icon('users'), h('span', {}, h('b', {}, groupText(group)), group.Participants.length ? ` : ${group.Participants.join(', ')}` : ''))),
      home(),
      h('button', { class: 'btn quiet small', on: { click: async () => { await together.leave(); replaceRoute('#/'); } } }, 'Quitter le groupe'));
  }
  function failed(text) {
    unwatch(); unwatch = () => {};
    page('Le groupe n’a pas pu être rejoint', text, home());
  }

  async function join() {
    page('Tu rejoins le groupe…', '', h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    if (together.group && !sameId(together.group.GroupId, id)) await together.leave();
    if (!together.group) {
      let groups;
      try { groups = await together.groups(); } catch (error) { failed(error?.status === 403 ? NO_SYNCPLAY : error.message); return; }
      const found = (groups ?? []).find((g) => sameId(g.GroupId, id));
      if (!found) { failed('Ce groupe n’existe plus : demande un nouveau lien à la personne qui t’a invité.'); return; }
      try { await together.join(found.GroupId); } catch (error) { failed(error?.status === 403 ? NO_SYNCPLAY : error.message); return; }
    }
    unwatch = together.watch(joined);
    joined();
  }

  join();
  return { el, title: 'Regarder ensemble', dispose: () => unwatch() };
}
