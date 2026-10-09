// The player: Jellyfin's stream in Safari's own video engine, with Mira's controls on top: resume, ±10 s, a
// timeline with previews, opening and recap to skip, the next episode, audio and subtitle tracks, speed, quality,
// AirPlay, Picture in Picture and iOS's own full screen. Progress goes to Jellyfin as on Windows.
import { api, signed } from '../api.js';
import { h, icon, clear, clock, seconds, ticks, episodeCode } from '../dom.js';
import { settings, device } from '../session.js';
import { artFor, imageUrl, picture } from '../images.js';
import { sheet, toast, changed, titleHref } from '../components.js';
import { goBack } from '../app.js';
import { deviceProfile, nativeHls, useHlsJs } from './profile.js';
import { sharedVideo } from './video.js';
import { trackText } from './tracks.js';
import { QUALITIES } from '../views/settings.js';

const SKIP_LABELS = { Intro: 'Passer l’intro', Recap: 'Passer le récap', Preview: 'Passer l’aperçu', Commercial: 'Passer la pub' };
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

export function create({ id, query }) {
  const video = sharedVideo();
  const fromStart = query.get('debut') === '1';
  const el = h('div', { class: 'player', role: 'region', 'aria-label': 'Lecteur vidéo' });

  // ---------- State ----------
  let item = null, source = null, playSessionId = '', playMethod = 'DirectPlay', hls = null;
  let offset = 0;             // progressive conversions start at the requested point: their time 0 is `offset`
  let progressive = false, total = 0, audioIndex = null, subtitleIndex = null, forceTranscode = false;
  let started = false, ended = false, disposed = false, progressTimer = 0, lastReport = 0;
  let segments = [], chapters = [], trick = null, next = null, nextDismissed = false, countdown = 0, countdownTimer = 0;
  let hideTimer = 0, scrubbing = false, pendingStart = 0, opening = 0;

  const position = () => offset + (video.currentTime || 0);

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
  const controls = h('div', { class: 'controls layer' },
    h('div', { class: 'p-top' },
      h('button', { class: 'round flat', 'aria-label': 'Retour', on: { click: leave } }, icon('back')),
      title, airplay, pip, tracksButton, moreButton),
    h('div', { class: 'p-center' }, back10, playButton, fwd10),
    h('div', { class: 'p-bottom' },
      timeline,
      h('div', { class: 'p-row' }, now, h('div', { class: 'spacer' }), muteButton, nextButton, fullButton, left)),
    busy);
  const surface = h('div', { class: 'layer', 'aria-hidden': 'true' });
  el.append(video, surface, controls, pillLayer, message);

  // ---------- Controls visibility ----------
  function showControls(hold = false) {
    el.classList.remove('idle');
    clearTimeout(hideTimer);
    if (!hold && !video.paused && !scrubbing) hideTimer = setTimeout(() => el.classList.add('idle'), 3200);
  }
  function hideControls() { if (!video.paused && !scrubbing) el.classList.add('idle'); }

  // A tap shows or hides the controls; a double tap on a side skips 10 s, as in the Apple TV app.
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
    showMessage(h('div', {},
      h('button', { class: 'round big', style: { width: '84px', height: '84px', background: 'rgba(245,245,247,.95)', color: '#0b0b0c' }, 'aria-label': 'Lecture', on: { click: () => { hideMessage(); play(); } } }, icon('play', { size: 36 })),
      h('p', {}, item ? item.Name : '')));
  }
  function jump(delta) { seek(position() + delta); showControls(); }
  function seek(target) {
    const end = total || video.duration || 0;
    const value = Math.max(0, end ? Math.min(target, end - 1) : target);
    if (progressive) { open(ticks(value)); return; }
    video.currentTime = value - offset;
    report('progress');
  }

  function updatePlayIcon() {
    clear(playButton).append(icon(video.paused ? 'play' : 'pause', { size: 36 }));
    playButton.setAttribute('aria-label', video.paused ? 'Lecture' : 'Pause');
  }
  function updateMute() { clear(muteButton).append(icon(video.muted ? 'mute' : 'volume')); }

  function updateTime() {
    if (scrubbing) return;
    const end = total || video.duration || 0, at = position();
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
    markers(at, end);
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
  async function playNext() {
    if (!next) return;
    clearInterval(countdownTimer); countdownTimer = 0;
    await stop();
    location.replace(`#/lecture/${next.Id}${progressFraction(next) > 0 ? '' : '?debut=1'}`);
  }
  const progressFraction = (x) => (x.RunTimeTicks ? (x.UserData?.PlaybackPositionTicks ?? 0) / x.RunTimeTicks : 0);

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

  // ---------- Opening the stream ----------
  async function open(startTicks, { keepPaused = false } = {}) {
    const attempt = ++opening;
    busy.hidden = false; hideMessage();
    if (started) { await report('progress'); }
    try {
      const max = await bitrate();
      const info = await api.playbackInfo(item.Id, {
        DeviceProfile: deviceProfile(max), MaxStreamingBitrate: max, StartTimeTicks: startTicks,
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
      if (source.SupportsDirectPlay && !source.TranscodingUrl && !forceTranscode) {
        const container = (source.Container ?? 'mp4').split(',').find((c) => c === 'mp4') ?? (source.Container ?? 'mp4').split(',')[0];
        url = signed(`Videos/${item.Id}/stream.${container}?Static=true&mediaSourceId=${encodeURIComponent(source.Id)}&deviceId=${device.id}${source.ETag ? `&Tag=${source.ETag}` : ''}`);
        playMethod = 'DirectPlay'; progressive = false; offset = 0;
      } else if (source.TranscodingUrl) {
        url = signed(source.TranscodingUrl);
        hlsStream = source.TranscodingSubProtocol === 'hls' || /\.m3u8/i.test(source.TranscodingUrl);
        playMethod = source.SupportsDirectStream && !/VideoCodec=|videoBitrate=/i.test(source.TranscodingUrl) ? 'DirectStream' : 'Transcode';
        progressive = !hlsStream;
        offset = progressive ? seconds(startTicks) : 0;
      } else {
        throw new Error('Jellyfin ne propose aucun flux lisible pour cet appareil.');
      }
      await attach(url, hlsStream, progressive ? 0 : seconds(startTicks));
      addSubtitles();
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
  }

  function addSubtitles() {
    const streams = source?.MediaStreams ?? [];
    for (const stream of streams) {
      if (stream.Type !== 'Subtitle' || stream.DeliveryMethod !== 'External' || !stream.DeliveryUrl) continue;
      video.append(h('track', {
        kind: 'subtitles', label: trackText(stream, streams.filter((s) => s.Type === 'Subtitle').indexOf(stream) + 1).label, srclang: stream.Language ?? 'und',
        src: signed(stream.DeliveryUrl), default: stream.Index === subtitleIndex, dataset: { index: String(stream.Index) },
      }));
    }
    const show = () => {
      for (const t of video.textTracks) {
        const el2 = [...video.querySelectorAll('track')].find((x) => x.track === t);
        t.mode = el2 && Number(el2.dataset.index) === subtitleIndex ? 'showing' : 'disabled';
      }
    };
    show();
    setTimeout(show, 300);
    el.style.setProperty('--cue', `${settings.get('subtitleSize')}%`);
  }

  // ---------- Tracks, speed and quality ----------
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
    if (audio != null && audio !== audioIndex) { audioIndex = audio; open(ticks(at), { keepPaused: wasPaused }); return; }
    if (subtitle != null && subtitle !== subtitleIndex) {
      const stream = (source?.MediaStreams ?? []).find((s) => s.Index === subtitle);
      subtitleIndex = subtitle;
      // Text subtitles already beside the video switch at once; pictures need a new conversion.
      if (subtitle === -1 || stream?.DeliveryMethod === 'External') {
        for (const t of video.textTracks) {
          const node = [...video.querySelectorAll('track')].find((x) => x.track === t);
          t.mode = node && Number(node.dataset.index) === subtitle ? 'showing' : 'disabled';
        }
        if (subtitle === -1 && playMethod !== 'DirectPlay' && burned()) open(ticks(at), { keepPaused: wasPaused });
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

  // ---------- Picture in Picture, AirPlay, full screen ----------
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
  if (window.WebKitPlaybackTargetAvailabilityEvent) {
    video.addEventListener('webkitplaybacktargetavailabilitychanged', (e) => { airplay.hidden = e.availability !== 'available'; });
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
    busy.hidden = true;
    showMessage(h('div', {}, icon('warning', { size: 36 }), h('h2', { class: 'h3' }, 'Lecture impossible'), h('p', {}, text),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', on: { click: leave } }, 'Retour'),
        h('button', { class: 'btn primary', on: { click: () => { hideMessage(); open(ticks(position())); } } }, 'Réessayer'))));
  }

  // ---------- Video events ----------
  function fitPicture() {
    const ratio = video.videoWidth && video.videoHeight ? video.videoWidth / video.videoHeight : 0;
    el.classList.toggle('fitted', ratio > 0);
    if (ratio) el.style.setProperty('--ratio', ratio.toFixed(4));
  }
  const on = {
    loadedmetadata: () => {
      if (pendingStart > 0 && !progressive) { try { video.currentTime = pendingStart; } catch { /* set again on canplay */ } }
      pendingStart = 0;
      fitPicture();
      updateTime();
    },
    resize: fitPicture,
    playing: () => {
      busy.hidden = true; hideMessage(); updatePlayIcon(); showControls();
      if (!started) {
        started = true;
        report('start');
        clearInterval(progressTimer);
        progressTimer = setInterval(() => { if (!video.paused && Date.now() - lastReport >= PROGRESS_EVERY - 200) report('progress'); }, PROGRESS_EVERY);
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
      if (ended) return;
      ended = true;
      await stop({ finished: true });
      if (next && settings.get('autoNext') && !nextDismissed) playNext();
      else leave();
    },
    error: () => {
      if (disposed || !item) return;
      // A file Safari was thought to play directly but cannot: Jellyfin converts it instead.
      if (playMethod === 'DirectPlay' && !forceTranscode) { forceTranscode = true; open(ticks(position())); return; }
      fail('Cet appareil ne peut pas lire ce flux. Essaie une qualité plus basse dans les réglages du lecteur.');
    },
  };
  for (const [name, fn] of Object.entries(on)) video.addEventListener(name, fn);

  function keys(e) {
    if (e.target.closest?.('input, .sheet')) return;
    const actions = {
      ' ': togglePlay, k: togglePlay, ArrowLeft: () => jump(-10), ArrowRight: () => jump(10), j: () => jump(-10), l: () => jump(10),
      f: fullscreen, m: () => { video.muted = !video.muted; updateMute(); }, n: () => next && playNext(), Escape: leave,
    };
    const action = actions[e.key];
    if (action) { e.preventDefault(); action(); showControls(); }
  }

  const onHide = () => { if (document.hidden) report('progress', true); };
  const onPageHide = () => { if (started) api.report('stop', body(), true).catch(() => {}); };

  async function leave() {
    await stop();
    goBack(item ? titleHref(item) : '#/');
  }

  // ---------- Media Session: lock screen and Control Center ----------
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
      for (const [action, fn] of Object.entries(handlers)) { try { navigator.mediaSession.setActionHandler(action, fn); } catch { /* unsupported action */ } }
    } catch { /* No Media Session here. */ }
  }

  // ---------- Start ----------
  async function begin() {
    busy.hidden = false;
    try {
      let target = await api.item(id);
      if (target.Type === 'Series') {
        target = (await api.seriesNext(target.Id)) ?? null;
        if (!target) throw new Error('Cette série n’a aucun épisode à lire.');
        target = await api.item(target.Id);
      }
      if (disposed) return;
      item = target;
      document.title = `${item.Type === 'Episode' ? item.SeriesName : item.Name} · Mira`;
      title.firstChild.textContent = item.Type === 'Episode' ? item.SeriesName ?? item.Name : item.Name;
      title.lastChild.textContent = item.Type === 'Episode' ? `${episodeCode(item)} · ${item.Name ?? ''}` : [item.ProductionYear, item.OfficialRating].filter(Boolean).join(' · ');
      chapters = item.Chapters ?? [];
      const fraction = progressFraction(item);
      const resume = !fromStart && settings.get('resume') && fraction > 0 && fraction < 0.95 ? item.UserData.PlaybackPositionTicks : 0;
      total = seconds(item.RunTimeTicks);
      drawChapters();
      setupTrickplay();
      mediaSession();
      await open(resume);
      api.segments(item.Id).then((list) => { segments = list; });
      if (item.Type === 'Episode') {
        api.nextEpisode(item).then((n) => { next = n; nextButton.hidden = !n; mediaSession(); }).catch(() => {});
      }
    } catch (error) {
      fail(error.message);
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

  addEventListener('keydown', keys);
  document.addEventListener('visibilitychange', onHide);
  addEventListener('pagehide', onPageHide);
  begin();

  return {
    el, title: 'Lecture',
    leave() { if (started) stop(); },
    dispose() {
      disposed = true;
      clearInterval(progressTimer); clearInterval(countdownTimer); clearTimeout(hideTimer);
      removeEventListener('keydown', keys);
      document.removeEventListener('visibilitychange', onHide);
      removeEventListener('pagehide', onPageHide);
      for (const [name, fn] of Object.entries(on)) video.removeEventListener(name, fn);
      hls?.destroy(); hls = null;
      video.pause();
      for (const t of [...video.querySelectorAll('track')]) t.remove();
      video.removeAttribute('src');
      video.load();
      video.remove();
      if ('mediaSession' in navigator) navigator.mediaSession.metadata = null;
    },
  };
}
