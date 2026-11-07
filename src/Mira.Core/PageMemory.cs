namespace Mira.Core;

/// <summary>
/// The rail's pages as they were left (Windows): Films → Accueil → Films comes back where the reader was, with the
/// filters, the search, the titles already loaded and the scroll, then refreshed quietly. A list reached from a title
/// page (a person, a studio, a collection) is not a page of the rail, and is not kept. Forgotten with the account.
/// </summary>
public sealed class PageMemory
{
    /// <summary>The choices of « Filtrer » by their tags, what was typed in Recherche, the titles and where the list was scrolled to.</summary>
    public sealed record State(string Genre, string GenreLabel, string Year, string Watched, string Sort, string Library, bool Favorites, bool FiltersOpen, string Search,
        List<MediaItem> Items, int TotalCount, double Offset, string Quality = "", string Rating = "", string Audio = "", string Subtitles = "");

    private readonly Dictionary<string, State> _pages = [];

    /// <summary>
    /// The rail page the catalogue shows (« Movie », « library:&lt;id&gt; »…), or null for one the rail does not lead to:
    /// a person's, a studio's or a collection's titles, and « Sur ce PC » (its own list, read from this PC at once).
    /// </summary>
    public static string? KeyOf(Catalog catalog) =>
        catalog.Person is not null || catalog.Studio is not null || catalog.Saga is not null || catalog.View == "offline" ? null
        : catalog.View == "library" ? "library:" + catalog.ParentId : catalog.View;

    /// <summary>Kept as left; a page still loading (nothing to come back to) is not.</summary>
    public void Keep(string? key, State state, bool loading)
    {
        if (key is null || loading) return;
        _pages[key] = state with { Items = [.. state.Items] };
    }

    /// <summary>The page as it was left.</summary>
    public State? Recall(string? key) => key is not null && _pages.TryGetValue(key, out var state) ? state : null;

    /// <summary>
    /// Whether the catalogue already shows the rail page <paramref name="view"/> (one library for « library », with
    /// <paramref name="parentId"/>): its button clicked again, or its Ctrl+1 to 5, reloads nothing and replays no
    /// arrival; the list only glides back to the top. A person's, a studio's or a collection's titles are another page.
    /// </summary>
    public static bool Shows(Catalog catalog, string view, string? parentId) =>
        catalog.Person is null && catalog.Studio is null && catalog.Saga is null && catalog.View == view && (view != "library" || catalog.ParentId == parentId);

    public void Clear() => _pages.Clear();
}
