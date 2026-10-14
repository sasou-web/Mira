// Settings: playback quality and behaviour, this phone, the account; what Mira and the server are.
import { api, base, VERSION } from '../api.js';
import { h, icon, clear, initials } from '../dom.js';
import { session, settings, isIOS, isStandalone, device, applyTextSize } from '../session.js';
import { sheet, toast } from '../components.js';
import { resetScreens, replaceRoute } from '../app.js';
import { installHint } from './login.js';
import { appleNative } from '../player/video.js';
import { AUDIO_LANGUAGES, SUBTITLE_LANGUAGES } from '../player/tracks.js';
import { homeRows, moveRow, showRow } from '../home-rows.js';

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
// Mira's text, on this phone: iOS's text size does not reach a web page, and an app of the Home Screen has no Safari
// around it to zoom the page. The layout holds up to the largest.
const TEXT_SIZES = [[100, 'Normale'], [115, 'Grande'], [130, 'Très grande']];

export function create() {
  const el = h('div', { class: 'view settings' });

  function toggle(key, label, sub, { onTurnOn = null } = {}) {
    const button = h('button', {
      class: 'item', role: 'switch', 'aria-checked': String(!!settings.get(key)),
      on: { click: () => {
        settings.set(key, !settings.get(key));
        button.setAttribute('aria-checked', String(!!settings.get(key)));
        if (settings.get(key)) onTurnOn?.();
      } },
    }, h('span', { class: 'grow' }, label, sub ? h('span', { class: 'sub' }, sub) : null), h('span', { class: 'switch', 'aria-hidden': 'true' }));
    return button;
  }
  function choice(label, value, run) {
    return h('button', { class: 'item', on: { click: run } }, h('span', { class: 'grow' }, label), h('span', { class: 'value' }, value), icon('right', { size: 20 }));
  }

  // The account and the server, once Jellyfin has said: kept for the next times the screen is drawn. quick: Quick
  // Connect on this server, for « Autoriser un appareil ».
  // libraries: for « Nouveau dans … » on the home page.
  let user = null, info = null, quick = false, libraries = [], drawn = '';
  // What the screen shows: drawn again only when one of these changed (coming back to the tab, Jellyfin's answer).
  const SHOWN = ['quality', 'autoNext', 'resume', 'subtitleSize', 'audioLanguages', 'subtitleLanguages', 'autoSkip', 'ambientAudio', 'nativePlayer', 'textSize', 'homeRows'];
  function render() {
    const current = session.current ?? {};
    const signature = JSON.stringify([SHOWN.map((key) => settings.get(key)), current.userName, current.serverName, user?.Id, user?.PrimaryImageTag, info?.Version, quick, libraries.map((x) => x.Id + x.Name)]);
    if (signature === drawn) return;
    drawn = signature;
    const quality = QUALITIES.find(([k]) => k === settings.get('quality')) ?? QUALITIES[0];
    const size = SIZES.find(([k]) => k === settings.get('subtitleSize')) ?? SIZES[1];
    const textSize = TEXT_SIZES.find(([k]) => k === settings.get('textSize')) ?? TEXT_SIZES[0];
    const audio = AUDIO_LANGUAGES.find(([k]) => k === settings.get('audioLanguages')) ?? AUDIO_LANGUAGES[0];
    const subtitles = SUBTITLE_LANGUAGES.find(([k]) => k === settings.get('subtitleLanguages')) ?? SUBTITLE_LANGUAGES[0];
    const languages = (title, list, current, key) => () => sheet({
      title, items: list.map(([value, label]) => ({ label, selected: value === current[0], run: () => { settings.set(key, value); render(); } })),
    });
    if (!photo || photo.dataset.tag !== String(user?.PrimaryImageTag ?? '')) {
      photo = h('div', { class: 'avatar', dataset: { tag: String(user?.PrimaryImageTag ?? '') } });
      if (user?.PrimaryImageTag) photo.append(h('img', { alt: '', src: `${base}/Users/${user.Id}/Images/Primary?maxWidth=160&tag=${user.PrimaryImageTag}` }));
      else photo.textContent = initials(current.userName);
    }
    const avatar = photo;

    clear(el).append(
      h('div', { class: 'page', style: { paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, 'Réglages')),
      h('div', { class: 'profile' }, avatar, h('div', {},
        h('div', { class: 'h3' }, current.userName ?? ''),
        h('div', { class: 'meta' }, current.serverName ? `Serveur « ${current.serverName} »` : 'Serveur Jellyfin'))),

      h('h2', { class: 'label group-title' }, 'Lecture'),
      h('div', { class: 'group' },
        choice('Qualité', quality[1], () => sheet({
          title: 'Qualité de lecture',
          items: QUALITIES.map(([key, label, sub]) => ({ label, sub, selected: key === quality[0], run: () => { settings.set('quality', key); render(); } })),
        })),
        toggle('resume', 'Reprendre là où tu t’es arrêté', 'Sinon, chaque titre repart du début.'),
        toggle('autoNext', 'Épisode suivant automatique', 'À la fin d’un épisode, le suivant démarre.'),
        toggle('autoSkip', 'Passer les intros et les récaps', 'Quand Jellyfin ou les chapitres du fichier les ont repérés, la lecture saute par-dessus.'),
        choice('Langue audio', audio[1], languages('Langue audio préférée', AUDIO_LANGUAGES, audio, 'audioLanguages')),
        choice('Langue des sous-titres', subtitles[1], languages('Langue des sous-titres', SUBTITLE_LANGUAGES, subtitles, 'subtitleLanguages')),
        appleNative()
          ? h('div', { class: 'item static' }, h('span', { class: 'grow' }, 'Lecteur d’Apple',
            h('span', { class: 'sub' }, 'Les vidéos s’ouvrent en plein écran. Audio et sous-titres se choisissent sur la fiche du titre, et les sous-titres aussi dans le menu du lecteur. Leur aspect se règle dans Réglages → Accessibilité → Sous-titres et sous-titres codés → Style.')))
          : choice('Taille des sous-titres', size[1], () => sheet({
            title: 'Taille des sous-titres',
            items: SIZES.map(([key, label]) => ({ label, selected: key === size[0], run: () => { settings.set('subtitleSize', key); render(); } })),
          })),
        // iOS shows a Home Screen app's video in the Dynamic Island, even in front. Only a mixable audio session keeps
        // it out, and the only one WebKit gives a page, « ambient », obeys the Silent switch (checked on iOS 27): no
        // session type gives both, hence a choice, off at first, said plainly.
        appleNative() && device.name === 'iPhone' && 'audioSession' in navigator
          ? toggle('ambientAudio', 'Masquer la Dynamic Island',
            'Le son suit alors le mode silencieux : iPhone en silencieux, film sans son. Il se mêle aussi à la musique des autres apps. Sur iPhone, une app web ne peut pas avoir les deux.',
            { onTurnOn: () => toast('Coupe le mode silencieux pour avoir le son.', { duration: 6000 }) })
          : null),
      h('p', { class: 'group-note' }, 'Les langues choisissent les pistes au début de chaque titre, comme Mira sur Windows et Mac. Sur la fiche d’un titre, « Audio et sous-titres » en choisit d’autres : Mira les retient pour les épisodes suivants de la série.'),

      h('h2', { class: 'label group-title' }, 'Accueil'),
      h('div', { class: 'group' }, choice('Rangées de l’accueil', homeSummary(), homeEditor)),
      h('p', { class: 'group-note' }, 'Choisis l’ordre des rangées et celles qui s’affichent, comme sur Mira pour Windows et Mac, sur cet appareil.'),

      h('h2', { class: 'label group-title' }, 'Cet appareil'),
      h('div', { class: 'group' },
        choice('Taille du texte', textSize[1], () => sheet({
          title: 'Taille du texte',
          items: TEXT_SIZES.map(([key, label]) => ({ label, selected: key === textSize[0], run: () => { settings.set('textSize', key); applyTextSize(); render(); } })),
        })),
        isIOS() && !isStandalone()
          ? h('button', { class: 'item', on: { click: () => sheet({ title: 'Mira comme une app', body: h('div', { style: { padding: '0 16px 12px' } }, installHint()) }) } },
            icon('add-home'), h('span', { class: 'grow' }, 'Ajouter à l’écran d’accueil', h('span', { class: 'sub' }, 'Mira s’ouvre alors en plein écran, comme une app.')), icon('right', { size: 20 }))
          : null,
        h('a', { class: 'item', href: '#/partager' }, icon('qr'),
          h('span', { class: 'grow' }, 'Ouvrir sur un autre appareil', h('span', { class: 'sub' }, 'Un QR code et l’adresse de Mira sur ton réseau.')), icon('right', { size: 20 }))),

      h('h2', { class: 'label group-title' }, 'Compte'),
      h('div', { class: 'group' },
        quick
          ? h('button', { class: 'item', on: { click: approve } }, icon('phone'),
            h('span', { class: 'grow' }, 'Autoriser un appareil', h('span', { class: 'sub' }, 'Connecte à ton compte un appareil qui affiche un code, sans y taper ton mot de passe.')), icon('right', { size: 20 }))
          : null,
        h('button', { class: 'item danger', on: { click: () => sheet({
          title: 'Se déconnecter de Mira sur cet appareil ?',
          // At once: Jellyfin is told behind it (up to 5 s when the PC is off).
          items: [{ label: 'Se déconnecter', symbol: 'sign-out', danger: true, run: () => { api.signOut(); resetScreens(); replaceRoute('#/connexion'); } }],
        }) } }, icon('sign-out'), h('span', { class: 'grow' }, 'Se déconnecter'))),

      h('p', { class: 'about' }, `Mira ${VERSION}`, info?.Version ? ` · Jellyfin ${info.Version}` : ''),
      h('p', { class: 'about' }, 'Ta progression est enregistrée sur ton serveur Jellyfin : elle te suit sur Mira pour Windows et pour Mac.'));
  }

  // « Accueil à ta main »: how many rows show, then each row with its switch and its arrows.
  function homeSummary() {
    const shown = homeRows(settings.get('homeRows'), libraries).filter((x) => x.shown).length;
    return shown ? `${shown} rangée${shown > 1 ? 's' : ''}` : 'Aucune';
  }
  function homeEditor() {
    const list = h('div', { class: 'group home-rows' });
    const save = (value, focus) => {
      settings.set('homeRows', value); draw(); render();
      // The arrow pressed stays under the finger or the keyboard, on the row it moved.
      if (focus) list.querySelector(`[data-key="${CSS.escape(focus[0])}"] .${focus[1]}`)?.focus();
    };
    const draw = () => {
      const rows = homeRows(settings.get('homeRows'), libraries);
      list.replaceChildren(...rows.map((row, i) => h('div', { class: 'item home-row', dataset: { key: row.key } },
        h('button', {
          class: 'grow home-switch', role: 'switch', 'aria-checked': String(row.shown),
          on: { click: () => save(showRow(settings.get('homeRows'), libraries, row.key, !row.shown), [row.key, 'home-switch']) },
        }, h('span', { class: 'grow' }, row.title), h('span', { class: 'switch', 'aria-hidden': 'true' })),
        h('button', {
          class: 'round flat up', 'aria-label': `Monter « ${row.title} »`, disabled: i === 0,
          on: { click: () => save(moveRow(settings.get('homeRows'), libraries, row.key, -1), [row.key, i === 1 ? 'down' : 'up']) },
        }, icon('up', { size: 20 })),
        h('button', {
          class: 'round flat down', 'aria-label': `Descendre « ${row.title} »`, disabled: i === rows.length - 1,
          on: { click: () => save(moveRow(settings.get('homeRows'), libraries, row.key, 1), [row.key, i === rows.length - 2 ? 'up' : 'down']) },
        }, icon('down', { size: 20 })))));
    };
    draw();
    const reset = h('button', { class: 'btn quiet small', on: { click: () => save('') } }, 'Revenir à l’ordre de Mira');
    sheet({
      title: 'Rangées de l’accueil',
      body: h('div', { class: 'sheet-form' }, list, h('p', { class: 'group-note' }, '« Nouveau dans … » montre les derniers titres d’une de tes bibliothèques.'), reset),
    });
  }

  // Quick Connect: the code another device shows (Mira web on a phone, a TV), approved here for this account.
  function approve() {
    const field = h('input', { class: 'input code-input', id: 'approve-code', inputmode: 'numeric', autocomplete: 'off', maxlength: 7, enterkeyhint: 'go', 'aria-describedby': 'approve-note' });
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, 'Autoriser');
    let close = () => {};
    const form = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        const code = field.value.replace(/\D/g, '');
        if (code.length !== 6) { errorBox.textContent = 'Le code a 6 chiffres.'; field.focus(); return; }
        button.disabled = true; button.textContent = 'Autorisation…'; errorBox.textContent = '';
        try {
          await api.approveCode(code);
          close();
          toast('Appareil autorisé : il se connecte à ton compte.');
        } catch (error) {
          errorBox.textContent = error.status === 404 ? 'Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.'
            : error.message;
          button.disabled = false; button.textContent = 'Autoriser';
          field.select();
        }
      } },
    },
    h('p', { class: 'group-note', id: 'approve-note' }, 'Saisis le code à 6 chiffres affiché par l’appareil à connecter. Il ouvrira ton compte : n’autorise qu’un appareil que tu as sous les yeux.'),
    h('div', { class: 'field' }, h('label', { for: 'approve-code' }, 'Code'), field), errorBox, button);
    close = sheet({ title: 'Autoriser un appareil', body: form, focus: field });
  }

  let photo = null;
  render();
  Promise.all([api.me().catch(() => null), api.publicInfo().catch(() => null), api.quickConnectEnabled(), api.libraries().catch(() => [])])
    .then(([me, server, on, views]) => { user = me; info = server; quick = on; libraries = views; render(); });
  // A setting changed elsewhere (the quality, in Mira's player) shows when the screen comes back.
  return { el, title: 'Réglages', keep: true, enter: () => render() };
}
