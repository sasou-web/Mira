// The guests of « Ta party » and « Inviter par lien », the host's side (an administrator of Jellyfin): who someone
// without an account here is once in (views/guests.js, where they are taken back), and the address on the Internet the
// links start with (Tailscale Funnel). The extension makes each guest's account and deletes it with them.
import { api } from './api.js';
import { h } from './dom.js';
import { tr, dateText } from './i18n.js';

/** What the address of the links is, and how to have one. */
export const FUNNEL_COMMAND = 'tailscale funnel --bg --set-path=/Mira http://127.0.0.1:8096/Mira';

/** Whether this account may make links: an administrator of Jellyfin, who alone can make an account. */
let administrator = null;
export const mayInvite = () => (administrator ??= api.me().then((me) => !!me?.Policy?.IsAdministrator, () => { administrator = null; return false; }));

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

/** Where a link is: not opened yet, or on which device and since when it served. */
export function linkState(invite, now = Date.now()) {
  const ends = dateText(new Date(invite.ends), { day: 'numeric', month: 'long' });
  if (!invite.opened) return tr('Pas encore ouvert · s’efface seul le {date}', { date: ends });
  const used = new Date(invite.used), device = invite.device || tr('un appareil');
  return now - used.getTime() < 24 * 3600_000
    ? tr('Ouvert sur {device} · a servi à {time} · s’efface seul après 7 jours sans servir', { device, time: dateText(used, { hour: '2-digit', minute: '2-digit' }) })
    : tr('Ouvert sur {device} · a servi le {date} · s’efface seul le {end} sans servir', { device, date: dateText(used, { day: 'numeric', month: 'long' }), end: ends });
}
