// « Regarder ensemble » in the player: what the group is doing (sync.js groupStatus), at the top of Mira's own player
// and under Apple's. A wait or a pause for the group, and someone coming or going, stay in sight; « Ensemble · … » shows
// with the controls only.
import { h, icon } from '../dom.js';
import { session } from '../session.js';
import { together } from '../together.js';
import { groupStatus } from './sync.js';

// How long « Marc a rejoint le groupe » stays.
const MEMBER = 4000;

export function groupPill(video) {
  const text = h('span');
  const el = h('div', { class: 'group-pill', role: 'status', 'aria-live': 'polite', hidden: true }, icon('users', { size: 16 }), text);
  let member = '', memberTimer = 0;
  function update() {
    // Loading here (the stream, or a seek): Jellyfin's wait may be for this device, not for the others.
    const status = groupStatus(together.group, session.current?.userName, video.readyState < 3 || video.seeking);
    el.hidden = !status;
    if (!status) return;
    const said = member || status.text;
    if (text.textContent !== said) text.textContent = said;
    el.classList.toggle('alert', !!member || status.alert);
  }
  const unwatch = together.watch(update);
  const unmember = together.onMember((said) => {
    member = said;
    clearTimeout(memberTimer);
    memberTimer = setTimeout(() => { member = ''; update(); }, MEMBER);
    update();
  });
  const events = ['waiting', 'playing', 'seeking', 'seeked', 'canplay', 'loadstart'];
  for (const name of events) video.addEventListener(name, update);
  update();
  return {
    el,
    dispose() {
      unwatch(); unmember(); clearTimeout(memberTimer);
      for (const name of events) video.removeEventListener(name, update);
    },
  };
}
