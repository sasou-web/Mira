using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private bool _seasonBusy;
    private IInputElement? _detailOrigin;
    private (string Id, bool Favorite)? _favoriteOverride;
    private MediaItem? _detailNext;
    /// <summary>The file of the episode Lecture plays on a series, once asked for: its picture shows in the page (« 1080p »).</summary>
    private (string Id, MediaSource? Source)? _detailFile;
    private List<MediaItem> _similar = [];
    private async void DetailPlay_Click(object sender, RoutedEventArgs e)
    { if (DetailTarget() is { } target) await PlayAsync(target, chosen: true); }
    /// <summary>« Depuis le début », beside « Reprendre »: the same title, from its first second (as on Mac and in Mira web).</summary>
    private async void DetailRestart_Click(object sender, RoutedEventArgs e)
    { if (DetailTarget() is { } target) await PlayAsync(target, fromStart: true, chosen: true); }
    /// <summary>What Lecture plays: the film, or the episode the series picks up at.</summary>
    private MediaItem? DetailTarget() => _detailNext is { } next && _detail?.Type == "Series" ? next : _detail;
    private async void CloseDetails_Click(object sender, RoutedEventArgs e) => await BackFromDetailsAsync();
    /// <summary>The title pages left for a similar title, the saga or the studio, to come back to.</summary>
    private readonly TitleTrail _detailTrail = new();
    /// <summary>« Retour » on a title page: the page it was opened from (a similar title…), where it was scrolled; else it closes.</summary>
    private async Task BackFromDetailsAsync()
    {
        if (_detailTrail.Back() is { } page) await ShowDetailsAsync(page.Item, returnTo: page.Offset);
        else await CloseDetailsAsync();
    }
    private async Task CloseDetailsAsync()
    {
        ++_detailVersion; _returnToDetail = null; _detailTrail.Clear();
        await Motion.HideAsync(DetailOverlay, 170); UpdateHeroClock();
        // Back to the card that opened the page, so keyboard users keep their place.
        if (_detailOrigin is UIElement { IsVisible: true } origin) origin.Focus();
        _detailOrigin = null;
    }
    /// <summary>An episode a search found, on its series' page: its season shows, and the page scrolls to it once.</summary>
    private MediaItem? _detailEpisode;
    private bool _detailEpisodeShown;
    /// <param name="atEpisode">The series' page opened at this episode (found by a search).</param>
    /// <param name="returnTo">Coming back to this page with « Retour » (<see cref="_detailTrail"/>): scrolled back there once it is drawn.</param>
    private async Task ShowDetailsAsync(MediaItem item, MediaItem? atEpisode = null, double? returnTo = null)
    {
        var version = ++_detailVersion; (_detailEpisode, _detailEpisodeShown) = (atEpisode, false);
        var previewArt = _hoverCard?.Item.Id == item.Id ? _hoverCard.ImageSource : null;
        var left = returnTo is null ? null : _detail;
        if (DetailOverlay.Visibility != Visibility.Visible) _detailOrigin = Keyboard.FocusedElement;
        // A page opened from the open one (similar title, saga, studio) keeps it to come back to; a page opened anew starts the trail again.
        if (returnTo is null)
        {
            if (DetailOverlay.Visibility == Visibility.Visible && DetailOverlay.IsHitTestVisible && _detail is { } current) _detailTrail.Leave(current, DetailScroll.VerticalOffset, item);
            else _detailTrail.Clear();
        }
        ClosePreview(); _detail = item; _episodes = []; _detailNext = null; _detailFile = null; _favoriteOverride = null; _similar = []; _saga = null; _studioTitles = null;
        SimilarCards.Children.Clear(); SimilarSection.Visibility = Visibility.Collapsed; SagaCards.Children.Clear(); SagaSection.Visibility = Visibility.Collapsed;
        StudioCards.Children.Clear(); StudioSection.Visibility = Visibility.Collapsed; SeasonMarkButton.Visibility = Visibility.Collapsed;
        _extras = []; ExtrasCards.Children.Clear(); ExtrasSection.Visibility = Visibility.Collapsed;
        AutoSaveSettings(); _ = Motion.HideAsync(SettingsOverlay); SmoothScroll.Jump(DetailScroll);
        DetailBackdropPrevious.Source = null; DetailLogo.Source = null; DetailLogo.Visibility = Visibility.Collapsed; DetailTitle.Visibility = Visibility.Visible;
        SetDetailBackdrop(previewArt ?? (_hero?.Id == item.Id || _hero?.SeriesId == item.Id ? HeroImage.Source : null), animate: false);
        if (_demo) SetDetailBackdrop(DemoLibrary.Artwork(Math.Max(0, _catalog.Items.FindIndex(x => x.Id == item.Id)), true), animate: false);
        RenderDetailText(item); EpisodesPanel.Children.Clear(); _calendar = ([], []); _calendarOpen = false; RenderCalendar();
        _seasonBusy = true; SeasonSelector.Items.Clear(); _seasonBusy = false;
        EpisodesHead.Visibility = EpisodeHeading.Visibility = SeasonSelector.Visibility = item.Type == "Series" ? Visibility.Visible : Visibility.Collapsed;
        if (item.Type == "Series") EpisodesPanel.Children.Add(new TextBlock { Text = L("Chargement des épisodes…", "Loading episodes…"), Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 20, 0, 20) });
        Motion.Reveal(DetailOverlay, 300, 14); UpdateHeroClock();
        // Focus moves into the page: Enter plays, Tab stays inside, Escape goes back.
        _ = Dispatcher.BeginInvoke(() => { if (version == _detailVersion) DetailPlay.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        var tasks = new List<Task>();
        if (_images is not null) { tasks.Add(LoadDetailBackdropAsync(item, version)); tasks.Add(LoadDetailLogoAsync(item, version)); }
        if (_metadata is not null)
        {
            tasks.Add(LoadDetailMetadataAsync(item, version));
            if (item.Type == "Series") { tasks.Add(LoadDetailEpisodesAsync(item, version)); tasks.Add(LoadCalendarAsync(item, version)); }
            tasks.Add(LoadSimilarAsync(item, version));
        }
        else if (_demo)
        {
            if (item.Type == "Series") { EpisodesPanel.Children.Clear(); EpisodesPanel.Children.Add(new TextBlock { Text = L("Les épisodes de ta bibliothèque apparaîtront ici après connexion à Jellyfin.", "The episodes of your library will show here once you’re connected to Jellyfin."), Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap }); SeasonSelector.Visibility = Visibility.Collapsed; }
            _similar = (_demoItems ?? DemoLibrary.Items()).Where(x => x.Id != item.Id && x.Genres.Intersect(item.Genres).Any()).ToList(); RenderSimilar();
        }
        await Task.WhenAll(tasks);
        if (returnTo is { } offset && version == _detailVersion) await Dispatcher.InvokeAsync(() => ReturnToPlace(version, offset, left), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /// <summary>
    /// Back on a page left for another: where it was scrolled, and the keyboard on the card that was opened from it
    /// (Enter would open it again). Neither if the person already moved while the rows came in.
    /// </summary>
    private void ReturnToPlace(int version, double offset, MediaItem? left)
    {
        if (version != _detailVersion || DetailOverlay.Visibility != Visibility.Visible) return;
        if (DetailScroll.VerticalOffset < 1 && offset > 0) { SmoothScroll.Jump(DetailScroll, offset); DetailScroll.UpdateLayout(); }
        if (left is null || !ReferenceEquals(Keyboard.FocusedElement, DetailPlay) || InputManager.Current.MostRecentInputDevice is not KeyboardDevice) return;
        var card = SagaCards.Children.OfType<MediaCard>().Concat(StudioCards.Children.OfType<MediaCard>()).Concat(SimilarCards.Children.OfType<MediaCard>())
            .FirstOrDefault(x => x.Item.Id == left.Id || x.Item.Id == left.SeriesId);
        card?.Focus();
    }
    /// <summary>Reloads progress and watched marks of the open page (after playback) without moving it.</summary>
    private async Task RefreshDetailAsync()
    {
        if (_detail is not { } item || DetailOverlay.Visibility != Visibility.Visible) return;
        var version = ++_detailVersion; _metadata?.Clear();
        if (_metadata is null) { RenderDetailText(item); if (_episodes.Count > 0) RenderEpisodes(); return; }
        var tasks = new List<Task> { LoadDetailMetadataAsync(item, version) };
        if (item.Type == "Series") { tasks.Add(LoadDetailEpisodesAsync(item, version)); tasks.Add(LoadCalendarAsync(item, version)); }
        await Task.WhenAll(tasks);
    }
    /// <summary>Its logo from Jellyfin in place of its name, when it has one of its own (a film's, a series').</summary>
    private async Task LoadDetailLogoAsync(MediaItem item, int version)
    {
        if (_images is not { } cache || !item.ImageTags.ContainsKey("Logo") || DetailLogo.Source is not null) return;
        var logo = await cache.LogoAsync(item);
        if (version != _detailVersion || logo is null || DetailLogo.Source is not null) return;
        DetailLogo.Source = logo; AutomationProperties.SetName(DetailLogo, item.DisplayTitle);
        DetailLogo.Visibility = Visibility.Visible; DetailTitle.Visibility = Visibility.Collapsed;
        Motion.Fade(DetailLogo, 1, 320, 0, Motion.Soft);
    }
    /// <summary>
    /// The picture as tall as the window for a film, most of it for a series (its episodes lead below the picture); never
    /// shorter than what sits on it (a summary opened, a narrow window).
    /// </summary>
    private void SizeDetailHero()
    {
        var view = DetailScroll.ActualHeight > 0 ? DetailScroll.ActualHeight : LayoutHeight;
        var wanted = _detail?.Type == "Series" ? Math.Max(470, view * .72) : Math.Max(470, view);
        DetailHero.Height = Math.Max(wanted, DetailHeroText.ActualHeight + DetailHeroText.Margin.Bottom + 110);
    }
    private void DetailScroll_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.HeightChanged) SizeDetailHero(); }
    private void DetailHeroText_SizeChanged(object sender, SizeChangedEventArgs e) { if (e.HeightChanged) SizeDetailHero(); }
    /// <summary>« Lire la suite »: the whole summary, then three lines again.</summary>
    private void DetailOverviewMore_Click(object sender, RoutedEventArgs e)
    {
        var open = double.IsPositiveInfinity(DetailOverview.MaxHeight);
        DetailOverview.MaxHeight = open ? 72 : double.PositiveInfinity;
        DetailOverviewMore.Content = open ? L("Lire la suite", "Read more") : L("Réduire", "Show less");
    }
    /// <summary>New artwork fades in over the previous one instead of flashing through black.</summary>
    private void SetDetailBackdrop(ImageSource? image, bool animate)
    {
        if (ReferenceEquals(DetailBackdrop.Source, image)) return;
        if (!animate || DetailBackdrop.Source is null && image is null) { DetailBackdrop.Source = image; Motion.Fade(DetailBackdrop, 1, 0); return; }
        DetailBackdropPrevious.Source = DetailBackdrop.Source; DetailBackdrop.Source = image;
        Motion.Fade(DetailBackdrop, 1, 420, 0, Motion.Soft);
    }
    private async Task LoadDetailBackdropAsync(MediaItem item, int version)
    {
        if (_images is not { } cache) return;
        var bitmap = await cache.GetAsync(item, true);
        if (version != _detailVersion || bitmap is null) return;
        SetDetailBackdrop(bitmap, animate: true);
    }
    private async Task LoadDetailMetadataAsync(MediaItem item, int version)
    {
        try
        {
            var full = await _metadata!.ItemAsync(item.Id); if (version != _detailVersion) return;
            // A favourite toggled while this request was in flight wins over the older server answer.
            if (_favoriteOverride is { } pending && pending.Id == full.Id) full.UserData.IsFavorite = pending.Favorite;
            _detail = full; RenderDetailText(full); UpdateDetailPlayLabel();
            _ = LoadDetailLogoAsync(full, version);
            await Task.WhenAll(LoadExtrasAsync(full, version), LoadStudioTitlesAsync(full, version));
        }
        catch (Exception ex) when (IsExpected(ex)) { if (version == _detailVersion) SetError(Friendly(ex)); }
    }
    /// <summary>The « Bonus » row: making-of, deleted scenes, textless credits, played from their card. Optional, as the similar titles.</summary>
    private async Task LoadExtrasAsync(MediaItem item, int version)
    {
        var extras = new List<MediaItem>();
        if (item.SpecialFeatureCount is > 0 && _client is { } client)
        {
            try { extras = await client.ExtrasAsync(item.Id); }
            catch (Exception ex) when (IsExpected(ex)) { }
            if (version != _detailVersion) return;
        }
        _extras = extras;
        ExtrasCards.Children.Clear();
        foreach (var extra in extras)
        {
            var card = new MediaCard(extra, true, 0, _settings, _demo);
            card.Click += async (_, _) => await PlayAsync(extra, chosen: true);
            card.HoverEntered += x => x.SetHover(true); card.FocusEntered += x => x.SetHover(true); card.HoverLeft += x => x.SetHover(false);
            ExtrasCards.Children.Add(card); _ = card.LoadImageAsync(_images, true);
        }
        // Under a series' episodes, as the similar titles are when there is no bonus.
        ExtrasSection.Margin = new Thickness(0, _detail?.Type == "Series" ? 36 : 0, 0, 36);
        ExtrasSection.Visibility = extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SimilarSection.Margin = new Thickness(0, _detail?.Type == "Series" && extras.Count == 0 ? 36 : 0, 0, 0);
    }
    private List<MediaItem> _extras = [];
    /// <summary>« Du même studio · Studio Ghibli »: its studio's other titles, those of its saga's row left out when drawn.</summary>
    private (StudioInfo Studio, List<MediaItem> Items)? _studioTitles;
    private async Task LoadStudioTitlesAsync(MediaItem item, int version)
    {
        if (_client is not { } client || item.Studios.Count == 0) return;
        try
        {
            var found = await client.StudioTitlesAsync(item, new HashSet<string>());
            if (version != _detailVersion || found is not { } titles) return;
            if (_store is { } store) { await Task.Run(() => store.ApplyLocalProgress(titles.Items)); if (version != _detailVersion) return; }
            _studioTitles = titles; RenderSimilar();
        }
        // Optional, as the similar titles: without an answer the page is complete as it is.
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    private async void StudioAll_Click(object sender, RoutedEventArgs e)
    { if (_studioTitles is { Studio.Id: { } id } titles) await ShowStudioAsync(id, titles.Studio.Name); }
    private async Task LoadDetailEpisodesAsync(MediaItem item, int version)
    {
        try
        {
            var result = await _metadata!.EpisodesAsync(item.Id);
            var next = _client is { } client ? await NextEpisodeAsync(client, item.Id) : null;
            if (version != _detailVersion) return;
            _episodes = result.Items;
            if (_store is { } store) { var episodes = _episodes; await Task.Run(() => store.ApplyLocalProgress(episodes)); if (version != _detailVersion) return; }
            _detailNext = next is null ? null : _episodes.FirstOrDefault(x => x.Id == next.Id) ?? next;
            var previous = (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
            _seasonBusy = true; SeasonSelector.Items.Clear();
            var pages = EpisodePages.From(_episodes);
            foreach (var page in pages) SeasonSelector.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page });
            // « Saison 1 · 1001–1100 » needs more room than « Saison 1 ».
            SeasonSelector.Width = pages.GroupBy(x => x.Season).Any(x => x.Count() > 1) ? 230 : 180;
            // The page shown before a refresh, else the one of the episode found by a search, of the next episode, of an
            // episode in progress, of the first unwatched.
            var resume = (previous is null ? null : pages.FirstOrDefault(x => x.Season == previous.Season && x.Skip == previous.Skip))
                ?? EpisodePages.Holding(pages, _episodes, _episodes.FirstOrDefault(x => x.Id == _detailEpisode?.Id))
                ?? EpisodePages.Holding(pages, _episodes, _detailNext ?? _episodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? _episodes.FirstOrDefault(x => !x.UserData.Played));
            SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, resume)) ?? SeasonSelector.Items.Cast<object>().FirstOrDefault();
            _seasonBusy = false; RenderEpisodes(); UpdateDetailPlayLabel(); RenderDetailMeta(); ShowOfflineButton();
            if (_detailNext is { } target && _detailFile?.Id != target.Id) await LoadDetailFileAsync(target, version);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version != _detailVersion) return;
            EpisodesPanel.Children.Clear();
            EpisodesPanel.Children.Add(new TextBlock { Text = Friendly(ex), Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap });
            var retry = new Button { Content = L("Réessayer", "Try again"), Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            retry.Click += async (_, _) => { _metadata?.Clear(); EpisodesPanel.Children.Clear(); await LoadDetailEpisodesAsync(item, ++_detailVersion); };
            EpisodesPanel.Children.Add(retry);
        }
    }
    private async Task LoadDetailFileAsync(MediaItem episode, int version)
    {
        try
        {
            var full = await _metadata!.ItemAsync(episode.Id); if (version != _detailVersion) return;
            _detailFile = (episode.Id, full.MediaSources?.FirstOrDefault()); RenderDetailMeta();
        }
        // Only a detail of the page: without it, the rest is complete.
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    /// <summary>"Titres similaires" under the page. Optional: without an answer the page is complete as it is.</summary>
    /// <summary>A film's saga, then the similar titles: a film of the saga is not shown again among them.</summary>
    private async Task LoadSimilarAsync(MediaItem item, int version)
    {
        Saga? saga = null;
        if (item.Type == "Movie")
        {
            try { saga = await _metadata!.SagaAsync(item.Id); }
            catch (Exception ex) when (IsExpected(ex)) { }
            if (version != _detailVersion) return;
        }
        try
        {
            var result = await _metadata!.SimilarAsync(item.Id); if (version != _detailVersion) return;
            var similar = result.Items.Where(x => x.Id != item.Id && saga?.Items.Any(s => s.Id == x.Id) != true).ToList();
            if (_store is { } store) { await Task.Run(() => { if (saga is not null) store.ApplyLocalProgress(saga.Items); store.ApplyLocalProgress(similar); }); if (version != _detailVersion) return; }
            _similar = similar; _saga = saga; RenderSimilar();
        }
        catch (Exception ex) when (IsExpected(ex)) { if (version == _detailVersion && saga is not null) { _saga = saga; RenderSimilar(); } }
    }
    private Saga? _saga;
    /// <summary>The saga's row and the similar titles' row: as many posters as the page is wide, sized like the library grid.</summary>
    private void RenderSimilar()
    {
        var available = EpisodesPanel.ActualWidth > 200 ? EpisodesPanel.ActualWidth : Math.Max(600, DetailScroll.ActualWidth - 100);
        var (columns, width) = PosterLayout(available);
        Fill(SagaCards, _saga?.Items ?? []); SagaTitle.Text = _saga?.Name ?? "";
        SagaSection.Visibility = SagaCards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill(StudioCards, _studioTitles?.Items.Where(x => _saga is null || _saga.Items.All(s => s.Id != x.Id)).ToList() ?? []);
        StudioTitle.Text = _studioTitles is { } studio ? L($"Du même studio · {studio.Studio.Name}", $"More from {studio.Studio.Name}") : "";
        StudioSection.Visibility = StudioCards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Fill(SimilarCards, _similar);
        SimilarSection.Margin = new Thickness(0, _detail?.Type == "Series" && _extras.Count == 0 ? 36 : 0, 0, 0);
        SimilarSection.Visibility = SimilarCards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _similarColumns = columns;
        void Fill(WrapPanel cards, List<MediaItem> items)
        {
            cards.Children.Clear();
            foreach (var (shown, index) in items.Take(columns).Select((x, i) => (x, i)))
            {
                var card = new MediaCard(shown, false, _demo ? Math.Max(0, (_demoItems ?? []).FindIndex(x => x.Id == shown.Id)) : index, _settings, _demo);
                card.Resize(width);
                card.Click += async (_, _) => await ShowDetailsAsync(shown);
                // The library's hover tracking stops under an open page: these cards light up on their own.
                card.HoverEntered += x => x.SetHover(true); card.FocusEntered += x => x.SetHover(true); card.HoverLeft += x => x.SetHover(false);
                card.ContextMenu = CardMenu(card, false);
                cards.Children.Add(card); _ = card.LoadImageAsync(_images, false);
            }
        }
    }
    private int _similarColumns;
    private void SimilarSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _similar.Count == 0 && _saga is null && _studioTitles is null) return;
        var (columns, width) = PosterLayout(EpisodesPanel.ActualWidth);
        if (columns != _similarColumns) { RenderSimilar(); return; }
        foreach (var card in SimilarCards.Children.OfType<MediaCard>().Concat(SagaCards.Children.OfType<MediaCard>()).Concat(StudioCards.Children.OfType<MediaCard>())) card.Resize(width);
    }
    private async Task<MediaItem?> NextEpisodeAsync(JellyfinClient client, string seriesId)
    {
        try
        {
            var next = await client.NextEpisodeAsync(seriesId);
            if (next is not null && _store is { } store) await Task.Run(() => store.ApplyLocalProgress([next]));
            // Jellyfin may still return the episode whose watched report is queued locally.
            return next?.UserData.Played == true ? null : next;
        }
        catch (Exception ex) when (IsExpected(ex)) { return null; }
    }
    /// <summary>"Reprendre · S01 E07" or "Regarder · S02 E01": the button says which episode it opens.</summary>
    private void UpdateDetailPlayLabel()
    {
        if (_detail?.Type != "Series") return;
        var target = _detailNext ?? _episodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played) ?? _episodes.FirstOrDefault(x => !x.UserData.Played);
        if (target is null) { DetailPlay.Content = ActionLabel("play", _episodes.Count > 0 ? L("Revoir depuis le début", "Watch again from the beginning") : L("Regarder", "Watch")); DetailRestart.Visibility = Visibility.Collapsed; return; }
        var resume = target.Progress > 0 && !target.UserData.Played && _settings.RememberPosition;
        var code = $"S{target.ParentIndexNumber ?? 1:00} E{target.IndexNumber ?? 1:00}";
        DetailPlay.Content = ActionLabel("play", GroupPlayLabel(target, code) ?? L($"{(resume ? "Reprendre" : "Regarder")} · {code}", $"{(resume ? "Resume" : "Watch")} · {code}"),
            resume && !InGroup ? MediaQuality.Left(target) : null);
        DetailRestart.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        _detailNext ??= target;
    }
    /// <summary>
    /// A film's page leads with its Lecture: a large button (what it plays, then the time left under it) and the
    /// progress as wide as it, on a taller picture. A series keeps its row as it is, its episodes doing the leading.
    /// </summary>
    private void ShapeDetailPlay(bool film)
    {
        SizeDetailHero();
        DetailPlay.Padding = film ? new Thickness(32, 15, 36, 15) : new Thickness(25, 13, 25, 13);
        DetailPlay.MinWidth = film ? 250 : 0;
        Motion.SetRadius(DetailPlay, new CornerRadius(film ? 14 : 6));
        DetailProgress.Width = film ? double.NaN : 300; DetailProgress.HorizontalAlignment = film ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
    private static StackPanel FilmPlayLabel(string label, string? detail)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(new Icon { Kind = "play", Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeights.Bold });
        if (detail is not null) text.Children.Add(new TextBlock { Text = detail, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Opacity = .72, Margin = new Thickness(0, 1, 0, 0) });
        panel.Children.Add(text); return panel;
    }
    /// <summary>Lecture's label: what it plays, or, in a group, that it starts it for everyone (or joins its title).</summary>
    private void ShowDetailPlayLabel(MediaItem item)
    {
        ShapeDetailPlay(item.Type == "Movie");
        if (item.Type == "Series") { DetailProgress.Visibility = Visibility.Collapsed; UpdateDetailPlayLabel(); return; }
        var resume = item.Progress > 0 && !item.UserData.Played && _settings.RememberPosition;
        DetailPlay.Content = FilmPlayLabel(GroupPlayLabel(item, "") ?? (resume ? L("Reprendre", "Resume") : item.UserData.Played ? L("Revoir", "Watch again") : L("Regarder", "Watch")),
            resume && !InGroup ? MediaQuality.Left(item) : item.DurationLabel is { Length: > 0 } length ? length : null);
        DetailRestart.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
        DetailProgress.Progress = item.Progress; DetailProgress.Visibility = resume ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>« Lancer pour le groupe », « Reprendre pour le groupe à 42 min », « Regarder avec le groupe » (Together.GroupPlayLabel); null outside a group.</summary>
    private string? GroupPlayLabel(MediaItem target, string code) =>
        InGroup ? Together.GroupPlayLabel(code, PlaybackSession.StartAt(target, remember: _settings.RememberPosition), _groupQueue?.ItemId == target.Id) : null;
    private void RenderDetailText(MediaItem item)
    {
        DetailTitle.Text = item.DisplayTitle;
        var kind = item.Type switch { "Series" => L("SÉRIE", "SERIES"), "Episode" => L("ÉPISODE", "EPISODE"), _ => L("FILM", "MOVIE") };
        DetailType.Text = item.ProductionYear is { } year ? $"{kind}  ·  {year}" : kind;
        RenderDetailMeta();
        DetailOverview.Text = OverviewText.Plain(item.Overview ?? L("Aucun résumé disponible pour ce titre.", "No summary available for this title."));
        // Three lines of the page's width hold about 300 characters: a longer summary gets « Lire la suite ».
        DetailOverview.MaxHeight = 72; DetailOverviewMore.Content = L("Lire la suite", "Read more");
        DetailOverviewMore.Visibility = DetailOverview.Text.Length > 260 ? Visibility.Visible : Visibility.Collapsed;
        var tagline = item.Taglines?.Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);
        DetailTagline.Text = tagline ?? ""; DetailTagline.Visibility = tagline is null ? Visibility.Collapsed : Visibility.Visible;
        ShowGenres(item); ShowCast(item);
        ShowPeople(DetailDirectors, item.DirectorPeople, true); DetailDirectorsBlock.Visibility = item.Directors.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var studios = item.Studios.Where(x => x.Name.Trim().Length > 0).Take(3).ToList();
        ShowLinks(DetailStudios, studios.Select(x => (x.Name, L($"Les titres de {x.Name} dans ta bibliothèque", $"Titles from {x.Name} in your library"),
            x.Id is { Length: > 0 } id ? () => ShowStudioAsync(id, x.Name) : (Func<Task>?)null)).ToList());
        DetailStudiosBlock.Visibility = studios.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowDetailPlayLabel(item);
        DetailRestart.Content ??= ActionLabel("refresh", L("Depuis le début", "From the beginning"));
        var favorite = item.UserData.IsFavorite;
        FavoriteButton.Content = ActionLabel(favorite ? "heart-fill" : "heart", favorite ? L("Favori", "Favorite") : L("Ajouter aux favoris", "Add to favorites"));
        ShowState(FavoriteButton, favorite, favorite ? L("Dans tes favoris. Clique pour le retirer", "In your favorites. Click to remove it") : null);
        var played = item.UserData.Played;
        WatchedButton.Content = ActionLabel("check", played ? item.Type == "Series" ? L("Vue", "Watched") : L("Vu", "Watched")
            : item.Type == "Series" ? L("Marquer la série comme vue", "Mark show as watched") : L("Marquer comme vu", "Mark as watched"));
        ShowState(WatchedButton, played, played ? L("Vu. Clique pour le marquer comme non vu", "Watched. Click to mark it as unwatched") : null);
        ShowTogetherButton(); ShowOfflineButton(); _ = ShowFilesButtonAsync(item); _ = ShowFollowButtonAsync(item);
    }
    /// <summary>Length and ratings, then the picture of the file Lecture plays and when it ends (the episode it picks up at, on a series).</summary>
    private void RenderDetailMeta()
    {
        if (_detail is not { } item) return;
        var target = item.Type == "Series" ? _detailNext : item;
        var file = item.Type != "Series" ? MediaQuality.Label(item) : _detailFile is { } known && known.Id == target?.Id ? MediaQuality.Label(known.Source) : null;
        var resume = target is not null && target.Progress > 0 && !target.UserData.Played && _settings.RememberPosition;
        DetailMeta.Text = string.Join("  ·  ", new[] { item.Subtitle, item.CommunityRating is { } rating and > 0 ? $"★ {Number(rating, "0.0")}" : "", item.OfficialRating ?? "",
            file ?? "", MediaQuality.EndsAt(target, DateTime.Now, resume) ?? "" }.Where(x => x.Length > 0));
    }
    /// <summary>
    /// A button of the page on or off, each state seen at a glance: on, a white outline and its word kept beside its icon
    /// (« Favori », « Suivie »), also once the row is short of room for words; off, the plain button, its icon alone.
    /// </summary>
    /// <param name="tip">Said by its tooltip and to the Narrator when on: what it is and what a click does.</param>
    private void ShowState(Button button, bool on, string? tip = null)
    {
        if (on) { button.BorderBrush = Palette.Get("Text"); button.BorderThickness = new Thickness(1.5); }
        else { button.ClearValue(Control.BorderBrushProperty); button.ClearValue(Control.BorderThicknessProperty); }
        if (!ReferenceEquals(button, TogetherButton)) ActionRow.SetKeep(button, on);
        button.ToolTip = tip;
        if (on && tip is not null) AutomationProperties.SetName(button, tip); else button.ClearValue(AutomationProperties.NameProperty);
    }
    /// <summary>A green dot: on and live (a group, a series followed).</summary>
    private static System.Windows.Shapes.Ellipse LiveDot() => new() { Width = 8, Height = 8, Fill = Palette.Get("Success"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The cast with their photos under the picture; each one their titles in the library.</summary>
    private void ShowCast(MediaItem item)
    {
        DetailCastCards.Children.Clear();
        foreach (var person in item.CastPeople)
        {
            var photo = person.PrimaryImageTag is { } tag ? new Dictionary<string, string> { ["Primary"] = tag } : [];
            var card = new PersonCard(new MediaItem { Id = person.Id ?? "", Name = person.Name, Type = "Person", ImageTags = photo }, person.Role);
            if (person.Id is { Length: > 0 }) card.Click += async (_, _) => await ShowPersonAsync(person, false);
            else { card.Focusable = false; card.IsHitTestVisible = false; }
            DetailCastCards.Children.Add(card);
            if (_images is { } images && person.Id is { Length: > 0 }) _ = card.LoadImageAsync(images);
        }
        DetailCastSection.Visibility = DetailCastCards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <param name="detail">Said lighter after the label: « 1 h 27 restantes » beside Reprendre.</param>
    private static StackPanel ActionLabel(string icon, string label, string? detail = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18 });
        var text = new TextBlock { Text = label, Margin = new Thickness(9, 0, 0, 0) };
        if (detail is not null) text.Inlines.Add(new System.Windows.Documents.Run("  ·  " + detail) { FontWeight = FontWeights.Normal, FontSize = 12.5 });
        panel.Children.Add(text); return panel;
    }
    private void Season_Changed(object sender, SelectionChangedEventArgs e) { if (!_seasonBusy) { RenderEpisodes(); ShowOfflineButton(); Motion.Reveal(EpisodesPanel, 220, 7); } }
    private void RenderEpisodes()
    {
        EpisodesPanel.Children.Clear(); _episodeCopyLabels.Clear();
        var page = (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as EpisodePage;
        var episodes = page is null ? [] : EpisodePages.Episodes(_episodes, page);
        // The whole season's count, also on a slice of a long one.
        EpisodeHeading.Text = $"{L("Épisodes", "Episodes")}  ·  {(page is null ? 0 : _episodes.Count(x => (x.ParentIndexNumber ?? 1) == page.Season))}";
        ShowSeasonMark(page);
        if (episodes.Count == 0) { EpisodesPanel.Children.Add(new TextBlock { Text = L("Aucun épisode disponible dans cette bibliothèque.", "No episodes available in this library."), Foreground = Palette.Get("Muted") }); return; }
        // The episode Lecture plays is in another season than the one shown (a long anime opened on its first): a click brings it.
        if (_detailEpisode is null && _detailNext is { } next && EpisodePages.NextElsewhere(SeasonPages(), _episodes, page, next) is { } elsewhere)
            EpisodesPanel.Children.Add(NextElsewhereButton(next, elsewhere));
        foreach (var episode in episodes)
        {
            var isNext = _detailNext?.Id == episode.Id; var wanted = _detailEpisode?.Id == episode.Id;
            var button = new Button { Background = Palette.Get(isNext || wanted ? "Raised" : "Surface"), BorderBrush = isNext || wanted ? Palette.Get("Line") : Brushes.Transparent, Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 9), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            var thumb = new Grid { Width = 160, Height = 90, Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 4, 4), Background = Palette.Get("Raised2") };
            var image = new Image { Stretch = Stretch.UniformToFill }; thumb.Children.Add(image);
            thumb.Children.Add(new Border { Background = Brush("#44000000") });
            thumb.Children.Add(new Icon { Kind = "play", Width = 23, Height = 23, Foreground = Brushes.White });
            if (episode.Progress > 0 && !episode.UserData.Played) thumb.Children.Add(new Border { Height = 3, Width = 160 * episode.Progress, Background = Palette.Get("Text"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
            grid.Children.Add(thumb);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(21, 0, 20, 0) }; Grid.SetColumn(copy, 1);
            var title = episode.IndexNumber is { } number ? $"{number:00}   {episode.Name}" : episode.Name;
            copy.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            copy.Children.Add(new TextBlock { Text = OverviewText.Plain(episode.Overview, flat: true), FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 36, LineHeight = 18, Margin = new Thickness(0, 7, 0, 0) });
            grid.Children.Add(copy);
            var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(status, 2);
            status.Children.Add(new TextBlock { Text = episode.DurationLabel, FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), HorizontalAlignment = HorizontalAlignment.Right });
            var state = episode.UserData.Played ? L("✓ Vu", "✓ Watched") : isNext ? episode.Progress > 0 ? L("En cours", "In progress") : L("À suivre", "Up next") : null;
            if (state is not null) status.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = Palette.Get(episode.UserData.Played ? "Success" : "TextBody"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
            if (CanKeep(episode)) status.Children.Add(EpisodeCopyLabel(episode));
            grid.Children.Add(status); button.Content = grid;
            var keptText = CopyState(episode.Id) is { } kept ? ", " + kept.ToLowerInvariant() : "";
            AutomationProperties.SetName(button, L($"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", vu" : isNext ? ", à suivre" : "")}{keptText}",
                $"Episode {episode.IndexNumber?.ToString() ?? ""}: {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", watched" : isNext ? ", up next" : "")}{keptText}"));
            button.Click += async (_, _) => await PlayAsync(episode, chosen: true);
            button.ContextMenu = EpisodeMenu(episode);
            EpisodesPanel.Children.Add(button);
            if (_images is not null) _ = LoadEpisodeImageAsync(image, episode);
            // The episode found by a search comes into view the first time its season shows.
            if (wanted && !_detailEpisodeShown)
            {
                _detailEpisodeShown = true;
                _ = Dispatcher.BeginInvoke(() => button.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }
    private List<EpisodePage> SeasonPages() => SeasonSelector.Items.OfType<ComboBoxItem>().Select(x => x.Tag).OfType<EpisodePage>().ToList();
    /// <summary>« À suivre : S02 E01 · Le titre », above the episodes of another season; « Saison 2 › » shows its own.</summary>
    private Button NextElsewhereButton(MediaItem next, EpisodePage elsewhere)
    {
        var button = new Button { Background = Palette.Get("Surface"), BorderBrush = Brushes.Transparent, Padding = new Thickness(15, 12, 15, 12), Margin = new Thickness(0, 0, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var code = $"S{next.ParentIndexNumber ?? 1:00} E{next.IndexNumber ?? 1:00}";
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Icon { Kind = "play", Width = 18, Height = 18, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.Normal, Foreground = Palette.Get("TextSecondary") }; Grid.SetColumn(text, 1);
        text.Inlines.Add(new System.Windows.Documents.Run(L("À suivre : ", "Up next: "))); text.Inlines.Add(new System.Windows.Documents.Run(code) { FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Text") });
        if (next.Name.Length > 0) text.Inlines.Add(new System.Windows.Documents.Run("  ·  " + next.Name));
        grid.Children.Add(text);
        var go = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(go, 2);
        go.Children.Add(new TextBlock { Text = elsewhere.Label, FontSize = 13, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center });
        go.Children.Add(new Icon { Kind = "next", Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0), Foreground = Palette.Get("Muted") });
        grid.Children.Add(go); button.Content = grid;
        AutomationProperties.SetName(button, L($"À suivre : {code} {next.Name}, afficher {elsewhere.Label}", $"Up next: {code} {next.Name}, show {elsewhere.Label}"));
        button.Click += (_, _) => SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => ReferenceEquals(x.Tag, elsewhere)) ?? SeasonSelector.SelectedItem;
        return button;
    }
    /// <summary>
    /// « Marquer la saison comme vue », or « non vue » once all its episodes are, beside the season list: only when the
    /// series has another season (with one, « Marquer la série comme vue » does it).
    /// </summary>
    private void ShowSeasonMark(EpisodePage? page)
    {
        if (page is null || !EpisodePages.MarksSeasons(_episodes)) { SeasonMarkButton.Visibility = Visibility.Collapsed; return; }
        var played = EpisodePages.SeasonMark(_episodes, page.Season).Played;
        SeasonMarkButton.Content = ActionLabel(played ? "refresh" : "check", played ? L("Marquer la saison comme non vue", "Mark season as unwatched") : L("Marquer la saison comme vue", "Mark season as watched"));
        SeasonMarkButton.Tag = (page.Season, !played); SeasonMarkButton.Visibility = Visibility.Visible;
    }
    /// <summary>A whole season watched or not: Jellyfin is told (the season in one call, else each episode), then the page follows it.</summary>
    private async void SeasonMark_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonMarkButton.Tag is not (int season, bool played) || _detail is not { } series) return;
        SeasonMarkButton.IsEnabled = false;
        try
        {
            var episodes = _episodes.Where(x => EpisodePages.Season(x) == season).ToList();
            if (_client is { } client) foreach (var id in EpisodePages.SeasonMark(_episodes, season).Ids) await client.SetPlayedAsync(id, played);
            else if (!_demo) return;
            if (_store is { } store) await Task.Run(() => { foreach (var episode in episodes) store.ForgetProgress(episode.Id); });
            foreach (var episode in episodes) { episode.UserData.Played = played; episode.UserData.PlaybackPositionTicks = 0; }
            _metadata?.Clear();
            SetNotice(played ? L("Saison marquée comme vue.", "Season marked as watched.") : L("Saison marquée comme non vue.", "Season marked as unwatched."));
            RenderEpisodes();
            if (!_demo) { await RefreshDetailAsync(); await RefreshAsync(quiet: true); }
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); if (_detail?.Id == series.Id) await RefreshDetailAsync(); }
        finally { SeasonMarkButton.IsEnabled = true; }
    }
    private ContextMenu EpisodeMenu(MediaItem episode)
    {
        var menu = new ContextMenu();
        var resume = episode.Progress > 0 && !episode.UserData.Played && _settings.RememberPosition;
        menu.Items.Add(MenuAction(resume ? L("Reprendre", "Resume") : L("Regarder", "Watch"), "play", async () => await PlayAsync(episode, chosen: true)));
        if (resume) menu.Items.Add(MenuAction(L("Lire depuis le début", "Play from the beginning"), "refresh", async () => await PlayAsync(episode, fromStart: true, chosen: true)));
        menu.Items.Add(MenuAction(episode.UserData.Played ? L("Marquer comme non vu", "Mark as unwatched") : L("Marquer comme vu", "Mark as watched"), episode.UserData.Played ? "refresh" : "check", async () => { await SetPlayedAsync(episode, !episode.UserData.Played); await RefreshDetailAsync(); }));
        if (OfflineMenuItem(episode) is { } offline) menu.Items.Add(offline);
        // An administrator's, once Jellyfin said (asked by the series' page).
        if (UpgradeMenuItem(episode) is { } upgrade)
        {
            var line = new Separator();
            menu.Items.Add(line); menu.Items.Add(upgrade);
            menu.Opened += (_, _) => line.Visibility = upgrade.Visibility = Administers ? Visibility.Visible : Visibility.Collapsed;
        }
        return menu;
    }
    private async Task LoadEpisodeImageAsync(Image image, MediaItem item)
    {
        if (_images is not { } cache) return;
        var task = cache.EpisodeAsync(item);
        var cached = task.IsCompletedSuccessfully;
        image.Source = await task;
        // Thumbnails downloaded now fade in; cached ones appear at once.
        if (!cached && image.Source is not null) Motion.Fade(image, 1, 240, 0, Motion.Soft);
    }
    /// <summary>Each name a link to this person's titles in the library.</summary>
    /// <param name="first">How many are named at first; « +N » then names the others.</param>
    private void ShowPeople(TextBlock target, IReadOnlyList<PersonInfo> people, bool directed, int first = int.MaxValue)
    {
        ShowLinks(target, people.Take(first).Select(person => (person.Name,
            L((directed ? "Les titres réalisés par " : "Les titres avec ") + person.Name + " dans ta bibliothèque", (directed ? "Titles directed by " : "Titles with ") + person.Name + " in your library"),
            person.Id is { Length: > 0 } ? () => ShowPersonAsync(person, directed) : (Func<Task>?)null)).ToList());
        if (people.Count <= first) return;
        var more = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"  +{people.Count - first}"))
        {
            Foreground = Palette.Get("Muted"), TextDecorations = null, Cursor = Cursors.Hand,
            ToolTip = L("Voir toute la distribution", "See the whole cast"), FocusVisualStyle = (Style)FindResource("KeyboardFocus")
        };
        System.Windows.Automation.AutomationProperties.SetName(more, L($"{people.Count - first} de plus : voir toute la distribution", $"{people.Count - first} more: see the whole cast"));
        more.Click += (_, _) => ShowPeople(target, people, directed);
        target.Inlines.Add(more);
    }
    /// <summary>Its genres, each one Films or Séries (as the title is) filtered on it, as in Mira web.</summary>
    private void ShowGenres(MediaItem item)
    {
        if (item.Genres.Length == 0) { DetailGenres.Inlines.Clear(); DetailGenres.Text = L("Non renseignés", "Not listed"); return; }
        var films = item.Type == "Series" ? L("séries", "shows") : L("films", "movies");
        List<(string, string, Func<Task>?)> links = [.. item.Genres.Select(genre => (genre, L($"Les {films} « {genre} » de ta bibliothèque", $"The {genre} {films} of your library"),
            (Func<Task>)(() => ShowGenreAsync(genre, item.Type))))];
        ShowLinks(DetailGenres, links);
    }
    /// <summary>
    /// Names separated by a dot; each one that can be opened is a link (underlined under the pointer, reachable with Tab
    /// and Enter): a person's, a studio's or a genre's titles in the library.
    /// </summary>
    private void ShowLinks(TextBlock target, IReadOnlyList<(string Name, string Tip, Func<Task>? Open)> links)
    {
        target.Inlines.Clear();
        for (var i = 0; i < links.Count; i++)
        {
            var (name, tip, open) = links[i];
            if (i > 0) target.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            if (_demo || open is null) { target.Inlines.Add(new System.Windows.Documents.Run(name)); continue; }
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(name))
            {
                Foreground = target.Foreground, TextDecorations = null, Cursor = Cursors.Hand, ToolTip = tip
            };
            // Underlined under the pointer and with the keyboard focus, ringed like the other controls (WPF's own ring is
            // a black dotted line, unseen on this page).
            link.FocusVisualStyle = (Style)FindResource("KeyboardFocus");
            void Underline() => link.TextDecorations = link.IsMouseOver || link.IsKeyboardFocused ? TextDecorations.Underline : null;
            link.MouseEnter += (_, _) => Underline(); link.MouseLeave += (_, _) => Underline();
            link.GotKeyboardFocus += (_, _) => Underline(); link.LostKeyboardFocus += (_, _) => Underline();
            link.Click += async (_, _) => await open();
            target.Inlines.Add(link);
        }
    }
    private async Task ToggleFavoriteAsync(MediaItem item)
    {
        try
        {
            var favorite = !item.UserData.IsFavorite;
            if (_client is not null) await _client.SetFavoriteAsync(item.Id, favorite);
            else if (!_demo) return;
            item.UserData.IsFavorite = favorite; _favoriteOverride = (item.Id, favorite);
            // In the favourites view, a removed title leaves the list.
            var left = _catalog.MarkFavorite(item, favorite, _similar);
            if (_detail is { } open && open.Id == item.Id) open.UserData.IsFavorite = favorite;
            _metadata?.Clear();
            if (left) RenderLibrary();
        }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item) return; FavoriteButton.IsEnabled = false;
        try { await ToggleFavoriteAsync(item); if (_detail?.Id == item.Id) RenderDetailText(_detail); }
        finally { FavoriteButton.IsEnabled = true; }
    }
    private async void Watched_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } item) return; WatchedButton.IsEnabled = false;
        try { await SetPlayedAsync(item, !item.UserData.Played); await RefreshDetailAsync(); }
        finally { WatchedButton.IsEnabled = true; }
    }
}
