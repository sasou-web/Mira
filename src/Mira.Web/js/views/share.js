// « Ouvrir sur un autre appareil »: the address of Mira on the home network, as a QR code for the phone's camera.
// Mira for Windows opens this page once it has installed Mira web on Jellyfin.
import { api, base } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { session } from '../session.js';
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

function qr(text) {
  const code = qrcode(0, 'M');
  code.addData(text);
  code.make();
  const box = h('div', { class: 'qr', role: 'img', 'aria-label': `QR code de ${text}` });
  box.innerHTML = code.createSvgTag({ cellSize: 4, margin: 0, scalable: true });
  return box;
}

export function create() {
  const el = h('div', { class: 'view gate' });

  async function render() {
    clear(el).append(h('div', { class: 'spinner', role: 'status', 'aria-label': 'Chargement' }));
    const address = await phoneAddress();
    const sameMachine = loopback(new URL(address).hostname);
    clear(el).append(
      markSvg(56),
      h('div', { style: { display: 'grid', gap: '8px', justifyItems: 'center' } },
        h('h1', {}, 'Mira sur ton téléphone'),
        h('p', { class: 'lead' }, 'Scanne ce code avec l’appareil photo de ton iPhone ou de ton téléphone Android, connecté au même Wi-Fi.')),
      qr(address),
      h('p', { class: 'address' }, address),
      sameMachine ? h('p', { class: 'form-error' }, 'Jellyfin ne connaît pas encore son adresse sur le réseau : ouvre plutôt Mira depuis l’adresse IP du PC, par exemple http://192.168.1.20:8096/Mira/.') : null,
      h('div', { class: 'hint' },
        h('div', { class: 'step' }, icon('qr'), h('span', {}, 'Ouvre le lien, puis connecte-toi avec ton compte Jellyfin.')),
        h('div', { class: 'step' }, icon('share'), h('span', {}, 'Sur iPhone : ', h('b', {}, 'Partager'), ', puis ', h('b', {}, 'Sur l’écran d’accueil'), ' pour l’avoir comme une app.')),
        h('div', { class: 'step' }, icon('phone'), h('span', {}, 'Hors de chez toi : même adresse, avec l’IP Tailscale du PC (100.x.y.z) à la place de celle-ci.'))),
      session.current
        ? h('button', { class: 'btn', on: { click: () => goBack('#/reglages') } }, icon('back', { size: 20 }), 'Retour')
        : h('a', { class: 'btn', href: '#/connexion' }, 'Se connecter sur cet appareil'));
  }

  render();
  return { el, title: 'Sur ton téléphone' };
}
