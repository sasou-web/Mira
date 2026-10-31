// « Inviter quelqu'un » in a group of « Regarder ensemble »: « Ta party », the one link of this account (the extension's
// PartyController), the same every evening, for anyone: a friend with Mira's app opens it there, anyone else in their
// browser, with or without an account here. Where the extension has no party links yet (an older one), the group's
// link as before (#/ensemble/<id>, views/join.js): an account of this Jellyfin, Tailscale away from home.
import { api, base, isTailnet } from './api.js';
import { h, icon } from './dom.js';
import { tr } from './i18n.js';
import { sheet, toast } from './components.js';
import { inviteLink, inviteSteps, groupHost, groupLabel } from './player/sync.js';
import { phoneAddress, copy } from './views/share.js';
import { mayInvite } from './guests.js';

/** This Jellyfin at an address a friend can open: this page's on Tailscale, else the home network's (never localhost). */
async function friendServer() {
  if (isTailnet(location.hostname)) return `${location.origin}${base}`;
  return (await phoneAddress()).replace(/\/Mira\/?$/i, '');
}

/** A link ready to send: the address, Copier, and Partager where the browser can share. */
export function partyBox(link) {
  const text = tr('Regarde avec moi sur Mira');
  return h('div', { class: 'invite-box' },
    h('p', { class: 'invite-link' }, link),
    h('div', { class: 'invite-actions' },
      h('button', {
        class: 'btn small primary',
        on: { click: async () => toast(await copy(link) ? tr('Lien copié : envoie-le à tes amis.') : tr('Copie impossible ici : touche longuement le lien pour le sélectionner.')) },
      }, icon('link', { size: 16 }), tr('Copier le lien')),
      typeof navigator.share === 'function'
        ? h('button', { class: 'btn small', on: { click: () => navigator.share({ title: 'Mira', text, url: link }).catch(() => {}) } }, icon('share', { size: 16 }), tr('Partager'))
        : null));
}

/** Where the party link works, said under it: everywhere, or what is missing for that. */
export function partyReach(party, admin) {
  if (party.reach === 'internet') return h('p', { class: 'invite-note' }, party.note);
  return h('p', { class: 'invite-note warn' }, party.note, admin ? [' ', h('a', { href: '#/invites' }, tr('Ouvrir à Internet'))] : null);
}

export async function inviteSheet(group) {
  if (!group) return;
  const party = await api.party().catch(() => null);
  if (party?.link) {
    const admin = await mayInvite();
    sheet({
      title: tr('Inviter quelqu’un'),
      body: h('div', { class: 'invite' },
        h('p', { class: 'invite-lead' }, tr('Ton lien de party, le même à chaque fois : qui l’ouvre rejoint ton groupe, dans l’app Mira ou dans son navigateur, sans compte à créer.')),
        partyBox(party.link),
        partyReach(party, admin)),
    });
    return;
  }
  const link = inviteLink(await friendServer(), group.GroupId, groupHost(group.GroupName));
  const name = groupLabel(group.GroupName);
  sheet({
    title: tr('Inviter quelqu’un'),
    body: h('div', { class: 'invite' },
      h('p', { class: 'invite-lead' }, tr('Envoie ce lien à ton ami pour qu’il rejoigne « {name} ».', { name })),
      partyBox(link),
      h('ol', { class: 'invite-steps' }, inviteSteps(link).map((step) => h('li', {}, step)))),
  });
}
