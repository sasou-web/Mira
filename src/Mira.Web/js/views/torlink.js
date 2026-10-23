// « Télécharger sur le PC serveur », opened from Réglages (or from Recherche) by an administrator of Jellyfin: a search
// or a magnet link goes to the relay of the extension (Mira/torlink), Mira for Windows on the PC of Jellyfin runs it
// with its TorLink and places the title in the library. What it has under way is read every 3 s while the screen
// shows, and only then; a search, every second until it answers.
import { api } from '../api.js';
import { h, icon, clear } from '../dom.js';
import { tr, trn } from '../i18n.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { session, temp } from '../session.js';
import { kindLabel, cleanQuery, infoHash, agentLine, hitLine, requestLine, downloadLine, controllable } from '../torlink.js';
import { groupReleases, parseRelease, qualityLine, warning, readableName } from '../releases.js';
import { ownedLookups, ownedLine, lookupKey } from '../owned.js';

const EVERY = 3000, SEARCH_EVERY = 1000, SEARCH_WAIT = 90_000;
// As long as the relay keeps a search and its results (TorLinkRelay.SearchLife).
const SEARCH_KEPT = 15 * 60_000;
const ACTIONS = { pause: tr('Pause demandée au PC serveur…'), resume: tr('Reprise demandée au PC serveur…'), delete: tr('Annulation demandée au PC serveur…') };

/** Mira web's words for what the relay refuses: an account that is not an administrator, an extension without it. */
function explain(error) {
  if (error?.status === 403) return tr('Seuls les comptes administrateurs de Jellyfin peuvent télécharger sur le PC serveur.');
  if (error?.status === 404) return tr('L’extension Mira de ton serveur ne connaît pas encore cet écran : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.');
  return error?.message ?? tr('Une erreur inattendue est survenue.');
}

export function create({ query }) {
  const el = h('div', { class: 'view torlink-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': tr('Retour'), on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, tr('Lecture du PC serveur…'));

  const input = h('input', {
    class: 'input', type: 'search', placeholder: tr('Film, série, anime…'), autocomplete: 'off', autocorrect: 'off', autocapitalize: 'none',
    spellcheck: false, enterkeyhint: 'search', maxlength: 200, 'aria-label': tr('Que veux-tu télécharger ?'), value: query?.get('q') ?? '',
  });
  const clearButton = h('button', { class: 'round flat clear', type: 'button', 'aria-label': tr('Effacer'), hidden: !input.value, on: { click: () => { input.value = ''; clearButton.hidden = true; input.focus(); } } }, icon('close', { size: 20 }));
  input.addEventListener('input', () => { clearButton.hidden = !input.value; });
  const go = h('button', { class: 'btn primary small', type: 'submit' }, tr('Chercher'));
  // Films, shows and anime at once: the relay runs a search of TorLink in each, and says which found each result.
  const form = h('form', { class: 'torlink-search', role: 'search', on: { submit: (e) => { e.preventDefault(); input.blur(); search(); } } },
    h('div', { class: 'search-box' }, icon('search'), input, clearButton), go);
  const paste = h('button', { class: 'btn quiet small torlink-paste', type: 'button', on: { click: () => askMagnet() } }, icon('link', { size: 16 }), tr('Coller un lien magnet'));
  const found = h('div', { class: 'torlink-results', 'aria-live': 'polite' });
  const listTitle = h('h2', { class: 'label group-title', hidden: true }, tr('Sur le PC serveur'));
  const list = h('div', { class: 'group torlink-list', hidden: true });
  const placedTitle = h('h2', { class: 'label group-title', hidden: true }, tr('Rangés dans ta bibliothèque'));
  const placedList = h('div', { class: 'group torlink-list', hidden: true });
  // The latest few only, under what is under way: a title just placed shows without going past the results.
  const placedMore = h('button', { class: 'btn quiet small torlink-all', type: 'button', hidden: true, 'aria-expanded': 'false', on: { click: () => { placedOpen = !placedOpen; render(lastState); } } });
  const note = h('p', { class: 'group-note torlink-note' },
    tr('TorLink télécharge sur le PC où tourne Jellyfin, sans que tu aies à y toucher, puis Mira range le titre dans ta bibliothèque. Mira doit y être ouvert, même réduit. Seuls les comptes administrateurs de Jellyfin voient cet écran.'));
  // What the PC has under way and has placed comes before the results: a download just asked for, or a title just
  // placed, shows at once, not 60 results below.
  const body = h('div', {}, h('div', { class: 'torlink-tools' }, form, paste), listTitle, list, placedTitle, placedList, placedMore, found, note);
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '12px' } }, h('h1', { class: 'page-title' }, tr('Télécharger')), status),
    body);

  let timer = 0, version = 0, failed = false, active = false, agent = null, ready = false, filled = false;
  let looking = null; // the search under way: { id, query, started, timer }
  let lastHits = null; // the last results shown, with their search
  let lastState = null, placedOpen = false;
  const sentHits = new Set(); // `${search}/${result}` sent from this screen, marked on their button
  const PLACED_SHOWN = 3;
  let latest = { requests: [], downloads: [], placed: [], controls: [] };

  // ---- What the PC has under way ----------------------------------------------------------------------

  async function load() {
    const mine = ++version;
    try {
      const state = await api.torlink();
      if (mine !== version) return;
      if (failed) { failed = false; clear(el.lastChild).append(...bodyParts); }
      render(state);
    } catch (error) {
      if (mine !== version) return;
      // A screen already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
      if (filled && error.status !== 403 && error.status !== 404) { say(explain(error)); return; }
      failed = true; say('');
      clear(el.lastChild).append(errorState({ unreachable: error?.unreachable, message: explain(error) }, () => { say(tr('Lecture du PC serveur…')); load(); }));
    }
  }
  const bodyParts = [...body.childNodes];

  function render(state) {
    filled = true; lastState = state;
    agent = state?.agent ?? null;
    const line = agentLine(agent);
    ready = line.ready;
    say(line.text);
    go.disabled = !ready || !!looking;
    latest = {
      requests: state?.requests ?? [], downloads: state?.downloads ?? [], placed: state?.placed ?? [], controls: state?.controls ?? [],
    };
    const inTorLink = new Set(latest.downloads.map((x) => x.id));
    // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
    const asked = latest.requests.filter((x) => !(x.state === 'sent' && inTorLink.has(x.infoHash)));
    const rows = [
      ...asked.map((x) => ({ key: `r:${x.id}`, ...named(x.name), line: requestLine(x), failed: x.state === 'failed', request: x })),
      ...latest.downloads.map((x) => {
        const pending = latest.controls.find((c) => c.target === x.id);
        return { key: `d:${x.id}`, ...named(x.name), line: pending ? ACTIONS[pending.action] ?? downloadLine(x) : downloadLine(x), progress: x.progress, download: x };
      }),
    ];
    update(list, listTitle, rows, row);
    const placed = latest.placed.map((x) => ({ key: `p:${x.id}`, name: x.title, line: x.status, placed: x }));
    update(placedList, placedTitle, placedOpen ? placed : placed.slice(0, PLACED_SHOWN), placedRow);
    placedMore.hidden = placed.length <= PLACED_SHOWN;
    placedMore.setAttribute('aria-expanded', String(placedOpen));
    placedMore.textContent = placedOpen ? tr('Voir moins') : tr('Voir les {n} titres rangés', { n: placed.length });
  }
  /** A release called by the title read from its name, the name itself under it (releases.js: readableName). */
  function named(name) { const { title, full } = readableName(name); return { name: title || name, full }; }

  /** The line under the title, said by screen readers only when it changes. */
  function say(text) { if (status.textContent !== text) status.textContent = text; }

  /**
   * Rows kept by their key, their texts changed in place (the progress, every 3 s): a button under the finger or the
   * keyboard stays where it is. A row is made again when what it offers changes.
   */
  function update(group, title, rows, make) {
    group.hidden = title.hidden = rows.length === 0;
    const old = new Map([...group.children].map((x) => [x.dataset.key, x]));
    const next = rows.map((data) => {
      const shape = JSON.stringify([data.request?.state, data.download && controllable(data.download), data.download?.status, data.placed?.itemId, data.placed?.ok]);
      const kept = old.get(data.key);
      if (kept && kept.dataset.shape === shape) {
        kept.querySelector('.torlink-name').textContent = data.name;
        kept.querySelector('.torlink-line').textContent = data.line;
        kept.querySelector('.torlink-bar i')?.style.setProperty('width', `${Math.max(0, Math.min(100, data.progress ?? 0))}%`);
        return kept;
      }
      const made = make(data);
      made.dataset.key = data.key; made.dataset.shape = shape;
      return made;
    });
    if (next.length !== group.children.length || next.some((x, i) => group.children[i] !== x)) group.replaceChildren(...next);
  }

  function texts(data) {
    return [h('span', { class: 'torlink-name clamp-2' }, data.name), h('span', { class: ['sub torlink-line', data.failed && 'torlink-failed'] }, data.line),
      data.full ? h('span', { class: 'sub torlink-full torlink-raw clamp-2' }, data.full) : null];
  }

  function row(data) {
    const { request, download } = data;
    const bar = download ? h('span', { class: 'torlink-bar', 'aria-hidden': 'true' }, h('i', { style: { width: `${Math.max(0, Math.min(100, download.progress ?? 0))}%` } })) : null;
    const action = request && request.state !== 'claimed'
      ? h('button', { class: 'round flat', 'aria-label': request.state === 'waiting' ? tr('Annuler « {name} »', { name: data.name }) : tr('Retirer « {name} »', { name: data.name }), on: { click: () => cancel(request) } }, icon('close', { size: 20 }))
      : download && controllable(download)
        ? h('button', { class: 'round flat', 'aria-label': tr('Actions pour « {name} »', { name: data.name }), on: { click: () => actions(download) } }, icon('more', { size: 20 }))
        : null;
    return h('div', { class: 'item static torlink-item' }, icon(download ? 'download' : request?.state === 'failed' ? 'warning' : 'clock', { size: 20 }),
      h('span', { class: 'grow' }, ...texts(data), bar), action);
  }

  function placedRow(data) {
    const { placed } = data;
    const inside = [icon(placed.ok ? 'check' : 'warning', { size: 20 }), h('span', { class: 'grow' }, ...texts(data))];
    return placed.itemId
      ? h('a', { class: 'item torlink-item', href: `#/titre/${placed.itemId}`, 'aria-label': tr('{name}, {line}. Ouvrir la fiche', { name: data.name, line: data.line }) }, ...inside, icon('right', { size: 20 }))
      : h('div', { class: 'item static torlink-item' }, ...inside);
  }

  async function cancel(request) {
    try {
      await api.torlinkCancel(request.id);
      toast(request.state === 'waiting' ? tr('Demande annulée.') : tr('Retiré de la liste.'));
    } catch (error) {
      toast(error.status === 409 ? tr('Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.') : error.status === 404 ? tr('Cette demande n’est plus là.') : explain(error));
    }
    load();
  }

  function actions(download) {
    const paused = download.status === 'paused' || download.status === 'stopped';
    sheet({
      title: readableName(download.name).title || download.name,
      items: [
        download.status === 'failed' ? null
          : paused ? { label: tr('Reprendre'), symbol: 'play', run: () => control(download, 'resume') }
            : { label: tr('Mettre en pause'), symbol: 'pause', run: () => control(download, 'pause') },
        { label: tr('Annuler le téléchargement'), sub: tr('TorLink l’arrête et supprime ce qu’il en a déjà sur le PC serveur.'), symbol: 'close', danger: true, run: () => control(download, 'delete') },
      ].filter(Boolean),
    });
  }

  async function control(download, action) {
    try {
      await api.torlinkControl(download.id, action);
      toast(ACTIONS[action]);
    } catch (error) { toast(explain(error)); }
    load();
  }

  // ---- Searching -----------------------------------------------------------------------------------------

  async function search() {
    const text = cleanQuery(input.value);
    if (!text) { input.focus(); return; }
    if (!ready) { say(agentLine(agent).text); return; }
    stopLooking();
    looking = { id: null, query: text, started: Date.now(), timer: 0 };
    waiting(text);
    try {
      const { id } = await api.torlinkSearch(text, 'all');
      if (!looking || looking.query !== text) return;
      looking.id = id;
      keep(looking);
      follow();
    } catch (error) {
      if (!looking || looking.query !== text) return;
      done();
      clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, explain(error)));
    }
  }

  /**
   * A search under way shows, whatever the screen had: the spinner and its words on top, what was there before dimmed
   * under it until the answer replaces it (an old message goes at once: it would answer the new search).
   */
  function waiting(text) {
    go.disabled = true;
    found.classList.add('pending');
    found.setAttribute('aria-busy', 'true');
    for (const x of found.querySelectorAll(':scope > .torlink-searching')) x.remove();
    found.prepend(h('p', { class: 'torlink-searching', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), tr('Recherche de « {query} » dans les films, les séries et les animes sur le PC serveur…', { query: text })));
  }

  function follow(delay = SEARCH_EVERY) {
    const current = looking;
    if (!current) return;
    current.timer = setTimeout(async () => {
      if (looking !== current) return;
      try {
        const state = await api.torlinkSearchState(current.id);
        if (looking !== current) return;
        if (state?.state === 'done') { done(); showHits(current, state.results ?? []); return; }
        if (state?.state === 'failed') { done(); clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, state.error || tr('La recherche n’a pas abouti.'))); return; }
      } catch (error) {
        if (looking !== current) return;
        // Forgotten (Jellyfin restarted meanwhile, or kept past its time): it will not come back. One found again after
        // a reload just goes, its words left in the field.
        if (error.status === 404) {
          done(); forget();
          if (current.restored) clear(found);
          else clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, tr('Cette recherche a été perdue : relance-la.')));
          return;
        }
      }
      if (Date.now() - current.started > SEARCH_WAIT) {
        done();
        clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, tr('Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche.')));
        return;
      }
      follow();
    }, delay);
  }

  function done() {
    stopLooking();
    found.classList.remove('pending');
    found.removeAttribute('aria-busy');
    go.disabled = !ready;
  }

  // The last search of this visit, for as long as the relay keeps it: iOS often reloads a Home Screen app left in the
  // background, and the screen comes back with it rather than empty. Kept for the account that made it.
  const savedKey = () => `mira.torlink.search.${session.current?.userId ?? ''}`;
  function keep(current) {
    const sent = [...sentHits].filter((x) => x.startsWith(`${current.id}/`)).map((x) => x.slice(current.id.length + 1));
    temp.set(savedKey(), JSON.stringify({ query: current.query, id: current.id, started: current.started, sent }));
  }
  function forget() { temp.remove(savedKey()); }
  function saved() {
    try {
      const value = JSON.parse(temp.get(savedKey()) ?? 'null');
      return typeof value?.query === 'string' && typeof value.id === 'string' && Date.now() - value.started < SEARCH_KEPT ? value : null;
    } catch { return null; }
  }
  /** The kept search asked again of the relay: its results show as they were, or it goes on waiting for them. */
  function restore(value) {
    input.value = value.query; clearButton.hidden = false;
    for (const x of Array.isArray(value.sent) ? value.sent : []) sentHits.add(`${value.id}/${x}`);
    looking = { id: value.id, query: value.query, started: value.started, timer: 0, restored: true };
    waiting(value.query);
    follow(0);
  }
  function stopLooking() { if (looking) clearTimeout(looking.timer); looking = null; }

  /**
   * The results by title and episode (or season, or film), the best release of each first (releases.js): its
   * picture and source, its size and sources, its full name; the other releases folded under it.
   */
  function showHits(current, hits) {
    lastHits = { search: current.id, query: current.query, started: current.started, hits };
    if (!hits.length) {
      clear(found).append(h('p', { class: 'torlink-searching', role: 'status' }, tr('Aucun résultat pour « {query} » dans les films, les séries et les animes. Essaie un autre titre.', { query: current.query })));
      return;
    }
    const groups = groupReleases(hits);
    const sections = [];
    clear(found).append(
      h('h2', { class: 'label group-title' }, trn(hits.length, '{n} version pour « {query} »', '{n} versions pour « {query} »', { query: current.query })),
      ...groups.map((group) => {
        const others = h('div', { class: 'torlink-others', hidden: true }, group.others.map((entry) => release(current.id, entry, false)));
        const more = group.others.length ? h('button', {
          class: 'item torlink-more', type: 'button', 'aria-expanded': 'false',
          on: { click: () => { const open = others.hidden; others.hidden = !open; more.setAttribute('aria-expanded', String(open)); more.querySelector('.grow').textContent = open ? tr('Masquer les autres versions') : trn(group.others.length, '{n} autre version', '{n} autres versions'); } },
        }, h('span', { class: 'grow' }, trn(group.others.length, '{n} autre version', '{n} autres versions')), icon('down', { size: 18 })) : null;
        const kind = kindLabel(group.category);
        const section = h('section', { class: 'group torlink-title', 'aria-label': [group.title, group.label, kind].filter(Boolean).join(' · ') },
          h('h3', { class: 'torlink-head' }, h('span', { class: 'clamp-2' }, group.title), group.label ? h('span', { class: 'torlink-episode' }, group.label) : null, kind ? h('span', { class: 'torlink-kind' }, kind) : null),
          release(current.id, group.best, true), more, others);
        sections.push({ group, section });
        return section;
      }));
    markOwned(current, sections);
  }

  /**
   * « Déjà dans ta bibliothèque » under a title Jellyfin has (owned.js), so that it is not downloaded twice: Jellyfin
   * asked once per title found, the episodes of a series once, after the results show.
   */
  async function markOwned(current, sections) {
    const still = () => lastHits?.search === current.id && found.isConnected;
    await Promise.all(ownedLookups(sections.map((x) => x.group)).map(async (lookup) => {
      const mine = sections.filter((x) => lookupKey(x.group) === lookup.key);
      let items = [], episodes = [];
      try {
        items = (await api.owned(lookup.title))?.Items ?? [];
        const series = lookup.film ? null : ownedLine(mine[0].group, items)?.itemId;
        if (series && mine.some((x) => !x.group.release.absolute)) episodes = (await api.ownedEpisodes(series))?.Items ?? [];
      } catch { return; } // Nothing said rather than a guess.
      if (!still()) return;
      for (const { group, section } of mine) {
        const line = ownedLine(group, items, episodes);
        if (!line || section.querySelector('.torlink-owned')) continue;
        section.querySelector('.torlink-head').after(h('a', { class: ['torlink-owned', line.owned && 'is-owned'], href: `#/titre/${line.itemId}` },
          icon(line.owned ? 'check' : 'info', { size: 16 }), h('span', { class: 'grow' }, line.text), icon('right', { size: 16 })));
      }
    }));
  }

  function release(searchId, entry, best) {
    const { hit, release: parsed } = entry;
    const caution = warning(entry);
    const made = h('button', { class: ['item torlink-item torlink-release', best && 'torlink-best'], type: 'button', on: { click: () => confirm(searchId, hit) } },
      h('span', { class: 'grow' },
        h('span', { class: 'torlink-quality' }, qualityLine(parsed) || tr('Qualité non précisée'), best ? h('span', { class: 'torlink-badge' }, tr('Meilleur choix')) : null),
        h('span', { class: 'sub torlink-line' }, hitLine(hit)),
        h('span', { class: 'sub torlink-full clamp-2' }, hit.name),
        caution ? h('span', { class: 'sub torlink-caution' }, caution) : null),
      icon('download', { size: 20 }));
    made.dataset.hit = `${searchId}/${hit.id}`;
    if (sentHits.has(made.dataset.hit)) markSent(made);
    return made;
  }

  /** A result already sent, as Windows and Mac show it: « Envoyé » in place of its button, not sent twice. */
  function markSent(button) {
    if (button.classList.contains('torlink-sent')) return;
    button.classList.add('torlink-sent');
    button.disabled = true;
    button.querySelector(':scope > .icon')?.replaceWith(icon('check', { size: 20 }));
    button.querySelector('.torlink-quality').append(h('span', { class: 'torlink-done' }, tr('Envoyé')));
  }

  function confirm(searchId, hit) {
    const details = [qualityLine(parseRelease(hit.name)), hitLine(hit)].filter(Boolean).join(' · ');
    sheet({
      title: tr('Télécharger sur le PC serveur ?'),
      body: h('div', { class: 'sheet-form' }, h('p', { class: 'torlink-confirm' }, hit.name),
        h('p', { class: 'group-note' }, agent?.name
          ? tr('{details}. TorLink le télécharge sur {name}, puis Mira le range dans ta bibliothèque. Tu peux l’annuler ici tant qu’il n’est pas terminé.', { details, name: agent.name })
          : tr('{details}. TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque. Tu peux l’annuler ici tant qu’il n’est pas terminé.', { details }))),
      items: [{ label: tr('Télécharger'), symbol: 'download', run: () => send({ search: searchId, result: hit.id, name: hit.name, size: hit.size }) }],
    });
  }

  async function send(body) {
    try {
      await api.torlinkRequest(body);
      if (body.result) {
        const key = `${body.search}/${body.result}`;
        sentHits.add(key);
        for (const x of found.querySelectorAll('.torlink-release')) if (x.dataset.hit === key) markSent(x);
        if (looking === null && lastHits?.search === body.search) keep({ query: lastHits.query, id: body.search, started: lastHits.started });
      }
      toast(ready ? tr('Envoyé au PC serveur.') : tr('Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.'));
      await load();
      // Its row, with its progress, is above the results: brought into view when the results hid it.
      if (!listTitle.hidden && listTitle.getBoundingClientRect().top < 0) listTitle.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'start' });
      return true;
    } catch (error) {
      toast(explain(error));
      return false;
    }
  }

  function askMagnet() {
    const field = h('input', {
      class: 'input', id: 'torlink-magnet', type: 'url', placeholder: 'magnet:?xt=urn:btih:…', autocomplete: 'off', autocorrect: 'off',
      autocapitalize: 'none', spellcheck: false, enterkeyhint: 'send', 'aria-label': tr('Lien magnet'), 'aria-describedby': 'torlink-magnet-note',
    });
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, tr('Envoyer au PC serveur'));
    let close = () => {};
    const box = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        if (!infoHash(field.value)) { errorBox.textContent = tr('Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).'); field.focus(); return; }
        button.disabled = true; errorBox.textContent = '';
        if (await send({ magnet: field.value.trim() })) close();
        else button.disabled = false;
      } },
    }, field,
    h('p', { class: 'group-note', id: 'torlink-magnet-note' }, tr('Colle le lien d’un site que tu connais : TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque.')),
    errorBox, button);
    close = sheet({ title: tr('Coller un lien magnet'), body: box, focus: field });
  }

  // Only while the screen shows and the phone is in front: Jellyfin is not asked for nothing.
  // Opened again without a title to look for (or with the same one): the search kept for this visit comes back.
  const keptSearch = saved();
  const restored = keptSearch && (!query?.get('q') || query.get('q') === keptSearch.query) ? keptSearch.query : null;
  if (restored) restore(keptSearch);

  const watch = () => {
    clearInterval(timer); timer = 0;
    if (active && document.visibilityState === 'visible') { timer = setInterval(load, EVERY); load(); }
  };
  document.addEventListener('visibilitychange', watch);
  let started = false;
  return {
    el, title: tr('Télécharger'), keep: true,
    async enter({ query: next } = {}) {
      active = true; watch();
      // Opened from Recherche with a title: searched at once, once the PC is known to be there.
      const asked = next?.get('q');
      if (asked && !(!started && asked === restored) && (!started || asked !== input.value)) { input.value = asked; clearButton.hidden = false; await load(); if (ready) search(); }
      started = true;
    },
    leave() { active = false; watch(); },
    refresh: () => load(),
    dispose() { active = false; stopLooking(); watch(); document.removeEventListener('visibilitychange', watch); },
  };
}
