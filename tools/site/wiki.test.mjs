// The wiki's Markdown and its build: node --test tools/site/wiki.test.mjs (run by site.yml).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, cpSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { inline, render, slugify, frontMatter, buildWiki } from './wiki.mjs';

test('inline: escapes HTML, keeps kbd, code is literal', () => {
  assert.equal(inline('<b> & **bold**'), '&lt;b&gt; &amp; <strong>bold</strong>');
  assert.equal(inline('<kbd>F1</kbd>'), '<kbd>F1</kbd>');
  assert.equal(inline('`**not bold**`'), '<code>**not bold**</code>');
  assert.equal(inline('<script>x</script>'), '&lt;script&gt;x&lt;/script&gt;');
});

test('inline: outside links get noopener, inside links go through the resolver', () => {
  assert.equal(inline('[a](https://example.com)'), '<a href="https://example.com" rel="noopener">a</a>');
  assert.equal(inline('[b](mac.md#updates)', () => '../mac/#updates'), '<a href="../mac/#updates">b</a>');
});

test('slugify drops accents and punctuation', () => {
  assert.equal(slugify('Quand Tailscale lui-même est en panne'), 'quand-tailscale-lui-meme-est-en-panne');
  assert.equal(slugify('Loin de la maison : la qualité'), 'loin-de-la-maison-la-qualite');
});

test('render: headings, nested lists with a tail, code, tables and callouts', () => {
  const { html, headings } = render([
    '## One',
    '1. First',
    '   - child',
    '   Back to the first item.',
    '2. Second',
    '',
    '```sh',
    'echo <hi>',
    '```',
    '',
    '| A | B |',
    '| --- | --- |',
    '| 1 | 2 |',
    '',
    '> [!WARNING]',
    '> Careful.',
    '',
    '## One',
  ].join('\n'));
  assert.deepEqual(headings.map(h => h.id), ['one', 'one-2']);
  assert.match(html, /<li>First<ul><li>child<\/li><\/ul><p>Back to the first item\.<\/p><\/li>/);
  assert.match(html, /echo &lt;hi&gt;/);
  assert.match(html, /<td>1<\/td><td>2<\/td>/);
  assert.match(html, /class="callout warning"/);
});

test('render refuses an unknown callout and an unclosed code block', () => {
  assert.throws(() => render('> [!DANGER]\n> no'));
  assert.throws(() => render('```\nnever closed'));
});

test('front matter is required', () => {
  assert.deepEqual(frontMatter('---\ntitle: T\ndescription: D\n---\nbody', 'x.md').fields.title, 'T');
  assert.throws(() => frontMatter('# no front matter', 'x.md'));
});

test('build: every page in both languages, links and anchors checked', () => {
  const out = mkdtempSync(join(tmpdir(), 'wiki-'));
  try {
    assert.ok(buildWiki(out, '1.2.3') >= 2);
    assert.ok(existsSync(join(out, 'index.html')) && existsSync(join(out, 'fr/index.html')));
    assert.equal(readFileSync(join(out, 'CNAME'), 'utf8').trim(), 'wiki.miracinema.app');
    const windows = readFileSync(join(out, 'windows/index.html'), 'utf8');
    assert.ok(windows.includes('Mira-1.2.3-win-x64-setup.exe'));
    assert.ok(!windows.includes('__VERSION__'));
    assert.match(windows, /Content-Security-Policy/);
  } finally { rmSync(out, { recursive: true, force: true }); }
});

test('build refuses a link to a missing anchor', () => {
  const root = mkdtempSync(join(tmpdir(), 'wiki-src-'));
  const out = mkdtempSync(join(tmpdir(), 'wiki-out-'));
  try {
    cpSync('wiki', join(root, 'wiki'), { recursive: true });
    const page = join(root, 'wiki/en/faq.md');
    writeFileSync(page, readFileSync(page, 'utf8') + '\n[broken](mac.md#nowhere)\n');
    assert.throws(() => buildWiki(out, '1.2.3', join(root, 'wiki')), /nowhere/);
  } finally {
    rmSync(root, { recursive: true, force: true });
    rmSync(out, { recursive: true, force: true });
  }
});
