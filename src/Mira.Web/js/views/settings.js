// Settings: playback quality and behaviour, this phone, the account; what Mira and the server are.
import { api, base, VERSION } from '../api.js';
import { h, icon, clear, initials, keepFocus } from '../dom.js';
import { session, settings, settingsChanged, isIOS, isStandalone, device, applyTextSize, storageBlocked } from '../session.js';
import { tr, trn, trc, number, LANGUAGES, browserLanguage } from '../i18n.js';
import { sheet, toast } from '../components.js';
import { resetScreens, replaceRoute } from '../app.js';
import { installHint } from './login.js';
import { photoSheet } from './profiles.js';
import { appleNative } from '../player/video.js';
import { AUDIO_LANGUAGES, SUBTITLE_LANGUAGES } from '../player/tracks.js';
import { RESUME_BACK } from '../player/stream.js';
import { homeRows, moveRow, showRow } from '../home-rows.js';
import { watchReport, reportStart, reportTime } from '../report.js';

export const QUALITIES = [
  ['auto', tr('Automatique'), tr('Pleine qualité à la maison ou quand ta connexion la tient, sinon adaptée à elle')],
  ['max', tr('Maximale'), tr('Le fichier tel quel dès que possible')],
  [20_000_000, tr('{rate} Mb/s', { rate: 20 }), tr('1080p, très bonne qualité')],
  [10_000_000, tr('{rate} Mb/s', { rate: 10 }), '1080p'],
  [6_000_000, tr('{rate} Mb/s', { rate: 6 }), '720p'],
  [3_000_000, tr('{rate} Mb/s', { rate: 3 }), '720p, 4G'],
  [1_500_000, tr('{rate} Mb/s', { rate: number(1.5) }), tr('480p, connexion faible')],
];
/** « 5 s avant », or « Non » for exactly where the title was left. */
const backText = (seconds) => (seconds > 0 ? tr('{n} s avant', { n: seconds }) : trc('resume', 'Non'));
// Mira's language on this device: each always named in its own language, whichever is shown.
const LANGUAGE_NAMES = { en: 'English', fr: 'Français' };
const SIZES = [[80, tr('Petite')], [100, tr('Normale')], [125, tr('Grande')], [150, tr('Très grande')]];
// Mira's text, on this phone: iOS's text size does not reach a web page, and an app of the Home Screen has no Safari
// around it to zoom the page. The layout holds up to the largest.
const TEXT_SIZES = [[100, tr('Normale')], [115, tr('Grande')], [130, tr('Très grande')]];

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
  const SHOWN = ['quality', 'autoNext', 'resume', 'resumeBack', 'subtitleSize', 'audioLanguages', 'subtitleLanguages', 'autoSkip', 'ambientAudio', 'nativePlayer', 'textSize', 'homeRows', 'language'];
  function render() {
    const current = session.current ?? {};
    const signature = JSON.stringify([SHOWN.map((key) => settings.get(key)), current.userName, current.serverName, user?.Id, user?.PrimaryImageTag, user?.Policy?.IsAdministrator, info?.Version, quick, libraries.map((x) => x.Id + x.Name)]);
    if (signature === drawn) return;
    drawn = signature;
    const quality = QUALITIES.find(([k]) => k === settings.get('quality')) ?? QUALITIES[0];
    const size = SIZES.find(([k]) => k === settings.get('subtitleSize')) ?? SIZES[1];
    const textSize = TEXT_SIZES.find(([k]) => k === settings.get('textSize')) ?? TEXT_SIZES[0];
    const audio = AUDIO_LANGUAGES.find(([k]) => k === settings.get('audioLanguages')) ?? AUDIO_LANGUAGES[0];
    const subtitles = SUBTITLE_LANGUAGES.find(([k]) => k === settings.get('subtitleLanguages')) ?? SUBTITLE_LANGUAGES[0];
    const chosenLanguage = LANGUAGES.includes(settings.get('language')) ? settings.get('language') : '';
    const automatic = LANGUAGE_NAMES[browserLanguage()];
    const languages = (title, list, current, key) => () => sheet({
      title, items: list.map(([value, label]) => ({ label, selected: value === current[0], run: () => { settings.set(key, value); render(); } })),
    });
    if (!photo || photo.dataset.tag !== String(user?.PrimaryImageTag ?? '')) {
      photo = h('div', { class: 'avatar', dataset: { tag: String(user?.PrimaryImageTag ?? '') } });
      if (user?.PrimaryImageTag) photo.append(h('img', { alt: '', src: `${base}/Users/${user.Id}/Images/Primary?maxWidth=160&tag=${user.PrimaryImageTag}` }));
      else photo.textContent = initials(current.userName);
    }
    const avatar = photo;

    // Drawn again after a choice: the keyboard stays on the line it was on.
    keepFocus(el, () => clear(el).append(
      h('div', { class: 'page', style: { paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Réglages'))),
      h('div', { class: 'profile' }, user ? h('button', {
        class: 'avatar-button', 'aria-label': user.PrimaryImageTag ? tr('Changer ta photo') : tr('Ajouter ta photo'),
        on: { click: () => photoSheet(user, reloadUser) },
      }, avatar, h('span', { class: 'avatar-edit', 'aria-hidden': 'true' }, icon('pencil', { size: 14 }))) : avatar, h('div', {},
        h('div', { class: 'h3' }, current.userName ?? ''),
        h('div', { class: 'meta' }, current.serverName ? tr('Serveur « {name} »', { name: current.serverName }) : tr('Serveur Jellyfin')))),
      h('div', { class: 'group' },
        h('button', { class: 'item', on: { click: showReport } }, icon('history'),
          h('span', { class: 'grow' }, tr('Ton bilan'), h('span', { class: 'sub' }, tr('Ce que tu as regardé ce mois-ci et cette année.'))), icon('right', { size: 20 })),
        // An administrator sees every session (another account only its own): who plays what, and how.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/en-ce-moment' }, icon('server'),
            h('span', { class: 'grow' }, tr('En ce moment sur Jellyfin'), h('span', { class: 'sub' }, tr('Qui regarde quoi, et si Jellyfin doit convertir.'))), icon('right', { size: 20 }))
          : null,
        // « Profils »: an account for someone of the house or a friend, each one's photo.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/profils' }, icon('users'),
            h('span', { class: 'grow' }, tr('Profils'), h('span', { class: 'sub' }, tr('Un compte pour chaque personne de la maison ou un ami, avec sa photo.'))), icon('right', { size: 20 }))
          : null,
        // Titles Jellyfin recognized badly, to identify again: what only an administrator may change.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/a-corriger' }, icon('warning'),
            h('span', { class: 'grow' }, tr('Titres à corriger'), h('span', { class: 'sub' }, tr('Ceux que Jellyfin n’a pas reconnus, sans affiche ou en double.'))), icon('right', { size: 20 }))
          : null,
        // « Sous-titres automatiques »: the extension's settings, which only an administrator may change.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/sous-titres-auto' }, icon('subtitles'),
            h('span', { class: 'grow' }, tr('Sous-titres automatiques'), h('span', { class: 'sub' }, tr('Jellyfin cherche les sous-titres de chaque titre qui arrive et les recale sur la vidéo.'))), icon('right', { size: 20 }))
          : null,
        // « Inviter par lien »: an account for each link, which only an administrator can make.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/invites' }, icon('link'),
            h('span', { class: 'grow' }, tr('Liens d’invités'), h('span', { class: 'sub' }, tr('Regarder ensemble avec quelqu’un sans compte ni Tailscale.'))), icon('right', { size: 20 }))
          : null,
        // TorLink on the PC of Jellyfin, sent from here: only an administrator, as for Jellyfin's own dashboard.
        user?.Policy?.IsAdministrator
          ? h('a', { class: 'item', href: '#/telecharger' }, icon('download'),
            h('span', { class: 'grow' }, tr('Télécharger sur le PC serveur'), h('span', { class: 'sub' }, tr('Cherche un titre : TorLink le télécharge là-bas, Mira le range dans ta bibliothèque.'))), icon('right', { size: 20 }))
          : null),

      h('h2', { class: 'label group-title' }, trc('settings', 'Lecture')),
      h('div', { class: 'group' },
        choice(tr('Qualité'), quality[1], () => sheet({
          title: tr('Qualité de lecture'),
          items: QUALITIES.map(([key, label, sub]) => ({ label, sub, selected: key === quality[0], run: () => { settings.set('quality', key); render(); } })),
        })),
        toggle('resume', tr('Reprendre là où tu t’es arrêté'), tr('Sinon, chaque titre repart du début.')),
        choice(tr('Reprendre un peu avant'), backText(settings.get('resumeBack')), () => sheet({
          title: tr('Reprendre un peu avant'),
          body: h('p', { class: 'sheet-note' }, tr('Pour retrouver le fil plutôt que de tomber au milieu d’une phrase. Un titre vu moins de 30 s reprend pile là où tu t’es arrêté.')),
          items: RESUME_BACK.map((seconds) => ({
            label: backText(seconds), selected: seconds === settings.get('resumeBack'), sub: seconds ? '' : tr('Pile là où tu t’es arrêté.'),
            run: () => { settings.set('resumeBack', seconds); render(); },
          })),
        })),
        toggle('autoNext', tr('Épisode suivant automatique'), appleNative()
          ? tr('Quand Jellyfin ou les chapitres repèrent le générique de fin, le suivant démarre 10 secondes après son début, sauf si une scène suit le générique. Sinon, à la fin de l’épisode.')
          : tr('À la fin d’un épisode, le suivant démarre.')),
        toggle('autoSkip', tr('Passer les intros et les récaps'), tr('Quand Jellyfin ou les chapitres du fichier les ont repérés, la lecture saute par-dessus.')),
        choice(tr('Langue audio'), audio[1], languages(tr('Langue audio préférée'), AUDIO_LANGUAGES, audio, 'audioLanguages')),
        choice(tr('Langue des sous-titres'), subtitles[1], languages(tr('Langue des sous-titres'), SUBTITLE_LANGUAGES, subtitles, 'subtitleLanguages')),
        appleNative()
          ? h('div', { class: 'item static' }, h('span', { class: 'grow' }, tr('Lecteur d’Apple'),
            h('span', { class: 'sub' }, tr('Les vidéos s’ouvrent en plein écran. Audio et sous-titres se choisissent sur la fiche du titre, et les sous-titres aussi dans le menu du lecteur. Leur aspect se règle dans Réglages → Accessibilité → Sous-titres et sous-titres codés → Style.'))))
          : choice(tr('Taille des sous-titres'), size[1], () => sheet({
            title: tr('Taille des sous-titres'),
            items: SIZES.map(([key, label]) => ({ label, selected: key === size[0], run: () => { settings.set('subtitleSize', key); render(); } })),
          })),
        // iOS shows a Home Screen app's video in the Dynamic Island, even in front. Only a mixable audio session keeps
        // it out, and the only one WebKit gives a page, « ambient », obeys the Silent switch (checked on iOS 27): no
        // session type gives both, hence a choice, off at first, said plainly.
        appleNative() && device.name === 'iPhone' && 'audioSession' in navigator
          ? toggle('ambientAudio', tr('Masquer la Dynamic Island'),
            tr('Le son suit alors le mode silencieux : iPhone en silencieux, film sans son. Il se mêle aussi à la musique des autres apps. Sur iPhone, une app web ne peut pas avoir les deux.'),
            { onTurnOn: () => toast(tr('Coupe le mode silencieux pour avoir le son.'), { duration: 6000 }) })
          : null),
      h('p', { class: 'group-note' }, tr('Les langues choisissent les pistes au début de chaque titre, comme Mira sur Windows et Mac. Sur la fiche d’un titre, « Audio et sous-titres » en choisit d’autres : Mira les retient pour les épisodes suivants de la série.')),
      h('p', { class: 'group-note' }, tr('Ces choix sont gardés sur ton Jellyfin, avec les pistes de chaque série et les titres retirés de Continuer à regarder : tu les retrouves sur ton iPhone, ton iPad, Windows et Mac, même après avoir remis l’icône.')),

      h('h2', { class: 'label group-title' }, tr('Accueil')),
      h('div', { class: 'group' }, choice(tr('Rangées de l’accueil'), homeSummary(), homeEditor)),
      h('p', { class: 'group-note' }, tr('Choisis l’ordre des rangées et celles qui s’affichent, comme sur Mira pour Windows et Mac, sur cet appareil.')),

      h('h2', { class: 'label group-title' }, tr('Cet appareil')),
      h('div', { class: 'group' },
        choice(tr('Langue'), chosenLanguage ? LANGUAGE_NAMES[chosenLanguage] : tr('Automatique · {language}', { language: automatic }), chooseLanguage(chosenLanguage, automatic)),
        choice(tr('Taille du texte'), textSize[1], () => sheet({
          title: tr('Taille du texte'),
          items: TEXT_SIZES.map(([key, label]) => ({ label, selected: key === textSize[0], run: () => { settings.set('textSize', key); applyTextSize(); render(); } })),
        })),
        isIOS() && !isStandalone()
          ? h('button', { class: 'item', on: { click: () => sheet({ title: tr('Mira comme une app'), body: h('div', { style: { padding: '0 16px 12px' } }, installHint()) }) } },
            icon('add-home'), h('span', { class: 'grow' }, tr('Ajouter à l’écran d’accueil'), h('span', { class: 'sub' }, tr('Mira s’ouvre alors en plein écran, comme une app.'))), icon('right', { size: 20 }))
          : null,
        h('a', { class: 'item', href: '#/partager' }, icon('qr'),
          h('span', { class: 'grow' }, tr('Ouvrir sur un autre appareil'), h('span', { class: 'sub' }, tr('Un QR code et l’adresse de Mira sur ton réseau.'))), icon('right', { size: 20 }))),

      h('h2', { class: 'label group-title' }, tr('Compte')),
      h('div', { class: 'group' },
        quick
          ? h('button', { class: 'item', on: { click: approve } }, icon('phone'),
            h('span', { class: 'grow' }, tr('Autoriser un appareil'), h('span', { class: 'sub' }, tr('Connecte à ton compte un appareil qui affiche un code, sans y taper ton mot de passe.'))), icon('right', { size: 20 }))
          : null,
        h('button', { class: 'item danger', on: { click: () => sheet({
          title: tr('Se déconnecter de Mira sur cet appareil ?'),
          // At once: Jellyfin is told behind it (up to 5 s when the PC is off).
          items: [{ label: tr('Se déconnecter'), symbol: 'sign-out', danger: true, run: () => { api.signOut(); resetScreens(); replaceRoute('#/connexion'); } }],
        }) } }, icon('sign-out'), h('span', { class: 'grow' }, tr('Se déconnecter')))),

      h('p', { class: 'about' }, `Mira ${VERSION}`, info?.Version ? ` · Jellyfin ${info.Version}` : ''),
      h('p', { class: 'about' }, tr('Ta progression est enregistrée sur ton serveur Jellyfin : elle te suit sur Mira pour Windows et pour Mac.'))));
  }

  // Another language: the page reloads, on this screen, so that every screen and table follows (i18n.js reads it first).
  function chooseLanguage(chosen, automatic) {
    return () => sheet({
      title: tr('Langue'),
      items: [['', tr('Automatique'), tr('Celle de cet appareil : {language}', { language: automatic })], ['en', LANGUAGE_NAMES.en], ['fr', LANGUAGE_NAMES.fr]]
        .map(([value, label, sub]) => ({ label, sub, selected: value === chosen, run: () => {
          if (value === chosen) return;
          // Without the site's storage the choice would be gone after the reload.
          if (storageBlocked()) { toast(tr('Ce navigateur bloque le stockage du site : Mira ne peut pas garder ce choix.'), { tone: 'error' }); return; }
          settings.set('language', value);
          location.reload();
        } })),
    });
  }

  // « Ton bilan »: the month or the year, from what Jellyfin knows of each title's last play.
  function showReport() {
    let year = false, version = 0;
    const content = h('div', { class: 'report', 'aria-live': 'polite' });
    const option = (value, label) => h('button', { class: 'chip', 'aria-pressed': String(year === value), on: { click: () => { if (year !== value) { year = value; load(); } } } }, label);
    const chips = h('div', { class: 'report-periods' });
    const list = (title, lines) => (lines.length ? h('section', { class: 'report-part' }, h('h3', { class: 'label' }, title), h('ul', {}, lines)) : null);
    async function load() {
      const mine = ++version;
      keepFocus(chips, () => chips.replaceChildren(option(false, tr('Ce mois-ci')), option(true, tr('Cette année'))));
      content.replaceChildren(h('p', { class: 'meta' }, tr('Calcul de ton bilan…')));
      try {
        const from = reportStart(Date.now(), year);
        const { played, series } = await api.playedSince(from);
        if (mine !== version) return;
        const r = watchReport(played, series, from);
        if (!r.films && !r.episodes) { content.replaceChildren(h('p', { class: 'meta' }, year ? tr('Aucun film ni épisode terminé cette année pour l’instant.') : tr('Aucun film ni épisode terminé ce mois-ci pour l’instant.'))); return; }
        const counts = [r.films ? trn(r.films, '{n} film', '{n} films') : null, r.episodes ? trn(r.episodes, '{n} épisode', '{n} épisodes') : null].filter(Boolean).join(' · ');
        content.replaceChildren(...[
          h('div', { class: 'report-time' }, h('strong', {}, reportTime(r.minutes)), h('span', {}, year ? tr('devant l’écran cette année') : tr('devant l’écran ce mois-ci'))),
          h('p', { class: 'report-counts' }, counts),
          list(tr('Séries les plus regardées'), r.series.map((x) => h('li', {}, h('span', { class: 'grow' }, x.name), h('span', { class: 'meta' }, `${trn(x.episodes, '{n} épisode', '{n} épisodes')} · ${reportTime(x.minutes)}`)))),
          list(trn(r.finished.length, 'Série terminée', 'Séries terminées'), r.finished.map((x) => h('li', {}, icon('check', { size: 18 }), h('span', { class: 'grow' }, x)))),
          r.genres.length ? h('section', { class: 'report-part' }, h('h3', { class: 'label' }, tr('Genres préférés')), h('p', { class: 'report-genres' }, r.genres.join(' · '))) : null,
        ].filter(Boolean));
      } catch (error) {
        if (mine === version) content.replaceChildren(h('p', { class: 'form-error' }, error.message));
      }
    }
    load();
    sheet({
      title: tr('Ton bilan'),
      body: h('div', { class: 'sheet-form' }, chips, content,
        h('p', { class: 'group-note' }, tr('D’après Jellyfin : chaque film ou épisode vu jusqu’au bout compte une fois, à sa dernière lecture, sur tous tes appareils.'))),
    });
  }

  // « Accueil à ta main »: how many rows show, then each row with its switch and its arrows.
  function homeSummary() {
    const shown = homeRows(settings.get('homeRows'), libraries).filter((x) => x.shown).length;
    return shown ? trn(shown, '{n} rangée', '{n} rangées') : tr('Aucune');
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
          class: 'round flat up', 'aria-label': tr('Monter « {name} »', { name: row.title }), disabled: i === 0,
          on: { click: () => save(moveRow(settings.get('homeRows'), libraries, row.key, -1), [row.key, i === 1 ? 'down' : 'up']) },
        }, icon('up', { size: 20 })),
        h('button', {
          class: 'round flat down', 'aria-label': tr('Descendre « {name} »', { name: row.title }), disabled: i === rows.length - 1,
          on: { click: () => save(moveRow(settings.get('homeRows'), libraries, row.key, 1), [row.key, i === rows.length - 2 ? 'up' : 'down']) },
        }, icon('down', { size: 20 })))));
    };
    draw();
    const reset = h('button', { class: 'btn quiet small', on: { click: () => save('') } }, tr('Revenir à l’ordre de Mira'));
    sheet({
      title: tr('Rangées de l’accueil'),
      body: h('div', { class: 'sheet-form' }, list, h('p', { class: 'group-note' }, tr('« Nouveau dans … » montre les derniers titres d’une de tes bibliothèques.')), reset),
    });
  }

  // Quick Connect: the code another device shows (Mira web on a phone, a TV), approved here for this account.
  function approve() {
    const field = h('input', { class: 'input code-input', id: 'approve-code', inputmode: 'numeric', autocomplete: 'off', maxlength: 7, enterkeyhint: 'go', 'aria-describedby': 'approve-note' });
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, tr('Autoriser'));
    let close = () => {};
    const form = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        const code = field.value.replace(/\D/g, '');
        if (code.length !== 6) { errorBox.textContent = tr('Le code a 6 chiffres.'); field.focus(); return; }
        button.disabled = true; button.textContent = tr('Autorisation…'); errorBox.textContent = '';
        try {
          await api.approveCode(code);
          close();
          toast(tr('Appareil autorisé : il se connecte à ton compte.'), { tone: 'done' });
        } catch (error) {
          errorBox.textContent = error.status === 404 ? tr('Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.')
            : error.message;
          button.disabled = false; button.textContent = tr('Autoriser');
          field.select();
        }
      } },
    },
    h('p', { class: 'group-note', id: 'approve-note' }, tr('Saisis le code à 6 chiffres affiché par l’appareil à connecter. Il ouvrira ton compte : n’autorise qu’un appareil que tu as sous les yeux.')),
    h('div', { class: 'field' }, h('label', { for: 'approve-code' }, tr('Code')), field), errorBox, button);
    close = sheet({ title: tr('Autoriser un appareil'), body: form, focus: field });
  }

  let photo = null;
  // The photo just changed: Jellyfin's new tag, for the picture of this screen.
  const reloadUser = () => api.me().then((me) => { user = me; render(); }, () => {});
  render();
  // A choice brought from another device by Jellyfin (account-choices.js) shows at once.
  settingsChanged.addEventListener('change', () => { if (el.isConnected) render(); });
  Promise.all([api.me().catch(() => null), api.publicInfo().catch(() => null), api.quickConnectEnabled(), api.libraries().catch(() => [])])
    .then(([me, server, on, views]) => { user = me; info = server; quick = on; libraries = views; render(); });
  // A setting changed elsewhere (the quality, in Mira's player) shows when the screen comes back.
  return { el, title: tr('Réglages'), keep: true, enter: () => render() };
}
