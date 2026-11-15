// tools/banner/plan.mjs: scenes.json's format, its refusals, the sentence, the band's geometry, the film of a version.
//   node --test tools/banner/plan.test.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { BAND, FILMS, SHOTS, before, filmOf, longDate, plan, sentence } from './plan.mjs';

const point = (title) => ({ icon: 'info', title, en: { title } });
const news = [
  { version: '0.9.8', date: '2026-11-20', summary: 'Résumé', en: { summary: 'Downloads from the title page, a smoother scroll.' }, items: [point('Get the missing episodes'), point('Smoother scrolling'), point('Fixes')] },
  { version: '0.9.7', date: '2026-11-13', summary: 'Résumé', en: { summary: 'Settings in a window, episodes in a grid.' }, items: [point('Settings in a window'), point('Episodes in a grid')] },
  { version: '1.0.0', date: '2026-12-01', summary: 'Résumé', en: { summary: 'Mira 1.0' }, items: [point('One')] },
];
const piece = (over = {}) => ({ point: 1, shot: 'windows-series', at: [93, 86], scale: 0.578, width: 536, ...over });
const three = [piece(), piece({ point: 2, shot: 'mac-home', at: [0, 0], scale: 0.5, width: 268 }), piece({ point: 3, shot: 'iphone-film', at: [0, 0], scale: 0.62, width: 252 })];
// The release before: its film and its band (a piece no other test cuts again).
const earlier = { film: 'spider-verse', band: [{ point: 1, shot: 'mac-settings', at: [0, 0], scale: 0.5, width: 400 }] };
const scenes = (entry, version = '0.9.8') => ({ [version]: entry, '0.9.7': earlier });
const planned = (entry, version = '0.9.8') => plan(version, { scenes: scenes(entry, version), news });

test('the repository: every entry of scenes.json plans, the newest version of WhatsNew.json has its own', () => {
  const repo = JSON.parse(readFileSync(new URL('./scenes.json', import.meta.url), 'utf8'));
  const releases = JSON.parse(readFileSync(new URL('../../src/Mira.Core/WhatsNew.json', import.meta.url), 'utf8'));
  for (const version of Object.keys(repo)) {
    const p = plan(version);
    if (p.template === 'film') assert.ok(p.band.length >= 1 && p.band.every((x) => x.caption), `${version}: a caption on every piece`);
  }
  assert.ok(repo[releases[0].version], `tools/banner/scenes.json has no entry for ${releases[0].version}, the newest version of WhatsNew.json: every release has its own banner (scenes.json, then banner.yml on the preparation branch)`);
  // Its scene is checked against the release before's: keep that entry.
  assert.ok(releases.length < 2 || repo[releases[1].version], `keep ${releases[1]?.version}'s entry in scenes.json: ${releases[0].version}'s banner is checked against it`);
  assert.deepEqual(plan('0.9.5').shots, ['mac-home', 'windows-films', 'iphone-film']);
  assert.equal(plan('0.9.8').iphone, false);
  assert.equal(plan('0.9.5').iphone, true);
});

test('a patch takes « film », x.y.0 « lineup », and the entry can force either', () => {
  assert.equal(planned({ band: three }).template, 'film');
  const l = plan('1.0.0', { scenes: { '1.0.0': {} }, news });
  assert.equal(l.template, 'lineup');
  assert.deepEqual(l.lineup, { lead: 'windows-home', back: 'mac-films', phone: 'iphone-home', focus: null });
  assert.deepEqual([l.windows, l.mac, l.iphone], [true, true, true]);
  assert.equal(plan('1.0.0', { scenes: { '1.0.0': { template: 'film' } }, news }).template, 'film');
  assert.throws(() => planned({ template: 'changes' }), /unknown template changes/);
  assert.throws(() => plan('1.0.0', { scenes: { '1.0.0': { lineup: { lead: 'windows-trailer' } } }, news }), /no gallery takes windows-trailer/);
});

test('the sentence is cut at the first comma, the comma kept in the bold part', () => {
  assert.deepEqual(sentence('Settings in a window, episodes in a grid, true black.'), { bold: 'Settings in a window,', rest: 'episodes in a grid, true black.' });
  assert.deepEqual(sentence('Mira 1.0'), { bold: 'Mira 1.0', rest: '' });
  assert.deepEqual(planned({ band: three }).sentence, { bold: 'Downloads from the title page,', rest: 'a smoother scroll.' });
});

test('the date in words', () => {
  assert.equal(longDate('2026-11-13'), 'November 13, 2026');
  assert.equal(longDate('2027-01-01'), 'January 1, 2027');
  assert.equal(planned({ band: three }).date, 'November 20, 2026');
});

test('the band: pieces side by side from x = 56, 16 apart, captions from the points or by hand', () => {
  const p = planned({ band: [three[0], { ...three[1], point: undefined, caption: 'By hand' }, three[2]] });
  assert.deepEqual(p.band.map((x) => x.left), [56, 608, 892]);
  assert.deepEqual(p.band.map((x) => x.caption), ['Get the missing episodes', 'By hand', 'Fixes']);
  assert.deepEqual(p.band.map((x) => x.units), [2, 2, 3]);
  assert.equal(p.band.at(-1).left + p.band.at(-1).width, BAND.right);
  assert.deepEqual(p.shots, ['windows-series', 'mac-home', 'iphone-film']);
});

test('no band: Mira for Windows\' Home, whole, without a caption', () => {
  const p = planned({});
  assert.deepEqual(p.band.map(({ shot, at, scale, width, left, caption }) => ({ shot, at, scale, width, left, caption })),
    [{ shot: 'windows-home', at: [0, 0], scale: 0.756, width: 1088, left: 56, caption: null }]);
  assert.deepEqual([p.windows, p.mac, p.iphone], [true, false, false]);
});

test('the band\'s refusals say what to change', () => {
  assert.throws(() => planned({ band: [piece({ width: 200 }), piece({ width: 200 }), piece({ width: 200 }), piece({ width: 200 }), piece({ width: 200 })] }), /5 pieces in the band, 4 at most/);
  assert.throws(() => planned({ band: [piece({ scale: 0.4, width: 1088 })] }), /scale 0.4 is out of 0.45 to 1.2/);
  assert.throws(() => planned({ band: [piece({ scale: 1.5, width: 1088 })] }), /scale 1.5 is out of/);
  assert.throws(() => planned({ band: [piece({ width: 1000 })] }), /ends at x = 1056, short of 1144 \(widen a piece by 88 px\)/);
  assert.throws(() => planned({ band: [piece({ shot: 'windows-trailer', width: 1088 })] }), /no gallery takes windows-trailer/);
  assert.throws(() => planned({ band: [piece({ shot: 'android-home', width: 1088 })] }), /no gallery takes android-home/);
  assert.throws(() => planned({ band: [piece({ point: 4, width: 1088 })] }), /point 4 is not one of the 3 points/);
  assert.throws(() => planned({ band: [piece({ point: 0, width: 1088 })] }), /point 0 is not one of the 3 points/);
  assert.throws(() => planned({ band: [piece({ at: [10], width: 1088 })] }), /at is \[x, y\]/);
});

test('never the banner of the release before', () => {
  // The same screen, the same place (0.9.9's first banner, 0.9.8's title page again), or most of it: refused.
  assert.throws(() => planned({ band: [piece({ shot: 'mac-settings', at: [0, 0], scale: 0.5, width: 1088 })] }), /0.9.8, piece 1: mac-settings at \[0,0\] shows again what 0.9.7's banner showed/);
  assert.throws(() => planned({ band: [piece({ shot: 'mac-settings', at: [40, 30], scale: 0.5, width: 1088 })] }), /shows again what 0.9.7/);
  // Another part of the same screen, or another screen: taken.
  assert.equal(planned({ band: [piece({ shot: 'mac-settings', at: [900, 0], scale: 0.5, width: 1088 })] }).band.length, 1);
  assert.equal(planned({ band: three }).band.length, 3);
  // A piece without a point of this version.
  assert.throws(() => planned({ band: [three[0], three[1], { ...three[2], point: undefined }] }), /piece 3: no point nor caption/);
  // No band twice in a row.
  assert.throws(() => plan('0.9.8', { scenes: { '0.9.8': {}, '0.9.7': {} }, news }), /0.9.8: no band, as 0.9.7/);
  // The release before without its entry any more: nothing to compare.
  assert.equal(plan('0.9.8', { scenes: { '0.9.8': { band: [piece({ shot: 'mac-settings', at: [0, 0], scale: 0.5, width: 1088 })] } }, news }).band.length, 1);
  assert.deepEqual(before('0.9.8', scenes({}), news), { version: '0.9.7', entry: earlier });
  assert.equal(before('1.0.0', scenes({}), news), null);
});

test('every shot a band may take is one a gallery captures', () => {
  assert.deepEqual(Object.keys(SHOTS), ['windows', 'mac', 'iphone']);
  for (const [platform, screens] of Object.entries(SHOTS)) for (const s of screens) plan('0.9.8', { scenes: { '0.9.8': { band: [piece({ shot: `${platform}-${s}`, width: 1088 })] } }, news });
});

test('the film: the entry\'s, else the version\'s own, never the one of the release before', () => {
  assert.equal(planned({ film: 'klaus' }).film, 'klaus');
  assert.throws(() => planned({ film: 'spirited-away' }), /the film spirited-away has no place in FILMS/);
  const names = Object.keys(FILMS);
  for (let i = 0; i < 60; i++) {
    const version = `0.${i}.3`, before = `0.${i}.2`;
    for (const film of names) {
      const chosen = filmOf(version, { [before]: { film } }, [{ version }, { version: before }]);
      assert.ok(names.includes(chosen));
      assert.notEqual(chosen, film, `${version} after ${film}`);
    }
    assert.equal(filmOf(version, {}, []), filmOf(version, {}, []));
  }
  assert.notEqual(planned({}).film, 'spider-verse');
});

test('every film\'s place covers the top right and fades before the captions', () => {
  for (const [name, f] of Object.entries(FILMS)) {
    assert.ok(f.x <= 480, `${name} starts at x = ${f.x}`);
    assert.ok(f.x + f.w >= 1200, `${name} stops at x = ${f.x + f.w}`);
    assert.ok(f.y <= 0, `${name} starts at y = ${f.y}`);
    assert.ok(f.y + f.w * 9 / 16 >= BAND.caption - 6, `${name} stops at y = ${f.y + f.w * 9 / 16}`);
    assert.ok(f.x + f.fade >= 470, `${name} is bright from x = ${f.x + f.fade}, under the sentence`);
  }
});
