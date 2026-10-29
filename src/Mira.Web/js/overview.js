// A title's summary as plain text: Jellyfin passes on the HTML of some metadata providers (AniList's « <br> »), which
// a text node would show as is. As OverviewText of Mira.Core and of the iPhone app (cases shared in
// tests/fixtures/overview.json). Imported by Node's tests: nothing of the browser here.

const NAMED = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ', hellip: '…', mdash: '—', ndash: '–',
  laquo: '«', raquo: '»', lsquo: '‘', rsquo: '’', ldquo: '“', rdquo: '”', bull: '•', middot: '·', copy: '©',
  reg: '®', trade: '™', deg: '°', times: '×', euro: '€', shy: '',
  agrave: 'à', aacute: 'á', acirc: 'â', auml: 'ä', ccedil: 'ç', egrave: 'è', eacute: 'é', ecirc: 'ê', euml: 'ë',
  icirc: 'î', iuml: 'ï', ocirc: 'ô', ouml: 'ö', ugrave: 'ù', ucirc: 'û', uuml: 'ü', oelig: 'œ',
  Agrave: 'À', Eacute: 'É', Egrave: 'È', Ecirc: 'Ê', Ccedil: 'Ç', OElig: 'Œ',
};

function decode(text) {
  return text.replace(/&(#\d+|#x[0-9a-f]+|[a-z]+);/gi, (whole, name) => {
    if (name[0] === '#') {
      const code = name[1] === 'x' || name[1] === 'X' ? parseInt(name.slice(2), 16) : parseInt(name.slice(1), 10);
      return code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : whole;
    }
    return NAMED[name] ?? whole;
  });
}

/**
 * Jellyfin's summary in plain text: <br> = a new line, a paragraph (or two <br>) = an empty line, other tags taken
 * out, entities read, spaces and empty lines collapsed. flat: on one line (a summary cut at two lines).
 */
export function plainOverview(text, { flat = false } = {}) {
  if (!text) return '';
  let s = String(text).replace(/\r\n?/g, '\n')
    .replace(/<br\s*\/?>/gi, '\n')
    .replace(/<\/?(?:p|div|ul|ol|li|h[1-6]|blockquote)\b[^>]*>/gi, '\n\n')
    .replace(/<\/?[a-z][^>]*>/gi, '');
  s = decode(s).split('\n').map((line) => line.replace(/[ \t]+/g, ' ').replace(/^ | $/g, '')).join('\n')
    .replace(/\n{3,}/g, '\n\n').replace(/^\n+|\n+$/g, '');
  return flat ? s.replace(/\n+/g, ' ') : s;
}
