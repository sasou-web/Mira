// Sign-in: the server is the one Mira web comes from; pick your profile, type your password, done.
import { api, base } from '../api.js';
import { h, icon, clear, initials } from '../dom.js';
import { isIOS, isStandalone } from '../session.js';
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
  const lead = h('p', { class: 'lead' }, 'Connecte-toi avec ton compte Jellyfin.');
  const body = h('div', { style: { display: 'grid', justifyItems: 'center', gap: '22px', width: '100%' } });
  el.append(markSvg(64), h('div', { style: { display: 'grid', gap: '8px', justifyItems: 'center' } }, title, lead), body);
  const hint = installHint();
  if (hint) el.append(hint);

  const notice = sessionStorage.getItem('mira.notice');
  sessionStorage.removeItem('mira.notice');

  /** busy(true) while Jellyfin checks, busy(false) if it refuses: the button or the profile touched says so. */
  async function finish(name, password, errorBox, busy) {
    busy(true); errorBox.textContent = '';
    try {
      await api.signIn(name, password);
      resetScreens();
      const next = sessionStorage.getItem('mira.next');
      sessionStorage.removeItem('mira.next');
      replaceRoute(next && !next.startsWith('#/connexion') ? next : '#/');
    } catch (error) {
      errorBox.textContent = error.status === 401 ? 'Nom d’utilisateur ou mot de passe incorrect.' : error.message;
      busy(false, error);
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
      on: { submit: (e) => { e.preventDefault(); if (!name.value.trim()) { name.focus(); return; } finish(name.value.trim(), password.value, errorBox, busy); } },
    },
    user ? h('div', { style: { display: 'flex', alignItems: 'center', gap: '12px' } }, avatar(user, 56), h('div', { class: 'h3' }, user.Name)) : null,
    // A profile picked: its name stays in the form, out of sight, so that the iPhone's passwords know whose it is.
    h('div', { class: ['field', user && 'sr'], 'aria-hidden': user ? 'true' : null }, h('label', { for: 'login-name' }, 'Nom d’utilisateur'), name),
    h('div', { class: 'field' }, h('label', { for: 'login-password' }, 'Mot de passe'), password),
    errorBox, button,
    users.length ? h('button', { class: 'btn quiet', type: 'button', on: { click: () => pick(users) } }, 'Choisir un autre profil') : null);
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
          else finish(user.Name, '', errorBox, (on) => { tile.disabled = on; tile.classList.toggle('busy', on); });
        },
      },
    }, avatar(user), h('span', { class: 'clamp-1' }, user.Name))));
    const errorBox = h('p', { class: 'form-error', role: 'alert' }, notice ?? '');
    body.append(list, errorBox, h('button', { class: 'btn quiet', on: { click: () => { lead.textContent = 'Connecte-toi avec ton compte Jellyfin.'; form(null, users); } } }, 'Autre compte'));
  }

  let failed = false;
  async function load() {
    failed = false;
    clear(body).append(h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    const [info, users] = await Promise.all([api.publicInfo().catch((e) => ({ error: e })), api.publicUsers().catch(() => [])]);
    if (info.error) {
      failed = true;
      clear(body).append(h('p', { class: 'lead' }, info.error.message), h('button', { class: 'btn', on: { click: load } }, icon('refresh', { size: 20 }), 'Réessayer'));
      return;
    }
    if (info.ServerName) title.textContent = `Bienvenue sur ${info.ServerName}`;
    if (users.length) pick(users); else form();
  }

  load();
  // Back from a server that did not answer: only a failed load is tried again, never a form being filled in.
  return { el, title: 'Connexion', refresh: () => { if (failed) load(); } };
}
