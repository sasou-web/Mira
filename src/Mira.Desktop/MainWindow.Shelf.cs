using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « L'étagère » (ogsas, maquette A, 2026-11-16; CollectionShelf of Core): a grouped collection is one card in Films,
// Séries and Ta bibliothèque, in place of its titles; a click opens its page, built like a title's: its name and facts,
// « Reprendre » across the whole collection, Favori, Vu, « … », a bar with one notch per title, then its titles numbered
// in their watch order (or by release date), each one opening its own page. Home and the search keep the titles.
public partial class MainWindow
{
    /// <summary>The collections grouped for the account of a client, its choices, and when Jellyfin said (read again after <see cref="GroupingAge"/>).</summary>
    private (JellyfinClient Client, CollectionShelf.Index Index, Dictionary<string, CollectionShelf.Choice> Choices, DateTimeOffset At)? _grouping;
    private static readonly TimeSpan GroupingAge = TimeSpan.FromMinutes(10);
    /// <summary>Read again at the next refresh (a title watched: the cards' progress), kept meanwhile should Jellyfin not answer.</summary>
    private void ForgetGrouping() { if (_grouping is { } known) _grouping = known with { At = DateTimeOffset.MinValue }; }
    /// <summary>The collection whose page is open, its titles (in no order), and the order shown.</summary>
    private MediaItem? _shelfBox;
    private List<MediaItem> _shelfTitles = [];
    private IReadOnlyList<string>? _shelfOrder;
    private bool _shelfByRelease, _shelfEditing, _shelfOrderBusy, _returnToShelf;
    private int _shelfVersion;
    /// <summary>What « Reprendre » plays: an episode of the series under way, or a film.</summary>
    private MediaItem? _shelfTarget;
    private IInputElement? _shelfOrigin;

    /// <summary>
    /// The collections grouped for this account (its choices, then the titles of each one), kept a while: a grid's pages
    /// and its quiet refreshes ask for it each time. None in the demo, or when Jellyfin does not say: the titles then show
    /// as they are.
    /// </summary>
    private async Task<CollectionShelf.Index> GroupingAsync(JellyfinClient client)
    {
        if (_demo) return CollectionShelf.Index.None;
        if (_grouping is { } known && ReferenceEquals(known.Client, client) && DateTimeOffset.UtcNow - known.At < GroupingAge) return known.Index;
        try
        {
            var ct = _connectionLifetime.Token;
            var choices = await client.ShelfChoicesAsync(ct);
            var index = await client.ShelvesAsync(choices, ct);
            if (!ReferenceEquals(client, _client)) return index;
            _grouping = (client, index, choices, DateTimeOffset.UtcNow);
            TellGroupedOnce(index, choices);
            return index;
        }
        catch (Exception ex) when (ex is OperationCanceledException || IsExpected(ex))
        {
            return _grouping is { } kept && ReferenceEquals(kept.Client, client) ? kept.Index : CollectionShelf.Index.None;
        }
    }
    /// <summary>
    /// The first time Mira groups sagas on its own (none chosen by this account): how many titles went where, once,
    /// with « Annuler », which leaves them as they were (each one « not grouped » for this account).
    /// </summary>
    private void TellGroupedOnce(CollectionShelf.Index index, Dictionary<string, CollectionShelf.Choice> choices)
    {
        if (_settings.CollectionsGroupedTold) return;
        var automatic = index.Shelves.Where(x => !choices.ContainsKey(x.Box.Id)).ToList();
        if (automatic.Count == 0) return;
        _settings.CollectionsGroupedTold = true; AutoSaveSettings();
        SetNotice(CollectionShelf.AutomaticNotice(automatic.Sum(x => x.Titles.Count), automatic.Count), L("Annuler", "Undo"), () => _ = UngroupAllAsync(automatic.Select(x => x.Box).ToList()), sticky: false);
    }
    private async Task UngroupAllAsync(List<MediaItem> boxes)
    {
        if (_client is not { } client) return;
        try { foreach (var box in boxes) await client.SetShelfChoiceAsync(box.Id, x => x with { Grouped = false }, _connectionLifetime.Token); }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); }
        ForgetGrouping(); await RefreshAsync(quiet: true);
    }
    /// <summary>Whether a collection's titles are one card in the library for this account (its choice, else its kind).</summary>
    private bool IsGrouped(MediaItem box)
    {
        if (_grouping is { } known)
        {
            if (known.Choices.TryGetValue(box.Id, out var choice) && choice.Grouped is { } chosen) return chosen;
            if (known.Index.Shelves.Any(x => x.Box.Id == box.Id)) return true;
        }
        return box.Shelf is not null || CollectionShelf.Automatic(box);
    }
    /// <summary>« Regrouper dans la bibliothèque » in a collection's menu, or « Ne plus regrouper »: this account's choice, kept on Jellyfin.</summary>
    private void AddShelfToggle(ItemsControl menu, MediaItem box)
    {
        if (_demo || _client is null) return;
        var grouped = IsGrouped(box);
        menu.Items.Add(MenuAction(grouped ? L("Ne plus regrouper dans la bibliothèque", "Stop grouping in the library") : L("Regrouper dans la bibliothèque", "Group in the library"),
            grouped ? "minus" : "library", () => SetGroupedAsync(box, !grouped, undoable: true)));
    }
    private async Task SetGroupedAsync(MediaItem box, bool grouped, bool undoable)
    {
        if (_client is not { } client) return;
        try { await client.SetShelfChoiceAsync(box.Id, x => x with { Grouped = grouped }, _connectionLifetime.Token); }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { SetError(Friendly(ex)); return; }
        ForgetGrouping();
        await RefreshAsync(quiet: true);
        var titles = _grouping?.Index.Shelves.FirstOrDefault(x => x.Box.Id == box.Id)?.Titles.Count ?? box.Shelf?.Count ?? box.ChildCount ?? 0;
        var said = grouped ? CollectionShelf.GroupedNotice(titles, box.DisplayTitle)
            : L($"Les titres de « {box.DisplayTitle} » sont de nouveau dans la bibliothèque.", $"The titles of “{box.DisplayTitle}” are back in the library.");
        if (undoable) SetNotice(said, L("Annuler", "Undo"), () => _ = SetGroupedAsync(box, !grouped, undoable: false), sticky: false);
        else SetNotice(said);
    }

    // —— The collection's page ——
    /// <summary>The title whose picture is at the top of the collection's page.</summary>
    private string? _shelfPicture;
    private async Task ShowShelfAsync(MediaItem box)
    {
        var version = ++_shelfVersion; ClosePreview();
        if (ShelfOverlay.Visibility != Visibility.Visible) _shelfOrigin = Keyboard.FocusedElement;
        // A title's page over it goes: the collection is what was asked for.
        if (DetailOverlay.Visibility == Visibility.Visible) { ++_detailVersion; _returnToDetail = null; _detailTrail.Clear(); _ = Motion.HideAsync(DetailOverlay); }
        AutoSaveSettings(); _ = Motion.HideAsync(SettingsOverlay); _ = Motion.HideAsync(GuideOverlay);
        _shelfBox = box; _shelfTitles = box.Shelf?.ToList() ?? []; _shelfOrder = null; _shelfTarget = null; _shelfEditing = false;
        SmoothScroll.Jump(ShelfScroll);
        // Never black while its picture comes: the BlurHash of the title it picks up at, then the picture over it.
        var lead = box.Shelf is { Count: > 0 } titles ? CollectionShelf.UpNext(titles) is int at ? titles[at] : titles[0] : null;
        ShelfTitle.Text = box.DisplayTitle; ShelfBackdrop.Source = lead is null ? null : DetailBlur(lead); _shelfPicture = null;
        if (ShelfBackdrop.Source is not null) Motion.Fade(ShelfBackdrop, 1, 0);
        ShowShelfOrderChoice(byRelease: false);
        ShowFavoriteOf(box);
        RenderShelf(loading: _shelfTitles.Count == 0);
        Motion.Reveal(ShelfOverlay, 300, 14); UpdateHeroClock();
        _ = Dispatcher.BeginInvoke(() => { if (version == _shelfVersion) ShelfPlay.Focus(); }, DispatcherPriority.Input);
        await LoadShelfAsync(box, version);
    }
    /// <summary>The collection's titles from Jellyfin (nested collections flattened), this account's order, then the page drawn again.</summary>
    private async Task LoadShelfAsync(MediaItem box, int version)
    {
        if (_demo || _client is not { } client) { if (version == _shelfVersion) RenderShelf(loading: false); return; }
        try
        {
            var ct = _connectionLifetime.Token;
            var titles = client.CollectionTitlesAsync(box.Id, ct);
            var choices = client.ShelfChoicesAsync(ct);
            var read = await titles;
            if (_store is { } store) await Task.Run(() => TryCache(() => store.ApplyLocalProgress(read)));
            var chosen = await choices;
            if (version != _shelfVersion) return;
            _shelfTitles = read; _shelfOrder = chosen.GetValueOrDefault(box.Id)?.Order;
            if (_grouping is { } known && ReferenceEquals(known.Client, client)) _grouping = known with { Choices = chosen };
            RenderShelf(loading: false);
            await LoadShelfTargetAsync(client, version);
        }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (version != _shelfVersion) return;
            // The titles of the card, if it had them, still show: only their order and progress may be older.
            if (_shelfTitles.Count > 0) { RenderShelf(loading: false); SetError(Friendly(ex)); }
            else ShowShelfState("warning", L("Jellyfin ne répond pas", "Jellyfin isn’t answering"), Friendly(ex), retry: true);
        }
    }
    /// <summary>After playback or a change in Mira: the titles' marks read again, the page staying where it is.</summary>
    private async Task RefreshShelfAsync()
    {
        if (_shelfBox is not { } box || ShelfOverlay.Visibility != Visibility.Visible) return;
        await LoadShelfAsync(box, ++_shelfVersion);
    }
    /// <summary>The titles in the watch order: this account's, the titles added since after it by release date.</summary>
    private List<MediaItem> ShelfOrdered() => CollectionShelf.InOrder(_shelfTitles, _shelfOrder);
    private void RenderShelf(bool loading)
    {
        var ordered = ShelfOrdered();
        var shown = _shelfByRelease && !_shelfEditing ? CollectionShelf.ByRelease(_shelfTitles) : ordered;
        var up = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : null;
        ShelfFacts.Text = _shelfTitles.Count > 0 ? CollectionShelf.Facts(_shelfTitles) : loading ? L("Chargement…", "Loading…") : "";
        ShelfHeading.Text = CollectionShelf.TitlesHeading(_shelfTitles.Count);
        ShelfHead.Visibility = _shelfTitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfOrderBox.Visibility = _shelfEditing || _shelfTitles.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
        ShelfOrderDone.Visibility = _shelfEditing ? Visibility.Visible : Visibility.Collapsed;
        ShowWatchedOf(_shelfTitles);
        RenderShelfSteps(ordered);
        ShowShelfPlayLabel(up, ordered);
        ShelfActions.Visibility = Visibility.Visible;
        ShelfCards.Children.Clear();
        if (loading)
        {
            ShelfState.Visibility = Visibility.Collapsed;
            ShelfCards.Children.Add(new TextBlock { Text = L("Chargement des titres…", "Loading titles…"), Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 10, 0, 10) });
            return;
        }
        if (_shelfTitles.Count == 0)
        {
            ShowShelfState("library", L("Rien dans cette collection", "Nothing in this collection yet"),
                L("Ajoute des films ou des séries depuis leur menu : « Ajouter à une collection… ».", "Add movies or shows from their menu: “Add to collection…”."), retry: false);
            return;
        }
        ShelfState.Visibility = Visibility.Collapsed;
        if (_shelfEditing) { RenderShelfEditing(ordered); return; }
        var (_, width) = PosterLayout(ShelfWidth());
        for (var i = 0; i < shown.Count; i++)
        {
            var title = shown[i];
            // Numbered in the watch order, whichever order the page shows.
            var place = ordered.IndexOf(title);
            var card = new MediaCard(title, false, i, _settings, _demo, CollectionShelf.Number(place), CollectionShelf.TitleLine(title), upNext: ReferenceEquals(title, up));
            card.Resize(width);
            card.Click += async (_, _) => await ShowDetailsAsync(title);
            card.HoverEntered += x => x.SetHover(true); card.FocusEntered += x => x.SetHover(true); card.HoverLeft += x => x.SetHover(false);
            var menu = CardMenu(card, false);
            // In its collection, a title can leave it.
            menu.Opened += (_, _) =>
            {
                if (_shelfBox is not { } box || !CollectionRights.Manage) return;
                menu.Items.Add(MenuAction(L("Retirer de la collection", "Remove from collection"), "minus", async () => { await PutInCollectionAsync(title, box.Id, box.Name, false); await RefreshShelfAsync(); }));
            };
            card.ContextMenu = menu;
            ShelfCards.Children.Add(card);
            _ = card.LoadImageAsync(_images, false);
        }
    }
    private double ShelfWidth() => ShelfCards.ActualWidth > 200 ? ShelfCards.ActualWidth : Math.Max(600, ShelfScroll.ActualWidth - 160);
    private void ShelfCards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _shelfEditing) return;
        var (_, width) = PosterLayout(ShelfWidth());
        foreach (var card in ShelfCards.Children.OfType<MediaCard>()) card.Resize(width);
        foreach (var row in ShelfCards.Children.OfType<FrameworkElement>().Where(x => x is not MediaCard)) row.Width = ShelfCards.ActualWidth;
    }
    /// <summary>One notch per title in the watch order: full once watched, partly while under way.</summary>
    private void RenderShelfSteps(List<MediaItem> ordered)
    {
        ShelfSteps.Children.Clear();
        ShelfSteps.Visibility = ordered.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var title in ordered)
        {
            var share = CollectionShelf.Progress(title);
            var notch = new Grid { Margin = new Thickness(0, 0, 3, 0), ToolTip = title.DisplayTitle };
            notch.Children.Add(new Border { Background = Palette.Get("LineStrong"), CornerRadius = new CornerRadius(2) });
            if (share > 0) notch.Children.Add(new Border { Background = Palette.Get("Text"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Tag = share });
            notch.SizeChanged += (_, e) => { foreach (var fill in notch.Children.OfType<Border>().Where(x => x.Tag is double)) fill.Width = e.NewSize.Width * (double)fill.Tag; };
            ShelfSteps.Children.Add(notch);
        }
        AutomationProperties.SetHelpText(ShelfSteps, L($"{ordered.Count(CollectionShelf.Watched)} sur {ordered.Count} vus", $"{ordered.Count(CollectionShelf.Watched)} of {ordered.Count} watched"));
    }
    /// <summary>« Reprendre · Nisemonogatari · S01 E04 », « Regarder · … », or « Revoir » from the first title once all are watched.</summary>
    private void ShowShelfPlayLabel(MediaItem? up, List<MediaItem> ordered)
    {
        ShelfPlay.IsEnabled = ordered.Count > 0;
        if (ordered.Count == 0) { ShelfPlay.Content = ActionLabel("play", L("Regarder", "Watch")); return; }
        var title = up ?? ordered[0];
        var episode = _shelfTarget is { Type: "Episode" } next && next.SeriesId == title.Id ? next : null;
        var playing = episode ?? (title.Type == "Movie" ? title : null);
        var resume = playing is not null && playing.Resumable && _settings.RememberPosition;
        var verb = up is null ? L("Revoir", "Watch again") : resume ? L("Reprendre", "Resume") : L("Regarder", "Watch");
        var code = episode is not null ? $"  ·  S{episode.ParentIndexNumber ?? 1:00} E{episode.IndexNumber ?? 1:00}" : "";
        ShelfPlay.Content = ActionLabel("play", $"{verb}  ·  {title.DisplayTitle}{code}", resume ? MediaQuality.Left(playing!) : null);
        AutomationProperties.SetName(ShelfPlay, $"{verb} {title.DisplayTitle}");
    }
    /// <summary>The episode a series of the collection picks up at (Jellyfin's next up), and the picture of the title to resume at the top.</summary>
    private async Task LoadShelfTargetAsync(JellyfinClient client, int version)
    {
        var ordered = ShelfOrdered();
        var title = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : ordered.FirstOrDefault();
        if (title is null) return;
        if (_images is { } cache)
        {
            // The same picture as before (the page read again after Vu, after playing): left as it is, no fade from black.
            if (_shelfPicture != title.Id)
            {
                var picture = await cache.PageBackdropAsync(title);
                if (version != _shelfVersion) return;
                if (picture is not null) { ShelfBackdrop.Source = picture; _shelfPicture = title.Id; Motion.Fade(ShelfBackdrop, 1, 420, 0, Motion.Soft); }
            }
        }
        if (title.Type != "Series") { _shelfTarget = title; return; }
        var next = await NextEpisodeAsync(client, title.Id);
        if (version != _shelfVersion) return;
        _shelfTarget = next;
        ShowShelfPlayLabel(CollectionShelf.UpNext(ordered) is int i ? ordered[i] : null, ordered);
    }
    private async void ShelfPlay_Click(object sender, RoutedEventArgs e)
    {
        var ordered = ShelfOrdered();
        if (ordered.Count == 0) return;
        var up = CollectionShelf.UpNext(ordered) is int at ? ordered[at] : null;
        var title = up ?? ordered[0];
        if (title.Type == "Movie") { await PlayAsync(title, fromStart: up is null, chosen: true); return; }
        // A series: the episode it picks up at; none known (all watched, or Jellyfin silent), its page, where Lecture says what it plays.
        if (up is not null && _shelfTarget is { Type: "Episode" } episode && episode.SeriesId == title.Id) { await PlayAsync(episode, chosen: true); return; }
        await ShowDetailsAsync(title);
    }
    private void ShowFavoriteOf(MediaItem box) => ShowState(ShelfFavorite, "favorite", box.UserData.IsFavorite, box.UserData.IsFavorite ? L("Favori", "Favorite") : L("Ajouter aux favoris", "Add to favorites"),
        box.UserData.IsFavorite ? L("Dans tes favoris. Clique pour la retirer", "In your favorites. Click to remove it") : L("Ajouter aux favoris", "Add to favorites"));
    private void ShowWatchedOf(List<MediaItem> titles)
    {
        var all = titles.Count > 0 && titles.All(CollectionShelf.Watched);
        ShowState(ShelfWatched, "watched", all, all ? L("Vue", "Watched") : L("Marquer la collection comme vue", "Mark collection as watched"),
            all ? L("Toute la collection est vue. Clique pour la marquer comme non vue", "The whole collection is watched. Click to mark it as unwatched") : L("Marquer chaque titre comme vu", "Mark every title as watched"));
    }
    private async void ShelfFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfBox is not { } box) return;
        // Its glyph at the click (as a title's page); a refusal puts it back.
        ShowState(ShelfFavorite, "favorite", !box.UserData.IsFavorite, "", "");
        await ToggleFavoriteAsync(box);
        ShowFavoriteOf(box); RenderLibrary();
    }
    private async void ShelfWatched_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfBox is not { } box || _shelfTitles.Count == 0) return;
        var played = !_shelfTitles.All(CollectionShelf.Watched);
        ShowState(ShelfWatched, "watched", played, "", "");
        // One request for all its titles: Jellyfin marks each one of a collection (UserPlayedItems on a BoxSet, checked on 12.1).
        // Done, SetPlayedAsync reads the page again itself (RenderAfterUserDataChange): once, not twice.
        if (!await SetPlayedAsync(box, played)) ShowWatchedOf(_shelfTitles);
    }
    private void ShelfMore_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfBox is not { } box) return;
        var menu = new ContextMenu { PlacementTarget = ShelfMore, Placement = PlacementMode.Bottom };
        AddShelfToggle(menu, box);
        if (!_demo && _client is not null && _shelfTitles.Count > 1)
            menu.Items.Add(MenuAction(L("Changer l’ordre de visionnage…", "Change the watch order…"), "sliders", () => { StartShelfEditing(); return Task.CompletedTask; }));
        if (_shelfOrder is not null && !_demo && _client is not null)
            menu.Items.Add(MenuAction(L("Revenir à l’ordre de sortie", "Back to release order"), "refresh", () => SaveShelfOrderAsync(null)));
        AddCollectionActions(menu, box);
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    // —— The watch order: chosen once, by this account, kept on Jellyfin beside its other choices ——
    private void ShowShelfOrderChoice(bool byRelease)
    {
        _shelfOrderBusy = true; _shelfByRelease = byRelease;
        (byRelease ? ShelfReleaseOrder : ShelfWatchOrder).IsChecked = true; ShelfOrder.Selected = byRelease ? 1 : 0;
        _shelfOrderBusy = false;
    }
    private void ShelfOrder_Checked(object sender, RoutedEventArgs e)
    {
        if (_shelfOrderBusy || _shelfBox is null) return;
        _shelfByRelease = ReferenceEquals(sender, ShelfReleaseOrder);
        RenderShelf(loading: false); Motion.Reveal(ShelfCards, 220, 7);
    }
    private void StartShelfEditing()
    {
        _shelfEditing = true; ShowShelfOrderChoice(byRelease: false);
        RenderShelf(loading: false);
        ShelfHead.BringIntoView();
    }
    private async void ShelfOrderDone_Click(object sender, RoutedEventArgs e)
    {
        _shelfEditing = false;
        await SaveShelfOrderAsync(ShelfOrdered().Select(x => x.Id).ToList());
    }
    /// <summary>The order kept for this account (null: by release date again), then the page and the library drawn again.</summary>
    private async Task SaveShelfOrderAsync(IReadOnlyList<string>? order)
    {
        if (_shelfBox is not { } box || _client is not { } client) return;
        var was = _shelfOrder; _shelfOrder = order; _shelfEditing = false;
        RenderShelf(loading: false);
        try { await client.SetShelfChoiceAsync(box.Id, x => x with { Order = order }, _connectionLifetime.Token); }
        catch (OperationCanceledException) when (_connectionLifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { _shelfOrder = was; RenderShelf(loading: false); SetError(Friendly(ex)); return; }
        ForgetGrouping();
        await LoadShelfTargetAsync(client, _shelfVersion);
    }
    /// <summary>The titles as rows while their order changes: number, name, year, and a step up or down.</summary>
    private void RenderShelfEditing(List<MediaItem> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            var place = i; var title = ordered[i];
            var row = new DockPanel { Width = ShelfWidth(), Margin = new Thickness(0, 0, 0, 6) };
            var up = ShelfStep("up", L($"Monter « {title.DisplayTitle} »", $"Move “{title.DisplayTitle}” up"), place > 0, () => MoveShelfTitle(place, -1));
            var down = ShelfStep("down", L($"Descendre « {title.DisplayTitle} »", $"Move “{title.DisplayTitle}” down"), place < ordered.Count - 1, () => MoveShelfTitle(place, 1));
            DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right);
            row.Children.Add(down); row.Children.Add(up);
            var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = CollectionShelf.Number(place), FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Palette.Get("Muted"), Width = 40 });
            text.Children.Add(new TextBlock { Text = title.DisplayTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = "   " + CollectionShelf.TitleLine(title), FontSize = 13, Foreground = Palette.Get("Muted") });
            row.Children.Add(new Squircle { Background = Palette.Get("Surface"), BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(1), CornerRadius = Palette.Control, Padding = new Thickness(16, 10, 10, 10), Child = text });
            ShelfCards.Children.Add(row);
        }
    }
    private Button ShelfStep(string icon, string name, bool enabled, Action move)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Content = new Icon { Kind = icon, Width = 16, Height = 16 }, IsEnabled = enabled, Margin = new Thickness(8, 0, 0, 0), ToolTip = name, Tag = icon };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => move();
        return button;
    }
    private void MoveShelfTitle(int index, int step)
    {
        _shelfOrder = CollectionShelf.Move(ShelfOrdered(), index, step);
        RenderShelf(loading: false);
        // The keyboard follows the title it moved.
        var at = Math.Clamp(index + step, 0, ShelfCards.Children.Count - 1);
        if (ShelfCards.Children[at] is DockPanel row && row.Children.OfType<Button>().FirstOrDefault(x => x.IsEnabled && (string?)x.Tag == (step < 0 ? "up" : "down")) is { } again) again.Focus();
    }

    private void ShowShelfState(string icon, string title, string hint, bool retry)
    {
        ShelfCards.Children.Clear(); ShelfHead.Visibility = Visibility.Collapsed; ShelfSteps.Visibility = Visibility.Collapsed;
        ShelfStateIcon.Kind = icon; ShelfStateTitle.Text = title; ShelfStateHint.Text = hint;
        ShelfStateRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        ShelfState.Visibility = Visibility.Visible;
        if (_shelfTitles.Count == 0 && retry) { ShelfFacts.Text = ""; ShelfActions.Visibility = Visibility.Collapsed; }
    }
    private async void ShelfRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfBox is not { } box) return;
        var version = ++_shelfVersion; RenderShelf(loading: true);
        await LoadShelfAsync(box, version);
    }
    private async void CloseShelf_Click(object sender, RoutedEventArgs e) => await CloseShelfAsync();
    private async Task CloseShelfAsync()
    {
        ++_shelfVersion; _returnToShelf = false;
        if (_shelfEditing) { _shelfEditing = false; }
        await Motion.HideDrawnAsync(ShelfOverlay, 170, () => UncoverLibrary(ShelfOverlay)); UpdateHeroClock();
        _shelfBox = null;
        if (_shelfOrigin is UIElement { IsVisible: true } origin) origin.Focus();
        _shelfOrigin = null;
    }
}
