// « Regarder ensemble » in Mira web (src/Mira.Web/js/player/sync.js), without a browser.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { serverTime, clockOffset, commandPlan, needsSeek, queueItem, groupName, groupText } from '../../../src/Mira.Web/js/player/sync.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/syncplay.json', import.meta.url), 'utf8'));

test('Jellyfin\'s dates, seven decimals included (tests/fixtures/syncplay.json)', () => {
  for (const [iso, ms] of fixture.times) assert.equal(serverTime(iso), ms, iso);
  assert.ok(Number.isNaN(serverTime(undefined)));
});

test('the server\'s clock, from the answer with the shortest round trip', () => {
  for (const [samples, offset, trip] of fixture.clock) {
    const found = clockOffset(samples);
    assert.deepEqual(found && { offset: found.offset, trip: found.trip }, offset == null ? null : { offset, trip });
  }
  assert.equal(clockOffset([]), null);
});

test('where and when a command of the group puts the player', () => {
  for (const [offset, now, command, plan] of fixture.commands) {
    const found = commandPlan(command, { offset, now });
    if (plan == null) { assert.equal(found, null); continue; }
    assert.equal(found.action, plan.action, command.Command);
    assert.equal(found.wait, plan.wait, command.Command);
    assert.ok(Math.abs(found.position - plan.position) < 1e-6, `${command.Command}: ${found.position} ≠ ${plan.position}`);
  }
});

test('the title a queue puts on, and the group\'s words', () => {
  for (const [data, item] of fixture.queue) assert.deepEqual(queueItem(data), item);
  for (const [group, words] of fixture.groups) assert.equal(groupText(group), words);
  assert.equal(groupName('mira'), 'Groupe de mira');
  assert.ok(needsSeek(10, 10.6) && !needsSeek(10, 10.4));
});
