// « Titres à corriger », opened from Réglages by an administrator: the films and series Jellyfin did not recognize,
// without a poster or a year, or there twice (identify.js, as Mira for Windows lists them). Each leads to its page, and
// « Identifier » fixes it from here. Read when the screen opens, and again after a title is identified.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { errorState, spinner, titleHref } from '../components.js';
import { goBack } from '../app.js';
import { tr, trn } from '../i18n.js';
import { artFor, picture } from '../images.js';
import { checkTitles, entryText, named } from '../identify.js';
import { identifySheet } from '../identify-sheet.js';

export function create() {
  const el = h('div', { class: 'view fix-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture de ta bibliothèque…'));
  const body = h('div', {}, spinner());
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '16px' } }, h('h1', { class: 'page-title' }, tr('Titres à corriger')), status),
    body);
  let version = 0, loaded = false;

  async function load() {
    const mine = ++version;
    try {
      const entries = checkTitles(await api.titlesToCheck());
      if (mine !== version) return;
      loaded = true;
      render(entries);
    } catch (error) {
      if (mine !== version) return;
      status.textContent = '';
      body.replaceChildren(errorState(error, () => { status.textContent = tr('Lecture de ta bibliothèque…'); load(); }));
    }
  }

  function render(entries) {
    const unknown = entries.filter((x) => x.problems.includes('Unidentified')).length;
    status.textContent = entries.length === 0 ? tr('Rien à corriger : Jellyfin a reconnu chaque film et chaque série, avec leur affiche.')
      : unknown ? trn(unknown, '{n} titre non reconnu par Jellyfin, sur {total} à revoir.', '{n} titres non reconnus par Jellyfin, sur {total} à revoir.', { total: entries.length })
        : trn(entries.length, '{n} titre à revoir.', '{n} titres à revoir.');
    if (!entries.length) { body.replaceChildren(); return; }
    body.replaceChildren(
      h('div', { class: 'group fix-list' }, entries.map((entry) => h('div', { class: 'item static fix-item', dataset: { id: entry.item.Id } },
        h('a', { class: 'fix-title', href: titleHref(entry.item) },
          picture(artFor(entry.item, 'poster'), { kind: 'poster', width: 80 }),
          h('span', { class: 'grow' }, h('span', { class: 'fix-name' }, named(entry.item)), h('span', { class: 'sub' }, entryText(entry)))),
        h('button', {
          class: 'btn small', 'aria-label': tr('Identifier « {name} »', { name: named(entry.item) }),
          on: { click: () => identifySheet(entry.item, () => load()) },
        }, tr('Identifier'))))),
      h('p', { class: 'group-note' }, tr('« Non reconnu » : Jellyfin n’a trouvé ce titre sur aucune de ses sources (TMDb…). Souvent un nom de dossier abîmé, ou un titre romaji. « Identifier » te laisse choisir le bon ; un titre en double est peut-être rangé deux fois.')));
  }

  load();
  return {
    el, title: tr('Titres à corriger'), keep: true,
    // Back from a title's page, where it may have been identified.
    enter() { if (loaded) load(); },
    refresh: () => load(),
  };
}
