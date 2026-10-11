// What the web player does when Apple's full screen player opens, closes or goes to Picture in Picture
// (src/Mira.Web/js/player/presentation.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { presentationStep } from '../../../src/Mira.Web/js/player/presentation.js';

const state = { closing: false, failed: false, paused: false, ended: false, videoEnded: false, switching: false };
const step = (was, mode, extra = {}) => presentationStep({ ...state, was, mode, ...extra });

test('Apple’s player opening, or Picture in Picture', () => {
  assert.equal(step('inline', 'fullscreen'), 'fullscreen');
  assert.equal(step('picture-in-picture', 'fullscreen'), 'fullscreen');
  assert.equal(step('fullscreen', 'picture-in-picture'), 'pip');
  assert.equal(step('fullscreen', 'fullscreen'), 'none');
  // Even while Mira closes: what shows under Apple's player follows it.
  assert.equal(step('inline', 'fullscreen', { closing: true }), 'fullscreen');
});

test('closed by the person: Mira closes, its picture landing first from full screen', () => {
  assert.equal(step('fullscreen', 'inline'), 'close-landing');
  assert.equal(step('fullscreen', 'inline', { paused: true }), 'close-landing');
  assert.equal(step('picture-in-picture', 'inline', { paused: true }), 'close');
  // While the next episode loads, the video still says the last one ended: closed all the same.
  assert.equal(step('fullscreen', 'inline', { switching: true, videoEnded: true }), 'close-landing');
});

test('Picture in Picture closed while playing: back to full screen', () => {
  assert.equal(step('picture-in-picture', 'inline'), 'reenter');
});

test('left by Mira itself, or by iOS at the end of a title: nothing', () => {
  assert.equal(step('fullscreen', 'inline', { closing: true }), 'none');
  assert.equal(step('fullscreen', 'inline', { failed: true }), 'none');
  assert.equal(step('picture-in-picture', 'inline', { failed: true }), 'none');
  assert.equal(step('fullscreen', 'inline', { ended: true }), 'none');
  assert.equal(step('fullscreen', 'inline', { videoEnded: true }), 'none');
});
