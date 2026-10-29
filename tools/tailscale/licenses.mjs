#!/usr/bin/env node
// The licences of everything a Go program of Mira links (mira-tailscale): Go's own (its standard library and runtime
// are in every Go binary), then each module the binary was built with, as the binary itself lists them (go version -m:
// exactly what was linked, for that system and those build tags), their files taken from Go's module cache.
//   node tools/tailscale/licenses.mjs <binary> <output folder>
// Writes <output>/go/LICENSE (and PATENTS), <output>/<module path>/LICENSE… and <output>/MODULES.txt (module, version,
// files). Exit code 1 when a module has no licence file: nothing ships without its licence. Needs the go that built it.
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const [binary, output] = process.argv.slice(2);
if (!binary || !output) { console.error('usage: node tools/tailscale/licenses.mjs <binary> <output folder>'); process.exit(2); }
const go = (...args) => execFileSync('go', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });

// "\tdep\t<path>\t<version>\t<sum>", then "\t=>\t<path>\t<version>\t<sum>" when a replace directive took its place.
const modules = [];
for (const line of go('version', '-m', resolve(binary)).split(/\r?\n/)) {
  const [, kind, path, version] = line.split('\t');
  if (kind === 'dep') modules.push({ path, version });
  else if (kind === '=>' && modules.length) modules[modules.length - 1] = { path, version };
}
const built = /\bgo[0-9][^\s]*/.exec(go('version', resolve(binary)))?.[0];
const here = go('env', 'GOVERSION').trim();
if (built !== here) { console.error(`${binary} was built by ${built}, this go is ${here}: its licence is that go's.`); process.exit(2); }

// LICENSE, LICENSE.md, COPYING, NOTICE.txt, PATENTS… but not license_test.go.
const licence = /^(licen[cs]e|copying|notice|patents|unlicense)([._-][\w.-]*)?$/i;
const code = /\.(go|s|c|h|py|sh|js|json|ya?ml|toml)$/i;
const filesOf = dir => readdirSync(dir).filter(name => licence.test(name) && !code.test(name) && statSync(join(dir, name)).isFile()).sort();
rmSync(output, { recursive: true, force: true });
const lines = [];
const missing = [];
function keep(name, version, dir) {
  const files = filesOf(dir);
  if (files.length === 0) { missing.push(`${name} ${version}`); return; }
  const target = join(output, ...name.split('/'));
  mkdirSync(target, { recursive: true });
  for (const file of files) copyFileSync(join(dir, file), join(target, file));
  lines.push(`${name} ${version}: ${files.map(file => `${name}/${file}`).join(', ')}`);
}
keep('go', here, go('env', 'GOROOT').trim());
for (const { path, version } of modules) {
  // Already in the module cache since the build; downloaded otherwise (checked by go.sum's hashes).
  const { Dir: dir } = JSON.parse(go('mod', 'download', '-json', `${path}@${version}`));
  if (!dir || !existsSync(dir)) { missing.push(`${path} ${version} (not in the module cache)`); continue; }
  keep(path, version, dir);
}
writeFileSync(join(output, 'MODULES.txt'), [
  `What ${binary.split(/[\\/]/).pop()} contains besides Mira's own code (MIT), and the licence files of each, beside this file.`,
  '', ...lines, ''].join('\n'));
if (missing.length) { console.error(`No licence file for: ${missing.join('; ')}`); process.exit(1); }
console.log(`${lines.length} licences (Go and ${modules.length} modules) in ${output}`);
