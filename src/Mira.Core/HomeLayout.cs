namespace Mira.Core;

/// <summary>
/// « Accueil à ta main »: which rows the home page shows, in which order. Saved as one line (« continue,favorites,-latest,
/// library:id »): the order, a « - » before a row put away. Rows not in it yet (a new library) come after, in their usual
/// place: the built-in rows shown, a library's own row put away. The same as Mira web's home-rows.js: tests/fixtures/home.json.
/// </summary>
public static class HomeLayout
{
    public const string Continue = "continue", Latest = "latest", Favorites = "favorites", LibraryPrefix = "library:";
    /// <summary>A row of the home page: <see cref="LibraryId"/> for « Nouveau dans … », a library's latest titles.</summary>
    public sealed record Row(string Key, string Title, bool Shown, string? LibraryId = null);

    /// <summary>The libraries that get a row of their own: films, series and mixed ones, not Jellyfin's collections.</summary>
    public static bool Offered(LibrarySection library) => library.CollectionType is null or "movies" or "tvshows" or "mixed";
    public static List<Row> Rows(string? saved, IEnumerable<LibrarySection> libraries)
    {
        var all = new List<Row> { new(Continue, L("Continuer à regarder", "Continue watching"), true), new(Latest, L("Ajouts récents", "Recently added"), true), new(Favorites, L("Tes favoris", "Your favorites"), true) };
        all.AddRange(libraries.Where(Offered).Select(x => new Row(LibraryPrefix + x.Id, L("Nouveau dans ", "New in ") + x.Name, false, x.Id)));
        var rows = new List<Row>();
        foreach (var part in (saved ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hidden = part.StartsWith('-'); var key = hidden ? part[1..] : part;
            // A library gone from Jellyfin, a row of a later version: left out, not kept as an empty place.
            if (all.FirstOrDefault(x => x.Key == key) is { } row && rows.All(x => x.Key != key)) rows.Add(row with { Shown = !hidden });
        }
        rows.AddRange(all.Where(x => rows.All(r => r.Key != x.Key)));
        return rows;
    }
    /// <summary>What a row shows, for the settings.</summary>
    public static string Detail(Row row) => row.Key switch
    {
        Continue => L("Tes titres en cours et les épisodes qui suivent.", "What you’re watching and the episodes that come next."),
        Latest => L("Les derniers films et séries arrivés sur Jellyfin.", "The latest movies and shows added to Jellyfin."),
        Favorites => L("Les titres que tu as mis en favori.", "The titles you’ve added to your favorites."),
        _ => L("Les derniers titres de cette bibliothèque.", "The latest titles in this library.")
    };
    public static string Save(IEnumerable<Row> rows) => string.Join(",", rows.Select(x => (x.Shown ? "" : "-") + x.Key));
    /// <summary>The row <paramref name="key"/> moved up (−1) or down (+1); as it was at either end.</summary>
    public static string Move(string? saved, IEnumerable<LibrarySection> libraries, string key, int step)
    {
        var rows = Rows(saved, libraries); var index = rows.FindIndex(x => x.Key == key); var target = index + Math.Sign(step);
        if (index < 0 || target < 0 || target >= rows.Count) return Save(rows);
        (rows[index], rows[target]) = (rows[target], rows[index]);
        return Save(rows);
    }
    public static string Show(string? saved, IEnumerable<LibrarySection> libraries, string key, bool shown) =>
        Save(Rows(saved, libraries).Select(x => x.Key == key ? x with { Shown = shown } : x));
}
