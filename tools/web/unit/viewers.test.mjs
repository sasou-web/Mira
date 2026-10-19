// « En ce moment sur Jellyfin » of Mira web (src/Mira.Web/js/viewers.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseViewers, who, what, how, place, clock, reason } from '../../../src/Mira.Web/js/viewers.js';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/viewers.json', import.meta.url), 'utf8'));

test('each session as Mira for Windows writes it (tests/fixtures/viewers.json)', () => {
  const viewers = parseViewers(fixture.sessions, fixture.ownDevice);
  assert.deepEqual(viewers.map((x) => x.sessionId), fixture.viewers.map((x) => x.id), 'only the sessions playing something');
  viewers.forEach((viewer, i) => {
    const expected = fixture.viewers[i];
    // Windows calls its own session « ce PC »; Mira web, « cet appareil ».
    assert.equal(who(viewer, 'ce PC'), expected.who, `${expected.id}: who`);
    assert.equal(what(viewer), expected.what, `${expected.id}: what`);
    assert.deepEqual(how(viewer), { how: expected.how, why: expected.why }, `${expected.id}: how`);
    assert.equal(viewer.canStop, expected.canStop, `${expected.id}: can stop`);
  });
  assert.equal(who(viewers.find((x) => x.own)), 'cet appareil');
  assert.deepEqual(parseViewers(null, 'x'), []);
  assert.deepEqual(parseViewers(fixture.sessions, '').filter((x) => x.own), []);
});

test('where a device is, and the times', () => {
  for (const [address, expected] of fixture.places) assert.equal(place(address), expected, `place of ${address}`);
  for (const [seconds, expected] of fixture.clocks) assert.equal(clock(seconds), expected, `clock of ${seconds}`);
  assert.equal(clock(Number.NaN), '0:00');
  assert.equal(reason('HDR'), 'HDR');
  assert.equal(reason('toString'), 'toString');
});

test('the same lines in English', () => {
  try {
    setLanguage('en');
    const viewers = parseViewers(fixture.sessions, fixture.ownDevice);
    const byId = (id) => viewers.find((x) => x.sessionId === id);
    assert.equal(who(byId('s-phone')), 'mira · iPhone · home network');
    assert.equal(who(byId('s-pc')), 'this device');
    assert.deepEqual(how(byId('s-phone')), {
      how: 'Converted: video to VP9, audio to Opus, on the processor · 1.6 Mbps', why: 'Why: file format, video codec, audio codec',
    });
    assert.equal(what(byId('s-friend')), 'Courte HLS · S1 E2 · Le départ — 1:02:03 / 1:20:00, paused');
    assert.equal(how(byId('s-friend')).how, 'Direct play: the file as it is');
    assert.equal(place('192.168.1.20'), 'home network');
    assert.equal(reason('VideoResolutionNotSupported'), 'resolution');
  } finally { setLanguage('fr'); }
});
