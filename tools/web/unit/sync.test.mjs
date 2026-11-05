// « Regarder ensemble » in Mira web (src/Mira.Web/js/player/sync.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { readFileSync } from 'node:fs';
import { serverTime, clockOffset, commandPlan, needsSeek, queueItem, groupName, groupText, inviteLink, inviteSteps, appLink, groupHost, findGroup, rejoinPlan, waitingText, namesText, groupStatus, memberText, asksToReplace, titleName, replaceQuestion, groupPlayLabel } from '../../../src/Mira.Web/js/player/sync.js';

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

test('what the group is doing, in words: Jellyfin never says who a wait or a pause is for', () => {
  for (const [names, text] of fixture.names) assert.equal(namesText(names), text);
  for (const [group, me, loading, status] of fixture.statuses) assert.deepEqual(groupStatus(group, me, loading), status, `${group.State} · ${me}`);
  assert.equal(groupStatus(null, 'mira'), null);
  assert.equal(memberText('Marc', true), 'Marc a rejoint le groupe');
  assert.equal(memberText('Marc', false), 'Marc a quitté le groupe');
});

test('Lecture in a group: what its button says, and when it asks before replacing the title the group has on', () => {
  for (const [group, queue, itemId, asks] of fixture.replaces) assert.equal(asksToReplace(group, queue, itemId), asks, `${group?.State} · ${queue?.itemId} → ${itemId}`);
  for (const [item, name] of fixture.titles) assert.equal(titleName(item), name);
  for (const [options, label] of fixture.playLabels) assert.equal(groupPlayLabel(options), label);
  for (const [current, question] of fixture.questions) assert.equal(replaceQuestion(current), question);
});

test('the invitation: its link and what it says under it, from Tailscale, home or the Internet', () => {
  for (const [server, group, host, link, steps] of fixture.invites) {
    assert.equal(inviteLink(server, group, host), link);
    assert.deepEqual(inviteSteps(link), steps);
  }
});

test('« Ouvrir dans Mira »: the same invitation for Mira on a computer (read back by Together.ParseAppLink)', () => {
  for (const [server, group, host, link] of fixture.appLinks) assert.equal(appLink(server, group, host), link);
});

test('a link that stays valid: the group of its id, or the one its host made again since, else none', () => {
  for (const [name, host] of fixture.hosts) assert.equal(groupHost(name), host, name);
  for (const [groups, id, host, found] of fixture.finds) assert.equal(findGroup(groups, id, host)?.GroupId ?? null, found, `${id} · ${host}`);
  for (const [groups, lost, made, plan] of fixture.rejoins) assert.deepEqual(rejoinPlan(groups, lost, made), plan, `${lost.GroupName} · ${made}`);
  assert.equal(findGroup(null, 'x', 'mira'), null);
  assert.equal(waitingText('mira'), 'mira n’a pas encore lancé la soirée');
});

test('the group\'s words in English, and its name read back in either language', () => {
  try {
    setLanguage('en');
    assert.equal(groupText({ GroupName: 'Groupe de mira', Participants: ['mira'] }), 'mira’s group · 1 person');
    assert.equal(groupText({ GroupName: 'Soirée', Participants: ['mira', 'léa', 'sam'] }), 'Soirée · 3 people');
    assert.equal(groupName('mira'), 'mira’s group');
    assert.equal(groupHost(groupName('Marc D')), 'Marc D');
    assert.equal(groupHost('Marc\'s group'), 'Marc');
    assert.equal(groupText({ GroupName: 'mira’s group', Participants: ['mira'] }), 'mira’s group · 1 person');
    // An invitation from a French device finds the group an English one made again, and the other way round.
    const made = [{ GroupId: 'b', GroupName: 'mira’s group', Participants: ['mira'] }];
    assert.equal(findGroup(made, 'a', groupHost('Groupe de mira'))?.GroupId, 'b');
    assert.equal(findGroup([{ GroupId: 'c', GroupName: 'Groupe de mira', Participants: [] }], 'a', groupHost(groupName('mira')))?.GroupId, 'c');
    assert.equal(namesText(['mira', 'Marc', 'Léa']), 'mira, Marc and Léa');
    assert.equal(namesText(['mira', 'Marc', 'Léa', 'sam', 'Zoé']), 'mira, Marc and 3 others');
    const group = { GroupName: 'Groupe de mira', Participants: ['mira', 'Marc'] };
    assert.deepEqual(groupStatus({ ...group, State: 'Waiting' }, 'mira'), { text: 'Waiting for Marc…', alert: true });
    assert.deepEqual(groupStatus({ ...group, State: 'Waiting' }, 'mira', true), { text: 'Loading for the group…', alert: true });
    assert.deepEqual(groupStatus({ ...group, State: 'Paused' }, 'mira'), { text: 'Paused for the group', alert: true });
    assert.deepEqual(groupStatus({ ...group, State: 'Playing' }, 'mira'), { text: 'Together · mira and Marc', alert: false });
    assert.equal(memberText('Marc', true), 'Marc joined the group');
    assert.equal(groupPlayLabel({}), 'Start for the group');
    assert.equal(groupPlayLabel({ resumeAt: 2520 }), 'Resume for the group at 42 min');
    assert.equal(groupPlayLabel({ code: 'S1 · E3', resumeAt: 600 }), 'Resume S1 · E3 for the group');
    assert.equal(titleName({ Type: 'Episode', SeriesName: 'Dark', ParentIndexNumber: 1, IndexNumber: 3 }), 'Dark · S1 · E3');
    assert.equal(replaceQuestion('Dune'), 'Replace “Dune” for the whole group?');
    assert.equal(waitingText('mira'), 'mira hasn’t started the watch party yet');
    setLanguage('fr');
    assert.equal(groupText({ GroupName: 'mira’s group', Participants: ['mira'] }), 'Groupe de mira · 1 personne');
    setLanguage('en');
    assert.match(inviteSteps('http://192.168.1.20:8096/Mira/#/ensemble/x')[2], /^This link only works on your Wi-Fi\./);
  } finally { setLanguage('fr'); }
});
