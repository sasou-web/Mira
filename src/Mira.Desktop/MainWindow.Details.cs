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
    private List<MediaItem> _similar = [];
    private async void DetailPlay_Click(object sender, RoutedEventArgs e)
    { if (_detail is not null) await PlayAsync(_detailNext is { } next && _detail.Type == "Series" ? next : _detail); }
    private async void CloseDetails_Click(object sender, RoutedEventArgs e) => await CloseDetailsAsync();
    private async Task CloseDetailsAsync()
    {
        ++_detailVersion; _returnToDetail = null;
        await Motion.HideAsync(DetailOverlay, 170); UpdateHeroClock();
        // Back to the card that opened the page, so keyboard users keep their place.
        if (_detailOrigin is UIElement { IsVisible: true } origin) origin.Focus();
        _detailOrigin = null;
    }
    private async Task ShowDetailsAsync(MediaItem item)
    {
        var version = ++_detailVersion;
        var previewArt = _hoverCard?.Item.Id == item.Id ? _hoverCard.ImageSource : null;
        if (DetailOverlay.Visibility != Visibility.Visible) _detailOrigin = Keyboard.FocusedElement;
        ClosePreview(); _detail = item; _episodes = []; _detailNext = null; _favoriteOverride = null; _similar = [];
        SimilarCards.Children.Clear(); SimilarSection.Visibility = Visibility.Collapsed;
        _ = Motion.HideAsync(SettingsOverlay); SmoothScroll.Jump(DetailScroll);
        DetailBackdropPrevious.Source = null;
        SetDetailBackdrop(previewArt ?? (_hero?.Id == item.Id || _hero?.SeriesId == item.Id ? HeroImage.Source : null), animate: false);
        if (_demo) SetDetailBackdrop(DemoLibrary.Artwork(Math.Max(0, _items.FindIndex(x => x.Id == item.Id)), true), animate: false);
        RenderDetailText(item); EpisodesPanel.Children.Clear();
        _seasonBusy = true; SeasonSelector.Items.Clear(); _seasonBusy = false;
        EpisodeHeading.Visibility = SeasonSelector.Visibility = item.Type == "Series" ? Visibility.Visible : Visibility.Collapsed;
        if (item.Type == "Series") EpisodesPanel.Children.Add(new TextBlock { Text = "Chargement des épisodes…", Foreground = Brush("#91919B"), Margin = new Thickness(0, 20, 0, 20) });
        Motion.Reveal(DetailOverlay, 300, 14); UpdateHeroClock();
        // Focus moves into the page: Enter plays, Tab stays inside, Escape goes back.
        _ = Dispatcher.BeginInvoke(() => { if (version == _detailVersion) DetailPlay.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        var tasks = new List<Task>();
        if (_images is not null) tasks.Add(LoadDetailBackdropAsync(item, version));
        if (_metadata is not null)
        {
            tasks.Add(LoadDetailMetadataAsync(item, version));
            if (item.Type == "Series") tasks.Add(LoadDetailEpisodesAsync(item, version));
            tasks.Add(LoadSimilarAsync(item, version));
        }
        else if (_demo)
        {
            if (item.Type == "Series") { EpisodesPanel.Children.Clear(); EpisodesPanel.Children.Add(new TextBlock { Text = "Les épisodes de ta bibliothèque apparaîtront ici après connexion à Jellyfin.", Foreground = Brush("#91919B"), TextWrapping = TextWrapping.Wrap }); SeasonSelector.Visibility = Visibility.Collapsed; }
            _similar = (_demoItems ?? DemoLibrary.Items()).Where(x => x.Id != item.Id && x.Genres.Intersect(item.Genres).Any()).ToList(); RenderSimilar();
        }
        await Task.WhenAll(tasks);
    }
    /// <summary>Reloads progress and watched marks of the open page (after playback) without moving it.</summary>
    private async Task RefreshDetailAsync()
    {
        if (_detail is not { } item || DetailOverlay.Visibility != Visibility.Visible) return;
        var version = ++_detailVersion; _metadata?.Clear();
        if (_metadata is null) { RenderDetailText(item); if (_episodes.Count > 0) RenderEpisodes(); return; }
        var tasks = new List<Task> { LoadDetailMetadataAsync(item, version) };
        if (item.Type == "Series") tasks.Add(LoadDetailEpisodesAsync(item, version));
        await Task.WhenAll(tasks);
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
        }
        catch (Exception ex) when (IsExpected(ex)) { if (version == _detailVersion) SetNotice(Friendly(ex)); }
    }
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
            var previous = (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as int?;
            _seasonBusy = true; SeasonSelector.Items.Clear();
            foreach (var season in _episodes.Select(x => x.ParentIndexNumber ?? 1).Distinct().Order()) SeasonSelector.Items.Add(new ComboBoxItem { Content = season == 0 ? "Épisodes spéciaux" : $"Saison {season}", Tag = season });
            var resumeSeason = previous ?? _detailNext?.ParentIndexNumber ?? _episodes.FirstOrDefault(x => x.Progress > 0 && !x.UserData.Played)?.ParentIndexNumber ?? _episodes.FirstOrDefault(x => !x.UserData.Played)?.ParentIndexNumber;
            SeasonSelector.SelectedItem = SeasonSelector.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (int)x.Tag == resumeSeason) ?? SeasonSelector.Items.Cast<object>().FirstOrDefault();
            _seasonBusy = false; RenderEpisodes(); UpdateDetailPlayLabel();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version != _detailVersion) return;
            EpisodesPanel.Children.Clear();
            EpisodesPanel.Children.Add(new TextBlock { Text = Friendly(ex), Foreground = Brush("#F0B4A7"), TextWrapping = TextWrapping.Wrap });
            var retry = new Button { Content = "Réessayer", Style = (Style)FindResource("Quiet"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            retry.Click += async (_, _) => { _metadata?.Clear(); EpisodesPanel.Children.Clear(); await LoadDetailEpisodesAsync(item, ++_detailVersion); };
            EpisodesPanel.Children.Add(retry);
        }
    }
    /// <summary>"Titres similaires" under the page. Optional: without an answer the page is complete as it is.</summary>
    private async Task LoadSimilarAsync(MediaItem item, int version)
    {
        try
        {
            var result = await _metadata!.SimilarAsync(item.Id); if (version != _detailVersion) return;
            var similar = result.Items.Where(x => x.Id != item.Id).ToList();
            if (_store is { } store) { await Task.Run(() => store.ApplyLocalProgress(similar)); if (version != _detailVersion) return; }
            _similar = similar; RenderSimilar();
        }
        catch (Exception ex) when (IsExpected(ex)) { }
    }
    /// <summary>One row of posters, as many as the page is wide, sized like the library grid.</summary>
    private void RenderSimilar()
    {
        SimilarCards.Children.Clear();
        var available = EpisodesPanel.ActualWidth > 200 ? EpisodesPanel.ActualWidth : Math.Max(600, DetailScroll.ActualWidth - 100);
        var (columns, width) = PosterLayout(available);
        foreach (var (similar, index) in _similar.Take(columns).Select((x, i) => (x, i)))
        {
            var card = new MediaCard(similar, false, _demo ? Math.Max(0, (_demoItems ?? []).FindIndex(x => x.Id == similar.Id)) : index, _settings, _demo);
            card.Resize(width);
            card.Click += async (_, _) => await ShowDetailsAsync(similar);
            // The library's hover tracking stops under an open page: these cards light up on their own.
            card.HoverEntered += x => x.SetHover(true); card.FocusEntered += x => x.SetHover(true); card.HoverLeft += x => x.SetHover(false);
            card.ContextMenu = CardMenu(card, false);
            SimilarCards.Children.Add(card); _ = card.LoadImageAsync(_images, false);
        }
        SimilarSection.Margin = new Thickness(0, _detail?.Type == "Series" ? 36 : 0, 0, 0);
        SimilarSection.Visibility = SimilarCards.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _similarColumns = columns;
    }
    private int _similarColumns;
    private void SimilarSection_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _similar.Count == 0) return;
        var (columns, width) = PosterLayout(EpisodesPanel.ActualWidth);
        if (columns != _similarColumns) { RenderSimilar(); return; }
        foreach (var card in SimilarCards.Children.OfType<MediaCard>()) card.Resize(width);
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
        if (target is null) { DetailPlay.Content = ActionLabel("play", _episodes.Count > 0 ? "Revoir depuis le début" : "Regarder"); return; }
        var resume = target.Progress > 0 && !target.UserData.Played && _settings.RememberPosition;
        DetailPlay.Content = ActionLabel("play", $"{(resume ? "Reprendre" : "Regarder")} · S{target.ParentIndexNumber ?? 1:00} E{target.IndexNumber ?? 1:00}");
        _detailNext ??= target;
    }
    private void RenderDetailText(MediaItem item)
    {
        DetailTitle.Text = item.DisplayTitle;
        var kind = item.Type switch { "Series" => "SÉRIE", "Episode" => "ÉPISODE", _ => "FILM" };
        DetailType.Text = item.ProductionYear is { } year ? $"{kind}  ·  {year}" : kind;
        DetailMeta.Text = string.Join("  ·  ", new[] { item.Subtitle, item.CommunityRating is > 0 ? $"★ {item.CommunityRating:0.0}" : "", item.OfficialRating ?? "" }.Where(x => x.Length > 0));
        DetailOverview.Text = PlainText(item.Overview ?? "Aucun résumé disponible pour ce titre."); DetailGenres.Text = item.Genres.Length > 0 ? string.Join("  ·  ", item.Genres) : "Non renseignés";
        ShowPeople(DetailCast, item.CastPeople, false); DetailCastBlock.Visibility = item.Cast.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowPeople(DetailDirectors, item.DirectorPeople, true); DetailDirectorsBlock.Visibility = item.Directors.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (item.Type != "Series") DetailPlay.Content = ActionLabel("play", item.Progress > 0 && !item.UserData.Played && _settings.RememberPosition ? "Reprendre" : item.UserData.Played ? "Revoir" : "Regarder");
        else UpdateDetailPlayLabel();
        FavoriteButton.Content = ActionLabel(item.UserData.IsFavorite ? "heart" : "plus", item.UserData.IsFavorite ? "Dans tes favoris" : "Ajouter aux favoris");
        WatchedButton.Content = ActionLabel(item.UserData.Played ? "refresh" : "check", item.UserData.Played ? "Marquer comme non vu" : item.Type == "Series" ? "Marquer la série comme vue" : "Marquer comme vu");
        AutomationProperties.SetName(DetailPlay, (DetailPlay.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "Regarder");
    }
    private static StackPanel ActionLabel(string icon, string label)
    { var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 9, 0) }); panel.Children.Add(new TextBlock { Text = label }); return panel; }
    private void Season_Changed(object sender, SelectionChangedEventArgs e) { if (!_seasonBusy) { RenderEpisodes(); Motion.Reveal(EpisodesPanel, 220, 7); } }
    private void RenderEpisodes()
    {
        EpisodesPanel.Children.Clear();
        var season = (SeasonSelector.SelectedItem as ComboBoxItem)?.Tag as int?;
        var episodes = _episodes.Where(x => (x.ParentIndexNumber ?? 1) == season).ToList();
        EpisodeHeading.Text = $"Épisodes  ·  {episodes.Count}";
        if (episodes.Count == 0) { EpisodesPanel.Children.Add(new TextBlock { Text = "Aucun épisode disponible dans cette bibliothèque.", Foreground = Brush("#93939D") }); return; }
        foreach (var episode in episodes)
        {
            var isNext = _detailNext?.Id == episode.Id;
            var button = new Button { Background = Brush(isNext ? "#17171A" : "#101011"), BorderBrush = isNext ? Brush("#2E2E34") : Brushes.Transparent, Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 9), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            var thumb = new Grid { Width = 160, Height = 90, Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 4, 4), Background = Brush("#222225") };
            var image = new Image { Stretch = Stretch.UniformToFill }; thumb.Children.Add(image);
            thumb.Children.Add(new Border { Background = Brush("#44000000") });
            thumb.Children.Add(new Icon { Kind = "play", Width = 23, Height = 23, Foreground = Brushes.White });
            if (episode.Progress > 0 && !episode.UserData.Played) thumb.Children.Add(new Border { Height = 3, Width = 160 * episode.Progress, Background = Brush("#F5F5F7"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
            grid.Children.Add(thumb);
            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(21, 0, 20, 0) }; Grid.SetColumn(copy, 1);
            var title = episode.IndexNumber is { } number ? $"{number:00}   {episode.Name}" : episode.Name;
            copy.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            copy.Children.Add(new TextBlock { Text = PlainText(episode.Overview ?? ""), FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brush("#94949C"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 36, LineHeight = 18, Margin = new Thickness(0, 7, 0, 0) });
            grid.Children.Add(copy);
            var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetColumn(status, 2);
            status.Children.Add(new TextBlock { Text = episode.DurationLabel, FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Brush("#A0A0AA"), HorizontalAlignment = HorizontalAlignment.Right });
            var state = episode.UserData.Played ? "✓ Vu" : isNext ? episode.Progress > 0 ? "En cours" : "À suivre" : null;
            if (state is not null) status.Children.Add(new TextBlock { Text = state, FontSize = 11, Foreground = Brush(episode.UserData.Played ? "#A0C7A8" : "#DADAE0"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
            grid.Children.Add(status); button.Content = grid;
            AutomationProperties.SetName(button, $"Épisode {episode.IndexNumber?.ToString() ?? ""} : {episode.Name}, {episode.DurationLabel}{(episode.UserData.Played ? ", vu" : isNext ? ", à suivre" : "")}");
            button.Click += async (_, _) => await PlayAsync(episode);
            button.ContextMenu = EpisodeMenu(episode);
            EpisodesPanel.Children.Add(button);
            if (_images is not null) _ = LoadEpisodeImageAsync(image, episode);
        }
    }
    private ContextMenu EpisodeMenu(MediaItem episode)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuAction(episode.Progress > 0 && !episode.UserData.Played ? "Reprendre" : "Regarder", "play", async () => await PlayAsync(episode)));
        menu.Items.Add(MenuAction(episode.UserData.Played ? "Marquer comme non vu" : "Marquer comme vu", episode.UserData.Played ? "refresh" : "check", async () => { await SetPlayedAsync(episode, !episode.UserData.Played); await RefreshDetailAsync(); }));
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
    /// <summary>
    /// Names separated by a dot; each one with a Jellyfin id is a link to this person's titles in the library
    /// (underlined under the pointer, reachable with Tab and Enter).
    /// </summary>
    private void ShowPeople(TextBlock target, IReadOnlyList<PersonInfo> people, bool directed)
    {
        target.Inlines.Clear();
        for (var i = 0; i < people.Count; i++)
        {
            var person = people[i];
            if (i > 0) target.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            if (_demo || person.Id is not { Length: > 0 }) { target.Inlines.Add(new System.Windows.Documents.Run(person.Name)); continue; }
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(person.Name))
            {
                Foreground = target.Foreground, TextDecorations = null, Cursor = Cursors.Hand,
                ToolTip = (directed ? "Les titres réalisés par " : "Les titres avec ") + person.Name + " dans ta bibliothèque"
            };
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            link.Click += async (_, _) => await ShowPersonAsync(person, directed);
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
            foreach (var known in _items.Concat(_resume).Concat(_nextUp).Concat(_similar).Where(x => x.Id == item.Id)) known.UserData.IsFavorite = favorite;
            if (_detail is { } open && open.Id == item.Id) open.UserData.IsFavorite = favorite;
            _metadata?.Clear();
            // In the favourites view, a removed title leaves the list.
            if (_favorites && !favorite) { _items.RemoveAll(x => x.Id == item.Id); _totalCount = Math.Max(0, _totalCount - 1); RenderLibrary(); }
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
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
