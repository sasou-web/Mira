// tools/banner/plan.mjs: scenes.json's format, its refusals, the sentence, the band's geometry, the film of a version.
//   node --test tools/banner/plan.test.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { BAND, FILMS, SHOTS, filmOf, longDate, plan, sentence } from './plan.mjs';

const point = (title) => ({ icon: 'info', title, en: { title } });
const news = [
  { version: '0.9.8', date: '2026-11-20', summary: 'Résumé', en: { summary: 'Downloads from the title page, a smoother scroll.' }, items: [point('Get the missing episodes'), point('Smoother scrolling'), point('Fixes')] },
  { version: '0.9.7', date: '2026-11-13', summary: 'Résumé', en: { summary: 'Settings in a window, episodes in a grid.' }, items: [point('Settings in a window'), point('Episodes in a grid')] },
  { version: '1.0.0', date: '2026-12-01', summary: 'Résumé', en: { summary: 'Mira 1.0' }, items: [point('One')] },
];
const piece = (over = {}) => ({ point: 1, shot: 'windows-series', at: [93, 86], scale: 0.578, width: 536, ...over });
const three = [piece(), piece({ point: 2, shot: 'mac-home', at: [0, 0], scale: 0.5, width: 268 }), piece({ point: 3, shot: 'iphone-film', at: [0, 0], scale: 0.62, width: 252 })];
const scenes = (entry, version = '0.9.8') => ({ [version]: entry, '0.9.7': { film: 'spider-verse' } });
const planned = (entry, version = '0.9.8') => plan(version, { scenes: scenes(entry, version), news });

test('the repository: every entry of scenes.json plans', () => {
  for (const version of ['0.9.5', '0.9.7']) {
    const p = plan(version);
    assert.equal(p.template, 'film');
    assert.ok(p.band.length >= 1 && p.band.every((x) => x.caption));
  }
  assert.deepEqual(plan('0.9.7').shots, ['windows-subtitles', 'windows-series', 'mac-home']);
  assert.equal(plan('0.9.7').iphone, false);
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
  assert.throws(() => plan('1.0.0', { scenes: { '1.0.0': { lineup: { lead: 'windows-player' } } }, news }), /no gallery takes windows-player/);
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
  const p = planned({ band: [three[0], { ...three[1], point: undefined, caption: 'By hand' }, { ...three[2], point: undefined }] });
  assert.deepEqual(p.band.map((x) => x.left), [56, 608, 892]);
  assert.deepEqual(p.band.map((x) => x.caption), ['Get the missing episodes', 'By hand', null]);
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
  assert.throws(() => planned({ band: [piece({ shot: 'windows-player', width: 1088 })] }), /no gallery takes windows-player/);
  assert.throws(() => planned({ band: [piece({ shot: 'android-home', width: 1088 })] }), /no gallery takes android-home/);
  assert.throws(() => planned({ band: [piece({ point: 4, width: 1088 })] }), /point 4 is not one of the 3 points/);
  assert.throws(() => planned({ band: [piece({ point: 0, width: 1088 })] }), /point 0 is not one of the 3 points/);
  assert.throws(() => planned({ band: [piece({ at: [10], width: 1088 })] }), /at is \[x, y\]/);
});

test('every shot a band may take is one a gallery captures', () => {
  assert.deepEqual(Object.keys(SHOTS), ['windows', 'mac', 'iphone']);
  for (const [platform, screens] of Object.entries(SHOTS)) for (const s of screens) planned({ band: [piece({ shot: `${platform}-${s}`, width: 1088 })] });
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
