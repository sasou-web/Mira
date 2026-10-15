// « Tes choix gardés sur ton Jellyfin »: the rules Mira for Windows and Mac follow too (tests/fixtures/choices.json).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { capture, merge, differs, apply, same, fromWeb, toWeb, LIMITS } from '../../../src/Mira.Web/js/choices.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/choices.json', import.meta.url), 'utf8'));

test('what changed on this device, with its time (tests/fixtures/choices.json)', () => {
  for (const row of fixture.capture) {
    const got = capture(row.doc, row.local, row.now);
    assert.ok(same(got, row.expected), `${row.name}: ${JSON.stringify(got)}`);
  }
});

test('this device and Jellyfin together, and whether Jellyfin must be told (tests/fixtures/choices.json)', () => {
  for (const row of fixture.merge) {
    const got = merge(row.doc, row.remote);
    assert.ok(same(got, row.expected), `${row.name}: ${JSON.stringify(got)}`);
    assert.equal(differs(got, row.remote), row.differs, row.name);
  }
});

test('what the device uses of it (tests/fixtures/choices.json)', () => {
  for (const row of fixture.apply) {
    const got = apply(row.doc);
    assert.ok(same(got.settings, row.expected.settings) && same(got.tracks, row.expected.tracks) && same(got.hidden, row.expected.hidden), `${row.name}: ${JSON.stringify(got)}`);
    assert.deepEqual(Object.keys(got.tracks), row.expected.order);
  }
});

test('the newest series and titles only, the same on every device', () => {
  const tracks = Object.fromEntries(Array.from({ length: LIMITS.tracks + 5 }, (_, i) => [`s${i}`, { audio: 'jpn', subtitles: false, at: i ? i : 1000 }]));
  const merged = merge({ seen: true }, { tracks });
  assert.equal(Object.keys(merged.tracks).length, LIMITS.tracks);
  // The five oldest go; s0 is the newest.
  for (const gone of ['s1', 's2', 's3', 's4', 's5']) assert.ok(!(gone in merged.tracks), gone);
  assert.ok('s0' in merged.tracks && 's6' in merged.tracks && 's104' in merged.tracks);
  // At the same time, by name: the same series go on every device.
  const tied = merge({}, { hidden: Object.fromEntries(Array.from({ length: LIMITS.hidden + 1 }, (_, i) => [`m${String(i).padStart(3, '0')}`, { hidden: true, at: 5 }])) });
  assert.ok(!(`m${LIMITS.hidden}` in tied.hidden) && 'm000' in tied.hidden);
});

test('Mira web keeps the tracks of a series its own way', () => {
  assert.deepEqual(fromWeb({ audio: 'jpn', subtitle: 'und', forced: false }), { audio: 'jpn', audioTitle: null, subtitles: true, subtitle: null, subtitleTitle: null, forced: false });
  assert.deepEqual(fromWeb({ audio: null, subtitle: null, forced: true }), { audio: null, audioTitle: null, subtitles: false, subtitle: null, subtitleTitle: null, forced: false });
  for (const memory of [{ audio: 'jpn', subtitle: 'fre', forced: true, audioTitle: 'Japonais', subtitleTitle: 'Panneaux' }, { audio: 'fre', subtitle: null, forced: false, audioTitle: null, subtitleTitle: null }, { audio: 'jpn', subtitle: 'und', forced: false, audioTitle: null, subtitleTitle: null }]) {
    assert.deepEqual(toWeb(fromWeb(memory)), memory);
  }
});
