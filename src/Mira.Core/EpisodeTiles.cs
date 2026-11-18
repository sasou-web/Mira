namespace Mira.Core;

/// <summary>
/// A series page's episodes as a grid of pictures (ogsas, 2026-11-12, mock-up B of
/// /mnt/project-files/fiche-film/liste-episodes/): four stills a row at the usual width, the seasons as pills, the one
/// to watch framed in white, a watched one with a check on its picture; those the library lacks (<see cref="EpisodeShelf"/>)
/// are hollow tiles at their place. Windows and Mac lay it out from these rules; the iPhone has its own two columns.
/// </summary>
public static class EpisodeTiles
{
    /// <summary>Between two tiles, across and down.</summary>
    public const double Gap = 20, RowGap = 28;
    /// <summary>A tile is never narrower: past it, one column less.</summary>
    public const double MinWidth = 250;
    public const int MaxColumns = 6;
    /// <summary>
    /// More pages than this (Monogatari Series: 13 seasons and its specials): « Les numéros » (ogsas, 2026-11-18, mock-up C of
    /// /mnt/project-files/selecteur-saisons/), each pill cut to its number, the one shown keeping its whole name, so that
    /// every season stays in sight and a click shows it: no list that opens, nothing that scrolls.
    /// </summary>
    public const int MaxPills = 10;

    /// <summary>How many tiles a row holds in <paramref name="width"/>.</summary>
    public static int Columns(double width) => Math.Clamp((int)((width + Gap) / (MinWidth + Gap)), 1, MaxColumns);

    /// <summary>The width of a tile in <paramref name="width"/>, whole pixels so the rows line up.</summary>
    public static double Width(double width) { var columns = Columns(width); return Math.Max(1, Math.Floor((width - Gap * (columns - 1)) / columns)); }

    /// <summary>
    /// Pills for the seasons from two pages. A single page (a one-season anime) has none: « Épisodes · 24 » says it all
    /// (ogsas, 0.9.7: a list holding only « Saison 1 »).
    /// </summary>
    public static bool Pills(IReadOnlyList<EpisodePage> pages) => pages.Count >= 2;

    /// <summary>Past <see cref="MaxPills"/> pages, the pills not shown are cut to <see cref="Short"/>.</summary>
    public static bool Compact(IReadOnlyList<EpisodePage> pages) => pages.Count > MaxPills;

    /// <summary>
    /// A cut pill: « 12 », « SP » for the specials; a slice of a long season its episodes, « 101–200 », with its season
    /// before them when several seasons are cut (« 2 · 101–200 »).
    /// </summary>
    public static string Short(EpisodePage page, IReadOnlyList<EpisodePage> pages)
    {
        var number = page.Season == 0 ? "SP" : page.Season.ToString(Culture);
        if (page.Absent || pages.Count(x => x.Season == page.Season && !x.Absent) < 2 || Pill(page).Note is not { } range) return number;
        return pages.Where(x => !x.Absent).GroupBy(x => x.Season).Count(x => x.Count() > 1) > 1 ? $"{number} · {range}" : range;
    }

    /// <summary>Every episode of the page watched: its cut pill is quieter.</summary>
    public static bool Watched(EpisodePage page, IReadOnlyList<MediaItem> episodes) =>
        !page.Absent && EpisodePages.Episodes(episodes, page) is { Count: > 0 } shown && shown.All(x => x.UserData.Played);

    /// <summary>
    /// The bubble over a cut pill: its whole name, then where you are, « 6 épisodes », « 2 vus sur 6 », « 1 vu sur 6 », « Vue »; for a season
    /// the library lacks, « absente » or « à venir ».
    /// </summary>
    public static (string Name, string Note) Tip(EpisodePage page, IReadOnlyList<MediaItem> episodes)
    {
        if (page.Absent) { var (name, note) = Pill(page); return (name, note ?? ""); }
        var shown = EpisodePages.Episodes(episodes, page); var seen = shown.Count(x => x.UserData.Played);
        var where = seen == 0 ? Count(shown.Count, "épisode", "épisodes", "episode", "episodes")
            : seen == shown.Count ? (page.Season == 0 ? L("Vus", "Watched") : L("Vue", "Watched"))
            : L($"{Count(seen, "vu", "vus", "", "")} sur {shown.Count.ToString(Culture)}", $"{seen.ToString(Culture)} of {shown.Count.ToString(Culture)} watched");
        return (page.Label, where);
    }

    /// <summary>A pill's name and its lighter note: « Saison 2 », « Saison 1 » + « absente », « Saison 1 » + « 1–100 ».</summary>
    public static (string Name, string? Note) Pill(EpisodePage page)
    {
        var at = page.Label.IndexOf(" · ", StringComparison.Ordinal);
        return at < 0 ? (page.Label, null) : (page.Label[..at], page.Label[(at + 3)..]);
    }

    /// <summary>
    /// Under a tile's title: « 24 min · 25 oct. », « 15 min restantes · 25 oct. » for the one begun, then its files once
    /// known (« · 1,2 Go », <see cref="TitleStorage"/>).
    /// </summary>
    public static string Meta(MediaItem episode, DateOnly today, long? size = null) =>
        string.Join(" · ", new[] { episode.Resumable ? MediaQuality.Left(episode) : episode.DurationLabel, EpisodeCalendar.AirDay(episode) is { } day ? EpisodeShelf.ShortDay(day, today) : null, TitleStorage.Text(size) }.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>
    /// Over the grid: « Épisodes · 12 », the whole season's count (also on a slice of a long one), then the room its files
    /// take once known (« · 48 Go »).
    /// </summary>
    public static string Heading(IReadOnlyList<MediaItem> episodes, EpisodePage? page, IReadOnlyDictionary<string, long>? sizes)
    {
        var season = page is null ? [] : episodes.Where(x => (x.ParentIndexNumber ?? 1) == page.Season).ToList();
        return string.Join("  ·  ", new[] { L("Épisodes", "Episodes"), season.Count.ToString(Culture), sizes is null ? null : TitleStorage.Text(TitleStorage.Sum(season, sizes)) }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>A tile's title: « 03  Le nom », the number lighter.</summary>
    public static string Number(MediaItem episode) => episode.IndexNumber is { } number ? $"{number:00}" : "";

    /// <summary>The tag on the picture of the episode Lecture plays.</summary>
    public static string UpNext => L("À SUIVRE", "UP NEXT");

    /// <summary>
    /// The two lines of a hollow tile standing for a run the library lacks: « Pas dans ta bibliothèque » + « Sortis du 3 nov.
    /// au 10 nov. », « À venir » + « Du 29 nov. au 27 déc. ».
    /// </summary>
    public static (string First, string Second) GapLines(ShelfLine line, DateOnly today) => line.Kind == ShelfKind.Later
        ? (L("À venir", "Coming"), Capital(EpisodeShelf.When(line.Episodes, today)))
        : (L("Pas dans ta bibliothèque", "Not in your library"), Capital(EpisodeShelf.Aired(line.Episodes, today)));

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Lang.Culture) + text[1..];
}
