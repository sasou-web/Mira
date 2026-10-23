// « Inviter par lien », the host's side (an administrator of Jellyfin): a link for someone without an account here, who
// opens it from anywhere (Tailscale Funnel) and watches with the host's group. Made from « Inviter quelqu'un » or from
// the screen of the links (views/guests.js), where they are taken back. The extension makes each link's account and
// deletes it with the link (Mira.Jellyfin InvitesController).
import { api } from './api.js';
import { h, icon } from './dom.js';
import { tr, dateText } from './i18n.js';
import { toast } from './components.js';
import { copy } from './views/share.js';

/** What a link does, said where one is made. */
export const LINK_RULES = tr('Le lien ouvre Mira chez ton invité, sans compte ni Tailscale, et le fait entrer dans ton groupe. Il ne sert qu’à un seul appareil, celui qui l’ouvre en premier. Tu peux le retirer à tout moment ; il s’efface seul après 7 jours sans servir.');
/** What the address of the links is, and how to have one. */
export const FUNNEL_COMMAND = 'tailscale funnel --bg --set-path=/Mira http://127.0.0.1:8096/Mira';

/** Whether this account may make links: an administrator of Jellyfin, who alone can make an account. */
let administrator = null;
export const mayInvite = () => (administrator ??= api.me().then((me) => !!me?.Policy?.IsAdministrator, () => { administrator = null; return false; }));

/** A link ready to send: the address, Copier, and Partager where the browser can share. */
export function linkBox(invite) {
  const text = tr('Regarde avec moi sur Mira : {link}', { link: invite.link });
  return h('div', { class: 'invite' },
    h('p', { class: 'invite-link' }, invite.link),
    h('div', { class: 'invite-actions' },
      h('button', {
        class: 'btn small primary',
        on: { click: async () => toast(await copy(invite.link) ? tr('Lien copié : envoie-le à {name}.', { name: invite.name }) : tr('Copie impossible ici : touche longuement le lien pour le sélectionner.')) },
      }, icon('link', { size: 16 }), tr('Copier le lien')),
      typeof navigator.share === 'function'
        ? h('button', { class: 'btn small', on: { click: () => navigator.share({ title: 'Mira', text }).catch(() => {}) } }, icon('share', { size: 16 }), tr('Partager'))
        : null));
}

/**
 * The address the links start with, when there is none yet (or to change it): what to run on the PC of Jellyfin, and
 * the field for what it shows. `saved(listing)` once Jellyfin has it.
 */
export function addressForm(current, saved) {
  const input = h('input', { class: 'input', type: 'url', inputmode: 'url', autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false', placeholder: 'https://pc.tailnet.ts.net', value: current ?? '', 'aria-label': tr('Adresse publique de Mira') });
  const said = h('p', { class: 'group-note', role: 'status' });
  const button = h('button', { class: 'btn small primary', on: { click: save } }, tr('Enregistrer'));
  async function save() {
    button.disabled = true; said.textContent = '';
    try { saved(await api.setInviteAddress(input.value.trim() || null)); } catch (error) { said.textContent = error.message; }
    button.disabled = false;
  }
  return h('div', { class: 'invite' },
    h('p', { class: 'invite-lead' }, tr('Pour qu’un lien marche depuis Internet, ouvre Mira au monde par Tailscale Funnel sur le PC de Jellyfin, dans PowerShell :')),
    h('p', { class: 'invite-link' }, FUNNEL_COMMAND),
    h('p', { class: 'invite-lead' }, tr('Funnel n’ouvre que Mira, jamais le reste de Jellyfin. Écris ici l’adresse qu’il affiche :')),
    h('div', { class: 'field' }, input), h('div', { class: 'invite-actions' }, button), said);
}

/**
 * Making a link: the guest's name (what the group shows of them), then the link to send. `made(invite)` once it is
 * made; `missing()` once Jellyfin has no address for the links (asked for again).
 */
export function newLinkForm({ made = () => {}, missing = () => {} } = {}) {
  const input = h('input', { class: 'input', type: 'text', autocomplete: 'off', maxlength: '40', placeholder: tr('Son prénom'), 'aria-label': tr('Prénom de ton invité') });
  const said = h('p', { class: 'group-note', role: 'status' });
  const button = h('button', { class: 'btn small primary', on: { click: create } }, icon('link', { size: 16 }), tr('Créer le lien'));
  const parts = [h('p', { class: 'invite-lead' }, LINK_RULES), h('div', { class: 'field' }, input), h('div', { class: 'invite-actions' }, button), said];
  const box = h('div', { class: 'invite' }, ...parts);
  function again() {
    input.value = ''; said.textContent = ''; button.disabled = false;
    box.replaceChildren(...parts);
    input.focus();
  }
  async function create() {
    button.disabled = true; said.textContent = tr('Création du compte de l’invité…');
    try {
      const invite = await api.invite(input.value.trim());
      if (!invite?.link) { said.textContent = ''; missing(); return; }
      box.replaceChildren(h('p', { class: 'invite-lead' }, tr('Envoie ce lien à {name} : il ouvre Mira et rejoint ton groupe.', { name: invite.name })), linkBox(invite),
        h('div', { class: 'invite-actions' }, h('button', { class: 'btn small quiet', on: { click: again } }, tr('Un autre lien'))));
      made(invite);
    } catch (error) { said.textContent = error.message; button.disabled = false; }
  }
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') create(); });
  return box;
}

/** Where a link is: not opened yet, or on which device and since when it served. */
export function linkState(invite, now = Date.now()) {
  const ends = dateText(new Date(invite.ends), { day: 'numeric', month: 'long' });
  if (!invite.opened) return tr('Pas encore ouvert · s’efface seul le {date}', { date: ends });
  const used = new Date(invite.used), device = invite.device || tr('un appareil');
  return now - used.getTime() < 24 * 3600_000
    ? tr('Ouvert sur {device} · a servi à {time} · s’efface seul après 7 jours sans servir', { device, time: dateText(used, { hour: '2-digit', minute: '2-digit' }) })
    : tr('Ouvert sur {device} · a servi le {date} · s’efface seul le {end} sans servir', { device, date: dateText(used, { day: 'numeric', month: 'long' }), end: ends });
}
