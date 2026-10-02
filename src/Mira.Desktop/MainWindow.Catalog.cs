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
        Choice(WatchedFilter) switch { "played" => true, "unplayed" => false, _ => null }, Choice(SortFilter));
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
        _filterBusy = true; SearchBox.Text = ""; _parentId = null; _favorites = false;
        GenreFilter.SelectedIndex = YearFilter.SelectedIndex = WatchedFilter.SelectedIndex = SortFilter.SelectedIndex = LibraryFilter.SelectedIndex = 0;
        _filterBusy = false; ShowFavoritesFilter();
    }
    private async void Favorites_Click(object sender, RoutedEventArgs e) { _favorites = !_favorites; ShowFavoritesFilter(); await RefreshAsync(); }
    /// <summary>Lit like the rail's current page while only the favourites show; a second click shows everything again.</summary>
    private void ShowFavoritesFilter()
    {
        if (_favorites) { FavoritesFilter.Background = Brush("#F4F4F5"); FavoritesFilter.Foreground = FavoritesIcon.Foreground = Brush("#151516"); }
        else { FavoritesFilter.ClearValue(BackgroundProperty); FavoritesFilter.ClearValue(ForegroundProperty); FavoritesIcon.ClearValue(Views.Icon.ForegroundProperty); }
        System.Windows.Automation.AutomationProperties.SetName(FavoritesFilter, _favorites ? "Favoris seulement, activé" : "Favoris seulement");
        FavoritesFilter.ToolTip = _favorites ? "Afficher tous les titres" : "Afficher seulement tes favoris";
    }
    private string CatalogKey(string search, CatalogQuery filters) => $"{_view}:{(_favorites ? "favorites" : "")}:{_parentId}:{search}:{filters.CacheKey}";
    private async void ResetFilters_Click(object sender, RoutedEventArgs e) { ResetFilters(); await RefreshAsync(); }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_initializing && !_filterBusy) await RefreshAsync(); }
    private async void LibraryFilter_Changed(object sender, SelectionChangedEventArgs e)
    { if (_initializing || _filterBusy) return; _parentId = Choice(LibraryFilter) is { Length: > 0 } id ? id : null; await RefreshAsync(); }
    private async void SearchNav_Click(object sender, RoutedEventArgs e)
    {
        _view = "search"; var work = NavigateLibraryAsync(); SearchBox.Focus(); SearchBox.SelectAll(); await work;
    }
    private async Task NavigateLibraryAsync()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) AutoSaveSettings();
        ClosePreview(); ++_detailVersion; _returnToDetail = null;
        var closing = Task.WhenAll(Motion.HideAsync(DetailOverlay), Motion.HideAsync(SettingsOverlay), Motion.HideAsync(TorLinkOverlay), Motion.HideAsync(GuideOverlay));
        DetailOverlay.IsHitTestVisible = SettingsOverlay.IsHitTestVisible = TorLinkOverlay.IsHitTestVisible = GuideOverlay.IsHitTestVisible = false;
        _catalogLoading = true;
        var key = CatalogKey(SearchBox.Text.Trim(), CurrentQuery());
        var cached = _store?.Load<ItemsResult>(_view == "home" ? "home" : key);
        _items = cached?.Items ?? []; _totalCount = cached?.TotalRecordCount ?? 0;
        if (cached is not null || _demo) _catalogLoading = false;
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
            var active = guide ? button == GuideNav : settings ? button == SettingsNav : torlink ? button == TorLinkNav : button.Tag?.ToString() == _view;
            button.Background = active ? Brush("#F4F4F5") : Brushes.Transparent;
            button.Foreground = active ? Brush("#151516") : Brush("#B8B8BE");
        }
    }
    private void RenderLibrary()
    {
        var search = SearchBox.Text.Trim(); var home = _view == "home" && search.Length == 0;
        UpdateNavigation();
        PageTitle.Text = _view switch { "Movie" => "Films", "Series" => "Séries & animes", "library" => "Ta bibliothèque", _ => "Explorer" };
        CatalogDescription.Text = _favorites ? "Les histoires que tu gardes à portée de main." : "Trouve ta prochaine séance dans ta bibliothèque.";
        Hero.Visibility = home && _items.Count + _resume.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogHeader.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        LibraryRows.Margin = new Thickness(110, home && Hero.Visibility == Visibility.Collapsed ? 80 : 0, 40, 42);
        ResumeSection.Visibility = home && _resume.Count + _nextUp.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextUpSection.Visibility = Visibility.Collapsed;
        GridTitle.Text = home ? "Ajoutés récemment" : search.Length > 0 ? $"Résultats pour « {search} »" : _favorites ? "Tes favoris" : "Tous les titres";
        ItemCount.Text = $"{_totalCount:N0} titre{(_totalCount > 1 ? "s" : "")}";
        if (home)
        {
            var previousCandidates = string.Join('|', _heroCandidates.Select(x => x.Id));
            _heroCandidates = _resume.Concat(_nextUp).Concat(_items).DistinctBy(x => x.SeriesId ?? x.Id).Take(5).ToList();
            var candidate = _heroCandidates.FirstOrDefault(x => x.Id == _heroSelectedId) ?? _heroCandidates.FirstOrDefault();
            if (candidate is not null && !_heroBusy) RenderHero(candidate);
            if (previousCandidates != string.Join('|', _heroCandidates.Select(x => x.Id))) BuildHeroDots();
        }
        AddCards(ResumeCards, ContinueWatching.Order(_resume, _nextUp, _recentPlayback), true); AddCards(PosterCards, _items, false);
        ResizePosters();
        var empty = !_catalogLoading && _items.Count == 0;
        if (empty) DescribeEmptyState(search);
        if (empty && EmptyState.Visibility != Visibility.Visible) Motion.Reveal(EmptyState, 260, 8); else if (!empty) EmptyState.Visibility = Visibility.Collapsed;
        ShowSkeleton(_catalogLoading);
        if (_catalogLoading) ItemCount.Text = "Chargement…";
        LoadMore.Visibility = !_demo && !_catalogLoading && _items.Count < _totalCount ? Visibility.Visible : Visibility.Collapsed;
        UpdateResumeArrows(); UpdateHeroClock();
        if (!_startupComplete && !_catalogLoading && (_items.Count > 0 || _resume.Count > 0)) _ = RevealStartupAsync();
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
            card.Children.Add(new Border { Height = width * 1.48, Background = Brush("#151517"), CornerRadius = new CornerRadius(5) });
            card.Children.Add(new Border { Height = 12, Width = width * .72, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#18181B"), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 16, 0, 0) });
            card.Children.Add(new Border { Height = 9, Width = width * .42, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#141417"), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 12, 0, 20) });
            CatalogSkeleton.Children.Add(card);
        }
        if (CatalogSkeleton.Visibility != Visibility.Visible) { CatalogSkeleton.Visibility = Visibility.Visible; Motion.Pulse(CatalogSkeleton, true); }
    }
    private void DescribeEmptyState(string search)
    {
        var query = CurrentQuery();
        var filtered = query.Genre is not null || query.Year is not null || query.Played is not null || _parentId is not null;
        // Nothing at all in the library (not a search, a filter or the favourites): where videos go is what is missing.
        var emptyLibrary = !_demo && search.Length == 0 && !filtered && !_favorites && _view is "home" or "library" && _resume.Count == 0;
        (EmptyIcon.Kind, EmptyTitle.Text, EmptyHint.Text) = (_view, search.Length, filtered) switch
        {
            (_, > 0, _) => ("search", $"Aucun résultat pour « {search} »", "Vérifie l’orthographe ou essaie un autre titre."),
            (_, _, true) => ("sliders", "Aucun titre ne correspond à ces filtres", "Élargis ta sélection pour retrouver tes titres."),
            _ when _favorites => ("heart", "Aucun favori ici pour l’instant", "Depuis la fiche d’un titre, choisis « Ajouter aux favoris » pour le retrouver ici."),
            _ when emptyLibrary => ("folder", "Ta bibliothèque est vide", "Range tes films et séries dans les dossiers de Jellyfin : ils apparaîtront ici."),
            _ => ("library", "Rien à afficher ici", "Les titres ajoutés à ta bibliothèque Jellyfin apparaîtront ici.")
        };
        EmptyReset.Visibility = filtered || search.Length > 0 || _favorites ? Visibility.Visible : Visibility.Collapsed;
        EmptyGuide.Visibility = emptyLibrary ? Visibility.Visible : Visibility.Collapsed;
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
                panel.Children.Insert(Math.Min(i, panel.Children.Count), card); _ = card.LoadImageAsync(_images, wide);
                // Page navigation already animates the page. Refreshing progress must not animate it again per card.
            }
            else if (panel.Children.IndexOf(card) != i) { panel.Children.Remove(card); panel.Children.Insert(i, card); }
        }
        while (panel.Children.Count > items.Count) panel.Children.RemoveAt(panel.Children.Count - 1);
        NoteScroll();
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
            menu.Items.Add(MenuAction(item.Type == "Episode" ? "Voir la série" : "Voir la fiche", "info", async () => await ShowDetailsAsync(await PageItemAsync(item))));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuAction(item.UserData.Played ? "Marquer comme non vu" : item.Type == "Series" ? "Marquer la série comme vue" : "Marquer comme vu", item.UserData.Played ? "refresh" : "check", async () => await SetPlayedAsync(item, !item.UserData.Played)));
            menu.Items.Add(MenuAction(item.UserData.IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris", item.UserData.IsFavorite ? "minus" : "heart", async () => { await ToggleFavoriteAsync(item); RenderAfterUserDataChange(); }));
        };
        menu.Items.Add(new MenuItem { Header = "…" }); // replaced when opened
        return menu;
    }
    private static MenuItem MenuAction(string header, string icon, Func<Task> action)
    {
        var entry = new MenuItem { Header = header, Tag = icon };
        entry.Click += async (_, _) => await action();
        return entry;
    }
    private static void AddPill(Panel panel, string text, string color = "#D5D5DB") => panel.Children.Add(new Border
    { CornerRadius = new CornerRadius(4), Background = Brush("#3D555559"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 7, 4), Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(color) } });
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Hero is null) return;
        ClosePreview();
        Hero.Height = Math.Clamp(ActualHeight * .70, 490, 800);
        var roomy = ActualWidth >= 1380;
        HeroSynopsis.Visibility = roomy ? Visibility.Visible : Visibility.Collapsed;
        HeroSmallOverview.Visibility = roomy ? Visibility.Collapsed : Visibility.Visible;
        HeroCopy.Width = Math.Clamp(ActualWidth * .47, 490, 710);
        HeroTitle.FontSize = ActualWidth > 1700 ? 62 : ActualWidth > 1200 ? 53 : 43;
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
            _recentPlayback.RemoveAll(x => x.Id == item.Id || x.SeriesId == item.Id);
            foreach (var known in _items.Concat(_resume).Concat(_nextUp).Concat(_episodes).Concat(_similar).Append(item).Where(x => x.Id == item.Id))
            { known.UserData.Played = played; known.UserData.PlaybackPositionTicks = 0; }
            if (item.Type == "Series") foreach (var episode in _episodes.Concat(_resume).Where(x => x.SeriesId == item.Id)) { episode.UserData.Played = played; episode.UserData.PlaybackPositionTicks = 0; }
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
        if (_demo) { _resume = (_demoItems ?? []).Where(x => x.Progress > 0).ToList(); }
        RenderLibrary();
        if (DetailOverlay.Visibility == Visibility.Visible && _detail is { } detail) { RenderDetailText(detail); if (_episodes.Count > 0) RenderEpisodes(); if (_similar.Count > 0) RenderSimilar(); }
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
