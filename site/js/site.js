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
