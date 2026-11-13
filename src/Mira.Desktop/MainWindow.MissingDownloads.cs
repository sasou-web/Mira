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
    // The requests already seen placed: each reloads the episodes once.
    private readonly HashSet<string> _missingPlaced = [];

    /// <summary>Whether this series' page offers the buttons: an administrator, and a Mira on the PC of Jellyfin that places them.</summary>
    private async Task ShowMissingDownloadsAsync(MediaItem item)
    {
        if (_missingFor != item.Id) { _missingFor = item.Id; _missingOffered = false; _missingStatus = null; _missingPlaced.Clear(); StopMissingTimer(); }
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
        foreach (var placed in status.Missing ?? []) if (MissingDownloads.Progress(status with { Missing = [placed] }, item.Id, placed.Season, null) is { Stage: MissingStage.Placed }) _missingPlaced.Add(placed.Id);
        if (offered != _missingOffered) { _missingOffered = offered; if (_episodesRead) { FillSeasons(); RenderEpisodes(); } }
        else UpdateMissingViews();
        WatchMissing();
    }

    private void StopMissingTimer() { _missingTimer?.Stop(); _missingTimer = null; }

    /// <summary>Looks at the relay every 3 s while something asked from this page is on its way.</summary>
    private void WatchMissing()
    {
        var active = _missingStatus?.Missing?.Any(x => _missingFor is { } id && MissingDownloads.Progress(_missingStatus with { Missing = [x] }, id, x.Season, null) is { Stage: not MissingStage.Placed and not MissingStage.Failed }) == true;
        if (!active || !_missingOffered) { StopMissingTimer(); return; }
        if (_missingTimer is not null) return;
        _missingTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await LookAtMissingAsync(), Dispatcher);
        _missingTimer.Start();
    }

    private async Task LookAtMissingAsync()
    {
        if (_client is not { } client || _detail is not { Type: "Series" } series || series.Id != _missingFor || DetailOverlay.Visibility != Visibility.Visible) { StopMissingTimer(); return; }
        try { _missingStatus = await client.TorLinkStatusAsync(_connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (_detail?.Id != series.Id) return;
        UpdateMissingViews();
        // Placed: the episodes take their place once Jellyfin has them (now, and again a little later: it reads the folder first).
        var placed = (_missingStatus.Missing ?? []).Where(x => !_missingPlaced.Contains(x.Id) && MissingDownloads.Progress(_missingStatus with { Missing = [x] }, series.Id, x.Season, null) is { Stage: MissingStage.Placed }).ToList();
        if (placed.Count > 0)
        {
            foreach (var x in placed) _missingPlaced.Add(x.Id);
            await ReloadSeriesEpisodesAsync(series);
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => Dispatcher.InvokeAsync(() => _detail?.Id == series.Id ? ReloadSeriesEpisodesAsync(series) : Task.CompletedTask), TaskScheduler.Default);
        }
        WatchMissing();
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
        foreach (var (season, episodes, _, show) in _missingViews.ToList()) show(MissingDownloads.Progress(_missingStatus, id, season, episodes));
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
        Show(MissingDownloads.Progress(_missingStatus, series.Id, season, numbers));
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
        Show(MissingDownloads.Progress(_missingStatus, series.Id, season, null));
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
            Background = Palette.Get("Menu"), BorderBrush = Palette.Get("Hairline"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Width = 440, Margin = new Thickness(0, 8, 0, 0),
        };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var menu = new MissingMenu { Series = series, Ask = ask, Heading = heading, Body = body };
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.Closed += (_, _) => { if (ReferenceEquals(_missingPopup, popup)) { _missingSearch?.Cancel(); _missingPopup = null; } };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; anchor.Focus(); } };
        _missingPopup = popup;
        popup.IsOpen = true;
        _ = SearchMissingAsync(menu, popup);
    }

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
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(6), Content = new Icon { Kind = "close", Width = 13, Height = 13 } };
        AutomationProperties.SetName(close, L("Fermer", "Close")); close.Click += (_, _) => popup.IsOpen = false;
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = menu.Heading, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);
        var picks = menu.Picks;
        if (menu.Error is { } error && picks is null or { Empty: true })
        {
            stack.Children.Add(new TextBlock { Text = error, Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 });
            var again = new Button { Style = (Style)FindResource("Quiet"), Content = MissingDownloads.Retry, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-12, 8, 0, 0) };
            again.Click += (_, _) => _ = SearchMissingAsync(menu, popup);
            stack.Children.Add(again);
        }
        else if (picks is null)
        {
            stack.Children.Add(new TextBlock { Text = MissingDownloads.Searching, FontSize = 13, Foreground = Palette.Get("TextSecondary") });
            for (var i = 0; i < 3; i++)
            {
                var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Opacity = 1 - i * 0.25 };
                row.Children.Add(new Border { Height = 11, Width = 240 - i * 30, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Palette.Get("Raised2") });
                row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Palette.Get("Raised2"), Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
                stack.Children.Add(row);
            }
            AutomationProperties.SetLiveSetting(stack, AutomationLiveSetting.Polite);
        }
        else if (picks.Empty) stack.Children.Add(MissingNothing(menu, popup));
        else
        {
            var list = new StackPanel();
            if (menu.Pack is not null) list.Children.Add(MissingPackRow(menu, popup));
            else foreach (var choice in picks.Episodes) list.Children.Add(MissingEpisodeRow(menu, popup, choice));
            stack.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-10, 0, -10, 0) });
            if (menu.Pack is null && MissingDownloads.NotFound(picks.NotFound) is { Length: > 0 } nothing)
                stack.Children.Add(new TextBlock { Text = nothing, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new Border { Height = 1, Background = Palette.Get("Wash"), Margin = new Thickness(0, 12, 0, 12) });
            var foot = new DockPanel();
            var go = menu.Go = new Button { Style = (Style)FindResource("Primary"), Padding = new Thickness(18, 8, 18, 8) };
            Motion.SetRadius(go, new CornerRadius(17));
            go.Click += async (_, _) => await SendMissingAsync(menu, popup);
            DockPanel.SetDock(go, Dock.Right); foot.Children.Add(go);
            foot.Children.Add(menu.Total = new TextBlock { FontSize = 12.5, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) });
            stack.Children.Add(foot);
            DrawMissingFoot(menu);
            if (menu.Error is { } refused) stack.Children.Add(new TextBlock { Text = refused, Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0) });
        }
        stack.Children.Add(new TextBlock { Text = MissingDownloads.Privacy, FontSize = 11.5, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var shown = menu.Error is not null && picks is null or { Empty: true } ? "error" : picks is null ? "searching" : picks.Empty ? "nothing" : menu.Pack is null ? "episodes" : "pack";
        var moved = shown != menu.Shown && !menu.Sending; menu.Shown = shown;
        menu.Body.Child = stack;
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
