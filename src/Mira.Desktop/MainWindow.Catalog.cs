using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private bool _filterBusy;
    private List<MediaItem> _heroCandidates = [];
    private string? _heroSelectedId;
    private static string Choice(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private CatalogQuery CurrentQuery() => new(Choice(GenreFilter) is { Length: > 0 } genre ? genre : null,
        int.TryParse(Choice(YearFilter), out var year) ? year : null,
        Choice(WatchedFilter) switch { "played" => true, "unplayed" => false, _ => null }, _catalog.Sort(Choice(SortFilter)), _catalog.Person?.Id, _catalog.Studio?.Id);
    /// <summary>The title page a person's titles were chosen from (<see cref="Catalog.Person"/>).</summary>
    private MediaItem? _personFrom;
    /// <summary>The search a person or a saga was found by (« Personnes », « Sagas »): taken back when their titles are left.</summary>
    private string? _personSearch;
    private string _personReturnView = "home";
    /// <summary>
    /// A name clicked on a title page, or found by a search (<paramref name="search"/>; <paramref name="directed"/> is
    /// then unknown): the library with this person's titles, the page or the search kept to come back to.
    /// </summary>
    private async Task ShowPersonAsync(PersonInfo person, bool? directed, string? search = null)
    {
        if (person.Id is not { Length: > 0 } id) return;
        var from = search is null ? _detail : null; var view = _catalog.View;
        ResetFilters();
        (_catalog.Person, _personFrom, _personSearch, _personReturnView, _catalog.View) = ((id, person.Name, directed), from, search, view, "library");
        ShowPersonFilter();
        await NavigateLibraryAsync();
    }
    /// <summary>
    /// A studio clicked on a title page, or found by a search (<paramref name="search"/>): the library with its titles,
    /// newest first, the page or the search kept to come back to, as for a person.
    /// </summary>
    private async Task ShowStudioAsync(string id, string name, string? search = null)
    {
        var from = search is null ? _detail : null; var view = _catalog.View;
        ResetFilters();
        (_catalog.Studio, _personFrom, _personSearch, _personReturnView, _catalog.View) = ((id, name), from, search, view, "library");
        _filterBusy = true; SortFilter.SelectedItem = SortFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == "year") ?? SortFilter.SelectedItem; _filterBusy = false;
        ShowPersonFilter();
        await NavigateLibraryAsync();
    }
    /// <summary>A genre clicked on a title page: Films or Séries (as the title is) filtered on it, as Mira web.</summary>
    private async Task ShowGenreAsync(string genre, string type)
    {
        ResetFilters(); _catalog.View = type == "Series" ? "Series" : "Movie";
        // Its names in every language Jellyfin knows it by: « Comédie » finds the titles under « Comedy » too.
        var known = GenreFilter.Items.OfType<ComboBoxItem>().SelectMany(x => (x.Tag?.ToString() ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            .Concat(_pageGenres.Values.SelectMany(x => x).SelectMany(x => x.Value.Split('|')));
        var group = GenreGroups.Of(genre, known);
        _filterBusy = true;
        if (GenreFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == group.Value) is not { } choice)
            GenreFilter.Items.Add(choice = new ComboBoxItem { Content = group.Label, Tag = group.Value });
        GenreFilter.SelectedItem = choice; _filterBusy = false;
        await NavigateLibraryAsync();
    }
    /// <summary>A collection (a saga, or one made by hand) found by a search or on Home: the library with its titles in the order they came out, the search kept to come back to.</summary>
    private async Task ShowSagaAsync(MediaItem saga, string search)
    {
        var view = _catalog.View;
        ResetFilters();
        (_catalog.Saga, _personSearch, _personReturnView, _catalog.View) = ((saga.Id, saga.Name), search, view, "library");
        ShowPersonFilter();
        await NavigateLibraryAsync();
    }
    /// <summary>Back from a person's titles: the page they were chosen from, or the search, over the view it was opened from.</summary>
    private async Task BackFromPersonAsync()
    {
        var title = _personFrom; var search = _personSearch; var view = _personReturnView;
        ResetFilters(); _catalog.View = view;
        if (search is not null) { _filterBusy = true; SearchBox.Text = search; _filterBusy = false; }
        await NavigateLibraryAsync();
        if (title is not null) await ShowDetailsAsync(title);
    }
    private async void PersonFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_personSearch is not null) { await BackFromPersonAsync(); return; }
        _catalog.Person = null; _catalog.Studio = null; _catalog.Saga = null; _personFrom = null; ShowPersonFilter(); await RefreshAsync();
    }
    /// <summary>The chip of a person's titles, of a studio's or of a saga's: a click takes it off.</summary>
    private void ShowPersonFilter()
    {
        PersonFilter.Visibility = _catalog.Person is null && _catalog.Studio is null && _catalog.Saga is null ? Visibility.Collapsed : Visibility.Visible;
        PersonFilterText.Text = _catalog.Person is { } person ? Catalog.PersonText(person.Name, person.Directed) : _catalog.Studio is { } studio ? studio.Name : _catalog.Saga is { } saga ? saga.Name : "";
    }
    private void InitializeFilters()
    {
        PopulateFilters(new CatalogFilters());
        LibraryFilter.Items.Add(new ComboBoxItem { Content = L("Toutes les bibliothèques", "All libraries"), Tag = "" }); LibraryFilter.SelectedIndex = 0;
    }
    private void PopulateFilters(CatalogFilters filters)
    {
        _filterBusy = true;
        GenreFilter.Items.Clear(); GenreFilter.Items.Add(new ComboBoxItem { Content = L("Tous", "All"), Tag = "" });
        foreach (var genre in GenreGroups.FromNames(filters.Genres)) GenreFilter.Items.Add(new ComboBoxItem { Content = genre.Label, Tag = genre.Value });
        YearFilter.Items.Clear(); YearFilter.Items.Add(new ComboBoxItem { Content = L("Toutes", "All"), Tag = "" });
        foreach (var year in filters.Years.OrderDescending()) YearFilter.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year.ToString() });
        GenreFilter.SelectedIndex = YearFilter.SelectedIndex = 0; _filterBusy = false;
    }
    private void ResetFilters()
    {
        _filterBusy = true; SearchBox.Text = ""; _catalog.ParentId = null; _catalog.Favorites = false; _catalog.Person = null; _catalog.Studio = null; _catalog.Saga = null; _personFrom = null; _personSearch = null;
        GenreFilter.SelectedIndex = YearFilter.SelectedIndex = WatchedFilter.SelectedIndex = SortFilter.SelectedIndex = LibraryFilter.SelectedIndex = 0;
        _filterBusy = false; ShowFavoritesFilter(); ShowPersonFilter();
    }
    private async void Favorites_Click(object sender, RoutedEventArgs e) { _catalog.Favorites = !_catalog.Favorites; ShowFavoritesFilter(); await RefreshAsync(); }
    /// <summary>Lit like the rail's current page while only the favourites show; a second click shows everything again.</summary>
    private void ShowFavoritesFilter()
    {
        if (_catalog.Favorites) { FavoritesFilter.Background = Palette.Get("Text"); FavoritesFilter.Foreground = FavoritesIcon.Foreground = Palette.Get("OnAccent"); }
        else { FavoritesFilter.ClearValue(BackgroundProperty); FavoritesFilter.ClearValue(ForegroundProperty); FavoritesIcon.ClearValue(Views.Icon.ForegroundProperty); }
        System.Windows.Automation.AutomationProperties.SetName(FavoritesFilter, _catalog.Favorites ? L("Favoris seulement, activé", "Favorites only, on") : L("Favoris seulement", "Favorites only"));
        FavoritesFilter.ToolTip = _catalog.Favorites ? L("Afficher tous les titres", "Show all titles") : L("Afficher seulement tes favoris", "Show only your favorites");
    }
    private async void ResetFilters_Click(object sender, RoutedEventArgs e) { ResetFilters(); await RefreshAsync(); }
    /// <summary>The genre, year, progress and library choices, folded behind « Filtrer » until asked for (a genre clicked opens them).</summary>
    private bool _filtersOpen;
    private void FiltersToggle_Click(object sender, RoutedEventArgs e)
    {
        _filtersOpen = !_filtersOpen; ShowFilters();
        if (_filtersOpen) { Motion.Reveal(CatalogFilters, 200, 6); GenreFilter.Focus(); }
    }
    /// <summary>
    /// « Filtrer », or « Filtres · 2 » lit like the favourites while filters narrow the list, folded or not: a list
    /// shorter than expected always says why. Recherche and « Sur ce PC » have none.
    /// </summary>
    private void ShowFilters()
    {
        var browsing = _catalog.View is not ("search" or "offline");
        CatalogTools.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        CatalogFilters.Visibility = browsing && _filtersOpen ? Visibility.Visible : Visibility.Collapsed;
        var count = _catalog.FilterCount(CurrentQuery());
        FiltersText.Text = Catalog.FiltersLabel(count);
        if (count > 0) { FiltersToggle.Background = Palette.Get("Text"); FiltersToggle.Foreground = FiltersIcon.Foreground = Palette.Get("OnAccent"); }
        else { FiltersToggle.ClearValue(BackgroundProperty); FiltersToggle.ClearValue(ForegroundProperty); FiltersIcon.ClearValue(Views.Icon.ForegroundProperty); }
        System.Windows.Automation.AutomationProperties.SetName(FiltersToggle, (count > 0 ? FiltersText.Text : L("Filtrer", "Filter")) + (_filtersOpen ? L(", ouverts", ", open") : ""));
        RenderGenreChips();
    }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_initializing && !_filterBusy) await RefreshAsync(); }
    private async void LibraryFilter_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initializing || _filterBusy) return; _catalog.ParentId = Choice(LibraryFilter) is { Length: > 0 } id ? id : null; await RefreshAsync(); }
    /// <summary>Recherche, a page of its own: what was typed there stays (Ctrl+K selects it); filters of another page do not follow.</summary>
    private async void SearchNav_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog.View != "search") { ResetFilters(); _filtersOpen = false; }
        // Shown before the page is drawn (it may wait for the cache): the field takes the first letter typed.
        _catalog.View = "search"; CatalogHeader.Visibility = SearchField.Visibility = Visibility.Visible;
        var work = NavigateLibraryAsync(); SearchBox.Focus(); SearchBox.SelectAll(); await work;
    }
    private int _navigationVersion;
    private async Task NavigateLibraryAsync()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) AutoSaveSettings();
        ClosePreview(); ++_detailVersion; _returnToDetail = null;
        var closing = Task.WhenAll(Motion.HideAsync(DetailOverlay), Motion.HideAsync(SettingsOverlay), Motion.HideAsync(TorLinkOverlay), Motion.HideAsync(GuideOverlay));
        DetailOverlay.IsHitTestVisible = SettingsOverlay.IsHitTestVisible = TorLinkOverlay.IsHitTestVisible = GuideOverlay.IsHitTestVisible = false;
        _catalog.Loading = true; var navigation = ++_navigationVersion;
        var key = _catalog.Key(SearchBox.Text.Trim(), CurrentQuery()); var page = _catalog.View == "home" ? "home" : key;
        // Read off the UI thread: a page of hundreds of titles takes tens of milliseconds to decode.
        var cached = _store is { } store && !_catalog.AwaitsSearch(SearchBox.Text.Trim()) ? await Task.Run(() => TryCache(() => store.Load<ItemsResult>(page))) : null;
        // Another page was chosen meanwhile: it shows instead.
        if (navigation != _navigationVersion) { await closing; return; }
        _catalog.Items = cached?.Items ?? []; _catalog.TotalCount = cached?.TotalRecordCount ?? 0;
        if (cached is not null || _demo) _catalog.Loading = false;
        RenderLibrary(); SmoothScroll.Jump(LibraryScroll); Motion.Reveal(LibraryScroll, 260, 10);
        UpdateHeroClock(); await RefreshAsync();
        await closing; UpdateNavigation(); UpdateHeroClock();
    }
    private Button? _railActive;
    private void UpdateNavigation()
    {
        var guide = GuideOverlay.IsHitTestVisible && GuideOverlay.Visibility == Visibility.Visible;
        var settings = !guide && SettingsOverlay.IsHitTestVisible && SettingsOverlay.Visibility == Visibility.Visible;
        var torlink = !guide && !settings && TorLinkOverlay.IsHitTestVisible && TorLinkOverlay.Visibility == Visibility.Visible;
        Button? shown = null;
        foreach (var button in new[] { HomeNav, SearchNav, MoviesNav, SeriesNav, LibraryNav, OfflineNav, TorLinkNav, GuideNav, SettingsNav })
        {
            var active = guide ? button == GuideNav : settings ? button == SettingsNav : torlink ? button == TorLinkNav : button.Tag?.ToString() == _catalog.View;
            if (active) shown = button;
            // Contrast themes keep the tile on the button itself: the sliding one is drawn in Mira's colours.
            button.Background = active && HighContrast ? Palette.Get("Text") : Brushes.Transparent;
            Ink(button, active ? Palette.Get("OnAccent") : Palette.Get("TextTertiary"), button != _railActive && (active || button == _railActive));
        }
        MoveRailIndicator(shown);
    }
    private static bool HighContrast => SystemParameters.HighContrast;
    /// <summary>The icon's ink, turned in step with the tile sliding under it.</summary>
    private static void Ink(Button button, Brush target, bool animate)
    {
        if (!animate || Motion.Reduced || target is not SolidColorBrush to || button.Foreground is not SolidColorBrush from) { button.Foreground = target; return; }
        if (from.Color == to.Color) return;
        var ink = new SolidColorBrush(from.Color); button.Foreground = ink;
        ink.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation(to.Color, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Enter });
    }
    private void MoveRailIndicator(Button? active)
    {
        var move = (TranslateTransform)RailIndicator.RenderTransform;
        if (active is null || !active.IsVisible || HighContrast) { _railActive = active; Motion.Fade(RailIndicator, 0, 140); return; }
        RailBody.UpdateLayout();
        var at = active.TranslatePoint(new Point(), RailBody);
        RailIndicator.Width = active.ActualWidth; RailIndicator.Height = active.ActualHeight;
        var first = RailIndicator.Opacity < .01 || _railActive is null;
        if (first || Motion.Reduced)
        {
            move.BeginAnimation(TranslateTransform.XProperty, null); move.BeginAnimation(TranslateTransform.YProperty, null);
            move.X = at.X; move.Y = at.Y; Motion.Fade(RailIndicator, 1, 160);
        }
        else if (active != _railActive)
        {
            Motion.Animate(move, TranslateTransform.XProperty, at.X, 320, ease: Motion.Glide);
            Motion.Animate(move, TranslateTransform.YProperty, at.Y, 320, ease: Motion.Glide);
            Motion.Fade(RailIndicator, 1, 120);
        }
        _railActive = active;
    }
    private void RenderLibrary()
    {
        var search = SearchBox.Text.Trim(); var home = _catalog.IsHome(search);
        UpdateNavigation();
        PageTitle.Text = _catalog.PageTitle;
        CatalogDescription.Text = _catalog.Description;
        // Films and Séries: the title, its tools and its genres, without a sentence between them.
        var bare = _catalog.View is "Movie" or "Series" && _catalog.Person is null && _catalog.Studio is null && _catalog.Saga is null;
        CatalogDescription.Visibility = bare ? Visibility.Collapsed : Visibility.Visible; PageTitle.Margin = new Thickness(0, 0, 0, bare ? 26 : 0);
        Hero.Visibility = home && _catalog.Items.Count + _catalog.Resume.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogHeader.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        // « Sur ce PC » lists the copies as they are: no search or filter of Jellyfin's. Recherche has its field alone.
        SearchField.Visibility = _catalog.View == "search" ? Visibility.Visible : Visibility.Collapsed; ShowFilters();
        if (_catalog.View == "offline") CatalogDescription.Text = OfflineSummary();
        LibraryRows.Margin = new Thickness(110, home && Hero.Visibility == Visibility.Collapsed ? 80 : 0, 40, 42);
        var row = _catalog.ContinueRow();
        ResumeSection.Visibility = home && row.Count > 0 && HomeShows(HomeLayout.Continue) ? Visibility.Visible : Visibility.Collapsed;
        NextUpSection.Visibility = Visibility.Collapsed;
        RenderShelves(home); ShowHomeGroup(); RenderPickUp(search);
        RenderPeople(search); RenderStudios(search); RenderFound(search);
        // « Ajoutés récemment » put away (Réglages → Apparence): the home page ends with its rows. A search that found
        // only people, episodes or sagas shows them alone.
        var grid = !home || HomeShows(HomeLayout.Latest);
        var found = _catalog.FoundBeside(search) && !_catalog.Loading && _catalog.Items.Count == 0;
        GridHeader.Visibility = PosterCards.Visibility = grid && !found && !_catalog.AwaitsSearch(search) ? Visibility.Visible : Visibility.Collapsed;
        GridTitle.Text = _catalog.GridTitle(search);
        ItemCount.Text = Catalog.CountText(_catalog.TotalCount);
        if (home)
        {
            var previousCandidates = string.Join('|', _heroCandidates.Select(x => x.Id));
            _heroCandidates = _catalog.HeroCandidates(row);
            var candidate = _heroCandidates.FirstOrDefault(x => x.Id == _heroSelectedId) ?? _heroCandidates.FirstOrDefault();
            if (candidate is not null && !_heroBusy) RenderHero(candidate);
            if (previousCandidates != string.Join('|', _heroCandidates.Select(x => x.Id))) BuildHeroDots();
        }
        AddCards(ResumeCards, row, true); AddCards(PosterCards, _catalog.Items, false);
        ResizePosters();
        var empty = grid && !found && !_catalog.Loading && _catalog.Items.Count == 0;
        if (empty) DescribeEmptyState(search);
        if (empty && EmptyState.Visibility != Visibility.Visible) Motion.Reveal(EmptyState, 260, 8); else if (!empty) EmptyState.Visibility = Visibility.Collapsed;
        ShowSkeleton(grid && _catalog.Loading);
        if (_catalog.Loading) ItemCount.Text = L("Chargement…", "Loading…");
        LoadMore.Visibility = grid && !_demo && !_catalog.Loading && _catalog.Items.Count < _catalog.TotalCount ? Visibility.Visible : Visibility.Collapsed;
        UpdateResumeArrows(); UpdateHeroClock();
        if (!_startupComplete && !_catalog.Loading && (_catalog.Items.Count > 0 || _catalog.Resume.Count > 0)) _ = RevealStartupAsync();
    }
    /// <summary>
    /// « Personnes » above a search's titles: the actors and directors whose name matches, as in Mira web. Drawn again
    /// only when they change, so that their photos do not flash at each refresh.
    /// </summary>
    private void RenderPeople(string search)
    {
        var people = _catalog.SearchesPeople(search) ? _catalog.People : [];
        PeopleSection.Visibility = people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var shown = PeopleCards.Children.OfType<PersonCard>().Select(x => x.Person.Id);
        if (shown.SequenceEqual(people.Select(x => x.Id))) return;
        PeopleCards.Children.Clear();
        foreach (var person in people)
        {
            var card = new PersonCard(person);
            card.Click += async (_, _) => await ShowPersonAsync(new PersonInfo { Id = person.Id, Name = person.Name }, null, SearchBox.Text.Trim());
            PeopleCards.Children.Add(card);
            if (_images is { } images) _ = card.LoadImageAsync(images);
        }
    }
    /// <summary>« Studios » above a search's titles (« Ghibli »), as in Mira web: a click shows the studio's titles.</summary>
    private void RenderStudios(string search)
    {
        var studios = _catalog.SearchesPeople(search) ? _catalog.Studios : [];
        StudioResults.Visibility = studios.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (StudioButtons.Children.OfType<Button>().Select(x => x.Tag as string).SequenceEqual(studios.Select(x => x.Id))) return;
        StudioButtons.Children.Clear();
        foreach (var studio in studios)
        {
            var button = new Button { Content = ActionLabel("film", studio.Name), Tag = studio.Id, Margin = new Thickness(0, 0, 10, 10), ToolTip = L($"Les titres de {studio.Name} dans ta bibliothèque", $"Titles from {studio.Name} in your library") };
            button.Click += async (_, _) => await ShowStudioAsync(studio.Id, studio.Name, SearchBox.Text.Trim());
            StudioButtons.Children.Add(button);
        }
    }
    /// <summary>« Épisodes » and « Sagas » under a search's titles, as in Mira web.</summary>
    private void RenderFound(string search)
    {
        var searching = _catalog.SearchesPeople(search);
        List<MediaItem> episodes = searching ? _catalog.Episodes : [], sagas = searching ? _catalog.Sagas : [];
        EpisodeResults.Visibility = episodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SagaResults.Visibility = sagas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddCards(EpisodeCards, episodes, true, found: true); AddCards(FoundSagaCards, sagas, false, found: true);
    }
    /// <summary>Placeholders shaped like the real cards (artwork, title, details) so nothing jumps when they arrive.</summary>
    private void ShowSkeleton(bool visible)
    {
        if (!visible) { if (CatalogSkeleton.Visibility == Visibility.Visible) { Motion.Pulse(CatalogSkeleton, false); CatalogSkeleton.Visibility = Visibility.Collapsed; } return; }
        var available = Math.Max(PosterCards.ActualWidth, CatalogSkeleton.ActualWidth); if (available < 200) available = Math.Max(600, LibraryRows.ActualWidth);
        var (columns, width) = PosterLayout(available);
        CatalogSkeleton.Children.Clear();
        for (var i = 0; i < columns * 2; i++)
        {
            var card = new StackPanel { Width = width, Margin = new Thickness(0, 0, 22, 30) };
            card.Children.Add(new Border { Height = width * 1.48, Background = Palette.Get("Surface"), CornerRadius = new CornerRadius(5) });
            card.Children.Add(new Border { Height = 12, Width = width * .72, HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 16, 0, 0) });
            card.Children.Add(new Border { Height = 9, Width = width * .42, HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.Get("Surface"), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 20) });
            CatalogSkeleton.Children.Add(card);
        }
        if (CatalogSkeleton.Visibility != Visibility.Visible) { CatalogSkeleton.Visibility = Visibility.Visible; Motion.Pulse(CatalogSkeleton, true); }
    }
    private void DescribeEmptyState(string search)
    {
        var empty = _catalog.Empty(search, CurrentQuery(), _demo);
        (EmptyIcon.Kind, EmptyTitle.Text, EmptyHint.Text) = (empty.Icon, empty.Title, empty.Hint);
        EmptyReset.Visibility = empty.Reset ? Visibility.Visible : Visibility.Collapsed;
        EmptyGuide.Visibility = empty.Guide ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateResumeArrows()
    {
        if (ResumeArrows is null) return;
        // Scroll arrows only when the row actually overflows.
        ResumeArrows.Visibility = ResumeScroll.ScrollableWidth > 1 ? Visibility.Visible : Visibility.Collapsed;
        ResumePreviousButton.IsEnabled = ResumeScroll.HorizontalOffset > 1; ResumeNextButton.IsEnabled = ResumeScroll.HorizontalOffset < ResumeScroll.ScrollableWidth - 1;
    }
    /// <param name="found">Found by a search: an episode opens its series at it, a saga its titles; neither plays from its card.</param>
    private void AddCards(Panel panel, List<MediaItem> items, bool wide, bool found = false)
    {
        // One lookup table instead of a scan of every card for every item.
        var existing = new Dictionary<string, MediaCard>();
        foreach (var known in panel.Children.OfType<MediaCard>()) existing.TryAdd(known.RenderKey, known);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]; var key = MediaCard.Key(item, _settings);
            if (!existing.Remove(key, out var card))
            {
                card = new MediaCard(item, wide, i, _settings, _demo);
                card.Click += async (_, _) =>
                {
                    ClosePreview();
                    if (item.Type == "BoxSet") await ShowSagaAsync(item, SearchBox.Text.Trim());
                    else if (found && item.Type == "Episode") await ShowDetailsAsync(await PageItemAsync(item), item);
                    else if (wide || _catalog.View == "offline") await PlayAsync(item, chosen: true);
                    else await ShowDetailsAsync(item);
                };
                card.HoverEntered += CardHoverEntered; card.FocusEntered += CardFocusEntered; card.HoverLeft += CardHoverLeft;
                card.ContextMenu = CardMenu(card, wide && !found);
                panel.Children.Insert(Math.Min(i, panel.Children.Count), card);
                // The grid's posters wait until they come near the visible part of the page (LoadVisibleArtwork).
                if (panel != PosterCards) _ = card.LoadImageAsync(_images, wide);
                // Page navigation already animates the page. Refreshing progress must not animate it again per card.
            }
            else if (panel.Children.IndexOf(card) != i) { panel.Children.Remove(card); panel.Children.Insert(i, card); }
        }
        while (panel.Children.Count > items.Count) panel.Children.RemoveAt(panel.Children.Count - 1);
        NoteScroll();
        if (panel == PosterCards) ScheduleVisibleArtwork();
    }
    private bool _artworkScheduled;
    /// <summary>Once the layout is done (positions known), at most once per frame however often the page scrolls.</summary>
    private void ScheduleVisibleArtwork()
    {
        if (_artworkScheduled) return; _artworkScheduled = true;
        Dispatcher.BeginInvoke(() => { _artworkScheduled = false; LoadVisibleArtwork(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>
    /// Asks for the artwork of the posters on screen or within a screen of it, in either direction. The others wait:
    /// a grid of hundreds of titles (« Afficher plus ») would otherwise download and decode them all at once.
    /// </summary>
    private void LoadVisibleArtwork()
    {
        if (_images is null || !PosterCards.IsVisible || LibraryScroll.ActualHeight <= 0) return;
        var margin = LibraryScroll.ActualHeight;
        foreach (var card in PosterCards.Children.OfType<MediaCard>())
        {
            if (card.ImageRequested) continue;
            if (!card.IsDescendantOf(LibraryScroll)) return;
            var top = card.TransformToAncestor(LibraryScroll).Transform(new Point()).Y;
            // In reading order: once a card is past the margin below, so are all the next ones.
            if (top > LibraryScroll.ActualHeight + margin) return;
            if (top + card.ActualHeight >= -margin) _ = card.LoadImageAsync(_images, false);
        }
    }
    /// <summary>Right click (or Shift+F10 / menu key) on a card.</summary>
    private ContextMenu CardMenu(MediaCard card, bool wide)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            var item = card.Item; menu.Items.Clear(); ClosePreview();
            // A collection (a saga or one made by hand) plays nothing by itself: its titles do, from their page.
            if (item.Type == "BoxSet")
            {
                menu.Items.Add(MenuAction(L("Voir la collection", "View collection"), "info", async () => await ShowSagaAsync(item, SearchBox.Text.Trim())));
                menu.Items.Add(MenuAction(item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), item.UserData.IsFavorite ? "minus" : "heart", async () => { await ToggleFavoriteAsync(item); RenderAfterUserDataChange(); }));
                AddCollectionActions(menu, item);
                return;
            }
            var resume = item.Progress > 0 && !item.UserData.Played && _settings.RememberPosition;
            menu.Items.Add(MenuAction(resume ? L("Reprendre", "Resume") : L("Regarder", "Watch"), "play", async () => await PlayAsync(item, chosen: true)));
            if (resume) menu.Items.Add(MenuAction(L("Lire depuis le début", "Play from the beginning"), "refresh", async () => await PlayAsync(item, fromStart: true, chosen: true)));
            menu.Items.Add(MenuAction(item.Type == "Episode" ? L("Voir la série", "View show") : L("Voir la fiche", "View details"), "info", async () => await ShowDetailsAsync(await PageItemAsync(item))));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(item.UserData.Played ? L("Marquer comme non vu", "Mark as unwatched") : item.Type == "Series" ? L("Marquer la série comme vue", "Mark show as watched") : L("Marquer comme vu", "Mark as watched"), item.UserData.Played ? "refresh" : "check", async () => await SetPlayedAsync(item, !item.UserData.Played)));
            menu.Items.Add(MenuAction(item.UserData.IsFavorite ? L("Retirer des favoris", "Remove from favorites") : L("Ajouter aux favoris", "Add to favorites"), item.UserData.IsFavorite ? "minus" : "heart", async () => { await ToggleFavoriteAsync(item); RenderAfterUserDataChange(); }));
            if (wide) menu.Items.Add(MenuAction(L("Retirer de Continuer à regarder", "Remove from Continue watching"), "close", async () => await HideFromResumeAsync(item)));
            AddCollectionEntries(menu, item);
            if (OfflineMenuItem(item) is { } offline) { menu.Items.Add(new Separator()); menu.Items.Add(offline); }
        };
        menu.Items.Add(new MenuItem { Header = "…" }); // replaced when opened
        return menu;
    }
    /// <summary>
    /// Takes a film or series out of « Continuer à regarder » until it is played again. A title in progress also loses
    /// its resume point, on Jellyfin too, so that no other device offers it; « Annuler » puts both back.
    /// </summary>
    private async Task HideFromResumeAsync(MediaItem item)
    {
        var group = ContinueWatching.Group(item); var ticks = item.UserData.PlaybackPositionTicks;
        var resumable = ticks > 0 && !item.UserData.Played;
        var client = _demo ? null : _client; var store = _store;
        var at = DateTimeOffset.UtcNow; _catalog.HiddenResume[group] = at;
        if (resumable) _catalog.RecentPlayback.RemoveAll(x => x.Id == item.Id);
        RenderLibrary();
        SetNotice(L($"« {item.DisplayTitle} » ne sera plus dans Continuer à regarder.", $"“{item.DisplayTitle}” removed from Continue watching."), L("Annuler", "Undo"), () => _ = ShowInResumeAsync(item, group, resumable ? ticks : 0), sticky: false);
        _resumeChange = Save();
        await _resumeChange;
        ChoicesChanged();
        async Task Save()
        {
            try
            {
                if (store is not null) await Task.Run(() => { store.HideFromResume(group, at); if (resumable) store.ForgetProgress(item.Id); });
                if (resumable && client is not null) await client.SetPlaybackPositionAsync(item.Id, 0);
            }
            catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex) + L(" Le titre est retiré de Mira seulement.", " The title is removed from Mira only.")); }
        }
    }
    /// <summary>The last removal still being saved: « Annuler » waits for it, so that it cannot land after the undo.</summary>
    private Task _resumeChange = Task.CompletedTask;
    private async Task ShowInResumeAsync(MediaItem item, string group, long ticks)
    {
        await _resumeChange;
        _catalog.HiddenResume.Remove(group);
        var client = _demo ? null : _client; var store = _store;
        try
        {
            if (store is not null) await Task.Run(() => store.HideFromResume(group, null));
            if (ticks > 0 && client is not null) await client.SetPlaybackPositionAsync(item.Id, ticks);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
        ChoicesChanged();
        RenderLibrary(); await RefreshAsync(quiet: true);
    }
    private static MenuItem MenuAction(string header, string icon, Func<Task> action)
    {
        var entry = new MenuItem { Header = header, Tag = icon };
        entry.Click += async (_, _) => await action();
        return entry;
    }
    private static void AddPill(Panel panel, string text, string color = "TextBody") => panel.Children.Add(new Border
    { CornerRadius = new CornerRadius(4), Background = Brush("#3D555559"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 7, 4), Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get(color) } });
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateZoom(); FitPages(); }
    /// <summary>The banner and the posters, sized for the page as it lays out (smaller than the window once zoomed).</summary>
    private void FitPages()
    {
        if (Hero is null) return;
        ClosePreview();
        var (width, height) = (LayoutWidth, LayoutHeight);
        Hero.Height = Math.Clamp(height * .70, 490, 800);
        var roomy = width >= 1380;
        HeroSynopsis.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        HeroSmallOverview.Visibility = roomy ? Visibility.Collapsed : Visibility.Visible;
        HeroCopy.Width = Math.Clamp(width * .47, 490, 710);
        HeroTitle.FontSize = width > 1700 ? 62 : width > 1200 ? 53 : 43;
        ResizePosters();
        if (_miniPlayer) Dispatcher.BeginInvoke(KeepMiniInPlace, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void ResizePosters()
    {
        var available = PosterCards.ActualWidth; if (available < 200) return;
        var (_, width) = PosterLayout(available);
        foreach (var card in PosterCards.Children.OfType<MediaCard>()) card.Resize(width);
        foreach (var card in CatalogSkeleton.Children.OfType<Border>()) { card.Width = width; card.Height = width * 1.48; }
    }
    /// <summary>Columns and card width of a row of posters filling <paramref name="available"/>, at the chosen density.</summary>
    private (int Columns, double Width) PosterLayout(double available)
    {
        var target = _settings.PosterDensity == "Compact" ? 161 : 192;
        var columns = Math.Max(3, (int)Math.Round((available + 22) / (target + 22)));
        return (columns, Math.Floor((available - 1) / columns) - 22);
    }
    private void PosterGrid_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.WidthChanged) ResizePosters(); }
    private void Library_Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) + Math.Abs(e.HorizontalChange) > 0) NoteScroll();
        UpdateResumeArrows();
        // The page itself (not the resume row inside it) moved or changed size: other posters may now be near the screen.
        if (e.OriginalSource == LibraryScroll) { ScheduleVisibleArtwork(); if (e.VerticalChange != 0 || e.ViewportHeightChange != 0) UpdateHeroClock(); }
    }
    /// <summary>The page to open for an item: an episode opens its series.</summary>
    private async Task<MediaItem> PageItemAsync(MediaItem item)
    {
        if (item.Type != "Episode" || item.SeriesId is not { } seriesId) return item;
        if (_client is { } client && await LoadHeroSeriesAsync(client, seriesId) is { } series) return series;
        return new MediaItem { Id = seriesId, Name = item.SeriesName ?? item.Name, Type = "Series" };
    }
    private async Task SetPlayedAsync(MediaItem item, bool played)
    {
        try
        {
            if (_client is not null) await _client.SetPlayedAsync(item.Id, played);
            else if (!_demo) return;
            if (_store is { } store) await Task.Run(() => store.ForgetProgress(item.Id));
            _catalog.MarkPlayed(item, played, _episodes, _similar);
            _metadata?.Clear();
            SetNotice(L($"« {item.DisplayTitle} » {(played ? "marqué comme vu" : "marqué comme non vu")}.", $"“{item.DisplayTitle}” marked as {(played ? "watched" : "unwatched")}."));
            RenderAfterUserDataChange();
            if (!_demo) await RefreshAsync(quiet: true);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }
    /// <summary>Redraws whatever shows watched or favourite state after a change made in Mira.</summary>
    private void RenderAfterUserDataChange()
    {
        if (_demo) { _catalog.Resume = (_demoItems ?? []).Where(x => x.Progress > 0).ToList(); }
        RenderLibrary();
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is { } detail) { RenderDetailText(detail); if (_episodes.Count > 0) RenderEpisodes(); if (_similar.Count > 0 || _saga is not null) RenderSimilar(); }
    }
    private void ResumePrevious_Click(object sender, RoutedEventArgs e) => SmoothScroll.By(ResumeScroll, -624, true);
    private void ResumeNext_Click(object sender, RoutedEventArgs e) => SmoothScroll.By(ResumeScroll, 624, true);
    private async void HeroPlay_Click(object sender, RoutedEventArgs e) { if (_hero is not null) await PlayAsync(_hero, chosen: true); }
    private async void HeroDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is not { } hero) return;
        await ShowDetailsAsync(await PageItemAsync(hero));
    }
    private async void HeroFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_hero is not { } hero) return; HeroFavorite.IsEnabled = false;
        try { var item = hero.SeriesId is { } seriesId && _client is { } client ? await LoadHeroSeriesAsync(client, seriesId) ?? hero : hero; await ToggleFavoriteAsync(item); if (_hero.Id == hero.Id) RenderHero(hero); }
        finally { HeroFavorite.IsEnabled = true; }
    }
    /// <summary>The countdown bar follows the notice's rounded corners (ClipToBounds clips a rectangle: the bar showed past them).</summary>
    private void NoticeBody_SizeChanged(object sender, SizeChangedEventArgs e) => NoticeBody.Clip = new RectangleGeometry(new Rect(e.NewSize), 15, 15);
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => HideNotice();
    private void Combo_PreviewClick(object sender, MouseButtonEventArgs e) => ((ComboBox)sender).Focus();
}
