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
        Choice(WatchedFilter) switch { "played" => true, "unplayed" => false, _ => null }, Choice(SortFilter), _catalog.Person?.Id);
    /// <summary>The title page a person's titles were chosen from (<see cref="Catalog.Person"/>).</summary>
    private MediaItem? _personFrom;
    private string _personReturnView = "home";
    /// <summary>A name clicked on a title page: the library with this person's titles, the page kept to come back to.</summary>
    private async Task ShowPersonAsync(PersonInfo person, bool directed)
    {
        if (person.Id is not { Length: > 0 } id) return;
        var from = _detail; var view = _catalog.View;
        ResetFilters();
        (_catalog.Person, _personFrom, _personReturnView, _catalog.View) = ((id, person.Name, directed), from, view, "library");
        ShowPersonFilter();
        await NavigateLibraryAsync();
    }
    /// <summary>Back from a person's titles: the page they were chosen from, over the view it was opened from.</summary>
    private async Task BackFromPersonAsync()
    {
        var title = _personFrom; var view = _personReturnView;
        ResetFilters(); _catalog.View = view;
        await NavigateLibraryAsync();
        if (title is not null) await ShowDetailsAsync(title);
    }
    private async void PersonFilter_Click(object sender, RoutedEventArgs e) { _catalog.Person = null; _personFrom = null; ShowPersonFilter(); await RefreshAsync(); }
    private void ShowPersonFilter()
    {
        PersonFilter.Visibility = _catalog.Person is null ? Visibility.Collapsed : Visibility.Visible;
        PersonFilterText.Text = _catalog.Person is { } person ? Catalog.PersonText(person.Name, person.Directed) : "";
    }
    private void InitializeFilters()
    {
        PopulateFilters(new CatalogFilters());
        LibraryFilter.Items.Add(new ComboBoxItem { Content = "Toutes les bibliothèques", Tag = "" }); LibraryFilter.SelectedIndex = 0;
    }
    private void PopulateFilters(CatalogFilters filters)
    {
        _filterBusy = true;
        GenreFilter.Items.Clear(); GenreFilter.Items.Add(new ComboBoxItem { Content = "Tous", Tag = "" });
        foreach (var genre in filters.Genres.Order(StringComparer.CurrentCultureIgnoreCase)) GenreFilter.Items.Add(new ComboBoxItem { Content = genre, Tag = genre });
        YearFilter.Items.Clear(); YearFilter.Items.Add(new ComboBoxItem { Content = "Toutes", Tag = "" });
        foreach (var year in filters.Years.OrderDescending()) YearFilter.Items.Add(new ComboBoxItem { Content = year.ToString(), Tag = year.ToString() });
        GenreFilter.SelectedIndex = YearFilter.SelectedIndex = 0; _filterBusy = false;
    }
    private void ResetFilters()
    {
        _filterBusy = true; SearchBox.Text = ""; _catalog.ParentId = null; _catalog.Favorites = false; _catalog.Person = null; _personFrom = null;
        GenreFilter.SelectedIndex = YearFilter.SelectedIndex = WatchedFilter.SelectedIndex = SortFilter.SelectedIndex = LibraryFilter.SelectedIndex = 0;
        _filterBusy = false; ShowFavoritesFilter(); ShowPersonFilter();
    }
    private async void Favorites_Click(object sender, RoutedEventArgs e) { _catalog.Favorites = !_catalog.Favorites; ShowFavoritesFilter(); await RefreshAsync(); }
    /// <summary>Lit like the rail's current page while only the favourites show; a second click shows everything again.</summary>
    private void ShowFavoritesFilter()
    {
        if (_catalog.Favorites) { FavoritesFilter.Background = Palette.Get("Text"); FavoritesFilter.Foreground = FavoritesIcon.Foreground = Palette.Get("OnAccent"); }
        else { FavoritesFilter.ClearValue(BackgroundProperty); FavoritesFilter.ClearValue(ForegroundProperty); FavoritesIcon.ClearValue(Views.Icon.ForegroundProperty); }
        System.Windows.Automation.AutomationProperties.SetName(FavoritesFilter, _catalog.Favorites ? "Favoris seulement, activé" : "Favoris seulement");
        FavoritesFilter.ToolTip = _catalog.Favorites ? "Afficher tous les titres" : "Afficher seulement tes favoris";
    }
    private async void ResetFilters_Click(object sender, RoutedEventArgs e) { ResetFilters(); await RefreshAsync(); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_initializing && !_filterBusy) await RefreshAsync(); }
    private async void LibraryFilter_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initializing || _filterBusy) return; _catalog.ParentId = Choice(LibraryFilter) is { Length: > 0 } id ? id : null; await RefreshAsync(); }
    private async void SearchNav_Click(object sender, RoutedEventArgs e)
    {
        _catalog.View = "search"; var work = NavigateLibraryAsync(); SearchBox.Focus(); SearchBox.SelectAll(); await work;
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
        var cached = _store is { } store ? await Task.Run(() => TryCache(() => store.Load<ItemsResult>(page))) : null;
        // Another page was chosen meanwhile: it shows instead.
        if (navigation != _navigationVersion) { await closing; return; }
        _catalog.Items = cached?.Items ?? []; _catalog.TotalCount = cached?.TotalRecordCount ?? 0;
        if (cached is not null || _demo) _catalog.Loading = false;
        RenderLibrary(); SmoothScroll.Jump(LibraryScroll); Motion.Reveal(LibraryScroll, 260, 10);
        UpdateHeroClock(); await RefreshAsync();
        await closing; UpdateNavigation(); UpdateHeroClock();
    }
    private void UpdateNavigation()
    {
        var guide = GuideOverlay.IsHitTestVisible && GuideOverlay.Visibility == Visibility.Visible;
        var settings = !guide && SettingsOverlay.IsHitTestVisible && SettingsOverlay.Visibility == Visibility.Visible;
        var torlink = !guide && !settings && TorLinkOverlay.IsHitTestVisible && TorLinkOverlay.Visibility == Visibility.Visible;
        foreach (var button in new[] { HomeNav, SearchNav, MoviesNav, SeriesNav, LibraryNav, TorLinkNav, GuideNav, SettingsNav })
        {
            var active = guide ? button == GuideNav : settings ? button == SettingsNav : torlink ? button == TorLinkNav : button.Tag?.ToString() == _catalog.View;
            button.Background = active ? Palette.Get("Text") : Brushes.Transparent;
            button.Foreground = active ? Palette.Get("OnAccent") : Palette.Get("TextTertiary");
        }
    }
    private void RenderLibrary()
    {
        var search = SearchBox.Text.Trim(); var home = _catalog.IsHome(search);
        UpdateNavigation();
        PageTitle.Text = _catalog.PageTitle;
        CatalogDescription.Text = _catalog.Description;
        Hero.Visibility = home && _catalog.Items.Count + _catalog.Resume.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogHeader.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        LibraryRows.Margin = new Thickness(110, home && Hero.Visibility == Visibility.Collapsed ? 80 : 0, 40, 42);
        var row = _catalog.ContinueRow();
        ResumeSection.Visibility = home && row.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextUpSection.Visibility = Visibility.Collapsed;
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
        var empty = !_catalog.Loading && _catalog.Items.Count == 0;
        if (empty) DescribeEmptyState(search);
        if (empty && EmptyState.Visibility != Visibility.Visible) Motion.Reveal(EmptyState, 260, 8); else if (!empty) EmptyState.Visibility = Visibility.Collapsed;
        ShowSkeleton(_catalog.Loading);
        if (_catalog.Loading) ItemCount.Text = "Chargement…";
        LoadMore.Visibility = !_demo && !_catalog.Loading && _catalog.Items.Count < _catalog.TotalCount ? Visibility.Visible : Visibility.Collapsed;
        UpdateResumeArrows(); UpdateHeroClock();
        if (!_startupComplete && !_catalog.Loading && (_catalog.Items.Count > 0 || _catalog.Resume.Count > 0)) _ = RevealStartupAsync();
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
    private void AddCards(Panel panel, List<MediaItem> items, bool wide)
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
                card.Click += async (_, _) => { ClosePreview(); if (wide) await PlayAsync(item); else await ShowDetailsAsync(item); };
                card.HoverEntered += CardHoverEntered; card.FocusEntered += CardFocusEntered; card.HoverLeft += CardHoverLeft;
                card.ContextMenu = CardMenu(card, wide);
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
            var resume = item.Progress > 0 && !item.UserData.Played && _settings.RememberPosition;
            menu.Items.Add(MenuAction(resume ? "Reprendre" : "Regarder", "play", async () => await PlayAsync(item)));
            if (resume) menu.Items.Add(MenuAction("Lire depuis le début", "refresh", async () => await PlayAsync(item, fromStart: true)));
            menu.Items.Add(MenuAction(item.Type == "Episode" ? "Voir la série" : "Voir la fiche", "info", async () => await ShowDetailsAsync(await PageItemAsync(item))));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(item.UserData.Played ? "Marquer comme non vu" : item.Type == "Series" ? "Marquer la série comme vue" : "Marquer comme vu", item.UserData.Played ? "refresh" : "check", async () => await SetPlayedAsync(item, !item.UserData.Played)));
            menu.Items.Add(MenuAction(item.UserData.IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris", item.UserData.IsFavorite ? "minus" : "heart", async () => { await ToggleFavoriteAsync(item); RenderAfterUserDataChange(); }));
            if (wide) menu.Items.Add(MenuAction("Retirer de Continuer à regarder", "close", async () => await HideFromResumeAsync(item)));
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
        SetNotice($"« {item.DisplayTitle} » ne sera plus dans Continuer à regarder.", "Annuler", () => _ = ShowInResumeAsync(item, group, resumable ? ticks : 0), sticky: false);
        _resumeChange = Save();
        await _resumeChange;
        async Task Save()
        {
            try
            {
                if (store is not null) await Task.Run(() => { store.HideFromResume(group, at); if (resumable) store.ForgetProgress(item.Id); });
                if (resumable && client is not null) await client.SetPlaybackPositionAsync(item.Id, 0);
            }
            catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex) + " Le titre est retiré de Mira seulement."); }
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
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
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
            SetNotice($"« {item.DisplayTitle} » {(played ? "marqué comme vu" : "marqué comme non vu")}.");
            RenderAfterUserDataChange();
            if (!_demo) await RefreshAsync(quiet: true);
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
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
    private async void HeroPlay_Click(object sender, RoutedEventArgs e) { if (_hero is not null) await PlayAsync(_hero); }
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
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => HideNotice();
    private void Combo_PreviewClick(object sender, MouseButtonEventArgs e) => ((ComboBox)sender).Focus();
    private static string PlainText(string text) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ")).Trim();
}
