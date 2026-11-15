// tools/banner/plan.mjs: scenes.json's format, its refusals, the closeup's thing and words, the sentence, the band's
// geometry, the film of a version.
//   node --test tools/banner/plan.test.mjs
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { BAND, CLOSEUP, FILMS, NO_GLOW, SHOTS, before, filmOf, glowOf, library, longDate, plan, sentence, slug } from './plan.mjs';

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

test('a patch takes « closeup » (« film » with a band), x.y.0 « lineup », and the entry can force one', () => {
  assert.equal(planned({ band: three }).template, 'film');
  assert.equal(planned({ point: 1, shot: 'windows-series', at: [500, 100], size: [600, 400], scale: 1 }).template, 'closeup');
  assert.equal(plan('1.0.0', { scenes: { '1.0.0': { template: 'closeup', point: 1 } }, news }).template, 'closeup');
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
  const p = planned({ template: 'film' });
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
  assert.throws(() => plan('0.9.8', { scenes: { '0.9.8': { template: 'film' }, '0.9.7': { template: 'film' } }, news }), /0.9.8: no band, as 0.9.7/);
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
  assert.equal(planned({ template: 'film', film: 'klaus' }).film, 'klaus');
  assert.throws(() => planned({ template: 'film', film: 'spirited-away' }), /the film spirited-away has no place in FILMS/);
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
  assert.notEqual(planned({ template: 'film' }).film, 'spider-verse');
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

// ——— closeup (« Gros plan ») ———
const thing = (over = {}) => ({ point: 1, shot: 'windows-player', at: [530, 150], size: [910, 730], scale: 0.62, ...over });

test('the library: every film and series of media.sh by its folder\'s name', () => {
  assert.equal(slug("Howl's Moving Castle (2004)"), 'howl-s-moving-castle');
  assert.equal(slug('Love, Death & Robots (2019)'), 'love-death-robots');
  assert.equal(slug('Amélie (2001)'), 'amelie');
  assert.deepEqual(library('film "Your Name. (2016)" 106\nseries "Arcane (2021)" 1 9 40\nseries "Arcane (2021)" 2 9 40\n# film "Not (2000)"\n'), ['your-name', 'arcane']);
  const titles = library();
  assert.ok(titles.length >= 8 && titles.includes('arcane'), titles.join(', '));
});

test('closeup: the point\'s title and text, the other titles in « Also », the thing centred on the right', () => {
  const p = planned(thing());
  assert.equal(p.template, 'closeup');
  const c = p.closeup;
  assert.equal(c.title, 'Get the missing episodes');
  assert.deepEqual(c.also, ['Smoother scrolling', 'Fixes']);
  assert.deepEqual([c.width, c.height], [564, 453]);
  assert.equal(c.left, CLOSEUP.room[0] + Math.round((CLOSEUP.width - c.width) / 2));
  assert.equal(c.top, Math.round((600 - c.height) / 2));
  assert.deepEqual(p.shots, ['windows-player']);
  assert.deepEqual([p.windows, p.mac, p.iphone], [true, false, false]);
  // By hand: the title, the text, « Also », its place, its glow.
  const h = planned(thing({ point: 2, title: 'Smooth', say: 'Short.', also: [], right: 64, glow: { bright: 1.6 } })).closeup;
  assert.deepEqual([h.title, h.say, h.also, h.left, h.glow], ['Smooth', 'Short.', [], 1200 - 64 - 564, { bright: 1.6, vivid: 1.7 }]);
  assert.equal(planned(thing({ point: 2 })).closeup.title, 'Smoother scrolling');
  // No shot (a release of fixes only): Windows' Home, whole.
  const home = planned({ point: 3 }).closeup;
  assert.deepEqual([home.shot, home.at, home.size], ['windows-home', [0, 0], [1440, 900]]);
  assert.ok(home.width <= CLOSEUP.width && home.height <= CLOSEUP.height);
  assert.equal(planned(thing({ shot: 'iphone-film', at: [0, 0], size: [402, 874], scale: 0.55 })).closeup.units, 3);
});

test('closeup: its refusals say what to change', () => {
  assert.throws(() => planned(thing({ scale: 0.8 })), /728 px wide, 636 at most \(lower its scale to 0.69/);
  assert.throws(() => planned(thing({ size: [600, 900], scale: 0.8 })), /720 px high, 500 at most/);
  assert.throws(() => planned(thing({ scale: 0.3 })), /scale 0.3 is out of 0.45 to 1.5/);
  assert.throws(() => planned(thing({ shot: 'windows-trailer' })), /no gallery takes windows-trailer/);
  assert.throws(() => planned(thing({ size: [10] })), /size is \[width, height\]/);
  assert.throws(() => planned(thing({ at: [-1, 0] })), /at is \[x, y\]/);
  assert.throws(() => planned(thing({ point: 9 })), /point 9 is not one of the 3 points/);
  assert.throws(() => planned(thing({ point: undefined })), /no point nor title/);
  assert.throws(() => planned(thing({ right: 640 })), /right 640 puts the thing shown at x = -4/);
  assert.throws(() => planned(thing({ also: 'Fixes' })), /also is a list of titles/);
  assert.throws(() => planned(thing({ film: 'spirited-away' })), /not spirited-away: its first backdrop/);
  assert.throws(() => planned(thing({ film: 'blade-runner' })), /the film blade-runner is not in the banners' library/);
});

test('closeup: never what the release before showed, film or closeup', () => {
  // 0.9.7 (« film ») cut mac-settings from [0, 0]: the same place refused, another one taken.
  assert.throws(() => planned(thing({ shot: 'mac-settings', at: [0, 0], size: [800, 600], scale: 0.7 })), /0.9.8: mac-settings at \[0,0\] shows again what 0.9.7's banner showed/);
  assert.equal(planned(thing({ shot: 'mac-settings', at: [900, 300], size: [500, 500], scale: 0.9 })).closeup.left, 500 + Math.round((636 - 450) / 2));
  // After a closeup: the same thing, Home twice, the same film.
  const last = thing({ film: 'klaus' });
  const after = (entry) => plan('0.9.8', { scenes: { '0.9.8': entry, '0.9.7': last }, news });
  assert.throws(() => after(thing({ film: 'coco', at: [560, 180] })), /windows-player at \[560,180\] shows again what 0.9.7/);
  assert.throws(() => after(thing({ shot: 'windows-series', film: 'klaus' })), /the film klaus again, as 0.9.7/);
  assert.throws(() => plan('0.9.8', { scenes: { '0.9.8': { point: 1 }, '0.9.7': { point: 1 } }, news }), /0.9.8: no shot, as 0.9.7/);
  assert.equal(after(thing({ shot: 'windows-series', at: [0, 0], size: [700, 500], scale: 0.9, film: 'coco' })).film, 'coco');
  // Old short names and the library's are the same film.
  assert.throws(() => plan('0.9.8', { scenes: { '0.9.8': thing({ shot: 'windows-series', film: 'spider-man-across-the-spider-verse' }), '0.9.7': earlier }, news }),
    /the film spider-man-across-the-spider-verse again, as 0.9.7/);
});

test('closeup: the glow is the version\'s own, never the film before, never one without colours', () => {
  const titles = library();
  for (let i = 0; i < 60; i++) {
    const version = `0.${i}.3`, previous = `0.${i}.2`;
    for (const film of ['arcane', 'klaus', 'wild-robot']) {
      const chosen = glowOf(version, { [previous]: { film } }, [{ version }, { version: previous }], titles);
      assert.ok(titles.includes(chosen) && !NO_GLOW[chosen], chosen);
      assert.notEqual(chosen, film === 'wild-robot' ? 'the-wild-robot' : film, `${version} after ${film}`);
    }
  }
  // Nothing in the entries: the release before's own glow is avoided too.
  for (let i = 0; i < 40; i++) {
    const v = `0.${i}.4`, previous = `0.${i}.3`;
    const scenes = { [v]: { point: 1 }, [previous]: { point: 1, shot: 'windows-series', at: [0, 0], size: [700, 500], scale: 0.9 } };
    const news2 = [{ version: v }, { version: previous }];
    assert.notEqual(glowOf(v, scenes, news2, titles), glowOf(previous, scenes, news2, titles));
  }
  assert.equal(planned(thing()).still, planned(thing()).film);
  assert.equal(planned(thing({ film: 'wild-robot' })).still, 'the-wild-robot');
});
