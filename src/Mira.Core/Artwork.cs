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
    /// of that episode), and only then the series' backdrop. With <see cref="Spoilers"/> on, an episode not started
    /// shows only its season's or series' art, or <see cref="None"/>: never a picture of its own.
    /// </summary>
    public static ImageRef Landscape(MediaItem item)
    {
        if (item.Type == "Episode")
        {
            // « Cacher les spoilers »: an episode not started yet shows its season's or series' art, never a still of its own.
            var own = !Spoilers.Hides(item);
            if (own && Tag(item.ImageTags, "Thumb") is { } thumb) return new(item.Id, "Thumb", thumb);
            if (item.SeasonId is { Length: > 0 } season)
            {
                if (item.SeasonThumbImageTag is { Length: > 0 } seasonThumb) return new(season, "Thumb", seasonThumb);
                // Jellyfin takes the closest parent with a backdrop: the season when it has one.
                if (item.ParentBackdropItemId == season && item.ParentBackdropImageTags.Length > 0) return new(season, "Backdrop", item.ParentBackdropImageTags[0]);
            }
            if (own && Tag(item.ImageTags, "Primary") is { } still) return new(item.Id, "Primary", still);
        }
        return Backdrop(item);
    }
    /// <summary>A poster: an episode shows its series' poster, anything else its own.</summary>
    public static ImageRef Poster(MediaItem item) => item.Type == "Episode" && item.SeriesId is not null
        ? new(item.SeriesId, "Primary", item.SeriesPrimaryImageTag ?? "")
        : new(item.Id, "Primary", Tag(item.ImageTags, "Primary") ?? "");
    /// <summary>
    /// A large background (the home banner, « À suivre » on top of Séries, a title page): the item's backdrop, else its
    /// closest parent's, else its thumbnail. An episode <see cref="Spoilers"/> hides gets its season's or series'
    /// backdrop, or <see cref="None"/> when neither has one (a series Jellyfin found nothing for: the only pictures of its
    /// episodes are frames of their own videos). Its own title page, opened on purpose, shows everything (<paramref name="ownPage"/>).
    /// </summary>
    public static ImageRef Backdrop(MediaItem item, bool ownPage = false)
    {
        var hidden = !ownPage && Spoilers.Hides(item);
        if (!hidden && item.BackdropImageTags.Length > 0) return new(item.Id, "Backdrop", item.BackdropImageTags[0]);
        if (item.ParentBackdropItemId is { Length: > 0 } parent) return new(parent, "Backdrop", item.ParentBackdropImageTags.FirstOrDefault() ?? "");
        if (hidden) return None;
        if (Tag(item.ImageTags, "Thumb") is { } thumb) return new(item.Id, "Thumb", thumb);
        return new(item.Id, "Primary", Tag(item.ImageTags, "Primary") ?? "");
    }
    /// <summary>No picture: a card or a banner keeps its placeholder, and nothing is asked of Jellyfin.</summary>
    public static ImageRef None { get; } = new("", "", "");
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
