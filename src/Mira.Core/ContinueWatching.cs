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

    public static List<MediaItem> Order(IEnumerable<MediaItem> resume, IEnumerable<MediaItem> next, IEnumerable<MediaItem> history)
    {
        var activity = history.GroupBy(Group).ToDictionary(x => x.Key, x => x.Max(i => i.UserData.LastPlayedDate) ?? DateTimeOffset.MinValue);
        // A real resume point wins over NextUp's not-yet-started episode of the same series.
        // Among several resumed episodes, the most recently watched one wins.
        var candidates = resume.Where(x => !x.UserData.Played && x.UserData.PlaybackPositionTicks > 0)
            .OrderByDescending(x => x.UserData.LastPlayedDate).Concat(next.Where(x => !x.UserData.Played))
            .DistinctBy(Group);
        return candidates.OrderByDescending(x =>
        {
            var own = x.UserData.LastPlayedDate ?? DateTimeOffset.MinValue;
            return activity.TryGetValue(Group(x), out var family) && family > own ? family : own;
        }).ToList();
    }
}
