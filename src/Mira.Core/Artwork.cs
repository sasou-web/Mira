namespace Mira.Core;

/// <summary>One image of a Jellyfin item: the item it belongs to, its type (Primary, Thumb, Backdrop) and its tag.</summary>
public sealed record ImageRef(string ItemId, string Type, string Tag);

/// <summary>Which of Jellyfin's images a view shows for an item.</summary>
public static class Artwork
{
    /// <summary>
    /// A wide card, such as "Continuer à regarder". For an episode it is art of its own season, never the series'
    /// when something closer exists: a series spans seasons that look nothing alike (another arc, another cast). In order:
    /// the episode's own landscape art, the season's thumbnail, the season's backdrop, the episode's own image (a still
    /// of that episode), and only then the series' backdrop.
    /// </summary>
    public static ImageRef Landscape(MediaItem item)
    {
        if (item.Type == "Episode")
        {
            if (Tag(item.ImageTags, "Thumb") is { } thumb) return new(item.Id, "Thumb", thumb);
            if (item.SeasonId is { Length: > 0 } season)
            {
                if (item.SeasonThumbImageTag is { Length: > 0 } seasonThumb) return new(season, "Thumb", seasonThumb);
                // Jellyfin takes the closest parent with a backdrop: the season when it has one.
                if (item.ParentBackdropItemId == season && item.ParentBackdropImageTags.Length > 0) return new(season, "Backdrop", item.ParentBackdropImageTags[0]);
            }
            if (Tag(item.ImageTags, "Primary") is { } still) return new(item.Id, "Primary", still);
        }
        return Backdrop(item);
    }
    /// <summary>A poster: an episode shows its series' poster, anything else its own.</summary>
    public static ImageRef Poster(MediaItem item) => item.Type == "Episode" && item.SeriesId is not null
        ? new(item.SeriesId, "Primary", item.SeriesPrimaryImageTag ?? "")
        : new(item.Id, "Primary", Tag(item.ImageTags, "Primary") ?? "");
    /// <summary>A large background (the home banner, a title page): the item's backdrop, else its closest parent's, else its thumbnail.</summary>
    public static ImageRef Backdrop(MediaItem item)
    {
        if (item.BackdropImageTags.Length > 0) return new(item.Id, "Backdrop", item.BackdropImageTags[0]);
        if (item.ParentBackdropItemId is { Length: > 0 } parent) return new(parent, "Backdrop", item.ParentBackdropImageTags.FirstOrDefault() ?? "");
        if (Tag(item.ImageTags, "Thumb") is { } thumb) return new(item.Id, "Thumb", thumb);
        return new(item.Id, "Primary", Tag(item.ImageTags, "Primary") ?? "");
    }
    /// <summary>The seasons of these episodes, each once: the ones whose thumbnail Mira asks Jellyfin for.</summary>
    public static IReadOnlyList<string> Seasons(IEnumerable<MediaItem> items) =>
        items.Where(x => x.Type == "Episode" && x.SeasonId is { Length: > 0 }).Select(x => x.SeasonId!).Distinct().ToList();
    private static string? Tag(Dictionary<string, string> tags, string type) => tags.TryGetValue(type, out var tag) && tag.Length > 0 ? tag : null;
    /// <summary>The widths asked of Jellyfin: a few steps, so that cards of slightly different sizes share one file.</summary>
    private static readonly int[] Steps = [160, 200, 240, 280, 320, 360, 400, 480, 560, 640, 720, 800, 960, 1120, 1280, 1600, 1920, 2560, 3840];
    /// <summary>
    /// The width, in pixels, to ask and decode an image shown <paramref name="width"/> units wide at <paramref name="scale"/>
    /// (1.5 at 150 %): the next step up, never more than <paramref name="max"/>. A poster decoded wider than it is
    /// shown only costs memory (a 500 px poster is 1.5 MB; shown 185 units wide at 100 %, 240 px are enough).
    /// </summary>
    public static int Pixels(double width, double scale, int max)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        if (!double.IsFinite(width) || width <= 0) return Math.Max(1, max);
        var needed = width * Math.Min(scale, 4);
        foreach (var step in Steps) if (step >= needed - .5) return Math.Min(step, max);
        return max;
    }
}
