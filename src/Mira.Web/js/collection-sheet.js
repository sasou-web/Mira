// Collections made by hand (collections.js), as Mira for Windows and Mac make them: « Ajouter à une collection… » from
// the menu of a film or series (each collection ticked when it holds it, a tap takes it out; « Nouvelle collection… »),
// and on a collection's page, « Renommer… » (an administrator), « Supprimer la collection… » and « Retirer de la
// collection ». Only for an account Jellyfin lets manage collections; a collection never moves a file.
import { api } from './api.js';
import { h, icon } from './dom.js';
import { tr, trn, trc } from './i18n.js';
import { sheet, toast } from './components.js';
import { nameProblem, problemText, cleanName, sortedCollections, MAX_NAME } from './collections.js';

/** A collection made, renamed or deleted, a title put in or taken out: Home and the pages that show it read again. */
export const collectionChanges = new EventTarget();
const said = (detail) => collectionChanges.dispatchEvent(new CustomEvent('change', { detail }));

/** Mira web's words for what Jellyfin refused. */
function explain(error) {
  if (error?.status === 403) return tr('Ton compte Jellyfin n’a pas le droit de gérer les collections : son administrateur peut le lui donner (Tableau de bord → Utilisateurs).');
  return error?.message ?? tr('Une erreur inattendue est survenue.');
}

/** The collections of a film or series: a tick on those that hold it (a tap takes it out), then « Nouvelle collection… ». */
export async function collectionSheet(item) {
  let list, holding;
  try { [list, holding] = await Promise.all([api.collections(), api.collectionsOf(item.Id).catch(() => null)]); } catch (error) { toast(explain(error), { tone: 'error' }); return; }
  const items = sortedCollections(list).map((collection) => {
    const inside = holding?.has(collection.Id) ?? false;
    return {
      symbol: 'stack', label: collection.Name, sub: collection.ChildCount ? trn(collection.ChildCount, '{n} titre', '{n} titres') : tr('Vide'),
      selected: holding ? inside : null, checkbox: true, run: () => put(item, collection, !inside),
    };
  });
  items.push({ symbol: 'plus', label: tr('Nouvelle collection…'), run: () => nameSheet({ title: tr('Nouvelle collection'), collections: list, save: tr('Créer'), done: (name) => create(item, name) }) });
  sheet({
    title: tr('Ajouter à une collection'),
    // Jellyfin 10.9 and 10.10 cannot say which collections hold a title: a tap adds it again, harmlessly.
    body: h('p', { class: 'sheet-note' }, holding ? tr('« {name} » va dans les collections cochées : rien n’est copié ni déplacé.', { name: item.Name })
      : tr('Ton Jellyfin ne dit pas dans quelles collections est déjà « {name} » : rien n’est copié ni déplacé.', { name: item.Name })),
    items,
  });
}

async function put(item, collection, inside) {
  try {
    await api.setInCollection(collection.Id, [item.Id], inside);
    said({ collection: collection.Id, item: item.Id });
    toast(inside ? tr('Ajouté à « {name} ».', { name: collection.Name }) : tr('Retiré de « {name} ».', { name: collection.Name }), {
      action: inside ? { label: tr('Voir'), run: () => { location.hash = `#/saga/${collection.Id}`; } } : { label: trc('undo', 'Annuler'), run: () => put(item, collection, true) },
    });
  } catch (error) { toast(explain(error), { tone: 'error' }); }
}

async function create(item, name) {
  try {
    const id = await api.createCollection(name, [item.Id]);
    said({ collection: id, item: item.Id });
    toast(tr('Collection « {name} » créée avec « {title} ».', { name, title: item.Name }), id ? { action: { label: tr('Voir'), run: () => { location.hash = `#/saga/${id}`; } } } : {});
  } catch (error) { toast(explain(error), { tone: 'error' }); }
}

/** « Nouvelle collection » of the collections' page: an empty one, filled from the menus of its titles. */
export async function newCollection() {
  let list;
  try { list = await api.collections(); } catch (error) { toast(explain(error), { tone: 'error' }); return; }
  nameSheet({
    title: tr('Nouvelle collection'), collections: list, save: tr('Créer'),
    done: async (name) => {
      try {
        const id = await api.createCollection(name, []);
        said({ collection: id });
        toast(tr('Collection « {name} » créée. Ajoute-y un film ou une série depuis son menu.', { name }), id ? { action: { label: tr('Voir'), run: () => { location.hash = `#/saga/${id}`; } } } : {});
      } catch (error) { toast(explain(error), { tone: 'error' }); }
    },
  });
}

/**
 * A name typed for a collection, refused at once while it cannot be one (collections.js: a name or folder already
 * taken, which Jellyfin would put in another's place). done(name) once it can.
 */
function nameSheet({ title, collections, value = '', renamed = null, save, done }) {
  const note = h('p', { class: 'group-note', id: 'collection-name-note', 'aria-live': 'polite' }, tr('Ses titres restent où ils sont : une collection ne fait que les rassembler.'));
  const field = h('input', {
    class: 'input', type: 'text', autocomplete: 'off', spellcheck: false, enterkeyhint: 'done', maxlength: MAX_NAME + 20,
    value, 'aria-label': tr('Nom de la collection'), 'aria-describedby': 'collection-name-note', placeholder: tr('Soirées, À montrer à Léa…'),
  });
  let close = () => {};
  const form = h('form', {
    class: 'sheet-form',
    on: { submit: (e) => {
      e.preventDefault();
      const refusal = nameProblem(field.value, collections, renamed);
      if (refusal) { note.textContent = problemText(refusal); note.classList.add('error'); field.focus(); return; }
      close();
      done(cleanName(field.value));
    } },
  }, field, note, h('button', { class: 'btn primary block', type: 'submit' }, icon('check', { size: 18 }), save));
  close = sheet({ title, body: form, focus: field });
}

/**
 * The « … » of a collection's page: « Renommer… » for an administrator, « Supprimer la collection… ». gone(): the page
 * goes back once it is deleted; renamed(name): it says its new name.
 */
export async function collectionActions(collection, { renamed, gone }) {
  const can = await api.collectionRights();
  if (!can.manage) return;
  sheet({
    title: collection.Name,
    items: [
      can.rename ? { symbol: 'pencil', label: tr('Renommer…'), run: () => rename(collection, renamed) } : null,
      { symbol: 'trash', label: tr('Supprimer la collection…'), danger: true, run: () => askDelete(collection, gone) },
    ].filter(Boolean),
  });
}

async function rename(collection, renamed) {
  let list;
  try { list = await api.collections(); } catch (error) { toast(explain(error), { tone: 'error' }); return; }
  nameSheet({
    title: tr('Renommer la collection'), collections: list, value: collection.Name, renamed: collection.Id, save: tr('Renommer'),
    done: async (name) => {
      if (name === collection.Name) return;
      try {
        await api.renameCollection(collection.Id, name);
        said({ collection: collection.Id });
        toast(tr('Collection renommée « {name} ».', { name }), { tone: 'done' });
        renamed?.(name);
      } catch (error) { toast(explain(error), { tone: 'error' }); }
    },
  });
}

function askDelete(collection, gone) {
  sheet({
    title: tr('Supprimer la collection'),
    body: h('p', { class: 'now-confirm' }, tr('Supprimer la collection « {name} » ? Ses titres restent dans ta bibliothèque.', { name: collection.Name })),
    items: [{
      symbol: 'trash', label: tr('Supprimer la collection'), danger: true,
      run: async () => {
        try {
          await api.deleteCollection(collection.Id);
          said({ collection: collection.Id, deleted: true });
          toast(tr('Collection « {name} » supprimée. Ses titres restent dans ta bibliothèque.', { name: collection.Name }));
          gone?.();
        } catch (error) { toast(explain(error), { tone: 'error' }); }
      },
    }],
  });
}

/** « Retirer de la collection », from the menu of a card on its page; « Annuler » puts it back. */
export const takeOut = (item, collection) => put(item, collection, false);
