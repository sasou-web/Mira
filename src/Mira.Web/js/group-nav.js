// « Regarder ensemble » in sight on every screen while this device is in a group, as the rail button of Mira for Windows
// and Mac: at the foot of the rail on a tablet or a computer, a pill above the tab bar on a phone (the tab bar keeps its
// five tabs). Tapped: the group and who is in it, its title, « Inviter quelqu'un » and « Quitter le groupe ».
// Nothing while a screen has no tab bar (the player, a sign-in): the player has its own pill (player/group-pill.js).
import { api } from './api.js';
import { h, icon } from './dom.js';
import { tr } from './i18n.js';
import { session } from './session.js';
import { sheet, titleHref } from './components.js';
import { together } from './together.js';
import { inviteSheet } from './invite.js';
import { countText, indicatorText, groupLabel, groupStatus, groupText, people, titleName } from './player/sync.js';

let shown = false;

/** The group's button in the rail (`rail`: the tab bar, a rail on wide screens) and its pill on a phone, in `app`. */
export function showGroupNav(rail, app) {
  if (shown) return;
  shown = true;
  const count = h('span', { class: 'group-count', 'aria-hidden': 'true' });
  const railButton = h('button', { type: 'button', class: 'rail-group', hidden: true, 'aria-haspopup': 'dialog', on: { click: open } },
    h('span', { class: 'rail-group-icon' }, icon('users'), count), h('span', { 'aria-hidden': 'true' }, tr('Groupe')));
  const pillText = h('span', { class: 'group-bar-text' });
  const pill = h('button', { type: 'button', class: 'group-bar', hidden: true, 'aria-haspopup': 'dialog', on: { click: open } },
    icon('users', { size: 18 }), pillText);
  rail.append(railButton);
  // Before the tabs, as it reads: Tab reaches it after the page, then the tabs.
  rail.before(pill);

  function render() {
    const group = together.group;
    railButton.hidden = pill.hidden = !group;
    app.classList.toggle('in-group', !!group);
    if (!group) return;
    const said = indicatorText(group, session.current?.userName);
    count.textContent = countText(group);
    railButton.setAttribute('aria-label', said); railButton.title = said;
    pill.setAttribute('aria-label', said);
    // Short on a phone: the group and its count, or what holds the picture (a wait, a pause).
    const status = groupStatus(group, session.current?.userName);
    pillText.textContent = status?.alert ? status.text : groupText(group);
  }
  together.watch(render);
  render();
}

async function open() {
  const group = together.group;
  if (!group) return;
  const queue = together.queue;
  // The group's title, named: one request, left out when Jellyfin is slow to answer.
  const title = queue?.itemId ? await Promise.race([api.item(queue.itemId).catch(() => null), new Promise((resolve) => { setTimeout(resolve, 3000, null); })]) : null;
  if (together.group !== group) return;
  const status = groupStatus(group, session.current?.userName);
  const names = group.Participants ?? [];
  sheet({
    title: tr('Regarder ensemble'),
    body: h('p', { class: 'sheet-note' }, status?.alert ? status.text : tr('Lecture lance un titre pour tout le groupe, au même moment sur chaque appareil. Pause, lecture et déplacements sont partagés.')),
    items: [
      { symbol: 'users', label: groupLabel(group.GroupName), sub: names.length ? names.join(', ') : people(1), selected: true },
      title && { symbol: 'play', label: tr('Voir « {title} »', { title: titleName(title) }), sub: tr('Le titre du groupe'), run: () => { location.hash = titleHref(title); } },
      { symbol: 'link', label: tr('Inviter quelqu’un'), sub: tr('Un lien à envoyer : ton ami l’ouvre et rejoint le groupe.'), run: () => inviteSheet(together.group) },
      { label: tr('Quitter le groupe'), danger: true, run: () => together.leave() },
    ].filter(Boolean),
  });
}
