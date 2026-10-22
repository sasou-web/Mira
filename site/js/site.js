// miracinema.app: what moves with the page. Everything here only adds to a page that is complete without it.
const reduced = matchMedia('(prefers-reduced-motion: reduce)');

// The bar turns to frosted glass once the page leaves the top.
const bar = document.querySelector('.top');
const screen = document.querySelector('.screen');
const phones = document.querySelector('.phones');

const clamp = value => Math.min(1, Math.max(0, value));
let pending = false;
function frame() {
  pending = false;
  const height = innerHeight;
  bar.classList.toggle('scrolled', scrollY > 8);
  if (reduced.matches) return;
  // The big screen straightens as its top climbs from the bottom of the window to its upper third.
  if (screen) {
    const top = screen.getBoundingClientRect().top;
    screen.style.setProperty('--tilt', clamp((top - height * 0.2) / (height * 0.6)).toFixed(3));
  }
  // The phones fan out while they cross the window.
  if (phones) {
    const box = phones.getBoundingClientRect();
    phones.style.setProperty('--spread', clamp(1 - (box.top + box.height / 2) / height).toFixed(3));
  }
}
const schedule = () => { if (!pending) { pending = true; requestAnimationFrame(frame); } };
addEventListener('scroll', schedule, { passive: true });
addEventListener('resize', schedule);
reduced.addEventListener('change', () => {
  screen?.style.removeProperty('--tilt');
  phones?.style.removeProperty('--spread');
  schedule();
});
frame();

// Blocks come up as they enter the window, once.
const reveal = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    entry.target.classList.add('in');
    reveal.unobserve(entry.target);
  }
}, { rootMargin: '0px 0px -12% 0px' });
document.querySelectorAll('.reveal').forEach(element => reveal.observe(element));

// The tour: the step in the middle of the window picks the picture on the screen beside it.
const steps = [...document.querySelectorAll('.step')];
const shots = [...document.querySelectorAll('.stage img')];
const marks = [...document.querySelectorAll('.progress span')];
function show(index) {
  steps.forEach((step, i) => step.classList.toggle('on', i === index));
  shots.forEach((shot, i) => shot.classList.toggle('on', i === index));
  marks.forEach((mark, i) => { mark.classList.toggle('on', i === index); mark.classList.toggle('done', i < index); });
}
const middle = new IntersectionObserver(entries => {
  for (const entry of entries) if (entry.isIntersecting) show(steps.indexOf(entry.target));
}, { rootMargin: '-45% 0px -45% 0px' });
steps.forEach(step => middle.observe(step));

// The set-up guide ticks itself off once in view, one line after the other.
const checklist = document.querySelector('.checklist');
if (checklist) {
  const lines = [...checklist.children];
  new IntersectionObserver((entries, observer) => {
    if (!entries.some(entry => entry.isIntersecting)) return;
    observer.disconnect();
    lines.forEach((line, i) => setTimeout(() => line.classList.add('done'), reduced.matches ? 0 : 500 + i * 650));
  }, { threshold: 0.5 }).observe(checklist);
}

// The big screen leans a little toward the pointer, on a computer with a mouse.
if (screen && matchMedia('(hover: hover) and (pointer: fine)').matches) {
  const hero = document.querySelector('.hero');
  hero.addEventListener('pointermove', event => {
    if (reduced.matches) return;
    screen.style.setProperty('--look-x', ((event.clientX / innerWidth) * 2 - 1).toFixed(3));
    screen.style.setProperty('--look-y', ((event.clientY / innerHeight) * 2 - 1).toFixed(3));
  });
  hero.addEventListener('pointerleave', () => {
    screen.style.removeProperty('--look-x');
    screen.style.removeProperty('--look-y');
  });
}

// The way in that fits this device: the Mac's disk image on a Mac, Mira web on a phone; Windows otherwise, as without
// scripts. An iPad says it is a Mac, but it has a touch screen.
const touchMac = /Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1;
const device = /iPhone|iPad|iPod|Android/.test(navigator.userAgent) || touchMac ? 'phone'
  : /Mac OS X|Macintosh/.test(navigator.userAgent) ? 'mac' : 'windows';
if (device !== 'windows') {
  document.querySelectorAll('.actions [data-for]').forEach(link => { link.hidden = link.dataset.for !== device; });
}
if (/Windows|Mac OS X|Macintosh|iPhone|iPad|iPod|Android/.test(navigator.userAgent)) {
  const card = document.querySelector(`.download[data-for="${device}"]`);
  card?.classList.add('mine');
  card?.querySelector('.yours')?.removeAttribute('hidden');
}

// Watch together, for real: one position for both lanes, and a pause on either side pauses both, as in Mira. Left to
// itself, the friend pauses once in a while.
const sync = document.querySelector('.sync');
if (sync) {
  const toggles = [...sync.querySelectorAll('.lane-toggle')];
  const state = sync.querySelector('.sync-state span');
  const LENGTH = 18000;
  let position = 0.08, playing = !reduced.matches, touched = false, last = 0, resumeAt = 0;
  sync.classList.add('live');
  const render = () => {
    sync.style.setProperty('--pos', position.toFixed(4));
    sync.classList.toggle('paused', !playing);
    for (const toggle of toggles) {
      toggle.setAttribute('aria-label', playing ? toggle.dataset.pause : toggle.dataset.play);
      toggle.querySelector('use').setAttribute('href', toggle.querySelector('use').getAttribute('href').replace(/#\w+$/, playing ? '#pause' : '#play'));
    }
  };
  const press = (who, user) => {
    // Read aloud only what a person did: the friend's pauses, left to themselves, would come back every loop.
    if (user && !touched) { touched = true; state.setAttribute('aria-live', 'polite'); }
    playing = !playing;
    state.textContent = playing ? sync.dataset.playing : who === 0 ? sync.dataset.byYou : sync.dataset.byFriend;
    render();
    if (playing) requestAnimationFrame(tick);
  };
  function tick(now) {
    if (!playing) return;
    if (last && now - last < 200) {
      const before = position;
      position += (now - last) / LENGTH;
      if (position >= 0.94) position = 0.08;
      // Unless someone has pressed a button, the friend pauses near the middle, then plays again.
      if (!touched && before < 0.46 && position >= 0.46) {
        press(1, false);
        resumeAt = setTimeout(() => { if (!touched && !playing) press(1, false); }, 2400);
      }
      render();
    }
    last = now;
    if (playing) requestAnimationFrame(tick);
    else last = 0;
  }
  toggles.forEach((toggle, who) => {
    toggle.hidden = false;
    toggle.addEventListener('click', () => { clearTimeout(resumeAt); last = 0; press(who, true); });
  });
  if (!playing) state.textContent = sync.dataset.playing;
  render();
  requestAnimationFrame(tick);
}
