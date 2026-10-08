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
/// A grid of the library: films, series, everything, a search, or the titles of one actor or director. Filters by
/// genre, year and watched state, sorts, keeps only favourites; more titles load as the grid scrolls.
/// </summary>
public sealed class BrowsePage : Page
{
    private readonly BrowseKind _kind;
    private readonly PersonInfo? _person;
    private readonly bool _director;
    private readonly CardGrid _grid = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _count = new() { FontSize = 14, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _search = new() { Watermark = "Un film, une série…", FontSize = 18, Padding = new Thickness(18, 14) };
    private readonly ComboBox _genre = new(), _year = new(), _state = new(), _sort = new();
    private readonly Button _favorites, _reset;
    private readonly TextBlock _empty = new() { FontSize = 15, Foreground = Ui.Muted, IsVisible = false, Margin = new Thickness(0, 20) };
    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly List<MediaItem> _items = [];
    private int _total, _version;
    private bool _loading, _filling, _favoritesOnly;
    private CancellationTokenSource? _request;
    public override string Rail => _person is not null ? "" : _kind switch { BrowseKind.Movies => "Movie", BrowseKind.Series => "Series", BrowseKind.Search => "search", _ => "library" };
    public IReadOnlyList<MediaItem> Items => _items;
    /// <summary>Types a search, as the field does (the self-check).</summary>
    public void Search(string text) => _search.Text = text;

    public BrowsePage(MainWindow shell, BrowseKind kind, PersonInfo? person = null, bool director = false) : base(shell)
    {
        _kind = kind; _person = person; _director = director;
        var title = person is not null ? (director ? "Réalisés par " : "Avec ") + person.Name
            : kind switch { BrowseKind.Movies => "Films", BrowseKind.Series => "Séries", BrowseKind.Search => "Rechercher", _ => "Bibliothèque" };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(_count, Dock.Right); header.Children.Add(_count);
        header.Children.Add(Ui.Text(title, 34, FontWeight.ExtraBold));

        _favorites = Ui.Action("Favoris", "heart", "chip", () => { _favoritesOnly = !_favoritesOnly; _favorites!.Classes.Set("on", _favoritesOnly); _ = ReloadAsync(); });
        _reset = Ui.Action("Réinitialiser", "refresh", "chip quiet", Reset);
        Fill(_state, "Tous", "Non vus", "Vus");
        Fill(_sort, "Ajouts récents", "Titre", "Année", "Note", "Derniers vus");
        _genre.PlaceholderText = "Genre"; _year.PlaceholderText = "Année";
        foreach (var (box, name) in new[] { (_genre, "Genre"), (_year, "Année"), (_state, "État"), (_sort, "Tri") })
        {
            Avalonia.Automation.AutomationProperties.SetName(box, name);
            box.SelectionChanged += (_, _) => { if (!_filling) _ = ReloadAsync(); };
        }
        var filters = new WrapPanel { ItemSpacing = 10, LineSpacing = 10, Margin = new Thickness(0, 0, 0, 26) };
        filters.Children.AddRange([_genre, _year, _state, _sort, _favorites, _reset]);

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
            var chip = Ui.Action((director ? "Réalisés par " : "Avec ") + person.Name, "close", "chip on", () => Shell.Back());
            ToolTip.SetTip(chip, "Retirer ce filtre");
            chip.HorizontalAlignment = HorizontalAlignment.Left; chip.Margin = new Thickness(0, -12, 0, 22);
            page.Children.Add(chip);
        }
        page.Children.Add(_empty);
        page.Children.Add(_grid);
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
        Sort: _sort.SelectedIndex switch { 1 => "title", 2 => "year", 3 => "rating", 4 => "played", _ => "recent" },
        PersonId: _person?.Id);
    private bool Filtered => _genre.SelectedIndex > 0 || _year.SelectedIndex > 0 || _state.SelectedIndex > 0 || _sort.SelectedIndex > 0 || _favoritesOnly;
    private void Reset()
    {
        _filling = true;
        _genre.SelectedIndex = 0; _year.SelectedIndex = 0; _state.SelectedIndex = 0; _sort.SelectedIndex = 0;
        _filling = false; _favoritesOnly = false; _favorites.Classes.Set("on", false);
        _ = ReloadAsync();
    }

    public override async Task LoadAsync()
    {
        if (Shell.Session is not { } session) return;
        if (_kind != BrowseKind.Search)
        {
            try
            {
                var filters = await session.Client.FiltersAsync();
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
        if (_kind == BrowseKind.Search && search.Length == 0) { _items.Clear(); _grid.Children.Clear(); _count.Text = ""; ShowEmpty("Tape le nom d’un film, d’une série ou d’un épisode."); return; }
        _request?.Cancel(); _request = new CancellationTokenSource();
        var ct = _request.Token; var version = ++_version;
        _loading = true;
        try
        {
            // A refresh keeps every page already loaded, so the grid does not shrink back under the reader.
            var limit = keep ? Math.Clamp(_items.Count, JellyfinClient.PageSize, 600) : JellyfinClient.PageSize;
            var result = await session.Client.BrowseAsync(Types, null, search.Length > 0 ? search : null, 0, _favoritesOnly, ct, Query, limit);
            await Task.Run(() => session.Store.ApplyLocalProgress(result.Items), CancellationToken.None);
            if (version != _version) return;
            _items.Clear(); _items.AddRange(result.Items); _total = result.TotalRecordCount;
            var offset = _scroll.Offset;
            _grid.Children.Clear();
            foreach (var item in result.Items) _grid.Children.Add(Cards.Poster(item, session.Images, Shell.Open, Shell.Menu));
            if (keep) _scroll.Offset = offset; else _scroll.Offset = default;
            Count();
            if (_items.Count == 0) ShowEmpty(_kind == BrowseKind.Search ? $"Rien ne correspond à « {search} »." : Filtered ? "Aucun titre ne correspond à ces filtres." : "Aucun titre dans cette bibliothèque pour l’instant.");
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
            var result = await session.Client.BrowseAsync(Types, null, search.Length > 0 ? search : null, _items.Count, _favoritesOnly, _request?.Token ?? default, Query);
            await Task.Run(() => session.Store.ApplyLocalProgress(result.Items));
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
    private void ShowEmpty(string text) { _empty.Text = text; _empty.IsVisible = true; }
}
