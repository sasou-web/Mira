// « Sous-titres automatiques », from Réglages, for an administrator: the extension (Mira.Jellyfin Subtitles/) searches
// OpenSubtitles, and SubDL when set, for each title that arrives, keeps only what fits the file, aligns it on the video,
// and says what it did in its journal. Here: turned on or off, the languages, what is accepted, the sources and their
// accounts (never shown back: only whether they are set), a try of the sources, the titles already there, the journal.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn, language as shown, timeOfDay, dateText } from '../i18n.js';
import { sheet, toast, errorState, emptyState, spinner } from '../components.js';
import { goBack } from '../app.js';
import { languageName } from '../player/tracks.js';
import { privacyText } from '../subtitles.js';
import { journalLine, waitingLine, AUTO_LANGUAGES, MAX_LANGUAGES } from '../subtitles-auto.js';

const HEARING = [
  ['any', () => tr('Indifférent'), () => tr('Les deux, le mieux noté d’abord.')],
  ['prefer', () => tr('De préférence'), () => tr('Avec les bruits et qui parle, quand il y en a.')],
  ['avoid', () => tr('Les éviter'), () => tr('Seulement les dialogues, quand il y en a.')],
];

/** A language's name, in the language of the page (« Français », « French »). */
const nameOf = (code) => languageName(code) ?? code;
/** When something happened: the time today, else the day. */
function when(iso) {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';
  return at.toDateString() === new Date().toDateString() ? timeOfDay(at) : dateText(at, { day: 'numeric', month: 'short' });
}

export function create() {
  const el = h('div', { class: 'view auto-subtitles-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture des réglages…'));
  const body = h('div', {}, spinner());
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Sous-titres automatiques')), status),
    body);
  let version = 0;
  let state = null;
  // What « Essayer les sources » answered, kept while the screen is drawn again.
  let tried = null;

  async function load() {
    const mine = ++version;
    try {
      const answer = await api.autoSubtitles();
      if (mine === version) { state = answer; render(); }
    } catch (error) {
      if (mine !== version) return;
      status.textContent = '';
      // An extension older than « Sous-titres automatiques » answers 404 here.
      body.replaceChildren(error.status === 404
        ? emptyState({ symbol: 'subtitles', title: tr('Mira sur Jellyfin est trop ancien'), text: tr('Mets à jour l’extension Mira de Jellyfin (Tableau de bord → Extensions), puis redémarre Jellyfin.') })
        : errorState(error, load));
    }
  }

  /** Sends what changed; the screen shows what the extension kept. */
  async function change(changes, said = '') {
    try {
      state = { ...state, settings: await api.setAutoSubtitles(changes) };
      render();
      if (said) toast(said);
    } catch (error) { toast(error.message, { tone: 'error' }); load(); }
  }

  function toggle(on, label, sub, run) {
    return h('button', { class: 'item', role: 'switch', 'aria-checked': String(!!on), on: { click: run } },
      h('span', { class: 'grow' }, label, sub ? h('span', { class: 'sub' }, sub) : null), h('span', { class: 'switch', 'aria-hidden': 'true' }));
  }
  function choice(label, value, run) {
    return h('button', { class: 'item', on: { click: run } }, h('span', { class: 'grow' }, label), h('span', { class: 'value' }, value), icon('right', { size: 20 }));
  }

  function render() {
    const s = state.settings;
    status.textContent = waitingLine(state, timeOfDay);
    const parts = [
      h('div', { class: 'group' },
        toggle(s.enabled, tr('Chercher tout seul'), tr('Quand un titre arrive, puis de temps en temps pendant un mois s’il n’y a rien d’assez sûr. Jamais pendant une lecture.'),
          () => change({ enabled: !s.enabled, ...(!s.enabled && !s.languages.length ? { languages: [shown() === 'fr' ? 'fre' : 'eng'] } : {}) }))),
      h('p', { class: 'group-note' }, privacyText(['Mira'])),

      h('h2', { class: 'label group-title' }, tr('Langues')),
      h('div', { class: 'group' },
        s.languages.map((code) => h('div', { class: 'item static' }, icon('language'), h('span', { class: 'grow' }, nameOf(code)),
          h('button', { class: 'btn small', 'aria-label': tr('Retirer {language}', { language: nameOf(code) }), on: { click: () => change({ languages: s.languages.filter((x) => x !== code) }) } }, tr('Retirer')))),
        s.languages.length < MAX_LANGUAGES
          ? h('button', { class: 'item', on: { click: addLanguage } }, icon('plus'), h('span', { class: 'grow' }, tr('Ajouter une langue')), icon('right', { size: 20 }))
          : null),
      h('p', { class: 'group-note' }, tr('Une recherche par langue et par titre. Ce qui a déjà des sous-titres de cette langue, dans le fichier ou à côté, est laissé tel quel.')),

      h('h2', { class: 'label group-title' }, tr('Ce que Mira garde')),
      h('div', { class: 'group' },
        choice(tr('Pour sourds et malentendants'), (HEARING.find(([k]) => k === s.hearing) ?? HEARING[0])[1](), () => sheet({
          title: tr('Pour sourds et malentendants'),
          items: HEARING.map(([key, label, sub]) => ({ label: label(), sub: sub(), selected: key === s.hearing, run: () => change({ hearing: key }) })),
        })),
        toggle(s.machineTranslated, tr('Accepter les traductions automatiques'), tr('Seulement s’il n’y a rien d’autre.'), () => change({ machineTranslated: !s.machineTranslated })),
        toggle(s.sync, tr('Recaler sur la vidéo'), tr('Sur une piste de sous-titres du fichier, sinon sur les voix : le PC serveur écoute le film (une minute ou deux, en priorité basse).'), () => change({ sync: !s.sync }))),
      h('p', { class: 'group-note' }, tr('Mira note chaque sous-titre de 0 à 100 d’après le fichier : même empreinte, même release, même source, même vitesse… Il garde seulement ce qui est sûr, ou ce qui colle une fois recalé.')),

      h('h2', { class: 'label group-title' }, tr('Sources')),
      h('div', { class: 'group' },
        toggle(s.openSubtitles.enabled, 'OpenSubtitles', s.openSubtitles.account
          ? tr('Compte {name}', { name: s.openSubtitles.account })
          : tr('Sans compte : quelques téléchargements par jour. Un compte gratuit en donne plus.'), () => change({ openSubtitles: { enabled: !s.openSubtitles.enabled } })),
        h('button', { class: 'item', on: { click: openSubtitlesAccount } }, h('span', { class: 'grow' }, tr('Compte OpenSubtitles'),
          h('span', { class: 'sub' }, s.openSubtitles.ownKey ? tr('Avec ta propre clé d’API.') : tr('Facultatif.'))), icon('right', { size: 20 })),
        toggle(s.subDl.enabled && s.subDl.ready, 'SubDL', s.subDl.ready ? tr('Clé d’API enregistrée.') : tr('Demande une clé d’API gratuite (subdl.com).'),
          () => (s.subDl.ready ? change({ subDl: { enabled: !s.subDl.enabled } }) : subDlKey())),
        s.subDl.ready ? h('button', { class: 'item', on: { click: subDlKey } }, h('span', { class: 'grow' }, tr('Clé SubDL')), icon('right', { size: 20 })) : null,
        h('button', { class: 'item', on: { click: trySources } }, icon('refresh'), h('span', { class: 'grow' }, tr('Essayer les sources'),
          tried ? h('span', { class: 'sub' }, tried) : null))),

      h('h2', { class: 'label group-title' }, tr('Ta bibliothèque')),
      h('div', { class: 'group' },
        h('button', { class: 'item', disabled: !s.enabled || !s.languages.length, on: { click: confirmLibrary } }, icon('films'),
          h('span', { class: 'grow' }, tr('Chercher aussi pour ce qui est déjà là'), h('span', { class: 'sub' }, tr('Une fois pour chaque titre sans sous-titres de tes langues, au rythme des quotas.'))), icon('right', { size: 20 }))),
    ];
    const journal = state.journal ?? [];
    parts.push(h('h2', { class: 'label group-title' }, tr('Journal')));
    parts.push(journal.length
      ? h('div', { class: 'group' }, journal.map((entry) => h(entry.itemId ? 'a' : 'div', { class: ['item', !entry.itemId && 'static'], href: entry.itemId ? `#/titre/${entry.itemId}` : null },
        icon(entry.kind === 'kept' ? 'check' : entry.kind === 'none' ? 'clock' : 'warning'),
        h('span', { class: 'grow' }, entry.name || tr('Titre'), h('span', { class: 'sub' }, [nameOf(entry.language), journalLine(entry), when(entry.when)].filter(Boolean).join(' · '))))))
      : h('p', { class: 'group-note journal-empty' }, tr('Rien encore : ce que Mira trouve, recale ou écarte s’inscrit ici.')));
    body.replaceChildren(...parts);
  }

  function addLanguage() {
    const s = state.settings;
    sheet({
      title: tr('Ajouter une langue'),
      items: AUTO_LANGUAGES.filter((code) => !s.languages.includes(code)).map((code) => ({ label: nameOf(code), run: () => change({ languages: [...s.languages, code] }) })),
    });
  }

  function openSubtitlesAccount() {
    const s = state.settings.openSubtitles;
    const name = h('input', { class: 'input', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', value: s.account ?? '', 'aria-label': tr('Nom d’utilisateur OpenSubtitles') });
    const password = h('input', { class: 'input', type: 'password', autocomplete: 'off', placeholder: s.account ? tr('Inchangé') : '', 'aria-label': tr('Mot de passe OpenSubtitles') });
    const key = h('input', { class: 'input', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', placeholder: s.ownKey ? tr('Inchangée') : tr('Celle de Mira'), 'aria-label': tr('Clé d’API OpenSubtitles') });
    let close = () => {};
    const save = (changes) => { close(); change({ openSubtitles: changes }, tr('Enregistré.')); };
    close = sheet({
      title: tr('Compte OpenSubtitles'),
      focus: name,
      body: h('div', { class: 'sheet-form' },
        h('p', { class: 'group-note' }, tr('Le mot de passe reste sur le PC serveur, jamais renvoyé à un appareil. Une clé d’API à toi ne sert que si tu as créé ton propre « API consumer ».')),
        h('div', { class: 'field' }, h('label', {}, tr('Nom d’utilisateur')), name),
        h('div', { class: 'field' }, h('label', {}, tr('Mot de passe')), password),
        h('div', { class: 'field' }, h('label', {}, tr('Clé d’API (facultatif)')), key),
        h('div', { class: 'invite-actions' },
          h('button', { class: 'btn small primary', on: { click: () => save({ username: name.value, ...(password.value ? { password: password.value } : {}), ...(key.value ? { apiKey: key.value } : {}) }) } }, tr('Enregistrer')),
          s.account || s.ownKey ? h('button', { class: 'btn small quiet', on: { click: () => save({ username: '', password: '', apiKey: '' }) } }, tr('Tout effacer')) : null)),
    });
  }

  function subDlKey() {
    const s = state.settings.subDl;
    const key = h('input', { class: 'input', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', placeholder: s.ready ? tr('Inchangée') : '', 'aria-label': tr('Clé d’API SubDL') });
    let close = () => {};
    const save = (changes) => { close(); change({ subDl: changes }, tr('Enregistré.')); };
    close = sheet({
      title: tr('Clé SubDL'),
      focus: key,
      body: h('div', { class: 'sheet-form' },
        h('p', { class: 'group-note' }, tr('Crée un compte sur subdl.com, puis copie la clé de ton profil. Elle reste sur le PC serveur.')),
        h('div', { class: 'field' }, h('label', {}, tr('Clé d’API')), key),
        h('div', { class: 'invite-actions' },
          h('button', { class: 'btn small primary', on: { click: () => { if (key.value.trim()) save({ apiKey: key.value, enabled: true }); } } }, tr('Enregistrer')),
          s.ready ? h('button', { class: 'btn small quiet', on: { click: () => save({ apiKey: '', enabled: false }) } }, tr('Effacer la clé')) : null)),
    });
  }

  async function trySources() {
    tried = tr('Essai en cours…');
    render();
    try {
      const { sources = [] } = await api.testAutoSubtitles();
      tried = sources.length
        ? sources.map((x) => (x.ok
          ? x.remaining != null ? trn(x.remaining, '{source} répond ({n} téléchargement restant)', '{source} répond ({n} téléchargements restants)', { source: x.source }) : tr('{source} répond', { source: x.source })
          : tr('{source} : {error}', { source: x.source, error: x.refused ? tr('identifiants ou clé refusés') : x.error }))).join(' · ')
        : tr('Aucune source allumée.');
    } catch (error) { tried = error.message; }
    render();
  }

  function confirmLibrary() {
    sheet({
      title: tr('Chercher pour toute la bibliothèque ?'),
      body: h('p', { class: 'now-confirm' }, tr('Chaque film et chaque épisode sans sous-titres de tes langues passe une fois, quelques-uns par minute, jamais pendant une lecture. Sans compte, le quota d’OpenSubtitles le fait durer plusieurs jours.')),
      items: [{ label: tr('Lancer'), run: queueLibrary }],
    });
  }
  async function queueLibrary() {
    try {
      const { added = 0, waiting = 0 } = await api.queueAutoSubtitles();
      toast(added ? trn(added, '{n} recherche ajoutée.', '{n} recherches ajoutées.') : tr('Rien à ajouter : tout est déjà en attente ou fait.'));
      state = { ...state, waiting };
      render();
    } catch (error) { toast(error.message, { tone: 'error' }); }
  }

  load();
  return { el, title: tr('Sous-titres automatiques'), refresh: () => load() };
}
