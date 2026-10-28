// Every text of Mira for iPhone has its French: reads the French export Xcode makes from the code and the string
// catalogs (xcodebuild -exportLocalizations -exportLanguage fr) and fails on any text left without a translation.
//   node tools/ios/strings.mjs <folder of the export>
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const folder = process.argv[2];
if (!folder) {
  console.error('Usage: node tools/ios/strings.mjs <folder of the export>');
  process.exit(1);
}
const files = readdirSync(folder, { recursive: true }).filter((x) => x.endsWith('.xliff')).map((x) => join(folder, x));
if (!files.length) {
  console.error(`No .xliff in ${folder}`);
  process.exit(1);
}
const decode = (text) => text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
let units = 0;
const missing = [];
for (const file of files) {
  const xliff = readFileSync(file, 'utf8');
  for (const unit of xliff.matchAll(/<trans-unit\b([^>]*)>([\s\S]*?)<\/trans-unit>/g)) {
    units++;
    const id = decode(/\bid="([^"]*)"/.exec(unit[1])?.[1] ?? '');
    const target = /<target\b([^>]*)>([\s\S]*?)<\/target>/.exec(unit[2]);
    const state = /\bstate="([^"]*)"/.exec(target?.[1] ?? '')?.[1] ?? '';
    if (!target || !target[2].trim() || ['new', 'needs-translation'].includes(state)) missing.push(id);
  }
}
if (!units) {
  console.error('The export holds no text: was it made from the project?');
  process.exit(1);
}
if (missing.length) {
  console.error(`${missing.length} text(s) without French (add them to src/Mira.iOS/Mira/Resources/Localizable.xcstrings):`);
  for (const id of missing) console.error(`  ${id}`);
  process.exit(1);
}
console.log(`${units} texts, all in French.`);
