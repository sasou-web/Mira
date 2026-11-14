using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

// « Télécharger les manquants » (mock-up A chosen by ogsas on 2026-11-13, MissingDownloads of Core): « Get E6–9 » in the tile
// of the episodes the library lacks, « Get season 2 » on the card of a season it has none of. A click searches on the PC of
// Jellyfin (the relay's searches, as the Downloads page), a menu beside the button puts forward a release for each episode
// (or a season pack), and what goes shows its progress in the tile and the season's pill until the episodes take their place.
// For an administrator, when the relay's agent places what a series' page asks (RelayAgent.Seasons).
public partial class MainWindow
{
    private RelayStatus? _missingStatus;
    private string? _missingFor;
    private bool _missingOffered;
    // The tiles, the card and the pills that show what was asked, updated in place at each look at the relay.
    private readonly List<(int Season, IReadOnlyList<int>? Episodes, bool Pill, Action<MissingProgress?> Show)> _missingViews = [];
    private DispatcherTimer? _missingTimer;
    private Popup? _missingPopup;
    private CancellationTokenSource? _missingSearch;
    // The requests already seen placed: each looks at the series' episodes at once.
    private readonly HashSet<string> _missingPlaced = [];
    // While a tile says « Adding to your library »: when the series' episodes were last read, since when it waits for Jellyfin.
    private DateTime _missingLookedAt;
    private DateTimeOffset? _missingWaitingSince;
    private DateTime _missingScannedAt;

    /// <summary>Whether this series' page offers the buttons: an administrator, and a Mira on the PC of Jellyfin that places them.</summary>
    private async Task ShowMissingDownloadsAsync(MediaItem item)
    {
        if (_missingFor != item.Id) { _missingFor = item.Id; _missingOffered = false; _missingStatus = null; _missingPlaced.Clear(); _missingWaitingSince = null; StopMissingTimer(); }
        if (_demo || item.Type != "Series" || _client is not { } client) return;
        RelayStatus status;
        try
        {
            if (!await IsAdministratorAsync(client)) return;
            status = await client.TorLinkStatusAsync(_connectionLifetime.Token);
        }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (_detail?.Id != item.Id || !ReferenceEquals(client, _client)) return;
        var offered = status.Agent is { Seasons: true };
        _missingStatus = status;
        // Placed before the page opened: its episodes were just read.
        foreach (var placed in status.Missing ?? []) if (IsPlaced(placed)) _missingPlaced.Add(placed.Id);
        _missingLookedAt = DateTime.UtcNow;
        if (offered != _missingOffered) { _missingOffered = offered; if (_episodesRead) { FillSeasons(); RenderEpisodes(); } }
        else UpdateMissingViews();
        WatchMissing();
    }

    private bool IsPlaced(RelayMissing x) =>
        _missingFor is { } id && _missingStatus is { } status && MissingDownloads.Progress(status with { Missing = [x] }, id, x.Season, null) is { Stage: MissingStage.Placed };

    private void StopMissingTimer() { _missingTimer?.Stop(); _missingTimer = null; }

    /// <summary>Something asked from this page is on its way: TorLink has it, or Mira places it.</summary>
    private bool MissingUnderWay() => _missingFor is { } id && _missingStatus is { } status
        && status.Missing?.Any(x => MissingDownloads.Progress(status with { Missing = [x] }, id, x.Season, null) is { Stage: MissingStage.Waiting or MissingStage.Downloading }) == true;

    /// <summary>A tile or a card of this page says « Adding to your library »: what it shows comes once Jellyfin lists it.</summary>
    private bool MissingAwaitsJellyfin() => _missingFor is { } id
        && _missingViews.Any(v => !v.Pill && MissingDownloads.Progress(_missingStatus, id, v.Season, v.Episodes) is { Stage: MissingStage.Adding or MissingStage.Placed });

    /// <summary>
    /// Looks at the relay every 3 s while something asked from this page downloads, and every 10 s while a tile waits for
    /// Jellyfin (the series' episodes read again now and then: <see cref="MissingDownloads.LookAgain"/>), until the episodes take
    /// their place.
    /// </summary>
    private void WatchMissing()
    {
        var underWay = MissingUnderWay(); var awaits = MissingAwaitsJellyfin();
        if (!awaits) _missingWaitingSince = null;
        if (!_missingOffered || !underWay && !awaits) { StopMissingTimer(); return; }
        var every = TimeSpan.FromSeconds(underWay ? 3 : 10);
        if (_missingTimer is not null) { if (_missingTimer.Interval != every) _missingTimer.Interval = every; return; }
        _missingTimer = new DispatcherTimer(every, DispatcherPriority.Background, async (_, _) => await LookAtMissingAsync(), Dispatcher);
        _missingTimer.Start();
    }

    private bool _missingLooking;

    private async Task LookAtMissingAsync()
    {
        if (_client is not { } client || _detail is not { Type: "Series" } series || series.Id != _missingFor || DetailOverlay.Visibility != Visibility.Visible) { StopMissingTimer(); return; }
        if (_missingLooking) return;
        _missingLooking = true;
        try
        {
            try { _missingStatus = await client.TorLinkStatusAsync(_connectionLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { return; }
            if (_detail?.Id != series.Id) return;
            UpdateMissingViews();
            // Newly placed: the series' episodes read at once; then, while a tile still waits, now and then (Jellyfin lists a
            // placed file about a minute later, after its folder monitor's delay).
            var placed = (_missingStatus.Missing ?? []).Where(x => !_missingPlaced.Contains(x.Id) && IsPlaced(x)).ToList();
            foreach (var x in placed) _missingPlaced.Add(x.Id);
            if (MissingAwaitsJellyfin())
            {
                var now = DateTimeOffset.UtcNow;
                var since = _missingWaitingSince ??= now;
                if (placed.Count > 0 || DateTime.UtcNow - _missingLookedAt >= MissingDownloads.LookAgain(now - since)) await LookForPlacedEpisodesAsync(client, series);
            }
        }
        finally { _missingLooking = false; }
        if (_detail?.Id == series.Id) WatchMissing();
    }

    /// <summary>The series' episodes read again; when Jellyfin lists new ones, the page shows them in place of the hollow tiles.</summary>
    private async Task LookForPlacedEpisodesAsync(JellyfinClient client, MediaItem series)
    {
        _missingLookedAt = DateTime.UtcNow;
        ItemsResult now;
        try { now = await client.EpisodesAsync(series.Id, _connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (_detail?.Id != series.Id || !MissingDownloads.Arrived(_episodes, now.Items)) return;
        await ReloadSeriesEpisodesAsync(series);
    }

    private async Task ReloadSeriesEpisodesAsync(MediaItem series)
    {
        _metadata?.Clear(); _virtualEpisodes = null;
        var version = _detailVersion;
        await LoadDetailEpisodesAsync(series, version);
        await LoadCalendarAsync(series, version);
    }

    private void UpdateMissingViews()
    {
        if (_missingFor is not { } id) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (season, episodes, _, show) in _missingViews.ToList()) show(MissingDownloads.Progress(_missingStatus, id, season, episodes, now));
    }

    // ---- In the tiles, the card and the pills -------------------------------------------------------------------------

    /// <summary>
    /// The part of a tile of missing episodes that asks for them: « Get E6–9 », or how what was asked is going; and the bar
    /// along the tile's foot. Null when the page doesn't offer it.
    /// </summary>
    private (FrameworkElement Part, FrameworkElement Bar)? MissingTilePart(ShelfLine line)
    {
        if (!_missingOffered || line.Kind != ShelfKind.Missing || _detail is not { } series || line.Episodes.Count > MissingDownloads.MaxEpisodes) return null;
        var numbers = line.Episodes.Select(x => x.IndexNumber).OfType<int>().ToList();
        var season = line.First.ParentIndexNumber ?? 1;
        if (numbers.Count == 0) return null;
        var ask = new MissingAsk(season, numbers, false); var heading = MissingDownloads.Code(numbers.Min(), numbers.Max());
        var part = new ContentControl { Margin = new Thickness(0, 10, 0, 0), Focusable = false };
        var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush("#38FFFFFF"), Visibility = Visibility.Collapsed };
        bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.Children.Add(new Border { Background = Palette.Get("Text") });
        var get = MissingButton(MissingDownloads.GetEpisodes(numbers.Min(), numbers.Max()), small: true);
        get.Click += (_, e) => { e.Handled = true; OpenMissingMenu(get, series, ask, heading); };
        void Show(MissingProgress? progress)
        {
            if (progress is null) { part.Content = get; bar.Visibility = Visibility.Collapsed; return; }
            part.Content = MissingProgressView(progress, again => OpenMissingMenu(again, series, ask, heading));
            var share = progress.Stage is MissingStage.Downloading or MissingStage.Waiting ? Math.Clamp(progress.Percent / 100.0, 0, 1) : 1;
            bar.ColumnDefinitions[0].Width = new GridLength(share, GridUnitType.Star); bar.ColumnDefinitions[1].Width = new GridLength(1 - share, GridUnitType.Star);
            bar.Visibility = progress.Stage == MissingStage.Failed ? Visibility.Collapsed : Visibility.Visible;
        }
        _missingViews.Add((season, numbers, false, Show));
        Show(MissingDownloads.Progress(_missingStatus, series.Id, season, numbers, DateTimeOffset.UtcNow));
        return (part, bar);
    }

    /// <summary>« Get season 2 » on the card of a season the library has none of, or how what was asked is going.</summary>
    private FrameworkElement? MissingSeasonPart(int season, IReadOnlyList<MediaItem> run)
    {
        if (!_missingOffered || _detail is not { } series) return null;
        var numbers = run.Select(x => x.IndexNumber).OfType<int>().Distinct().Order().ToList();
        var part = new ContentControl { Focusable = false, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 0, 0), MaxWidth = 300 };
        var ask = new MissingAsk(season, numbers, true); var heading = L($"Saison {season}", $"Season {season}");
        var get = MissingButton(MissingDownloads.GetSeason(season), small: false);
        get.Click += (_, _) => OpenMissingMenu(get, series, ask, heading);
        void Show(MissingProgress? progress) => part.Content = progress is null ? get : MissingProgressView(progress, again => OpenMissingMenu(again, series, ask, heading), bar: true);
        _missingViews.Add((season, null, false, Show));
        Show(MissingDownloads.Progress(_missingStatus, series.Id, season, null, DateTimeOffset.UtcNow));
        return part;
    }

    /// <summary>What a season's pill adds while its episodes come: « 38 % ».</summary>
    private TextBlock? MissingPillNote(EpisodePage page)
    {
        if (!_missingOffered || _detail is not { } series) return null;
        var note = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.62 };
        void Show(MissingProgress? progress)
        {
            var text = progress is null ? "" : MissingDownloads.Pill(progress);
            note.Text = text; note.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        _missingViews.Add((page.Season, null, true, Show));
        Show(MissingDownloads.Progress(_missingStatus, series.Id, page.Season, null));
        return note;
    }

    private Button MissingButton(string text, bool small)
    {
        var button = new Button { Style = (Style)FindResource(small ? "Quiet" : "Primary"), HorizontalAlignment = HorizontalAlignment.Left, Padding = small ? new Thickness(12, 6, 13, 6) : new Thickness(16, 9, 18, 9) };
        if (small) { button.BorderBrush = Palette.Get("Line"); button.BorderThickness = new Thickness(1); }
        Motion.SetRadius(button, new CornerRadius(small ? 15 : 18));
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new Icon { Kind = "download", Width = small ? 13 : 15, Height = small ? 13 : 15, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = text, FontSize = small ? 12.5 : 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        button.Content = label;
        AutomationProperties.SetName(button, text); AutomationProperties.SetHelpText(button, MissingDownloads.Privacy);
        return button;
    }

    /// <summary>« Downloading on your Jellyfin PC · 38% · about 12 min left », « The download failed » with « Try again ».</summary>
    private FrameworkElement MissingProgressView(MissingProgress progress, Action<Button> retry, bool bar = false)
    {
        var panel = new StackPanel();
        var failed = progress.Stage == MissingStage.Failed;
        panel.Children.Add(new TextBlock { Text = progress.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get(failed ? "Danger" : "TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (progress.Line.Length > 0) panel.Children.Add(new TextBlock { Text = progress.Line, FontSize = 12, Foreground = Palette.Get("Muted"), TextWrapping = failed ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) });
        if (bar && progress.Stage is MissingStage.Downloading or MissingStage.Waiting)
        {
            var track = new Grid { Height = 3, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#38FFFFFF"), Margin = new Thickness(0, 8, 0, 0) };
            var share = Math.Clamp(progress.Percent / 100.0, 0, 1);
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share, GridUnitType.Star) });
            track.Children.Add(new Border { Background = Palette.Get("Text") });
            panel.Children.Add(track);
        }
        if (progress.Rescan)
        {
            // Jellyfin didn't see it after its monitor's delay: a scan of the libraries, as its dashboard does.
            if (DateTime.UtcNow - _missingScannedAt < TimeSpan.FromMinutes(5))
                panel.Children.Add(new TextBlock { Text = MissingDownloads.Rescanning, FontSize = 12, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            else
            {
                var scan = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.Rescan, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(-10, 4, 0, 0), FontSize = 12.5 };
                scan.Click += async (_, e) => { e.Handled = true; await ScanForMissingAsync(scan); };
                panel.Children.Add(scan);
            }
        }
        if (failed)
        {
            var again = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(-10, 4, 0, 0), FontSize = 12.5 };
            again.Click += (_, e) => { e.Handled = true; retry(again); };
            panel.Children.Add(again);
        }
        AutomationProperties.SetName(panel, $"{progress.Title}. {progress.Line}");
        AutomationProperties.SetLiveSetting(panel, AutomationLiveSetting.Polite);
        return panel;
    }

    /// <summary>« Scan libraries »: Jellyfin looks through its folders; the page reads the series' episodes often again meanwhile.</summary>
    private async Task ScanForMissingAsync(Button scan)
    {
        if (_client is not { } client) return;
        scan.IsEnabled = false;
        try
        {
            if (!await client.ScanLibrariesAsync(_connectionLifetime.Token)) { SetNotice(L("Seul un administrateur de Jellyfin peut analyser les médiathèques.", "Only a Jellyfin administrator can scan the libraries.")); scan.IsEnabled = true; return; }
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); scan.IsEnabled = true; return; }
        _missingScannedAt = DateTime.UtcNow; _missingWaitingSince = DateTimeOffset.UtcNow;
        UpdateMissingViews(); WatchMissing();
    }

    // ---- The menu ------------------------------------------------------------------------------------------------------

    private sealed class MissingMenu
    {
        public required MediaItem Series { get; init; }
        public required MissingAsk Ask { get; init; }
        public required string Heading { get; init; }
        public required Squircle Body { get; init; }
        public string? Query { get; set; }
        public MissingPicks? Picks { get; set; }
        // What goes: one release per episode (null: unchecked), or the pack chosen.
        public Dictionary<int, MissingOption?> Chosen { get; } = [];
        public MissingOption? Pack { get; set; }
        public bool Sending { get; set; }
        public string? Error { get; set; }
        // The foot, updated in place when a check or a release changes (the rest of the menu, and the focus, stay).
        public TextBlock? Total { get; set; }
        public Button? Go { get; set; }
        // What the menu showed last: its focus moves in only when that changes (searching, then results).
        public string? Shown { get; set; }
    }

    private void OpenMissingMenu(Button anchor, MediaItem series, MissingAsk ask, string heading)
    {
        _missingSearch?.Cancel();
        if (_missingPopup is { IsOpen: true }) _missingPopup.IsOpen = false;
        var body = new Squircle
        {
            Background = Palette.Get("Menu"), BorderBrush = Palette.Get("Hairline"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Width = MissingMenuWidth,
        };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var menu = new MissingMenu { Series = series, Ask = ask, Heading = heading, Body = body };
        // Kept inside Mira's window (a Popup is a window of its own, which WPF only keeps on the screen): below the button or
        // above it, narrower in a narrow window, its list scrolling in a short one (MenuFit of Core).
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Custom, StaysOpen = false, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (size, _, _) => [new CustomPopupPlacement(MissingMenuPlace(anchor, size), PopupPrimaryAxis.None)];
        FitMissingMenu(anchor, body);
        // The window resized or the page scrolled under it: the menu follows its button, still inside the window.
        void Refit(object? sender, EventArgs e) { if (!popup.IsOpen) return; FitMissingMenu(anchor, body); NudgeMissingMenu(popup); }
        ScrollChangedEventHandler scrolled = Refit;
        SizeChanged += Refit;
        AddHandler(ScrollViewer.ScrollChangedEvent, scrolled);
        popup.Closed += (_, _) =>
        {
            SizeChanged -= Refit;
            RemoveHandler(ScrollViewer.ScrollChangedEvent, scrolled);
            if (ReferenceEquals(_missingPopup, popup)) { _missingSearch?.Cancel(); _missingPopup = null; }
        };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; anchor.Focus(); } };
        _missingPopup = popup;
        popup.IsOpen = true;
        _ = SearchMissingAsync(menu, popup);
    }

    private const double MissingMenuWidth = 440;
    /// <summary>The menu's width and tallest height, from where its button is in the window (title bar excluded).</summary>
    private void FitMissingMenu(FrameworkElement anchor, Squircle body)
    {
        if (MissingMenuArea(anchor) is not { } area) return;
        var bounds = MenuFit.Size(area.Width, area.Height, area.Anchor, MissingMenuWidth);
        body.Width = bounds.Width; body.MaxHeight = bounds.MaxHeight;
    }
    /// <summary>Where WPF puts the menu: relative to its button's top-left corner, in device pixels.</summary>
    private Point MissingMenuPlace(FrameworkElement anchor, Size size)
    {
        var dpi = VisualTreeHelper.GetDpi(anchor);
        if (MissingMenuArea(anchor) is not { } area) return new Point(0, (anchor.ActualHeight + MenuFit.Gap) * dpi.DpiScaleY);
        var at = MenuFit.At(area.Width, area.Height, area.Anchor, size.Width / dpi.DpiScaleX, size.Height / dpi.DpiScaleY);
        return new Point((at.X - area.Anchor.Left) * dpi.DpiScaleX, (at.Y - area.Anchor.Top) * dpi.DpiScaleY);
    }
    /// <summary>The window below its title bar and the button in it, in device-independent pixels (screen positions: the
    /// pages' zoom of the couch mode and the text size are in them).</summary>
    private (double Width, double Height, MenuFit.Box Anchor)? MissingMenuArea(FrameworkElement anchor)
    {
        if (Content is not FrameworkElement root || !anchor.IsVisible || PresentationSource.FromVisual(anchor) is null || PresentationSource.FromVisual(root) is null) return null;
        var dpi = VisualTreeHelper.GetDpi(root);
        var origin = root.PointToScreen(new Point(0, TitleHeight)); var end = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
        var from = anchor.PointToScreen(new Point(0, 0)); var to = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        double X(double x) => (x - origin.X) / dpi.DpiScaleX; double Y(double y) => (y - origin.Y) / dpi.DpiScaleY;
        return (X(end.X), Y(end.Y), new MenuFit.Box(X(from.X), Y(from.Y), X(to.X) - X(from.X), Y(to.Y) - Y(from.Y)));
    }
    /// <summary>WPF places a popup again when an offset changes.</summary>
    private static void NudgeMissingMenu(Popup popup) => popup.HorizontalOffset = popup.HorizontalOffset == 0 ? .01 : 0;

    private async Task SearchMissingAsync(MissingMenu menu, Popup popup)
    {
        if (_client is not { } client) return;
        _missingSearch?.Cancel();
        var cts = _missingSearch = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token);
        var ct = cts.Token;
        menu.Picks = null; menu.Error = null; menu.Chosen.Clear(); menu.Pack = null;
        DrawMissingMenu(menu, popup);
        var series = menu.Series; var season = menu.Ask.Season;
        var known = new MissingSeries(series.Name, series.OriginalTitle, series.ProductionYear, MissingDownloads.Before(_episodes.Concat(_seriesVirtuals), season), menu.Query);
        try
        {
            bool anime;
            try { anime = await MissingDownloads.IsAnimeAsync(client, series, ct); }
            catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested) { anime = false; }
            var hits = new List<(string Search, RelayHit Hit)>();
            string? failure = null;
            foreach (var query in MissingDownloads.Queries(known, season))
            {
                menu.Query ??= query;
                var (found, error) = await MissingDownloads.SearchAsync(client, query, MissingDownloads.Categories(anime), ct);
                hits.AddRange(found); failure ??= error;
                var picks = MissingDownloads.Choose(menu.Ask, known, hits);
                // Everything found already (or a season's pack): the second, wider search would only add noise.
                if (picks.NotFound.Count == 0 || picks.Packs.Count > 0) break;
            }
            if (ct.IsCancellationRequested) return;
            menu.Picks = MissingDownloads.Choose(menu.Ask, known, hits);
            if (menu.Picks.Empty && failure is not null) menu.Error = failure;
            menu.Pack = menu.Picks.Packs.FirstOrDefault();
            foreach (var choice in menu.Picks.Episodes) menu.Chosen[choice.Episode] = choice.Best;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { menu.Error = ServerError(ex); }
        if (popup.IsOpen) DrawMissingMenu(menu, popup);
    }

    private void DrawMissingMenu(MissingMenu menu, Popup popup)
    {
        // Heading on top, foot below, what is between scrolling when the window leaves the menu too little height.
        var stack = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        var middle = new StackPanel();
        var foot = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(6), Content = new Icon { Kind = "close", Width = 13, Height = 13 } };
        AutomationProperties.SetName(close, L("Fermer", "Close")); close.Click += (_, _) => popup.IsOpen = false;
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = menu.Heading, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);
        var picks = menu.Picks;
        // Inside the scrolled part, « Retry » and the rows reach 12 px past the text on each side (their hover).
        var scrolled = middle;
        var listMargin = new Thickness(-12, 0, -12, 0);
        middle.Margin = new Thickness(12, 0, 12, 0);
        if (menu.Error is { } error && picks is null or { Empty: true })
        {
            middle.Children.Add(new TextBlock { Text = error, Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 });
            var again = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-12, 8, 0, 0) };
            again.Click += (_, _) => _ = SearchMissingAsync(menu, popup);
            middle.Children.Add(again);
        }
        else if (picks is null)
        {
            middle.Children.Add(new TextBlock { Text = MissingDownloads.Searching, FontSize = 13, Foreground = Palette.Get("TextSecondary") });
            for (var i = 0; i < 3; i++)
            {
                var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Opacity = 1 - i * 0.25 };
                row.Children.Add(new Border { Height = 11, Width = 240 - i * 30, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Palette.Get("Raised2") });
                row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Palette.Get("Raised2"), Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
                middle.Children.Add(row);
            }
            AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Polite);
        }
        else if (picks.Empty) middle.Children.Add(MissingNothing(menu, popup));
        else
        {
            var list = new StackPanel();
            if (menu.Pack is not null) list.Children.Add(MissingPackRow(menu, popup));
            else foreach (var choice in picks.Episodes) list.Children.Add(MissingEpisodeRow(menu, popup, choice));
            scrolled = list; listMargin = new Thickness(-10, 0, -10, 0); middle.Margin = new Thickness(0);
            if (menu.Pack is null && MissingDownloads.NotFound(picks.NotFound) is { Length: > 0 } nothing)
                foot.Children.Add(new TextBlock { Text = nothing, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            foot.Children.Add(new Border { Height = 1, Background = Palette.Get("Wash"), Margin = new Thickness(0, 12, 0, 12) });
            var bar = new DockPanel();
            var go = menu.Go = new Button { Style = (Style)FindResource("Primary"), Padding = new Thickness(18, 8, 18, 8) };
            Motion.SetRadius(go, new CornerRadius(17));
            go.Click += async (_, _) => await SendMissingAsync(menu, popup);
            DockPanel.SetDock(go, Dock.Right); bar.Children.Add(go);
            bar.Children.Add(menu.Total = new TextBlock { FontSize = 12.5, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            foot.Children.Add(bar);
            DrawMissingFoot(menu);
            if (menu.Error is { } refused) foot.Children.Add(new TextBlock { Text = refused, Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) });
        }
        foot.Children.Add(new TextBlock { Text = MissingDownloads.Privacy, FontSize = 11.5, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        // Never taller than its list (380): beyond that, the list scrolls even in a tall window, as before.
        var scroller = new ScrollViewer { Content = scrolled, MaxHeight = ReferenceEquals(scrolled, middle) ? double.PositiveInfinity : 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = listMargin, Focusable = false };
        Grid.SetRow(scroller, 1); Grid.SetRow(foot, 2);
        stack.Children.Add(scroller); stack.Children.Add(foot);
        var shown = menu.Error is not null && picks is null or { Empty: true } ? "error" : picks is null ? "searching" : picks.Empty ? "nothing" : menu.Pack is null ? "episodes" : "pack";
        var moved = shown != menu.Shown && !menu.Sending; menu.Shown = shown;
        menu.Body.Child = stack;
        // A new height: placed again (above the button when the results no longer fit below it).
        _ = Dispatcher.BeginInvoke(() => { if (popup.IsOpen) NudgeMissingMenu(popup); }, DispatcherPriority.Loaded);
        // Into the menu once its results come (keyboard and screen readers start there), never again while it is used.
        if (moved && shown != "searching") _ = Dispatcher.BeginInvoke(() => stack.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    /// <summary>An episode's row: checked with the release put forward, « Change » for the others.</summary>
    private FrameworkElement MissingEpisodeRow(MissingMenu menu, Popup popup, MissingChoice choice)
    {
        var current = menu.Chosen.GetValueOrDefault(choice.Episode) ?? choice.Best;
        var row = new StackPanel();
        var line = new DockPanel();
        var options = new[] { choice.Best }.Concat(choice.Others).ToList();
        var open = false;
        var others = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(26, 0, 0, 4) };
        if (options.Count > 1)
        {
            var change = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = MissingDownloads.Change, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary") });
            var arrow = new Icon { Kind = "down", Width = 11, Height = 11, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Get("TextSecondary") };
            label.Children.Add(arrow); change.Content = label;
            AutomationProperties.SetName(change, $"{MissingDownloads.Change} {MissingDownloads.EpisodeLabel(choice.Episode)}");
            change.Click += (_, _) => { open = !open; others.Visibility = open ? Visibility.Visible : Visibility.Collapsed; arrow.Kind = open ? "up" : "down"; };
            DockPanel.SetDock(change, Dock.Right); line.Children.Add(change);
        }
        var check = new CheckBox { Style = (Style)FindResource("PlayerMenuChoice"), IsChecked = menu.Chosen.GetValueOrDefault(choice.Episode) is not null, Content = MissingOptionText(current, MissingDownloads.EpisodeLabel(choice.Episode)) };
        AutomationProperties.SetName(check, $"{MissingDownloads.EpisodeLabel(choice.Episode)} : {MissingDownloads.Option(current).Quality}, {MissingDownloads.Option(current).Detail}");
        check.Click += (_, _) => { menu.Chosen[choice.Episode] = check.IsChecked == true ? current : null; DrawMissingFoot(menu); };
        line.Children.Add(check);
        row.Children.Add(line);
        foreach (var option in options)
        {
            var pick = new RadioButton { Style = (Style)FindResource("PlayerMenuChoice"), GroupName = "missing-" + choice.Episode, IsChecked = ReferenceEquals(option, current), Content = MissingOptionText(option, null) };
            pick.Checked += (_, _) =>
            {
                current = option; menu.Chosen[choice.Episode] = option; check.IsChecked = true;
                check.Content = MissingOptionText(option, MissingDownloads.EpisodeLabel(choice.Episode));
                AutomationProperties.SetName(check, $"{MissingDownloads.EpisodeLabel(choice.Episode)} : {MissingDownloads.Option(option).Quality}, {MissingDownloads.Option(option).Detail}");
                DrawMissingFoot(menu);
            };
            others.Children.Add(pick);
        }
        row.Children.Add(others);
        return row;
    }

    /// <summary>The season's pack put forward; « Other choices (6) » for the others.</summary>
    private FrameworkElement MissingPackRow(MissingMenu menu, Popup popup)
    {
        var picks = menu.Picks!; var current = menu.Pack!;
        var row = new StackPanel();
        var shown = new Border { Padding = new Thickness(10, 8, 10, 8), Child = MissingOptionText(current, ReleaseRanking.EpisodeLabel(current.Entry.Release)) };
        row.Children.Add(shown);
        if (picks.Packs.Count > 1)
        {
            var others = new StackPanel { Visibility = Visibility.Collapsed };
            var toggle = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.OtherChoices(picks.Packs.Count - 1), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
            toggle.Click += (_, _) => others.Visibility = others.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            foreach (var option in picks.Packs)
            {
                var pick = new RadioButton { Style = (Style)FindResource("PlayerMenuChoice"), GroupName = "missing-pack", IsChecked = ReferenceEquals(option, current), Content = MissingOptionText(option, null) };
                pick.Checked += (_, _) => { menu.Pack = option; shown.Child = MissingOptionText(option, ReleaseRanking.EpisodeLabel(option.Entry.Release)); DrawMissingFoot(menu); };
                others.Children.Add(pick);
            }
            row.Children.Add(toggle); row.Children.Add(others);
        }
        // Episode by episode instead, when some were found one by one.
        if (picks.Episodes.Count > 0)
        {
            var single = new Button { Style = (Style)FindResource("Quiet"), Content = L($"Plutôt épisode par épisode ({picks.Episodes.Count})", $"Episode by episode instead ({picks.Episodes.Count})"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
            single.Click += (_, _) => { menu.Pack = null; DrawMissingMenu(menu, popup); };
            row.Children.Add(single);
        }
        return row;
    }

    /// <summary>« É6 · 1080p · WEB-DL · MULTI », then « 2,1 Go · 120 sources · 1337x » under it.</summary>
    private static FrameworkElement MissingOptionText(MissingOption option, string? label)
    {
        var (quality, detail) = MissingDownloads.Option(option);
        var text = new StackPanel();
        var first = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        if (label is not null) first.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { FontWeight = FontWeights.Bold });
        first.Inlines.Add(new System.Windows.Documents.Run(quality));
        text.Children.Add(first);
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        text.ToolTip = option.Entry.Hit.Name;
        return text;
    }

    /// <summary>The foot after a check or a release changed: « 5,6 Go · into Arcane › Season 02 » and « Download 3 ».</summary>
    private static void DrawMissingFoot(MissingMenu menu)
    {
        var chosen = menu.Pack is { } pack ? [pack] : menu.Chosen.Values.OfType<MissingOption>().ToList();
        if (menu.Total is { } total) total.Text = MissingDownloads.Footer(chosen.Sum(x => x.Entry.Hit.Size), menu.Series.Name, menu.Ask.Season);
        if (menu.Go is { } go) { go.Content = menu.Sending ? L("Envoi…", "Sending…") : MissingDownloads.Download(chosen.Count, menu.Pack is not null); go.IsEnabled = chosen.Count > 0 && !menu.Sending; }
    }

    /// <summary>« Nothing found for “Arcane S02”. », the name to change, « Search again », « Open Downloads ».</summary>
    private FrameworkElement MissingNothing(MissingMenu menu, Popup popup)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = MissingDownloads.NothingFound(menu.Query ?? menu.Series.Name), FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = MissingDownloads.NothingHelp, FontSize = 12.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        var box = new TextBox { Text = menu.Query ?? menu.Series.Name, FontSize = 13, Padding = new Thickness(10, 7, 10, 7) };
        AutomationProperties.SetName(box, L("Nom à chercher", "Name to search"));
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var search = new Button { Style = (Style)FindResource("Primary"), Content = MissingDownloads.SearchAgain, Padding = new Thickness(16, 7, 16, 7) };
        Motion.SetRadius(search, new CornerRadius(16));
        void Again() { if (RemoteDownloads.CleanQuery(box.Text) is { } typed) { menu.Query = typed; _ = SearchMissingAsync(menu, popup); } }
        search.Click += (_, _) => Again();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Again(); e.Handled = true; } };
        var downloads = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.OpenDownloads, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(6, 0, 0, 0) };
        downloads.Click += async (_, _) => { popup.IsOpen = false; await ShowTorLinkAsync(); };
        buttons.Children.Add(search); buttons.Children.Add(downloads);
        panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>Each release chosen goes to the relay with where it goes; the menu closes, the tile shows the rest.</summary>
    private async Task SendMissingAsync(MissingMenu menu, Popup popup)
    {
        if (_client is not { } client || menu.Picks is not { } picks) return;
        var series = menu.Series;
        var sends = menu.Pack is { } pack ? [(pack, new MissingPlace(series.Id, menu.Ask.Season, []))]
            : menu.Chosen.Where(x => x.Value is not null).OrderBy(x => x.Key).Select(x => (x.Value!, new MissingPlace(series.Id, menu.Ask.Season, [x.Key]))).ToList();
        if (sends.Count == 0) return;
        menu.Sending = true; menu.Error = null; DrawMissingMenu(menu, popup);
        var sent = 0;
        try
        {
            foreach (var (option, place) in sends) { await client.TorLinkMissingAsync(option.Search, option.Entry.Hit, place, _connectionLifetime.Token); sent++; }
        }
        catch (Exception ex) when (IsExpected(ex)) { menu.Error = ServerError(ex); }
        menu.Sending = false;
        if (sent > 0)
        {
            try { _missingStatus = await client.TorLinkStatusAsync(_connectionLifetime.Token); } catch (Exception ex) when (IsExpected(ex)) { }
            if (_detail?.Id == series.Id) { UpdateMissingViews(); WatchMissing(); }
        }
        if (menu.Error is null) popup.IsOpen = false;
        else
        {
            // What went is no longer offered in the menu: only what the relay refused stays to send again.
            foreach (var (option, place) in sends.Take(sent)) if (place.Episodes is [var e]) menu.Chosen[e] = null; else menu.Pack = null;
            if (popup.IsOpen) DrawMissingMenu(menu, popup);
        }
    }
}
