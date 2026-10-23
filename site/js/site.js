// miracinema.app: the page plays as a film in Mira's player. Everything here only adds to a page that is complete
// without it: the chapters, the subtitles and the menus are in the HTML.
const reduced = matchMedia('(prefers-reduced-motion: reduce)');
const sticky = matchMedia('(min-width: 901px)');
const root = document.documentElement;
const clamp = (value, low = 0, high = 1) => Math.min(high, Math.max(low, value));
const RUNTIME = 360; // the film lasts six minutes, from the top of the page to the bottom
const stamp = seconds => {
  const s = Math.round(clamp(seconds, 0, RUNTIME));
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`;
};

const bar = document.querySelector('.top');
const player = document.querySelector('.player');
const timeline = player.querySelector('.timeline');
const scrub = player.querySelector('.scrub');
const rail = player.querySelector('.rail');
const now = player.querySelector('.time .now');
const current = player.querySelector('.current');
const playButton = player.querySelector('.play');
const opening = document.querySelector('.opening');
timeline.hidden = false;
player.querySelector('.time').hidden = false;

// The bars' real heights, for the frame between them.
const measure = () => {
  root.style.setProperty('--top-h', `${bar.offsetHeight}px`);
  root.style.setProperty('--player-h', `${player.offsetHeight}px`);
};
new ResizeObserver(measure).observe(player);
measure();

// Chapters: every part of the page, named as in the chapters' menu, placed in time by where it starts.
const links = [...document.querySelectorAll('#chapitres a')];
const chapters = links.map(link => ({ link, section: document.querySelector(link.hash), name: link.querySelector('span').textContent, at: 0 }));
const extent = () => Math.max(1, root.scrollHeight - innerHeight);
const timeOf = y => (clamp(y / extent()) * RUNTIME);
const yOf = seconds => clamp(seconds / RUNTIME) * extent();
const marks = chapters.slice(1).map(() => rail.appendChild(document.createElement('span')));
marks.forEach(mark => mark.classList.add('mark'));
function place() {
  for (const chapter of chapters) {
    chapter.at = timeOf(chapter.section.getBoundingClientRect().top + scrollY - bar.offsetHeight);
    chapter.link.querySelector('time').textContent = stamp(chapter.at);
  }
  chapters.slice(1).forEach((chapter, i) => { marks[i].style.left = `${(chapter.at / RUNTIME) * 100}%`; });
}
const chapterAt = seconds => chapters.reduce((found, chapter) => (seconds + 0.5 >= chapter.at ? chapter : found), chapters[0]);

// The position: the bar, the time, the chapter, as the page scrolls.
let shown = null;
function position() {
  const seconds = timeOf(scrollY);
  scrub.style.setProperty('--p', (seconds / RUNTIME).toFixed(4));
  // A chapter is on once its title reaches the middle of the frame.
  const chapter = chapterAt(timeOf(scrollY + innerHeight * 0.45));
  now.textContent = stamp(seconds);
  scrub.setAttribute('aria-valuenow', String(Math.round(seconds)));
  scrub.setAttribute('aria-valuetext', `${stamp(seconds)}, ${chapter.name}`);
  if (chapter !== shown) {
    shown = chapter;
    current.textContent = chapter.name;
    links.forEach(link => link.classList.toggle('on', link === chapter.link));
  }
  return seconds;
}

// Seeking: a click or a drag on the bar, the arrows on the keyboard. The preview names the chapter there.
const preview = timeline.querySelector('.preview');
const previewImage = preview.querySelector('.preview-image');
const previewChapter = preview.querySelector('.preview-chapter');
const previewTime = preview.querySelector('.preview-time');
const thumb = document.createElement('img');
thumb.alt = ''; thumb.width = 200; thumb.height = 113;
const fraction = event => {
  const box = scrub.getBoundingClientRect();
  return clamp((event.clientX - box.left) / box.width);
};
const seek = seconds => scrollTo({ top: yOf(seconds), behavior: 'instant' });
function peek(at) {
  const seconds = at * RUNTIME, chapter = chapterAt(seconds);
  scrub.style.setProperty('--h', at.toFixed(4));
  previewChapter.textContent = chapter.name;
  previewTime.textContent = stamp(seconds);
  const source = chapter.section.dataset.thumb;
  if (source) { if (thumb.getAttribute('src') !== source) thumb.src = source; if (!thumb.isConnected) previewImage.append(thumb); }
  else thumb.remove();
  const width = timeline.offsetWidth, half = preview.offsetWidth / 2;
  preview.style.left = `${clamp(at * width - half, 0, width - half * 2)}px`;
  timeline.classList.add('peek');
}
let dragging = false;
scrub.addEventListener('pointerdown', event => {
  dragging = true; stopPlaying();
  scrub.setPointerCapture(event.pointerId);
  scrub.classList.add('dragging');
  seek(fraction(event) * RUNTIME); peek(fraction(event));
});
scrub.addEventListener('pointermove', event => {
  if (dragging) seek(fraction(event) * RUNTIME);
  if (dragging || event.pointerType === 'mouse') peek(fraction(event));
});
const release = () => { dragging = false; scrub.classList.remove('dragging'); timeline.classList.remove('peek'); };
scrub.addEventListener('pointerup', release);
scrub.addEventListener('pointercancel', release);
scrub.addEventListener('pointerleave', () => { if (!dragging) timeline.classList.remove('peek'); });
scrub.addEventListener('keydown', event => {
  const seconds = timeOf(scrollY);
  const index = chapters.indexOf(chapterAt(seconds));
  const to = {
    ArrowLeft: seconds - 10, ArrowDown: seconds - 10, ArrowRight: seconds + 10, ArrowUp: seconds + 10,
    PageUp: chapters[Math.max(0, seconds - chapters[index].at > 3 ? index : index - 1)].at,
    PageDown: chapters[Math.min(chapters.length - 1, index + 1)].at,
    Home: 0, End: RUNTIME,
  }[event.key];
  if (to === undefined) return;
  event.preventDefault(); stopPlaying(); seek(to);
});

// The menus close once a choice is made.
for (const menu of document.querySelectorAll('.menu')) {
  menu.addEventListener('click', event => { if (event.target.closest('a') && menu.hidePopover) menu.hidePopover(); });
}

// Play: the page scrolls by itself at the film's pace, until anyone touches anything.
let playing = false, lastFrame = 0, carried = 0;
function stopPlaying() {
  if (!playing) return;
  playing = false;
  playButton.setAttribute('aria-pressed', 'false');
  playButton.querySelector('use').setAttribute('href', playButton.querySelector('use').getAttribute('href').replace(/#\w+$/, '#play'));
}
function roll(time) {
  if (!playing) return;
  if (lastFrame) {
    carried += (time - lastFrame) / 1000 * (extent() / RUNTIME);
    const step = Math.floor(carried);
    if (step) { carried -= step; scrollTo({ top: scrollY + step, behavior: 'instant' }); }
    if (scrollY >= extent() - 1) stopPlaying();
  }
  lastFrame = time;
  requestAnimationFrame(roll);
}
playButton.hidden = reduced.matches;
reduced.addEventListener('change', () => { playButton.hidden = reduced.matches; stopPlaying(); });
playButton.addEventListener('click', () => {
  if (playing) { stopPlaying(); return; }
  playing = true; lastFrame = 0; carried = 0;
  playButton.setAttribute('aria-pressed', 'true');
  playButton.querySelector('use').setAttribute('href', playButton.querySelector('use').getAttribute('href').replace(/#\w+$/, '#pause'));
  if (scrollY >= extent() - 1) scrollTo({ top: 0, behavior: 'instant' });
  requestAnimationFrame(roll);
});
for (const type of ['wheel', 'touchmove']) addEventListener(type, stopPlaying, { passive: true });
for (const type of ['keydown', 'pointerdown']) {
  addEventListener(type, event => { if (!playButton.contains(event.target)) stopPlaying(); }, { passive: true });
}

// Resume where the last visit left off, as Mira does: kept in this browser only.
const resume = bar.querySelector('.resume');
const KEY = 'mira.site.at';
let saved = 0;
try { saved = Number(localStorage.getItem(KEY)) || 0; } catch { saved = 0; }
if (saved >= 15 && saved <= RUNTIME - 15 && !location.hash && scrollY < 40) {
  resume.querySelector('time').textContent = stamp(saved);
  resume.hidden = false;
  resume.addEventListener('click', event => {
    event.preventDefault();
    resume.hidden = true;
    scrollTo({ top: yOf(saved), behavior: reduced.matches ? 'instant' : 'smooth' });
  });
}
const keep = () => { try { localStorage.setItem(KEY, String(Math.round(timeOf(scrollY)))); } catch { /* not kept: nothing else changes */ } };
addEventListener('pagehide', keep);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') keep(); });

// The opening: the picture sinks into the dark as the film moves on, and on a wide screen it starts in scope.
function lightsDown() {
  const height = opening.offsetHeight;
  const progress = clamp(scrollY / height);
  if (reduced.matches) { opening.style.removeProperty('--open'); root.style.setProperty('--scope', '0px'); return; }
  opening.style.setProperty('--open', progress.toFixed(3));
  const view = innerHeight - bar.offsetHeight - player.offsetHeight;
  const scope = innerWidth / innerHeight > 1.25 ? Math.max(0, (view - innerWidth / 2.39) / 2) : 0;
  root.style.setProperty('--scope', `${(scope * (1 - clamp(progress * 3))).toFixed(1)}px`);
}

// Scenes: the frame stays while the page scrolls, and the subtitle under it changes, one line after the other.
const scenes = [...document.querySelectorAll('.scene')].map(scene => {
  const stage = scene.querySelector('.stage');
  const cues = [...scene.querySelectorAll('.cue')];
  const shots = [...scene.querySelectorAll('.shot')];
  scene.style.setProperty('--cues', String(cues.length));
  return { scene, stage, cues, shots, index: -1 };
});
const checklist = document.querySelector('.checklist');
let ticked = false;
function tick() {
  if (ticked || !checklist) return;
  ticked = true;
  [...checklist.children].forEach((line, i) => setTimeout(() => line.classList.add('done'), reduced.matches ? 0 : 400 + i * 600));
}
function cue(entry, index) {
  if (index === entry.index) return;
  entry.index = index;
  entry.stage.dataset.cue = String(index);
  entry.cues.forEach((line, i) => line.classList.toggle('on', i === index));
  const shot = Number(entry.cues[index].dataset.shot ?? index);
  entry.shots.forEach((frame, i) => { frame.classList.toggle('on', i === shot); frame.classList.toggle('past', i < shot); });
  if (entry.shots[shot]?.classList.contains('guide')) tick();
}
function scenesAt() {
  if (!sticky.matches) return;
  for (const entry of scenes) {
    const box = entry.scene.getBoundingClientRect();
    const run = entry.scene.offsetHeight - entry.stage.offsetHeight;
    if (box.bottom < 0 || box.top > innerHeight) continue;
    const progress = clamp((bar.offsetHeight - box.top) / Math.max(1, run));
    cue(entry, Math.min(entry.cues.length - 1, Math.floor(progress * entry.cues.length)));
  }
}
// Where the frame does not stay (a phone, a small window), the guide ticks itself off once in view.
if (checklist) {
  new IntersectionObserver((entries, observer) => {
    if (!entries.some(entry => entry.isIntersecting) || sticky.matches) return;
    observer.disconnect(); tick();
  }, { threshold: 0.5 }).observe(checklist);
}

// Intertitles come up as they enter the window, once.
const reveal = new IntersectionObserver(entries => {
  for (const entry of entries) {
    if (!entry.isIntersecting) continue;
    entry.target.classList.add('in');
    reveal.unobserve(entry.target);
  }
}, { rootMargin: '0px 0px -15% 0px' });
document.querySelectorAll('.intertitle').forEach(element => reveal.observe(element));

let pending = false;
function frame() {
  pending = false;
  position(); lightsDown(); scenesAt();
  if (!resume.hidden && timeOf(scrollY) >= saved - 2) resume.hidden = true;
}
const schedule = () => { if (!pending) { pending = true; requestAnimationFrame(frame); } };
addEventListener('scroll', schedule, { passive: true });
addEventListener('resize', () => { place(); schedule(); });
sticky.addEventListener('change', () => { place(); schedule(); });
reduced.addEventListener('change', schedule);
new ResizeObserver(() => { place(); schedule(); }).observe(document.body);
place(); frame();

// The way in that fits this device: the Mac's disk image on a Mac, Mira web on a phone; Windows otherwise, as without
// scripts. An iPad says it is a Mac, but it has a touch screen.
const touchMac = /Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1;
const device = /iPhone|iPad|iPod|Android/.test(navigator.userAgent) || touchMac ? 'phone'
  : /Mac OS X|Macintosh/.test(navigator.userAgent) ? 'mac' : 'windows';
if (device !== 'windows') {
  document.querySelectorAll('.actions [data-for]').forEach(link => { link.hidden = link.dataset.for !== device; });
}
if (/Windows|Mac OS X|Macintosh|iPhone|iPad|iPod|Android/.test(navigator.userAgent)) {
  const room = document.querySelector(`.room[data-for="${device}"]`);
  room?.classList.add('mine');
  room?.querySelector('.yours')?.removeAttribute('hidden');
}

// Watch together, for real: one position for both screens, and a pause on either side pauses both, as in Mira. Left
// to itself, the friend pauses once in a while.
const together = document.querySelector('.together');
if (together) {
  const toggles = [...together.querySelectorAll('.toggle')];
  const state = together.querySelector('.state');
  const LENGTH = 18000;
  let at = 0.08, running = !reduced.matches, touched = false, last = 0, resumeAt = 0;
  together.classList.add('live');
  together.querySelector('.try').hidden = false;
  const render = () => {
    together.style.setProperty('--pos', at.toFixed(4));
    together.classList.toggle('paused', !running);
    for (const toggle of toggles) {
      toggle.setAttribute('aria-label', running ? toggle.dataset.pause : toggle.dataset.play);
      const use = toggle.querySelector('use');
      use.setAttribute('href', use.getAttribute('href').replace(/#\w+$/, running ? '#pause' : '#play'));
    }
  };
  const press = (who, user) => {
    // Read aloud only what a person did: the friend's pauses, left to themselves, would come back every loop.
    if (user && !touched) { touched = true; state.setAttribute('aria-live', 'polite'); }
    running = !running;
    state.textContent = running ? together.dataset.playing : who === 0 ? together.dataset.byYou : together.dataset.byFriend;
    render();
    if (running) requestAnimationFrame(advance);
  };
  function advance(time) {
    if (!running) return;
    if (last && time - last < 200) {
      const before = at;
      at += (time - last) / LENGTH;
      if (at >= 0.94) at = 0.08;
      // Unless someone has pressed a button, the friend pauses near the middle, then plays again.
      if (!touched && before < 0.46 && at >= 0.46) {
        press(1, false);
        resumeAt = setTimeout(() => { if (!touched && !running) press(1, false); }, 2400);
      }
      render();
    }
    last = time;
    if (running) requestAnimationFrame(advance);
    else last = 0;
  }
  toggles.forEach((toggle, who) => {
    toggle.hidden = false;
    toggle.addEventListener('click', () => { clearTimeout(resumeAt); last = 0; press(who, true); });
  });
  render();
  requestAnimationFrame(advance);
}
