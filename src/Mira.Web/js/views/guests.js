// « Liens d'invités » (Inviter par lien), from Réglages or « Inviter quelqu'un », for an administrator: the address the
// links start with, a new link, and the links made, each taken back with a tap (its guest is out at once, and their
// account deleted from Jellyfin).
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { addressForm, newLinkForm, linkBox, linkState } from '../guests.js';

export function create() {
  const el = h('div', { class: 'view guests-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture des liens…'));
  const body = h('div', {});
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Liens d’invités')), status),
    body);
  let version = 0;
  // The form of a new link stays across reloads of the list: the link just made stays shown, ready to send.
  let form = null;

  async function load() {
    const mine = ++version;
    try {
      const listing = await api.invites();
      if (mine === version) render(listing);
    } catch (error) {
      if (mine !== version) return;
      status.textContent = '';
      body.replaceChildren(errorState(error, load));
    }
  }

  function render({ address, invites = [] }) {
    status.textContent = invites.length ? trn(invites.length, '{n} lien en cours.', '{n} liens en cours.') : tr('Aucun lien en cours.');
    const parts = [];
    if (!address) {
      form = null;
      parts.push(h('h2', { class: 'label group-title' }, tr('Adresse des liens')), h('div', { class: 'group' }, addressForm(null, render)));
    } else {
      form ??= newLinkForm({ made: () => load(), missing: () => { form = null; load(); } });
      parts.push(h('h2', { class: 'label group-title' }, tr('Nouveau lien')), h('div', { class: 'group' }, form));
    }
    if (invites.length) {
      parts.push(h('h2', { class: 'label group-title' }, tr('Liens en cours')), h('div', { class: 'group' }, invites.map((invite) => h('div', { class: 'item static' },
        icon(invite.opened ? 'users' : 'link'),
        h('span', { class: 'grow' }, invite.name, h('span', { class: 'sub' }, linkState(invite))),
        invite.link && !invite.opened ? h('button', { class: 'btn small', 'aria-label': tr('Le lien de {name}', { name: invite.name }), on: { click: () => sheet({ title: invite.name, body: linkBox(invite) }) } }, tr('Lien')) : null,
        h('button', { class: 'btn small', 'aria-label': tr('Retirer le lien de {name}', { name: invite.name }), on: { click: () => confirmRevoke(invite) } }, tr('Retirer'))))));
    }
    if (address) {
      parts.push(h('h2', { class: 'label group-title' }, tr('Adresse des liens')), h('div', { class: 'group' }, h('div', { class: 'item static' },
        icon('server'), h('span', { class: 'grow guests-address' }, address),
        h('button', { class: 'btn small', 'aria-label': tr('Changer l’adresse des liens'), on: { click: () => sheet({ title: tr('Adresse des liens'), body: addressForm(address, (listing) => { render(listing); toast(tr('Adresse enregistrée.')); }) }) } }, tr('Changer')))));
    }
    body.replaceChildren(...parts);
  }

  function confirmRevoke(invite) {
    sheet({
      title: tr('Retirer ce lien ?'),
      body: h('p', { class: 'now-confirm' }, invite.opened
        ? tr('{name} sort du groupe tout de suite et ne pourra plus revenir avec ce lien. Son compte est supprimé de Jellyfin.', { name: invite.name })
        : tr('Le lien de {name} ne marchera plus. Son compte est supprimé de Jellyfin.', { name: invite.name })),
      items: [{ label: tr('Retirer le lien'), danger: true, run: () => revoke(invite) }],
    });
  }
  async function revoke(invite) {
    try { await api.revokeInvite(invite.id); toast(tr('Lien de {name} retiré.', { name: invite.name })); } catch (error) { if (error.status !== 404) toast(error.message); }
    load();
  }

  load();
  return { el, title: tr('Liens d’invités'), refresh: () => load() };
}
