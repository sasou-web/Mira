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
    /// <summary>More pages than this (a long anime cut in slices): the season list instead of pills.</summary>
    public const int MaxPills = 10;

    /// <summary>How many tiles a row holds in <paramref name="width"/>.</summary>
    public static int Columns(double width) => Math.Clamp((int)((width + Gap) / (MinWidth + Gap)), 1, MaxColumns);

    /// <summary>The width of a tile in <paramref name="width"/>, whole pixels so the rows line up.</summary>
    public static double Width(double width) { var columns = Columns(width); return Math.Max(1, Math.Floor((width - Gap * (columns - 1)) / columns)); }

    /// <summary>Pills for the seasons: two pages at least, <see cref="MaxPills"/> at most.</summary>
    public static bool Pills(IReadOnlyList<EpisodePage> pages) => pages.Count is >= 2 and <= MaxPills;

    /// <summary>A pill's name and its lighter note: « Saison 2 », « Saison 1 » + « absente », « Saison 1 » + « 1–100 ».</summary>
    public static (string Name, string? Note) Pill(EpisodePage page)
    {
        var at = page.Label.IndexOf(" · ", StringComparison.Ordinal);
        return at < 0 ? (page.Label, null) : (page.Label[..at], page.Label[(at + 3)..]);
    }

    /// <summary>Under a tile's title: « 24 min · 25 oct. », « 15 min restantes · 25 oct. » for the one begun.</summary>
    public static string Meta(MediaItem episode, DateOnly today) =>
        string.Join(" · ", new[] { episode.Resumable ? MediaQuality.Left(episode) : episode.DurationLabel, EpisodeCalendar.AirDay(episode) is { } day ? EpisodeShelf.ShortDay(day, today) : null }.Where(x => !string.IsNullOrEmpty(x)));

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
