using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

public enum BrowseKind { Movies, Series, All, Search }

/// <summary>
/// A grid of the library: films, series, everything, a search, the titles of one actor or director, or a saga's.
/// Filters by genre, year and watched state, sorts, keeps only favourites; more titles load as the grid scrolls. A
/// search also finds people, above its titles, and episodes and sagas, under them.
/// </summary>
public sealed class BrowsePage : Page
{
    private readonly BrowseKind _kind;
    private readonly PersonInfo? _person;
    private readonly bool? _director;
    /// <summary>A saga (a Jellyfin collection): its titles in the order they came out, unless another order is chosen.</summary>
    private readonly MediaItem? _saga;
    private readonly CardGrid _grid = new();
    /// <summary>« Personnes »: the actors and directors a search finds, as in Mira web.</summary>
    private readonly WrapPanel _people = new() { ItemSpacing = 18, LineSpacing = 18 };
    private readonly StackPanel _peopleSection = new() { Spacing = 16, IsVisible = false, Margin = new Thickness(0, 0, 0, 30) };
    private readonly List<MediaItem> _found = [];
    /// <summary>« Épisodes » and « Sagas »: what a search finds beside films and series, under them, as in Mira web.</summary>
    private readonly WrapPanel _episodes = new() { ItemSpacing = 18, LineSpacing = 18 }, _sagas = new() { ItemSpacing = 18, LineSpacing = 18 };
    private readonly StackPanel _episodesSection = new() { Spacing = 16, IsVisible = false, Margin = new Thickness(0, 30, 0, 0) },
        _sagasSection = new() { Spacing = 16, IsVisible = false, Margin = new Thickness(0, 30, 0, 0) };
    private List<MediaItem> _foundEpisodes = [], _foundSagas = [];
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _count = new() { FontSize = 14, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _search = new() { Watermark = "Un film, une série, un acteur…", FontSize = 18, Padding = new Thickness(18, 14) };
    private readonly ComboBox _genre = new(), _year = new(), _state = new(), _sort = new(), _library = new() { IsVisible = false };
    /// <summary>The libraries of this kind (Séries and Animes), when there are several: one at a time, or all of them.</summary>
    private List<LibrarySection> _libraries = [];
    private readonly Button _favorites, _reset;
    private readonly TextBlock _empty = new() { FontSize = 15, Foreground = Ui.Muted, IsVisible = false, Margin = new Thickness(0, 20) };
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly List<MediaItem> _items = [];
    private int _total, _version;
    private bool _loading, _filling, _favoritesOnly;
    private CancellationTokenSource? _request;
    public override string Rail => _person is not null || _saga is not null ? "" : _kind switch { BrowseKind.Movies => "Movie", BrowseKind.Series => "Series", BrowseKind.Search => "search", _ => "library" };
    public IReadOnlyList<MediaItem> Items => _items;
    /// <summary>The people the search found, shown above its titles.</summary>
    public IReadOnlyList<MediaItem> People => _found;
    /// <summary>The episodes and the sagas the search found, shown under its titles.</summary>
    public IReadOnlyList<MediaItem> FoundEpisodes => _foundEpisodes;
    public IReadOnlyList<MediaItem> FoundSagas => _foundSagas;
    /// <summary>Types a search, as the field does (the self-check).</summary>
    public void Search(string text) => _search.Text = text;

    public BrowsePage(MainWindow shell, BrowseKind kind, PersonInfo? person = null, bool? director = false, MediaItem? saga = null) : base(shell)
    {
        _kind = kind; _person = person; _director = director; _saga = saga;
        var title = person is not null ? Catalog.PersonText(person.Name, director) : saga is not null ? saga.Name
            : kind switch { BrowseKind.Movies => "Films", BrowseKind.Series => "Séries", BrowseKind.Search => "Rechercher", _ => "Bibliothèque" };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(_count, Dock.Right); header.Children.Add(_count);
        header.Children.Add(Ui.Text(title, 34, FontWeight.ExtraBold));

        _favorites = Ui.Action("Favoris", "heart", "chip", () => { _favoritesOnly = !_favoritesOnly; _favorites!.Classes.Set("on", _favoritesOnly); _ = ReloadAsync(); });
        _reset = Ui.Action("Réinitialiser", "refresh", "chip quiet", Reset);
        Fill(_state, "Tous", "Non vus", "Vus");
        if (saga is not null) Fill(_sort, "Ordre de sortie", "Titre", "Année", "Note", "Derniers vus");
        else Fill(_sort, "Ajouts récents", "Titre", "Année", "Note", "Derniers vus");
        _genre.PlaceholderText = "Genre"; _year.PlaceholderText = "Année";
        foreach (var (box, name) in new[] { (_library, "Bibliothèque"), (_genre, "Genre"), (_year, "Année"), (_state, "État"), (_sort, "Tri") })
        {
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => { if (!_filling) _ = ReloadAsync(); };
        }
        var filters = new WrapPanel { ItemSpacing = 10, LineSpacing = 10, Margin = new Thickness(0, 0, 0, 26) };
        filters.Children.AddRange([_library, _genre, _year, _state, _sort, _favorites, _reset]);

        var page = new StackPanel { Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 60) };
        page.Children.Add(header);
        if (kind == BrowseKind.Search)
        {
            _search.Margin = new Thickness(0, 0, 0, 22);
            _search.TextChanged += (_, _) => { _typing.Stop(); _typing.Start(); };
            _typing.Tick += (_, _) => { _typing.Stop(); _ = ReloadAsync(); };
            Avalonia.Automation.AutomationProperties.SetName(_search, "Rechercher dans la bibliothèque");
            page.Children.Add(_search);
            AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        }
        else page.Children.Add(filters);
        if (person is not null)
        {
            var chip = Ui.Action(Catalog.PersonText(person.Name, director), "close", "chip on", () => Shell.Back());
            ToolTip.SetTip(chip, "Retirer ce filtre");
            chip.HorizontalAlignment = HorizontalAlignment.Left; chip.Margin = new Thickness(0, -12, 0, 22);
            page.Children.Add(chip);
        }
        if (saga is not null)
        {
            var chip = Ui.Action("Saga", "close", "chip on", () => Shell.Back());
            ToolTip.SetTip(chip, "Retirer ce filtre");
            chip.HorizontalAlignment = HorizontalAlignment.Left; chip.Margin = new Thickness(0, -12, 0, 22);
            page.Children.Add(chip);
        }
        page.Children.Add(_empty);
        _peopleSection.Children.Add(Ui.Text("Personnes", 22, FontWeight.Bold));
        _peopleSection.Children.Add(_people);
        page.Children.Add(_peopleSection);
        page.Children.Add(_grid);
        _episodesSection.Children.Add(Ui.Text("Épisodes", 22, FontWeight.Bold));
        _episodesSection.Children.Add(_episodes);
        _sagasSection.Children.Add(Ui.Text("Sagas", 22, FontWeight.Bold));
        _sagasSection.Children.Add(_sagas);
        page.Children.Add(_episodesSection); page.Children.Add(_sagasSection);
        _scroll = new ScrollViewer { Content = page };
        _scroll.ScrollChanged += (_, _) => { if (_scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 700) _ = MoreAsync(); };
        Content = _scroll;
    }
    private static void Fill(ComboBox box, params string[] items) { foreach (var item in items) box.Items.Add(item); box.SelectedIndex = 0; }

    private string Types => _kind switch { BrowseKind.Movies => "Movie", BrowseKind.Series => "Series", _ => "Movie,Series" };
    private CatalogQuery Query => new(
        Genre: _genre.SelectedIndex > 0 ? _genre.SelectedItem as string : null,
        Year: _year.SelectedIndex > 0 && int.TryParse(_year.SelectedItem as string, out var year) ? year : null,
        Played: _state.SelectedIndex switch { 1 => false, 2 => true, _ => null },
        Sort: _sort.SelectedIndex switch { 1 => "title", 2 => "year", 3 => "rating", 4 => "played", _ => _saga is not null ? "saga" : "recent" },
        PersonId: _person?.Id);
    private string? Library => _saga?.Id ?? (_library.SelectedIndex > 0 && _library.SelectedIndex <= _libraries.Count ? _libraries[_library.SelectedIndex - 1].Id : null);
    private bool Filtered => _library.SelectedIndex > 0 || _genre.SelectedIndex > 0 || _year.SelectedIndex > 0 || _state.SelectedIndex > 0 || _sort.SelectedIndex > 0 || _favoritesOnly;
    private void Reset()
    {
        _filling = true;
        if (_library.ItemCount > 0) _library.SelectedIndex = 0;
        _genre.SelectedIndex = 0; _year.SelectedIndex = 0; _state.SelectedIndex = 0; _sort.SelectedIndex = 0;
        _filling = false; _favoritesOnly = false; _favorites.Classes.Set("on", false);
        _ = ReloadAsync();
    }

    public override async Task LoadAsync()
    {
        if (Shell.Session is not { } session) return;
        if (_kind != BrowseKind.Search)
        {
            // Asked together: the grid waits for both.
            var libraries = session.Client.LibrariesAsync();
            var options = session.Client.FiltersAsync();
            try
            {
                var kinds = _kind switch { BrowseKind.Movies => ["movies"], BrowseKind.Series => ["tvshows"], _ => new[] { "movies", "tvshows" } };
                _libraries = (await libraries).Where(x => kinds.Contains(x.CollectionType)).ToList();
                _filling = true;
                _library.Items.Add("Toutes les bibliothèques"); foreach (var library in _libraries) _library.Items.Add(library.Name);
                _library.SelectedIndex = 0; _library.IsVisible = _person is null && _saga is null && _libraries.Count > 1;
                _filling = false;
            }
            catch (Exception ex) when (Errors.Expected(ex)) { _filling = false; }
            try
            {
                var filters = await options;
                _filling = true;
                _genre.Items.Add("Tous les genres"); foreach (var genre in filters.Genres) _genre.Items.Add(genre);
                _year.Items.Add("Toutes les années"); foreach (var year in filters.Years.OrderDescending()) _year.Items.Add(year.ToString());
                _genre.SelectedIndex = 0; _year.SelectedIndex = 0;
                _filling = false;
            }
            catch (Exception ex) when (Errors.Expected(ex)) { _filling = false; }
        }
        await ReloadAsync();
    }
    public override Task RefreshAsync() => ReloadAsync(keep: true);

    private async Task ReloadAsync(bool keep = false)
    {
        if (Shell.Session is not { } session) return;
        _reset.IsVisible = Filtered;
        var search = _search.Text?.Trim() ?? "";
        if (_kind == BrowseKind.Search && search.Length == 0) { _items.Clear(); _grid.Children.Clear(); ShowPeople([]); ShowFound([], []); _count.Text = ""; ShowEmpty("Tape le nom d’un film, d’une série, d’un épisode, d’une saga ou d’un acteur."); return; }
        _request?.Cancel(); _request = new CancellationTokenSource();
        var ct = _request.Token; var version = ++_version;
        _loading = true;
        try
        {
            // A refresh keeps every page already loaded, so the grid does not shrink back under the reader.
            var limit = keep ? Math.Clamp(_items.Count, JellyfinClient.PageSize, 600) : JellyfinClient.PageSize;
            var others = _kind == BrowseKind.Search && search.Length >= 2;
            var people = others ? session.Client.PersonsAsync(search, ct) : null;
            var episodes = others ? session.Client.SearchOthersAsync("Episode", search, ct) : null;
            var sagas = others ? session.Client.SearchOthersAsync("BoxSet", search, ct) : null;
            var result = await session.Client.BrowseAsync(Types, Library, search.Length > 0 ? search : null, 0, _favoritesOnly, ct, Query, limit);
            await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(result.Items)), CancellationToken.None);
            // The people, episodes and sagas are a bonus: the titles show even if Jellyfin does not answer for them.
            async Task<List<MediaItem>> Bonus(Task<List<MediaItem>>? asked)
            {
                if (asked is not null) try { return await asked; } catch (Exception ex) when (!ct.IsCancellationRequested && Errors.Expected(ex)) { }
                return [];
            }
            var found = await Bonus(people); var foundEpisodes = await Bonus(episodes); var foundSagas = await Bonus(sagas);
            if (version != _version) return;
            ShowPeople(found); ShowFound(foundEpisodes, foundSagas);
            _items.Clear(); _items.AddRange(result.Items); _total = result.TotalRecordCount;
            var offset = _scroll.Offset;
            _grid.Children.Clear();
            foreach (var item in result.Items) _grid.Children.Add(Cards.Poster(item, session.Images, Shell.Open, Shell.Menu));
            if (keep) _scroll.Offset = offset; else _scroll.Offset = default;
            Count();
            if (_items.Count == 0 && _found.Count + _foundEpisodes.Count + _foundSagas.Count == 0)
                ShowEmpty(_kind == BrowseKind.Search ? $"Rien ne correspond à « {search} »." : Filtered ? "Aucun titre ne correspond à ces filtres."
                    : _saga is not null ? "Les films de cette saga ne sont pas dans ta bibliothèque." : "Aucun titre dans cette bibliothèque pour l’instant.");
            else _empty.IsVisible = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { if (version == _version) Shell.Notice(Errors.Friendly(ex), "Réessayer", () => _ = ReloadAsync()); }
        finally { if (version == _version) _loading = false; }
    }
    private async Task MoreAsync()
    {
        if (_loading || _items.Count >= _total || Shell.Session is not { } session) return;
        _loading = true; var version = _version;
        try
        {
            var search = _search.Text?.Trim() ?? "";
            var result = await session.Client.BrowseAsync(Types, Library, search.Length > 0 ? search : null, _items.Count, _favoritesOnly, _request?.Token ?? default, Query);
            await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(result.Items)));
            if (version != _version) return;
            foreach (var item in result.Items.Where(x => _items.All(old => old.Id != x.Id)))
            { _items.Add(item); _grid.Children.Add(Cards.Poster(item, session.Images, Shell.Open, Shell.Menu)); }
            _total = result.Items.Count == 0 ? _items.Count : result.TotalRecordCount;
            Count();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
        finally { if (version == _version) _loading = false; }
    }
    private void Count() => _count.Text = _total == 1 ? "1 titre" : $"{_total} titres";
    /// <summary>The people found, drawn again only when they change (their photos do not flash at each refresh).</summary>
    private void ShowPeople(List<MediaItem> people)
    {
        _peopleSection.IsVisible = people.Count > 0;
        if (people.Select(x => x.Id).SequenceEqual(_found.Select(x => x.Id))) return;
        _found.Clear(); _found.AddRange(people); _people.Children.Clear();
        foreach (var person in people)
            _people.Children.Add(Cards.Person(person, Shell.Session?.Images, x => Shell.OpenPerson(new PersonInfo { Id = x.Id, Name = x.Name }, null)));
    }
    /// <summary>The episodes (opened at their series' season) and the sagas found, drawn again only when they change.</summary>
    private void ShowFound(List<MediaItem> episodes, List<MediaItem> sagas)
    {
        _episodesSection.IsVisible = episodes.Count > 0; _sagasSection.IsVisible = sagas.Count > 0;
        if (Shell.Session is not { } session) return;
        if (!episodes.Select(x => x.Id).SequenceEqual(_foundEpisodes.Select(x => x.Id)))
        {
            _foundEpisodes = episodes; _episodes.Children.Clear();
            foreach (var episode in episodes) _episodes.Children.Add(Cards.Wide(episode, session.Images, Shell.Open, menu: Shell.Menu));
        }
        if (!sagas.Select(x => x.Id).SequenceEqual(_foundSagas.Select(x => x.Id)))
        {
            _foundSagas = sagas; _sagas.Children.Clear();
            foreach (var saga in sagas) { var card = Cards.Poster(saga, session.Images, Shell.Open, Shell.Menu); card.Width = 168; _sagas.Children.Add(card); }
        }
    }
    private void ShowEmpty(string text) { _empty.Text = text; _empty.IsVisible = true; }
}
