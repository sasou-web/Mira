// « Télécharger sur le PC serveur » of Mira web (src/Mira.Web/js/torlink.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import {
  kindLabel, cleanQuery, infoHash, displayName, cleanName, size, speed, agentLine, hitLine, requestLine, downloadLine, controllable, remaining, timeLeft,
} from '../../../src/Mira.Web/js/torlink.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/torlink-remote.json', import.meta.url), 'utf8'));

test('magnet links, searches and names checked as Mira.Core checks them (tests/fixtures/torlink-remote.json)', () => {
  for (const [link, expected] of fixture.magnets) assert.equal(infoHash(link), expected, `info hash of ${JSON.stringify(link)}`);
  for (const [typed, expected] of fixture.queries) assert.equal(cleanQuery(typed), expected, `search ${JSON.stringify(typed)}`);
  for (const [link, expected] of fixture.names) assert.equal(displayName(link), expected, `name of ${JSON.stringify(link)}`);
  assert.equal(infoHash(`magnet:?xt=urn:btih:${'a'.repeat(40)}&tr=${'x'.repeat(8192)}`), null, 'too long');
  assert.equal(infoHash(null), null);
  assert.equal(cleanQuery(42), null);
  assert.equal(cleanName('x'.repeat(301)), `${'x'.repeat(300)}…`);
  assert.deepEqual(['movies', 'tv', 'anime', 'games', undefined].map(kindLabel), ['Film', 'Série', 'Anime', '', ''], 'never games');
});

test('sizes and speeds as Mira for Windows writes them', () => {
  assert.equal(size(0), '1 Mo');
  assert.equal(size(850 * 2 ** 20), '850 Mo');
  assert.equal(size(4.2 * 2 ** 30), '4,2 Go');
  assert.equal(size(12.4 * 2 ** 30), '12 Go');
  assert.equal(speed(0), '');
  assert.equal(speed(350 * 1024), '350 Ko/s');
  assert.equal(speed(2.44 * 2 ** 20), '2,4 Mo/s');
  assert.equal(speed(12 * 2 ** 20), '12 Mo/s');
});

test('what the screen says of the PC, a result, a request and a download', () => {
  const now = Date.parse('2026-10-15T12:00:00Z');
  assert.equal(agentLine(null).ready, false);
  assert.match(agentLine(null).text, /pas encore été ouvert/);
  const away = agentLine({ name: 'DESKTOP-INVEFQH', online: false, seen: '2026-10-15T09:00:00Z', accepting: false }, now);
  assert.equal(away.ready, false);
  assert.equal(away.text, 'Mira n’est pas ouvert sur DESKTOP-INVEFQH (vu il y a 3 h) : tes demandes l’attendent, il les fera à son retour.');
  assert.deepEqual(agentLine({ name: 'PC', online: true, accepting: false, problem: 'TorLink est à l’écran sur le PC serveur : tes demandes partent dès qu’on quitte sa page.' }, now),
    { text: 'TorLink est à l’écran sur le PC serveur : tes demandes partent dès qu’on quitte sa page.', ready: true });
  assert.equal(agentLine({ name: 'PC', online: true, accepting: true }, now).text, 'PC est prêt : TorLink y télécharge, puis Mira range le titre dans ta bibliothèque.');

  assert.equal(hitLine({ size: 1.4 * 2 ** 30, seeders: 120, source: 'YTS' }), '1,4 Go · 120 sources · YTS');
  assert.equal(hitLine({ size: 700 * 2 ** 20, seeders: 0, source: '' }), '700 Mo · aucune source');
  assert.equal(hitLine({ size: 700 * 2 ** 20, seeders: 0, source: 'subsplease' }), '700 Mo · subsplease', 'SubsPlease counts no source');

  assert.equal(requestLine({ state: 'waiting' }), 'En attente du PC serveur');
  assert.equal(requestLine({ state: 'failed', error: 'Ce n’est pas un lien magnet.' }), 'Ce n’est pas un lien magnet.');
  assert.equal(requestLine({ state: 'failed' }), 'TorLink a refusé ce téléchargement.');

  assert.equal(downloadLine({ status: 'downloading', progress: 42.4, speed: 2.44 * 2 ** 20, peers: 12 }), '42 % · 2,4 Mo/s · 12 pairs');
  assert.equal(downloadLine({ status: 'downloading', progress: 0, speed: 0, peers: 0 }), '0 % · recherche de pairs…');
  assert.equal(downloadLine({ status: 'downloading', progress: 0, speed: 0, peers: 3, size: 2.9 * 2 ** 30 }), '0 % · 2,9 Go · 3 pairs, en attente de données');
  // TorLink's guess first, then what is left at this speed (an agent of 0.7.9 sends neither size nor guess).
  assert.equal(downloadLine({ status: 'downloading', progress: 41, speed: 2 * 2 ** 20, peers: 12, size: 2.9 * 2 ** 30, downloaded: 1.2 * 2 ** 30, eta: 750 }),
    '41 % · 1,2 Go sur 2,9 Go · 2,0 Mo/s · reste 13 min · 12 pairs');
  assert.equal(downloadLine({ status: 'downloading', progress: 50, speed: 2 ** 20, peers: 1, size: 2 * 2 ** 30, downloaded: 2 ** 30 }), '50 % · 1,0 Go sur 2,0 Go · 1,0 Mo/s · reste 17 min · 1 pair');
  assert.equal(downloadLine({ status: 'paused', progress: 10 }), 'En pause · 10 %');
  assert.equal(downloadLine({ status: 'paused', progress: 10, size: 850 * 2 ** 20, downloaded: 85 * 2 ** 20, eta: 99 }), 'En pause · 10 % · 85 Mo sur 850 Mo');
  assert.equal(downloadLine({ status: 'stopped', progress: 10 }), 'Arrêté · 10 % · reprend quand TorLink rouvre sur le PC serveur');
  assert.equal(downloadLine({ status: 'completed', progress: 100 }), 'Terminé · rangement dans ta bibliothèque…');
  assert.equal(timeLeft({ status: 'paused', eta: 60 }), null);
  assert.equal(timeLeft({ status: 'downloading', speed: 10 }), null, 'size unknown');
  assert.deepEqual([30, 59.9, 60, 720, 3599, 3600, 3900, 86399, 86400, 3 * 86400].map(remaining),
    ['moins d’une minute', 'moins d’une minute', '1 min', '12 min', '1 h 00', '1 h 00', '1 h 05', '1 jour', '1 jour', '3 jours']);
  assert.ok(controllable({ status: 'queued' }) && controllable({ status: 'paused' }) && !controllable({ status: 'completed' }));
});

test('the lines Mira for Windows and Mac write too (tests/fixtures/torlink-lines.json)', () => {
  const lines = JSON.parse(readFileSync(new URL('../../../tests/fixtures/torlink-lines.json', import.meta.url), 'utf8'));
  const now = Date.parse(lines.now);
  for (const [bytes, text] of lines.sizes) assert.equal(size(bytes), text, `size ${bytes}`);
  for (const [rate, text] of lines.speeds) assert.equal(speed(rate), text, `speed ${rate}`);
  for (const { agent, text, ready } of lines.agents) assert.deepEqual(agentLine(agent, now), { text, ready }, `agent ${JSON.stringify(agent)}`);
  for (const [hit, text] of lines.hits) assert.equal(hitLine(hit), text, `hit ${JSON.stringify(hit)}`);
  for (const [request, text] of lines.requests) assert.equal(requestLine(request), text, `request ${request.state}`);
  for (const [download, text] of lines.downloads) assert.equal(downloadLine(download), text, `download ${JSON.stringify(download)}`);
  for (const [seconds, text] of lines.remaining) assert.equal(remaining(seconds), text, `remaining ${seconds}`);
});

test('the same lines in English', () => {
  try {
    setLanguage('en');
    const now = Date.parse('2026-10-15T12:00:00Z');
    assert.equal(size(4.2 * 2 ** 30), '4.2 GB');
    assert.equal(size(850 * 2 ** 20), '850 MB');
    assert.equal(speed(2.44 * 2 ** 20), '2.4 MB/s');
    assert.equal(agentLine({ name: 'DESKTOP-INVEFQH', online: false, seen: '2026-10-15T09:00:00Z' }, now).text,
      'Mira isn’t open on DESKTOP-INVEFQH (seen 3h ago). Your requests will wait for it and go through when it’s back.');
    assert.equal(hitLine({ size: 1.4 * 2 ** 30, seeders: 1, source: 'nyaa' }), '1.4 GB · 1 seeder · nyaa');
    assert.equal(hitLine({ size: 700 * 2 ** 20, seeders: 0, source: '' }), '700 MB · no seeders');
    assert.equal(requestLine({ state: 'sent', error: 'x' }), 'Sent to TorLink: x');
    assert.equal(downloadLine({ status: 'downloading', progress: 41, speed: 2 * 2 ** 20, peers: 12, size: 2.9 * 2 ** 30, downloaded: 1.2 * 2 ** 30, eta: 750 }),
      '41% · 1.2 GB of 2.9 GB · 2.0 MB/s · 13 min left · 12 peers');
    assert.equal(downloadLine({ status: 'downloading', progress: 0, speed: 0, peers: 1 }), '0% · 1 peer, waiting for data');
    assert.equal(downloadLine({ status: 'paused', progress: 10 }), 'Paused · 10%');
    assert.deepEqual([30, 720, 3900, 86400, 3 * 86400].map(remaining), ['less than a minute', '12 min', '1h 05m', '1 day', '3 days']);
  } finally { setLanguage('fr'); }
});
