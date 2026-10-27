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

    /// <summary>The season of an episode; one without a number is in the first.</summary>
    public static int Season(MediaItem episode) => episode.ParentIndexNumber ?? 1;
    private static int Number(MediaItem episode, int position) => episode.IndexNumber ?? position + 1;
}
