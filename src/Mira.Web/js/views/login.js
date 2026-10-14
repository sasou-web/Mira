// Sign-in: the server is the one Mira web comes from; pick your profile, type your password, done. Or, with no
// password to type on the phone, a code approved from a device already signed in (Quick Connect).
import { api, base, exposed } from '../api.js';
import { h, icon, clear, initials, wait } from '../dom.js';
import { isIOS, isStandalone, storageBlocked, temp, computerApp } from '../session.js';
import { appLink } from '../player/sync.js';
import { markSvg, resetScreens, replaceRoute } from '../app.js';

export function installHint() {
  if (!isIOS() || isStandalone()) return null;
  return h('div', { class: 'hint' },
    h('div', {}, h('b', {}, 'Ajoute Mira à ton écran d’accueil'), ' pour l’ouvrir comme une app, en plein écran :'),
    h('div', { class: 'step' }, icon('share'), h('span', {}, 'touche ', h('b', {}, 'Partager'), ' en bas de Safari,')),
    h('div', { class: 'step' }, icon('add-home'), h('span', {}, 'puis ', h('b', {}, 'Sur l’écran d’accueil'), '.')));
}

function avatar(user, size = 120) {
  const box = h('div', { class: 'avatar' });
  if (user.PrimaryImageTag) {
    const img = h('img', { alt: '', src: `${base}/Users/${user.Id}/Images/Primary?maxWidth=${size * 2}&tag=${user.PrimaryImageTag}&quality=90` });
    img.addEventListener('error', () => { img.remove(); box.textContent = initials(user.Name); }, { once: true });
    box.append(img);
  } else box.textContent = initials(user.Name);
  return box;
}

export function create() {
  const el = h('div', { class: 'view gate' });
  const title = h('h1', {}, 'Bienvenue sur Mira');
  // Brought here by an invitation to « Regarder ensemble » (views/join.js): the group comes once signed in.
  const invited = (temp.get('mira.next') ?? '').startsWith('#/ensemble/');
  const lead = h('p', { class: 'lead' }, invited ? 'On t’invite à regarder ensemble : connecte-toi avec ton compte Jellyfin pour rejoindre le groupe.' : 'Connecte-toi avec ton compte Jellyfin.');
  const body = h('div', { style: { display: 'grid', justifyItems: 'center', gap: '22px', width: '100%' } });
  el.append(markSvg(64), h('div', { style: { display: 'grid', gap: '8px', justifyItems: 'center' } }, title, lead), body);
  // Over plain HTTP from the Internet, the password could be read on its way: said before it is typed.
  if (exposed()) {
    body.before(h('div', { class: 'hint warn', role: 'note' }, h('div', { class: 'step' }, icon('warning'),
      h('span', {}, h('b', {}, 'Connexion non chiffrée. '), 'Cette adresse passe par Internet sans protection : ton mot de passe et ce que Mira échange avec le serveur peuvent y être lus. Connecte-toi plutôt sur le Wi-Fi de la maison, ou avec Tailscale.'))));
  }
  // Without the site's storage, the session lasts until Mira is closed: said before signing in, with the setting.
  if (storageBlocked()) {
    body.before(h('div', { class: 'hint warn', role: 'note' }, h('div', { class: 'step' }, icon('warning'),
      h('span', {}, h('b', {}, 'Mira a besoin du stockage du site. '), 'Ce navigateur le bloque : Mira ne pourra pas garder ta connexion, ni ce qu’il affiche, une fois fermé. Sur iPhone, désactive « Bloquer tous les cookies » dans les réglages de Safari, puis rouvre Mira.'))));
  }
  // On a computer with Mira: the group joined from the app (which asks first), with no sign-in here.
  const group = invited ? /^#\/ensemble\/([0-9a-f-]+)$/i.exec(temp.get('mira.next'))?.[1] : null;
  const app = group ? computerApp() : null;
  if (app) {
    body.before(h('div', { class: 'hint open-app' },
      h('div', {}, h('b', {}, `Mira pour ${app} est installé ici ?`), ' Rejoins le groupe depuis l’app : ton navigateur te demandera de l’ouvrir.'),
      h('a', { class: 'btn primary small', href: appLink(`${location.origin}${base}`, group) }, icon('users', { size: 16 }), `Ouvrir dans Mira pour ${app}`)));
  }
  const hint = installHint();
  if (hint) el.append(hint);

  const notice = temp.get('mira.notice');
  temp.remove('mira.notice');

  /** busy(true) while Jellyfin checks, busy(false) if it refuses: the button or the profile touched says so. */
  async function finish(open, errorBox, busy) {
    busy(true); errorBox.textContent = '';
    try {
      await open();
      resetScreens();
      const next = temp.get('mira.next');
      temp.remove('mira.next');
      replaceRoute(next && !next.startsWith('#/connexion') ? next : '#/');
    } catch (error) {
      errorBox.textContent = error.status === 401 ? 'Nom d’utilisateur ou mot de passe incorrect.' : error.message;
      busy(false, error);
    }
  }

  // Quick Connect on this server: asked with the profiles, so that the button is there or not from the start.
  let codes = false;
  const codeButton = (back) => (codes ? h('button', { class: 'btn quiet', type: 'button', on: { click: () => byCode(back) } }, 'Se connecter avec un code') : null);

  // The code on screen, and Jellyfin asked every 2 s whether a device signed in approved it. A new draw of the
  // screen (back, another code, Mira leaving the screen) ends the asking of the one before.
  let asking = 0;
  // before: the screen's line under the title, put back by Retour (a new code keeps the one of the first).
  async function byCode(back, before = lead.textContent) {
    const id = ++asking;
    const again = () => byCode(back, before);
    lead.textContent = 'Connexion par code';
    const leave = () => { asking++; lead.textContent = before; back(); };
    const status = h('p', { class: 'lead', role: 'status' }, 'Demande d’un code…');
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const actions = h('div', { class: 'code-actions' }, h('button', { class: 'btn quiet', type: 'button', on: { click: leave } }, 'Retour'));
    clear(body).append(status, errorBox, actions);
    let start;
    try { start = await api.quickConnectStart(); } catch (error) {
      if (id !== asking) return;
      status.textContent = '';
      errorBox.textContent = error.status === 401 || error.status === 403 || error.status === 404 ? 'Jellyfin n’accepte pas la connexion par code : active Quick Connect dans son tableau de bord (Général).' : error.message;
      actions.prepend(h('button', { class: 'btn', type: 'button', on: { click: again } }, icon('refresh', { size: 20 }), 'Réessayer'));
      return;
    }
    if (id !== asking) return;
    const code = String(start.Code ?? '');
    const shown = code.length === 6 ? `${code.slice(0, 3)} ${code.slice(3)}` : code;
    clear(body).append(
      h('div', { class: 'code', 'aria-label': `Code ${code.split('').join(' ')}` }, shown),
      h('div', { class: 'hint' },
        h('div', {}, 'Sur un appareil déjà connecté à ce serveur, saisis ce code :'),
        h('div', { class: 'step' }, icon('settings'), h('span', {}, 'dans Mira, sur ton PC, ton Mac ou un autre téléphone : ', h('b', {}, 'Réglages → Autoriser un appareil'), ' ;')),
        h('div', { class: 'step' }, icon('user'), h('span', {}, 'ou dans Jellyfin : ', h('b', {}, 'ton profil → Quick Connect'), '.'))),
      status, errorBox, actions);
    status.textContent = 'En attente de ton accord…';
    while (id === asking) {
      await wait(2000);
      if (id !== asking) return;
      if (document.hidden) continue;
      let state;
      try { state = await api.quickConnectState(start.Secret); } catch (error) {
        if (id !== asking) return;
        if (error.status === 404) {
          // Jellyfin keeps a code a few minutes: a new one, at a touch, rather than a code that no longer works.
          clear(body).append(h('p', { class: 'lead' }, 'Ce code a expiré.'),
            h('div', { class: 'code-actions' },
              h('button', { class: 'btn primary', type: 'button', on: { click: again } }, 'Nouveau code'),
              h('button', { class: 'btn quiet', type: 'button', on: { click: leave } }, 'Retour')));
          return;
        }
        // The server away a moment: the code stays on screen, asked again next time.
        status.textContent = error.message;
        continue;
      }
      if (id !== asking) return;
      status.textContent = 'En attente de ton accord…';
      if (!state?.Authenticated) continue;
      asking++;
      status.textContent = 'Connexion…';
      return finish(() => api.signInWithCode(start.Secret), errorBox, (on, error) => {
        if (on || !error) return;
        status.textContent = '';
        if (error.status === 401) errorBox.textContent = 'Jellyfin a refusé ce code. Demandes-en un nouveau.';
        actions.prepend(h('button', { class: 'btn primary', type: 'button', on: { click: again } }, 'Nouveau code'));
      });
    }
  }

  function form(user = null, users = []) {
    clear(body);
    const errorBox = h('p', { class: 'form-error', role: 'alert' }, notice ?? '');
    const name = h('input', { class: 'input', id: 'login-name', autocomplete: 'username', autocapitalize: 'none', autocorrect: 'off', spellcheck: false, enterkeyhint: 'next', value: user?.Name ?? '' });
    const password = h('input', { class: 'input', id: 'login-password', type: 'password', autocomplete: 'current-password', enterkeyhint: 'go' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, 'Se connecter');
    const busy = (on, error) => {
      button.disabled = on; button.textContent = on ? 'Connexion…' : 'Se connecter';
      // A wrong password: the field shakes, as on iPhone, and its text is selected, ready to be typed again.
      if (error?.status === 401) {
        fields.classList.remove('shake'); void fields.offsetWidth; fields.classList.add('shake');
        password.select();
      }
    };
    const fields = h('form', {
      class: 'login-form',
      on: { submit: (e) => { e.preventDefault(); if (!name.value.trim()) { name.focus(); return; } finish(() => api.signIn(name.value.trim(), password.value), errorBox, busy); } },
    },
    user ? h('div', { style: { display: 'flex', alignItems: 'center', gap: '12px' } }, avatar(user, 56), h('div', { class: 'h3' }, user.Name)) : null,
    // A profile picked: its name stays in the form, out of sight, so that the iPhone's passwords know whose it is.
    h('div', { class: ['field', user && 'sr'], 'aria-hidden': user ? 'true' : null }, h('label', { for: 'login-name' }, 'Nom d’utilisateur'), name),
    h('div', { class: 'field' }, h('label', { for: 'login-password' }, 'Mot de passe'), password),
    errorBox, button,
    users.length ? h('button', { class: 'btn quiet', type: 'button', on: { click: () => pick(users) } }, 'Choisir un autre profil') : null,
    codeButton(() => form(user, users)));
    fields.addEventListener('animationend', () => fields.classList.remove('shake'));
    if (user) name.tabIndex = -1;
    // The keyboard's « suivant » goes to the password, rather than sending the form without it.
    name.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); password.focus(); } });
    body.append(fields);
    // Within the touch that picked the profile: iOS only brings the keyboard up for a focus a touch asked for.
    (user ? password : name).focus({ preventScroll: true });
  }

  function pick(users) {
    clear(body);
    lead.textContent = 'Qui regarde ?';
    const list = h('div', { class: 'users', role: 'list' }, users.map((user) => h('button', {
      class: 'user-pick', role: 'listitem',
      on: {
        click: (e) => {
          const tile = e.currentTarget;
          if (user.HasPassword) form(user, users);
          else finish(() => api.signIn(user.Name, ''), errorBox, (on) => { tile.disabled = on; tile.classList.toggle('busy', on); });
        },
      },
    }, avatar(user), h('span', { class: 'clamp-1' }, user.Name))));
    const errorBox = h('p', { class: 'form-error', role: 'alert' }, notice ?? '');
    body.append(list, errorBox, h('button', { class: 'btn quiet', on: { click: () => { lead.textContent = 'Connecte-toi avec ton compte Jellyfin.'; form(null, users); } } }, 'Autre compte'),
    codeButton(() => pick(users)));
  }

  let failed = false;
  async function load() {
    failed = false;
    clear(body).append(h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    const [info, users, quick] = await Promise.all([api.publicInfo().catch((e) => ({ error: e })), api.publicUsers().catch(() => []), api.quickConnectEnabled()]);
    if (info.error) {
      failed = true;
      clear(body).append(h('p', { class: 'lead' }, info.error.message), h('button', { class: 'btn', on: { click: load } }, icon('refresh', { size: 20 }), 'Réessayer'));
      return;
    }
    codes = quick;
    if (info.ServerName) title.textContent = `Bienvenue sur ${info.ServerName}`;
    if (users.length) pick(users); else form();
  }

  load();
  // Back from a server that did not answer: only a failed load is tried again, never a form being filled in.
  // Leaving the screen ends the asking for a code's approval.
  return { el, title: 'Connexion', refresh: () => { if (failed) load(); }, leave: () => { asking++; } };
}
