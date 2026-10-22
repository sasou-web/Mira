// The showcase site's icons (site/img/icons.svg): the Phosphor paths of Mira web, as one sprite of <symbol>.
// node tools/site/icons.mjs
import { writeFileSync } from 'node:fs';
import { icons } from '../../src/Mira.Web/js/icons.js';

const names = ['download', 'phone', 'users', 'server', 'check', 'play', 'right', 'qr', 'add-home', 'subtitles', 'history', 'link', 'films', 'search', 'close'];
const symbols = names.map(name => `  <symbol id="${name}" viewBox="0 0 256 256"><path fill="currentColor" d="${icons[name]}"/></symbol>`);
writeFileSync('site/img/icons.svg', `<svg xmlns="http://www.w3.org/2000/svg">\n  <!-- Phosphor Icons (MIT), from src/Mira.Web/js/icons.js by tools/site/icons.mjs -->\n${symbols.join('\n')}\n</svg>\n`);
