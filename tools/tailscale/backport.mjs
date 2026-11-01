#!/usr/bin/env node
// What Mira's Tailscale node takes from Tailscale's main branch before it is in a release. The node is then built from
// a copy of src/Mira.Tailscale with its modules vendored (go mod vendor) and the one line below changed in the copy of
// tailscale.com; nothing else of the module changes, and the repository is never touched.
//
// Capability version 149 (tailscale/tailscale main, tailcfg.go, 2026-10-07: « Sentinel version to request the server
// send back NodeAttrCacheNetworkMaps set to enabled »): a node that says 149 gets « cache-network-maps » from Tailscale
// without a line in the tailnet's policy, so Built-in Tailscale starts from its saved network map during an outage of
// Tailscale's coordination server, with nothing to paste. 149 changes nothing else in the client (main's client code
// for the cache is 1.104.4's), and a policy's « disable-cache-network-maps » still wins. Nothing more is sent to
// Tailscale: the number was already in every map request. Gone by itself once go.mod names a release at 149 or later.
//   node tools/tailscale/backport.mjs <empty folder>   → prints the folder to build in, then its go flags (one line each)
import { execFileSync } from 'node:child_process';
import { cpSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const module = resolve(dirname(fileURLToPath(import.meta.url)), '../../src/Mira.Tailscale');
const wanted = 149;
const declaration = /^const CurrentCapabilityVersion CapabilityVersion = (\d+)$/m;

// Where to build the node and with which go flags: src/Mira.Tailscale itself when its tailscale.com already says
// `wanted`, else the patched copy written in folder.
export function backport(folder, env = process.env) {
  const go = (cwd, ...argv) => execFileSync('go', argv, { cwd, env: { ...env, GOFLAGS: '-mod=readonly' }, encoding: 'utf8' }).trim();
  const dir = go(module, 'list', '-m', '-f', '{{.Dir}}', 'tailscale.com');
  const current = capability(join(dir, 'tailcfg', 'tailcfg.go'));
  if (current >= wanted) return { dir: module, flags: ['-mod=readonly'] };
  if (current !== wanted - 1) throw new Error(`tailscale.com: capability version ${current}, ${wanted - 1} expected: see tools/tailscale/backport.mjs.`);
  const copy = resolve(folder);
  cpSync(module, copy, { recursive: true, filter: source => !/\.syso$|[\\/]vendor$/.test(source) });
  go(copy, 'mod', 'vendor');
  const file = join(copy, 'vendor', 'tailscale.com', 'tailcfg', 'tailcfg.go');
  if (capability(file) !== current) throw new Error(`${file}: not the module's copy.`);
  writeFileSync(file, readFileSync(file, 'utf8').replace(declaration, `const CurrentCapabilityVersion CapabilityVersion = ${wanted}`));
  if (capability(file) !== wanted) throw new Error(`${file}: not changed.`);
  return { dir: copy, flags: ['-mod=vendor'] };
}

function capability(file) {
  const found = Number(declaration.exec(readFileSync(file, 'utf8'))?.[1]);
  if (!found) throw new Error(`${file}: CurrentCapabilityVersion not found: see what tools/tailscale/backport.mjs changes.`);
  return found;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (!process.argv[2]) { console.error('usage: node tools/tailscale/backport.mjs <empty folder>'); process.exit(2); }
  const { dir, flags } = backport(process.argv[2]);
  console.log(`${dir}\n${flags.join(' ')}`);
}
