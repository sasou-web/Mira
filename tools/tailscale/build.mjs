#!/usr/bin/env node
// Builds mira-tailscale, the Tailscale node of Mira for Windows and Mac (src/Mira.Tailscale), for one system and the
// same way everywhere (the workflows, a PC): pure Go, no C compiler; reproducible (-trimpath, no VCS stamp, no build
// id: two builds of one commit with the same Go are the same bytes); stamped with the version of Mira it ships with;
// built with the tags of build-tags.txt (what tsnet leaves out, the logs it would send to Tailscale first); on Windows
// with its file properties (product Mira and its version, which SignPath checks and the firewall's prompt shows). Then
// the licence files of every module it links (licenses.mjs) and its SHA-256.
//   node tools/tailscale/build.mjs <goos>/<goarch> <output folder> [--version x.y.z]
// Writes <output>/mira-tailscale.exe (windows) or <output>/mira-tailscale, its .sha256 and <output>/licenses/. Needs Go
// 1.21 or later and Node: go fetches and runs the very Go that go.mod names (GOTOOLCHAIN), whatever the one installed,
// and go-winres is pinned in tools/tailscale/winres.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { backport } from './backport.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const module = join(root, 'src/Mira.Tailscale');

const args = process.argv.slice(2);
const versionAt = args.indexOf('--version');
const version = versionAt >= 0 ? args.splice(versionAt, 2)[1]
  : /<Version>([^<]+)<\/Version>/.exec(readFileSync(join(root, 'src/Mira.Desktop/Mira.Desktop.csproj'), 'utf8'))?.[1];
const [target, out] = args;
const [goos, goarch] = (target ?? '').split('/');
if (!goos || !goarch || !out) { console.error('usage: node tools/tailscale/build.mjs <goos>/<goarch> <output folder> [--version x.y.z]'); process.exit(2); }
if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) { console.error(`Version x.y.z expected, got '${version}'.`); process.exit(2); }
const tags = readFileSync(join(module, 'build-tags.txt'), 'utf8').split('\n')
  .map(line => line.replace(/#.*/, '').trim()).filter(Boolean).join(',').split(/[\s,]+/).filter(Boolean);
if (!tags.includes('ts_omit_logtail')) { console.error('build-tags.txt must keep ts_omit_logtail: without it the node can send its logs to Tailscale.'); process.exit(2); }

const output = resolve(out);
const name = goos === 'windows' ? 'mira-tailscale.exe' : 'mira-tailscale';
const binary = join(output, name);
const run = (file, argv, options) => execFileSync(file, argv, { stdio: ['ignore', 'inherit', 'inherit'], ...options });
// What go does with these comes from here alone, not from the caller's environment; and always the Go of go.mod (its
// toolchain line, else its go line), not a newer one that happens to be installed: the same bytes on every machine.
const mod = readFileSync(join(module, 'go.mod'), 'utf8');
const toolchain = /^toolchain\s+(go\S+)/m.exec(mod)?.[1] ?? `go${/^go\s+(\d+\.\d+\.\d+)\s*$/m.exec(mod)?.[1]}`;
if (!/^go\d+\.\d+\.\d+$/.test(toolchain)) { console.error(`src/Mira.Tailscale/go.mod: an exact Go version (go 1.x.y) expected, got '${toolchain}'.`); process.exit(2); }
const host = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(GO(OS|ARCH|FLAGS|AMD64|ARM64|EXPERIMENT|TOOLCHAIN)|CGO_.*)$/.test(key)));
host.GOTOOLCHAIN = toolchain;
// Go from the PATH, or else the newest of a GitHub runner's tool cache (GOROOT_1_24_X64…), put first in the PATH of
// everything run here.
try { execFileSync('go', ['version'], { stdio: 'ignore', env: { ...host, GOTOOLCHAIN: 'local' } }); } catch {
  const found = Object.entries(process.env).map(([key, value]) => [/^GOROOT_1_(\d+)_(X64|ARM64)$/i.exec(key)?.[1], value])
    .filter(([minor, value]) => minor && existsSync(join(value, 'bin', process.platform === 'win32' ? 'go.exe' : 'go')))
    .sort(([a], [b]) => Number(b) - Number(a))[0];
  if (!found) { console.error('Go is not installed: https://go.dev/dl/ (1.21 or later, which fetches the Go of go.mod).'); process.exit(2); }
  const path = Object.keys(host).find(key => key.toUpperCase() === 'PATH') ?? 'PATH';
  host[path] = join(found[1], 'bin') + delimiter + (host[path] ?? '');
}
rmSync(output, { recursive: true, force: true });
mkdirSync(output, { recursive: true });

// What comes from Tailscale's main branch before its release (backport.mjs): the cache of the network map without a
// line in the tailnet's policy. The node is built in the folder it gives (a copy of src/Mira.Tailscale, or itself).
const work = mkdtempSync(join(tmpdir(), 'mira-tailscale-'));
const source = backport(join(work, 'module'), host);
// The file properties of the Windows executable, as an object file go build links when it sits beside main.go.
const resource = join(source.dir, 'cmd/mira-tailscale', `rsrc_windows_${goarch}.syso`);
try {
  if (goos === 'windows') {
    run('go', ['tool', 'go-winres', 'simply', '--arch', goarch, '--manifest', 'cli',
      '--product-name', 'Mira', '--product-version', version, '--file-version', version,
      '--file-description', 'Mira (Tailscale)', '--original-filename', name,
      '--copyright', 'Copyright (c) 2026 sasou-web and Mira contributors; includes Tailscale (BSD-3-Clause)',
      '--icon', join(root, 'src/Mira.Desktop/Assets/mira.ico'), '--out', join(source.dir, 'cmd/mira-tailscale', 'rsrc')], { cwd: join(here, 'winres'), env: host });
    if (!existsSync(resource)) throw new Error(`go-winres did not write ${resource}`);
  }
  run('go', ['build', '-o', binary, '-trimpath', '-buildvcs=false', '-tags', tags.join(','),
    '-ldflags', `-s -w -buildid= -X main.version=${version}`, './cmd/mira-tailscale'], {
    cwd: source.dir,
    env: { ...host, CGO_ENABLED: '0', GOOS: goos, GOARCH: goarch, GOFLAGS: source.flags.join(' '), ...(goarch === 'amd64' ? { GOAMD64: 'v1' } : {}), ...(goarch === 'arm64' ? { GOARM64: 'v8.0' } : {}) },
  });
} finally {
  rmSync(resource, { force: true });
  rmSync(work, { recursive: true, force: true });
}

// Built for this machine: its hello says the capability version backport.mjs is for (the node ends with its input).
const system = { win32: 'windows' }[process.platform] ?? process.platform;
if (goos === system && goarch === ({ x64: 'amd64' }[process.arch] ?? process.arch)) {
  const hello = JSON.parse(execFileSync(binary, [], { input: '', encoding: 'utf8', timeout: 30000 }).split('\n')[0]);
  if (!(hello.capabilityVersion >= 149)) { console.error(`${binary}: capability version ${hello.capabilityVersion}, 149 or later expected (backport.mjs).`); process.exit(1); }
}

// Mira checks the executable by its SHA-256 with the PE checksum and certificate entry read as zeros and without a
// signature at its end (TailnetExecutable.Digest): true of a signed copy only if the build has those zeros, and a
// length a signature is appended to without padding.
if (goos === 'windows') {
  const data = readFileSync(binary);
  const pe = data.readUInt32LE(0x3c);
  const optional = pe + 24;
  const certificates = optional + (data.readUInt16LE(optional) === 0x20b ? 112 : 96) + 4 * 8;
  if (data.readUInt32LE(optional + 64) !== 0 || data.readUInt32LE(certificates) !== 0 || data.readUInt32LE(certificates + 4) !== 0 || data.length % 8 !== 0) {
    console.error(`${binary}: PE checksum or certificate entry not zero, or a length not a multiple of 8: TailnetExecutable.Digest needs them.`);
    process.exit(1);
  }
}

// The licences of what it links, read from the binary itself (run where go.mod picks the same Go as the build).
run(process.execPath, [join(here, 'licenses.mjs'), binary, join(output, 'licenses')], { cwd: module, env: host });
const sha256 = createHash('sha256').update(readFileSync(binary)).digest('hex');
writeFileSync(`${binary}.sha256`, `${sha256}  ${name}\n`);
console.log(`${binary}: ${(statSync(binary).size / 1e6).toFixed(1)} MB, version ${version}, SHA-256 ${sha256}`);
