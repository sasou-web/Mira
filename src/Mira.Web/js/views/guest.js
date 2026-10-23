// « Inviter par lien », the guest's first screen: a link (#/invite/<code>) opened by someone without an account here.
// The link is opened for this device (api.openInvite), then Mira reloads on the extension's gateway and joins the
// host's group (views/join.js, #/ensemble). Without a code (#/invite), the link no longer works: taken back by the host,
// or its week without use gone.
import { api } from '../api.js';
import { h, clear } from '../dom.js';
import { tr } from '../i18n.js';
import { markSvg } from '../app.js';
import { session } from '../session.js';

/** What a guest reads once the link no longer lets them in. */
export const ENDED = tr('Ce lien ne marche plus : demande-en un nouveau à la personne qui t’a invité.');

export function create({ code } = {}) {
  const el = h('div', { class: 'view gate' });
  let disposed = false;
  const page = (title, lead, ...rest) => clear(el).append(markSvg(56),
    h('div', { class: 'gate-text' }, h('h1', {}, title), lead ? h('p', { class: 'lead' }, lead) : ''), ...rest);

  async function open() {
    page(tr('Tu entres dans la soirée…'), '', h('div', { class: 'spinner', role: 'status', 'aria-label': tr('Chargement') }));
    try { await api.openInvite(code); } catch (error) {
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
  if (code && session.current && !session.current.guest) {
    page(tr('Ce lien est pour ton invité'), tr('Tu es déjà connecté à Mira ici. Envoie ce lien à la personne que tu invites : il ne sert qu’à un seul appareil.'));
  } else if (code) open();
  else {
    // A guest's session that Jellyfin no longer takes: nothing of it stays.
    if (session.current?.guest) session.clear();
    page(tr('La soirée est finie pour ce lien'), ENDED);
  }
  return { el, title: tr('Regarder ensemble'), dispose: () => { disposed = true; } };
}
