using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Télécharger les manquants », as on Windows (mock-up A chosen by ogsas on 2026-11-13, MissingDownloads of Core): « Get E6–9 »
// in the tile of the episodes the library lacks, « Get season 2 » on the card of a season it has none of; a menu beside the
// button puts forward a release for each episode (or a season pack), searched on the PC of Jellyfin through the relay, and
// what goes shows its progress in the tile and the season's pill until the episodes take their place. For an administrator,
// when the relay's agent (Mira for Windows on the PC of Jellyfin) places what a series' page asks (RelayAgent.Seasons).
public sealed partial class DetailPage
{
    private RelayStatus? _missingStatus;
    private bool _missingOffered;
    // The tiles, the card and the pills that show what was asked, updated in place at each look at the relay.
    private readonly List<(int Season, IReadOnlyList<int>? Episodes, bool Pill, Action<MissingProgress?> Show)> _missingViews = [];
    private DispatcherTimer? _missingTimer;
    private Flyout? _missingFlyout;
    private CancellationTokenSource? _missingSearch;
    private readonly HashSet<string> _missingPlaced = [];
    private bool _missingWatching;
    private MissingMenu? _missingMenu;

    /// <summary>For the self-check: the buttons offered, the menu of the first opened, what it shows (« nothing », « episodes »…).</summary>
    public bool MissingOffered => _missingOffered;
    public string? MissingMenuShown => _missingMenu is { Open: true } menu ? menu.Shown : null;
    public bool OpenMissingForCheck()
    {
        var get = _episodes.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => AutomationProperties.GetHelpText(x) == MissingDownloads.Privacy && x.IsEffectivelyVisible);
        get?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        return get is not null;
    }
    public void CloseMissingForCheck() => _missingFlyout?.Hide();
    public bool MissingMenuOpen => _missingMenu is { Open: true };
    /// <summary>The pack rows of the open menu: checked, and named for VoiceOver.</summary>
    public IReadOnlyList<(bool Chosen, bool Named)> MissingPacksForCheck => _missingMenu is { Open: true } menu
        ? menu.Body.GetLogicalDescendants().OfType<RadioButton>().Where(x => x.GroupName == "missing-pack" && x.GetLogicalAncestors().OfType<Control>().TakeWhile(a => !ReferenceEquals(a, menu.Body)).All(a => a.IsVisible) && x.IsVisible)
            .Select(x => (x.IsChecked == true, !string.IsNullOrEmpty(AutomationProperties.GetName(x)))).ToList()
        : [];

    /// <summary>Whether this page offers the buttons: an administrator, and a Mira on the PC of Jellyfin that places them.</summary>
    private async Task LoadMissingAsync(Session session)
    {
        if (!_missingWatching)
        {
            // Looked at only while the page is on screen (not under the player, nor in the Dock).
            _missingWatching = true;
            Showing += shown => { if (shown) WatchMissing(); else StopMissingTimer(); };
        }
        RelayStatus status;
        try
        {
            if (await session.Client.IsAdministratorAsync(session.Lifetime) != true) return;
            status = await session.Client.TorLinkStatusAsync(session.Lifetime);
        }
        // An extension without the relay, or a hiccup: the page is complete without the buttons.
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (!ReferenceEquals(session, Shell.Session)) return;
        _missingStatus = status;
        // Placed before the page opened: its episodes were just read.
        foreach (var x in status.Missing ?? []) if (Placed(status, x)) _missingPlaced.Add(x.Id);
        _missingLookedAt = DateTime.UtcNow;
        var offered = status.Agent is { Seasons: true };
        if (offered != _missingOffered) { _missingOffered = offered; if (_allEpisodes.Count > 0 || _virtuals.Count > 0) { FillSeasons(); RenderEpisodes(); } }
        else UpdateMissingViews();
        WatchMissing();
    }

    // While a tile says « Adding to your library »: when the series' episodes were last read, since when it waits for Jellyfin.
    private DateTime _missingLookedAt;
    private DateTimeOffset? _missingWaitingSince;
    private DateTime _missingScannedAt;
    private bool _missingLooking;

    private bool Placed(RelayStatus status, RelayMissing x) => MissingDownloads.Progress(status with { Missing = [x] }, _item.Id, x.Season, null) is { Stage: MissingStage.Placed };

    private void StopMissingTimer() { _missingTimer?.Stop(); _missingTimer = null; }

    /// <summary>Something asked from this page is on its way: TorLink has it.</summary>
    private bool MissingUnderWay() => _missingStatus is { } status
        && status.Missing?.Any(x => MissingDownloads.Progress(status with { Missing = [x] }, _item.Id, x.Season, null) is { Stage: MissingStage.Waiting or MissingStage.Downloading }) == true;

    /// <summary>A tile or a card of this page says « Adding to your library »: what it shows comes once Jellyfin lists it.</summary>
    private bool MissingAwaitsJellyfin() =>
        _missingViews.Any(v => !v.Pill && MissingDownloads.Progress(_missingStatus, _item.Id, v.Season, v.Episodes) is { Stage: MissingStage.Adding or MissingStage.Placed });

    /// <summary>
    /// Looks at the relay every 3 s while something asked from this page downloads, and every 10 s while a tile waits for
    /// Jellyfin (the series' episodes read again now and then: <see cref="MissingDownloads.LookAgain"/>), until the episodes take
    /// their place. As on Windows.
    /// </summary>
    private void WatchMissing()
    {
        var underWay = MissingUnderWay(); var awaits = MissingAwaitsJellyfin();
        if (!awaits) _missingWaitingSince = null;
        if (!underWay && !awaits || !_missingOffered || !IsShowing) { StopMissingTimer(); return; }
        var every = TimeSpan.FromSeconds(underWay ? 3 : 10);
        if (_missingTimer is not null) { if (_missingTimer.Interval != every) _missingTimer.Interval = every; return; }
        _missingTimer = new DispatcherTimer(every, DispatcherPriority.Background, async (_, _) => await LookAtMissingAsync());
        _missingTimer.Start();
    }

    private async Task LookAtMissingAsync()
    {
        if (Shell.Session is not { } session || !IsShowing) { StopMissingTimer(); return; }
        if (_missingLooking) return;
        _missingLooking = true;
        try
        {
            try { _missingStatus = await session.Client.TorLinkStatusAsync(session.Lifetime); }
            catch (Exception ex) when (Errors.Expected(ex)) { return; }
            if (!ReferenceEquals(session, Shell.Session)) return;
            UpdateMissingViews();
            // Newly placed: the series' episodes read at once; then, while a tile still waits, now and then (Jellyfin lists a
            // placed file about a minute later, after its folder monitor's delay).
            var placed = (_missingStatus.Missing ?? []).Where(x => !_missingPlaced.Contains(x.Id) && Placed(_missingStatus, x)).ToList();
            foreach (var x in placed) _missingPlaced.Add(x.Id);
            if (MissingAwaitsJellyfin())
            {
                var now = DateTimeOffset.UtcNow;
                var since = _missingWaitingSince ??= now;
                if (placed.Count > 0 || DateTime.UtcNow - _missingLookedAt >= MissingDownloads.LookAgain(now - since)) await LookForPlacedEpisodesAsync(session);
            }
        }
        finally { _missingLooking = false; }
        if (ReferenceEquals(session, Shell.Session)) WatchMissing();
    }

    /// <summary>The series' episodes read again; when Jellyfin lists new ones, the page shows them in place of the hollow tiles.</summary>
    private async Task LookForPlacedEpisodesAsync(Session session)
    {
        _missingLookedAt = DateTime.UtcNow;
        ItemsResult now;
        try { now = await session.Client.EpisodesAsync(_item.Id, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (!ReferenceEquals(session, Shell.Session) || !MissingDownloads.Arrived(_allEpisodes, now.Items)) return;
        await ReloadEpisodesAsync(session);
    }

    /// <summary>The series' episodes and TheTVDB's read again: those placed leave the hollow tiles for their own.</summary>
    private async Task ReloadEpisodesAsync(Session session)
    {
        try
        {
            session.Forget();
            var episodes = await session.EpisodesAsync(_item.Id);
            await Task.Run(() => Errors.TryCache(() => session.Store.ApplyLocalProgress(episodes.Items)));
            var virtuals = (await session.Client.VirtualEpisodesAsync(session.Lifetime)).Where(x => x.SeriesId == _item.Id).ToList();
            if (!ReferenceEquals(session, Shell.Session)) return;
            _allEpisodes = episodes.Items; _virtuals = virtuals;
            FillSeasons(); RenderEpisodes();
            _ = LoadSizeAsync(session);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    private void UpdateMissingViews()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (season, episodes, _, show) in _missingViews.ToList()) show(MissingDownloads.Progress(_missingStatus, _item.Id, season, episodes, now));
    }

    // ---- In the tiles, the card and the pills -------------------------------------------------------------------------

    /// <summary>« Get E6–9 » in a tile of missing episodes, or how what was asked is going; and the bar along its foot.</summary>
    private (Control Part, Control Bar)? MissingTilePart(ShelfLine line)
    {
        if (!_missingOffered || line.Kind != ShelfKind.Missing || line.Episodes.Count > MissingDownloads.MaxEpisodes) return null;
        var numbers = line.Episodes.Select(x => x.IndexNumber).OfType<int>().ToList();
        if (numbers.Count == 0) return null;
        var season = line.First.ParentIndexNumber ?? 1;
        var ask = new MissingAsk(season, numbers, false); var heading = MissingDownloads.Code(numbers.Min(), numbers.Max());
        var part = new ContentControl { Margin = new Thickness(0, 10, 0, 0) };
        var bar = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Ui.Brush("#38FFFFFF"), IsVisible = false, ColumnDefinitions = new ColumnDefinitions("*,*") };
        bar.Children.Add(new Border { Background = Ui.Ink });
        var get = MissingButton(MissingDownloads.GetEpisodes(numbers.Min(), numbers.Max()), small: true);
        get.Click += (_, e) => { e.Handled = true; OpenMissingMenu(get, ask, heading); };
        void Show(MissingProgress? progress)
        {
            if (progress is null) { part.Content = get; bar.IsVisible = false; return; }
            part.Content = MissingProgressView(progress, again => OpenMissingMenu(again, ask, heading));
            var share = progress.Stage is MissingStage.Downloading or MissingStage.Waiting ? Math.Clamp(progress.Percent / 100.0, 0, 1) : 1;
            bar.ColumnDefinitions[0].Width = new GridLength(share, GridUnitType.Star); bar.ColumnDefinitions[1].Width = new GridLength(1 - share, GridUnitType.Star);
            bar.IsVisible = progress.Stage != MissingStage.Failed;
        }
        _missingViews.Add((season, numbers, false, Show));
        Show(MissingDownloads.Progress(_missingStatus, _item.Id, season, numbers, DateTimeOffset.UtcNow));
        return (part, bar);
    }

    /// <summary>« Get season 2 » on the card of a season the library has none of, or how what was asked is going.</summary>
    private Control? MissingSeasonPart(int season, IReadOnlyList<MediaItem> run)
    {
        if (!_missingOffered) return null;
        var numbers = run.Select(x => x.IndexNumber).OfType<int>().Distinct().Order().ToList();
        var part = new ContentControl { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 0, 0, 0), MaxWidth = 300 };
        var ask = new MissingAsk(season, numbers, true); var heading = L($"Saison {season}", $"Season {season}");
        var get = MissingButton(MissingDownloads.GetSeason(season), small: false);
        get.Click += (_, _) => OpenMissingMenu(get, ask, heading);
        void Show(MissingProgress? progress) => part.Content = progress is null ? get : MissingProgressView(progress, again => OpenMissingMenu(again, ask, heading), bar: true);
        _missingViews.Add((season, null, false, Show));
        Show(MissingDownloads.Progress(_missingStatus, _item.Id, season, null, DateTimeOffset.UtcNow));
        return part;
    }

    /// <summary>What a season's pill adds while its episodes come: « 38 % ».</summary>
    private TextBlock? MissingPillNote(EpisodePage page)
    {
        if (!_missingOffered) return null;
        var note = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.62 };
        void Show(MissingProgress? progress)
        {
            var text = progress is null ? "" : MissingDownloads.Pill(progress);
            note.Text = text; note.IsVisible = text.Length > 0;
        }
        _missingViews.Add((page.Season, null, true, Show));
        Show(MissingDownloads.Progress(_missingStatus, _item.Id, page.Season, null));
        return note;
    }

    private static Button MissingButton(string text, bool small)
    {
        var label = Ui.Row(7, new Icon("download", small ? 13 : 15) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, FontSize = small ? 12.5 : 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Padding = small ? new Thickness(12, 6, 13, 6) : new Thickness(16, 9, 18, 9), CornerRadius = new CornerRadius(small ? 15 : 18) };
        if (small) { button.Classes.Add("quiet"); button.BorderBrush = Ui.Brush("#3A3A3A"); button.BorderThickness = new Thickness(1); }
        else button.Classes.Add("primary");
        AutomationProperties.SetName(button, text); AutomationProperties.SetHelpText(button, MissingDownloads.Privacy);
        return button;
    }

    /// <summary>« Downloading on your Jellyfin PC · 38% · about 12 min left », « The download failed » with « Try again ».</summary>
    private Control MissingProgressView(MissingProgress progress, Action<Button> retry, bool bar = false)
    {
        var panel = new StackPanel();
        var failed = progress.Stage == MissingStage.Failed;
        panel.Children.Add(new TextBlock { Text = progress.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = failed ? Ui.Brush("#FF6B6B") : Ui.Brush("#E0E0E3"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (progress.Line.Length > 0) panel.Children.Add(new TextBlock { Text = progress.Line, FontSize = 12, Foreground = Ui.Muted, TextWrapping = failed ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, Margin = new Thickness(0, 2, 0, 0) });
        if (bar && progress.Stage is MissingStage.Downloading or MissingStage.Waiting)
        {
            var share = Math.Clamp(progress.Percent / 100.0, 0, 1);
            var track = new Grid { Height = 3, Width = 200, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush("#38FFFFFF"), Margin = new Thickness(0, 8, 0, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition(share, GridUnitType.Star)); track.ColumnDefinitions.Add(new ColumnDefinition(1 - share, GridUnitType.Star));
            track.Children.Add(new Border { Background = Ui.Ink });
            panel.Children.Add(track);
        }
        if (progress.Rescan)
        {
            // Jellyfin didn't see it after its monitor's delay: a scan of the libraries, as its dashboard does.
            if (DateTime.UtcNow - _missingScannedAt < TimeSpan.FromMinutes(5))
                panel.Children.Add(new TextBlock { Text = MissingDownloads.Rescanning, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            else
            {
                var scan = new Button { Classes = { "quiet" }, Content = MissingDownloads.Rescan, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4), Margin = new Thickness(-10, 4, 0, 0), FontSize = 12.5 };
                scan.Click += async (_, e) => { e.Handled = true; await ScanForMissingAsync(scan); };
                panel.Children.Add(scan);
            }
        }
        if (failed)
        {
            var again = new Button { Classes = { "quiet" }, Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4), Margin = new Thickness(-10, 4, 0, 0), FontSize = 12.5 };
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
        if (Shell.Session is not { } session) return;
        scan.IsEnabled = false;
        try
        {
            if (!await session.Client.ScanLibrariesAsync(session.Lifetime))
            {
                Shell.Notice(L("Seul un administrateur de Jellyfin peut analyser les médiathèques.", "Only a Jellyfin administrator can scan the libraries."), tone: NoticeTone.Error);
                scan.IsEnabled = true; return;
            }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); scan.IsEnabled = true; return; }
        _missingScannedAt = DateTime.UtcNow; _missingWaitingSince = DateTimeOffset.UtcNow;
        UpdateMissingViews(); WatchMissing();
    }

    // ---- The menu ------------------------------------------------------------------------------------------------------

    private sealed class MissingMenu
    {
        public required MissingAsk Ask { get; init; }
        public required string Heading { get; init; }
        public required Border Body { get; init; }
        public required Flyout Flyout { get; init; }
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
        public TextBlock? GoLabel { get; set; }
        public string? Shown { get; set; }
        public bool Open { get; set; }
    }

    private void OpenMissingMenu(Button anchor, MissingAsk ask, string heading)
    {
        _missingSearch?.Cancel();
        _missingFlyout?.Hide();
        // The flyout's presenter is the window of the player's menus (« Écrin », App.axaml): rows reach its 8 px padding.
        var body = new Border { Width = MissingMenuWidth };
        // Kept inside Mira's window (a popup is a window of its own on macOS, which Avalonia only keeps on the screen): below
        // the button or above it, narrower in a narrow window, its list scrolling in a short one (MenuFit of Core). Avalonia
        // places it again when its button moves (the page scrolls) or its size changes.
        var flyout = new Flyout { Placement = PlacementMode.Custom, ShowMode = FlyoutShowMode.Standard, Content = body };
        flyout.CustomPopupPlacementCallback = placement => PlaceMissingMenu(anchor, placement);
        var menu = new MissingMenu { Ask = ask, Heading = heading, Body = body, Flyout = flyout, Open = true };
        var window = TopLevel.GetTopLevel(anchor);
        void Refit(object? sender, SizeChangedEventArgs e) { if (menu.Open) FitMissingMenu(anchor, body); }
        FitMissingMenu(anchor, body);
        if (window is not null) window.SizeChanged += Refit;
        flyout.Closed += (_, _) =>
        {
            menu.Open = false;
            if (window is not null) window.SizeChanged -= Refit;
            if (ReferenceEquals(_missingFlyout, flyout)) { _missingSearch?.Cancel(); _missingFlyout = null; }
        };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; anchor.Focus(); } };
        _missingFlyout = flyout; _missingMenu = menu;
        flyout.ShowAt(anchor);
        _ = SearchMissingAsync(menu);
    }

    private const double MissingMenuWidth = 542;
    // The flyout's presenter around the menu (App.axaml: padding 8, border 1), and the window's title bar (MainWindow).
    private const double MissingMenuFrame = 18, MissingTitleBar = 30;
    /// <summary>The menu's width and tallest height, from where its button is in the window (title bar excluded).</summary>
    private void FitMissingMenu(Control anchor, Border body)
    {
        if (MissingMenuArea(anchor) is not { } area) return;
        var bounds = MenuFit.Size(area.Width, area.Height, area.Anchor, MissingMenuWidth + MissingMenuFrame);
        body.Width = Math.Max(0, bounds.Width - MissingMenuFrame); body.MaxHeight = Math.Max(0, bounds.MaxHeight - MissingMenuFrame);
    }
    /// <summary>Where Avalonia puts the menu: its top-left corner from its button's.</summary>
    private void PlaceMissingMenu(Control anchor, CustomPopupPlacement placement)
    {
        placement.Anchor = PopupAnchor.TopLeft; placement.Gravity = PopupGravity.BottomRight; placement.ConstraintAdjustment = PopupPositionerConstraintAdjustment.None;
        // The anchor rectangle is in the window's coordinates (a rectangle at 0, 0 put the menu at the window's left edge).
        placement.AnchorRectangle = TopLevel.GetTopLevel(anchor) is { } top && anchor.TranslatePoint(new Point(0, 0), top) is { } corner ? new Rect(corner, anchor.Bounds.Size) : new Rect(anchor.Bounds.Size);
        if (MissingMenuArea(anchor) is not { } area) { placement.Offset = new Point(0, anchor.Bounds.Height + MenuFit.Gap); return; }
        var at = MenuFit.At(area.Width, area.Height, area.Anchor, placement.PopupSize.Width, placement.PopupSize.Height);
        placement.Offset = new Point(at.X - area.Anchor.Left, at.Y - area.Anchor.Top);
    }
    /// <summary>The window below its title bar and the button in it.</summary>
    private static (double Width, double Height, MenuFit.Box Anchor)? MissingMenuArea(Control anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not { } window || anchor.TranslatePoint(new Point(0, 0), window) is not { } from
            || anchor.TranslatePoint(new Point(anchor.Bounds.Width, anchor.Bounds.Height), window) is not { } to) return null;
        return (window.Bounds.Width, window.Bounds.Height - MissingTitleBar, new MenuFit.Box(from.X, from.Y - MissingTitleBar, to.X - from.X, to.Y - from.Y));
    }

    private async Task SearchMissingAsync(MissingMenu menu)
    {
        if (Shell.Session is not { } session) return;
        _missingSearch?.Cancel();
        var cts = _missingSearch = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime);
        var ct = cts.Token;
        menu.Picks = null; menu.Error = null; menu.Chosen.Clear(); menu.Pack = null;
        DrawMissingMenu(menu);
        var season = menu.Ask.Season;
        var known = new MissingSeries(_item.Name, _item.OriginalTitle, _item.ProductionYear, MissingDownloads.Before(_allEpisodes.Concat(_virtuals), season), menu.Query);
        try
        {
            bool anime;
            try { anime = await MissingDownloads.IsAnimeAsync(session.Client, _item, ct); }
            catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested) { anime = false; }
            var hits = new List<(string Search, RelayHit Hit)>();
            string? failure = null;
            foreach (var query in MissingDownloads.Queries(known, season))
            {
                menu.Query ??= query;
                var (found, error) = await MissingDownloads.SearchAsync(session.Client, query, MissingDownloads.Categories(anime), ct);
                hits.AddRange(found); failure ??= error;
                var picks = MissingDownloads.Choose(menu.Ask, known, hits);
                // Everything found already (or three season packs to choose from): the second, wider search would only add noise.
                if (MissingDownloads.SearchedEnough(picks)) break;
            }
            if (ct.IsCancellationRequested) return;
            menu.Picks = MissingDownloads.Choose(menu.Ask, known, hits);
            if (menu.Picks.Empty && failure is not null) menu.Error = failure;
            menu.Pack = menu.Picks.Packs.FirstOrDefault();
            foreach (var choice in menu.Picks.Episodes) menu.Chosen[choice.Episode] = choice.Best;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (Errors.Expected(ex)) { menu.Error = Errors.Friendly(ex); }
        if (menu.Open) DrawMissingMenu(menu);
    }

    private void DrawMissingMenu(MissingMenu menu)
    {
        // Heading on top, foot below, what is between scrolling when the window leaves the menu too little height.
        var stack = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var middle = new StackPanel();
        var foot = new StackPanel();
        var picks = menu.Picks;
        // The heading and what the menu offers under it; the close button in the corner, 8 px from the window's edges.
        var head = new Grid();
        head.Children.Add(Ui.Column(4,
            new TextBlock { Text = menu.Heading, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Ui.Ink, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = MissingDownloads.Lead(picks is { Empty: false } ? menu.Pack is not null : null), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }));
        ((Control)head.Children[0]).Margin = new Thickness(14, 12, 40, 10);
        var close = Ui.IconButton("close", L("Fermer", "Close"), () => menu.Flyout.Hide(), "icon", 15);
        close.Width = close.Height = 32; close.CornerRadius = new CornerRadius(10); close.Foreground = Ui.TextSecondary;
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        head.Children.Add(close);
        stack.Children.Add(head);
        // Words (searching, nothing found, an error) line up with the heading; rows reach the window's padding, as in the player.
        Control scrolled = middle;
        middle.Margin = new Thickness(14, 0, 14, 4);
        if (menu.Error is { } error && picks is null or { Empty: true })
        {
            middle.Children.Add(new TextBlock { Text = error, Foreground = Ui.Brush("#FF6B6B"), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 });
            var again = new Button { Classes = { "quiet" }, Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-12, 8, 0, 0) };
            again.Click += (_, _) => _ = SearchMissingAsync(menu);
            middle.Children.Add(again);
        }
        else if (picks is null)
        {
            middle.Children.Add(new TextBlock { Text = MissingDownloads.Searching, FontSize = 13, Foreground = Ui.TextSecondary });
            for (var i = 0; i < 3; i++)
            {
                var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Opacity = 1 - i * 0.25 };
                row.Children.Add(new Border { Height = 11, Width = 240 - i * 30, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Ui.Raised2 });
                row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Ui.Raised2, Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
                middle.Children.Add(row);
            }
            AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Polite);
        }
        else if (picks.Empty) middle.Children.Add(MissingNothing(menu));
        else
        {
            var list = new StackPanel();
            if (menu.Pack is not null) list.Children.Add(MissingPackRow(menu));
            else foreach (var choice in picks.Episodes) list.Children.Add(MissingEpisodeRow(menu, choice));
            scrolled = list; middle.Margin = new Thickness(0);
            if (menu.Pack is null && MissingDownloads.NotFound(picks.NotFound) is { Length: > 0 } nothing)
                foot.Children.Add(new TextBlock { Text = nothing, FontSize = 12.5, Foreground = Ui.Muted, Margin = new Thickness(14, 8, 14, 0), TextWrapping = TextWrapping.Wrap });
            // A line, then what goes and where (its size in bold), the privacy line under it, « Download season » on the right.
            foot.Children.Add(new Border { Height = 1, Background = Ui.Rule, Margin = new Thickness(6, 8, 6, 0) });
            var bar = new DockPanel { Margin = new Thickness(8, 14, 8, 8) };
            menu.GoLabel = new TextBlock { FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var go = menu.Go = new Button { Classes = { "primary" }, Height = 42, Padding = new Thickness(20, 0), CornerRadius = new CornerRadius(21), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Content = Ui.Row(9, new Icon("download", 16) { VerticalAlignment = VerticalAlignment.Center }, menu.GoLabel) };
            go.Click += async (_, _) => await SendMissingAsync(menu);
            DockPanel.SetDock(go, Dock.Right); bar.Children.Add(go);
            menu.Total = new TextBlock { FontSize = 13.5, Foreground = Ui.TextBody, TextTrimming = TextTrimming.CharacterEllipsis };
            var where = Ui.Column(3, menu.Total, new TextBlock { Text = MissingDownloads.Privacy, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap });
            where.VerticalAlignment = VerticalAlignment.Center; where.Margin = new Thickness(0, 0, 16, 0);
            bar.Children.Add(where);
            foot.Children.Add(bar);
            DrawMissingFoot(menu);
            if (menu.Error is { } refused) foot.Children.Add(new TextBlock { Text = refused, Foreground = Ui.Brush("#FF6B6B"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(8, 0, 8, 8) });
        }
        // Without results, the privacy line alone, under the words.
        if (ReferenceEquals(scrolled, middle))
            foot.Children.Add(new TextBlock { Text = MissingDownloads.Privacy, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 8) });
        // Never taller than its list (380): beyond that, the list scrolls even in a tall window, as before.
        var scroller = new ScrollViewer { Content = scrolled, MaxHeight = ReferenceEquals(scrolled, middle) ? double.PositiveInfinity : 380, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Grid.SetRow(scroller, 1); Grid.SetRow(foot, 2);
        stack.Children.Add(scroller); stack.Children.Add(foot);
        var shown = menu.Error is not null && picks is null or { Empty: true } ? "error" : picks is null ? "searching" : picks.Empty ? "nothing" : menu.Pack is null ? "episodes" : "pack";
        var moved = shown != menu.Shown && !menu.Sending; menu.Shown = shown;
        menu.Body.Child = stack;
        // Into the menu once its results come (keyboard and VoiceOver start there), never again while it is used.
        if (moved && shown != "searching")
            Dispatcher.UIThread.Post(() =>
            {
                if (!menu.Open) return;
                var first = stack.Children.Skip(1).SelectMany(x => x.GetSelfAndVisualDescendants()).OfType<InputElement>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible && x.IsEffectivelyEnabled);
                first?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Input);
    }

    /// <summary>An episode's row: checked with the release put forward, « Change » for the others.</summary>
    private Control MissingEpisodeRow(MissingMenu menu, MissingChoice choice)
    {
        var current = menu.Chosen.GetValueOrDefault(choice.Episode) ?? choice.Best;
        string Name(MissingOption option) { var (quality, detail) = MissingDownloads.Option(option); return $"{MissingDownloads.EpisodeLabel(choice.Episode)} : {quality}, {detail}"; }
        var row = new StackPanel();
        var line = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var options = new[] { choice.Best }.Concat(choice.Others).ToList();
        var others = new StackPanel { IsVisible = false, Margin = new Thickness(26, 0, 0, 4) };
        if (options.Count > 1)
        {
            var arrow = new Icon("down", 11) { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.TextSecondary };
            var change = new Button { Classes = { "quiet" }, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, Content = Ui.Row(5, new TextBlock { Text = MissingDownloads.Change, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextSecondary }, arrow) };
            AutomationProperties.SetName(change, $"{MissingDownloads.Change} {MissingDownloads.EpisodeLabel(choice.Episode)}");
            change.Click += (_, _) => { others.IsVisible = !others.IsVisible; arrow.Kind = others.IsVisible ? "up" : "down"; };
            DockPanel.SetDock(change, Dock.Right); line.Children.Add(change);
        }
        var check = new CheckBox { IsChecked = menu.Chosen.GetValueOrDefault(choice.Episode) is not null, Content = MissingOptionText(current, MissingDownloads.EpisodeLabel(choice.Episode)), Margin = new Thickness(0, 4) };
        AutomationProperties.SetName(check, Name(current));
        check.IsCheckedChanged += (_, _) => { menu.Chosen[choice.Episode] = check.IsChecked == true ? current : null; DrawMissingFoot(menu); };
        line.Children.Add(check);
        row.Children.Add(line);
        foreach (var option in options)
        {
            var pick = new RadioButton { Classes = { "pack" }, Padding = new Thickness(12, 8), GroupName = $"missing-{choice.Episode}", IsChecked = ReferenceEquals(option, current), Content = MissingOptionText(option, null) };
            AutomationProperties.SetName(pick, Name(option));
            pick.IsCheckedChanged += (_, _) =>
            {
                if (pick.IsChecked != true) return;
                current = option; menu.Chosen[choice.Episode] = option; check.IsChecked = true;
                check.Content = MissingOptionText(option, MissingDownloads.EpisodeLabel(choice.Episode));
                AutomationProperties.SetName(check, Name(option));
                DrawMissingFoot(menu);
            };
            others.Children.Add(pick);
        }
        row.Children.Add(others);
        return row;
    }

    /// <summary>The season's packs as radio rows (the best checked, what each one is said on its right); « Other choices (6) » for the others.</summary>
    private Control MissingPackRow(MissingMenu menu)
    {
        var picks = menu.Picks!; var episodes = menu.Ask.Episodes.Count;
        var row = new StackPanel();
        // Up to three to choose from (best picture, best balance, smallest), the best checked; the others below them.
        var choices = MissingDownloads.PackChoices(picks.Packs, episodes);
        RadioButton Pick(MissingOption option, string? kind)
        {
            var text = (StackPanel)MissingOptionText(option, null);
            text.Children.Add(new TextBlock { Text = MissingDownloads.Covers(option, episodes), FontSize = 12, FontWeight = FontWeight.Normal, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap });
            var content = new DockPanel();
            if (kind is not null)
            {
                var tag = new TextBlock { Text = kind, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(tag, Dock.Right); content.Children.Add(tag);
            }
            content.Children.Add(text);
            var pick = new RadioButton { Classes = { "pack" }, GroupName = "missing-pack", IsChecked = ReferenceEquals(option, menu.Pack), Content = content };
            var (quality, detail) = MissingDownloads.Option(option);
            AutomationProperties.SetName(pick, string.Join(", ", new[] { kind, quality, detail, MissingDownloads.Covers(option, episodes) }.Where(x => !string.IsNullOrEmpty(x))));
            pick.IsCheckedChanged += (_, _) => { if (pick.IsChecked == true) { menu.Pack = option; DrawMissingFoot(menu); } };
            return pick;
        }
        foreach (var choice in choices) row.Children.Add(Pick(choice.Option, choices.Count > 1 ? MissingDownloads.PackKindName(choice.Kind) : null));
        var rest = picks.Packs.Where(x => choices.All(c => !ReferenceEquals(c.Option, x))).ToList();
        if (rest.Count > 0)
        {
            var others = new StackPanel { IsVisible = false };
            var toggle = MissingMore(MissingDownloads.OtherChoices(rest.Count));
            toggle.Click += (_, _) => others.IsVisible = !others.IsVisible;
            foreach (var option in rest) others.Children.Add(Pick(option, null));
            row.Children.Add(toggle); row.Children.Add(others);
        }
        // Episode by episode instead, when some were found one by one.
        if (picks.Episodes.Count > 0)
        {
            var single = MissingMore(L($"Plutôt épisode par épisode ({picks.Episodes.Count})", $"Episode by episode instead ({picks.Episodes.Count})"));
            single.Click += (_, _) => { menu.Pack = null; DrawMissingMenu(menu); };
            row.Children.Add(single);
        }
        return row;
    }

    /// <summary>« Other choices (6) », « Episode by episode instead »: quiet words under the rows, aligned with their text.</summary>
    private static Button MissingMore(string text) => new()
    {
        Classes = { "quiet" }, Content = text, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 7), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(10), FontSize = 13, Foreground = Ui.TextSecondary,
    };

    /// <summary>« É6 · 1080p · WEB-DL · MULTI », then « 2,1 Go · 120 sources · 1337x » under it.</summary>
    private static Control MissingOptionText(MissingOption option, string? label)
    {
        var (quality, detail) = MissingDownloads.Option(option);
        var first = new TextBlock { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        if (label is not null) first.Inlines!.Add(new Avalonia.Controls.Documents.Run(label + "  ") { FontWeight = FontWeight.Bold });
        first.Inlines!.Add(new Avalonia.Controls.Documents.Run(quality));
        var text = Ui.Column(2, first, new TextBlock { Text = detail, FontSize = 12.5, FontWeight = FontWeight.Normal, Foreground = Ui.Faint, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(text, option.Entry.Hit.Name);
        return text;
    }

    /// <summary>The foot after a check or a release changed: « <b>5,6 Go</b> into Arcane › Season 02 » and « Download 3 ».</summary>
    private void DrawMissingFoot(MissingMenu menu)
    {
        var chosen = menu.Pack is { } pack ? [pack] : menu.Chosen.Values.OfType<MissingOption>().ToList();
        if (menu.Total is { } total)
        {
            total.Inlines!.Clear();
            total.Inlines.Add(new Avalonia.Controls.Documents.Run(RemoteLines.Size(chosen.Sum(x => x.Entry.Hit.Size))) { FontWeight = FontWeight.Bold, Foreground = Ui.Ink });
            total.Inlines.Add(new Avalonia.Controls.Documents.Run(" " + MissingDownloads.Destination(_item.Name, menu.Ask.Season)));
        }
        if (menu.Go is { } go)
        {
            var label = menu.Sending ? L("Envoi…", "Sending…") : MissingDownloads.Download(chosen.Count, menu.Pack is not null);
            if (menu.GoLabel is { } words) words.Text = label;
            AutomationProperties.SetName(go, label);
            go.IsEnabled = chosen.Count > 0 && !menu.Sending;
        }
    }

    /// <summary>« Nothing found for “Arcane S02”. », the name to change, « Search again », « Open Downloads ».</summary>
    private Control MissingNothing(MissingMenu menu)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = MissingDownloads.NothingFound(menu.Query ?? _item.Name), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = MissingDownloads.NothingHelp, FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        var box = new TextBox { Text = menu.Query ?? _item.Name, FontSize = 13 };
        AutomationProperties.SetName(box, L("Nom à chercher", "Name to search"));
        panel.Children.Add(box);
        void Again() { if (RemoteDownloads.CleanQuery(box.Text) is { } typed) { menu.Query = typed; _ = SearchMissingAsync(menu); } }
        var search = new Button { Classes = { "primary" }, Content = MissingDownloads.SearchAgain, Padding = new Thickness(16, 7), CornerRadius = new CornerRadius(16) };
        search.Click += (_, _) => Again();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Again(); e.Handled = true; } };
        var downloads = new Button { Classes = { "quiet" }, Content = MissingDownloads.OpenDownloads, Padding = new Thickness(14, 7) };
        downloads.Click += (_, _) => { menu.Flyout.Hide(); Shell.Go("downloads"); };
        var buttons = Ui.Row(6, search, downloads); buttons.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(buttons);
        return panel;
    }

    /// <summary>Each release chosen goes to the relay with where it goes; the menu closes, the tile shows the rest.</summary>
    private async Task SendMissingAsync(MissingMenu menu)
    {
        if (Shell.Session is not { } session || menu.Picks is null) return;
        var sends = menu.Pack is { } pack ? [(pack, new MissingPlace(_item.Id, menu.Ask.Season, []))]
            : menu.Chosen.Where(x => x.Value is not null).OrderBy(x => x.Key).Select(x => (x.Value!, new MissingPlace(_item.Id, menu.Ask.Season, [x.Key]))).ToList();
        if (sends.Count == 0) return;
        menu.Sending = true; menu.Error = null; DrawMissingMenu(menu);
        var sent = 0;
        try
        {
            foreach (var (option, place) in sends) { await session.Client.TorLinkMissingAsync(option.Search, option.Entry.Hit, place, session.Lifetime); sent++; }
        }
        catch (Exception ex) when (Errors.Expected(ex)) { menu.Error = Errors.Friendly(ex); }
        menu.Sending = false;
        if (sent > 0)
        {
            try { _missingStatus = await session.Client.TorLinkStatusAsync(session.Lifetime); } catch (Exception ex) when (Errors.Expected(ex)) { }
            if (ReferenceEquals(session, Shell.Session)) { UpdateMissingViews(); WatchMissing(); }
        }
        if (menu.Error is null) menu.Flyout.Hide();
        else
        {
            // What went is no longer offered in the menu: only what the relay refused stays to send again.
            foreach (var (_, place) in sends.Take(sent)) if (place.Episodes is [var e]) menu.Chosen[e] = null; else menu.Pack = null;
            if (menu.Open) DrawMissingMenu(menu);
        }
    }
}
