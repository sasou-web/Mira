// « En ce moment sur Jellyfin », opened from Réglages by an administrator: who plays what on the server, the file as it
// is or converted, on which hardware and why (viewers.js, as Mira for Windows writes it), with « Arrêter » for a device
// that takes it. Read again every 5 s while the screen shows, and only then.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { device } from '../session.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { parseViewers, who, what, how, titleOf } from '../viewers.js';
import { tr, trn } from '../i18n.js';

const EVERY = 5000;

export function create() {
  const el = h('div', { class: 'view now-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture des sessions de Jellyfin…'));
  const list = h('div', { class: 'group now-list', hidden: true });
  const note = h('p', { class: 'group-note', hidden: true },
    tr('Jellyfin dit comment il envoie chaque titre : le fichier tel quel, ou converti, et pourquoi. Une conversion par le processeur peut faire ramer la lecture ; Mira pour Windows peut la confier à la carte graphique. « Arrêter » coupe la lecture sur l’appareil qui regarde, sans rien changer à son compte.'));
  const body = h('div', {}, list, note);
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('En ce moment')), status),
    body);

  let timer = 0, version = 0, shown = '', failed = false, active = false;
  // The last line read of each session, for « Arrêter » on a row kept from an earlier read.
  let latest = new Map();

  async function load() {
    const mine = ++version;
    try {
      const viewers = parseViewers(await api.sessions(), device.id);
      if (mine !== version) return;
      if (failed) { failed = false; shown = ''; body.replaceChildren(list, note); }
      render(viewers);
    } catch (error) {
      if (mine !== version) return;
      // A screen already filled keeps its last list (a hiccup on Tailscale); an empty one says why.
      if (shown) { say(error.message); return; }
      failed = true;
      say('');
      body.replaceChildren(errorState(error, () => { say(tr('Lecture des sessions de Jellyfin…')); load(); }));
    }
  }

  function render(viewers) {
    const lines = viewers.map((viewer) => ({ viewer, who: who(viewer), what: what(viewer), ...how(viewer) }));
    latest = new Map(lines.map((line) => [line.viewer.sessionId, line]));
    say(lines.length === 0 ? tr('Personne ne regarde rien sur Jellyfin pour l’instant.')
      : trn(lines.length, '{n} lecture en cours sur Jellyfin.', '{n} lectures en cours sur Jellyfin.'));
    list.hidden = note.hidden = lines.length === 0;
    // The same sessions: only their texts change (the time, every 5 s), so a button under the finger or the keyboard
    // stays where it is. Rows are made again when a session comes or goes.
    const signature = JSON.stringify(lines.map((x) => [x.viewer.sessionId, x.viewer.canStop && !x.viewer.own]));
    if (signature === shown) {
      lines.forEach((line, i) => {
        const row = list.children[i];
        row.querySelector('.now-who').textContent = line.who;
        row.querySelector('.now-what').textContent = line.what;
        row.querySelector('.now-how').textContent = line.how;
        const why = row.querySelector('.now-why');
        why.textContent = line.why ?? ''; why.hidden = !line.why;
        const button = row.querySelector('.btn');
        if (button) button.setAttribute('aria-label', tr('Arrêter la lecture sur {name}', { name: line.who }));
      });
      return;
    }
    shown = signature;
    list.replaceChildren(...lines.map((line) => h('div', { class: 'item static now-item', dataset: { session: line.viewer.sessionId } },
      h('span', { class: 'grow' },
        h('span', { class: 'now-who' }, line.who),
        h('span', { class: 'now-what' }, line.what),
        h('span', { class: 'sub now-how' }, line.how),
        h('span', { class: 'sub now-why', hidden: !line.why }, line.why ?? '')),
      line.viewer.canStop && !line.viewer.own
        ? h('button', { class: 'btn small', 'aria-label': tr('Arrêter la lecture sur {name}', { name: line.who }), on: { click: () => confirmStop(latest.get(line.viewer.sessionId) ?? line) } }, tr('Arrêter'))
        : null)));
  }
  /** The line under the title, said by screen readers only when it changes. */
  function say(text) { if (status.textContent !== text) status.textContent = text; }

  function confirmStop({ viewer, who: name }) {
    sheet({
      title: tr('Arrêter cette lecture ?'),
      body: h('p', { class: 'now-confirm' }, tr('« {title} » s’arrête sur {name}, qui le dit à l’écran. Rien d’autre ne change pour ce compte.', { title: titleOf(viewer), name })),
      items: [{ label: tr('Arrêter la lecture'), danger: true, run: () => stop(viewer, name) }],
    });
  }

  async function stop(viewer, name) {
    try {
      await api.stopSession(viewer.sessionId);
      toast(tr('Arrêt demandé à {name}.', { name }));
    } catch (error) {
      // Refused (another account, without the rights) or already gone.
      toast(error.status === 403 ? tr('Seul un administrateur de Jellyfin peut arrêter la lecture d’un autre compte.')
        : error.status === 404 ? tr('Cette lecture est déjà terminée.') : error.message);
    }
    // The device takes a moment to stop and tell Jellyfin.
    setTimeout(() => { if (active) load(); }, 1500);
  }

  // Only while the screen shows and the phone is in front: Jellyfin is not asked for nothing.
  const watch = () => {
    clearInterval(timer); timer = 0;
    if (active && document.visibilityState === 'visible') { timer = setInterval(load, EVERY); load(); }
  };
  document.addEventListener('visibilitychange', watch);
  return {
    el, title: tr('En ce moment'), keep: true,
    enter() { active = true; watch(); },
    leave() { active = false; watch(); },
    refresh: () => load(),
    dispose() { active = false; watch(); document.removeEventListener('visibilitychange', watch); },
  };
}
