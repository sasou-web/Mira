// Settings: playback quality and behaviour, this phone, the account; what Mira and the server are.
import { api, base, VERSION } from '../api.js';
import { h, icon, clear, initials } from '../dom.js';
import { session, settings, isIOS, isStandalone } from '../session.js';
import { sheet } from '../components.js';
import { resetScreens } from '../app.js';
import { installHint } from './login.js';

export const QUALITIES = [
  ['auto', 'Automatique', 'Pleine qualité à la maison, adaptée à la connexion ailleurs'],
  ['max', 'Maximale', 'Le fichier tel quel dès que possible'],
  [20_000_000, '20 Mb/s', '1080p, très bonne qualité'],
  [10_000_000, '10 Mb/s', '1080p'],
  [6_000_000, '6 Mb/s', '720p'],
  [3_000_000, '3 Mb/s', '720p, 4G'],
  [1_500_000, '1,5 Mb/s', '480p, connexion faible'],
];
const SIZES = [[80, 'Petite'], [100, 'Normale'], [125, 'Grande'], [150, 'Très grande']];

export function create() {
  const el = h('div', { class: 'view settings' });

  function toggle(key, label, sub) {
    const button = h('button', {
      class: 'item', role: 'switch', 'aria-checked': String(!!settings.get(key)),
      on: { click: () => { settings.set(key, !settings.get(key)); button.setAttribute('aria-checked', String(!!settings.get(key))); } },
    }, h('span', { class: 'grow' }, label, sub ? h('span', { class: 'sub' }, sub) : null), h('span', { class: 'switch', 'aria-hidden': 'true' }));
    return button;
  }
  function choice(label, value, run) {
    return h('button', { class: 'item', on: { click: run } }, h('span', { class: 'grow' }, label), h('span', { class: 'value' }, value), icon('right', { size: 20 }));
  }

  function render(user = null, info = null) {
    const current = session.current ?? {};
    const quality = QUALITIES.find(([k]) => k === settings.get('quality')) ?? QUALITIES[0];
    const size = SIZES.find(([k]) => k === settings.get('subtitleSize')) ?? SIZES[1];
    const avatar = h('div', { class: 'avatar' });
    if (user?.PrimaryImageTag) avatar.append(h('img', { alt: '', src: `${base}/Users/${user.Id}/Images/Primary?maxWidth=160&tag=${user.PrimaryImageTag}` }));
    else avatar.textContent = initials(current.userName);

    clear(el).append(
      h('div', { class: 'page', style: { paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, 'Réglages')),
      h('div', { class: 'profile' }, avatar, h('div', {},
        h('div', { class: 'h3' }, current.userName ?? ''),
        h('div', { class: 'meta' }, current.serverName ? `Serveur « ${current.serverName} »` : 'Serveur Jellyfin'))),

      h('h2', { class: 'label group-title' }, 'Lecture'),
      h('div', { class: 'group' },
        choice('Qualité', quality[1], () => sheet({
          title: 'Qualité de lecture',
          items: QUALITIES.map(([key, label, sub]) => ({ label, sub, selected: key === quality[0], run: () => { settings.set('quality', key); render(user, info); } })),
        })),
        toggle('resume', 'Reprendre là où tu t’es arrêté', 'Sinon, chaque titre repart du début.'),
        toggle('autoNext', 'Épisode suivant automatique', 'À la fin d’un épisode, le suivant démarre.'),
        choice('Taille des sous-titres', size[1], () => sheet({
          title: 'Taille des sous-titres',
          items: SIZES.map(([key, label]) => ({ label, selected: key === size[0], run: () => { settings.set('subtitleSize', key); render(user, info); } })),
        }))),

      h('h2', { class: 'label group-title' }, 'Cet appareil'),
      h('div', { class: 'group' },
        isIOS() && !isStandalone()
          ? h('button', { class: 'item', on: { click: () => sheet({ title: 'Mira comme une app', body: h('div', { style: { padding: '0 16px 12px' } }, installHint()) }) } },
            icon('add-home'), h('span', { class: 'grow' }, 'Ajouter à l’écran d’accueil', h('span', { class: 'sub' }, 'Mira s’ouvre alors en plein écran, comme une app.')), icon('right', { size: 20 }))
          : null,
        h('a', { class: 'item', href: '#/partager' }, icon('qr'),
          h('span', { class: 'grow' }, 'Ouvrir sur un autre appareil', h('span', { class: 'sub' }, 'Un QR code et l’adresse de Mira sur ton réseau.')), icon('right', { size: 20 }))),

      h('h2', { class: 'label group-title' }, 'Compte'),
      h('div', { class: 'group' },
        h('button', { class: 'item danger', on: { click: () => sheet({
          title: 'Se déconnecter de Mira sur cet appareil ?',
          items: [{ label: 'Se déconnecter', symbol: 'sign-out', danger: true, run: async () => { await api.signOut(); resetScreens(); location.replace('#/connexion'); } }],
        }) } }, icon('sign-out'), h('span', { class: 'grow' }, 'Se déconnecter'))),

      h('p', { class: 'about' }, `Mira ${VERSION}`, info?.Version ? ` · Jellyfin ${info.Version}` : ''),
      h('p', { class: 'about' }, 'Ta progression est enregistrée sur ton serveur Jellyfin : elle te suit sur Mira pour Windows et pour Mac.'));
  }

  render();
  Promise.all([api.me().catch(() => null), api.publicInfo().catch(() => null)]).then(([user, info]) => render(user, info));
  return { el, title: 'Réglages', keep: true };
}
