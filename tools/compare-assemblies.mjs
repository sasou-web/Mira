#!/usr/bin/env node
// Compares two builds of the same .NET assembly (Mira.dll, Mira.Core.dll) part by part, to tell a build made on one
// PC from the same source compiled elsewhere (the CI, a clean machine): a byte-by-byte diff is useless here, since a
// single string of another length shifts everything after it.
//   node tools/compare-assemblies.mjs <one.dll> <other.dll>
// Exit code 0 when the method bodies and the embedded resources are the same, 1 otherwise, 2 on an unreadable file.
// What two honest builds of one commit still differ by: the MVID (#GUID), the versions of the SDK's tools in #Blob
// (PresentationBuildTasks, the regex generator), the regex generator's hash in #Strings, resource names written with
// the path separator of the building OS, and the line endings of a resource git does not normalise.
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

function read(path) {
  const data = readFileSync(path);
  const peOffset = data.readUInt32LE(0x3c);
  if (data.readUInt32LE(peOffset) !== 0x4550) throw new Error(`${path}: not a PE file`);
  const sectionCount = data.readUInt16LE(peOffset + 6);
  const optionalSize = data.readUInt16LE(peOffset + 20);
  const optional = peOffset + 24;
  const pe32Plus = data.readUInt16LE(optional) === 0x20b;
  const directories = optional + (pe32Plus ? 112 : 96);
  const sections = [];
  let at = optional + optionalSize;
  for (let i = 0; i < sectionCount; i++, at += 40) {
    sections.push({
      name: data.subarray(at, at + 8).toString('latin1').replace(/\0+$/, ''),
      virtualSize: data.readUInt32LE(at + 8), virtualAddress: data.readUInt32LE(at + 12),
      rawSize: data.readUInt32LE(at + 16), rawPointer: data.readUInt32LE(at + 20),
    });
  }
  const offset = rva => {
    for (const s of sections) if (rva >= s.virtualAddress && rva < s.virtualAddress + Math.max(s.virtualSize, s.rawSize)) return s.rawPointer + rva - s.virtualAddress;
    throw new Error(`${path}: RVA ${rva.toString(16)} in no section`);
  };
  const cliRva = data.readUInt32LE(directories + 14 * 8);
  if (cliRva === 0) throw new Error(`${path}: not a .NET assembly`);
  const cli = offset(cliRva);
  const cliSize = data.readUInt32LE(cli);
  const metadata = offset(data.readUInt32LE(cli + 8)); const metadataSize = data.readUInt32LE(cli + 12);
  const resourcesRva = data.readUInt32LE(cli + 24); const resourcesSize = data.readUInt32LE(cli + 28);
  // Method bodies: between the CLI header and the metadata (Roslyn's layout).
  const code = data.subarray(cli + cliSize, metadata);
  // Metadata streams.
  if (data.readUInt32LE(metadata) !== 0x424a5342) throw new Error(`${path}: metadata signature missing`);
  const versionLength = data.readUInt32LE(metadata + 12);
  let p = metadata + 16 + versionLength + 2;
  const streamCount = data.readUInt16LE(p); p += 2;
  const streams = {};
  for (let i = 0; i < streamCount; i++) {
    const streamOffset = data.readUInt32LE(p); const streamSize = data.readUInt32LE(p + 4); p += 8;
    let name = ''; while (data[p] !== 0) name += String.fromCharCode(data[p++]);
    p = (p + 4) & ~3;
    streams[name] = data.subarray(metadata + streamOffset, metadata + streamOffset + streamSize);
  }
  // Managed resources: 4-byte length, bytes, each aligned to 8; named by the ManifestResource table.
  const names = resourceNames(streams);
  const resources = new Map();
  if (resourcesSize > 0) {
    const start = offset(resourcesRva); let r = start; const end = start + resourcesSize;
    while (r + 4 <= end) {
      const n = data.readUInt32LE(r);
      // Names carry the path separator of the OS that built: one spelling here.
      resources.set((names?.get(r - start) ?? `resource at ${r - start}`).replace(/\\/g, '/'), data.subarray(r + 4, r + 4 + n));
      r = start + ((r - start + 4 + n + 7) & ~7);
    }
  }
  return { path, size: data.length, code, metadataSize, streams, resources };
}

/**
 * The ManifestResource table of the #~ stream: offset in the resources blob → name (ECMA-335 II.22). Row sizes of
 * every earlier table come from its row count and the index sizes; null when the tables are not the ones of a DLL.
 */
function resourceNames(streams) {
  const tables = streams['#~']; const stringsHeap = streams['#Strings'];
  if (!tables || !stringsHeap) return null;
  const heapSizes = tables[6];
  const stringSize = heapSizes & 1 ? 4 : 2, guidSize = heapSizes & 2 ? 4 : 2, blobSize = heapSizes & 4 ? 4 : 2;
  const valid = tables.readBigUInt64LE(8);
  const rows = new Array(64).fill(0); let p = 24;
  for (let i = 0; i < 64; i++) if (valid & (1n << BigInt(i))) { rows[i] = tables.readUInt32LE(p); p += 4; }
  if (valid >> 45n) return null; // Portable PDB tables: not an assembly's own metadata.
  const index = table => rows[table] < 0x10000 ? 2 : 4;
  const coded = (bits, members) => Math.max(...members.map(t => rows[t])) < 1 << (16 - bits) ? 2 : 4;
  const S = stringSize, G = guidSize, B = blobSize;
  const TypeDefOrRef = coded(2, [2, 1, 0x1b]), HasConstant = coded(2, [4, 8, 0x17]);
  const HasCustomAttribute = coded(5, [6, 4, 1, 2, 8, 9, 0xa, 0, 0xe, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b]);
  const HasFieldMarshal = coded(1, [4, 8]), HasDeclSecurity = coded(2, [2, 6, 0x20]), MemberRefParent = coded(3, [2, 1, 0x1a, 6, 0x1b]);
  const HasSemantics = coded(1, [0x14, 0x17]), MethodDefOrRef = coded(1, [6, 0xa]), MemberForwarded = coded(1, [4, 6]);
  const Implementation = coded(2, [0x26, 0x23, 0x27]), CustomAttributeType = coded(3, [6, 0xa]), ResolutionScope = coded(2, [0, 0x1a, 0x23, 1]);
  const TypeOrMethodDef = coded(1, [2, 6]);
  const rowSize = [
    2 + S + G + G + G, ResolutionScope + S + S, 4 + S + S + TypeDefOrRef + index(4) + index(6), index(4), 2 + S + B, index(6),
    4 + 2 + 2 + S + B + index(8), index(8), 2 + 2 + S, index(2) + TypeDefOrRef, MemberRefParent + S + B, 1 + 1 + HasConstant + B,
    HasCustomAttribute + CustomAttributeType + B, HasFieldMarshal + B, 2 + HasDeclSecurity + B, 2 + 4 + index(2), 4 + index(4), B,
    index(2) + index(0x14), index(0x14), 2 + S + TypeDefOrRef, index(2) + index(0x17), index(0x17), 2 + S + B,
    2 + index(6) + HasSemantics, index(2) + MethodDefOrRef + MethodDefOrRef, S, B, 2 + MemberForwarded + S + index(0x1a),
    4 + index(4), 4 + 4, 4, 4 + 2 + 2 + 2 + 2 + 4 + B + S + S, 4, 4 + 4 + 4, 2 + 2 + 2 + 2 + 4 + B + S + S + B, 4 + index(0x23),
    4 + 4 + 4 + index(0x23), 4 + S + B, 4 + 4 + S + S + Implementation, 4 + 4 + S + Implementation, index(2) + index(2),
    2 + 2 + TypeOrMethodDef + S, MethodDefOrRef + B, index(0x2a) + TypeDefOrRef,
  ];
  for (let t = 0; t < 0x28; t++) p += rows[t] * rowSize[t];
  const names = new Map();
  for (let i = 0; i < rows[0x28]; i++, p += rowSize[0x28]) {
    const at = tables.readUInt32LE(p); const name = S === 4 ? tables.readUInt32LE(p + 8) : tables.readUInt16LE(p + 8);
    let end = name; while (stringsHeap[end] !== 0) end++;
    names.set(at, stringsHeap.subarray(name, end).toString('utf8'));
  }
  return names;
}

const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const short = bytes => sha(bytes).slice(0, 12);

/** The entries of a .resources blob (ResourceReader format): name → hash of its bytes; null for another format. */
function resourceEntries(blob) {
  if (blob.length < 12 || blob.readUInt32LE(0) !== 0xbeefcace) return null;
  let p = 4; p += 4; const headerSize = blob.readUInt32LE(p); p += 4 + headerSize;
  p += 4; const count = blob.readUInt32LE(p); p += 4; const typeCount = blob.readUInt32LE(p); p += 4;
  const string7 = () => {
    let n = 0, shift = 0, b;
    do { b = blob[p++]; n |= (b & 0x7f) << shift; shift += 7; } while (b & 0x80);
    const s = blob.subarray(p, p + n); p += n; return s;
  };
  for (let i = 0; i < typeCount; i++) string7();
  p = (p + 7) & ~7;
  p += 4 * count; const namePositions = []; for (let i = 0; i < count; i++) { namePositions.push(blob.readUInt32LE(p)); p += 4; }
  const dataSection = blob.readUInt32LE(p); p += 4;
  const names = p; const items = [];
  for (let i = 0; i < count; i++) { p = names + namePositions[i]; const name = string7().toString('utf16le'); items.push({ name, at: blob.readInt32LE(p) }); }
  items.sort((a, b) => a.at - b.at);
  const entries = new Map();
  items.forEach((item, i) => { const end = i + 1 < items.length ? dataSection + items[i + 1].at : blob.length; entries.set(item.name, short(blob.subarray(dataSection + item.at, end))); });
  return entries;
}

/** The blobs of a #Blob heap, each with its compressed length. */
function blobs(heap) {
  const out = []; let i = 0;
  while (i < heap.length) {
    const b = heap[i]; let n;
    if (b < 0x80) { n = b; i += 1; } else if (b < 0xc0) { n = ((b & 0x3f) << 8) | heap[i + 1]; i += 2; } else { n = ((b & 0x1f) << 24) | (heap[i + 1] << 16) | (heap[i + 2] << 8) | heap[i + 3]; i += 4; }
    out.push(heap.subarray(i, i + n).toString('latin1')); i += n;
  }
  return out;
}
const multisetDifference = (a, b) => { const counts = new Map(); for (const x of b) counts.set(x, (counts.get(x) ?? 0) + 1); return a.filter(x => { const c = counts.get(x) ?? 0; if (c > 0) { counts.set(x, c - 1); return false; } return true; }); };
const printable = s => s.replace(/[^\x20-\x7e]/g, '.').slice(0, 100);

if (process.argv.length !== 4) { console.error('node tools/compare-assemblies.mjs <one.dll> <other.dll>'); process.exit(2); }
let a, b;
try { a = read(process.argv[2]); b = read(process.argv[3]); } catch (error) { console.error(error.message); process.exit(2); }
let same = true;
console.log(`A ${a.path}: ${a.size} bytes, sha256 ${sha(readFileSync(a.path))}`);
console.log(`B ${b.path}: ${b.size} bytes, sha256 ${sha(readFileSync(b.path))}`);

const codeSame = a.code.equals(b.code);
console.log(`Method bodies (IL): ${a.code.length} and ${b.code.length} bytes, ${codeSame ? 'identical' : 'DIFFERENT'}`);
if (!codeSame) same = false;

for (const name of new Set([...Object.keys(a.streams), ...Object.keys(b.streams)])) {
  const x = a.streams[name], y = b.streams[name];
  if (!x || !y) { console.log(`${name}: only in ${x ? 'A' : 'B'}`); same = false; continue; }
  if (x.equals(y)) { console.log(`${name}: identical (${x.length} bytes)`); continue; }
  if (name === '#GUID') { console.log('#GUID: differs (the MVID: expected between two builds)'); continue; }
  if (name === '#Strings' || name === '#US') {
    const split = h => name === '#Strings' ? h.toString('latin1').split('\0') : [h.toString('latin1')];
    const onlyA = multisetDifference(split(x), split(y)), onlyB = multisetDifference(split(y), split(x));
    if (name === '#US') { console.log(`#US (string literals): differ (${x.length} and ${y.length} bytes)`); same = false; continue; }
    console.log(`#Strings (names): ${onlyA.length} only in A, ${onlyB.length} only in B`);
    for (const s of onlyA.slice(0, 12)) console.log(`  A: ${printable(s)}`);
    for (const s of onlyB.slice(0, 12)) console.log(`  B: ${printable(s)}`);
    const benign = s => /^<RegexGenerator_g>/.test(s) || /[\\/]/.test(s);
    if (![...onlyA, ...onlyB].every(benign)) same = false;
    continue;
  }
  if (name === '#Blob') {
    const onlyA = multisetDifference(blobs(x), blobs(y)), onlyB = multisetDifference(blobs(y), blobs(x));
    console.log(`#Blob (signatures, attribute values): ${onlyA.length} only in A, ${onlyB.length} only in B`);
    for (const s of onlyA.slice(0, 12)) console.log(`  A: ${printable(s)}`);
    for (const s of onlyB.slice(0, 12)) console.log(`  B: ${printable(s)}`);
    const benign = s => s === '' || /PresentationBuildTasks|RegularExpressions\.Generator/.test(s);
    if (![...onlyA, ...onlyB].every(benign)) same = false;
    continue;
  }
  // #~ (tables): rows index the heaps, so every row after a longer string shifts. Judged by the other parts.
  console.log(`${name}: differs (${x.length} and ${y.length} bytes${name === '#~' ? ', indexes into the heaps above: judged by them' : ''})`);
  if (name !== '#~') same = false;
}

console.log(`Embedded resources: ${a.resources.size} in A, ${b.resources.size} in B`);
for (const name of new Set([...a.resources.keys(), ...b.resources.keys()])) {
  const x = a.resources.get(name), y = b.resources.get(name);
  if (!x || !y) { console.log(`  ${name}: only in ${x ? 'A' : 'B'}`); same = false; continue; }
  if (x.equals(y)) { console.log(`  ${name}: identical (${x.length} bytes)`); continue; }
  const ex = resourceEntries(x), ey = resourceEntries(y);
  if (ex && ey) {
    const entries = new Set([...ex.keys(), ...ey.keys()]); const differing = [...entries].filter(n => ex.get(n) !== ey.get(n));
    console.log(`  ${name}: ${ex.size} and ${ey.size} entries, ${differing.length} differing: ${differing.slice(0, 8).join(', ')}`);
  } else {
    const crlf = x.toString('latin1').replace(/\r\n/g, '\n') === y.toString('latin1').replace(/\r\n/g, '\n');
    console.log(`  ${name}: DIFFERENT (${x.length} and ${y.length} bytes${crlf ? ', the same text with other line endings' : ''})`);
    if (crlf) continue;
  }
  same = false;
}
console.log(same ? 'Same code and resources: the two builds come from the same source.' : 'The two builds differ beyond what two builds of one commit differ by.');
process.exit(same ? 0 : 1);
