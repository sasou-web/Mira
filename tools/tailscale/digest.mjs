// The SHA-256 by which Mira for Windows knows mira-tailscale.exe (TailnetExecutable.Digest): the same for the file as
// tools/tailscale/build.mjs makes it and for a copy signed later, whose Authenticode signature changes only the PE
// checksum, the certificate table's entry and what follows the image. For a file that is not signed, it is its SHA-256.
//
// Usage: node tools/tailscale/digest.mjs <file> [<other file>]
//   prints each file's digest; with two files, exits with 1 when they differ.
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function digest(bytes) {
    const data = Buffer.from(bytes);
    const read = Math.min(data.length, 4096);
    const pe = read >= 0x40 ? data.readInt32LE(0x3c) : -1;
    if (!(pe > 0 && pe + 24 + 112 + 5 * 8 <= read && data.toString('latin1', pe, pe + 4) === 'PE\0\0')) return sha256(data);
    const optional = pe + 24;
    const certificates = optional + (data.readUInt16LE(optional) === 0x20b ? 112 : 96) + 4 * 8;
    const start = data.readUInt32LE(certificates), size = data.readUInt32LE(certificates + 4);
    const end = size > 0 && start >= certificates + 8 && start <= data.length ? start : data.length;
    const image = Buffer.from(data.subarray(0, end));
    image.fill(0, Math.min(optional + 64, end), Math.min(optional + 68, end));
    image.fill(0, Math.min(certificates, end), Math.min(certificates + 8, end));
    return sha256(image);
}

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const files = process.argv.slice(2);
    if (files.length < 1 || files.length > 2) {
        console.error('Usage: node tools/tailscale/digest.mjs <file> [<other file>]');
        process.exit(2);
    }
    const digests = files.map(file => {
        try { return digest(readFileSync(file)); }
        catch (error) { console.error(`${file}: ${error.code === 'ENOENT' ? 'missing' : error.message}`); process.exit(1); }
    });
    files.forEach((file, i) => console.log(`${digests[i]}  ${file}`));
    if (digests.length === 2 && digests[0] !== digests[1]) {
        console.error('Different programs: these files are not the same build, signed or not.');
        process.exit(1);
    }
}
