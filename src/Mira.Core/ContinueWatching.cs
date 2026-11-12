namespace Mira.Core;

/// <summary>Keep one resumable episode per series, then rank films and series by actual playback activity.</summary>
public static class ContinueWatching
{
    /// <summary>A film stands for itself; an episode for its series.</summary>
    public static string Group(MediaItem item) => item.SeriesId ?? item.Id;
    public static List<MediaItem> History(IEnumerable<MediaItem> items) => items
        .OrderByDescending(x => x.UserData.LastPlayedDate).DistinctBy(x => x.Id).Take(64).ToList();

    /// <summary>
    /// The row without the films and series removed from it (« Retirer de Continuer à regarder »), unless played again
    /// since: then they come back on their own.
    /// </summary>
    public static List<MediaItem> WithoutHidden(IEnumerable<MediaItem> row, IReadOnlyDictionary<string, DateTimeOffset> hidden, IEnumerable<MediaItem> history)
    {
        if (hidden.Count == 0) return row.ToList();
        var activity = history.GroupBy(Group).ToDictionary(x => x.Key, x => x.Max(i => i.UserData.LastPlayedDate) ?? DateTimeOffset.MinValue);
        return row.Where(x => !hidden.TryGetValue(Group(x), out var removed) || x.UserData.LastPlayedDate > removed
            || activity.TryGetValue(Group(x), out var played) && played > removed).ToList();
    }

    /// <param name="history">What was played lately, here and on other devices (Jellyfin's own record, by date played): each film or series stands where its last activity puts it.</param>
    public static List<MediaItem> Order(IEnumerable<MediaItem> resume, IEnumerable<MediaItem> next, IEnumerable<MediaItem> history)
    {
        var played = history.ToList(); var upNext = next.Where(x => !x.UserData.Played).ToList();
        var activity = played.GroupBy(Group).ToDictionary(x => x.Key, x => x.Max(i => i.UserData.LastPlayedDate) ?? DateTimeOffset.MinValue);
        // A real resume point wins over NextUp's not-yet-started episode of the same series (a title watched again too:
        // Jellyfin 12 keeps it watched, with its position), unless the series went on past it since. Among several resumed
        // episodes, the most recently watched one wins.
        var candidates = resume.Where(x => x.Resumable && !LeftBehind(x, upNext, played))
            .OrderByDescending(x => x.UserData.LastPlayedDate).Concat(upNext)
            .DistinctBy(Group);
        return candidates.OrderByDescending(x =>
        {
            var own = x.UserData.LastPlayedDate ?? DateTimeOffset.MinValue;
            return activity.TryGetValue(Group(x), out var family) && family > own ? family : own;
        }).ToList();
    }

    /// <summary>
    /// An episode left halfway that its series went on past: Jellyfin offers one after it (NextUp), and an episode after it
    /// was played since it was left (skipped with the next-episode button, finished on another device). Its series is
    /// where that episode left it, not halfway through the older one (checked on Jellyfin 12.1: S01E02 left at 30 %, then
    /// S01E03 and S01E04 watched, keeps S01E02 in UserItems/Resume while Shows/NextUp says S01E05).
    /// </summary>
    public static bool LeftBehind(MediaItem resumed, IEnumerable<MediaItem> next, IEnumerable<MediaItem> history) =>
        resumed is { Type: "Episode", SeriesId: { } series, UserData.LastPlayedDate: { } left }
        && next.Any(x => x.SeriesId == series && Later(x, resumed))
        && history.Any(x => x.SeriesId == series && x.Id != resumed.Id && Later(x, resumed) && x.UserData.LastPlayedDate > left);
    /// <summary>Whether <paramref name="episode"/> comes after <paramref name="than"/> in its series (season, then number).</summary>
    private static bool Later(MediaItem episode, MediaItem than) =>
        (episode.ParentIndexNumber ?? 0, episode.IndexNumber ?? 0).CompareTo((than.ParentIndexNumber ?? 0, than.IndexNumber ?? 0)) > 0;
}
