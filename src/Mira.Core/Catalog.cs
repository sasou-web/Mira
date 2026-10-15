namespace Mira.Core;

/// <summary>
/// The catalogue of the Windows app, free of WPF: which list is shown (page, favourites, library, person, saga), what Jellyfin
/// gave for it, « Continuer à regarder » and the rules that turn them into titles, the banner, an empty page and the
/// demo. MainWindow.Catalog keeps one and draws it.
/// </summary>
public sealed class Catalog
{
    /// <summary>"home", "Movie", "Series", "library", "search" or "offline" (the titles kept on this PC, from no request).</summary>
    public string View { get; set; } = "home";
    /// <summary>Only the favourites of the current view (films, series, library, search): a filter like the others.</summary>
    public bool Favorites { get; set; }
    /// <summary>One Jellyfin library, or all of them.</summary>
    public string? ParentId { get; set; }
    /// <summary>
    /// Only the titles of one actor or director, chosen from a title page (Directed says which) or from a search (null:
    /// Jellyfin does not say what they did).
    /// </summary>
    public (string Id, string Name, bool? Directed)? Person { get; set; }
    /// <summary>The actors and directors whose name matches the search, shown above its titles (<see cref="SearchesPeople"/>).</summary>
    public List<MediaItem> People { get; set; } = [];
    /// <summary>Only the titles of one saga (a Jellyfin collection) found by a search, in the order they came out.</summary>
    public (string Id, string Name)? Saga { get; set; }
    /// <summary>The episodes and the sagas whose name matches the search, shown under its titles, as in Mira web.</summary>
    public List<MediaItem> Episodes { get; set; } = [];
    public List<MediaItem> Sagas { get; set; } = [];
    public List<MediaItem> Items { get; set; } = [];
    /// <summary>How many titles the list has on Jellyfin, beyond the pages loaded.</summary>
    public int TotalCount { get; set; }
    public List<MediaItem> Resume { get; set; } = [];
    public List<MediaItem> NextUp { get; set; } = [];
    /// <summary>What was played on this PC, newest first, kept in the library cache.</summary>
    public List<MediaItem> RecentPlayback { get; set; } = [];
    /// <summary>Films and series taken out of « Continuer à regarder », with when; kept per account in its library cache.</summary>
    public Dictionary<string, DateTimeOffset> HiddenResume { get; set; } = [];
    /// <summary>The home page's rows beside Continuer à regarder and the grid: « favorites », « library:id » (<see cref="HomeLayout"/>).</summary>
    public Dictionary<string, List<MediaItem>> Shelves { get; set; } = [];
    /// <summary>Nothing to show yet: the page shows placeholders.</summary>
    public bool Loading { get; set; }

    /// <summary>The home page, not searched: the banner, « Continuer à regarder » and the latest titles.</summary>
    public bool IsHome(string search) => View == "home" && search.Length == 0;
    /// <summary>Jellyfin's item types for this page.</summary>
    public string Types => View is "Movie" or "Series" ? View : "Movie,Series";
    /// <summary>Where a list is kept in the library cache.</summary>
    public string Key(string search, CatalogQuery query) => $"{View}:{(Favorites ? "favorites" : "")}:{Parent}:{search}:{query.CacheKey}";
    /// <summary>Where Jellyfin's titles are asked from: the saga shown, else the library chosen (or all of them).</summary>
    public string? Parent => Saga?.Id ?? ParentId;
    /// <summary>The order asked: a saga's titles in the order they came out, unless another one is chosen.</summary>
    public string Sort(string chosen) => Saga is not null && chosen is "" or "recent" ? "saga" : chosen;
    public string PageTitle => View switch { "Movie" => "Films", "Series" => "Séries & animes", "library" => "Ta bibliothèque", "offline" => "Sur ce PC", _ => "Explorer" };
    public string Description => Favorites ? "Les histoires que tu gardes à portée de main." : "Trouve ta prochaine séance dans ta bibliothèque.";
    public string GridTitle(string search) => IsHome(search) ? "Ajoutés récemment" : View == "offline" ? "Prêts sans connexion" : search.Length > 0 ? $"Résultats pour « {search} »"
        : Person is { } shown ? PersonText(shown.Name, shown.Directed) : Saga is { } saga ? saga.Name : Favorites ? "Tes favoris" : "Tous les titres";
    public static string PersonText(string name, bool? directed) => directed switch { true => "Réalisés par ", false => "Avec ", null => "" } + name;
    /// <summary>
    /// A search of two letters or more looks for people, episodes and sagas too, as Mira web does; not in a person's or a
    /// saga's titles nor « Sur ce PC ».
    /// </summary>
    public bool SearchesPeople(string search) => search.Length >= 2 && View != "offline" && Person is null && Saga is null;
    /// <summary>The search found people, episodes or sagas: without titles, the page shows them rather than « Aucun résultat ».</summary>
    public bool FoundBeside(string search) => SearchesPeople(search) && People.Count + Episodes.Count + Sagas.Count > 0;
    /// <summary>« KR » for Keanu Reeves: in a person's circle when Jellyfin has no photo, as Mira web.</summary>
    public static string Initials(string name)
    {
        var letters = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0]));
        return string.Concat(letters) is { Length: > 0 } text ? text : "?";
    }
    public static string CountText(int count) => $"{count:N0} titre{(count > 1 ? "s" : "")}";

    /// <summary>A new account or none: nothing of the previous one stays.</summary>
    public void Clear()
    {
        Items = []; Resume = []; NextUp = []; RecentPlayback = []; HiddenResume = []; People = []; Episodes = []; Sagas = []; TotalCount = 0;
    }
    /// <summary>A page from Jellyfin: the first replaces the list, the next ones (« Afficher plus ») add what is new.</summary>
    public void Receive(ItemsResult result, bool more)
    {
        if (more) Items.AddRange(result.Items.Where(x => Items.All(old => old.Id != x.Id))); else Items = result.Items;
        TotalCount = result.TotalRecordCount;
    }
    /// <summary>« Continuer à regarder »: Jellyfin's resume and next-up rows with what this PC played, without what was taken out.</summary>
    public List<MediaItem> ContinueRow() => ContinueWatching.WithoutHidden(ContinueWatching.Order(Resume, NextUp, RecentPlayback), HiddenResume, RecentPlayback);
    /// <summary>
    /// The titles of the banner: those of the row, then the latest, one per series, five at most. A title taken out of
    /// the row does not come back in the banner either.
    /// </summary>
    public List<MediaItem> HeroCandidates(IEnumerable<MediaItem> row)
    {
        var kept = row.Select(ContinueWatching.Group).ToHashSet();
        return Resume.Concat(NextUp).Where(x => kept.Contains(ContinueWatching.Group(x))).Concat(Items).DistinctBy(x => x.SeriesId ?? x.Id).Take(5).ToList();
    }
    /// <summary>The title playing, put first in the recent list and in « Continuer à regarder » while it has a position.</summary>
    public void Remember(MediaItem snapshot)
    {
        RecentPlayback = ContinueWatching.History(RecentPlayback.Append(snapshot));
        Resume.RemoveAll(x => x.Id == snapshot.Id);
        if (!snapshot.UserData.Played && snapshot.UserData.PlaybackPositionTicks > 0) Resume.Insert(0, snapshot);
    }
    /// <summary>
    /// Seen or not seen, in every list holding a copy (and in <paramref name="others"/>, the title page's): from the
    /// start next time. A series takes its episodes with it.
    /// </summary>
    public void MarkPlayed(MediaItem item, bool played, IReadOnlyCollection<MediaItem> episodes, IEnumerable<MediaItem> others)
    {
        RecentPlayback.RemoveAll(x => x.Id == item.Id || x.SeriesId == item.Id);
        foreach (var known in Items.Concat(Resume).Concat(NextUp).Concat(episodes).Concat(others).Append(item).Where(x => x.Id == item.Id))
        { known.UserData.Played = played; known.UserData.PlaybackPositionTicks = 0; }
        if (item.Type == "Series") foreach (var episode in episodes.Concat(Resume).Where(x => x.SeriesId == item.Id)) { episode.UserData.Played = played; episode.UserData.PlaybackPositionTicks = 0; }
    }
    /// <summary>A favourite or not, in every list holding a copy; out of the list when only favourites show. True when it left the list.</summary>
    public bool MarkFavorite(MediaItem item, bool favorite, IEnumerable<MediaItem> others)
    {
        foreach (var known in Items.Concat(Resume).Concat(NextUp).Concat(others).Where(x => x.Id == item.Id)) known.UserData.IsFavorite = favorite;
        if (!Favorites || favorite) return false;
        Items.RemoveAll(x => x.Id == item.Id); TotalCount = Math.Max(0, TotalCount - 1);
        return true;
    }

    /// <summary>What an empty page says: a search, filters, the favourites, or an empty library (then where videos go).</summary>
    public CatalogEmpty Empty(string search, CatalogQuery query, bool demo)
    {
        var filtered = query.Genre is not null || query.Year is not null || query.Played is not null || ParentId is not null && Saga is null || query.PersonId is not null;
        // Nothing at all in the library (not a search, a filter or the favourites): where videos go is what is missing.
        var emptyLibrary = !demo && search.Length == 0 && !filtered && !Favorites && View is "home" or "library" && Resume.Count == 0;
        var (icon, title, hint) = (search.Length, filtered) switch
        {
            ( > 0, _) => ("search", $"Aucun résultat pour « {search} »", "Vérifie l’orthographe ou essaie un autre titre."),
            (_, true) => ("sliders", "Aucun titre ne correspond à ces filtres", "Élargis ta sélection pour retrouver tes titres."),
            _ when View == "offline" => ("laptop", "Rien sur ce PC pour l’instant", "Sur la fiche d’un film ou d’une série, le bouton ⤓ garde le titre sur ce PC pour le regarder sans connexion."),
            _ when Favorites => ("heart", "Aucun favori ici pour l’instant", "Depuis la fiche d’un titre, choisis « Ajouter aux favoris » pour le retrouver ici."),
            _ when emptyLibrary => ("folder", "Ta bibliothèque est vide", "Range tes films et séries dans les dossiers de Jellyfin : ils apparaîtront ici."),
            _ when Saga is not null => ("film", "Aucun titre dans cette saga", "Ses films ne sont pas dans ta bibliothèque."),
            _ => ("library", "Rien à afficher ici", "Les titres ajoutés à ta bibliothèque Jellyfin apparaîtront ici.")
        };
        return new(icon, title, hint, Reset: filtered || search.Length > 0 || Favorites, Guide: emptyLibrary);
    }

    /// <summary>The demo's list for this page, searched, filtered and sorted as Jellyfin would.</summary>
    public void ShowDemo(List<MediaItem> all, string search, CatalogQuery query)
    {
        Resume = all.Where(x => x.Progress > 0).ToList(); NextUp = [];
        var items = all.Where(x => (View is not ("Movie" or "Series") || x.Type == View) && (!Favorites || x.UserData.IsFavorite) &&
            (search.Length == 0 || x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (View != "home")
        {
            items = items.Where(x => (query.Genre is null || x.Genres.Contains(query.Genre)) && (query.Year is null || x.ProductionYear == query.Year) && (query.Played is null || x.UserData.Played == query.Played)).ToList();
            items = query.Sort switch
            {
                "title" => items.OrderBy(x => x.Name).ToList(),
                "year" => items.OrderByDescending(x => x.ProductionYear).ToList(),
                "rating" => items.OrderByDescending(x => x.CommunityRating).ToList(),
                "played" => items.OrderByDescending(x => x.UserData.LastPlayedDate ?? DateTimeOffset.MinValue).ThenByDescending(x => x.Progress).ToList(),
                _ => items
            };
        }
        Items = items; TotalCount = items.Count;
    }
}

/// <summary>An empty page: its icon, title and hint, and whether « Réinitialiser » and the guide are offered.</summary>
public readonly record struct CatalogEmpty(string Icon, string Title, string Hint, bool Reset, bool Guide);
