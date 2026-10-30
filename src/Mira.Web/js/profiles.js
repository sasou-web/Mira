// « Profils » (views/profiles.js, administrators): an account of their own on this Jellyfin, as « Ajouter un ami » of
// Mira for Windows and Mac makes it (Friends of Mira.Core): the libraries chosen and nothing else, no download, no
// administration. Jellyfin makes a new account see every library and download (checked on 12.1): its policy is read,
// changed here and sent back whole. Nothing of the browser at load: tools/web/unit/profiles.test.mjs.
import { tr } from './i18n.js';

/** Letters and digits easy to read out and to type on a phone: no 0 or o, no 1, l or i. */
export const ALPHABET = 'abcdefghjkmnpqrstuvwxyz23456789';
/**
 * A password of four groups of four (« k7mq-t3vx-9hcw-p2ra », about 79 bits), never shown again; never empty:
 * Jellyfin lets in an account without a password to anyone who knows its name.
 */
export function newPassword(random = (n) => crypto.getRandomValues(new Uint32Array(n))) {
  const groups = [];
  for (let g = 0; g < 4; g++) {
    let part = '';
    // Values past the last whole run of the alphabet are drawn again: each letter as likely as the others.
    const limit = Math.floor(2 ** 32 / ALPHABET.length) * ALPHABET.length;
    while (part.length < 4) for (const value of random(4)) if (value < limit && part.length < 4) part += ALPHABET[value % ALPHABET.length];
    groups.push(part);
  }
  return groups.join('-');
}
/** A name as typed, as sent: spaces around it dropped, a typographic apostrophe made Jellyfin's. */
export const cleanName = (name) => String(name ?? '').trim().replaceAll('’', "'");
// Jellyfin's own rule for a name (checked on 12.1): letters of any language, digits, spaces and - _ ' . @ +, no space
// first or last; and no two accounts with the same name, whatever the case.
const NAME_RULE = /^(?!\s)[\p{L}\p{M}\p{N}_ \-'.@+]+(?<!\s)$/u;
/** What is wrong with a profile's name, in words; null when Jellyfin takes it. */
export function nameProblem(name, taken = []) {
  const trimmed = cleanName(name);
  if (!trimmed) return tr('Écris son prénom.');
  if (!NAME_RULE.test(trimmed)) return tr('Son prénom ne peut contenir que des lettres, des chiffres, des espaces et ces signes : - _ \' . @ +');
  return taken.some((x) => String(x).toLocaleLowerCase() === trimmed.toLocaleLowerCase()) ? tr('Un compte « {name} » existe déjà sur ton Jellyfin.', { name: trimmed }) : null;
}
const groups = (type) => type === 'boxsets' || type === 'playlists';
/** The libraries a profile may be given, in the server's order: Collections and Playlists come with them. */
export const choices = (folders) => folders.filter((x) => x?.ItemId && !groups(x.CollectionType));
/** The folders a profile sees: those chosen, and Jellyfin's Collections and Playlists. None chosen, none at all. */
export function chosenFolders(folders, chosen) {
  const all = folders.filter((x) => x?.ItemId);
  const picked = new Set([...chosen].map((x) => String(x).toLowerCase()));
  const mine = all.filter((x) => !groups(x.CollectionType) && picked.has(x.ItemId.toLowerCase())).map((x) => x.ItemId);
  return mine.length ? [...mine, ...all.filter((x) => groups(x.CollectionType)).map((x) => x.ItemId)] : [];
}
/** A new account's policy, made a profile's: these folders only, no download (nor its conversions), no administration. */
export function profilePolicy(policy, folders) {
  return {
    ...policy, IsAdministrator: false, EnableAllFolders: false, EnabledFolders: [...folders],
    EnableContentDownloading: false, EnableSyncTranscoding: false, EnableMediaConversion: false,
  };
}
/** What an account sees, said under its name: « Administrateur », « Toutes les bibliothèques », or those it has. */
export function profileLine(user, folders) {
  const policy = user?.Policy ?? {};
  if (policy.IsAdministrator) return tr('Administrateur');
  if (policy.IsDisabled) return tr('Désactivé');
  if (policy.EnableAllFolders) return tr('Toutes les bibliothèques');
  const enabled = new Set((policy.EnabledFolders ?? []).map((x) => String(x).toLowerCase()));
  const names = choices(folders).filter((x) => enabled.has(x.ItemId.toLowerCase())).map((x) => x.Name);
  return names.length ? joined(names) : tr('Aucune bibliothèque');
}
/** Libraries named as a sentence says them: « Films », « Films et Séries », « Animes, Films et Séries ». */
export const joined = (names) => (names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')}${tr(' et ')}${names.at(-1)}`);
