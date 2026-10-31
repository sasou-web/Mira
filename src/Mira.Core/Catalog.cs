namespace Mira.Core;

/// <summary>
/// The catalogue of the Windows app, free of WPF: which list is shown (page, favourites, library, person, studio, saga), what Jellyfin
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
    /// <summary>Only the titles of one studio (« Studio Ghibli »), chosen from a title page or from a search.</summary>
    public (string Id, string Name)? Studio { get; set; }
    /// <summary>The studios whose name matches the search (« Ghibli »), shown above its titles: a title's search never looks at them.</summary>
    public List<MediaItem> Studios { get; set; } = [];
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
    public string PageTitle => View switch { "Movie" => L("Films", "Movies"), "Series" => L("Séries & animes", "Shows & anime"), "library" => L("Ta bibliothèque", "Your library"), "offline" => L("Sur ce PC", "On this PC"), "search" => L("Rechercher", "Search"), _ => L("Explorer", "Browse") };
    public string Description => View == "search" ? L("Un film, une série, un épisode, une collection, un acteur ou un studio de ta bibliothèque.", "A movie, a show, an episode, a collection, an actor or a studio from your library.")
        : Favorites ? L("Les histoires que tu gardes à portée de main.", "The stories you keep close at hand.") : L("Trouve ta prochaine séance dans ta bibliothèque.", "Find your next show in your library.");
    /// <summary>The search page before anything is typed: an invitation, not every title (Jellyfin is not asked).</summary>
    public bool AwaitsSearch(string search) => View == "search" && search.Length == 0 && Person is null && Studio is null && Saga is null;
    /// <summary>
    /// The filters behind « Filtrer » that are not at « Tous »: genre, year, progress, library. The sort and the
    /// favourites stay in sight, beside it.
    /// </summary>
    public int FilterCount(CatalogQuery query) => (query.Genre is null ? 0 : 1) + (query.Year is null ? 0 : 1) + (query.Played is null ? 0 : 1) + (ParentId is not null && Saga is null ? 1 : 0);
    /// <summary>« Filtrer », or « Filtres · 2 » while two of them narrow the list.</summary>
    public static string FiltersLabel(int count) => count == 0 ? L("Filtrer", "Filter") : L($"Filtres · {count}", $"Filters · {count}");
    public string GridTitle(string search) => IsHome(search) ? L("Ajoutés récemment", "Recently added") : View == "offline" ? L("Prêts sans connexion", "Ready offline") : search.Length > 0 ? L($"Résultats pour « {search} »", $"Results for “{search}”")
        : Person is { } shown ? PersonText(shown.Name, shown.Directed) : Studio is { } studio ? studio.Name : Saga is { } saga ? saga.Name : Favorites ? L("Tes favoris", "Your favorites") : L("Tous les titres", "All titles");
    public static string PersonText(string name, bool? directed) => directed switch { true => L("Réalisés par ", "Directed by "), false => L("Avec ", "With "), null => "" } + name;
    /// <summary>
    /// A search of two letters or more looks for people, studios, episodes and sagas too, as Mira web does; not in a
    /// person's, a studio's or a saga's titles nor « Sur ce PC ».
    /// </summary>
    public bool SearchesPeople(string search) => search.Length >= 2 && View != "offline" && Person is null && Studio is null && Saga is null;
    /// <summary>The search found people, studios, episodes or sagas: without titles, the page shows them rather than « Aucun résultat ».</summary>
    public bool FoundBeside(string search) => SearchesPeople(search) && People.Count + Studios.Count + Episodes.Count + Sagas.Count > 0;
    /// <summary>« KR » for Keanu Reeves: in a person's circle when Jellyfin has no photo, as Mira web.</summary>
    public static string Initials(string name)
    {
        var letters = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(x => char.ToUpperInvariant(x[0]));
        return string.Concat(letters) is { Length: > 0 } text ? text : "?";
    }
    public static string CountText(int count) => count.ToString("N0", Culture) + " " + Plural(count, "titre", "titres", "title", "titles");

    /// <summary>A new account or none: nothing of the previous one stays.</summary>
    public void Clear()
    {
        Items = []; Resume = []; NextUp = []; RecentPlayback = []; HiddenResume = []; People = []; Studios = []; Episodes = []; Sagas = []; TotalCount = 0;
    }
    /// <summary>A page from Jellyfin: the first replaces the list, the next ones (loaded as the grid scrolls) add what is new.</summary>
    public void Receive(ItemsResult result, bool more)
    {
        if (more) Items.AddRange(result.Items.Where(x => Items.All(old => old.Id != x.Id))); else Items = result.Items;
        TotalCount = result.TotalRecordCount;
    }
    /// <summary>« Continuer à regarder »: Jellyfin's resume and next-up rows with what this PC played, without what was taken out.</summary>
    /// <summary>
    /// On top of Films, the film last started; on top of Séries, the episode the show last watched picks up at: each page
    /// its own way back in. Only on the whole list (no search, filter, favourites or person), never on Home, which has its row.
    /// </summary>
    public MediaItem? PickUp(string search, CatalogQuery query) =>
        View is not ("Movie" or "Series") || search.Length > 0 || Favorites || Person is not null || Studio is not null || Saga is not null || FilterCount(query) > 0 ? null
        : ContinueRow().FirstOrDefault(x => x.Type == (View == "Movie" ? "Movie" : "Episode"));
    /// <summary>« À REPRENDRE », « À SUIVRE » (an episode not begun yet) above its title.</summary>
    public static string PickUpLabel(MediaItem item) => item.Progress > 0 ? L("À REPRENDRE", "PICK UP WHERE YOU LEFT") : L("À SUIVRE", "UP NEXT");
    /// <summary>« 2023 · 2 h 20 · 1 h 27 restantes », « S02 E04 · L’épisode · 12 min restantes ».</summary>
    public static string PickUpLine(MediaItem item) => string.Join("  ·  ", new[]
    {
        item.Type == "Episode" ? $"S{item.ParentIndexNumber ?? 1:00} E{item.IndexNumber ?? 1:00}" : item.ProductionYear?.ToString() ?? "",
        item.Type == "Episode" ? item.Name : item.DurationLabel, MediaQuality.Left(item) ?? ""
    }.Where(x => x.Length > 0));
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
    /// <summary>A film or series gone from Jellyfin (sent to the Recycle Bin from Mira): out of every list, its episodes with it.</summary>
    public void Forget(string id)
    {
        bool Of(MediaItem x) => x.Id == id || x.SeriesId == id;
        TotalCount -= Items.RemoveAll(Of);
        Resume.RemoveAll(Of); NextUp.RemoveAll(Of); RecentPlayback.RemoveAll(Of); Episodes.RemoveAll(Of);
        foreach (var shelf in Shelves.Values) shelf.RemoveAll(Of);
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
        var filtered = query.Genre is not null || query.Year is not null || query.Played is not null || ParentId is not null && Saga is null || query.PersonId is not null || query.StudioId is not null;
        // Nothing at all in the library (not a search, a filter or the favourites): where videos go is what is missing.
        var emptyLibrary = !demo && search.Length == 0 && !filtered && !Favorites && View is "home" or "library" && Resume.Count == 0;
        var (icon, title, hint) = (search.Length, filtered) switch
        {
            _ when AwaitsSearch(search) => ("search", L("Que veux-tu regarder ?", "What do you want to watch?"), L("Tape le nom d’un film, d’une série, d’un épisode, d’une collection, d’un acteur ou d’un studio.", "Type the name of a movie, a show, an episode, a collection, an actor or a studio.")),
            ( > 0, _) => ("search", L($"Aucun résultat pour « {search} »", $"No results for “{search}”"), L("Vérifie l’orthographe ou essaie un autre titre.", "Check the spelling or try another title.")),
            (_, true) => ("sliders", L("Aucun titre ne correspond à ces filtres", "No titles match these filters"), L("Élargis ta sélection pour retrouver tes titres.", "Widen your selection to find your titles.")),
            _ when View == "offline" => ("laptop", L("Rien sur ce PC pour l’instant", "Nothing on this PC yet"), L("Sur la fiche d’un film ou d’une série, le bouton ⤓ garde le titre sur ce PC pour le regarder sans connexion.", "On a movie or show’s page, the ⤓ button keeps the title on this PC to watch offline.")),
            _ when Favorites => ("heart", L("Aucun favori ici pour l’instant", "No favorites here yet"), L("Depuis la fiche d’un titre, choisis « Ajouter aux favoris » pour le retrouver ici.", "On a title’s page, choose “Add to favorites” to find it here.")),
            _ when emptyLibrary => ("folder", L("Ta bibliothèque est vide", "Your library is empty"), L("Range tes films et séries dans les dossiers de Jellyfin : ils apparaîtront ici.", "Put your movies and shows in Jellyfin’s folders: they’ll show up here.")),
            _ when Saga is not null => ("film", L("Aucun titre dans cette collection", "No titles in this collection"), L("Ajoute-y un film ou une série depuis son menu « Ajouter à une collection… ».", "Add a movie or show to it from its menu, “Add to collection…”.")),
            _ => ("library", L("Rien à afficher ici", "Nothing to show here"), L("Les titres ajoutés à ta bibliothèque Jellyfin apparaîtront ici.", "Titles added to your Jellyfin library will show up here."))
        };
        return new(icon, title, hint, Reset: !AwaitsSearch(search) && (filtered || search.Length > 0 || Favorites), Guide: emptyLibrary);
    }

    /// <summary>The demo's list for this page, searched, filtered and sorted as Jellyfin would.</summary>
    public void ShowDemo(List<MediaItem> all, string search, CatalogQuery query)
    {
        Resume = all.Where(x => x.Progress > 0).ToList(); NextUp = [];
        if (AwaitsSearch(search)) { Items = []; TotalCount = 0; return; }
        var items = all.Where(x => (View is not ("Movie" or "Series") || x.Type == View) && (!Favorites || x.UserData.IsFavorite) &&
            (search.Length == 0 || x.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (View != "home")
        {
            items = items.Where(x => (query.Genre is null || GenreGroups.Has(x.Genres, query.Genre)) && (query.Year is null || x.ProductionYear == query.Year) && (query.Played is null || x.UserData.Played == query.Played)).ToList();
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
