// wiki.miracinema.app: every page is complete without this script. With it: search over the whole wiki (search.json,
// fetched the first time the field is used, from this site only), the pages folded on a phone, a Copy button on each
// command, and what is on the page following the reading.
const body = document.body;
const narrow = matchMedia('(max-width: 1000px)');

// The pages: unfolded beside the text, folded on a phone (open by default for a browser without this script).
const pages = document.querySelector('.pages');
if (pages) {
  const fit = () => { pages.open = !narrow.matches; };
  fit();
  narrow.addEventListener('change', fit);
}

// Search: every word typed must be somewhere in a section (accents and case ignored); a match in the page's name or
// the section's heading comes first.
const form = document.querySelector('.find');
if (form && 'fetch' in window) {
  form.hidden = false;
  const input = form.querySelector('input');
  const list = form.querySelector('.found');
  const nothing = form.querySelector('.nothing');
  const plain = text => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  const escape = text => text.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  let entries = null;
  let loading = null;
  let active = -1;
  const load = () => loading ??= fetch(body.dataset.search).then(r => r.json()).then(data => {
    entries = data.map(e => ({ ...e, key: plain(`${e.p} ${e.s} ${e.t}`), head: plain(`${e.p} ${e.s}`) }));
  }).catch(() => { loading = null; });
  // The words around the first match, the matches in bold.
  const bit = (text, words) => {
    const flat = plain(text);
    let at = Math.max(0, Math.min(...words.map(w => flat.indexOf(w)).filter(i => i >= 0)) - 40);
    if (at > 0) at = text.indexOf(' ', at) + 1;
    let piece = (at > 0 ? '…' : '') + text.slice(at, at + 160) + (text.length > at + 160 ? '…' : '');
    piece = escape(piece);
    for (const word of words) {
      const pattern = new RegExp(word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').split('').map(c => `${c}[̀-ͯ]?`).join(''), 'gi');
      piece = piece.replace(pattern, match => `<mark>${match}</mark>`);
    }
    return piece;
  };
  const choose = index => {
    const items = [...list.children];
    active = items.length ? (index + items.length) % items.length : -1;
    items.forEach((item, i) => item.setAttribute('aria-selected', String(i === active)));
    if (active >= 0) {
      input.setAttribute('aria-activedescendant', items[active].id);
      items[active].scrollIntoView({ block: 'nearest' });
    } else input.removeAttribute('aria-activedescendant');
  };
  const close = () => {
    list.hidden = true;
    input.setAttribute('aria-expanded', 'false');
    input.removeAttribute('aria-activedescendant');
    active = -1;
  };
  const show = async () => {
    const query = input.value.trim();
    nothing.textContent = '';
    if (query.length < 2) { list.replaceChildren(); close(); return; }
    await load();
    if (!entries || input.value.trim() !== query) return;
    const words = plain(query).split(/\s+/).filter(Boolean);
    const found = entries.filter(e => words.every(w => e.key.includes(w)))
      .map(e => ({ e, score: words.filter(w => e.head.includes(w)).length * 2 + (e.s ? 0 : 1) }))
      .sort((a, b) => b.score - a.score).slice(0, 8);
    list.innerHTML = found.map(({ e }, i) => `<li role="option" id="found-${i}" aria-selected="false"><a href="${new URL(e.u, new URL(body.dataset.search, location.href)).href}" tabindex="-1"><span class="where">${escape(e.p)}</span><span class="what">${escape(e.s || e.p)}</span><span class="bit">${bit(e.t, words)}</span></a></li>`).join('');
    if (!found.length) {
      close();
      nothing.textContent = `${body.dataset.none} « ${query} »`;
      return;
    }
    list.hidden = false;
    input.setAttribute('aria-expanded', 'true');
    choose(0);
  };
  input.addEventListener('focus', load, { once: true });
  input.addEventListener('input', show);
  input.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      if (list.hidden) return;
      event.preventDefault();
      choose(active + (event.key === 'ArrowDown' ? 1 : -1));
    } else if (event.key === 'Escape') {
      if (!list.hidden) close();
      else { input.value = ''; nothing.textContent = ''; input.blur(); }
    }
  });
  form.addEventListener('submit', event => {
    event.preventDefault();
    const link = list.children[Math.max(active, 0)]?.querySelector('a');
    if (link && !list.hidden) location.href = link.href;
  });
  list.addEventListener('mousedown', event => event.preventDefault());
  form.addEventListener('focusout', () => setTimeout(() => { if (!form.contains(document.activeElement)) close(); }));
  // "/" from anywhere on the page, as on GitHub.
  document.addEventListener('keydown', event => {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.target.closest('input, textarea, [contenteditable]')) return;
    event.preventDefault();
    if (narrow.matches) input.scrollIntoView({ block: 'center' });
    input.focus();
  });
}

// Copy on each command.
if (navigator.clipboard) {
  for (const block of document.querySelectorAll('.code')) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'copy';
    button.textContent = body.dataset.copy;
    button.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(block.querySelector('code').textContent);
        button.textContent = body.dataset.copied;
        button.dataset.done = '';
        setTimeout(() => { button.textContent = body.dataset.copy; delete button.dataset.done; }, 1600);
      } catch { /* the text stays there to select */ }
    });
    block.append(button);
  }
}

// What is on the page: the section being read, lit.
const toc = [...document.querySelectorAll('.toc a')];
const sections = toc.map(a => document.getElementById(decodeURIComponent(a.hash.slice(1)))).filter(Boolean);
if (sections.length) {
  let queued = false;
  const light = () => {
    queued = false;
    let current = sections[0];
    for (const section of sections) if (section.getBoundingClientRect().top < innerHeight * 0.3) current = section;
    for (const a of toc) {
      if (a.hash === `#${current.id}`) a.setAttribute('aria-current', 'true');
      else a.removeAttribute('aria-current');
    }
  };
  addEventListener('scroll', () => { if (!queued) { queued = true; requestAnimationFrame(light); } }, { passive: true });
  light();
}
