// « Inviter quelqu'un » in a group of « Regarder ensemble »: a link that opens Mira web on the group (#/ensemble/<id>,
// views/join.js), to send to a friend, with what the friend needs: an account of this Jellyfin, Tailscale away from home.
// It names the group's host, so that it still finds the group once made again (together.js), or waits for it.
import { base, isTailnet } from './api.js';
import { h, icon } from './dom.js';
import { tr } from './i18n.js';
import { sheet, toast } from './components.js';
import { inviteLink, inviteSteps, groupHost, groupLabel } from './player/sync.js';
import { phoneAddress, copy } from './views/share.js';

/** This Jellyfin at an address a friend can open: this page's on Tailscale, else the home network's (never localhost). */
async function friendServer() {
  if (isTailnet(location.hostname)) return `${location.origin}${base}`;
  return (await phoneAddress()).replace(/\/Mira\/?$/i, '');
}

export async function inviteSheet(group) {
  if (!group) return;
  const link = inviteLink(await friendServer(), group.GroupId, groupHost(group.GroupName));
  const name = groupLabel(group.GroupName);
  const share = typeof navigator.share === 'function'
    ? h('button', { class: 'btn small', on: { click: () => navigator.share({ title: name, text: tr('Regarde avec moi sur Mira : {name}', { name }), url: link }).catch(() => {}) } },
      icon('share', { size: 16 }), tr('Partager'))
    : null;
  sheet({
    title: tr('Inviter quelqu’un'),
    body: h('div', { class: 'invite' },
      h('p', { class: 'invite-lead' }, tr('Envoie ce lien à ton ami pour qu’il rejoigne « {name} ».', { name })),
      h('p', { class: 'invite-link' }, link),
      h('div', { class: 'invite-actions' },
        h('button', {
          class: 'btn small primary',
          on: { click: async () => toast(await copy(link) ? tr('Lien copié : envoie-le à ton ami.') : tr('Copie impossible ici : touche longuement le lien pour le sélectionner.')) },
        }, icon('link', { size: 16 }), tr('Copier le lien')),
        share),
      h('ol', { class: 'invite-steps' }, inviteSteps(link).map((step) => h('li', {}, step)))),
  });
}
