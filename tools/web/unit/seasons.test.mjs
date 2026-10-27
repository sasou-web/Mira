// A series' seasons on its title page (src/Mira.Web/js/seasons.js), without a browser.
import './fr.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { episodePages, marksSeasons, seasonMark, nextElsewhere } from '../../../src/Mira.Web/js/seasons.js';

const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/seasons.json', import.meta.url), 'utf8'));
const episode = ([Id, SeasonId, season, played]) => ({ Id, SeasonId: SeasonId ?? undefined, ParentIndexNumber: season ?? undefined, UserData: { Played: played } });

test('a season marked watched, and « À suivre » in another season, as Windows and Mac (tests/fixtures/seasons.json)', () => {
  for (const [name, rows, marks, seasons, nexts] of fixture.cases) {
    const episodes = rows.map(episode);
    assert.equal(marksSeasons(episodes), marks, name);
    for (const [season, ids, played] of seasons) assert.deepEqual(seasonMark(episodes, season), { ids, played }, `${name}: ${season}`);
    const pages = episodePages(episodes);
    for (const [shown, next, expected] of nexts) {
      const page = pages.find((p) => p.season === shown);
      assert.equal(nextElsewhere(pages, episodes, page, episodes.find((x) => x.Id === next) ?? null)?.season ?? null, expected, `${name}: ${shown} ${next}`);
    }
  }
});

test('the season list of a long season, in slices of 100', () => {
  const episodes = Array.from({ length: 230 }, (_, i) => ({ Id: `e${i}`, ParentIndexNumber: 1, IndexNumber: i + 1 }));
  assert.deepEqual(episodePages(episodes).map((p) => p.label), ['Saison 1 · 1–100', 'Saison 1 · 101–200', 'Saison 1 · 201–230']);
});
