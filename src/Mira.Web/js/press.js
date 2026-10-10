// Pressed looks on touch screens, drawn the way iOS draws them: a moment after the finger lands (70 ms), gone as
// soon as it moves or the page scrolls, kept a little after a tap. WebKit keeps :active for the whole of a scroll
// that starts on an element, so app.css styles .pressed there, and :active only for a mouse.
const PRESSABLE = '.card, .btn, .chip, .round, .item, .sheet-item, .tracks-line, .tool, .tabbar a, .section-head a, .user-pick, .recent button';

export function setupPress() {
  if (!matchMedia('(pointer: coarse)').matches && !('ontouchstart' in window)) return;
  document.documentElement.classList.add('touch');
  let target = null, timer = 0, startX = 0, startY = 0;
  const release = (after = 0) => {
    clearTimeout(timer);
    const was = target;
    target = null;
    if (!was) return;
    if (after) setTimeout(() => was.classList.remove('pressed'), after); else was.classList.remove('pressed');
  };
  const options = { passive: true, capture: true };
  document.addEventListener('touchstart', (e) => {
    release();
    if (e.touches.length !== 1) return;
    const found = e.target.closest?.(PRESSABLE);
    if (!found || found.disabled) return;
    target = found; startX = e.touches[0].clientX; startY = e.touches[0].clientY;
    timer = setTimeout(() => target?.classList.add('pressed'), 70);
  }, options);
  document.addEventListener('touchmove', (e) => {
    if (target && Math.hypot(e.touches[0].clientX - startX, e.touches[0].clientY - startY) > 8) release();
  }, options);
  // A quick tap still shows it was taken.
  document.addEventListener('touchend', () => { if (target) { target.classList.add('pressed'); release(90); } }, options);
  document.addEventListener('touchcancel', () => release(), options);
  addEventListener('scroll', () => release(), options);
}
