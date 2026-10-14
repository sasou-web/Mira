namespace Mira.Core;

/// <summary>
/// « Nouvel épisode » notices of Mira for Windows: the episodes Jellyfin added since the last look, of the series this
/// account follows (one already started, or a favourite), one notice for each series. The mark is Jellyfin's own date
/// of the newest episode seen, never this PC's clock; the first look only sets it.
/// </summary>
public static class NewEpisodes
{
    public const int MaxNotices = 3;
    /// <summary>One series' notice: its name, what came out, and the episode to open (the first of them).</summary>
    public sealed record Notice(string SeriesId, string Title, string Text, MediaItem Episode, int Count);
    /// <summary>Started (part of it watched, a new episode makes it unwatched again) or in the favourites.</summary>
    public static bool Follows(MediaItem series) => series.UserData.IsFavorite || series.UserData.PlayedPercentage > 0;
    /// <summary>The newest date of these episodes when it is past <paramref name="seen"/>: the next mark.</summary>
    public static DateTimeOffset? Newest(IEnumerable<MediaItem> episodes, DateTimeOffset? seen)
    {
        var newest = seen;
        foreach (var episode in episodes) if (episode.DateCreated is { } date && (newest is null || date > newest)) newest = date;
        return newest;
    }
    /// <summary>
    /// The notices for the episodes added after <paramref name="seen"/>, not watched yet, of the followed series among
    /// <paramref name="series"/>; the most recent series first, <see cref="MaxNotices"/> at most, and a line for the rest.
    /// </summary>
    public static (List<Notice> Notices, string? More) Pick(IEnumerable<MediaItem> episodes, IReadOnlyDictionary<string, MediaItem> series, DateTimeOffset seen)
    {
        var groups = episodes
            .Where(x => x.Type == "Episode" && x.DateCreated > seen && !x.UserData.Played && x.SeriesId is { } id && series.TryGetValue(id, out var s) && Follows(s))
            .GroupBy(x => x.SeriesId!)
            .OrderByDescending(g => g.Max(x => x.DateCreated))
            .Select(g => g.OrderBy(x => x.ParentIndexNumber ?? 0).ThenBy(x => x.IndexNumber ?? 0).ToList())
            .ToList();
        var notices = groups.Take(MaxNotices).Select(g => new Notice(g[0].SeriesId!, series[g[0].SeriesId!].Name is { Length: > 0 } name ? name : g[0].SeriesName ?? "", Text(g), g[0], g.Count)).ToList();
        var rest = groups.Count - notices.Count;
        return (notices, rest switch { <= 0 => null, 1 => "Et une autre série que tu suis a un nouvel épisode.", _ => $"Et {rest} autres séries que tu suis ont de nouveaux épisodes." });
    }
    /// <summary>« Nouvel épisode : S2 É5 · Le départ », « 3 nouveaux épisodes : S2 É5 à É7 », « 4 nouveaux épisodes, dès S1 É9 ».</summary>
    public static string Text(IReadOnlyList<MediaItem> episodes)
    {
        var first = episodes[0];
        if (episodes.Count == 1) return "Nouvel épisode : " + string.Join(" · ", new[] { Label(first), first.Name }.Where(x => x.Length > 0));
        var last = episodes[^1];
        return first.ParentIndexNumber == last.ParentIndexNumber && last.IndexNumber is { } end && first.IndexNumber is not null
            ? $"{episodes.Count} nouveaux épisodes : {Label(first)} à É{end}"
            : $"{episodes.Count} nouveaux épisodes, dès {Label(first)}";
    }
    private static string Label(MediaItem episode) =>
        episode.ParentIndexNumber is { } season ? episode.IndexNumber is { } number ? $"S{season} É{number}" : $"S{season}" : episode.IndexNumber is { } n ? $"É{n}" : "";
}
