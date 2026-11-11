namespace Mira.Core;

/// <summary>An entry of the season list on a series page: a season, or a slice of a long one.</summary>
public sealed record EpisodePage(int Season, int Skip, int Take, string Label);

/// <summary>
/// The season list of a series page. A season of more than <see cref="Size"/> episodes (a long anime numbered from 1 to
/// 1,100) is cut into slices, « Saison 1 · 1–100 », so a page never builds a thousand rows and thumbnails at once.
/// </summary>
public static class EpisodePages
{
    public const int Size = 100;

    /// <summary>The pages in season order; <paramref name="episodes"/> in Jellyfin's order (season, then number).</summary>
    public static List<EpisodePage> From(IReadOnlyList<MediaItem> episodes)
    {
        var pages = new List<EpisodePage>();
        foreach (var season in episodes.GroupBy(Season).OrderBy(x => x.Key))
        {
            var list = season.ToList(); var name = season.Key == 0 ? L("Épisodes spéciaux", "Specials") : L($"Saison {season.Key}", $"Season {season.Key}");
            if (list.Count <= Size) { pages.Add(new(season.Key, 0, list.Count, name)); continue; }
            for (var skip = 0; skip < list.Count; skip += Size)
            {
                var take = Math.Min(Size, list.Count - skip);
                var (first, last) = (Number(list[skip], skip), Number(list[skip + take - 1], skip + take - 1));
                pages.Add(new(season.Key, skip, take, first == last ? $"{name} · {first}" : $"{name} · {first}–{last}"));
            }
        }
        return pages;
    }

    /// <summary>The episodes a page shows.</summary>
    public static List<MediaItem> Episodes(IReadOnlyList<MediaItem> episodes, EpisodePage page) =>
        episodes.Where(x => Season(x) == page.Season).Skip(page.Skip).Take(page.Take).ToList();

    /// <summary>The page holding <paramref name="episode"/>, or null when it is not in the list.</summary>
    public static EpisodePage? Holding(IReadOnlyList<EpisodePage> pages, IReadOnlyList<MediaItem> episodes, MediaItem? episode)
    {
        if (episode is null) return null;
        var position = episodes.Where(x => Season(x) == Season(episode)).ToList().FindIndex(x => x.Id == episode.Id);
        return position < 0 ? null : pages.FirstOrDefault(x => x.Season == Season(episode) && position >= x.Skip && position < x.Skip + x.Take);
    }

    /// <summary>A season of its own to mark only beside another: with one, the series' own button does it.</summary>
    public static bool MarksSeasons(IReadOnlyList<MediaItem> episodes) => episodes.Select(Season).Distinct().Count() > 1;

    /// <summary>
    /// What « Marquer la saison comme vue » marks: the season as Jellyfin holds it (one call, every episode with it),
    /// else each episode (episodes without a season of Jellyfin's, or split between two). Played: all its episodes are
    /// watched, and the button offers « non vue ». As seasons.js of Mira web (tests/fixtures/seasons.json).
    /// </summary>
    public static (List<string> Ids, bool Played) SeasonMark(IReadOnlyList<MediaItem> episodes, int season)
    {
        var list = episodes.Where(x => Season(x) == season).ToList();
        var held = list.Select(x => x.SeasonId).Distinct().ToList();
        List<string> ids = held is [{ Length: > 0 } one] ? [one] : list.Select(x => x.Id).ToList();
        return (ids, list.Count > 0 && list.All(x => x.UserData.Played));
    }

    /// <summary>The page holding the episode Lecture plays when another season is shown (« À suivre », above its episodes); else null.</summary>
    public static EpisodePage? NextElsewhere(IReadOnlyList<EpisodePage> pages, IReadOnlyList<MediaItem> episodes, EpisodePage? shown, MediaItem? next) =>
        Holding(pages, episodes, next) is { } holding && shown is not null && (holding.Season != shown.Season || holding.Skip != shown.Skip) ? holding : null;

    /// <summary>
    /// The episode Lecture opens on a series when Jellyfin's next up says none (a series watched to its end, or watched
    /// out of order; Shows/NextUp also leaves the specials out): <see cref="Unfinished"/>, else <see cref="Again"/>, else
    /// the first, to watch it again. The numbered seasons come before the specials, which Jellyfin lists first (season 0):
    /// « Revoir » starts at S01E01, not at a making-of (checked on 12.1). The iPhone's SeriesStart, cases of
    /// tests/fixtures/series-start.json.
    /// </summary>
    public static MediaItem? Start(IReadOnlyList<MediaItem> episodes) => Unfinished(episodes) ?? Again(episodes) ?? Numbered(episodes).FirstOrDefault();

    /// <summary>
    /// A series watched to its end and being watched again: the episode after the one watched last, so that S01E03 follows
    /// S01E02 watched again yesterday (Shows/NextUp, without enableRewatching, answers none once every episode is watched,
    /// checked on 12.1). Null when that one is a special, or the last episode (a series watched to its end, or marked
    /// watched at once: Jellyfin dates each episode in turn, in their order), or shares its date with another.
    /// </summary>
    public static MediaItem? Again(IReadOnlyList<MediaItem> episodes)
    {
        var dated = episodes.Where(x => x.UserData.LastPlayedDate is not null).OrderByDescending(x => x.UserData.LastPlayedDate).Take(2).ToList();
        if (dated.Count == 0 || dated.Count == 2 && dated[0].UserData.LastPlayedDate == dated[1].UserData.LastPlayedDate) return null;
        var ordered = episodes.Where(x => Season(x) != 0).ToList();
        var at = ordered.FindIndex(x => x.Id == dated[0].Id);
        return at >= 0 && at + 1 < ordered.Count ? ordered[at + 1] : null;
    }

    /// <summary>An episode left halfway, else the first not watched, the numbered seasons before the specials; null when all are watched.</summary>
    public static MediaItem? Unfinished(IReadOnlyList<MediaItem> episodes)
    {
        var ordered = Numbered(episodes);
        return ordered.FirstOrDefault(x => x.Resumable) ?? ordered.FirstOrDefault(x => !x.UserData.Played);
    }

    /// <summary>The episodes of the numbered seasons, then the specials, each in Jellyfin's order.</summary>
    private static List<MediaItem> Numbered(IReadOnlyList<MediaItem> episodes) => [.. episodes.Where(x => Season(x) != 0), .. episodes.Where(x => Season(x) == 0)];

    /// <summary>The season of an episode; one without a number is in the first.</summary>
    public static int Season(MediaItem episode) => episode.ParentIndexNumber ?? 1;
    private static int Number(MediaItem episode, int position) => episode.IndexNumber ?? position + 1;
}
