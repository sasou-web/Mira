// The showcase site as GitHub Pages serves it: site/ copied to dist/site, with every __VERSION__ of its pages set to
// the published release its download links point at. node tools/site/build.mjs <x.y.z> [out]
import { cpSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [version, out = 'dist/site'] = process.argv.slice(2);
if (!/^\d+\.\d+\.\d+$/.test(version ?? '')) {
  console.error('Usage: node tools/site/build.mjs <x.y.z> [out]');
  process.exit(1);
}
rmSync(out, { recursive: true, force: true });
cpSync('site', out, { recursive: true });
for (const entry of readdirSync(out, { recursive: true })) {
  if (!entry.endsWith('.html')) continue;
  const path = join(out, entry);
  writeFileSync(path, readFileSync(path, 'utf8').replaceAll('__VERSION__', version));
}
const left = readdirSync(out, { recursive: true }).filter(entry => /\.(html|xml|txt)$/.test(entry)
  && readFileSync(join(out, entry), 'utf8').includes('__VERSION__'));
if (left.length) {
  console.error(`__VERSION__ left in ${left.join(', ')}`);
  process.exit(1);
}
console.log(`${out}: Mira ${version}`);
