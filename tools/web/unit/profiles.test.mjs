// « Profils » (src/Mira.Web/js/profiles.js), without a browser: as Friends of Mira.Core makes a friend's account.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { newPassword, ALPHABET, cleanName, nameProblem, choices, chosenFolders, profilePolicy, profileLine, joined } from '../../../src/Mira.Web/js/profiles.js';

const folders = [
  { Name: 'Films', CollectionType: 'movies', ItemId: 'f1' },
  { Name: 'Séries', CollectionType: 'tvshows', ItemId: 's1' },
  { Name: 'Collections', CollectionType: 'boxsets', ItemId: 'b1' },
  { Name: 'Listes de lecture', CollectionType: 'playlists', ItemId: 'p1' },
  { Name: 'Sans id', CollectionType: 'movies' },
];

test('a password of four groups of four, from the easy alphabet only', () => {
  for (let i = 0; i < 50; i++) assert.match(newPassword(), new RegExp(`^[${ALPHABET}]{4}(-[${ALPHABET}]{4}){3}$`));
  assert.notEqual(newPassword(), newPassword());
});

test('a name as Jellyfin takes it', () => {
  assert.equal(cleanName('  Marc l’ami '), "Marc l'ami");
  assert.equal(nameProblem('Léa'), null);
  assert.equal(nameProblem("Jean-Luc O'Neil"), null);
  assert.equal(nameProblem('   '), 'Écris son prénom.');
  assert.match(nameProblem('Léa!'), /que des lettres/);
  assert.match(nameProblem('léa', ['Léa']), /existe déjà/);
});

test('the libraries given: those chosen, with Collections and Playlists; none chosen, none', () => {
  assert.deepEqual(choices(folders).map((x) => x.ItemId), ['f1', 's1']);
  assert.deepEqual(chosenFolders(folders, ['S1']), ['s1', 'b1', 'p1']);
  assert.deepEqual(chosenFolders(folders, []), []);
});

test('a new account\'s policy made a profile\'s, the rest kept as Jellyfin set it', () => {
  const policy = profilePolicy({ IsAdministrator: true, EnableAllFolders: true, EnableContentDownloading: true, EnableSyncTranscoding: true, EnableMediaConversion: true, SyncPlayAccess: 'CreateAndJoinGroups', AuthenticationProviderId: 'x' }, ['f1']);
  assert.deepEqual(policy, { IsAdministrator: false, EnableAllFolders: false, EnabledFolders: ['f1'], EnableContentDownloading: false, EnableSyncTranscoding: false, EnableMediaConversion: false, SyncPlayAccess: 'CreateAndJoinGroups', AuthenticationProviderId: 'x' });
});

test('what an account sees, under its name', () => {
  assert.equal(profileLine({ Policy: { IsAdministrator: true } }, folders), 'Administrateur');
  assert.equal(profileLine({ Policy: { EnableAllFolders: true } }, folders), 'Toutes les bibliothèques');
  assert.equal(profileLine({ Policy: { EnabledFolders: ['f1', 's1', 'b1'] } }, folders), 'Films et Séries');
  assert.equal(profileLine({ Policy: { EnabledFolders: [] } }, folders), 'Aucune bibliothèque');
  assert.equal(joined(['Animes', 'Films', 'Séries']), 'Animes, Films et Séries');
});
