// Sign-in: the server is the one Mira web comes from; pick your profile, type your password, done.
import { api, base } from '../api.js';
import { h, icon, clear, initials } from '../dom.js';
import { isIOS, isStandalone } from '../session.js';
import { markSvg, resetScreens } from '../app.js';

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

  async function finish(name, password, errorBox, button) {
    button.disabled = true; button.textContent = 'Connexion…'; errorBox.textContent = '';
    try {
      await api.signIn(name, password);
      resetScreens();
      const next = sessionStorage.getItem('mira.next');
      sessionStorage.removeItem('mira.next');
      location.replace(next && !next.startsWith('#/connexion') ? next : '#/');
    } catch (error) {
      errorBox.textContent = error.status === 401 ? 'Nom d’utilisateur ou mot de passe incorrect.' : error.message;
      button.disabled = false; button.textContent = 'Se connecter';
    }
  }

  function form(user = null, users = []) {
    clear(body);
    const errorBox = h('p', { class: 'form-error', role: 'alert' }, notice ?? '');
    const name = h('input', { class: 'input', id: 'login-name', autocomplete: 'username', autocapitalize: 'none', autocorrect: 'off', spellcheck: false, enterkeyhint: 'next', value: user?.Name ?? '' });
    const password = h('input', { class: 'input', id: 'login-password', type: 'password', autocomplete: 'current-password', enterkeyhint: 'go' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, 'Se connecter');
    const fields = h('form', {
      class: 'login-form',
      on: { submit: (e) => { e.preventDefault(); if (!name.value.trim()) { name.focus(); return; } finish(name.value.trim(), password.value, errorBox, button); } },
    },
    user ? h('div', { style: { display: 'flex', alignItems: 'center', gap: '12px' } }, avatar(user, 56), h('div', { class: 'h3' }, user.Name)) : null,
    h('div', { class: 'field', hidden: !!user }, h('label', { for: 'login-name' }, 'Nom d’utilisateur'), name),
    h('div', { class: 'field' }, h('label', { for: 'login-password' }, 'Mot de passe'), password),
    errorBox, button,
    users.length ? h('button', { class: 'btn quiet', type: 'button', on: { click: () => pick(users) } }, 'Choisir un autre profil') : null);
    body.append(fields);
    requestAnimationFrame(() => (user ? password : name).focus());
  }

  function pick(users) {
    clear(body);
    lead.textContent = 'Qui regarde ?';
    const list = h('div', { class: 'users', role: 'list' }, users.map((user) => h('button', {
      class: 'user-pick', role: 'listitem',
      on: { click: (e) => user.HasPassword ? form(user, users) : finish(user.Name, '', errorBox, e.currentTarget) },
    }, avatar(user), h('span', { class: 'clamp-1' }, user.Name))));
    const errorBox = h('p', { class: 'form-error', role: 'alert' }, notice ?? '');
    body.append(list, errorBox, h('button', { class: 'btn quiet', on: { click: () => { lead.textContent = 'Connecte-toi avec ton compte Jellyfin.'; form(null, users); } } }, 'Autre compte'));
  }

  async function load() {
    clear(body).append(h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    const [info, users] = await Promise.all([api.publicInfo().catch((e) => ({ error: e })), api.publicUsers().catch(() => [])]);
    if (info.error) {
      clear(body).append(h('p', { class: 'lead' }, info.error.message), h('button', { class: 'btn', on: { click: load } }, icon('refresh', { size: 20 }), 'Réessayer'));
      return;
    }
    if (info.ServerName) title.textContent = `Bienvenue sur ${info.ServerName}`;
    if (users.length) pick(users); else form();
  }

  load();
  return { el, title: 'Connexion' };
}
