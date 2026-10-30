// « Profils », opened from Réglages by an administrator: the accounts of this Jellyfin with their photo, a new one
// made for someone of the house or a friend (profiles.js: the libraries chosen, nothing to download), and each one's
// photo changed. A new profile's phone signs in by a code that « Autoriser son appareil » approves for that account,
// or with the password shown once here, never kept.
import { api } from '../api.js';
import { h, icon, clear, initials } from '../dom.js';
import { session } from '../session.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { tr } from '../i18n.js';
import { newPassword, cleanName, nameProblem, choices, chosenFolders, profilePolicy, profileLine } from '../profiles.js';
import { choosePhoto } from '../photo.js';

/** An account's round photo, or its initials. */
export function avatar(user, size = 56) {
  const box = h('div', { class: 'avatar', 'aria-hidden': 'true' });
  if (user?.PrimaryImageTag) box.append(h('img', { alt: '', src: api.photoUrl(user.Id, user.PrimaryImageTag, size * 3) }));
  else box.textContent = initials(user?.Name ?? '');
  return box;
}

/** Changes an account's photo: a picture chosen, or the photo taken off. Resolves to true once Jellyfin has the change. */
export function photoSheet(user, done) {
  const run = async (remove) => {
    try {
      if (remove) await api.removePhoto(user.Id);
      else {
        const jpeg = await choosePhoto();
        if (!jpeg) return;
        await api.setPhoto(user.Id, jpeg);
      }
      toast(remove ? tr('Photo retirée.') : tr('Photo changée : elle suit ce compte sur tous ses appareils.'));
      done?.();
    } catch (error) { toast(error.message); }
  };
  sheet({
    title: user.Id === session.current?.userId ? tr('Ta photo') : tr('Photo de {name}', { name: user.Name }),
    items: [
      { label: tr('Choisir une photo'), symbol: 'user', run: () => run(false) },
      user.PrimaryImageTag ? { label: tr('Retirer la photo'), symbol: 'trash', danger: true, run: () => run(true) } : null,
    ].filter(Boolean),
  });
}

export function create() {
  const el = h('div', { class: 'view profiles-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture des comptes de Jellyfin…'));
  const add = h('button', { class: 'btn primary', on: { click: () => addProfile() } }, icon('plus'), tr('Ajouter un profil'));
  const list = h('div', { class: 'group profiles-list' });
  const note = h('p', { class: 'group-note' },
    tr('Un profil voit seulement les bibliothèques choisies, sans téléchargement ni réglages de Jellyfin. Sa progression, ses favoris et ses choix restent à lui. Touche un profil pour changer sa photo ou autoriser son appareil.'));
  const body = h('div', {}, h('div', { class: 'page profiles-actions' }, add), list, note);
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Profils')), status),
    body);

  let users = [], folders = [], version = 0, quick = false;

  async function load() {
    const mine = ++version;
    try {
      const [accounts, libraries, codes] = await Promise.all([api.users(), api.virtualFolders(), api.quickConnectEnabled()]);
      if (mine !== version) return;
      users = accounts; folders = libraries; quick = codes;
      body.hidden = false;
      render();
    } catch (error) {
      if (mine !== version) return;
      status.textContent = '';
      body.replaceWith(errorState(error, () => location.reload()));
    }
  }
  function render() {
    status.textContent = '';
    add.disabled = choices(folders).length === 0;
    list.replaceChildren(...users.map((user) => h('button', { class: 'item profile-item', on: { click: () => manage(user) } },
      avatar(user, 44),
      h('span', { class: 'grow' }, user.Name, user.Id === session.current?.userId ? h('span', { class: 'badge' }, tr('Toi')) : null,
        h('span', { class: 'sub' }, profileLine(user, folders))),
      icon('right', { size: 20 }))));
  }
  function manage(user) {
    sheet({
      title: user.Name,
      items: [
        { label: user.PrimaryImageTag ? tr('Changer la photo') : tr('Ajouter une photo'), symbol: 'user', run: () => photoSheet(user, load) },
        quick && !user.Policy?.IsDisabled ? { label: tr('Autoriser son appareil'), sub: tr('Avec le code que Mira affiche sur son téléphone.'), symbol: 'phone', run: () => approveFor(user) } : null,
      ].filter(Boolean),
    });
  }

  function addProfile() {
    const field = h('input', { class: 'input', id: 'profile-name', autocomplete: 'off', autocapitalize: 'words', enterkeyhint: 'done', maxlength: 60 });
    const libraries = choices(folders);
    // One library: given at once. Several: each one chosen, none at first (as Mira for Windows asks).
    const boxes = libraries.map((folder) => h('input', { type: 'checkbox', value: folder.ItemId, checked: libraries.length === 1 }));
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, tr('Créer le profil'));
    let close = () => {};
    const form = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        const name = cleanName(field.value);
        const wrong = nameProblem(name, users.map((x) => x.Name));
        if (wrong) { errorBox.textContent = wrong; field.focus(); return; }
        const chosen = boxes.filter((x) => x.checked).map((x) => x.value);
        if (!chosen.length) { errorBox.textContent = tr('Choisis au moins une bibliothèque à partager.'); return; }
        button.disabled = true; button.textContent = tr('Création du profil…'); errorBox.textContent = '';
        const password = newPassword();
        try {
          const made = await api.createProfile(name, password, (policy) => profilePolicy(policy, chosenFolders(folders, chosen)));
          close();
          await load();
          made.Name = name;
          created(made, password, libraries.filter((x) => chosen.includes(x.ItemId)).map((x) => x.Name));
        } catch (error) {
          errorBox.textContent = error.status === 400 ? tr('Jellyfin refuse ce nom : il est déjà pris, ou contient un signe qu’il n’accepte pas.') : error.message;
          button.disabled = false; button.textContent = tr('Créer le profil');
        }
      } },
    },
    h('div', { class: 'field' }, h('label', { for: 'profile-name' }, tr('Prénom')), field),
    h('fieldset', { class: 'field profile-libraries' }, h('legend', {}, tr('Ce qu’il voit')),
      ...libraries.map((folder, i) => h('label', { class: 'check-line' }, boxes[i], h('span', {}, folder.Name)))),
    errorBox, button);
    close = sheet({ title: tr('Ajouter un profil'), body: form, focus: field });
  }

  // The new profile: how its phone signs in. The password shows here once; Mira keeps it nowhere.
  function created(user, password, shared) {
    const secret = h('code', { class: 'profile-password' }, password);
    const reveal = h('button', { class: 'btn quiet small', on: { click: () => { secret.hidden = false; reveal.remove(); } } }, tr('Afficher son mot de passe'));
    secret.hidden = quick;
    sheet({
      title: tr('Profil « {name} » créé', { name: user.Name }),
      body: h('div', { class: 'sheet-form' },
        h('p', { class: 'group-note' }, tr('Il voit : {libraries}.', { libraries: shared.join(', ') })),
        quick
          ? h('p', { class: 'group-note' }, tr('Sur son téléphone, dans Mira : « Se connecter avec un code ». Puis ici, touche son profil → « Autoriser son appareil » et tape le code.'))
          : h('p', { class: 'group-note' }, tr('Sur son téléphone, dans Mira : son prénom et ce mot de passe. Note-le maintenant : Mira ne le garde pas.')),
        secret, quick ? reveal : null),
    });
  }

  // Quick Connect: the code the new profile's device shows, approved for that account.
  function approveFor(user) {
    const field = h('input', { class: 'input code-input', id: 'profile-code', inputmode: 'numeric', autocomplete: 'off', maxlength: 7, enterkeyhint: 'go' });
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, tr('Autoriser'));
    let close = () => {};
    const form = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        const code = field.value.replace(/\D/g, '');
        if (code.length !== 6) { errorBox.textContent = tr('Le code a 6 chiffres.'); field.focus(); return; }
        button.disabled = true; errorBox.textContent = '';
        try {
          await api.approveCode(code, user.Id);
          close();
          toast(tr('Appareil autorisé : il se connecte au profil « {name} ».', { name: user.Name }));
        } catch (error) {
          errorBox.textContent = error.status === 404 ? tr('Code inconnu ou expiré. Vérifie-le, ou demandes-en un nouveau sur l’appareil.') : error.message;
          button.disabled = false; field.select();
        }
      } },
    },
    h('p', { class: 'group-note' }, tr('Le code à 6 chiffres affiché par son appareil. Il ouvrira le profil « {name} ».', { name: user.Name })),
    h('div', { class: 'field' }, h('label', { for: 'profile-code' }, tr('Code')), field), errorBox, button);
    close = sheet({ title: tr('Autoriser son appareil'), body: form, focus: field });
  }

  body.hidden = true;
  load();
  return { el, title: tr('Profils'), enter: () => load() };
}
