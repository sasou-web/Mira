// « Télécharger sur le PC serveur », opened from Réglages (or from Recherche) by an administrator of Jellyfin: a search
// or a magnet link goes to the relay of the extension (Mira/torlink), Mira for Windows on the PC of Jellyfin runs it
// with its TorLink and places the title in the library. What it has under way is read every 3 s while the screen
// shows, and only then; a search, every second until it answers.
import { api } from '../api.js';
import { h, icon, clear, plural } from '../dom.js';
import { sheet, toast, errorState } from '../components.js';
import { goBack } from '../app.js';
import { CATEGORIES, cleanQuery, infoHash, agentLine, hitLine, requestLine, downloadLine, controllable } from '../torlink.js';
import { groupReleases, parseRelease, qualityLine, warning } from '../releases.js';

const EVERY = 3000, SEARCH_EVERY = 1000, SEARCH_WAIT = 90_000;
const ACTIONS = { pause: 'Pause demandée au PC serveur…', resume: 'Reprise demandée au PC serveur…', delete: 'Annulation demandée au PC serveur…' };

/** Mira web's words for what the relay refuses: an account that is not an administrator, an extension without it. */
function explain(error) {
  if (error?.status === 403) return 'Seuls les comptes administrateurs de Jellyfin peuvent télécharger sur le PC serveur.';
  if (error?.status === 404) return 'L’extension Mira de ton serveur ne connaît pas encore cet écran : mets à jour Mira sur le PC serveur, puis redémarre Jellyfin.';
  return error?.message ?? 'Une erreur inattendue est survenue.';
}

export function create({ query }) {
  const el = h('div', { class: 'view torlink-page' });
  const back = h('button', { class: 'round floating-back', 'aria-label': 'Retour', on: { click: () => goBack('#/reglages') } }, icon('back'));
  const status = h('p', { class: 'page-sub', role: 'status' }, 'Lecture du PC serveur…');

  let category = CATEGORIES.some(([key]) => key === query?.get('type')) ? query.get('type') : 'movies';
  const input = h('input', {
    class: 'input', type: 'search', placeholder: 'Un film, une série, un anime…', autocomplete: 'off', autocorrect: 'off', autocapitalize: 'none',
    spellcheck: false, enterkeyhint: 'search', maxlength: 200, 'aria-label': 'Que veux-tu télécharger ?', value: query?.get('q') ?? '',
  });
  const clearButton = h('button', { class: 'round flat clear', type: 'button', 'aria-label': 'Effacer', hidden: !input.value, on: { click: () => { input.value = ''; clearButton.hidden = true; input.focus(); } } }, icon('close', { size: 20 }));
  input.addEventListener('input', () => { clearButton.hidden = !input.value; });
  const kinds = h('div', { class: 'segmented', role: 'radiogroup', 'aria-label': 'Catégorie' },
    CATEGORIES.map(([key, label]) => h('button', {
      type: 'button', role: 'radio', 'aria-checked': String(key === category), dataset: { key },
      on: { click: () => { category = key; for (const b of kinds.children) b.setAttribute('aria-checked', String(b.dataset.key === key)); } },
    }, label)));
  const go = h('button', { class: 'btn primary small', type: 'submit' }, 'Chercher');
  const form = h('form', { class: 'torlink-search', role: 'search', on: { submit: (e) => { e.preventDefault(); input.blur(); search(); } } },
    h('div', { class: 'search-box' }, icon('search'), input, clearButton),
    h('div', { class: 'torlink-kinds' }, kinds, go));
  const paste = h('button', { class: 'btn quiet small torlink-paste', type: 'button', on: { click: () => askMagnet() } }, icon('link', { size: 16 }), 'Coller un lien magnet');
  const found = h('div', { class: 'torlink-results', 'aria-live': 'polite' });
  const listTitle = h('h2', { class: 'label group-title', hidden: true }, 'Sur le PC serveur');
  const list = h('div', { class: 'group torlink-list', hidden: true });
  const placedTitle = h('h2', { class: 'label group-title', hidden: true }, 'Rangés dans ta bibliothèque');
  const placedList = h('div', { class: 'group torlink-list', hidden: true });
  const note = h('p', { class: 'group-note torlink-note' },
    'TorLink télécharge sur le PC où tourne Jellyfin, sans que tu aies à y toucher, puis Mira range le titre dans ta bibliothèque. Mira doit y être ouvert, même réduit. Seuls les comptes administrateurs de Jellyfin voient cet écran.');
  // What the PC has under way comes before the results: a download just asked for shows at once, not 60 results below.
  const body = h('div', {}, h('div', { class: 'torlink-tools' }, form, paste), listTitle, list, found, placedTitle, placedList, note);
  clear(el).append(back,
    h('div', { class: 'page', style: { paddingTop: 'calc(var(--top) + 64px)', paddingBottom: '12px' } }, h('h1', { class: 'page-title' }, 'Télécharger'), status),
    body);

  let timer = 0, version = 0, failed = false, active = false, agent = null, ready = false, filled = false;
  let looking = null; // the search under way: { id, query, category, started, timer }
  let lastHits = null; // the last results shown, with their search
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
      clear(el.lastChild).append(errorState({ unreachable: error?.unreachable, message: explain(error) }, () => { say('Lecture du PC serveur…'); load(); }));
    }
  }
  const bodyParts = [...body.childNodes];

  function render(state) {
    filled = true;
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
      ...asked.map((x) => ({ key: `r:${x.id}`, name: x.name, line: requestLine(x), failed: x.state === 'failed', request: x })),
      ...latest.downloads.map((x) => {
        const pending = latest.controls.find((c) => c.target === x.id);
        return { key: `d:${x.id}`, name: x.name, line: pending ? ACTIONS[pending.action] ?? downloadLine(x) : downloadLine(x), progress: x.progress, download: x };
      }),
    ];
    update(list, listTitle, rows, row);
    update(placedList, placedTitle, latest.placed.map((x) => ({ key: `p:${x.id}`, name: x.title, line: x.status, placed: x })), placedRow);
  }
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
    return [h('span', { class: 'torlink-name clamp-2' }, data.name), h('span', { class: ['sub torlink-line', data.failed && 'torlink-failed'] }, data.line)];
  }

  function row(data) {
    const { request, download } = data;
    const bar = download ? h('span', { class: 'torlink-bar', 'aria-hidden': 'true' }, h('i', { style: { width: `${Math.max(0, Math.min(100, download.progress ?? 0))}%` } })) : null;
    const action = request && request.state !== 'claimed'
      ? h('button', { class: 'round flat', 'aria-label': `${request.state === 'waiting' ? 'Annuler' : 'Retirer'} « ${data.name} »`, on: { click: () => cancel(request) } }, icon('close', { size: 20 }))
      : download && controllable(download)
        ? h('button', { class: 'round flat', 'aria-label': `Actions pour « ${data.name} »`, on: { click: () => actions(download) } }, icon('more', { size: 20 }))
        : null;
    return h('div', { class: 'item static torlink-item' }, icon(download ? 'download' : request?.state === 'failed' ? 'warning' : 'clock', { size: 20 }),
      h('span', { class: 'grow' }, ...texts(data), bar), action);
  }

  function placedRow(data) {
    const { placed } = data;
    const inside = [icon(placed.ok ? 'check' : 'warning', { size: 20 }), h('span', { class: 'grow' }, ...texts(data))];
    return placed.itemId
      ? h('a', { class: 'item torlink-item', href: `#/titre/${placed.itemId}`, 'aria-label': `${data.name}, ${data.line}. Ouvrir la fiche` }, ...inside, icon('right', { size: 20 }))
      : h('div', { class: 'item static torlink-item' }, ...inside);
  }

  async function cancel(request) {
    try {
      await api.torlinkCancel(request.id);
      toast(request.state === 'waiting' ? 'Demande annulée.' : 'Retiré de la liste.');
    } catch (error) {
      toast(error.status === 409 ? 'Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.' : error.status === 404 ? 'Cette demande n’est plus là.' : explain(error));
    }
    load();
  }

  function actions(download) {
    const paused = download.status === 'paused' || download.status === 'stopped';
    sheet({
      title: download.name,
      items: [
        download.status === 'failed' ? null
          : paused ? { label: 'Reprendre', symbol: 'play', run: () => control(download, 'resume') }
            : { label: 'Mettre en pause', symbol: 'pause', run: () => control(download, 'pause') },
        { label: 'Annuler le téléchargement', sub: 'TorLink l’arrête et supprime ce qu’il en a déjà sur le PC serveur.', symbol: 'close', danger: true, run: () => control(download, 'delete') },
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
    const kind = category;
    const label = CATEGORIES.find(([key]) => key === kind)?.[1] ?? '';
    looking = { id: null, query: text, category: kind, started: Date.now(), timer: 0 };
    go.disabled = true;
    found.classList.add('pending');
    if (!found.firstChild || !lastHits) clear(found).append(h('p', { class: 'torlink-searching', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), `Recherche de « ${text} » en ${label} sur le PC serveur…`));
    try {
      const { id } = await api.torlinkSearch(text, kind);
      if (!looking || looking.query !== text) return;
      looking.id = id;
      follow();
    } catch (error) {
      if (!looking || looking.query !== text) return;
      done();
      clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, explain(error)));
    }
  }

  function follow() {
    const current = looking;
    if (!current) return;
    current.timer = setTimeout(async () => {
      if (looking !== current) return;
      if (Date.now() - current.started > SEARCH_WAIT) {
        done();
        clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, 'Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche.'));
        return;
      }
      try {
        const state = await api.torlinkSearchState(current.id);
        if (looking !== current) return;
        if (state?.state === 'done') { done(); showHits(current, state.results ?? []); return; }
        if (state?.state === 'failed') { done(); clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, state.error || 'La recherche n’a pas abouti.')); return; }
      } catch (error) {
        if (looking !== current) return;
        // Forgotten (Jellyfin restarted meanwhile): it will not come back.
        if (error.status === 404) { done(); clear(found).append(h('p', { class: 'torlink-searching torlink-failed', role: 'alert' }, 'Cette recherche a été perdue : relance-la.')); return; }
      }
      follow();
    }, SEARCH_EVERY);
  }

  function done() {
    stopLooking();
    found.classList.remove('pending');
    go.disabled = !ready;
  }
  function stopLooking() { if (looking) clearTimeout(looking.timer); looking = null; }

  /**
   * The results by title and episode (or season, or film), the best release of each first (releases.js): its
   * picture and source, its size and sources, its full name; the other releases folded under it.
   */
  function showHits(current, hits) {
    lastHits = { search: current.id, hits };
    const label = CATEGORIES.find(([key]) => key === current.category)?.[1] ?? '';
    if (!hits.length) {
      clear(found).append(h('p', { class: 'torlink-searching', role: 'status' }, `Aucun résultat pour « ${current.query} » en ${label}. Essaie un autre titre, ou une autre catégorie.`));
      return;
    }
    const groups = groupReleases(hits, current.category);
    clear(found).append(
      h('h2', { class: 'label group-title' }, `${plural(hits.length, 'version', 'versions')} pour « ${current.query} »`),
      ...groups.map((group) => {
        const others = h('div', { class: 'torlink-others', hidden: true }, group.others.map((entry) => release(current.id, entry, false)));
        const more = group.others.length ? h('button', {
          class: 'item torlink-more', type: 'button', 'aria-expanded': 'false',
          on: { click: () => { const open = others.hidden; others.hidden = !open; more.setAttribute('aria-expanded', String(open)); more.querySelector('.grow').textContent = open ? 'Masquer les autres versions' : `${plural(group.others.length, 'autre version', 'autres versions')}`; } },
        }, h('span', { class: 'grow' }, plural(group.others.length, 'autre version', 'autres versions')), icon('down', { size: 18 })) : null;
        return h('section', { class: 'group torlink-title', 'aria-label': [group.title, group.label].filter(Boolean).join(' · ') },
          h('h3', { class: 'torlink-head' }, h('span', { class: 'clamp-2' }, group.title), group.label ? h('span', { class: 'torlink-episode' }, group.label) : null),
          release(current.id, group.best, true), more, others);
      }));
  }

  function release(searchId, entry, best) {
    const { hit, release: parsed } = entry;
    const caution = warning(entry);
    return h('button', { class: ['item torlink-item torlink-release', best && 'torlink-best'], type: 'button', on: { click: () => confirm(searchId, hit) } },
      h('span', { class: 'grow' },
        h('span', { class: 'torlink-quality' }, qualityLine(parsed) || 'Qualité non précisée', best ? h('span', { class: 'torlink-badge' }, 'Meilleur choix') : null),
        h('span', { class: 'sub torlink-line' }, hitLine(hit)),
        h('span', { class: 'sub torlink-full clamp-2' }, hit.name),
        caution ? h('span', { class: 'sub torlink-caution' }, caution) : null),
      icon('download', { size: 20 }));
  }

  function confirm(searchId, hit) {
    const where = agent?.name ? `sur ${agent.name}` : 'sur le PC serveur';
    sheet({
      title: 'Télécharger sur le PC serveur ?',
      body: h('div', { class: 'sheet-form' }, h('p', { class: 'torlink-confirm' }, hit.name),
        h('p', { class: 'group-note' }, `${[qualityLine(parseRelease(hit.name)), hitLine(hit)].filter(Boolean).join(' · ')}. TorLink le télécharge ${where}, puis Mira le range dans ta bibliothèque. Tu peux l’annuler ici tant qu’il n’est pas terminé.`)),
      items: [{ label: 'Télécharger', symbol: 'download', run: () => send({ search: searchId, result: hit.id, name: hit.name, size: hit.size }) }],
    });
  }

  async function send(body) {
    try {
      await api.torlinkRequest(body);
      toast(ready ? 'Envoyé au PC serveur.' : 'Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.');
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
      autocapitalize: 'none', spellcheck: false, enterkeyhint: 'send', 'aria-label': 'Lien magnet', 'aria-describedby': 'torlink-magnet-note',
    });
    const errorBox = h('p', { class: 'form-error', role: 'alert' });
    const button = h('button', { class: 'btn primary block', type: 'submit' }, 'Envoyer au PC serveur');
    let close = () => {};
    const box = h('form', {
      class: 'sheet-form',
      on: { submit: async (e) => {
        e.preventDefault();
        if (!infoHash(field.value)) { errorBox.textContent = 'Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).'; field.focus(); return; }
        button.disabled = true; errorBox.textContent = '';
        if (await send({ magnet: field.value.trim() })) close();
        else button.disabled = false;
      } },
    }, field,
    h('p', { class: 'group-note', id: 'torlink-magnet-note' }, 'Colle le lien d’un site que tu connais : TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque.'),
    errorBox, button);
    close = sheet({ title: 'Coller un lien magnet', body: box, focus: field });
  }

  // Only while the screen shows and the phone is in front: Jellyfin is not asked for nothing.
  const watch = () => {
    clearInterval(timer); timer = 0;
    if (active && document.visibilityState === 'visible') { timer = setInterval(load, EVERY); load(); }
  };
  document.addEventListener('visibilitychange', watch);
  let started = false;
  return {
    el, title: 'Télécharger', keep: true,
    async enter({ query: next } = {}) {
      active = true; watch();
      // Opened from Recherche with a title: searched at once, once the PC is known to be there.
      const asked = next?.get('q');
      if (asked && (!started || asked !== input.value)) { input.value = asked; clearButton.hidden = false; await load(); if (ready) search(); }
      started = true;
    },
    leave() { active = false; watch(); },
    refresh: () => load(),
    dispose() { active = false; stopLooking(); watch(); document.removeEventListener('visibilitychange', watch); },
  };
}
