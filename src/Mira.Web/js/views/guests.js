// « Ta party » (from Réglages or « Inviter quelqu'un »): this account's party link, made again when it went too far,
// the guests it let in (each taken back with a tap: out of the group at once, their account deleted from Jellyfin),
// and, for an administrator, the address on the Internet the links start with (Tailscale Funnel). Links of one device
// made before party links stay listed until they go.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { addressForm, linkState, mayInvite } from '../guests.js';
import { partyBox, partyReach } from '../invite.js';

export function create() {
  const el = h('div', { class: 'view guests-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture du lien…'));
  const body = h('div', {});
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Ta party')), status),
    body);
  let version = 0;

  async function load() {
    const mine = ++version;
    try {
      const admin = await mayInvite();
      const [party, listing] = await Promise.all([api.party(), admin ? api.invites() : null]);
      if (mine === version) render(party, listing, admin);
    } catch (error) {
      if (mine !== version) return;
      status.textContent = '';
      body.replaceChildren(errorState(error, load));
    }
  }

  function render(party, listing, admin) {
    const guests = (listing?.invites ?? []).filter((x) => x.party);
    const single = (listing?.invites ?? []).filter((x) => !x.party);
    status.textContent = tr('Un seul lien pour regarder ensemble, le même à chaque fois : envoie-le à qui tu veux.');
    const parts = [
      h('h2', { class: 'label group-title' }, tr('Ton lien')),
      h('div', { class: 'group' }, h('div', { class: 'party-card' }, partyBox(party.link), partyReach(party, false),
        h('div', { class: 'invite-actions' }, h('button', { class: 'btn small quiet', on: { click: confirmNew } }, tr('Faire un nouveau lien'))))),
    ];
    if (admin) {
      const listed = guests;
      parts.push(h('h2', { class: 'label group-title' }, listed.length ? trn(listed.length, '{n} invité', '{n} invités') : tr('Invités')),
        h('div', { class: 'group' }, listed.length
          ? listed.map((invite) => h('div', { class: 'item static' }, icon('users'),
            h('span', { class: 'grow' }, invite.name, h('span', { class: 'sub' }, linkState(invite))),
            h('button', { class: 'btn small', 'aria-label': tr('Retirer {name}', { name: invite.name }), on: { click: () => confirmRevoke(invite) } }, tr('Retirer'))))
          : h('div', { class: 'item static' }, icon('users'), h('span', { class: 'grow' }, tr('Personne pour l’instant'), h('span', { class: 'sub' }, tr('Chaque appareil qui ouvre ton lien sans compte ici y apparaît, avec le prénom donné.'))))));
      if (single.length) {
        parts.push(h('h2', { class: 'label group-title' }, tr('Anciens liens')), h('div', { class: 'group' }, single.map((invite) => h('div', { class: 'item static' },
          icon(invite.opened ? 'users' : 'link'),
          h('span', { class: 'grow' }, invite.name, h('span', { class: 'sub' }, linkState(invite))),
          h('button', { class: 'btn small', 'aria-label': tr('Retirer le lien de {name}', { name: invite.name }), on: { click: () => confirmRevoke(invite) } }, tr('Retirer'))))));
      }
      const address = listing?.address, tunnel = listing?.tunnel;
      parts.push(h('h2', { class: 'label group-title' }, tr('Depuis Internet')), h('div', { class: 'group' }, address
        ? h('div', { class: 'item static' },
          icon('server'), h('span', { class: 'grow guests-address' }, address, tunnel?.text ? h('span', { class: `sub guests-tunnel ${tunnel.state}` }, tunnel.text) : null),
          h('button', { class: 'btn small', 'aria-label': tr('Changer l’adresse des liens'), on: { click: () => sheet({ title: tr('Adresse des liens'), body: addressForm(address, () => { load(); toast(tr('Adresse enregistrée.')); }) }) } }, tr('Changer')))
        : addressForm(null, () => load())));
    }
    body.replaceChildren(...parts);
  }

  function confirmNew() {
    sheet({
      title: tr('Faire un nouveau lien ?'),
      body: h('p', { class: 'now-confirm' }, tr('L’ancien lien ne marchera plus, et ceux qui sont entrés sans compte sortent du groupe : leur compte est supprimé de Jellyfin. Tes amis qui ont un compte ici ne perdent rien.')),
      items: [{ label: tr('Faire un nouveau lien'), danger: true, run: async () => {
        try { await api.newParty(); toast(tr('Nouveau lien prêt : l’ancien ne marche plus.')); } catch (error) { toast(error.message, { tone: 'error' }); }
        load();
      } }],
    });
  }
  function confirmRevoke(invite) {
    sheet({
      title: tr('Retirer {name} ?', { name: invite.name }),
      body: h('p', { class: 'now-confirm' }, tr('{name} sort du groupe tout de suite. Son compte est supprimé de Jellyfin ; s’il rouvre ton lien, il entre de nouveau.', { name: invite.name })),
      items: [{ label: tr('Retirer'), danger: true, run: () => revoke(invite) }],
    });
  }
  async function revoke(invite) {
    try { await api.revokeInvite(invite.id); toast(tr('{name} retiré.', { name: invite.name })); } catch (error) { if (error.status !== 404) toast(error.message, { tone: 'error' }); }
    load();
  }

  load();
  return { el, title: tr('Ta party'), refresh: () => load() };
}
