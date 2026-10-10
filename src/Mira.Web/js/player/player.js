// The player: Jellyfin's stream in the browser's own video engine. On iPhone and iPad, Apple's player does it all,
// in full screen: play and pause, the timeline, AirPlay, Picture in Picture, speed, and the subtitles and audio tracks
// of its menu. Elsewhere, Mira's controls on top: resume, ±10 s, a timeline with previews, opening and recap to skip,
// the next episode, audio and subtitle tracks, speed, quality, AirPlay and Picture in Picture. Progress goes to
// Jellyfin as on Windows, and the next episode follows in the same player.
import { api, signed } from '../api.js';
import { h, icon, clear, clock, seconds, ticks, episodeCode } from '../dom.js';
import { settings, device } from '../session.js';
import { artFor, imageUrl, picture } from '../images.js';
import { sheet, toast, changed, titleHref } from '../components.js';
import { goBack } from '../app.js';
import { deviceProfile, useHlsJs, audioTracks as switchesAudio } from './profile.js';
import { sharedVideo, appleNative } from './video.js';
import { trackText, chooseTracks, trackMemory } from './tracks.js';
import { QUALITIES } from '../views/settings.js';

const SKIP_LABELS = { Intro: 'Passer l’intro', Recap: 'Passer le récap', Preview: 'Passer l’aperçu', Commercial: 'Passer la pub' };
const AUTO_SKIP = ['Intro', 'Recap'];
const NEXT_LEAD = 20;      // seconds before the end when, without an end credits marker, the next episode is offered
const COUNTDOWN = 10;      // seconds before it starts by itself
const PROGRESS_EVERY = 10_000;

function isLocalHost(host) {
  return /^(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.|\[?::1\]?$|[^.]+\.local$)/i.test(host);
}

/** The bitrate to ask for: the chosen one, or, on « Automatique », full quality at home and a measure elsewhere. */
async function bitrate() {
  const choice = settings.get('quality');
  if (typeof choice === 'number') return choice;
  if (choice === 'max' || isLocalHost(location.hostname)) return 120_000_000;
  try {
    const measured = await api.bitrateTest(1_000_000);
    return Math.max(1_500_000, Math.min(40_000_000, Math.round(measured * 0.7)));
  } catch { return 8_000_000; }
}

const ERRORS = {
  NotAllowed: 'Ton compte Jellyfin n’a pas le droit de lire ce titre.',
  NoCompatibleStream: 'Jellyfin ne sait pas convertir ce fichier pour cet appareil.',
  RateLimitExceeded: 'Le serveur a atteint sa limite de lectures simultanées.',
};

/** Tracks chosen on the title page, from the player's address (« ?audio=2&sous-titres=-1 »). */
function chosenTracks(query) {
  const number = (key) => (query.has(key) && /^-?\d+$/.test(query.get(key)) ? Number(query.get(key)) : null);
  const tracks = { audio: number('audio'), subtitle: number('sous-titres') };
  return tracks.audio == null && tracks.subtitle == null ? null : tracks;
}

export function create({ id, query }) {
  const video = sharedVideo();
  const native = appleNative();
  const el = h('div', { class: ['player', native && 'native'], role: 'region', 'aria-label': 'Lecteur vidéo' });

  // ---------- The title playing (the next episode replaces it in the same player) ----------
  let item = null, source = null, playSessionId = '', playMethod = 'DirectPlay', hls = null;
  let offset = 0;             // progressive conversions start at the requested point: their time 0 is `offset`
  let progressive = false, total = 0, audioIndex = null, subtitleIndex = null, forceTranscode = false;
  let inband = false;         // the HLS stream carries the text subtitles (Apple's player): no <track> beside it
  let started = false, ended = false, progressTimer = 0, lastReport = 0;
  let segments = [], chapters = [], trick = null, next = null, nextDismissed = false, countdown = 0, countdownTimer = 0;
  let pendingStart = 0, opening = 0, beginning = 0, target = { id, fromStart: query.get('debut') === '1', tracks: chosenTracks(query) };
  const skipped = new Set();
  // ---------- The player itself ----------
  let disposed = false, closing = false, switching = false, failed = false;
  let hideTimer = 0, scrubbing = false, mode = 'inline', nativeCheck = 0, tracksSettle = 0;

  const position = () => offset + (video.currentTime || 0);
  const presentation = () => video.webkitPresentationMode ?? (video.webkitDisplayingFullscreen ? 'fullscreen' : 'inline');

  // ---------- Elements ----------
  const title = h('div', { class: 'p-title' }, h('strong', {}, ''), h('span', {}, ''));
  const airplay = h('button', { class: 'round flat', 'aria-label': 'AirPlay', hidden: true, on: { click: () => video.webkitShowPlaybackTargetPicker?.() } }, icon('airplay'));
  const pip = h('button', { class: 'round flat', 'aria-label': 'Image dans l’image', hidden: !pipSupported(), on: { click: togglePip } }, icon('pip'));
  const tracksButton = h('button', { class: 'round flat', 'aria-label': 'Audio et sous-titres', on: { click: tracksSheet } }, icon('subtitles'));
  const moreButton = h('button', { class: 'round flat', 'aria-label': 'Vitesse et qualité', on: { click: moreSheet } }, icon('sliders'));
  const playButton = h('button', { class: 'round big', 'aria-label': 'Lecture', on: { click: (e) => { e.stopPropagation(); togglePlay(); } } }, icon('play', { size: 36 }));
  const skipMark = (name) => h('span', { class: 'skip-mark' }, icon(name, { size: 36 }), h('b', {}, '10'));
  const back10 = h('button', { class: 'round', 'aria-label': 'Reculer de 10 secondes', on: { click: (e) => { e.stopPropagation(); jump(-10); } } }, skipMark('back10'));
  const fwd10 = h('button', { class: 'round', 'aria-label': 'Avancer de 10 secondes', on: { click: (e) => { e.stopPropagation(); jump(10); } } }, skipMark('forward10'));
  const nextButton = h('button', { class: 'round flat', 'aria-label': 'Épisode suivant', hidden: true, on: { click: () => playNext() } }, icon('next'));
  const muteButton = h('button', { class: 'round flat', 'aria-label': 'Couper le son', hidden: /iPhone|iPad/.test(device.name), on: { click: () => { video.muted = !video.muted; updateMute(); } } }, icon('volume'));
  const fullButton = h('button', { class: 'round flat', 'aria-label': 'Plein écran', on: { click: fullscreen } }, icon('fullscreen'));
  const now = h('span', { class: 'p-time' }, '0:00');
  const left = h('span', { class: 'p-time right' }, '');
  const played = h('div', { class: 'played' });
  const buffered = h('div', { class: 'buffered' });
  const knob = h('div', { class: 'knob' });
  const track = h('div', { class: 'track' }, buffered, played, knob);
  const tipThumb = h('div', { class: 'thumb', hidden: true });
  const tipTime = h('span', {}, '');
  const tip = h('div', { class: 'scrub-tip', hidden: true }, tipThumb, tipTime);
  const timeline = h('div', { class: 'timeline', role: 'slider', tabIndex: 0, 'aria-label': 'Position', 'aria-valuemin': '0' }, track, tip);
  const busy = h('div', { class: 'p-busy', hidden: true }, h('div', { class: 'spinner' }));
  const pillLayer = h('div');
  const message = h('div', { class: 'p-message', hidden: true });

  // Apple's player covers the screen: under it, the title and what Mira is doing, seen while it opens and closes.
  const nativeStatus = h('div', { class: 'n-status', role: 'status', 'aria-live': 'polite' });
  const nativeScreen = h('div', { class: 'n-screen' });

  if (native) {
    video.controls = false;
    nativeScreen.append(video);
    el.append(
      h('div', { class: 'n-top' }, h('button', { class: 'round', 'aria-label': 'Retour', on: { click: close } }, icon('back'))),
      h('div', { class: 'n-stage' }, nativeScreen, title, nativeStatus),
      message);
  } else {
    const controls = h('div', { class: 'controls layer' },
      h('div', { class: 'p-top' },
        h('button', { class: 'round flat', 'aria-label': 'Retour', on: { click: close } }, icon('back')),
        title, airplay, pip, tracksButton, moreButton),
      h('div', { class: 'p-center' }, back10, playButton, fwd10),
      h('div', { class: 'p-bottom' },
        timeline,
        h('div', { class: 'p-row' }, now, h('div', { class: 'spacer' }), muteButton, nextButton, fullButton, left)),
      busy);
    const surface = h('div', { class: 'layer', 'aria-hidden': 'true' });
    el.append(video, surface, controls, pillLayer, message);
    setupSurface(surface, controls);
  }

  /** What the page under Apple's player says: preparing, a tap to start, Picture in Picture, or nothing. */
  function showNative(state) {
    if (!native) return;
    clear(nativeStatus);
    if (state === 'loading') nativeStatus.append(h('div', { class: 'spinner' }), h('p', {}, 'Préparation de la lecture…'));
    if (state === 'tap') {
      nativeStatus.append(
        h('button', { class: 'round big n-play', 'aria-label': 'Lecture', on: { click: () => { showNative('loading'); if (enterNative()) play(); else showNative('tap'); } } }, icon('play', { size: 36 })),
        h('p', {}, 'Touche pour lancer la lecture en plein écran.'));
    }
    if (state === 'pip') {
      nativeStatus.append(h('p', {}, 'Lecture en image dans l’image.'),
        h('div', { class: 'actions' },
          h('button', { class: 'btn', on: { click: close } }, 'Arrêter'),
          h('button', { class: 'btn primary', on: { click: () => { try { video.webkitSetPresentationMode('fullscreen'); } catch { enterNative(); } } } }, icon('fullscreen', { size: 20 }), 'Plein écran')));
    }
  }

  // ---------- Controls visibility (Mira's controls) ----------
  function showControls(hold = false) {
    el.classList.remove('idle');
    clearTimeout(hideTimer);
    if (!hold && !video.paused && !scrubbing) hideTimer = setTimeout(() => el.classList.add('idle'), 3200);
  }
  function hideControls() { if (!video.paused && !scrubbing) el.classList.add('idle'); }

  // A tap shows or hides the controls; a double tap on a side skips 10 s, as in the Apple TV app.
  function setupSurface(surface, controls) {
    let lastTap = 0, lastSide = '', tapTimer = 0;
    surface.addEventListener('click', (e) => {
      const rect = el.getBoundingClientRect();
      const side = e.clientX < rect.width * 0.35 ? 'left' : e.clientX > rect.width * 0.65 ? 'right' : 'middle';
      const time = Date.now();
      if (time - lastTap < 300 && side === lastSide && side !== 'middle') {
        clearTimeout(tapTimer);
        jump(side === 'left' ? -10 : 10);
        flash(side);
        lastTap = 0;
        return;
      }
      lastTap = time; lastSide = side;
      clearTimeout(tapTimer);
      tapTimer = setTimeout(() => (el.classList.contains('idle') ? showControls() : hideControls()), side === 'middle' ? 0 : 260);
    });
    el.addEventListener('pointermove', (e) => { if (e.pointerType === 'mouse') showControls(); });
    controls.addEventListener('click', () => showControls());
  }

  function flash(side) {
    const mark = h('div', { class: ['p-flash', side] }, icon(side === 'left' ? 'back10' : 'forward10', { size: 28 }), '10 s');
    el.append(mark);
    setTimeout(() => mark.remove(), 600);
  }

  // ---------- Playback ----------
  function togglePlay() {
    if (video.paused) play(); else video.pause();
  }
  async function play() {
    try { await video.play(); hideMessage(); }
    catch (error) {
      if (error?.name === 'NotAllowedError') showTapToPlay();
    }
  }
  function showTapToPlay() {
    if (native) { showNative('tap'); return; }
    showMessage(h('div', {},
      h('button', { class: 'round big', style: { width: '84px', height: '84px', background: 'rgba(245,245,247,.95)', color: '#0b0b0c' }, 'aria-label': 'Lecture', on: { click: () => { hideMessage(); play(); } } }, icon('play', { size: 36 })),
      h('p', {}, item ? item.Name : '')));
  }
  function jump(delta) { seek(position() + delta); showControls(); }
  function seek(at) {
    const end = total || video.duration || 0;
    const value = Math.max(0, end ? Math.min(at, end - 1) : at);
    if (progressive) { open(ticks(value)); return; }
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
    if (!native || disposed || mode === was) return;
    if (mode === 'fullscreen') { showNative(''); return; }
    if (mode === 'picture-in-picture') { showNative('pip'); return; }
    // Back in the page: Apple's player was closed (Terminé, a swipe down), or Picture in Picture was.
    if (closing || failed || switching || ended || video.ended) return;
    if (was === 'picture-in-picture' && !video.paused) { if (!enterNative()) { video.pause(); showNative('tap'); } return; }
    close();
  }

  function updatePlayIcon() {
    clear(playButton).append(icon(video.paused ? 'play' : 'pause', { size: 36 }));
    playButton.setAttribute('aria-label', video.paused ? 'Lecture' : 'Pause');
  }
  function updateMute() { clear(muteButton).append(icon(video.muted ? 'mute' : 'volume')); }

  function updateTime() {
    if (scrubbing) return;
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
    timeline.setAttribute('aria-valuetext', `${clock(at)} sur ${clock(end)}`);
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
    if (trick) {
      const index = Math.min(trick.ThumbnailCount - 1, Math.floor((at * 1000) / trick.Interval));
      const perTile = trick.TileWidth * trick.TileHeight, tile = Math.floor(index / perTile), cell = index % perTile;
      const width = 160, height = Math.round((160 * trick.Height) / trick.Width);
      tipThumb.hidden = false;
      Object.assign(tipThumb.style, {
        width: `${width}px`, height: `${height}px`,
        backgroundImage: `url("${trick.url(tile)}")`,
        backgroundSize: `${trick.TileWidth * width}px auto`,
        backgroundPosition: `-${(cell % trick.TileWidth) * width}px -${Math.floor(cell / trick.TileWidth) * height}px`,
      });
    }
    return at;
  }
  timeline.addEventListener('pointerdown', (e) => {
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
    const segment = segments.find((s) => SKIP_LABELS[s.Type] && at >= seconds(s.StartTicks) && at < seconds(s.EndTicks) - 1);
    // Skipped once by itself when the settings say so: going back into it plays it.
    if (segment && settings.get('autoSkip') && AUTO_SKIP.includes(segment.Type) && !skipped.has(segment)) {
      skipped.add(segment);
      seek(seconds(segment.EndTicks));
      return;
    }
    // Apple's player has no room for Mira's buttons: there, the next episode starts at the end.
    if (native) return;
    if (segment !== currentSkip) {
      currentSkip = segment;
      pillLayer.querySelector('.pill')?.remove();
      if (segment) {
        pillLayer.append(h('button', { class: 'pill', on: { click: (e) => { e.stopPropagation(); seek(seconds(segment.EndTicks)); } } }, SKIP_LABELS[segment.Type], icon('next', { size: 20 })));
      }
    }
    if (!next || nextDismissed || !settings.get('autoNext') || !end) return;
    const outro = segments.find((s) => s.Type === 'Outro');
    const from = outro ? seconds(outro.StartTicks) : end - NEXT_LEAD;
    if (at >= from && !countdownTimer && !video.paused) offerNext();
  }

  function offerNext() {
    countdown = COUNTDOWN;
    const counter = h('span', {}, `Dans ${countdown} s`);
    const card = h('div', { class: 'up-next', role: 'dialog', 'aria-label': 'Épisode suivant' },
      picture(artFor(next, 'still'), { kind: 'wide', width: 112 }),
      h('div', {}, h('div', { class: 'label' }, 'Épisode suivant'), h('strong', { class: 'clamp-2' }, `${episodeCode(next)} · ${next.Name ?? ''}`), counter),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', on: { click: (e) => { e.stopPropagation(); cancelNext(); } } }, 'Annuler'),
        h('button', { class: 'btn primary', on: { click: (e) => { e.stopPropagation(); playNext(); } } }, icon('play', { size: 18 }), 'Lire')));
    pillLayer.append(card);
    countdownTimer = setInterval(() => {
      if (video.paused) return;
      countdown -= 1;
      counter.textContent = `Dans ${countdown} s`;
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
    const upcoming = next, resume = progressFraction(upcoming) > 0;
    switching = true;
    clearInterval(countdownTimer); countdownTimer = 0;
    clear(pillLayer);
    await stop();
    if (disposed || closing) return;
    history.replaceState(history.state, '', `#/lecture/${upcoming.Id}${resume ? '' : '?debut=1'}`);
    resetTitle();
    target = { id: upcoming.Id, fromStart: !resume, tracks: null };
    await begin();
  }
  const progressFraction = (x) => (x.RunTimeTicks ? (x.UserData?.PlaybackPositionTicks ?? 0) / x.RunTimeTicks : 0);

  function resetTitle() {
    clearInterval(progressTimer);
    item = null; source = null; playSessionId = ''; playMethod = 'DirectPlay'; offset = 0; progressive = false; total = 0; inband = false;
    audioIndex = null; subtitleIndex = null; forceTranscode = false; started = false; ended = false;
    segments = []; chapters = []; trick = null; next = null; nextDismissed = false; currentSkip = null;
    skipped.clear();
    nextButton.hidden = true;
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
    if (!source?.MediaStreams?.length) { audioIndex = chosen?.audio ?? null; subtitleIndex = chosen?.subtitle ?? null; return; }
    const memory = item.SeriesId ? settings.get('seriesTracks')?.[item.SeriesId] ?? null : null;
    const auto = chooseTracks(source, { memory, audioOrder: settings.get('audioLanguages'), subtitleOrder: settings.get('subtitleLanguages') });
    const has = (type, index) => source.MediaStreams.some((s) => s.Type === type && s.Index === index);
    audioIndex = chosen?.audio != null && has('Audio', chosen.audio) ? chosen.audio : auto.audio;
    subtitleIndex = chosen?.subtitle === -1 || (chosen?.subtitle != null && has('Subtitle', chosen.subtitle)) ? chosen.subtitle : auto.subtitle;
    if (chosen) remember();
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
    busy.hidden = false; hideMessage(); failed = false;
    if (started) { await report('progress'); }
    try {
      const max = await bitrate();
      const info = await api.playbackInfo(item.Id, {
        DeviceProfile: deviceProfile(max, { hlsSubtitles: native }), MaxStreamingBitrate: max, StartTimeTicks: startTicks,
        AudioStreamIndex: audioIndex ?? undefined, SubtitleStreamIndex: subtitleIndex ?? undefined, MediaSourceId: source?.Id,
        EnableDirectPlay: !forceTranscode, EnableDirectStream: !forceTranscode, EnableTranscoding: true,
        AllowVideoStreamCopy: !forceTranscode, AllowAudioStreamCopy: true, AutoOpenLiveStream: true,
      });
      if (attempt !== opening || disposed) return;
      if (info?.ErrorCode) throw new Error(ERRORS[info.ErrorCode] ?? 'Jellyfin n’a pas pu préparer ce titre pour cet appareil.');
      const previousSession = playSessionId;
      source = info.MediaSources?.[0];
      if (!source) throw new Error('Ce titre n’a aucun fichier lisible.');
      playSessionId = info.PlaySessionId;
      if (previousSession && previousSession !== playSessionId && playMethod !== 'DirectPlay') api.stopEncoding(previousSession);
      audioIndex ??= source.DefaultAudioStreamIndex ?? null;
      subtitleIndex ??= source.DefaultSubtitleStreamIndex ?? -1;
      total = seconds(source.RunTimeTicks ?? item.RunTimeTicks);

      let url, hlsStream = false;
      inband = false;
      if (source.SupportsDirectPlay && !source.TranscodingUrl && !forceTranscode) {
        const container = (source.Container ?? 'mp4').split(',').find((c) => c === 'mp4') ?? (source.Container ?? 'mp4').split(',')[0];
        url = signed(`Videos/${item.Id}/stream.${container}?Static=true&mediaSourceId=${encodeURIComponent(source.Id)}&deviceId=${device.id}${source.ETag ? `&Tag=${source.ETag}` : ''}`);
        playMethod = 'DirectPlay'; progressive = false; offset = 0;
      } else if (source.TranscodingUrl) {
        let address = source.TranscodingUrl;
        hlsStream = source.TranscodingSubProtocol === 'hls' || /\.m3u8/i.test(address);
        // Apple's player: every text subtitle in the stream, for its menu. Jellyfin writes them there only with
        // SubtitleMethod=Hls, which it adds when a text subtitle is chosen; with none chosen, Mira asks for it (all
        // are then off at first). Never with burned-in pictures (SubtitleMethod=Encode).
        if (native && hlsStream && textSubtitles().length) {
          if (!/[?&]SubtitleMethod=/i.test(address)) address += `${address.includes('?') ? '&' : '?'}SubtitleMethod=Hls`;
          inband = /[?&]SubtitleMethod=Hls/i.test(address);
          // Jellyfin times them for MPEG-TS segments (10 s late on fMP4 in Apple's player): Mira's plugin serves the
          // same playlists with the subtitles in time (Mira.Jellyfin, HlsPlaylists).
          if (inband && /[?&]SegmentContainer=mp4/i.test(address)) {
            address = address.replace(/^\/videos\/([0-9a-f-]+)\/master\.m3u8/i, '/Mira/hls/$1/master.m3u8');
            // Apple's menu shows the stream's names: Mira's, in French, rather than Jellyfin's (« French - SUBRIP - External »).
            address += `&MiraNames=${encodeURIComponent(textSubtitles().map((s) => `${s.Index}:${menuName(s).replace(/\|/g, '/')}`).join('|'))}`;
          }
        }
        url = signed(address);
        playMethod = source.SupportsDirectStream && !/VideoCodec=|videoBitrate=/i.test(source.TranscodingUrl) ? 'DirectStream' : 'Transcode';
        progressive = !hlsStream;
        offset = progressive ? seconds(startTicks) : 0;
      } else {
        throw new Error('Jellyfin ne propose aucun flux lisible pour cet appareil.');
      }
      await attach(url, hlsStream, progressive ? 0 : seconds(startTicks));
      if (native && !enterNative()) { busy.hidden = true; showNative('tap'); return; }
      if (!keepPaused) play();
    } catch (error) {
      if (attempt !== opening || disposed) return;
      busy.hidden = true;
      fail(error.message || 'La lecture n’a pas pu démarrer.');
    }
  }

  async function attach(url, hlsStream, startSeconds) {
    hls?.destroy(); hls = null;
    for (const t of [...video.querySelectorAll('track')]) t.remove();
    pendingStart = startSeconds;
    // Subtitles go in before the stream: Safari hands the tracks it finds at loading to Apple's player and its menu.
    addSubtitles();
    tracksSettle = Date.now() + 1500;
    if (hlsStream && useHlsJs) {
      const { default: Hls } = await import('../../vendor/hls.light.min.mjs');
      hls = new Hls({ startPosition: startSeconds, maxBufferLength: 30, backBufferLength: 60, enableWorker: true });
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return;
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) hls.recoverMediaError();
        else fail('Le flux vidéo s’est interrompu. Vérifie la connexion au serveur.');
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
  }

  function addSubtitles() {
    el.style.setProperty('--cue', `${settings.get('subtitleSize')}%`);
    // Subtitles in the HLS stream (Apple's player) arrive with it: a <track> beside them would only repeat them.
    if (inband) return;
    const streams = source?.MediaStreams ?? [];
    const subtitles = streams.filter((s) => s.Type === 'Subtitle');
    for (const stream of subtitles) {
      if (stream.DeliveryMethod !== 'External' || !stream.DeliveryUrl) continue;
      video.append(h('track', {
        kind: stream.IsForced ? 'forced' : 'subtitles', label: trackText(stream, subtitles.indexOf(stream) + 1).label, srclang: stream.Language ?? 'und',
        src: signed(stream.DeliveryUrl), default: stream.Index === subtitleIndex, dataset: { index: String(stream.Index) },
      }));
    }
  }
  /** A subtitle's name in Apple's menu: Mira's, with its details when another one has the same. */
  function menuName(stream) {
    const subtitles = (source?.MediaStreams ?? []).filter((s) => s.Type === 'Subtitle');
    const text = (s) => trackText(s, subtitles.indexOf(s) + 1);
    const { label, details } = text(stream);
    return textSubtitles().some((s) => s !== stream && text(s).label === label) && details ? `${label} (${details})` : label;
  }
  /** The text subtitles Jellyfin can send as text, in its order: the order of the HLS stream's subtitles. */
  const textSubtitles = () => (source?.MediaStreams ?? []).filter((s) => s.Type === 'Subtitle' && s.IsTextSubtitleStream);
  const trackNode = (textTrack) => [...video.querySelectorAll('track')].find((x) => x.track === textTrack) ?? null;
  /** The subtitle tracks the HLS stream carries, as the browser lists them. */
  const streamTracks = () => [...video.textTracks].filter((t) => !trackNode(t) && ['subtitles', 'captions', 'forced'].includes(t.kind));
  /** Jellyfin's index of a subtitle track: beside the video, its <track>; in the HLS stream, its place there (or its name). */
  function trackIndex(textTrack) {
    const node = trackNode(textTrack);
    if (node) return Number(node.dataset.index);
    if (!inband) return null;
    const carried = streamTracks(), streams = textSubtitles(), at = carried.indexOf(textTrack);
    if (at < 0) return null;
    if (carried.length === streams.length) return streams[at].Index;
    const named = streams.filter((s) => s.DisplayTitle === textTrack.label || menuName(s) === textTrack.label);
    return named.length === 1 ? named[0].Index : null;
  }
  /** Shows the chosen subtitles among those the browser has, beside the video or in the stream; hides the others. */
  function showTrack() {
    for (const t of video.textTracks) {
      const index = trackIndex(t);
      if (index == null) continue;
      const mode = index === subtitleIndex ? 'showing' : 'disabled';
      if (t.mode !== mode) t.mode = mode;
    }
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
      title: 'Audio et sous-titres',
      items: [
        ...audio.map((s, i) => ({ label: trackText(s, i + 1).label, sub: describe('Audio', s, i + 1), selected: s.Index === audioIndex, run: () => switchTracks({ audio: s.Index }) })),
        { label: 'Sans sous-titres', sub: 'Sous-titres', selected: subtitleIndex === -1 || subtitleIndex == null, run: () => switchTracks({ subtitle: -1 }) },
        ...subs.map((s, i) => ({ label: trackText(s, i + 1).label, sub: describe('Sous-titres', s, i + 1), selected: s.Index === subtitleIndex, run: () => switchTracks({ subtitle: s.Index }) })),
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
      if (subtitle === -1 || stream?.DeliveryMethod === 'External' || (inband && stream?.IsTextSubtitleStream)) {
        showTrack();
        if (wasBurned) open(ticks(at), { keepPaused: wasPaused });
        else report('progress');
      } else open(ticks(at), { keepPaused: wasPaused });
    }
  }
  const burned = () => /SubtitleMethod=Encode/i.test(source?.TranscodingUrl ?? '');

  function moreSheet() {
    const speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];
    const quality = settings.get('quality');
    sheet({
      title: 'Vitesse et qualité',
      items: [
        ...speeds.map((s) => ({ label: s === 1 ? 'Vitesse normale' : `Vitesse × ${String(s).replace('.', ',')}`, selected: video.playbackRate === s, run: () => { video.playbackRate = s; } })),
        ...QUALITIES.map(([key, label, sub]) => ({ label: `Qualité : ${label}`, sub, selected: key === quality, run: () => {
          settings.set('quality', key);
          open(ticks(position()), { keepPaused: video.paused });
        } })),
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
    } catch { toast('L’image dans l’image n’est pas disponible ici.'); }
  }
  function fullscreen() {
    // iPhone: iOS's own player, with its controls, AirPlay and subtitles. Elsewhere: the whole player.
    if (!document.fullscreenEnabled && video.webkitEnterFullscreen) { video.webkitEnterFullscreen(); return; }
    if (document.fullscreenElement) document.exitFullscreen?.();
    else el.requestFullscreen?.().then(() => screen.orientation?.lock?.('landscape').catch(() => {})).catch(() => video.webkitEnterFullscreen?.());
  }

  // ---------- Errors and messages ----------
  function showMessage(content) { clear(message).append(content); message.hidden = false; }
  function hideMessage() { message.hidden = true; }
  function fail(text) {
    failed = true; switching = false;
    busy.hidden = true;
    clearTimeout(nativeCheck);
    exitPresentation();
    showNative('');
    showMessage(h('div', {}, icon('warning', { size: 36 }), h('h2', { class: 'h3' }, 'Lecture impossible'), h('p', {}, text),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', on: { click: close } }, 'Retour'),
        h('button', { class: 'btn primary', on: { click: retry } }, 'Réessayer'))));
  }
  function retry() {
    hideMessage(); failed = false; showNative('loading');
    if (item) open(ticks(position())); else begin();
  }

  // ---------- Video events ----------
  function fitPicture() {
    if (native) return;
    const ratio = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight : 0;
    el.classList.toggle('fitted', ratio > 0);
    if (ratio) el.style.setProperty('--ratio', ratio.toFixed(4));
  }
  const on = {
    loadedmetadata: () => {
      if (pendingStart > 0 && !progressive) { try { video.currentTime = pendingStart; } catch { /* set again on canplay */ } }
      pendingStart = 0;
      if (playMethod === 'DirectPlay') selectAudioTrack();
      fitPicture();
      updateTime();
    },
    resize: fitPicture,
    playing: () => {
      busy.hidden = true; hideMessage(); updatePlayIcon(); showControls();
      switching = false;
      if (!started) {
        started = true;
        report('start');
        clearInterval(progressTimer);
        progressTimer = setInterval(() => { if (!video.paused && Date.now() - lastReport >= PROGRESS_EVERY - 200) report('progress'); }, PROGRESS_EVERY);
      }
      // Safari may refuse full screen without a word: still in the page a moment later, Mira asks for a tap.
      if (native && presentation() === 'inline') {
        clearTimeout(nativeCheck);
        nativeCheck = setTimeout(() => {
          if (!disposed && !closing && presentation() === 'inline' && !video.paused) { video.pause(); showNative('tap'); }
        }, 1500);
      }
    },
    pause: () => { updatePlayIcon(); showControls(true); report('progress'); },
    play: () => updatePlayIcon(),
    waiting: () => { busy.hidden = false; },
    seeking: () => { busy.hidden = false; },
    seeked: () => { busy.hidden = true; updateTime(); },
    canplay: () => { busy.hidden = true; },
    timeupdate: updateTime,
    progress: updateTime,
    ratechange: () => report('progress'),
    ended: async () => {
      if (ended || disposed) return;
      ended = true;
      await stop({ finished: true });
      if (disposed || closing) return;
      if (next && settings.get('autoNext') && !nextDismissed) playNext();
      else close();
    },
    error: () => {
      if (disposed || !item || !video.getAttribute('src')) return;
      // A file Safari was thought to play directly but cannot: Jellyfin converts it instead.
      if (playMethod === 'DirectPlay' && !forceTranscode) { forceTranscode = true; open(ticks(position())); return; }
      fail('Cet appareil ne peut pas lire ce flux. Essaie une qualité plus basse dans les réglages.');
    },
    webkitpresentationmodechanged: presentationChanged,
    webkitplaybacktargetavailabilitychanged: (e) => { airplay.hidden = e.availability !== 'available'; },
  };
  for (const [name, fn] of Object.entries(on)) video.addEventListener(name, fn);
  video.textTracks.addEventListener('change', textTracksChanged);
  // The HLS stream's subtitles appear once it loads: the chosen one shows, the others stay off.
  video.textTracks.addEventListener('addtrack', showTrack);
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

  /** Back to the title page, from Retour or from Apple's player once closed. */
  async function close() {
    if (closing) return;
    closing = true;
    clearTimeout(nativeCheck);
    exitPresentation();
    video.pause();
    await stop();
    goBack(item ? titleHref(item) : '#/');
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
   * iPhone, « ambient », which iOS does not make the Now Playing app (the Dynamic Island).
   */
  function audioSession(ambient) {
    try { if ('audioSession' in navigator) navigator.audioSession.type = ambient ? 'ambient' : 'auto'; } catch { /* not settable here */ }
  }

  async function begin() {
    const attempt = ++beginning;
    busy.hidden = false; showNative('loading');
    audioSession(native && settings.get('ambientAudio'));
    // Muted when the player closed (see dispose): the title plays with its sound.
    video.muted = false; updateMute();
    try {
      let found = await api.item(target.id);
      if (found.Type === 'Series') {
        const up = await api.seriesNext(found.Id);
        if (!up) throw new Error('Cette série n’a aucun épisode à lire.');
        found = await api.item(up.Id);
      }
      if (disposed || attempt !== beginning) return;
      item = found;
      document.title = `${item.Type === 'Episode' ? item.SeriesName : item.Name} · Mira`;
      title.firstChild.textContent = item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name;
      title.lastChild.textContent = item.Type === 'Episode' ? `${episodeCode(item)} · ${item.Name ?? ''}` : [item.ProductionYear, item.OfficialRating].filter(Boolean).join(' · ');
      chapters = item.Chapters ?? [];
      const fraction = progressFraction(item);
      const resume = !target.fromStart && settings.get('resume') && fraction > 0 && fraction < 0.95 ? item.UserData.PlaybackPositionTicks : 0;
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
      await open(resume);
      api.segments(item.Id).then((list) => { if (attempt === beginning) segments = list; });
      if (item.Type === 'Episode') {
        api.nextEpisode(item).then((n) => { if (attempt !== beginning) return; next = n; nextButton.hidden = !n; mediaSession(); }).catch(() => {});
      }
    } catch (error) {
      if (attempt === beginning && !disposed) fail(error.message);
    }
  }

  function drawChapters() {
    for (const tick of track.querySelectorAll('.tick')) tick.remove();
    if (!total || chapters.length < 2) return;
    for (const chapter of chapters.slice(1)) {
      const at = seconds(chapter.StartPositionTicks);
      if (at > 0 && at < total) track.append(h('i', { class: 'tick', style: { left: `${(at / total) * 100}%` } }));
    }
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
    el, title: 'Lecture',
    leave() { if (started) stop(); },
    dispose() {
      disposed = true;
      clearInterval(progressTimer); clearInterval(countdownTimer); clearTimeout(hideTimer); clearTimeout(nativeCheck);
      removeEventListener('keydown', keys);
      document.removeEventListener('visibilitychange', onHide);
      removeEventListener('pagehide', onPageHide);
      for (const [name, fn] of Object.entries(on)) video.removeEventListener(name, fn);
      video.textTracks.removeEventListener('change', textTracksChanged);
      video.textTracks.removeEventListener('addtrack', showTrack);
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
      video.muted = true;
      if ('mediaSession' in navigator) {
        // Nothing left for the lock screen or the Dynamic Island once the player is gone.
        navigator.mediaSession.metadata = null;
        for (const action of ACTIONS) { try { navigator.mediaSession.setActionHandler(action, null); } catch { /* unsupported action */ } }
      }
    },
  };
}
