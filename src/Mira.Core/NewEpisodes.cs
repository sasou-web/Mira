namespace Mira.Core;

/// <summary>
/// « Nouvel épisode » notices of Mira for Windows: the episodes Jellyfin added since the last look, of the series this
/// account follows (one already started, or a favourite), one notice for each series. The mark is Jellyfin's own date
/// of the newest episode seen, never this PC's clock; the first look only sets it. With it, the episodes the last look
/// listed (<see cref="Known"/>): Jellyfin dates an episode by its file, so one can arrive after the mark yet be dated
/// before it (<see cref="Late"/>).
/// </summary>
public static class NewEpisodes
{
    public const int MaxNotices = 3;
    /// <summary>One series' notice: its name, what came out, and the episode to open (the first of them).</summary>
    public sealed record Notice(string SeriesId, string Title, string Text, MediaItem Episode, int Count);
    /// <summary>Started (part of it watched, a new episode makes it unwatched again) or in the favourites.</summary>
    public static bool Follows(MediaItem series) => series.UserData.IsFavorite || series.UserData.PlayedPercentage > 0;
    /// <summary>
    /// How much older than the mark an episode new since the last look may be dated. Jellyfin dates an episode by its
    /// file (« UseFileCreationTimeForDateAdded », on by default: the creation time on Windows, kept by a move on the same
    /// disk and by a hard link), so a file comes into the library after others dated later: a download started before
    /// another and finished after it, a season pack still downloading while a single episode lands, a file downloaded on
    /// Monday and moved in on Wednesday. Older than this, it is not news.
    /// </summary>
    public static readonly TimeSpan Late = TimeSpan.FromDays(7);
    /// <summary>Where the episodes a look must list go down to, from the mark: <see cref="Late"/> before it.</summary>
    public static DateTimeOffset Floor(DateTimeOffset mark) => mark > DateTimeOffset.MinValue + Late ? mark - Late : DateTimeOffset.MinValue;
    /// <summary>
    /// The ids of the episodes listed, watched or not, dated after <see cref="Floor"/> of <paramref name="mark"/>: what the
    /// next look knows was already there (an episode dated before the mark but not among them came since).
    /// </summary>
    public static List<string> Known(IEnumerable<MediaItem> episodes, DateTimeOffset mark)
    {
        var floor = Floor(mark);
        return episodes.Where(x => x.DateCreated > floor).Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    /// <summary>Whether two lists of <see cref="Known"/> hold the same episodes (both null, or neither).</summary>
    public static bool Same(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b) =>
        a is null || b is null ? a is null && b is null : a.Count == b.Count && a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
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
    /// Added since the last look: dated after the mark, or after its <see cref="Floor"/> and not among the episodes that
    /// look listed (<paramref name="known"/>, null when it did not list them all).
    /// </summary>
    public static (List<Notice> Notices, string? More) Pick(IEnumerable<MediaItem> episodes, IReadOnlyDictionary<string, MediaItem> series, DateTimeOffset seen, IEnumerable<string>? known = null)
    {
        var there = known is null ? null : new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        var floor = Floor(seen);
        var groups = episodes
            .Where(x => x.Type == "Episode" && x.DateCreated is { } added && (added > seen || there is not null && added > floor && !there.Contains(x.Id))
                && !x.UserData.Played && x.SeriesId is { } id && series.TryGetValue(id, out var s) && Follows(s))
            .GroupBy(x => x.SeriesId!)
            .OrderByDescending(g => g.Max(x => x.DateCreated))
            .Select(g => g.OrderBy(x => x.ParentIndexNumber ?? 0).ThenBy(x => x.IndexNumber ?? 0).ToList())
            .ToList();
        var notices = groups.Take(MaxNotices).Select(g => new Notice(g[0].SeriesId!, series[g[0].SeriesId!].Name is { Length: > 0 } name ? name : g[0].SeriesName ?? "", Text(g), g[0], g.Count)).ToList();
        var rest = groups.Count - notices.Count;
        return (notices, rest switch { <= 0 => null, 1 => L("Et une autre série que tu suis a un nouvel épisode.", "And another show you follow has a new episode."), _ => L($"Et {rest} autres séries que tu suis ont de nouveaux épisodes.", $"And {rest} other shows you follow have new episodes.") });
    }
    /// <summary>
    /// The picture of a notice: the episode's own still, unless <see cref="Spoilers"/> hides it (an episode just added
    /// is not started yet): then its season's or series' art, as on a wide card, or none.
    /// </summary>
    public static ImageRef? Picture(MediaItem episode)
    {
        var image = Spoilers.Hides(episode) ? Artwork.Landscape(episode) : new ImageRef(episode.Id, "Primary", episode.ImageTags.GetValueOrDefault("Primary") ?? "");
        return image.ItemId.Length > 0 && image.Tag.Length > 0 ? image : null;
    }
    /// <summary>« Nouvel épisode : S2 É5 · Le départ », « 3 nouveaux épisodes : S2 É5 à É7 », « 4 nouveaux épisodes, dès S1 É9 »; "3 new episodes: S2 E5 to E7".</summary>
    public static string Text(IReadOnlyList<MediaItem> episodes)
    {
        var first = episodes[0];
        if (episodes.Count == 1) return L("Nouvel épisode : ", "New episode: ") + string.Join(" · ", new[] { Label(first), first.Name }.Where(x => x.Length > 0));
        var last = episodes[^1];
        return first.ParentIndexNumber == last.ParentIndexNumber && last.IndexNumber is { } end && first.IndexNumber is not null
            ? L($"{episodes.Count} nouveaux épisodes : {Label(first)} à É{end}", $"{episodes.Count} new episodes: {Label(first)} to E{end}")
            : L($"{episodes.Count} nouveaux épisodes, dès {Label(first)}", $"{episodes.Count} new episodes, from {Label(first)}");
    }
    private static string Label(MediaItem episode) =>
        episode.ParentIndexNumber is { } season ? episode.IndexNumber is { } number ? L($"S{season} É{number}", $"S{season} E{number}") : $"S{season}" : episode.IndexNumber is { } n ? L($"É{n}", $"E{n}") : "";
}
