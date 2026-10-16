// Mira web in English and in French (src/Mira.Web/js/i18n.js): every text of the code has its English, and the
// browser's language picks the one shown.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tr, trn, setLanguage, browserLanguage, timeOfDay } from '../../../src/Mira.Web/js/i18n.js';
import EN from '../../../src/Mira.Web/js/lang/en.js';

const root = fileURLToPath(new URL('../../../src/Mira.Web/js/', import.meta.url));
const files = (dir) => readdirSync(dir).flatMap((name) => {
  const path = join(dir, name);
  if (statSync(path).isDirectory()) return name === 'lang' ? [] : files(path);
  return name.endsWith('.js') && name !== 'i18n.js' ? [path] : [];
});

// A string literal, in any of JavaScript's quotes (a template literal only without ${…}).
const LITERAL = String.raw`('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|\`(?:[^\`\\$]|\\.)*\`)`;
const unquote = (literal) => Function(`return ${literal}`)();

/** Each tr('…') and trn(n, '…', '…') of the code, with where it is. */
function calls() {
  const found = [];
  for (const file of files(root)) {
    const source = readFileSync(file, 'utf8');
    const where = (index) => `${file.slice(root.length)}:${source.slice(0, index).split('\n').length}`;
    for (const m of source.matchAll(/\btr\(\s*/g)) {
      const rest = source.slice(m.index + m[0].length);
      const literal = rest.match(new RegExp(`^${LITERAL}`));
      found.push({ kind: 'tr', at: where(m.index), text: literal ? unquote(literal[1]) : null });
    }
    for (const m of source.matchAll(/\btrc\(\s*/g)) {
      const rest = source.slice(m.index + m[0].length);
      const literal = rest.match(new RegExp(`^${LITERAL}\\s*,\\s*${LITERAL}`));
      found.push({ kind: 'tr', at: where(m.index), text: literal ? `${unquote(literal[1])}|${unquote(literal[2])}` : null });
    }
    for (const m of source.matchAll(/\btrn\(/g)) {
      // The count first (any expression without a quote), then the two literals.
      const rest = source.slice(m.index + m[0].length);
      const literal = rest.match(new RegExp(`^[^'"\`;]*?,\\s*${LITERAL}\\s*,\\s*${LITERAL}`));
      found.push({ kind: 'trn', at: where(m.index), text: literal ? unquote(literal[1]) : null, many: literal ? unquote(literal[2]) : null });
    }
  }
  return found;
}

const names = (text) => [...String(text).matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();

test('every text of the code is a literal with its English, the same {names} in both', () => {
  const problems = [];
  for (const call of calls()) {
    if (call.text == null) { problems.push(`${call.at}: ${call.kind}() needs a string literal first, for this test`); continue; }
    const english = EN[call.text];
    if (call.kind === 'tr') {
      if (typeof english !== 'string') problems.push(`${call.at}: no English for ${JSON.stringify(call.text)}`);
      else if (names(english).join() !== names(call.text.replace(/^[^|]*\|/, '')).join()) problems.push(`${call.at}: ${JSON.stringify(call.text)} and its English name different {…}`);
    } else if (!Array.isArray(english) || english.length !== 2) {
      problems.push(`${call.at}: no [one, other] English for ${JSON.stringify(call.text)}`);
    } else {
      for (const form of english) if (names(form).join() !== names(call.many).join()) problems.push(`${call.at}: ${JSON.stringify(form)} and ${JSON.stringify(call.many)} name different {…}`);
    }
  }
  assert.deepEqual(problems, []);
});

test('no English is left that the code no longer asks for, and no French has two', async () => {
  const used = new Set(calls().map((call) => call.text));
  assert.deepEqual(Object.keys(EN).filter((key) => !used.has(key)), []);
  // Two parts of the catalog with the same French: the same English (the spread of lang/en.js keeps the last one).
  const seen = new Map(), problems = [];
  const dir = join(root, 'lang', 'en');
  for (const name of readdirSync(dir)) {
    const part = (await import(new URL(`../../../src/Mira.Web/js/lang/en/${name}`, import.meta.url))).default;
    for (const [key, value] of Object.entries(part)) {
      if (seen.has(key) && JSON.stringify(seen.get(key).value) !== JSON.stringify(value)) problems.push(`${JSON.stringify(key)}: ${seen.get(key).name} and ${name} differ`);
      seen.set(key, { name, value });
    }
  }
  assert.deepEqual(problems, []);
});

test('the browser\'s language: the first of English and French in its list, English without either', () => {
  assert.equal(browserLanguage(['fr-FR', 'en-US']), 'fr');
  assert.equal(browserLanguage(['en-GB', 'fr']), 'en');
  assert.equal(browserLanguage(['de-DE', 'fr-CH']), 'fr');
  assert.equal(browserLanguage(['de-DE', 'es']), 'en');
  assert.equal(browserLanguage([]), 'en');
  assert.equal(browserLanguage(['FR_ca']), 'fr');
});

test('a count and its noun, in each language', () => {
  try {
    setLanguage('fr');
    assert.equal(trn(0, '{n} épisode', '{n} épisodes'), '0 épisode');
    assert.equal(trn(1, '{n} épisode', '{n} épisodes'), '1 épisode');
    assert.equal(trn(2, '{n} épisode', '{n} épisodes'), '2 épisodes');
    assert.equal(tr('Serveur « {name} »', { name: 'Maison' }), 'Serveur « Maison »');
    assert.equal(timeOfDay(new Date(2026, 9, 16, 23, 5)), '23 h 05');
    setLanguage('en');
    // A text without its English shows its French, rather than nothing.
    assert.equal(tr('Une phrase que Mira ne dit pas'), 'Une phrase que Mira ne dit pas');
  } finally { setLanguage('en'); }
});
