// The release Mira puts forward on « Télécharger sur le PC serveur » (src/Mira.Web/js/releases.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { setLanguage } from '../../../src/Mira.Web/js/i18n.js';
import { parseRelease, qualityLine, episodeLabel, groupReleases, releaseScore, titleKey, warning, readableName } from '../../../src/Mira.Web/js/releases.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/releases.json', import.meta.url), 'utf8'));

test('what a release name says (tests/fixtures/releases.json)', () => {
  for (const [name, title, year, label, quality] of fixture.names) {
    const release = parseRelease(name);
    assert.deepEqual([release.title, release.year, episodeLabel(release), qualityLine(release)], [title, year, label, quality], name);
  }
  assert.equal(parseRelease('[ToonsHub] JoJos Bizarre Adventure S06E04 1080p NF WEB-DL (JoJo no Kimyou na Bouken: Steel Ball Run - 2nd - 3rd STAGE, Dual-Audio)').alternate, 'JoJo no Kimyou na Bouken: Steel Ball Run');
  assert.equal(parseRelease('Dune (2021) [1080p bluray]').alternate, null, 'a year is no other name');
  assert.deepEqual(parseRelease('Film.2020.1080p.WEB-DL.MultiSub.x264').languages, [], 'subtitles in many languages are no MULTI');
  assert.equal(titleKey('JoJo’s Bizarre Adventure: Éclat & Co'), 'jojos bizarre adventure eclat and co');
  assert.equal(parseRelease(null).title, '');
});

test('a download called by its title, its name under it (tests/fixtures/releases.json)', () => {
  for (const [name, title, full] of fixture.readable) assert.deepEqual(readableName(name), { title, full }, name);
});

test('the best release first, by title and episode (tests/fixtures/releases.json)', () => {
  for (const { category, hits, groups } of fixture.ranked) {
    const found = groupReleases(hits.map(([id, name, size, seeders, source, kind]) => ({ id, name, size, seeders, source, ...(kind ? { category: kind } : {}) })), category);
    assert.deepEqual(found.map((g, i) => [g.title, g.label, g.best.hit.id, g.others.map((x) => x.hit.id), ...(groups[i]?.length > 4 ? [g.category] : [])]), groups, category);
  }
});

test('seeders only break ties, and a source without them is not dead', () => {
  const hit = (name, seeders, source = '1337x') => ({ name, seeders, size: 3 * 2 ** 30, source });
  assert.ok(releaseScore(hit('Film.2020.1080p.BluRay.x264-A', 5)) > releaseScore(hit('Film.2020.1080p.WEBRip.x264-B', 5000)));
  assert.ok(releaseScore(hit('[SubsPlease] Show - 05 (1080p)', 0, 'subsplease')) > releaseScore(hit('[SubsPlease] Show - 05 (1080p)', 0)));
  assert.equal(warning({ release: parseRelease('[SubsPlease] Show - 05 (1080p)'), hit: hit('', 0, 'subsplease') }), '');
  assert.match(warning({ release: parseRelease('Film.2020.1080p.BluRay'), hit: hit('', 1) }), /Peu de sources/);
  assert.match(warning({ release: parseRelease('Film.2020.HDCAM'), hit: hit('', 900) }), /salle de cinéma/);
  const [a, b] = groupReleases([hit('Film.2020.1080p.WEB-DL-A', 10), hit('Film.2020.1080p.WEB-DL-B', 90)]);
  assert.equal(a.best.hit.name, 'Film.2020.1080p.WEB-DL-B', 'equal releases: the most shared');
  assert.equal(b, undefined);
});

test('labels and warnings in English, the release\'s own tags as they are', () => {
  try {
    setLanguage('en');
    assert.equal(episodeLabel(parseRelease('Show.S02.1080p.WEB-DL.x264-GRP')), 'Complete season 2');
    assert.equal(episodeLabel(parseRelease('[Erai-raws] Show - 01 ~ 12 [1080p]')), 'Episodes 1–12');
    assert.equal(episodeLabel(parseRelease('[SubsPlease] Show - 05 (1080p)')), 'Episode 5');
    assert.equal(episodeLabel(parseRelease('Show.S06E05.1080p.WEB-DL')), 'S06E05');
    assert.equal(qualityLine(parseRelease('Film.2020.HDCAM.x264')), 'Theater cam · H.264');
    assert.equal(qualityLine(parseRelease('Film.2020.MULTI.VFF.1080p.BluRay.x265-GRP')), '1080p · BluRay · HEVC · MULTI · VFF');
    assert.equal(qualityLine(parseRelease('[Erai-raws] Show - 05 [1080p]')), '1080p · Erai-raws');
    assert.equal(readableName('Film.2020.1080p.BluRay').title, 'Film (2020)');
    assert.equal(readableName('Show.S02.1080p.WEB-DL.x264-GRP').title, 'Show · Complete season 2');
    assert.equal(warning({ release: parseRelease('Film.2020.1080p.BluRay'), hit: { seeders: 0, source: '1337x' } }), 'Nobody is sharing this file, so it probably won’t download.');
  } finally { setLanguage('fr'); }
});
