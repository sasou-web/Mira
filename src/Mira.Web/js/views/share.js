// « Ouvrir sur un autre appareil »: the address of Mira as a QR code for the phone's camera. Mira for Windows opens this
// page from its guide, with this PC's Tailscale address once phones get through there (?partout=): that one works at
// home and away, the home network's only at home.
import { api, base, isTailnet } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { session, device } from '../session.js';
import { toast } from '../components.js';
import { markSvg, goBack } from '../app.js';
import qrcode from '../../vendor/qrcode.mjs';

const loopback = (host) => /^(localhost|127\.|\[?::1\]?$)/i.test(host);

/** The address a phone on the same network can open: this one, unless it is the PC itself (localhost). */
export async function phoneAddress() {
  if (!loopback(location.hostname)) return `${location.origin}${base}/Mira/`;
  const info = await api.publicInfo().catch(() => null);
  const local = info?.LocalAddress?.replace(/\/$/, '');
  return local ? `${local}/Mira/` : `${location.origin}${base}/Mira/`;
}

/**
 * This PC on Tailscale: given by Mira for Windows (only a Tailscale address is taken, never another site), or the
 * address this page is open at when it is one. Null when Tailscale is not set up.
 */
function everywhereAddress(query) {
  try {
    const given = new URL(query?.get('partout') ?? '');
    if (/^https?:$/.test(given.protocol) && isTailnet(given.hostname)) return `${given.origin}${base}/Mira/`;
  } catch { /* none given */ }
  return isTailnet(location.hostname) ? `${location.origin}${base}/Mira/` : null;
}

function qr(text) {
  const code = qrcode(0, 'M');
  code.addData(text);
  code.make();
  const box = h('div', { class: 'qr', role: 'img', 'aria-label': `QR code de ${text}` });
  box.innerHTML = code.createSvgTag({ cellSize: 4, margin: 0, scalable: true });
  return box;
}

/**
 * Copies text from a tap. Over plain HTTP (Mira at home), Safari has no navigator.clipboard: a selected field and
 * execCommand still copy.
 */
async function copy(text) {
  if (window.isSecureContext && navigator.clipboard) return navigator.clipboard.writeText(text).then(() => true, () => false);
  const field = h('textarea', { readOnly: true, 'aria-hidden': 'true', style: { position: 'fixed', top: '0', left: '0', opacity: '0', fontSize: '16px' } });
  field.value = text;
  document.body.append(field);
  field.select(); field.setSelectionRange(0, text.length);
  let done = false;
  try { done = document.execCommand('copy'); } catch { /* not here */ }
  field.remove();
  return done;
}

export function create({ query } = {}) {
  const el = h('div', { class: 'view gate' });
  // Opened on the phone itself, the code is for another device; on the PC, for the phone.
  const onPhone = ['iPhone', 'iPad', 'Android'].includes(device.name);
  const everywhere = everywhereAddress(query);
  let mode = everywhere ? 'everywhere' : 'home';

  async function render() {
    clear(el).append(h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    const home = await phoneAddress();
    const choice = everywhere && home !== everywhere;
    if (!choice) mode = everywhere ? 'everywhere' : 'home';
    const address = mode === 'everywhere' ? everywhere : home;
    const sameMachine = loopback(new URL(address).hostname);
    const copyButton = h('button', {
      class: 'btn small',
      on: { click: async () => toast(await copy(address) ? 'Adresse copiée.' : 'Copie impossible ici : touche longuement l’adresse pour la sélectionner.') },
    }, icon('link', { size: 16 }), 'Copier l’adresse');
    const option = (key, label) => h('button', { class: 'chip', 'aria-pressed': String(mode === key), on: { click: () => { mode = key; render(); } } }, label);
    const other = 'à un autre téléphone ou à une tablette';
    const lead = mode === 'everywhere'
      ? onPhone
        ? `Montre ce code ${other}, avec Tailscale activé sur le même compte : son appareil photo ouvre Mira, chez toi comme ailleurs.`
        : 'Scanne ce code avec l’appareil photo de ton téléphone. Tailscale doit y être activé, avec le même compte que ce PC : Mira marche alors chez toi comme ailleurs.'
      : onPhone
        ? `Montre ce code ${other}, connecté au même Wi-Fi : son appareil photo ouvre Mira.`
        : 'Scanne ce code avec l’appareil photo de ton iPhone ou de ton téléphone Android, connecté au même Wi-Fi que ce PC.';
    clear(el).append(
      session.current ? h('button', { class: 'round floating-back', 'aria-label': 'Retour', on: { click: () => goBack('#/reglages') } }, icon('back')) : '',
      markSvg(56),
      h('div', { style: { display: 'grid', gap: '8px', justifyItems: 'center' } },
        h('h1', {}, onPhone ? 'Mira sur un autre appareil' : 'Mira sur ton téléphone'),
        h('p', { class: 'lead' }, lead)),
      choice ? h('div', { class: 'chips', role: 'group', 'aria-label': 'Où ce code marche', style: { margin: '0', justifyContent: 'center' } },
        option('everywhere', 'Partout, avec Tailscale'), option('home', 'Chez toi seulement')) : '',
      qr(address),
      h('p', { class: 'address' }, address),
      copyButton,
      sameMachine ? h('p', { class: 'form-error' }, 'Jellyfin ne connaît pas encore son adresse sur le réseau : ouvre plutôt Mira depuis l’adresse IP du PC, par exemple http://192.168.1.20:8096/Mira/.') : '',
      h('div', { class: 'hint' },
        mode === 'everywhere'
          ? h('div', { class: 'step' }, icon('phone'), h('span', {}, 'Pas encore Tailscale sur le téléphone ? Installe-le depuis l’App Store ou le Play Store, puis connecte-toi avec le même compte.'))
          : '',
        h('div', { class: 'step' }, icon('qr'), h('span', {}, 'Ouvre le lien, puis connecte-toi avec ton compte Jellyfin.')),
        h('div', { class: 'step' }, icon('share'), h('span', {}, 'Sur iPhone : ', h('b', {}, 'Partager'), ', puis ', h('b', {}, 'Sur l’écran d’accueil'), ' pour l’avoir comme une app.')),
        mode === 'home'
          ? h('div', { class: 'step' }, icon('offline'), h('span', {}, choice
            ? 'Cette adresse ne marche que sur le Wi-Fi de la maison.'
            : 'Hors de chez toi, cette adresse ne marche pas : installe Tailscale, puis ouvre le guide de Mira sur le PC.'))
          : ''),
      session.current ? '' : h('a', { class: 'btn', href: '#/connexion' }, 'Se connecter sur cet appareil'));
  }

  render();
  return { el, title: 'Sur ton téléphone', refresh: render };
}
