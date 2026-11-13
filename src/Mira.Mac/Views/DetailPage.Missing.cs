using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
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
        foreach (var x in status.Missing ?? []) if (Placed(status, x)) _missingPlaced.Add(x.Id);
        var offered = status.Agent is { Seasons: true };
        if (offered != _missingOffered) { _missingOffered = offered; if (_allEpisodes.Count > 0 || _virtuals.Count > 0) { FillSeasons(); RenderEpisodes(); } }
        else UpdateMissingViews();
        WatchMissing();
    }

    private bool Placed(RelayStatus status, RelayMissing x) => MissingDownloads.Progress(status with { Missing = [x] }, _item.Id, x.Season, null) is { Stage: MissingStage.Placed };

    private void StopMissingTimer() { _missingTimer?.Stop(); _missingTimer = null; }

    /// <summary>Looks at the relay every 3 s while something asked from this page is on its way.</summary>
    private void WatchMissing()
    {
        var status = _missingStatus;
        var active = status?.Missing?.Any(x => MissingDownloads.Progress(status with { Missing = [x] }, _item.Id, x.Season, null) is { Stage: not MissingStage.Placed and not MissingStage.Failed }) == true;
        if (!active || !_missingOffered || !IsShowing) { StopMissingTimer(); return; }
        if (_missingTimer is not null) return;
        _missingTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await LookAtMissingAsync());
        _missingTimer.Start();
    }

    private async Task LookAtMissingAsync()
    {
        if (Shell.Session is not { } session || !IsShowing) { StopMissingTimer(); return; }
        try { _missingStatus = await session.Client.TorLinkStatusAsync(session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (!ReferenceEquals(session, Shell.Session)) return;
        UpdateMissingViews();
        // Placed: the episodes take their place once Jellyfin has them (now, and again a little later: it reads the folder first).
        var placed = (_missingStatus.Missing ?? []).Where(x => !_missingPlaced.Contains(x.Id) && Placed(_missingStatus, x)).ToList();
        if (placed.Count > 0)
        {
            foreach (var x in placed) _missingPlaced.Add(x.Id);
            await ReloadEpisodesAsync(session);
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(session, Shell.Session)) _ = ReloadEpisodesAsync(session); }), TaskScheduler.Default);
        }
        WatchMissing();
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
        }
        catch (Exception ex) when (Errors.Expected(ex)) { }
    }

    private void UpdateMissingViews()
    {
        foreach (var (season, episodes, _, show) in _missingViews.ToList()) show(MissingDownloads.Progress(_missingStatus, _item.Id, season, episodes));
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
        Show(MissingDownloads.Progress(_missingStatus, _item.Id, season, numbers));
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
        Show(MissingDownloads.Progress(_missingStatus, _item.Id, season, null));
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
    private static Control MissingProgressView(MissingProgress progress, Action<Button> retry, bool bar = false)
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
        public string? Shown { get; set; }
        public bool Open { get; set; }
    }

    private void OpenMissingMenu(Button anchor, MissingAsk ask, string heading)
    {
        _missingSearch?.Cancel();
        _missingFlyout?.Hide();
        var body = new Border { Width = 440, Padding = new Thickness(10, 8) };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard, Content = body };
        var menu = new MissingMenu { Ask = ask, Heading = heading, Body = body, Flyout = flyout, Open = true };
        flyout.Closed += (_, _) => { menu.Open = false; if (ReferenceEquals(_missingFlyout, flyout)) { _missingSearch?.Cancel(); _missingFlyout = null; } };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; anchor.Focus(); } };
        _missingFlyout = flyout; _missingMenu = menu;
        flyout.ShowAt(anchor);
        _ = SearchMissingAsync(menu);
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
        catch (Exception ex) when (Errors.Expected(ex)) { menu.Error = Errors.Friendly(ex); }
        if (menu.Open) DrawMissingMenu(menu);
    }

    private void DrawMissingMenu(MissingMenu menu)
    {
        var stack = new StackPanel();
        var close = Ui.IconButton("close", L("Fermer", "Close"), () => menu.Flyout.Hide(), "icon", 13);
        close.Width = close.Height = 28;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = menu.Heading, FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        var picks = menu.Picks;
        if (menu.Error is { } error && picks is null or { Empty: true })
        {
            stack.Children.Add(new TextBlock { Text = error, Foreground = Ui.Brush("#FF6B6B"), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 });
            var again = new Button { Classes = { "quiet" }, Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-12, 8, 0, 0) };
            again.Click += (_, _) => _ = SearchMissingAsync(menu);
            stack.Children.Add(again);
        }
        else if (picks is null)
        {
            stack.Children.Add(new TextBlock { Text = MissingDownloads.Searching, FontSize = 13, Foreground = Ui.Brush("#E0E0E3") });
            for (var i = 0; i < 3; i++)
            {
                var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Opacity = 1 - i * 0.25 };
                row.Children.Add(new Border { Height = 11, Width = 240 - i * 30, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Ui.Brush("#2A2A2A") });
                row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Ui.Brush("#2A2A2A"), Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
                stack.Children.Add(row);
            }
            AutomationProperties.SetLiveSetting(stack, AutomationLiveSetting.Polite);
        }
        else if (picks.Empty) stack.Children.Add(MissingNothing(menu));
        else
        {
            var list = new StackPanel();
            if (menu.Pack is not null) list.Children.Add(MissingPackRow(menu));
            else foreach (var choice in picks.Episodes) list.Children.Add(MissingEpisodeRow(menu, choice));
            stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (menu.Pack is null && MissingDownloads.NotFound(picks.NotFound) is { Length: > 0 } nothing)
                stack.Children.Add(new TextBlock { Text = nothing, FontSize = 12.5, Foreground = Ui.Muted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new Border { Height = 1, Background = Ui.Brush("#2A2A2A"), Margin = new Thickness(0, 12) });
            var foot = new DockPanel();
            var go = menu.Go = new Button { Classes = { "primary" }, Padding = new Thickness(18, 8), CornerRadius = new CornerRadius(17) };
            go.Click += async (_, _) => await SendMissingAsync(menu);
            DockPanel.SetDock(go, Dock.Right); foot.Children.Add(go);
            foot.Children.Add(menu.Total = new TextBlock { FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            stack.Children.Add(foot);
            DrawMissingFoot(menu);
            if (menu.Error is { } refused) stack.Children.Add(new TextBlock { Text = refused, Foreground = Ui.Brush("#FF6B6B"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) });
        }
        stack.Children.Add(new TextBlock { Text = MissingDownloads.Privacy, FontSize = 11.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
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
        var line = new DockPanel();
        var options = new[] { choice.Best }.Concat(choice.Others).ToList();
        var others = new StackPanel { IsVisible = false, Margin = new Thickness(28, 0, 0, 4) };
        if (options.Count > 1)
        {
            var arrow = new Icon("down", 11) { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("#C7C7CC") };
            var change = new Button { Classes = { "quiet" }, Padding = new Thickness(8, 4), VerticalAlignment = VerticalAlignment.Center, Content = Ui.Row(5, new TextBlock { Text = MissingDownloads.Change, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Ui.Brush("#C7C7CC") }, arrow) };
            AutomationProperties.SetName(change, $"{MissingDownloads.Change} {MissingDownloads.EpisodeLabel(choice.Episode)}");
            change.Click += (_, _) => { others.IsVisible = !others.IsVisible; arrow.Kind = others.IsVisible ? "up" : "down"; };
            DockPanel.SetDock(change, Dock.Right); line.Children.Add(change);
        }
        var check = new CheckBox { IsChecked = menu.Chosen.GetValueOrDefault(choice.Episode) is not null, Content = MissingOptionText(current, MissingDownloads.EpisodeLabel(choice.Episode)), Margin = new Thickness(0, 2) };
        AutomationProperties.SetName(check, Name(current));
        check.IsCheckedChanged += (_, _) => { menu.Chosen[choice.Episode] = check.IsChecked == true ? current : null; DrawMissingFoot(menu); };
        line.Children.Add(check);
        row.Children.Add(line);
        foreach (var option in options)
        {
            var pick = new RadioButton { GroupName = $"missing-{choice.Episode}", IsChecked = ReferenceEquals(option, current), Content = MissingOptionText(option, null) };
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

    /// <summary>The season's pack put forward; « Other choices (6) » for the others.</summary>
    private Control MissingPackRow(MissingMenu menu)
    {
        var picks = menu.Picks!; var current = menu.Pack!;
        var row = new StackPanel();
        var shown = new Border { Padding = new Thickness(0, 6, 0, 8), Child = MissingOptionText(current, ReleaseRanking.EpisodeLabel(current.Entry.Release)) };
        row.Children.Add(shown);
        if (picks.Packs.Count > 1)
        {
            var others = new StackPanel { IsVisible = false };
            var toggle = new Button { Classes = { "quiet" }, Content = MissingDownloads.OtherChoices(picks.Packs.Count - 1), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4), Margin = new Thickness(-10, 0, 0, 0), FontSize = 12.5 };
            toggle.Click += (_, _) => others.IsVisible = !others.IsVisible;
            foreach (var option in picks.Packs)
            {
                var pick = new RadioButton { GroupName = "missing-pack", IsChecked = ReferenceEquals(option, current), Content = MissingOptionText(option, null) };
                pick.IsCheckedChanged += (_, _) =>
                {
                    if (pick.IsChecked != true) return;
                    menu.Pack = option; shown.Child = MissingOptionText(option, ReleaseRanking.EpisodeLabel(option.Entry.Release)); DrawMissingFoot(menu);
                };
                others.Children.Add(pick);
            }
            row.Children.Add(toggle); row.Children.Add(others);
        }
        // Episode by episode instead, when some were found one by one.
        if (picks.Episodes.Count > 0)
        {
            var single = new Button { Classes = { "quiet" }, Content = L($"Plutôt épisode par épisode ({picks.Episodes.Count})", $"Episode by episode instead ({picks.Episodes.Count})"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4), Margin = new Thickness(-10, 0, 0, 0), FontSize = 12.5 };
            single.Click += (_, _) => { menu.Pack = null; DrawMissingMenu(menu); };
            row.Children.Add(single);
        }
        return row;
    }

    /// <summary>« É6 · 1080p · WEB-DL · MULTI », then « 2,1 Go · 120 sources · 1337x » under it.</summary>
    private static Control MissingOptionText(MissingOption option, string? label)
    {
        var (quality, detail) = MissingDownloads.Option(option);
        var first = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        if (label is not null) first.Inlines!.Add(new Avalonia.Controls.Documents.Run(label + "  ") { FontWeight = FontWeight.Bold });
        first.Inlines!.Add(new Avalonia.Controls.Documents.Run(quality));
        var text = Ui.Column(2, first, new TextBlock { Text = detail, FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        ToolTip.SetTip(text, option.Entry.Hit.Name);
        return text;
    }

    /// <summary>The foot after a check or a release changed: « 5,6 Go · into Arcane › Season 02 » and « Download 3 ».</summary>
    private void DrawMissingFoot(MissingMenu menu)
    {
        var chosen = menu.Pack is { } pack ? [pack] : menu.Chosen.Values.OfType<MissingOption>().ToList();
        if (menu.Total is { } total) total.Text = MissingDownloads.Footer(chosen.Sum(x => x.Entry.Hit.Size), _item.Name, menu.Ask.Season);
        if (menu.Go is { } go)
        {
            var label = menu.Sending ? L("Envoi…", "Sending…") : MissingDownloads.Download(chosen.Count, menu.Pack is not null);
            go.Content = label; AutomationProperties.SetName(go, label);
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
