// The player: Jellyfin's stream in the browser's own video engine. On iPhone and iPad, Apple's player does it all,
// in full screen: play and pause, the timeline, AirPlay, Picture in Picture, speed, and the subtitles and audio tracks
// of its menu. Elsewhere, Mira's controls on top: resume, ±10 s, a timeline with previews, opening and recap to skip,
// the next episode, audio and subtitle tracks, speed, quality, AirPlay and Picture in Picture. Progress goes to
// Jellyfin as on Windows, and the next episode follows in the same player.
import { api, signed, ping, onForeignServer, recheckServer, isLocalHost, viaTailscale, isGuest, VERSION } from '../api.js';
import { h, icon, clear, clock, seconds, ticks, episodeCode } from '../dom.js';
import { tr, number } from '../i18n.js';
import { settings, device, session } from '../session.js';
import { artFor, imageUrl, picture } from '../images.js';
import { sheet, toast, changed, titleHref } from '../components.js';
import { goBack } from '../app.js';
import { deviceProfile, lastResortProfile, useHlsJs, audioTracks as switchesAudio } from './profile.js';
import { sharedVideo, appleNative } from './video.js';
import { trackText, trackMemory, chosenTracks, startTracks, fileTextSubtitles, carriedIndex } from './tracks.js';
import {
  knownBitrate, autoBitrate, UNMEASURED_BITRATE, nextStage, stageRequest, videoError, videoErrorText, hlsError, hlsErrorText, videoErrorStep,
  hlsErrorStep, afterCheck, resumeTicks, playbackError, chooseStream, uncappedBitrate, burnedIn, subtitleSwitch, webVttAddress,
  progressFraction, startPosition, STILL, stallWatch, stallLimit, stallStep, STALL_FORGIVEN,
} from './stream.js';
import { presentationStep } from './presentation.js';
import { mediaLine, attemptText, transcodeReasons, failureReport } from './failure.js';
import { skipLabel, COUNTDOWN, skippable, skipsItself, nextFrom, creditsFrom, nativeNextAt, trickCell, chapterMarks, chapterPassages, withChapters, withoutFromStart } from './timeline.js';
import { QUALITIES } from '../views/settings.js';
import { sleep, sleepChoices } from './sleep.js';
import { listen } from '../remote.js';
import { together } from '../together.js';
import { needsSeek, groupText } from './sync.js';
import { inviteSheet } from '../invite.js';
import { groupPill } from './group-pill.js';

// Forced subtitles (signs, foreign lines): Safari has the « forced » kind; elsewhere it reads as metadata, never drawn.
const FORCED_KIND = (() => { try { const t = document.createElement('track'); t.kind = 'forced'; return t.kind === 'forced'; } catch { return false; } })();
const PROGRESS_EVERY = 10_000;

// At home (a home network's address or name), full quality without measuring. Not through Tailscale: its address
// is the one the phone uses away from home too (on mobile data), where only a measure tells what the line takes.
const atHome = isLocalHost(location.hostname) && !viaTailscale(location.hostname);

// The last measure of the connection, kept an hour (MEASURE_KEPT): each title does not download its megabytes again.
// Forgotten when the phone may have changed networks (lost and found again, another kind of connection, a while in
// the background).
let measure = null;     // { at, speed }
let measuring = null;   // the measure on its way, shared by whoever needs it
const forgetMeasure = () => { measure = null; };
addEventListener('online', forgetMeasure);
addEventListener('offline', forgetMeasure);
navigator.connection?.addEventListener?.('change', forgetMeasure);
let leftAt = 0;
document.addEventListener('visibilitychange', () => {
  if (document.hidden) leftAt = Date.now();
  else if (leftAt && Date.now() - leftAt > 5 * 60_000) forgetMeasure();
});

/** The connection's speed (bits per second), measured once for whoever asks meanwhile; null when it cannot be. */
function measureConnection() {
  measuring ??= api.bitrateTest().then((speed) => { measure = { at: Date.now(), speed }; return speed; }, () => null)
    .finally(() => { measuring = null; });
  return measuring;
}
/** On « Automatique » away from home, without a fresh measure: measured from now, while the title itself loads. */
function measureAhead() {
  if (knownBitrate(settings.get('quality'), { atHome, measure, now: Date.now() }) == null) measureConnection();
}
/**
 * The bitrate to ask for a file of this bitrate: the chosen one, or, on « Automatique », full quality at home and,
 * elsewhere, full quality when the connection holds it, else what it allows.
 */
async function bitrate(fileBitrate) {
  const known = knownBitrate(settings.get('quality'), { atHome, measure, now: Date.now(), fileBitrate });
  if (known != null) return known;
  const speed = await measureConnection();
  return speed == null ? UNMEASURED_BITRATE : autoBitrate(speed, fileBitrate);
}

export function create({ id, query }) {
  const video = sharedVideo();
  const native = appleNative();
  // A speed chosen for another title does not follow into this one (the <video> is shared).
  video.defaultPlaybackRate = 1; video.playbackRate = 1;
  // A timer chosen on a title page whose minutes ran out before Lecture: it does not pause the film at once.
  sleep.due(Date.now());
  const el = h('div', { class: ['player', native && 'native'], role: 'region', 'aria-label': tr('Lecteur vidéo') });

  // ---------- The title playing (the next episode replaces it in the same player) ----------
  let item = null, source = null, playSessionId = '', playMethod = 'DirectPlay', hls = null;
  let offset = 0;             // progressive conversions start at the requested point: their time 0 is `offset`
  let progressive = false, total = 0, audioIndex = null, subtitleIndex = null;
  // The stage of the cascade a title refused by the player is asked at (stream.js): 0 as Jellyfin decides, 1 its video
  // converted, 2 everything converted. Kept for the title (seeks, other tracks), back to 0 for the next one.
  let fallback = 0, lastError = '';
  // The streams asked for the title, { stage, method, reasons, error } (failure.js): those that failed, and the one in
  // the <video> (current). Said under « Lecture impossible ».
  let attempts = [], current = null;
  let streamAttempt = 0;      // the opening whose stream is in the <video>: errors of an older one say nothing
  let inband = false;         // the HLS stream carries the text subtitles (Apple's player): no <track> beside it
  let started = false, ended = false, progressTimer = 0, lastReport = 0;
  let segments = [], chapters = [], trick = null, next = null, nextDismissed = false, countdown = 0, countdownTimer = 0;
  // Apple's player: where the title started (seconds), and whether the person moved into its end credits: then they
  // play to the end, and the next episode follows there, as before.
  // lastAt: the position at the previous tick, to tell playing on from a jump.
  let titleStart = 0, creditsWatched = false, lastAt = 0;
  let pendingStart = 0, opening = 0, beginning = 0, target = { id, fromStart: query.get('debut') === '1', tracks: chosenTracks(query) };
  const skipped = new Set();
  // ---------- The player itself ----------
  let disposed = false, closing = false, switching = false, failed = false;
  let hideTimer = 0, scrubbing = false, mode = 'inline', nativeCheck = 0, tracksSettle = 0, waitingForServer = false, retryTimer = 0, waitToken = 0;
  let streamPlaying = false;  // the current stream has played: its position says where the title is
  let currentStart = 0;       // where the current opening was asked to start (ticks), until it plays
  let resumedByUser = true;   // the next 'playing' follows a pause the person made: the controls show then, not after a stall
  let switchFrom = '';        // how the title that ended was shown (Apple's full screen, Picture in Picture) when the next began
  // A stream that no longer comes (stream.js stallWatch): the last look at it, the server being asked about it, and
  // where it was opened again after a stall (seconds; null until then, and once it has played on from there).
  let look = STILL, askingStuck = false, reopenedAt = null;
  // Paused by the person, the group or Mira itself, not by a stream that broke: browsers pause the video on an error
  // (Chromium before saying so), and a stream opened again after it must play on as it was (pausedThen).
  let pausedByChoice = false;
  // « Regarder ensemble » (together.js): what the player did itself, not to be told to the group as the person's
  // (until these times); the group's play to come; a stall told to the group; a seek told once the finger stops.
  const expect = { play: 0, pause: 0, seek: 0 };
  let groupTimer = 0, stallTimer = 0, stalled = false, readyPending = false, groupSeekTimer = 0;

  const position = () => offset + (video.currentTime || 0);
  const presentation = () => video.webkitPresentationMode ?? (video.webkitDisplayingFullscreen ? 'fullscreen' : 'inline');

  // ---------- Elements ----------
  const title = h('div', { class: 'p-title' }, h('strong', {}, ''), h('span', {}, ''));
  const airplay = h('button', { class: 'round flat', 'aria-label': 'AirPlay', hidden: true, on: { click: () => video.webkitShowPlaybackTargetPicker?.() } }, icon('airplay'));
  const pip = h('button', { class: 'round flat', 'aria-label': tr('Image dans l’image'), hidden: !pipSupported(), on: { click: togglePip } }, icon('pip'));
  const tracksButton = h('button', { class: 'round flat', 'aria-label': tr('Audio et sous-titres'), on: { click: tracksSheet } }, icon('subtitles'));
  const moreButton = h('button', { class: 'round flat', 'aria-label': tr('Vitesse, qualité et minuterie'), on: { click: moreSheet } }, icon('sliders'));
  const playButton = h('button', { class: 'round big', 'aria-label': tr('Lecture'), on: { click: (e) => { e.stopPropagation(); togglePlay(); } } }, icon('play', { size: 36 }));
  const skipMark = (name) => h('span', { class: 'skip-mark' }, icon(name, { size: 36 }), h('b', {}, '10'));
  const back10 = h('button', { class: 'round', 'aria-label': tr('Reculer de 10 secondes'), on: { click: (e) => { e.stopPropagation(); jump(-10); } } }, skipMark('back10'));
  const fwd10 = h('button', { class: 'round', 'aria-label': tr('Avancer de 10 secondes'), on: { click: (e) => { e.stopPropagation(); jump(10); } } }, skipMark('forward10'));
  const nextButton = h('button', { class: 'round flat', 'aria-label': tr('Épisode suivant'), hidden: true, on: { click: () => playNext() } }, icon('next'));
  const muteButton = h('button', { class: 'round flat', 'aria-label': tr('Couper le son'), hidden: /iPhone|iPad/.test(device.name), on: { click: () => { video.muted = !video.muted; updateMute(); } } }, icon('volume'));
  const fullButton = h('button', { class: 'round flat', 'aria-label': tr('Plein écran'), on: { click: fullscreen } }, icon('fullscreen'));
  const now = h('span', { class: 'p-time' }, '0:00');
  const left = h('span', { class: 'p-time right' }, '');
  const played = h('div', { class: 'played' });
  const buffered = h('div', { class: 'buffered' });
  const knob = h('div', { class: 'knob' });
  const track = h('div', { class: 'track' }, buffered, played, knob);
  const tipThumb = h('div', { class: 'thumb', hidden: true });
  const tipTime = h('span', {}, '');
  const tip = h('div', { class: 'scrub-tip', hidden: true }, tipThumb, tipTime);
  const timeline = h('div', { class: 'timeline', role: 'slider', tabIndex: 0, 'aria-label': tr('Position'), 'aria-valuemin': '0' }, track, tip);
  const busy = h('div', { class: 'p-busy', hidden: true }, h('div', { class: 'spinner' }));
  const pillLayer = h('div');
  const message = h('div', { class: 'p-message', hidden: true });
  const groupLine = groupPill(video);

  // Apple's player covers the screen: under it, the title and what Mira is doing, seen while it opens and closes.
  const nativeStatus = h('div', { class: 'n-status', role: 'status', 'aria-live': 'polite' });
  const nativeScreen = h('div', { class: 'n-screen' });

  if (native) {
    video.controls = false;
    nativeScreen.append(video);
    el.append(
      h('div', { class: 'n-top' }, h('button', { class: 'round', 'aria-label': tr('Retour'), on: { click: close } }, icon('back'))),
      h('div', { class: 'n-stage' }, nativeScreen, title, nativeStatus, groupLine.el),
      message);
  } else {
    const controls = h('div', { class: 'controls layer' },
      h('div', { class: 'p-top' },
        h('button', { class: 'round flat', 'aria-label': tr('Retour'), on: { click: close } }, icon('back')),
        title, airplay, pip, tracksButton, moreButton),
      h('div', { class: 'p-center' }, back10, playButton, fwd10),
      h('div', { class: 'p-bottom' },
        timeline,
        h('div', { class: 'p-row' }, now, h('div', { class: 'spacer' }), muteButton, nextButton, fullButton, left)),
      busy);
    const surface = h('div', { class: 'layer', 'aria-hidden': 'true' });
    el.append(video, surface, controls, pillLayer, groupLine.el, message);
    setupSurface(surface, controls);
  }

  /** What the page under Apple's player says: preparing, a tap to start, Picture in Picture, or nothing. */
  function showNative(state) {
    if (!native) return;
    clear(nativeStatus);
    if (state === 'loading') nativeStatus.append(h('div', { class: 'spinner' }), h('p', {}, tr('Préparation de la lecture…')));
    if (state === 'lost') nativeStatus.append(h('div', { class: 'spinner' }), h('p', {}, tr('Connexion au serveur perdue. La lecture reprend dès qu’il répond.')));
    if (state === 'tap') {
      nativeStatus.append(
        h('button', { class: 'round big n-play', 'aria-label': tr('Lecture'), on: { click: () => { showNative('loading'); if (enterNative()) play(); else showNative('tap'); } } }, icon('play', { size: 36 })),
        h('p', {}, tr('Touche pour lancer la lecture en plein écran.')));
    }
    if (state === 'pip') {
      nativeStatus.append(h('p', {}, tr('Lecture en image dans l’image.')),
        h('div', { class: 'actions' },
          h('button', { class: 'btn', on: { click: close } }, tr('Arrêter')),
          h('button', { class: 'btn primary', on: { click: () => { try { video.webkitSetPresentationMode('fullscreen'); } catch { enterNative(); } } } }, icon('fullscreen', { size: 20 }), tr('Plein écran'))));
    }
  }

  // ---------- Controls visibility (Mira's controls) ----------
  function showControls(hold = false) {
    el.classList.remove('idle');
    clearTimeout(hideTimer);
    // Never from under the keyboard: a control Tab is on stays shown.
    if (!hold && !video.paused && !scrubbing) hideTimer = setTimeout(() => { if (!el.querySelector(':focus-visible')) el.classList.add('idle'); }, 3200);
  }
  function hideControls() { if (!video.paused && !scrubbing) el.classList.add('idle'); }

  // A tap shows or hides the controls; a double tap on a side skips 10 s, as in the Apple TV app, and each tap after
  // it on the same side 10 s more, at once, the controls staying hidden.
  function setupSurface(surface, controls) {
    let lastTap = 0, lastSide = '', tapTimer = 0, streak = 0, streakTimer = 0;
    const step = (side) => {
      streak += 1;
      seek(position() + (side === 'left' ? -10 : 10));
      flash(side, streak * 10);
      clearTimeout(streakTimer);
      streakTimer = setTimeout(() => { streak = 0; }, 700);
    };
    surface.addEventListener('click', (e) => {
      const rect = el.getBoundingClientRect();
      const side = e.clientX < rect.width * 0.35 ? 'left' : e.clientX > rect.width * 0.65 ? 'right' : 'middle';
      const time = Date.now();
      if (side !== 'middle' && side === lastSide && (streak || time - lastTap < 300)) {
        clearTimeout(tapTimer);
        step(side);
        lastTap = 0;
        return;
      }
      streak = 0;
      lastTap = time; lastSide = side;
      clearTimeout(tapTimer);
      tapTimer = setTimeout(() => (el.classList.contains('idle') ? showControls() : hideControls()), side === 'middle' ? 0 : 260);
    });
    el.addEventListener('pointermove', (e) => { if (e.pointerType === 'mouse') showControls(); });
    controls.addEventListener('click', () => showControls());
    // Tab brings the controls back, as a move of the mouse does: never a ring on something unseen.
    el.addEventListener('focusin', () => { if (el.querySelector(':focus-visible')) showControls(); });
  }

  let flashMark = null;
  function flash(side, amount = 10) {
    flashMark?.remove();
    const mark = h('div', { class: ['p-flash', side] }, icon(side === 'left' ? 'back10' : 'forward10', { size: 28 }), tr('{s} s', { s: amount }));
    flashMark = mark;
    el.append(mark);
    setTimeout(() => mark.remove(), 600);
  }

  // ---------- Playback ----------
  function togglePlay() {
    if (video.paused) play(); else video.pause();
  }
  /** A pause Mira makes (closing, a stall, the group's): the group is not told it as the person's. */
  function quietPause() {
    if (!video.paused) expect.pause = Date.now() + 1500;
    video.pause();
  }
  const expected = (kind) => { const yes = Date.now() < expect[kind]; expect[kind] = 0; return yes; };
  async function play() {
    try { await video.play(); hideMessage(); }
    catch (error) {
      if (error?.name === 'NotAllowedError') showTapToPlay();
    }
  }
  function showTapToPlay() {
    if (native) { showNative('tap'); return; }
    showMessage(h('div', {},
      h('button', { class: 'round big', style: { width: '84px', height: '84px', background: 'rgba(245,245,247,.95)', color: '#0b0b0c' }, 'aria-label': tr('Lecture'), on: { click: () => { hideMessage(); play(); } } }, icon('play', { size: 36 })),
      h('p', {}, item ? item.Name : '')));
  }
  function jump(delta) { seek(position() + delta); showControls(); }
  function seek(at) {
    const end = total || video.duration || 0;
    const value = Math.max(0, end ? Math.min(at, end - 1) : at);
    // In a group, the jump is seen here at once and asked of the group once the taps stop (±10 s, several times).
    if (together.group) { clearTimeout(groupSeekTimer); groupSeekTimer = setTimeout(() => group.seek(value), 400); }
    if (progressive) { open(ticks(value), { keepPaused: !!together.group || video.paused }); return; }
    expect.seek = Date.now() + 3000;
    video.currentTime = value - offset;
    report('progress');
  }

  /** Apple's full screen player, asked for at once; false when Safari wants a tap for it first. */
  function enterNative() {
    if (!native || presentation() !== 'inline') return true;
    try { video.webkitEnterFullscreen(); return true; } catch { return false; }
  }
  function exitPresentation() {
    if (!native) return;
    try {
      if (presentation() === 'picture-in-picture') video.webkitSetPresentationMode('inline');
      else if (presentation() === 'fullscreen') video.webkitExitFullscreen();
    } catch { /* Already back in the page. */ }
  }
  /** Apple's player opened, closed, or went to Picture in Picture. */
  function presentationChanged() {
    const was = mode;
    mode = presentation();
    if (!native || disposed) return;
    // Back in the page: Apple's player was closed (Terminé, a swipe down), or Picture in Picture was (see
    // presentationStep). Closed by the person, even while the next episode loads: Mira closes, and does not open it again.
    const step = presentationStep({ was, mode, closing, failed, paused: video.paused, ended, videoEnded: video.ended, switching });
    if (step === 'fullscreen') showNative('');
    else if (step === 'pip') showNative('pip');
    else if (step === 'reenter') { if (!enterNative()) { quietPause(); showNative('tap'); } }
    else if (step === 'close' || step === 'close-landing') close({ landing: step === 'close-landing' });
  }

  function updatePlayIcon() {
    clear(playButton).append(icon(video.paused ? 'play' : 'pause', { size: 36 }));
    playButton.setAttribute('aria-label', video.paused ? tr('Lecture') : tr('Pause'));
  }
  function updateMute() { clear(muteButton).append(icon(video.muted ? 'mute' : 'volume')); }

  function updateTime() {
    if (scrubbing) return;
    // « Minuterie »: the minutes are over, the film pauses where it is (Apple's player too).
    if (sleep.due(Date.now()) && !video.paused) { video.pause(); toast(tr('Minuterie : lecture en pause.'), { symbol: 'clock' }); }
    const end = total || video.duration || 0, at = position();
    markers(at, end);
    if (native) return;
    const ratio = end ? Math.min(1, at / end) : 0;
    played.style.width = `${ratio * 100}%`;
    knob.style.left = `${ratio * 100}%`;
    now.textContent = clock(at);
    left.textContent = end ? `-${clock(Math.max(0, end - at))}` : '';
    timeline.setAttribute('aria-valuemax', String(Math.round(end)));
    timeline.setAttribute('aria-valuenow', String(Math.round(at)));
    timeline.setAttribute('aria-valuetext', tr('{at} sur {end}', { at: clock(at), end: clock(end) }));
    if (video.buffered.length && end) {
      let far = 0;
      for (let i = 0; i < video.buffered.length; i++) if (video.buffered.start(i) <= video.currentTime + 1) far = Math.max(far, video.buffered.end(i));
      buffered.style.width = `${Math.min(1, (offset + far) / end) * 100}%`;
    }
  }

  // ---------- Timeline scrubbing, with trickplay previews when Jellyfin has made them ----------
  function ratioAt(clientX) {
    const rect = track.getBoundingClientRect();
    return Math.max(0, Math.min(1, (clientX - rect.left) / Math.max(1, rect.width)));
  }
  function preview(ratio) {
    const end = total || video.duration || 0, at = ratio * end;
    played.style.width = `${ratio * 100}%`; knob.style.left = `${ratio * 100}%`;
    tip.hidden = false; tip.style.left = `${Math.min(Math.max(ratio * 100, 12), 88)}%`;
    tipTime.textContent = clock(at); now.textContent = clock(at);
    tipThumb.hidden = !trick;
    if (trick) {
      const { tile, width, height, x, y } = trickCell(trick, at);
      tipThumb.hidden = false;
      Object.assign(tipThumb.style, {
        width: `${width}px`, height: `${height}px`,
        backgroundImage: `url("${trick.url(tile)}")`,
        backgroundSize: `${trick.TileWidth * width}px auto`,
        backgroundPosition: `-${x}px -${y}px`,
      });
    }
    return at;
  }
  timeline.addEventListener('pointerdown', (e) => {
    // The mouse's other buttons (a right click for the browser's menu) do not move the film.
    if (e.button !== 0) return;
    scrubbing = true; timeline.classList.add('active'); timeline.setPointerCapture(e.pointerId);
    showControls(true); preview(ratioAt(e.clientX));
  });
  timeline.addEventListener('pointermove', (e) => { if (scrubbing) preview(ratioAt(e.clientX)); });
  const endScrub = (e) => {
    if (!scrubbing) return;
    const at = preview(ratioAt(e.clientX));
    scrubbing = false; timeline.classList.remove('active'); tip.hidden = true;
    seek(at); showControls();
  };
  timeline.addEventListener('pointerup', endScrub);
  timeline.addEventListener('pointercancel', () => { scrubbing = false; timeline.classList.remove('active'); tip.hidden = true; updateTime(); });
  timeline.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') { e.preventDefault(); jump(-10); }
    if (e.key === 'ArrowRight') { e.preventDefault(); jump(10); }
  });

  // ---------- Skippable passages and the next episode ----------
  let currentSkip = null;
  function markers(at, end) {
    const segment = skippable(segments, at);
    // Skipped once by itself when the settings say so: going back into it plays it. Only once the stream plays: a stream
    // being opened says 0, and a jump then would move where it starts.
    if (segment && streamPlaying && skipsItself(segment, { autoSkip: settings.get('autoSkip') && !together.group, skipped })) {
      skipped.add(segment);
      seek(seconds(segment.EndTicks));
      return;
    }
    // Apple's player has no room for Mira's buttons: there, the next episode starts by itself a few seconds into end
    // credits that only a preview follows, or at the end.
    if (native) { nativeNext(at, end); return; }
    if (segment !== currentSkip) {
      currentSkip = segment;
      pillLayer.querySelector('.pill')?.remove();
      if (segment) {
        pillLayer.append(h('button', { class: 'pill', on: { click: (e) => { e.stopPropagation(); seek(seconds(segment.EndTicks)); } } }, skipLabel(segment.Type), icon('next', { size: 20 })));
      }
    }
    if (!next || nextDismissed || !autoNext() || !end) return;
    const from = nextFrom(segments, end);
    // Gone back before it, to watch again: the offer goes, and comes back when that point is reached again.
    if (countdownTimer && at < from - 2) { clearInterval(countdownTimer); countdownTimer = 0; pillLayer.querySelector('.up-next')?.remove(); return; }
    if (at >= from && !countdownTimer && !video.paused) offerNext();
  }

  /** The next episode starts by itself: the settings say so, and the sleep timer does not stop at this one. */
  const autoNext = () => settings.get('autoNext') && !sleep.atEnd;
  /**
   * Apple's player: the next episode a countdown into the end credits, as Mira's own controls start it once their
   * countdown ends, without a word (there is no room for one). Not in a group: the others would lose the credits
   * without seeing it coming; there, and without such credits, it starts at the end of the title.
   */
  function nativeNext(at, end) {
    const before = lastAt;
    lastAt = at;
    if (!next || nextDismissed || creditsWatched || !autoNext() || together.group || !end || !streamPlaying || video.paused || switching) return;
    const when = nativeNextAt(segments, end, { startedAt: titleStart });
    // Reached by playing on, not by a jump: the person's seek in Apple's player lands past it before `seeked` says so.
    if (when != null && before < when && at >= when && at - before < 5) playNext();
  }
  function offerNext() {
    countdown = COUNTDOWN;
    const counter = h('span', {}, tr('Dans {n} s', { n: countdown }));
    const card = h('div', { class: 'up-next', role: 'dialog', 'aria-label': tr('Épisode suivant') },
      picture(artFor(next, 'still'), { kind: 'wide', width: 112 }),
      h('div', {}, h('div', { class: 'label' }, tr('Épisode suivant')), h('strong', { class: 'clamp-2' }, `${episodeCode(next)} · ${next.Name ?? ''}`), counter),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', on: { click: (e) => { e.stopPropagation(); cancelNext(); } } }, tr('Annuler')),
        h('button', { class: 'btn primary', on: { click: (e) => { e.stopPropagation(); playNext(); } } }, icon('play', { size: 18 }), tr('Lire'))));
    pillLayer.append(card);
    countdownTimer = setInterval(() => {
      if (video.paused) return;
      countdown -= 1;
      counter.textContent = tr('Dans {n} s', { n: countdown });
      if (countdown <= 0) playNext();
    }, 1000);
  }
  function cancelNext() {
    nextDismissed = true;
    clearInterval(countdownTimer); countdownTimer = 0;
    pillLayer.querySelector('.up-next')?.remove();
  }
  /** The next episode in this same player: Apple's full screen stays open, and Back still leads to the title page. */
  async function playNext() {
    if (!next || switching || closing) return;
    // In a group, the next episode is put on for the group, and comes back here as its title (groupSwitch).
    if (together.group) { clearInterval(countdownTimer); countdownTimer = 0; pillLayer.querySelector('.up-next')?.remove(); group.start(next.Id, 0); return; }
    const upcoming = next, resume = progressFraction(upcoming) > 0;
    switching = true;
    // An opening of this title still on its way (a fallback) must not land while the next one starts.
    opening++;
    switchFrom = presentation();
    clearInterval(countdownTimer); countdownTimer = 0;
    clear(pillLayer);
    // The episode that ends stops at once, and the next one's name is there while it loads.
    if (!video.ended) quietPause();
    busy.hidden = false;
    title.firstChild.textContent = upcoming.SeriesName ?? '';
    title.lastChild.textContent = `${episodeCode(upcoming)} · ${upcoming.Name ?? ''}`;
    played.style.width = '0%'; knob.style.left = '0%'; now.textContent = '0:00'; left.textContent = '';
    await stop();
    if (disposed || closing) return;
    // Its address says which episode, not « from the start »: if iOS reloads the page later, it resumes where it was.
    history.replaceState(history.state, '', `#/lecture/${upcoming.Id}`);
    resetTitle();
    target = { id: upcoming.Id, fromStart: !resume, tracks: null };
    await begin();
  }

  function resetTitle() {
    clearInterval(progressTimer);
    stopWaiting();
    // An opening or a check of the server still on its way for the title before lands on nothing.
    opening++;
    item = null; source = null; playSessionId = ''; playMethod = 'DirectPlay'; offset = 0; progressive = false; total = 0; inband = false;
    audioIndex = null; subtitleIndex = null; fallback = 0; lastError = ''; attempts = []; current = null; started = false; ended = false; reopenedAt = null; pausedByChoice = false;
    segments = []; chapters = []; trick = null; next = null; nextDismissed = false; currentSkip = null;
    titleStart = 0; creditsWatched = false; lastAt = 0;
    skipped.clear();
    nextButton.hidden = true;
    tipThumb.hidden = true; tipThumb.style.backgroundImage = '';
  }

  // ---------- Reports to Jellyfin ----------
  function body(extra = {}) {
    return {
      ItemId: item.Id, MediaSourceId: source?.Id, PlaySessionId: playSessionId, PlayMethod: playMethod,
      PositionTicks: ticks(position()), IsPaused: video.paused, IsMuted: video.muted, VolumeLevel: 100, CanSeek: true,
      AudioStreamIndex: audioIndex ?? undefined, SubtitleStreamIndex: subtitleIndex ?? -1, RepeatMode: 'RepeatNone', ...extra,
    };
  }
  function report(kind, keepalive = false) {
    if (!item || !playSessionId || (kind !== 'start' && !started)) return Promise.resolve();
    lastReport = Date.now();
    return api.report(kind, body(), keepalive).catch(() => {});
  }
  async function stop({ finished = false } = {}) {
    if (!started || !item) return;
    started = false;
    clearInterval(progressTimer);
    const final = body(finished ? { PositionTicks: item.RunTimeTicks ?? ticks(position()) } : {});
    await api.report('stop', final, true).catch(() => {});
    if (playMethod !== 'DirectPlay') api.stopEncoding(playSessionId);
    item.UserData = { ...item.UserData, PlaybackPositionTicks: finished ? 0 : final.PositionTicks, Played: finished || item.UserData?.Played };
    changed(item, { position: final.PositionTicks });
  }

  // ---------- Tracks a title starts with ----------
  function pickTracks(chosen) {
    const memory = item.SeriesId ? settings.get('seriesTracks')?.[item.SeriesId] ?? null : null;
    ({ audio: audioIndex, subtitle: subtitleIndex } = startTracks(source, chosen,
      { memory, audioOrder: settings.get('audioLanguages'), subtitleOrder: settings.get('subtitleLanguages') }));
    if (chosen && source?.MediaStreams?.length) remember();
  }
  /** The languages chosen for a series, for its next episodes on this phone. */
  function remember() {
    if (!item?.SeriesId || !source) return;
    const all = { ...settings.get('seriesTracks') };
    delete all[item.SeriesId];
    all[item.SeriesId] = trackMemory(source, audioIndex, subtitleIndex);
    settings.set('seriesTracks', Object.fromEntries(Object.entries(all).slice(-100)));
  }

  // ---------- Opening the stream ----------
  async function open(startTicks, { keepPaused = false } = {}) {
    const attempt = ++opening;
    currentStart = startTicks ?? 0;
    busy.hidden = false; hideMessage(); failed = false;
    const tried = { stage: fallback, method: '', reasons: [], error: '' };
    attempts.push(tried);
    if (started) { await report('progress'); }
    try {
      const max = await bitrate(source?.Bitrate ?? item.MediaSources?.[0]?.Bitrate ?? 0);
      const { lastResort, ...stage } = stageRequest(fallback);
      const ask = (limit) => api.playbackInfo(item.Id, {
        DeviceProfile: lastResort ? lastResortProfile(limit, { hlsSubtitles: native }) : deviceProfile(limit, { hlsSubtitles: native }),
        MaxStreamingBitrate: limit, StartTimeTicks: startTicks,
        AudioStreamIndex: audioIndex ?? undefined, SubtitleStreamIndex: subtitleIndex ?? undefined, MediaSourceId: source?.Id,
        EnableDirectPlay: stage.EnableDirectPlay, EnableDirectStream: stage.EnableDirectStream, EnableTranscoding: true,
        AllowVideoStreamCopy: stage.AllowVideoStreamCopy, AllowAudioStreamCopy: stage.AllowAudioStreamCopy, AutoOpenLiveStream: true,
      });
      let info = await ask(max);
      if (attempt !== opening || disposed || closing) return;
      // An account Jellyfin does not let convert, the file above the quality asked: the file as it is (uncappedBitrate).
      const full = info?.ErrorCode || !info?.MediaSources?.[0] ? null : uncappedBitrate(info.MediaSources[0], { stage: fallback, bitrate: max });
      if (full) {
        info = await ask(full);
        if (attempt !== opening || disposed || closing) return;
      }
      if (info?.ErrorCode) throw new Error(playbackError(info.ErrorCode));
      const previousSession = playSessionId;
      source = info.MediaSources?.[0];
      if (!source) throw new Error(tr('Ce titre n’a aucun fichier lisible.'));
      playSessionId = info.PlaySessionId;
      if (previousSession && previousSession !== playSessionId && playMethod !== 'DirectPlay') api.stopEncoding(previousSession);
      audioIndex ??= source.DefaultAudioStreamIndex ?? null;
      subtitleIndex ??= source.DefaultSubtitleStreamIndex ?? -1;
      total = seconds(source.RunTimeTicks ?? item.RunTimeTicks);

      // The file as it is, or Jellyfin's conversion (rewritten for Apple's player).
      inband = false;
      const stream = chooseStream(source, { itemId: item.Id, deviceId: device.id, stage: fallback, native, subtitleIndex });
      if (!stream) throw new Error(tr('Jellyfin ne propose aucun flux lisible pour cet appareil.'));
      const url = signed(stream.address), hlsStream = stream.hlsStream;
      ({ playMethod, progressive, inband } = stream);
      tried.method = playMethod; tried.reasons = transcodeReasons(source.TranscodingUrl); current = tried;
      offset = progressive ? seconds(startTicks) : 0;
      if (!(await attach(url, hlsStream, progressive ? 0 : seconds(startTicks), attempt)) || attempt !== opening || disposed || closing) return;
      // Apple's player was left while the next episode loaded: the person closed it, before WebKit even said so.
      if (native && switching && switchFrom === 'fullscreen' && presentation() === 'inline') { close({ landing: true }); return; }
      if (native && !enterNative()) { busy.hidden = true; showNative('tap'); return; }
      if (!keepPaused) { expect.play = Date.now() + 3000; play(); }
      if (keepPaused && together.group) checkReady();
    } catch (error) {
      if (attempt !== opening || disposed || closing) return;
      busy.hidden = true;
      // Jellyfin did not answer (PC asleep, network changing): Mira waits for it rather than giving up.
      if (error?.unreachable && item) { waitForServer(startTicks); return; }
      tried.error ||= error.message || '';
      fail(error.message || tr('La lecture n’a pas pu démarrer.'));
    }
  }

  /** The stream in the video; false when a newer opening, or the player's end, came first. */
  async function attach(url, hlsStream, startSeconds, attempt) {
    hls?.destroy(); hls = null;
    streamAttempt = attempt;
    for (const t of [...video.querySelectorAll('track')]) t.remove();
    pendingStart = startSeconds;
    streamPlaying = false;
    look = STILL;
    touched = new WeakSet();
    // Subtitles go in before the stream: Safari hands the tracks it finds at loading to Apple's player and its menu.
    addSubtitles();
    tracksSettle = Date.now() + 1500;
    if (hlsStream && useHlsJs) {
      const { default: Hls } = await import('../../vendor/hls.light.min.mjs');
      if (disposed || closing || attempt !== opening) return false;
      hls = new Hls({ startPosition: startSeconds, maxBufferLength: 30, backBufferLength: 60, enableWorker: true });
      let recovered = false;
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal || attempt !== opening) return;
        // A decoding hiccup: hls.js starts the decoder again, once. Twice, or anything else with Jellyfin answering
        // (a segment it could not make), the stream is asked again with more of it converted.
        if (hlsErrorStep({ fatal: data.fatal, mediaError: data.type === Hls.ErrorTypes.MEDIA_ERROR, recovered }) === 'recover') { recovered = true; hls.recoverMediaError(); return; }
        lastError = hlsErrorText({ details: data.details, type: data.type, stage: fallback });
        if (current) current.error = hlsError({ details: data.details, type: data.type });
        // Read before hls.js resets the video (its time back to 0); the failed stream stops there.
        const at = resumeAt(), paused = pausedThen();
        hls.stopLoad();
        streamFailed(REFUSED, { then: nextFallback(), detail: lastError, at, paused });
      });
      hls.loadSource(url);
      hls.attachMedia(video);
      pendingStart = 0;
    } else {
      video.src = url;
      video.load();
    }
    showTrack();
    setTimeout(showTrack, 300);
    return true;
  }

  function addSubtitles() {
    el.style.setProperty('--cue', `${settings.get('subtitleSize')}%`);
    // Subtitles in the HLS stream (Apple's player) arrive with it: a <track> beside them would only repeat them.
    if (inband) return;
    const streams = source?.MediaStreams ?? [];
    const subtitles = streams.filter((s) => s.Type === 'Subtitle');
    for (const stream of subtitles) {
      if (stream.DeliveryMethod !== 'External' || !stream.DeliveryUrl) continue;
      // ASS and SSA (anime) come as files beside the video: a browser only reads WebVTT, which Jellyfin makes of them.
      const src = webVttAddress(stream.DeliveryUrl);
      video.append(h('track', {
        kind: stream.IsForced && FORCED_KIND ? 'forced' : 'subtitles', label: trackText(stream, subtitles.indexOf(stream) + 1).label, srclang: stream.Language ?? 'und',
        src: signed(src), default: stream.Index === subtitleIndex, dataset: { index: String(stream.Index) },
      }));
    }
  }
  const subtitleStream = (index) => (source?.MediaStreams ?? []).find((s) => s.Type === 'Subtitle' && s.Index === index) ?? null;
  /** The file's own text subtitles, read by Safari when it plays the file as it is (see fileTextSubtitles). */
  const fileText = () => fileTextSubtitles(source, playMethod);
  const trackNode = (textTrack) => [...video.querySelectorAll('track')].find((x) => x.track === textTrack) ?? null;
  /** The subtitle tracks the HLS stream carries, as the browser lists them. */
  const streamTracks = () => [...video.textTracks].filter((t) => !trackNode(t) && ['subtitles', 'captions', 'forced'].includes(t.kind));
  /** Jellyfin's index of a subtitle track: beside the video, its <track>; in the HLS stream, its place there (or its name). */
  function trackIndex(textTrack) {
    const node = trackNode(textTrack);
    if (node) return Number(node.dataset.index);
    // The file's own subtitles, read by Safari, or the HLS stream's (see carriedIndex).
    const carried = streamTracks(), at = carried.indexOf(textTrack);
    if (at < 0) return null;
    return carriedIndex({ at, count: carried.length, label: textTrack.label, inband, own: inband ? null : fileText(), source });
  }
  // The tracks Mira has set itself. WebKit chooses for any other (the phone's language, a default or forced track),
  // and such a choice must not be taken for one made in Apple's menu.
  let touched = new WeakSet();
  /** Shows the chosen subtitles among those the browser has, beside the video or in the stream; hides the others. */
  function showTrack() {
    for (const t of video.textTracks) {
      const index = trackIndex(t);
      if (index == null) continue;
      const mode = index === subtitleIndex ? 'showing' : 'disabled';
      if (t.mode !== mode) t.mode = mode;
      // Setting the mode a track already has changes nothing in WebKit, not even who set it: one step through
      // « hidden » makes it Mira's.
      // Not for a <track>: « hidden » would download its file. WebKit does not choose for those while Mira sets them.
      else if (native && !touched.has(t) && mode === 'disabled' && !trackNode(t)) { t.mode = 'hidden'; t.mode = 'disabled'; }
      touched.add(t);
    }
  }
  /** A track appears (the HLS stream's, the file's own): Mira sets it, and the browser's moves for a moment are not a choice. */
  function trackAdded() {
    tracksSettle = Math.max(tracksSettle, Date.now() + 1500);
    // The file's own subtitles are there: Mira's copies of them go.
    const own = fileText();
    if (own.length && streamTracks().length === own.length) {
      for (const stream of own) video.querySelector(`track[data-index="${stream.Index}"]`)?.remove();
    }
    showTrack();
  }
  /**
   * Subtitles chosen in Apple's menu (or Safari's own full screen): Mira follows, reports and remembers. In the page,
   * only Mira's controls choose: the browser's own moves while tracks load are not a choice.
   */
  const fromSystemMenu = () => presentation() !== 'inline' && Date.now() > tracksSettle;
  function textTracksChanged() {
    if (!source || !item || !fromSystemMenu()) return;
    const showing = [...video.textTracks].find((t) => t.mode === 'showing' && trackIndex(t) != null);
    const index = showing ? trackIndex(showing) : -1;
    // The burned-in subtitles are in the picture, not in the menu: « Off » there changes nothing.
    if (index === subtitleIndex || (index === -1 && burned())) return;
    switchTracks({ subtitle: index });
  }
  /** The audio track of a file Safari plays as it is: false when it cannot be switched there. */
  function selectAudioTrack() {
    const list = video.audioTracks, audios = (source?.MediaStreams ?? []).filter((s) => s.Type === 'Audio');
    const at = audios.findIndex((s) => s.Index === audioIndex);
    if (!switchesAudio || !list || list.length < 2 || list.length !== audios.length || at < 0) return false;
    for (let i = 0; i < list.length; i++) list[i].enabled = i === at;
    return true;
  }
  /** Audio chosen in Apple's menu: Mira follows, reports and remembers. */
  function audioTracksChanged() {
    const list = video.audioTracks, audios = (source?.MediaStreams ?? []).filter((s) => s.Type === 'Audio');
    if (playMethod !== 'DirectPlay' || !list || list.length !== audios.length || !fromSystemMenu()) return;
    const index = audios[Array.from({ length: list.length }, (_, i) => list[i]).findIndex((t) => t.enabled)]?.Index;
    if (index == null || index === audioIndex) return;
    audioIndex = index; remember(); report('progress');
  }

  // ---------- Tracks, speed and quality (Mira's controls) ----------
  const describe = (kind, stream, number) => [kind, trackText(stream, number).details].filter(Boolean).join(' · ');
  function tracksSheet() {
    const streams = source?.MediaStreams ?? [];
    const audio = streams.filter((s) => s.Type === 'Audio');
    const subs = streams.filter((s) => s.Type === 'Subtitle');
    sheet({
      title: tr('Audio et sous-titres'),
      items: [
        ...audio.map((s, i) => ({ label: trackText(s, i + 1).label, sub: describe(tr('Audio'), s, i + 1), selected: s.Index === audioIndex, run: () => switchTracks({ audio: s.Index }) })),
        { label: tr('Sans sous-titres'), sub: tr('Sous-titres'), selected: subtitleIndex === -1 || subtitleIndex == null, run: () => switchTracks({ subtitle: -1 }) },
        ...subs.map((s, i) => ({ label: trackText(s, i + 1).label, sub: describe(tr('Sous-titres'), s, i + 1), selected: s.Index === subtitleIndex, run: () => switchTracks({ subtitle: s.Index }) })),
      ],
    });
  }
  function switchTracks({ audio, subtitle }) {
    const at = position(), wasPaused = video.paused;
    if (audio != null && audio !== audioIndex) {
      audioIndex = audio; remember();
      // A file's own tracks switch at once in Safari; a conversion is asked again with the new one.
      if (playMethod === 'DirectPlay' && selectAudioTrack()) { report('progress'); return; }
      open(ticks(at), { keepPaused: wasPaused });
      return;
    }
    if (subtitle != null && subtitle !== subtitleIndex) {
      const stream = (source?.MediaStreams ?? []).find((s) => s.Index === subtitle);
      const wasBurned = burned();
      subtitleIndex = subtitle; remember();
      // Text subtitles already beside the video, or in the HLS stream, switch at once; pictures need a new
      // conversion, and so does leaving them.
      const { show, reopen } = subtitleSwitch({ subtitle, stream, inband, wasBurned });
      if (show) showTrack();
      if (reopen) open(ticks(at), { keepPaused: wasPaused });
      else report('progress');
    }
  }
  /** The chosen subtitles are pictures, drawn into the video by Jellyfin (Apple's menu cannot turn those off). */
  const burned = () => burnedIn(subtitleStream(subtitleIndex), source?.TranscodingUrl);

  function moreSheet() {
    const speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];
    const quality = settings.get('quality');
    sheet({
      title: tr('Vitesse, qualité et minuterie'),
      items: [
        // The default rate too: a new stream (another track, quality or episode) keeps the speed chosen.
        ...speeds.map((s) => ({ label: s === 1 ? tr('Vitesse normale') : tr('Vitesse × {rate}', { rate: number(s) }), selected: video.defaultPlaybackRate === s, run: () => { video.defaultPlaybackRate = s; video.playbackRate = s; } })),
        ...QUALITIES.map(([key, label, sub]) => ({ label: tr('Qualité : {label}', { label }), sub, selected: key === quality, run: () => {
          settings.set('quality', key);
          open(ticks(position()), { keepPaused: video.paused });
        } })),
        { heading: tr('Minuterie') },
        ...sleepChoices(sleep, { episode: item?.Type === 'Episode', now: Date.now() }).map(({ label, sub, selected, minutes }) => ({ label, sub, selected, run: () => {
          sleep.choose(minutes, Date.now());
          // An offer of the next episode already counting stops, as « Annuler » does.
          if (sleep.atEnd && countdownTimer) cancelNext();
        } })),
        ...(together.group ? [
          { heading: tr('Regarder ensemble') },
          { label: groupText(together.group), sub: tr('Pause, lecture et déplacements pour tout le groupe.'), selected: true },
          // A guest of « Inviter par lien » invites nobody: their link is the host's to give.
          ...(isGuest() ? [] : [{ symbol: 'link', label: tr('Inviter quelqu’un'), run: () => inviteSheet(together.group) }]),
          { label: tr('Quitter le groupe'), danger: true, run: () => together.leave() },
        ] : []),
      ],
    });
  }

  // ---------- Picture in Picture, AirPlay, full screen (Mira's controls) ----------
  function pipSupported() {
    return !!(video.webkitSupportsPresentationMode?.('picture-in-picture') || document.pictureInPictureEnabled);
  }
  async function togglePip() {
    try {
      if (video.webkitSupportsPresentationMode?.('picture-in-picture')) {
        video.webkitSetPresentationMode(video.webkitPresentationMode === 'picture-in-picture' ? 'inline' : 'picture-in-picture');
      } else if (document.pictureInPictureElement) await document.exitPictureInPicture();
      else await video.requestPictureInPicture();
    } catch { toast(tr('L’image dans l’image n’est pas disponible ici.'), { tone: 'error' }); }
  }
  function fullscreen() {
    // iPhone: iOS's own player, with its controls, AirPlay and subtitles. Elsewhere: the whole player, turned to
    // landscape for a landscape picture.
    if (!document.fullscreenEnabled && video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); return; }
    if (document.fullscreenElement) document.exitFullscreen?.();
    else {
      el.requestFullscreen?.().then(() => {
        if (video.videoWidth >= video.videoHeight) screen.orientation?.lock?.('landscape').catch(() => {});
      }).catch(() => video.webkitEnterFullscreen?.());
    }
  }
  function fullscreenChanged() {
    const full = document.fullscreenElement === el;
    clear(fullButton).append(icon(full ? 'exit-fullscreen' : 'fullscreen'));
    fullButton.setAttribute('aria-label', full ? tr('Quitter le plein écran') : tr('Plein écran'));
    if (!full) { try { screen.orientation?.unlock?.(); } catch { /* not locked */ } }
  }
  function leaveFullscreen() {
    if (document.fullscreenElement === el) document.exitFullscreen?.().catch(() => {});
  }

  // ---------- Errors and messages ----------
  function showMessage(content) { clear(message).append(content); message.hidden = false; }
  function hideMessage() { message.hidden = true; }
  /**
   * « Lecture impossible »: why, what the file holds (« MKV · HEVC 4K 10 bits HDR10 · DTS 5.1 »), each stream Mira
   * tried, and « Envoyer le détail à Jellyfin »: the same in a report Mira for Windows shows its administrator.
   */
  function fail(text, detail = '') {
    failed = true; switching = false;
    busy.hidden = true;
    clearTimeout(nativeCheck);
    exitPresentation();
    showNative('');
    const file = source ?? item?.MediaSources?.[0];
    const media = mediaLine(file, { audioIndex, subtitleIndex });
    const tried = attempts.filter((a) => a.method).map(attemptText);
    const report = item ? failureReport({
      item, source: file, audioIndex, subtitleIndex, message: text, attempts, device: device.name,
      user: session.current?.userName, version: VERSION, browser: navigator.userAgent,
    }) : null;
    const sent = h('p', { class: 'detail', role: 'status' });
    // A guest's report would go to someone else's Jellyfin, which takes none from a link (the gateway refuses it).
    const send = report && !isGuest() ? h('button', { class: 'btn quiet small', on: { click: () => sendReport(report, send, sent) } }, tr('Envoyer le détail à Jellyfin')) : null;
    showMessage(h('div', {}, icon('warning', { size: 36 }), h('h2', { class: 'h3' }, tr('Lecture impossible')), h('p', {}, text),
      media ? h('p', { class: 'detail media' }, media) : null,
      tried.length ? h('ol', { class: 'tried', 'aria-label': tr('Essayé') }, tried.map((line) => h('li', {}, line))) : detail ? h('p', { class: 'detail' }, detail) : null,
      h('div', { class: 'actions' },
        h('button', { class: 'btn', on: { click: close } }, tr('Retour')),
        h('button', { class: 'btn primary', on: { click: retry } }, tr('Réessayer'))),
      send, sent));
  }
  /** The report in Jellyfin's logs; said under the button, which goes once it is there. */
  async function sendReport(report, button, said) {
    button.disabled = true; said.textContent = tr('Envoi…');
    try {
      await api.sendLog(report);
      button.remove();
      said.textContent = tr('Envoyé à Jellyfin. Mira pour Windows le montre à son administrateur : Réglages → Jellyfin & synchronisation.');
    } catch (error) {
      button.disabled = false;
      said.textContent = error?.status === 403
        ? tr('Ton serveur Jellyfin refuse les rapports des appareils (son réglage AllowClientLogUpload est coupé). Ce qui est écrit ici reste valable.')
        : error?.message || tr('L’envoi n’a pas abouti.');
    }
  }
  function retry() {
    hideMessage(); failed = false; showNative('loading');
    // Asked by the person: a stall of this new stream is a first one.
    reopenedAt = null;
    if (item) open(resumeAt()); else begin();
  }
  /** Where to pick the title up again: where it plays, or, before it has played, where it was asked to start. */
  const resumeAt = () => resumeTicks({ streamPlaying, progressive, positionTicks: ticks(position()), currentStart });
  /** Whether a stream opened again after a failure stays paused: it was, before it broke (never once it had not played). */
  const pausedThen = () => streamPlaying && pausedByChoice;
  function stopWaiting() { waitToken++; clearTimeout(retryTimer); waitingForServer = false; }
  /**
   * The stream stopped on an error that may be the connection's: Mira asks the server. Silent (PC asleep, Wi-Fi to
   * 4G), Mira waits for it; answering, the stream itself is at fault, and that is said (no endless reopening).
   */
  function streamFailed(text, { then = null, detail = '', at = resumeAt(), paused = pausedThen() } = {}) {
    const attempt = opening;
    // A stream that breaks may be a network that changed: the server is checked again before it is asked anything.
    recheckServer();
    ping().then((up) => {
      if (disposed || closing || attempt !== opening) return;
      const step = afterCheck({ up, canGoOn: !!then });
      if (step === 'wait') waitForServer(at, paused);
      else if (step === 'next') then(at, paused);
      else fail(text, detail);
    });
  }
  /**
   * A stream the player refused while Jellyfin answers: asked again, its video converted, then everything converted,
   * from where the title was. Null once nothing is left to try.
   */
  const nextFallback = () => (nextStage(fallback) == null ? null : (at = resumeAt(), paused = false) => { fallback++; open(at, { keepPaused: paused }); });
  const REFUSED = tr('Ce titre n’a pas pu être lu sur cet appareil, même entièrement converti par Jellyfin.');
  /**
   * Jellyfin does not answer: not this stream's fault. Apple's player stays open, Mira says so under it or over its
   * own controls, asks again every few seconds, and picks up where the title was, paused if it was.
   */
  function waitForServer(at = null, keepPaused = null) {
    if (waitingForServer || disposed || closing) return;
    waitingForServer = true;
    const mine = ++waitToken;
    const from = at ?? resumeAt(), paused = keepPaused ?? pausedThen();
    busy.hidden = false;
    showNative('lost');
    if (!native) {
      showMessage(h('div', {}, h('div', { class: 'spinner' }), h('p', {}, tr('Connexion au serveur perdue. La lecture reprend dès qu’il répond.')),
        h('div', { class: 'actions' }, h('button', { class: 'btn', on: { click: close } }, tr('Retour')))));
    }
    const again = async () => {
      if (disposed || closing || mine !== waitToken) return;
      const up = await ping();
      if (disposed || closing || mine !== waitToken) return;
      if (!up) { retryTimer = setTimeout(again, 3000); return; }
      waitingForServer = false;
      hideMessage(); showNative('');
      open(from, { keepPaused: paused });
    };
    retryTimer = setTimeout(again, 2000);
  }

  /**
   * Looked at every second: a stream that should play and stays still (no error: the browser would wait for it
   * forever, its spinner on, the controls hidden) is a stall (stream.js stallWatch).
   */
  function lookAtStream() {
    const expecting = !!item && streamAttempt === opening && !!video.getAttribute('src') && !failed && !waitingForServer && !askingStuck
      && !closing && !disposed && !video.ended && (!video.paused || video.seeking);
    // Played on well past where it was opened again: the next stall is a first one.
    if (reopenedAt != null && streamPlaying && position() > reopenedAt + STALL_FORGIVEN) reopenedAt = null;
    look = stallWatch(look, { now: Date.now(), at: video.currentTime, expecting, ready: video.readyState >= 3, seeking: video.seeking });
    if (look.stuck) streamStuck(stallLimit(look.moved));
  }
  const STUCK = tr('Jellyfin répond, mais la vidéo n’arrive plus jusqu’ici. Réessaie, ou choisis une qualité plus basse si la connexion est lente.');
  /**
   * A stall: the server is asked. Silent, Mira waits for it, as after an error; answering, the stream is opened again
   * where the title is (Jellyfin may have stopped its conversion), and stuck again before it played on,
   * « Lecture impossible » with « Réessayer » (stream.js stallStep).
   */
  function streamStuck(limit) {
    const attempt = opening, at = resumeAt(), wasReopened = reopenedAt != null;
    if (current) current.error = tr('Plus rien reçu pendant {n} s', { n: Math.round(limit / 1000) });
    look = STILL; askingStuck = true;
    recheckServer();
    ping().then((up) => {
      askingStuck = false;
      if (disposed || closing || attempt !== opening) return;
      const step = stallStep({ up, reopened: wasReopened });
      // The stream given up stops asking for more (hls.js would keep trying, and say so later).
      if (step !== 'reopen') { hls?.stopLoad(); quietPause(); }
      if (step === 'wait') waitForServer(at, false);
      else if (step === 'reopen') { reopenedAt = seconds(at); open(at); }
      else fail(STUCK);
    });
  }
  const stallClock = setInterval(lookAtStream, 1000);

  // ---------- Video events ----------
  function fitPicture() {
    if (native) return;
    const ratio = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight : 0;
    el.classList.toggle('fitted', ratio > 0);
    if (ratio) el.style.setProperty('--ratio', ratio.toFixed(4));
  }
  const on = {
    loadedmetadata: () => {
      // WebKit sets up the tracks again now: its moves for a moment are not a choice made in the menu.
      tracksSettle = Math.max(tracksSettle, Date.now() + 1500);
      if (pendingStart > 0 && !progressive) { expect.seek = Date.now() + 3000; try { video.currentTime = pendingStart; } catch { /* set again on canplay */ } }
      pendingStart = 0;
      if (playMethod === 'DirectPlay') selectAudioTrack();
      fitPicture();
      updateTime();
    },
    resize: fitPicture,
    playing: () => {
      busy.hidden = true; hideMessage(); updatePlayIcon();
      groupResumed();
      streamPlaying = true;
      // What played is no failure: only the streams refused before it stay, for a later « Lecture impossible ».
      attempts = attempts.filter((a) => a.error || a === current);
      // After a stall, only the spinner went: the controls show at the start and after a pause made by the person (and
      // keep hiding by themselves whenever they are shown).
      if (!started || resumedByUser || !el.classList.contains('idle')) showControls();
      resumedByUser = false;
      switching = false;
      if (!started) {
        started = true;
        report('start');
        // Paused too: Jellyfin ends a conversion a minute after its last news of the player.
        clearInterval(progressTimer);
        progressTimer = setInterval(() => { if (Date.now() - lastReport >= PROGRESS_EVERY - 200) report('progress'); }, PROGRESS_EVERY);
        // « From the start » was for this opening: a reload of the page later resumes where the title is.
        const address = withoutFromStart(location.hash, item.Id);
        if (address != null) history.replaceState(history.state, '', address);
      }
      // Safari may refuse full screen without a word: still in the page a moment later, Mira asks for a tap.
      if (native && presentation() === 'inline') {
        clearTimeout(nativeCheck);
        nativeCheck = setTimeout(() => {
          if (!disposed && !closing && presentation() === 'inline' && !video.paused) { quietPause(); showNative('tap'); }
        }, 1500);
      }
    },
    pause: () => {
      // The browser's own pause on a stream that broke: neither the person's (Jellyfin, the group) nor kept (pausedThen).
      if (video.error) { updatePlayIcon(); return; }
      pausedByChoice = true;
      if (!video.seeking && !video.ended) resumedByUser = true;
      updatePlayIcon(); showControls(true); report('progress');
      // Paused by the person (Mira's button, Apple's, the lock screen): for the whole group.
      if (!expected('pause') && together.group && !closing && !switching && !video.ended && !video.seeking) { clearTimeout(groupTimer); group.pause(); }
    },
    play: () => {
      pausedByChoice = false;
      updatePlayIcon();
      // Played by the person: the group plays, all at the same moment; until Jellyfin says when, this one waits.
      if (!expected('play') && together.group && !closing) { quietPause(); group.unpause(); }
    },
    waiting: () => { busy.hidden = false; groupStall(); },
    seeking: () => { busy.hidden = false; },
    seeked: () => {
      const byPerson = !expected('seek');
      // Moved in Apple's player into the end credits: they play to the end; back before them, the next episode comes
      // again from them (as Mira's countdown goes and comes back elsewhere).
      if (byPerson && native) { const from = creditsFrom(segments, total || video.duration || 0); creditsWatched = from != null && position() >= from - 1; }
      busy.hidden = true; updateTime(); checkReady();
      // Moved in Apple's player: for the whole group. Mira's own controls tell it themselves (seek).
      if (byPerson && native && together.group && !closing && !switching) { clearTimeout(groupSeekTimer); group.seek(position()); }
    },
    canplay: () => { busy.hidden = true; checkReady(); groupResumed(); },
    timeupdate: updateTime,
    progress: updateTime,
    ratechange: () => report('progress'),
    ended: async () => {
      if (ended || disposed) return;
      ended = true;
      await stop({ finished: true });
      if (disposed || closing) return;
      // In a group, the next episode is put on for everyone, and replaces this one here (groupSwitch).
      if (next && autoNext() && !nextDismissed) playNext();
      else close();
    },
    error: () => {
      // An opening on its way replaces this stream: what it still reports says nothing of the next one.
      if (disposed || closing || !item || !video.getAttribute('src') || streamAttempt !== opening) return;
      const code = video.error?.code;
      lastError = videoErrorText({ code, message: video.error?.message, playMethod, stage: fallback });
      if (current) current.error = videoError({ code, message: video.error?.message });
      // A file Safari was thought to play directly but cannot (decode, format): Jellyfin converts it instead, at once.
      if (videoErrorStep({ playMethod, stage: fallback, code }) === 'convert') { nextFallback()(resumeAt(), pausedThen()); return; }
      // Anything else: the server is asked. Silent, Mira waits for it; answering, the stream is at fault: asked again,
      // more of it converted (a lower quality alone would change nothing: Jellyfin copies what fits under it), until
      // nothing is left to convert.
      streamFailed(REFUSED, { then: nextFallback(), detail: lastError });
    },
    webkitpresentationmodechanged: presentationChanged,
    webkitplaybacktargetavailabilitychanged: (e) => { airplay.hidden = e.availability !== 'available'; },
  };
  for (const [name, fn] of Object.entries(on)) video.addEventListener(name, fn);
  document.addEventListener('fullscreenchange', fullscreenChanged);
  video.textTracks.addEventListener('change', textTracksChanged);
  // The HLS stream's subtitles appear once it loads: the chosen one shows, the others stay off.
  video.textTracks.addEventListener('addtrack', trackAdded);
  video.audioTracks?.addEventListener?.('change', audioTracksChanged);

  function keys(e) {
    if (e.target.closest?.('input, .sheet')) return;
    const actions = {
      ' ': togglePlay, k: togglePlay, ArrowLeft: () => jump(-10), ArrowRight: () => jump(10), j: () => jump(-10), l: () => jump(10),
      f: fullscreen, m: () => { video.muted = !video.muted; updateMute(); }, n: () => next && playNext(), Escape: close,
    };
    const action = actions[e.key];
    if (action) { e.preventDefault(); action(); showControls(); }
  }

  const onHide = () => { if (document.hidden) report('progress', true); };
  const onPageHide = () => { if (started) api.report('stop', body(), true).catch(() => {}); };
  // Another server answers here (another network): the stream's addresses carry the session (signed), so the player
  // closes rather than ask it for one more piece; the screen over Mira says why (app.js).
  const unwatch = onForeignServer((yes) => { if (yes) close(); });
  // Asked from another device (« Arrêter » in Mira for Windows, Jellyfin's dashboard).
  const remote = listen({
    playstate: (command) => {
      if (closing || disposed) return;
      if (command === 'Stop') { toast(tr('Lecture arrêtée depuis un autre appareil.')); close(); }
      else if (command === 'Pause') video.pause();
      else if (command === 'Unpause') play();
      else if (command === 'PlayPause') togglePlay();
    },
  });

  // « Regarder ensemble »: while this device is in a group, its title plays for the group (together.js). The group's
  // commands come here; the person's pause, play and seeks go to the group (the video events above).
  together.restore();
  const group = together.bind({
    itemId: () => item?.Id ?? null,
    position: () => position(),
    prepare: (startTicks) => { clearTimeout(groupTimer); quietPause(); seekTo(seconds(startTicks)); readyPending = true; checkReady(); },
    switchTo: (id, startTicks) => groupSwitch(id, startTicks),
    command: groupCommand,
    // A guest out of the group is most often a link taken back: the screen that says so follows, without this word.
    left: () => { clearTimeout(groupTimer); if (!isGuest()) toast(tr('Tu as quitté le groupe : la lecture continue ici seulement.')); },
  });
  /** Where the group is: a seek here that it does not take as the person's. */
  function seekTo(at) {
    if (progressive) { open(ticks(at), { keepPaused: true }); return; }
    expect.seek = Date.now() + 3000;
    try { video.currentTime = Math.max(0, at - offset); } catch { /* not seekable yet: set on loading */ }
  }
  function groupCommand(plan) {
    if (!plan || closing || disposed) return;
    clearTimeout(groupTimer);
    if (plan.action === 'stop') { toast(tr('Le groupe a arrêté la lecture.')); close(); return; }
    if (plan.action === 'pause') { quietPause(); if (needsSeek(position(), plan.position)) seekTo(plan.position); return; }
    if (plan.action === 'seek') { quietPause(); seekTo(plan.position); readyPending = true; checkReady(); return; }
    // Play: there at the moment Jellyfin set, from where the others are.
    if (needsSeek(position(), plan.position)) seekTo(plan.position);
    const go = () => { if (closing || disposed) return; expect.play = Date.now() + 1500; play(); };
    if (plan.wait > 0) groupTimer = setTimeout(go, plan.wait); else go();
  }
  /** Told the group once the stream can play where it is: Jellyfin starts everyone when all are. */
  function checkReady() {
    if (!readyPending || !together.group || !item || video.seeking || video.readyState < 3) return;
    readyPending = false;
    group.ready(position(), !video.paused);
  }
  /** A stall of more than a moment while playing: the group waits for this device. */
  function groupStall() {
    if (!together.group || video.paused || closing || Date.now() < expect.seek) return;
    clearTimeout(stallTimer);
    stallTimer = setTimeout(() => {
      if (!together.group || video.readyState >= 3 || closing) return;
      stalled = true;
      group.buffering(position(), true);
    }, 1500);
  }
  function groupResumed() {
    clearTimeout(stallTimer);
    if (!stalled) return;
    stalled = false;
    group.ready(position(), !video.paused);
  }
  /** The group put another title on (the next episode, a title chosen by someone else): it replaces this one here. */
  async function groupSwitch(id, startTicks) {
    if (closing || disposed) return;
    switching = true;
    opening++;
    switchFrom = presentation();
    clearInterval(countdownTimer); countdownTimer = 0;
    clear(pillLayer);
    clearTimeout(groupTimer);
    if (!video.ended) quietPause();
    busy.hidden = false;
    await stop();
    if (disposed || closing) return;
    history.replaceState(history.state, '', `#/lecture/${id}?ensemble=1`);
    resetTitle();
    target = { id, fromStart: false, tracks: null, startTicks };
    await begin();
  }

  /**
   * Back to the title page, from Retour or from Apple's player once closed: at once, the stop going to Jellyfin on its
   * way (its report is built before anything changes, and sent even if the page goes).
   */
  function close({ landing = false } = {}) {
    if (closing) return;
    closing = true;
    // The timer was for this sitting: the next title starts without it.
    sleep.clear();
    clearTimeout(nativeCheck); stopWaiting();
    exitPresentation();
    leaveFullscreen();
    video.pause();
    stop();
    // A guest has no title page: back to the group.
    const back = isGuest() ? '#/ensemble' : item ? titleHref(item) : '#/';
    // Closed from Apple's player: its picture shrinks back onto the page first, then Mira leaves (unless something
    // else moved Mira meanwhile).
    if (!landing) { goBack(back); return; }
    const at = location.hash;
    setTimeout(() => { if (!disposed && location.hash === at) goBack(back); }, 450);
  }

  // ---------- Media Session: lock screen and Control Center ----------
  const ACTIONS = ['play', 'pause', 'seekbackward', 'seekforward', 'seekto', 'nexttrack'];
  function mediaSession() {
    if (!('mediaSession' in navigator)) return;
    const art = artFor(item, item.Type === 'Episode' ? 'still' : 'backdrop') ?? artFor(item, 'poster');
    try {
      navigator.mediaSession.metadata = new MediaMetadata({
        title: item.Type === 'Episode' ? `${episodeCode(item)} · ${item.Name}` : item.Name,
        artist: item.Type === 'Episode' ? item.SeriesName : (item.ProductionYear ? String(item.ProductionYear) : ''),
        album: 'Mira',
        artwork: art ? [{ src: new URL(imageUrl(art, 512), location.href).href, sizes: '512x288', type: 'image/jpeg' }] : [],
      });
      const handlers = {
        play: () => play(), pause: () => video.pause(),
        seekbackward: () => jump(-10), seekforward: () => jump(10),
        seekto: (d) => seek(d.seekTime), nexttrack: next ? () => playNext() : null,
      };
      for (const action of ACTIONS) { try { navigator.mediaSession.setActionHandler(action, handlers[action]); } catch { /* unsupported action */ } }
    } catch { /* No Media Session here. */ }
  }

  // ---------- Start ----------
  /**
   * The audio session the film plays in: WebKit's own (sound even in Silent mode), or, when the settings ask for it on
   * iPhone, « ambient », which iOS does not make the Now Playing app (the Dynamic Island) and the Silent mode mutes.
   */
  function audioSession(ambient) {
    try { if ('audioSession' in navigator) navigator.audioSession.type = ambient ? 'ambient' : 'auto'; } catch { /* not settable here */ }
  }

  async function begin() {
    const attempt = ++beginning;
    busy.hidden = false; showNative('loading');
    audioSession(native && device.name === 'iPhone' && settings.get('ambientAudio'));
    // Muted when Apple's player closed (see dispose): the title plays with its sound. Mira's own mute button stays
    // as it was left elsewhere.
    if (native) { video.muted = false; updateMute(); }
    // The connection is measured while the title loads, not after (« Automatique », away from home).
    measureAhead();
    try {
      let found = await api.item(target.id);
      if (found.Type === 'Series') {
        const up = await api.seriesNext(found.Id);
        if (!up) throw new Error(tr('Cette série n’a aucun épisode à lire.'));
        found = await api.item(up.Id);
      }
      if (disposed || closing || attempt !== beginning) return;
      item = found;
      document.title = `${item.Type === 'Episode' ? item.SeriesName : item.Name} · Mira`;
      title.firstChild.textContent = item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name;
      title.lastChild.textContent = item.Type === 'Episode' ? `${episodeCode(item)} · ${item.Name ?? ''}` : [item.ProductionYear, item.OfficialRating].filter(Boolean).join(' · ');
      chapters = item.Chapters ?? [];
      // The group's title starts where the group does; any other one, chosen here, is put on for the group from where
      // it would start.
      const joining = !!together.group && together.queue?.itemId === item.Id;
      // A few seconds before where it was left (« Reprendre un peu avant »): put on for the group from there too.
      const resume = target.startTicks ?? (joining ? together.queue.startTicks : startPosition(item, { fromStart: target.fromStart, resume: settings.get('resume'), back: settings.get('resumeBack') }));
      titleStart = seconds(resume);
      total = seconds(item.RunTimeTicks);
      // The title's own streams choose its tracks before Jellyfin is asked for a stream.
      source = item.MediaSources?.[0] ?? null;
      pickTracks(target.tracks);
      if (native) {
        const art = artFor(item, item.Type === 'Episode' ? 'still' : 'backdrop') ?? artFor(item, 'wide') ?? artFor(item, 'poster');
        if (art) video.poster = imageUrl(art, 640); else video.removeAttribute('poster');
      }
      drawChapters();
      setupTrickplay();
      mediaSession();
      readyPending = joining;
      if (joining && target.startTicks == null) group.buffering(seconds(resume), false);
      await open(resume, { keepPaused: !!together.group });
      if (disposed || attempt !== beginning) return;
      if (together.group && !joining) group.start(item.Id, resume);
      // Once the title's own stream plays (before, a skip would move the one it replaces), and Jellyfin has answered: its
      // segments win, the passages the chapters name fill the rest (all of them without segments, or on Jellyfin 10.9).
      const fromChapters = chapterPassages(chapters, item.RunTimeTicks);
      api.segments(item.Id).then((list) => { if (attempt === beginning) segments = withChapters(list, fromChapters); });
      if (item.Type === 'Episode') {
        api.nextEpisode(item).then((n) => { if (attempt !== beginning) return; next = n; nextButton.hidden = !n; mediaSession(); }).catch(() => {});
      }
    } catch (error) {
      if (attempt !== beginning || disposed || closing) return;
      if (error?.unreachable && target) { waitForBegin(); return; }
      fail(error.message);
    }
  }
  /** Jellyfin unreachable before the title was even known: asked again until it answers. */
  function waitForBegin() {
    stopWaiting();
    const mine = waitToken;
    showNative('lost');
    if (!native) showMessage(h('div', {}, h('div', { class: 'spinner' }), h('p', {}, tr('Le serveur ne répond pas. La lecture commence dès qu’il répond.')),
      h('div', { class: 'actions' }, h('button', { class: 'btn', on: { click: close } }, tr('Retour')))));
    const again = async () => {
      if (disposed || closing || mine !== waitToken) return;
      const up = await ping();
      if (disposed || closing || mine !== waitToken) return;
      if (!up) { retryTimer = setTimeout(again, 3000); return; }
      hideMessage(); begin();
    };
    retryTimer = setTimeout(again, 2000);
  }

  function drawChapters() {
    for (const tick of track.querySelectorAll('.tick')) tick.remove();
    for (const ratio of chapterMarks(chapters, total)) track.append(h('i', { class: 'tick', style: { left: `${ratio * 100}%` } }));
  }

  function setupTrickplay() {
    const sets = item.Trickplay ? Object.values(item.Trickplay)[0] : null;
    if (!sets) return;
    const [width, info] = Object.entries(sets).sort((a, b) => Number(a[0]) - Number(b[0]))[0] ?? [];
    if (!info) return;
    const sourceId = Object.keys(item.Trickplay)[0];
    trick = { ...info, url: (tile) => signed(`Videos/${item.Id}/Trickplay/${width}/${tile}.jpg?mediaSourceId=${sourceId}`) };
  }

  if (!native) addEventListener('keydown', keys);
  document.addEventListener('visibilitychange', onHide);
  addEventListener('pagehide', onPageHide);
  begin();

  return {
    el, title: tr('Lecture'),
    leave() { if (started) stop(); },
    dispose() {
      disposed = true;
      clearInterval(progressTimer); clearInterval(countdownTimer); clearInterval(stallClock); clearTimeout(hideTimer); clearTimeout(nativeCheck); stopWaiting();
      document.removeEventListener('fullscreenchange', fullscreenChanged);
      leaveFullscreen();
      removeEventListener('keydown', keys);
      document.removeEventListener('visibilitychange', onHide);
      removeEventListener('pagehide', onPageHide);
      unwatch(); remote.close(); group.unbind(); groupLine.dispose();
      clearTimeout(groupTimer); clearTimeout(stallTimer); clearTimeout(groupSeekTimer);
      for (const [name, fn] of Object.entries(on)) video.removeEventListener(name, fn);
      video.textTracks.removeEventListener('change', textTracksChanged);
      video.textTracks.removeEventListener('addtrack', trackAdded);
      video.audioTracks?.removeEventListener?.('change', audioTracksChanged);
      exitPresentation();
      hls?.destroy(); hls = null;
      video.pause();
      audioSession(false);
      for (const t of [...video.querySelectorAll('track')]) t.remove();
      video.removeAttribute('src');
      video.removeAttribute('poster');
      video.load();
      video.remove();
      // A video that once had sound stays iOS's « Now Playing » item, paused, until it is muted: muted once out of
      // Apple's player (muting in full screen changes nothing), it leaves the lock screen and Control Center.
      if (native) video.muted = true;
      if ('mediaSession' in navigator) {
        // Nothing left for the lock screen or the Dynamic Island once the player is gone.
        navigator.mediaSession.metadata = null;
        for (const action of ACTIONS) { try { navigator.mediaSession.setActionHandler(action, null); } catch { /* unsupported action */ } }
      }
    },
  };
}
