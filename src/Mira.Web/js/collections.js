// Collections made by hand (« Soirées », « À montrer à Léa »), beside the sagas Jellyfin makes, as Mira for Windows
// and Mac judge them (TitleCollections.cs, cases in tests/fixtures/collections.json). Both are Jellyfin's collections:
// a list of titles kept in its data folder (« Soirées [boxset] »), never a file moved; deleting one leaves its titles.
// Imported by Node too (tools/web/unit): nothing of the browser here.
import { tr } from './i18n.js';

/** The longest name Mira gives a collection. */
export const MAX_NAME = 100;

/** The titles a collection takes from a card's menu or a page: films and series. */
export const holds = (item) => item?.Type === 'Movie' || item?.Type === 'Series';

/** A name as typed, without the spaces around it. */
export const cleanName = (name) => String(name ?? '').trim();

/**
 * What this account may do with collections, from the policy of Users/Me: make, fill, empty and delete them (an
 * administrator, or an account given « Gérer les collections »), rename them (an administrator: Items/{id}).
 */
export function collectionRights(policy) {
  const admin = policy?.IsAdministrator === true;
  return { manage: admin || policy?.EnableCollectionManagement === true, rename: admin };
}

/** The name of a collection's folder without « [boxset] » (the last part of its path, from Windows or not); null without one. */
export function folderOf(path) {
  if (!path || !String(path).trim()) return null;
  const last = String(path).replace(/[/\\]+$/, '').split(/[/\\]/).pop();
  return last.toLowerCase().endsWith(' [boxset]') ? last.slice(0, -' [boxset]'.length) : last;
}

/** What two names that would share a folder have in common: Jellyfin's forbidden characters made spaces, lower case. */
export const nameKey = (name) => [...String(name ?? '').normalize('NFC')]
  .map((c) => (c.charCodeAt(0) < 32 || '"<>|:*?\\/'.includes(c) ? ' ' : c)).join('').toLowerCase();

/**
 * Why a new name is refused ({ problem: empty, long, unusable or taken, taken: the collection that has it }), or null.
 * Jellyfin keeps a collection in a folder named after it, and a second collection of the same folder takes the place
 * of the first, which loses its titles (checked on 12.1): a name is refused when its folder, or its name, is already
 * another collection's, whatever the case. A collection renamed keeps its first folder: its old name stays taken.
 * renamed: the id of the one being renamed, which may keep its own.
 */
export function nameProblem(name, collections, renamed = null) {
  const clean = cleanName(name);
  if (!clean) return { problem: 'empty', taken: null };
  if (clean.length > MAX_NAME) return { problem: 'long', taken: null };
  const key = nameKey(clean);
  if (!key.trim()) return { problem: 'unusable', taken: null };
  for (const other of collections ?? []) {
    if (renamed && String(other.Id).toLowerCase() === String(renamed).toLowerCase()) continue;
    const folder = folderOf(other.Path);
    if (nameKey(other.Name) === key || (folder != null && nameKey(folder) === key)) return { problem: 'taken', taken: other.Name };
  }
  return null;
}

/** What a refused name says. */
export function problemText(refusal) {
  switch (refusal?.problem) {
    case 'empty': return tr('Donne un nom à la collection.');
    case 'long': return tr('Ce nom est trop long : {n} caractères au plus.', { n: MAX_NAME });
    case 'unusable': return tr('Ce nom ne peut pas servir : mets-y des lettres ou des chiffres.');
    default: return tr('Ce nom est déjà pris par la collection « {name} ».', { name: refusal?.taken ?? '' });
  }
}

/** Collections by name, as a menu lists them. */
export const sortedCollections = (collections) => [...(collections ?? [])].sort((a, b) => String(a.Name).localeCompare(String(b.Name), undefined, { sensitivity: 'base' }));
