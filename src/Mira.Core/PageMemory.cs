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
        List<MediaItem> Items, int TotalCount, double Offset);

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

    /// <summary>
    /// The page as it was left. Its own rail button clicked again while it is on screen (no Réglages, Guide or title page
    /// over it) starts it afresh, at the top: the way back up a long list.
    /// </summary>
    public State? Recall(string? key, bool again)
    {
        if (key is null) return null;
        if (again) { _pages.Remove(key); return null; }
        return _pages.TryGetValue(key, out var state) ? state : null;
    }

    public void Clear() => _pages.Clear();
}
