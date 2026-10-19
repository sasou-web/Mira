// The sleep timer (« Minuterie »), as Mira for Windows and Mac have it (SleepTimer.cs): a pause after some minutes of
// the clock, or a stop at the end of the episode rather than the next one. Chosen on a title page (Apple's player has
// no room for Mira's menus) or in Mira's player; it carries on into the next episode, and goes once it has done its
// work or the player closes. Nothing of the browser here: Node imports this module for its tests.
import { tr } from '../i18n.js';

export const SLEEP_MINUTES = [15, 30, 60];

/** « 15 min », « 1 h », « 1 h 30 ». */
export const sleepDuration = (minutes) =>
  (minutes < 60 ? tr('{m} min', { m: minutes }) : minutes % 60 === 0 ? tr('{h} h', { h: minutes / 60 })
    : tr('{h} h {mm}', { h: Math.floor(minutes / 60), mm: String(minutes % 60).padStart(2, '0') }));

/** A timer: `chosen` is the menu's choice (minutes, 0 for the end of the episode, null when off). */
export function sleepTimer() {
  let chosen = null, until = 0;
  return {
    get chosen() { return chosen; },
    /** The episode playing is the last: the next one neither starts nor is offered. */
    get atEnd() { return chosen === 0; },
    choose(minutes, now) { chosen = minutes ?? null; until = minutes > 0 ? now + minutes * 60_000 : 0; },
    clear() { chosen = null; until = 0; },
    /** True once, when the minutes are over: the player pauses, and the timer is off. */
    due(now) {
      if (!until || now < until) return false;
      chosen = null; until = 0;
      return true;
    },
    /** « Pause dans 23 min », « Arrêt à la fin de l’épisode »; null when off. */
    status(now) {
      if (chosen === 0) return tr('Arrêt à la fin de l’épisode');
      return until ? tr('Pause dans {time}', { time: sleepDuration(Math.max(1, Math.ceil((until - now) / 60_000))) }) : null;
    },
  };
}

/** The one timer of the page: set on a title page, read by the player that title opens. */
export const sleep = sleepTimer();

/** The choices of a sheet, the end of the episode only for an episode or a series. */
export function sleepChoices(timer, { episode, now }) {
  return [
    { label: tr('Désactivée'), minutes: null },
    ...SLEEP_MINUTES.map((m) => ({ label: sleepDuration(m), minutes: m })),
    ...(episode ? [{ label: tr('Fin de l’épisode'), minutes: 0 }] : []),
  ].map((c) => ({ ...c, selected: timer.chosen === c.minutes, sub: timer.chosen === c.minutes && c.minutes ? timer.status(now) : undefined }));
}
