// « Inviter par lien », the guest's first screen: a link (#/invite/<code>) opened by someone without an account here.
// The link is opened for this device (api.openInvite), then Mira reloads on the extension's gateway and joins the
// host's group (views/join.js, #/ensemble). Without a code (#/invite), the link no longer works: taken back by the host,
// or its week without use gone. « Ta party » (#/party/<code>): the guest says their name, then the same; someone signed
// in here with an account of this Jellyfin goes to the host's group with it.
import { api } from '../api.js';
import { h, clear } from '../dom.js';
import { tr } from '../i18n.js';
import { markSvg } from '../app.js';
import { session } from '../session.js';
import { replaceRoute } from '../app.js';

const NAME = 'mira.party.name';
function savedName() { try { return localStorage.getItem(NAME) || ''; } catch { return ''; } }
function saveName(name) { try { localStorage.setItem(NAME, name); } catch { /* asked again next time */ } }

/** What a guest reads once the link no longer lets them in. */
export const ENDED = tr('Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité.');

export function create({ code, party } = {}) {
  const el = h('div', { class: 'view gate' });
  let disposed = false;
  const page = (title, lead, ...rest) => clear(el).append(markSvg(56),
    h('div', { class: 'gate-text' }, h('h1', {}, title), lead ? h('p', { class: 'lead' }, lead) : ''), ...rest);

  let guestName = '';

  /** A party: who joins, as the group shows them (kept for the next time); then in. */
  function askName() {
    const input = h('input', { class: 'input', id: 'party-name', autocomplete: 'nickname', enterkeyhint: 'go', maxlength: 30, value: savedName(), placeholder: tr('Ton prénom') });
    const go = () => { guestName = input.value.trim(); if (guestName) saveName(guestName); open(); };
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') go(); });
    page(tr('Rejoins la party'), tr('Ton prénom, pour que le groupe sache qui est là. Pas de compte à créer.'),
      h('div', { class: 'gate-form' },
        h('label', { class: 'label', for: 'party-name' }, tr('Ton prénom')), input,
        h('button', { class: 'btn primary block', on: { click: go } }, tr('Rejoindre la party'))));
    input.focus();
  }

  /**
   * Someone with an account here: the host's group, with that account. A guest of this same host already (this device
   * came before): their group, as they are, never a new session that would end the one of another tab.
   */
  async function toHost({ guest = false } = {}) {
    page(tr('Tu entres dans la soirée…'), '', h('div', { class: 'spinner', role: 'status', 'aria-label': tr('Chargement') }));
    try {
      const { host } = await api.partyInfo(party);
      if (disposed) return;
      if (guest && host !== session.current?.host) { open(); return; }
      replaceRoute(guest ? '#/ensemble' : `#/ensemble?hote=${encodeURIComponent(host)}`);
    } catch (error) {
      if (!disposed) page(tr('Le lien n’a pas marché'), error?.message || ENDED);
    }
  }

  async function open() {
    page(tr('Tu entres dans la soirée…'), '', h('div', { class: 'spinner', role: 'status', 'aria-label': tr('Chargement') }));
    try { await api.openInvite(code, party ? { party, guest: guestName } : {}); } catch (error) {
      if (disposed) return;
      page(tr('Le lien n’a pas marché'), error?.message || ENDED,
        error?.unreachable || error?.status === 429 || error?.status >= 500
          ? h('button', { class: 'btn primary', on: { click: open } }, tr('Réessayer'))
          : '');
      return;
    }
    if (disposed) return;
    // The gateway is Jellyfin for this page only once it loads again (api.js base): the code stays out of the history.
    location.replace('#/ensemble');
    location.reload();
  }

  // Already signed in here with an account of this Jellyfin: the link is for someone without one, and opening it
  // would end this session.
  if (party) {
    if (session.current && !session.current.guest) toHost();
    else if (session.current?.guest) toHost({ guest: true });
    else askName();
  } else if (code && session.current && !session.current.guest) {
    page(tr('Ce lien est pour ton invité'), tr('Tu es déjà connecté à Mira ici. Envoie ce lien à la personne que tu invites : il ne sert qu’à un seul appareil.'));
  } else if (code) open();
  else {
    // A guest's session that Jellyfin no longer takes: nothing of it stays.
    if (session.current?.guest) session.clear();
    page(tr('La soirée est finie pour ce lien'), ENDED);
  }
  return { el, title: tr('Regarder ensemble'), dispose: () => { disposed = true; } };
}
