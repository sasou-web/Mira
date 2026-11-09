// The wiki of Mira, wiki.miracinema.app: Markdown pages in wiki/<language>/<page>.md, their order and groups in
// wiki/nav.json, turned into static pages in the style of miracinema.app (its stylesheet, font and icons copied along),
// with nothing loaded from anywhere else. English at the root, French in /fr/, each page under the same name in both.
// The build refuses a page missing from one language, a link to a page or a heading that does not exist, and HTML
// written in the Markdown (only <kbd> is let through). site.yml publishes the result to sasou-web/mira-wiki, whose
// GitHub Pages serve it (one custom domain per repository: miracinema.app is this one's).
// node tools/site/wiki.mjs <x.y.z> [out=dist/wiki]
import { copyFileSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

export const languages = ['en', 'fr'];
export const origin = 'https://wiki.miracinema.app';
const site = 'https://miracinema.app';
const repository = 'https://github.com/sasou-web/Mira';

const words = {
  en: {
    wiki: 'Wiki', skip: 'Skip to content', site: 'Mira', download: 'Download', other: 'Français', otherShort: 'FR',
    search: 'Search the wiki', searchHint: 'Search', noResults: 'Nothing found for', pages: 'Pages', onThisPage: 'On this page',
    previous: 'Previous', next: 'Next', edit: 'Edit this page on GitHub', ask: 'Ask on Discord', copy: 'Copy', copied: 'Copied',
    anchor: 'Link to this section', home: 'Mira wiki, home', foot: 'Free and open source. Not affiliated with Jellyfin. No films provided.',
    callouts: { NOTE: 'Note', TIP: 'Tip', IMPORTANT: 'Important', WARNING: 'Careful', PRIVACY: 'Privacy' },
    links: { code: 'Source code', discord: 'Discord', releases: 'Releases', security: 'Security' },
  },
  fr: {
    wiki: 'Wiki', skip: 'Aller au contenu', site: 'Mira', download: 'Télécharger', other: 'English', otherShort: 'EN',
    search: 'Chercher dans le wiki', searchHint: 'Chercher', noResults: 'Rien trouvé pour', pages: 'Pages', onThisPage: 'Sur cette page',
    previous: 'Précédent', next: 'Suivant', edit: 'Modifier cette page sur GitHub', ask: 'Demander sur Discord', copy: 'Copier', copied: 'Copié',
    anchor: 'Lien vers cette partie', home: 'Wiki de Mira, accueil', foot: 'Libre et gratuit. Sans lien avec Jellyfin. Aucun film fourni.',
    callouts: { NOTE: 'À savoir', TIP: 'Astuce', IMPORTANT: 'Important', WARNING: 'Attention', PRIVACY: 'Vie privée' },
    links: { code: 'Code source', discord: 'Discord', releases: 'Versions', security: 'Sécurité' },
  },
};

// The page's place in the wiki: its folder, from the wiki's root ('', 'fr/tailscale/').
const folder = (lang, slug) => `${lang === 'en' ? '' : `${lang}/`}${slug === 'index' ? '' : `${slug}/`}`;
// From one folder to another, as a relative address ('../tailscale/', './').
const relative = (from, to) => {
  const path = posix.relative(`/${from}`, `/${to}`);
  return path === '' ? './' : `${path}/`;
};
const up = from => '../'.repeat(from.split('/').filter(Boolean).length);

export const escape = text => text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');

// A heading's id: its words, lower case, without accents ('Sign in to Jellyfin' → 'sign-in-to-jellyfin').
export const slugify = text => text.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()
  .replace(/<[^>]+>/g, '').replace(/&[a-z]+;/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

// Inline Markdown: `code`, **strong**, *em*, [links](target), <kbd>keys</kbd>. Code first, untouched by the rest.
export function inline(text, link = target => target) {
  const codes = [];
  let out = escape(text).replace(/`([^`]+)`/g, (_, code) => `\u0000${codes.push(code) - 1}\u0000`);
  out = out.replace(/&lt;kbd&gt;(.+?)&lt;\/kbd&gt;/g, '<kbd>$1</kbd>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*\w])\*(?!\s)(.+?)\*(?!\w)/g, '$1<em>$2</em>')
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, target) => {
      const href = link(target.replaceAll('&amp;', '&'));
      const outside = /^https?:/.test(href);
      return `<a href="${escape(href)}"${outside ? ' rel="noopener"' : ''}>${label}</a>`;
    });
  return out.replace(/\u0000(\d+)\u0000/g, (_, index) => `<code>${codes[index]}</code>`);
}

// Block Markdown: ## and ### headings (with ids), paragraphs, lists (one level of nesting), ``` code, | tables |, and
// > [!NOTE] callouts (GitHub's syntax, plus PRIVACY). Returns the HTML and the headings, for the contents and search.
export function render(markdown, { link, callouts = words.en.callouts, anchor = words.en.anchor } = {}) {
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  const html = [];
  const headings = [];
  const ids = new Set();
  const text = value => inline(value, link);
  let i = 0;
  const heading = (level, title) => {
    let id = slugify(title);
    for (let n = 2; ids.has(id); n++) id = `${slugify(title)}-${n}`;
    ids.add(id);
    headings.push({ level, title: title.replace(/`/g, ''), id });
    html.push(`<h${level} id="${id}">${text(title)}<a class="anchor" href="#${id}" aria-label="${escape(anchor)}">#</a></h${level}>`);
  };
  const list = (start) => {
    const ordered = /^\d+\.\s/.test(lines[start]);
    const items = [];
    while (i < lines.length) {
      const line = lines[i];
      const item = line.match(/^(\d+\.|[-*])\s+(.*)$/);
      if (item && (/^\d/.test(item[1]) === ordered)) { items.push({ body: [item[2]], children: [], tail: [] }); i++; continue; }
      const child = line.match(/^(\s{2,4})(\d+\.|[-*])\s+(.*)$/);
      if (child && items.length) {
        const last = items.at(-1);
        const kind = /^\d/.test(child[2]) ? 'ol' : 'ul';
        if (!last.children.length || last.children.at(-1).kind !== kind) last.children.push({ kind, items: [], indent: child[1].length });
        last.children.at(-1).items.push([child[3]]);
        i++; continue;
      }
      const code = line.match(/^\s{2,4}```/);
      if (code && items.length) {
        const block = [];
        i++;
        while (i < lines.length && !/^\s*```/.test(lines[i])) block.push(lines[i++].replace(/^\s{2,4}/, ''));
        i++;
        items.at(-1).children.push({ kind: 'code', block });
        continue;
      }
      // A line indented further: more of the item, of its last sub-item when indented past that one's marker, or
      // what follows its sub-list when indented like the item's own text.
      const more = line.match(/^(\s{2,})(\S.*)$/);
      if (more && items.length) {
        const last = items.at(-1);
        const group = last.children.at(-1);
        if (group && group.kind !== 'code' && more[1].length > group.indent) group.items.at(-1).push(more[2]);
        else if (group) last.tail.push(more[2]);
        else last.body.push(more[2]);
        i++; continue;
      }
      break;
    }
    const tag = ordered ? 'ol' : 'ul';
    html.push(`<${tag}>${items.map(item => `<li>${text(item.body.join(' '))}${item.children.map(group => group.kind === 'code'
      ? codeBlock(group.block, '')
      : `<${group.kind}>${group.items.map(body => `<li>${text(body.join(' '))}</li>`).join('')}</${group.kind}>`).join('')}${item.tail.length ? `<p>${text(item.tail.join(' '))}</p>` : ''}</li>`).join('')}</${tag}>`);
  };
  const codeBlock = (block, language) => `<div class="code"><pre${language ? ` data-language="${escape(language)}"` : ''}><code>${escape(block.join('\n'))}</code></pre></div>`;
  const cells = line => line.trim().replace(/^\||\|$/g, '').split(/(?<!\\)\|/).map(cell => cell.trim().replaceAll('\\|', '|'));
  while (i < lines.length) {
    const line = lines[i];
    if (!line.trim()) { i++; continue; }
    let match;
    if ((match = line.match(/^(#{2,3})\s+(.*)$/))) { heading(match[1].length, match[2].trim()); i++; continue; }
    if (/^#\s/.test(line)) throw new Error(`a page's title is in its front matter, not a "# " heading: ${line}`);
    if ((match = line.match(/^```(\S*)\s*$/))) {
      const block = [];
      i++;
      while (i < lines.length && !/^```\s*$/.test(lines[i])) block.push(lines[i++]);
      if (i >= lines.length) throw new Error('a code block is never closed');
      i++;
      html.push(codeBlock(block, match[1]));
      continue;
    }
    if (/^>\s?/.test(line)) {
      const quote = [];
      while (i < lines.length && /^>\s?/.test(lines[i])) quote.push(lines[i++].replace(/^>\s?/, ''));
      const kind = quote[0].match(/^\[!([A-Z]+)\]\s*$/);
      if (kind && !callouts[kind[1]]) throw new Error(`unknown callout [!${kind[1]}]`);
      const body = render(quote.slice(kind ? 1 : 0).join('\n'), { link, callouts, anchor }).html;
      if (kind) {
        const name = kind[1].toLowerCase();
        const icon = name === 'warning' ? 'warning' : 'info';
        html.push(`<aside class="callout ${name}"><p class="label"><svg class="icon" aria-hidden="true"><use href="__ICONS__#${icon}"/></svg>${escape(callouts[kind[1]])}</p>${body}</aside>`);
      } else html.push(`<blockquote>${body}</blockquote>`);
      continue;
    }
    if (/^(\d+\.|[-*])\s+/.test(line)) { list(i); continue; }
    if (/^\|/.test(line) && /^\|?\s*:?-{3,}/.test(lines[i + 1] ?? '')) {
      const head = cells(line);
      i += 2;
      const rows = [];
      while (i < lines.length && /^\|/.test(lines[i])) rows.push(cells(lines[i++]));
      html.push(`<div class="table"><table><thead><tr>${head.map(cell => `<th scope="col">${text(cell)}</th>`).join('')}</tr></thead><tbody>${rows.map(row => `<tr>${row.map(cell => `<td>${text(cell)}</td>`).join('')}</tr>`).join('')}</tbody></table></div>`);
      continue;
    }
    const paragraph = [];
    while (i < lines.length && lines[i].trim() && !/^(#{2,3}\s|```|>|\||(\d+\.|[-*])\s)/.test(lines[i])) paragraph.push(lines[i++].trim());
    html.push(`<p>${text(paragraph.join(' '))}</p>`);
  }
  return { html: html.join('\n'), headings };
}

// "---\ntitle: …\ndescription: …\n---" at the top of each page.
export function frontMatter(source, file) {
  const match = source.match(/^---\n([\s\S]*?)\n---\n/);
  if (!match) throw new Error(`${file}: no front matter`);
  const fields = Object.fromEntries(match[1].split('\n').map(line => {
    const at = line.indexOf(':');
    return [line.slice(0, at).trim(), line.slice(at + 1).trim()];
  }));
  for (const key of ['title', 'description']) if (!fields[key]) throw new Error(`${file}: no ${key}`);
  return { fields, body: source.slice(match[0].length) };
}

// Words for search: the text of an HTML fragment.
// Cells of a row are kept apart by " · ", blocks by a space, inline tags leave nothing (no space before a comma).
const plain = html => html.replace(/<a class="anchor"[^>]*>#<\/a>/g, '').replace(/<\/t[dh]>(?=<t[dh])/g, ' · ')
  .replace(/<\/?(p|li|ul|ol|tr|td|th|table|div|pre|aside|h\d)\b[^>]*>/g, ' ').replace(/<[^>]+>/g, '')
  .replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', '"').replaceAll('&amp;', '&').replace(/\s+/g, ' ').trim();

function page({ lang, slug, fields, html, headings, nav, groupTitle, previous, next, version }) {
  const w = words[lang];
  const here = folder(lang, slug);
  const root = up(here);
  const wikiHome = relative(here, folder(lang, 'index'));
  const other = languages.find(code => code !== lang);
  const otherHref = relative(here, folder(other, slug));
  const asset = path => `${root}${path}`;
  const home = `${site}/${lang === 'en' ? '' : 'fr/'}`;
  const contents = headings.filter(h => h.level === 2);
  const title = slug === 'index' ? `${fields.title} · Mira` : `${fields.title} · Mira wiki`;
  const alternates = languages.map(code => `  <link rel="alternate" hreflang="${code}" href="${origin}/${folder(code, slug)}">`).join('\n');
  const navigation = nav.map(group => `<li><p class="group">${escape(group.title[lang])}</p><ul>${group.pages.map(item => {
    const current = item.slug === slug;
    return `<li><a href="${relative(here, folder(lang, item.slug))}"${current ? ' aria-current="page"' : ''}>${escape(item.label)}</a></li>`;
  }).join('')}</ul></li>`).join('');
  const pager = [previous && ['previous', previous], next && ['next', next]].filter(Boolean).map(([kind, item]) =>
    `<a class="${kind}" href="${relative(here, folder(lang, item.slug))}" rel="${kind === 'previous' ? 'prev' : 'next'}"><span>${escape(w[kind])}</span>${escape(item.label)}</a>`).join('');
  const platforms = fields.platforms ? `<ul class="platforms" aria-label="${lang === 'en' ? 'For' : 'Pour'}">${fields.platforms.split(',').map(name => `<li>${escape(name.trim())}</li>`).join('')}</ul>` : '';
  const body = html.replaceAll('__ICONS__', asset('img/icons.svg')).replaceAll('__VERSION__', version);
  const source = `${repository}/blob/main/wiki/${lang}/${slug}.md`;
  return `<!doctype html>
<html lang="${lang}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' data:; style-src 'self'; font-src 'self'; script-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'">
  <meta name="referrer" content="no-referrer">
  <title>${escape(title)}</title>
  <meta name="description" content="${escape(fields.description)}">
  <meta name="theme-color" content="#000000">
  <link rel="canonical" href="${origin}/${here}">
${alternates}
  <link rel="alternate" hreflang="x-default" href="${origin}/${folder('en', slug)}">
  <meta property="og:type" content="article">
  <meta property="og:site_name" content="Mira">
  <meta property="og:title" content="${escape(fields.title)}">
  <meta property="og:description" content="${escape(fields.description)}">
  <meta property="og:url" content="${origin}/${here}">
  <meta property="og:image" content="${origin}/img/${lang === 'en' ? 'share-en' : 'share'}.jpg">
  <meta property="og:image:width" content="1200">
  <meta property="og:image:height" content="630">
  <meta property="og:locale" content="${lang === 'en' ? 'en_US' : 'fr_FR'}">
  <meta name="twitter:card" content="summary_large_image">
  <link rel="icon" href="${asset('img/mark.svg')}" type="image/svg+xml">
  <link rel="icon" href="${asset('img/icon-192.png')}" type="image/png" sizes="192x192">
  <link rel="apple-touch-icon" href="${asset('img/apple-touch-icon.png')}">
  <link rel="preload" href="${asset('fonts/nunito.woff2')}" as="font" type="font/woff2" crossorigin>
  <link rel="stylesheet" href="${asset('css/wiki.css')}">
  <script src="${asset('js/wiki.js')}" defer></script>
</head>
<body class="wiki-page" data-search="${relative(here, folder(lang, 'index'))}search.json" data-copy="${escape(w.copy)}" data-copied="${escape(w.copied)}" data-none="${escape(w.noResults)}">
  <a class="skip" href="#main">${escape(w.skip)}</a>

  <header class="bar wrap">
    <a class="brand" href="${wikiHome}" aria-label="${escape(w.home)}"><img src="${asset('img/mark.svg')}" alt="" width="26" height="26">Mira <span class="kind">${w.wiki}</span></a>
    <nav aria-label="Mira">
      <a href="${home}">${lang === 'en' ? 'Website' : 'Site'}</a>
      <a href="${home}#download">${escape(w.download)}</a>
      <a href="https://discord.gg/Bh3F6PDUvB">Discord</a>
    </nav>
    <a class="lang" href="${otherHref}" hreflang="${other}" lang="${other}">${w.otherShort}</a>
  </header>

  <div class="wiki wrap">
    <aside class="side">
      <form class="find" role="search" hidden>
        <label class="field"><svg class="icon" aria-hidden="true"><use href="${asset('img/icons.svg')}#search"/></svg><span class="visually-hidden">${escape(w.search)}</span><input type="search" placeholder="${escape(w.searchHint)}…" autocomplete="off" spellcheck="false" role="combobox" aria-expanded="false" aria-controls="found" aria-autocomplete="list"><kbd class="hint" aria-hidden="true">/</kbd></label>
        <ul class="found" id="found" role="listbox" hidden></ul>
        <p class="nothing" role="status"></p>
      </form>
      <details class="pages" open>
        <summary>${escape(w.pages)}<span class="where">${escape(fields.short ?? fields.title)}</span></summary>
        <nav aria-label="${escape(w.pages)}"><ul>${navigation}</ul></nav>
      </details>
    </aside>

    <main id="main" class="doc">
      <header class="doc-head">
        ${groupTitle ? `<p class="crumb">${escape(groupTitle)}</p>` : ''}
        <h1>${inline(fields.title)}</h1>
        <p class="lede">${inline(fields.description)}</p>
        ${platforms}
      </header>
      ${contents.length >= 3 ? `<nav class="toc" aria-label="${escape(w.onThisPage)}"><p>${escape(w.onThisPage)}</p><ol>${contents.map(h => `<li><a href="#${h.id}">${inline(h.title)}</a></li>`).join('')}</ol></nav>` : ''}
      <article class="prose">
${body}
      </article>
      <footer class="doc-foot">
        ${pager ? `<nav class="pager" aria-label="${escape(w.pages)}">${pager}</nav>` : ''}
        <p class="help"><a href="${source}"><svg class="icon" aria-hidden="true"><use href="${asset('img/icons.svg')}#pencil"/></svg>${escape(w.edit)}</a><a href="https://discord.gg/Bh3F6PDUvB"><svg class="icon" aria-hidden="true"><use href="${asset('img/icons.svg')}#users"/></svg>${escape(w.ask)}</a></p>
      </footer>
    </main>
  </div>

  <footer class="foot wrap">
    <div class="sign">
      <a class="brand" href="${home}"><img src="${asset('img/mark.svg')}" alt="" width="24" height="24">Mira</a>
      <p>${escape(w.foot)}</p>
    </div>
    <nav aria-label="Links">
      <a href="${repository}">${escape(w.links.code)}</a>
      <a href="https://discord.gg/Bh3F6PDUvB">${escape(w.links.discord)}</a>
      <a href="${repository}/releases">${escape(w.links.releases)}</a>
      <a href="${repository}/blob/main/${lang === 'en' ? '' : 'docs/'}SECURITY${lang === 'en' ? '' : '.fr'}.md">${escape(w.links.security)}</a>
      <a href="${otherHref}" hreflang="${other}" lang="${other}">${escape(w.other)}</a>
    </nav>
  </footer>
</body>
</html>
`;
}

// GitHub Pages' page for an address that leads nowhere: served at any depth, so its links start at the root.
const lost = () => `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
  <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src 'self' data:; style-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'">
  <meta name="referrer" content="no-referrer">
  <meta name="robots" content="noindex">
  <title>Page not found · Mira wiki</title>
  <meta name="theme-color" content="#000000">
  <link rel="icon" href="/img/mark.svg" type="image/svg+xml">
  <link rel="stylesheet" href="/css/wiki.css">
</head>
<body>
  <main class="lost">
    <img src="/img/mark.svg" alt="" width="56" height="56">
    <p class="code num">404</p>
    <h1>This page isn’t in the wiki.</h1>
    <p lang="fr">Cette page n’est pas dans le wiki.</p>
    <div class="actions">
      <a class="btn primary" href="/">Wiki home</a>
      <a class="btn quiet" href="/fr/" lang="fr">Accueil du wiki</a>
    </div>
  </main>
</body>
</html>
`;

export function buildWiki(out, version, source = 'wiki') {
  const nav = JSON.parse(readFileSync(join(source, 'nav.json'), 'utf8'));
  const slugs = nav.flatMap(group => group.pages);
  const problems = [];
  for (const lang of languages) {
    const files = readdirSync(join(source, lang)).filter(name => name.endsWith('.md')).map(name => name.slice(0, -3));
    for (const name of files) if (!slugs.includes(name)) problems.push(`wiki/${lang}/${name}.md is in no group of wiki/nav.json`);
    for (const slug of slugs) if (!files.includes(slug)) problems.push(`wiki/${lang}/${slug}.md is missing`);
  }
  if (problems.length) throw new Error(problems.join('\n'));

  rmSync(out, { recursive: true, force: true });
  // What the pages share with the showcase site: the font, the mark, the icons and the share cards. The stylesheet is
  // the wiki's own (wiki/css/wiki.css), so a new look of the site never changes the wiki by accident.
  for (const file of ['fonts/nunito.woff2', 'fonts/OFL.txt', 'img/mark.svg', 'img/icons.svg', 'img/icon-192.png',
    'img/icon-512.png', 'img/apple-touch-icon.png', 'img/share.jpg', 'img/share-en.jpg', 'css/wiki.css', 'js/wiki.js']) {
    mkdirSync(dirname(join(out, file)), { recursive: true });
    copyFileSync(join(file.includes('wiki') ? source : 'site', file), join(out, file));
  }
  const urls = [];
  for (const lang of languages) {
    const pages = new Map(slugs.map(slug => {
      const file = join(source, lang, `${slug}.md`);
      return [slug, { slug, file, ...frontMatter(readFileSync(file, 'utf8'), file) }];
    }));
    const labels = slug => pages.get(slug).fields.short ?? pages.get(slug).fields.title;
    const localNav = nav.map(group => ({ title: group.title, pages: group.pages.map(slug => ({ slug, label: labels(slug) })) }));
    const order = localNav.flatMap(group => group.pages);
    // Each page rendered once to know its headings, then again with links checked against them.
    const anchors = new Map([...pages.values()].map(p => [p.slug, new Set(render(p.body).headings.map(h => h.id))]));
    const index = [];
    for (const p of pages.values()) {
      const here = folder(lang, p.slug);
      const link = target => {
        if (/^(https?:|mailto:)/.test(target)) return target;
        const [path, hash] = target.split('#');
        const slug = path === '' ? p.slug : path.replace(/\.md$/, '');
        if (!pages.has(slug)) { problems.push(`${p.file}: link to ${target}, no such page`); return target; }
        if (hash && !anchors.get(slug).has(hash)) problems.push(`${p.file}: link to ${target}, no such heading`);
        return path === '' ? `#${hash}` : `${relative(here, folder(lang, slug))}${hash ? `#${hash}` : ''}`;
      };
      let rendered;
      try {
        rendered = render(p.body, { link, callouts: words[lang].callouts, anchor: words[lang].anchor });
      } catch (error) {
        problems.push(`${p.file}: ${error.message}`);
        continue;
      }
      const at = order.findIndex(item => item.slug === p.slug);
      const group = nav.find(g => g.pages.includes(p.slug));
      const html = page({
        lang, slug: p.slug, fields: p.fields, html: rendered.html, headings: rendered.headings, nav: localNav,
        groupTitle: p.slug === 'index' ? '' : group.title[lang], previous: order[at - 1], next: order[at + 1], version,
      });
      const target = join(out, here, 'index.html');
      mkdirSync(dirname(target), { recursive: true });
      writeFileSync(target, html);
      urls.push(`${origin}/${here}`);
      // Search: one entry for the page, one for each section, with its words.
      const url = relative(folder(lang, 'index'), here);
      const sections = rendered.html.split(/(?=<h[23] id=")/);
      index.push({ p: p.fields.title, s: '', u: url, t: plain(`${p.fields.description} ${sections[0]}`) });
      for (const section of sections.slice(1)) {
        const id = section.match(/^<h[23] id="([^"]+)"/)[1];
        const title = plain(section.match(/^<h[23][^>]*>([\s\S]*?)<\/h[23]>/)[1]);
        index.push({ p: p.fields.title, s: title, u: `${url}#${id}`, t: plain(section.replace(/^<h[23][^>]*>[\s\S]*?<\/h[23]>/, '')) });
      }
    }
    writeFileSync(join(out, folder(lang, 'index'), 'search.json'), JSON.stringify(index));
  }
  if (problems.length) throw new Error(problems.join('\n'));
  writeFileSync(join(out, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls.map(url => `  <url><loc>${url}</loc></url>`).join('\n')}
</urlset>
`);
  writeFileSync(join(out, 'robots.txt'), `User-agent: *\nAllow: /\nSitemap: ${origin}/sitemap.xml\n`);
  // GitHub Pages: the custom domain, and no Jekyll (it would hide nothing here, but costs a build).
  writeFileSync(join(out, 'CNAME'), `${new URL(origin).host}\n`);
  writeFileSync(join(out, '.nojekyll'), '');
  writeFileSync(join(out, '404.html'), lost());
  return urls.length;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [version, out = 'dist/wiki'] = process.argv.slice(2);
  if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) {
    console.error('Usage: node tools/site/wiki.mjs <x.y.z> [out]');
    process.exit(1);
  }
  console.log(`${out}: ${buildWiki(out, version)} pages`);
}
