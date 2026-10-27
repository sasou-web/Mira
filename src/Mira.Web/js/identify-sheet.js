// « Identifier… » and « Actualiser les métadonnées », for an administrator, from the page of a film or series
// (views/detail.js) and from « Titres à corriger » (views/fix.js), as Mira for Windows and Mac do them: Jellyfin
// searches its metadata sources (TMDb…) and gives the title the one chosen. Their posters come through Mira's
// extension: the phone never speaks to TMDb.
import { api } from './api.js';
import { h, icon } from './dom.js';
import { tr } from './i18n.js';
import { sheet, toast } from './components.js';
import { prefill, searchQuery, matchYear, matchLine, named } from './identify.js';

/** Shown at most: Jellyfin's sources give up to twenty, the right one among the first. */
const SHOWN = 12;

/** The « … » of a title's page: Identifier…, Actualiser les métadonnées. `changed(full)`: the page draws it again. */
export function titleToolsSheet(item, changed) {
  sheet({
    title: named(item),
    items: [
      { symbol: 'search', label: tr('Identifier…'), sub: tr('Jellyfin s’est trompé de titre, ou ne l’a pas reconnu : choisis le bon.'), run: () => identifySheet(item, changed) },
      { symbol: 'refresh', label: tr('Actualiser les métadonnées'), sub: tr('Jellyfin relit ce titre auprès de ses sources et complète ce qui manque (affiche, résumé, distribution).'), run: () => refresh(item, changed) },
    ],
  });
}

async function refresh(item, changed) {
  try {
    await api.refreshMetadata(item.Id);
    toast(tr('Jellyfin relit « {name} » : la fiche se met à jour dans un moment.', { name: item.Name }));
    // Queued by Jellyfin: a film takes a few seconds, a series longer.
    setTimeout(() => changed?.(false), 6000);
    setTimeout(() => changed?.(false), 20000);
  } catch (error) { toast(error.status === 403 ? tr('Seul un administrateur de Jellyfin peut faire ça.') : error.message); }
}

/**
 * The search, prefilled with what the title is called (prefill), its proposals with their posters, then the one
 * chosen, asked once more: Jellyfin replaces the title's metadata and pictures with it.
 */
export function identifySheet(item, changed) {
  const start = prefill(item);
  const name = h('input', {
    class: 'input', type: 'search', enterkeyhint: 'search', autocomplete: 'off', spellcheck: 'false', value: start.name,
    'aria-label': tr('Titre à chercher, ou lien TMDb ou IMDb'), placeholder: tr('Titre, ou lien TMDb ou IMDb'),
  });
  const year = h('input', { class: 'input identify-year', type: 'text', inputmode: 'numeric', maxlength: 4, autocomplete: 'off', value: start.year ?? '', 'aria-label': tr('Année'), placeholder: tr('Année') });
  const find = h('button', { class: 'btn primary', type: 'submit' }, icon('search', { size: 18 }), tr('Chercher'));
  const form = h('form', { class: 'identify-form', on: { submit: (e) => { e.preventDefault(); search(); } } }, name, year, find);
  const status = h('p', { class: 'group-note', role: 'status' });
  const results = h('div', { class: 'identify-results', role: 'list', 'aria-label': tr('Propositions de Jellyfin') });
  let version = 0, applying = false, posters = new AbortController();

  async function search() {
    if (applying) return;
    const query = searchQuery(name.value, year.value);
    if (!query.name && !Object.keys(query.providerIds).length) { status.textContent = tr('Écris le titre à chercher.'); name.focus(); return; }
    const mine = ++version;
    posters.abort(); posters = new AbortController();
    results.replaceChildren(); status.textContent = tr('Jellyfin cherche…'); find.disabled = true;
    try {
      const found = (await api.findMatches(item, query)).slice(0, SHOWN);
      if (mine !== version) return;
      status.textContent = found.length ? '' : tr('Rien trouvé. Essaie le titre d’origine, sans l’année, ou colle le lien de sa page TMDb.');
      results.replaceChildren(...found.map(proposal));
    } catch (error) {
      if (mine === version) status.textContent = error.status === 403 ? tr('Seul un administrateur de Jellyfin peut faire ça.') : error.message;
    } finally { if (mine === version) find.disabled = false; }
  }

  function proposal(match) {
    const art = h('div', { class: 'art poster identify-poster' }, h('span', { class: 'initials', text: '' }));
    if (match.ImageUrl) {
      api.matchImage(match.ImageUrl, posters.signal).then((address) => {
        if (!address) return;
        const img = h('img', { alt: '', class: 'ready instant' });
        img.addEventListener('load', () => URL.revokeObjectURL(address), { once: true });
        img.addEventListener('error', () => URL.revokeObjectURL(address), { once: true });
        img.src = address;
        art.replaceChildren(img);
      });
    }
    return h('button', { class: 'identify-match', role: 'listitem', on: { click: () => confirm(match) } },
      art,
      h('span', { class: 'grow' },
        h('span', { class: 'identify-name' }, match.Name),
        matchLine(match) ? h('span', { class: 'sub' }, matchLine(match)) : null,
        match.Overview ? h('span', { class: 'identify-overview clamp-2' }, match.Overview) : null));
  }

  function confirm(match) {
    if (applying) return;
    const chosen = matchYear(match) ? `${match.Name} (${matchYear(match)})` : match.Name;
    const shown = [...results.children];
    const back = () => { results.replaceChildren(...shown); status.textContent = ''; };
    const apply = h('button', { class: 'btn primary', on: { click: () => run(match, chosen, apply) } }, tr('Remplacer'));
    status.textContent = '';
    results.replaceChildren(h('div', { class: 'identify-confirm' },
      h('p', { class: 'sheet-question' }, tr('« {title} » devient « {chosen} » ?', { title: named(item), chosen })),
      h('p', { class: 'group-note' }, tr('Jellyfin remplace son titre, son résumé, ses affiches et sa distribution par ceux de ce titre, sur chaque appareil. Le fichier ne bouge pas.')),
      h('div', { class: 'identify-actions' }, apply, h('button', { class: 'btn', on: { click: back } }, tr('Retour')))));
    apply.focus({ preventScroll: true });
  }

  async function run(match, chosen, button) {
    if (applying) return;
    applying = true; button.disabled = true;
    status.textContent = tr('Jellyfin prend les informations de « {chosen} »…', { chosen });
    try {
      await api.applyMatch(item.Id, match);
      close();
      toast(tr('« {chosen} » : Jellyfin a mis à jour sa fiche.', { chosen }));
      changed?.(true);
    } catch (error) {
      status.textContent = error.status === 403 ? tr('Seul un administrateur de Jellyfin peut faire ça.') : error.message;
      button.disabled = false;
    } finally { applying = false; }
  }

  const close = sheet({
    title: tr('Identifier « {name} »', { name: named(item) }),
    body: h('div', { class: 'identify' },
      h('div', { class: 'sheet-form' },
        h('p', { class: 'group-note' }, tr('C’est Jellyfin qui cherche, auprès de ses sources (TMDb…) : ton appareil ne leur parle pas. Un lien vers la page TMDb ou IMDb du titre tombe juste.')),
        form, status),
      results),
  });
  search();
}
