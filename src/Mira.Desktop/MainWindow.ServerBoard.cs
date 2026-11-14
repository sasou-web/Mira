using System.IO;
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

/// <summary>
/// The Downloads page as « Régie » lays it out (maquette A chosen by ogsas, 2026-11-14): at rest the whole width is the
/// server PC's, its queue in columns (picture, name and quality, bar, share, size, speed, time left, peers), then the series
/// followed in a table and the latest titles placed as posters, side by side (one under the other below ~1150 px); during a
/// search the results take the left and the queue goes tight on the right, the series and the titles folded under it.
/// Pictures only from Jellyfin: a series followed, a title placed, the series a page asked for; anything else a plain tile.
/// </summary>
public partial class MainWindow
{
    /// <summary>Below this width of the page, one column (« Régie » keeps both side by side above it).</summary>
    private const double ServerWideFrom = 1150;
    private bool _serverNarrow, _serverSearchShown, _serverPlacedAll, _serverFollowsOpen, _serverPlacedOpen;
    /// <summary>What the page last showed, drawn again when a picture arrives or the width changes.</summary>
    private RelayStatus? _serverStatus;
    /// <summary>The titles of Jellyfin whose poster the page shows, by id (null: Jellyfin has none, or no such title).</summary>
    private readonly Dictionary<string, MediaItem?> _serverPosters = new(StringComparer.OrdinalIgnoreCase);
    private bool _serverPostersAsking;
    /// <summary>What is left on the disks of this PC's libraries, read at most once a minute.</summary>
    private (DateTime At, long? Bytes) _mediaFree = (DateTime.MinValue, null);

    // ---- Rest and search ----------------------------------------------------------------------------

    /// <summary>A search shown (its results on the left, the queue tight on the right), or the page at rest.</summary>
    private void ShowServerSearch(bool on)
    {
        if (_serverSearchShown == on && (on ? ServerSearchView.Visibility : ServerRestScroll.Visibility) == Visibility.Visible) return;
        _serverSearchShown = on;
        ServerRestScroll.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ServerSearchView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }

    /// <summary>Back to the page at rest: the search is let go (one still running comes to nothing).</summary>
    private void LeaveServerSearch()
    {
        if (!_serverSearchShown) return;
        if (_serverSearching) { ++_serverSearchVersion; _serverSearching = false; ServerSearchButton.Content = L("Chercher", "Search"); ServerSearchButton.IsEnabled = _serverReady; }
        ServerResults.Children.Clear(); ServerResults.Opacity = 1; ServerSearchStatus.Text = "";
        ShowServerSearch(false);
    }

    private void ServerDownloads_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < ServerWideFrom;
        if (narrow == _serverNarrow && e.PreviousSize.Width > 0) { if (_serverStatus is { } same && !_serverPlacedAll) RenderServerPlaced(same); return; }
        _serverNarrow = narrow;
        // Side by side, or one under the other: the series followed, then the titles placed.
        ServerColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 40);
        ServerColumns.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(404);
        Grid.SetColumn(ServerPlacedSection, narrow ? 0 : 2); Grid.SetRow(ServerPlacedSection, narrow ? 1 : 0);
        // During a search, the queue above the results.
        ServerSearchView.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 36);
        ServerSearchView.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(428);
        Grid.SetColumn(ServerQueueScroll, narrow ? 0 : 2); Grid.SetRow(ServerQueueScroll, 0); Grid.SetRowSpan(ServerQueueScroll, narrow ? 1 : 2);
        ServerQueueScroll.MaxHeight = narrow ? 320 : double.PositiveInfinity; ServerQueueScroll.Margin = new Thickness(0, 0, 0, narrow ? 18 : 0);
        Grid.SetRow(ServerResultsColumn, narrow ? 1 : 0); Grid.SetRowSpan(ServerResultsColumn, narrow ? 1 : 2);
        // The magnet link and the .torrent under the search field rather than squeezed beside it.
        ServerSearchRule.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(ServerSearchTools, narrow ? 0 : 3); Grid.SetRow(ServerSearchTools, narrow ? 1 : 0); Grid.SetColumnSpan(ServerSearchTools, narrow ? 4 : 1);
        ServerSearchTools.Margin = new Thickness(narrow ? -4 : 0, narrow ? 10 : 0, 0, 0); ServerSearchTools.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        // The queue's columns change: its rows are made again.
        foreach (var key in _serverRows.Keys.Where(x => x.StartsWith("w:", StringComparison.Ordinal)).ToList()) _serverRows.Remove(key);
        if (_serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }

    private void ServerPlacedAll_Click(object sender, RoutedEventArgs e)
    {
        _serverPlacedAll = !_serverPlacedAll;
        if (_serverStatus is { } status) RenderServerPlaced(status);
    }
    private void ServerFollowsFold_Click(object sender, RoutedEventArgs e)
    {
        _serverFollowsOpen = !_serverFollowsOpen;
        if (_serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }
    private void ServerPlacedFold_Click(object sender, RoutedEventArgs e)
    {
        _serverPlacedOpen = !_serverPlacedOpen;
        if (_serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }

    // ---- The whole page from what the PC says ------------------------------------------------------------

    private void RenderServerStatus(RelayStatus status, DateTimeOffset now)
    {
        _serverFilled = true; _serverAgent = status.Agent; _serverStatus = status;
        DescribeServerAgent(now);
        var queue = RemoteBoard.Queue(status);
        var follows = RemoteBoard.Follows(status, now);
        var placed = PlacedOf(status);
        var summary = RemoteBoard.Summary(queue);
        ServerQueueSummary.Text = ServerSideSummary.Text = summary;
        // The rows of the page shown are kept up to date; those of the other are made when it shows.
        if (_serverSearchShown)
        {
            UpdateServerRows(ServerQueueCompact, [.. queue.Select(x => CompactLine(x, now))]);
            ServerQueueCompactEmpty.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServerFollowsFold.Visibility = follows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ServerFollowsFoldCount.Text = follows.Count.ToString(Lang.Culture); ServerFollowsFoldLine.Text = _serverFollowsOpen ? "" : RemoteBoard.Coming(status.Follows, now);
            ServerFollowsChevron.Kind = _serverFollowsOpen ? "down" : "next";
            ServerFollowsCompactCard.Visibility = _serverFollowsOpen && follows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateServerRows(ServerFollowsCompact, _serverFollowsOpen ? [.. follows.Select(x => FollowLine(x, compact: true))] : []);
            ServerPlacedFold.Visibility = placed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ServerPlacedFoldCount.Text = placed.Count.ToString(Lang.Culture); ServerPlacedFoldLine.Text = _serverPlacedOpen ? "" : RemoteBoard.Latest(placed);
            ServerPlacedChevron.Kind = _serverPlacedOpen ? "down" : "next";
            ServerPlacedCompact.Visibility = _serverPlacedOpen && placed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_serverPlacedOpen) FillPosters(ServerPlacedCompact, placed, placed.Count);
        }
        else
        {
            UpdateServerRows(ServerQueue, [.. queue.Select(x => WideLine(x, now))]);
            ServerQueueEmpty.Visibility = queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServerFollowsSection.Visibility = follows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ServerFollowsCount.Text = follows.Count.ToString(Lang.Culture);
            UpdateServerRows(ServerFollows, [.. follows.Select(x => FollowLine(x, compact: false))]);
            RenderServerPlaced(status);
            // One under the other: a gap between them only when both show.
            ServerPlacedSection.Margin = new Thickness(0, _serverNarrow && follows.Count > 0 ? 32 : 0, 0, 0);
            var any = follows.Count > 0 || ServerPlacedSection.Visibility == Visibility.Visible;
            ServerColumns.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ServerResultsEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }
        // Rows no longer shown are let go (those of the page hidden too: made again when it shows).
        foreach (var key in _serverRows.Keys.Except(_serverShownKeys).ToList()) _serverRows.Remove(key);
        _serverShownKeys.Clear();
        _serverQueued = queue.Count + follows.Count;
        _ = FetchServerPostersAsync(queue.Select(x => x.PosterId).Concat(follows.Select(x => x.Follow.SeriesId)).Concat(placed.Select(x => x.ItemId)));
    }

    /// <summary>The titles placed: the relay's, or on the PC of Jellyfin this PC's own placement journal.</summary>
    private List<RelayPlaced> PlacedOf(RelayStatus status) => _serverLocal ? _torlink is { } torlink ? TorLinkRemote.Placed(torlink.Importer.Entries) : [] : status.Placed;

    private void RenderServerPlaced(RelayStatus status)
    {
        var placed = PlacedOf(status);
        ServerPlacedSection.Visibility = placed.Count > 0 || (_serverLocal && ServerPlacedList.Children.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        ServerPlacedCount.Text = placed.Count.ToString(Lang.Culture);
        // As many as one row holds; « View all » the rest (on the PC of Jellyfin, TorLink's page's list, with its actions).
        var width = _serverNarrow ? Math.Max(0, ServerDownloadsPanel.ActualWidth - 8) : 404;
        var fit = Math.Max(1, (int)((width + 16) / 105));
        var more = placed.Count > fit || _serverLocal;
        ServerPlacedAll.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        ServerPlacedAll.Content = _serverPlacedAll ? L("Moins", "Show less") : L("Tout voir", "View all");
        AutomationProperties.SetName(ServerPlacedAll, _serverPlacedAll ? L("Montrer moins de titres rangés", "Show fewer titles sorted") : L("Voir tous les titres rangés", "View all the titles sorted"));
        var list = _serverLocal && _serverPlacedAll;
        ServerPlaced.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        ServerPlacedList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        if (!list) FillPosters(ServerPlaced, placed, _serverPlacedAll ? placed.Count : fit);
    }

    /// <summary>On the PC of Jellyfin, « Tout voir » shows TorLink's page's own list: its folder, a retry, another library.</summary>
    private void RenderLocalPlaced(TorLinkPlacement torlink)
    {
        ServerPlacedList.Children.Clear();
        foreach (var entry in torlink.Importer.Entries.Take(40)) ServerPlacedList.Children.Add(ImportRow(entry));
        foreach (var earlier in torlink.Earlier) ServerPlacedList.Children.Add(EarlierRow(earlier));
        if (_serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }

    // ---- Rows kept between reads ---------------------------------------------------------------------------

    /// <summary>A row of the page: its texts changed in place every 3 s (a button under the mouse stays put); made again when its shape changes.</summary>
    private sealed record ServerRow(string Shape, FrameworkElement View, TextBlock[] Texts, ScaleTransform? Bar, FrameworkElement? Rule);
    private sealed record ServerRowData(string Key, string Shape, string Name, string[] Texts, int? Progress, Func<ServerRow> Make);

    /// <summary>The keys of the rows drawn by this pass of <see cref="RenderServerStatus"/>; the others are let go after it.</summary>
    private readonly HashSet<string> _serverShownKeys = [];

    private void UpdateServerRows(Panel panel, List<ServerRowData> rows)
    {
        var next = new List<ServerRow>();
        foreach (var data in rows)
        {
            _serverShownKeys.Add(data.Key);
            if (!_serverRows.TryGetValue(data.Key, out var row) || row.Shape != data.Shape)
                _serverRows[data.Key] = row = data.Make() with { Shape = data.Shape };
            for (var i = 0; i < Math.Min(row.Texts.Length, data.Texts.Length); i++)
            {
                if (row.Texts[i].Text != data.Texts[i]) { row.Texts[i].Text = data.Texts[i]; if (row.Texts[i].Tag is "keep") row.Texts[i].ToolTip = data.Texts[i].Length > 0 ? data.Texts[i] : null; }
                // An empty cell takes no room (a time left unknown, a tight row's second line).
                row.Texts[i].Visibility = data.Texts[i].Length > 0 || row.Texts[i].Tag is "keep" ? Visibility.Visible : Visibility.Collapsed;
            }
            if (row.Bar is not null && data.Progress is { } progress) row.Bar.ScaleX = Math.Clamp(progress, 0, 100) / 100.0;
            AutomationProperties.SetName(row.View, string.Join(", ", new[] { data.Name }.Concat(data.Texts).Where(x => x.Length > 0)));
            next.Add(row);
        }
        // A hairline between rows, never above the first.
        for (var i = 0; i < next.Count; i++) if (next[i].Rule is { } rule) rule.Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (next.Count == panel.Children.Count && next.Select((x, i) => ReferenceEquals(x.View, panel.Children[i])).All(x => x)) return;
        panel.Children.Clear();
        foreach (var row in next) { if (row.View.Parent is Panel other) other.Children.Remove(row.View); panel.Children.Add(row.View); }
    }

    /// <summary>A row's frame: its content over a hairline at its top, shown from the second row on.</summary>
    private static (Grid Frame, Border Rule) RowFrame(FrameworkElement content, double height, Thickness padding)
    {
        var frame = new Grid { Background = Brushes.Transparent };
        var rule = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = Palette.Get("Raised"), Margin = new Thickness(12, 0, 12, 0), IsHitTestVisible = false };
        var body = new Border { Padding = padding, Child = content };
        if (height > 0) body.MinHeight = height;
        frame.Children.Add(body); frame.Children.Add(rule);
        return (frame, rule);
    }

    // ---- The queue -----------------------------------------------------------------------------------------

    private static TextBlock Cell(string text, double size = 13.5, string color = "TextBody", bool right = false, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Palette.Get(color), FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(18, 0, 0, 0),
        Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed
    };

    /// <summary>« Suzume (2022) », « Blue Eye Samurai · S01E04 », and « Followed » after it.</summary>
    private static FrameworkElement LineName(BoardLine line, double size)
    {
        var name = new TextBlock { FontSize = size, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = line.Full ?? line.Title };
        name.Inlines.Add(new Run(line.Title));
        if (line.Year is { } year) name.Inlines.Add(new Run($" ({year})") { Foreground = Palette.Get("Muted"), FontWeight = FontWeights.SemiBold });
        if (line.Episode.Length > 0) { name.Inlines.Add(new Run(" · ") { Foreground = Palette.Get("Faint") }); name.Inlines.Add(new Run(line.Episode)); }
        if (!line.Followed) return name;
        var row = new DockPanel { LastChildFill = true };
        var tag = FollowedTag(L("Suivie", "Followed"));
        DockPanel.SetDock(tag, Dock.Right); row.Children.Add(tag); row.Children.Add(name);
        // The tag stays right after the name, not at the far end of the column.
        row.HorizontalAlignment = HorizontalAlignment.Left;
        return row;
    }
    private static Border FollowedTag(string text) => new()
    {
        CornerRadius = new CornerRadius(6), Background = Palette.Get("Raised"), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Palette.Get("TextSecondary") }
    };

    /// <summary>A picture Jellyfin has (a poster), else a plain tile with the kind's icon.</summary>
    private FrameworkElement Thumb(string? id, bool episodic, double width, double height, double radius = 6)
    {
        var tile = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(radius), Background = Palette.Get("Raised"), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
        if (id is not null && _serverPosters.GetValueOrDefault(id) is { } item && _images is { } images)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            tile.Child = image;
            tile.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
            _ = SetAsync();
            async Task SetAsync() { if (await images.GetAsync(item, false, images.Pixels(width)) is { } bitmap) image.Source = bitmap; }
        }
        else
        {
            tile.BorderBrush = Palette.Get("Raised2"); tile.BorderThickness = new Thickness(1); tile.Background = Palette.Get("Surface");
            tile.Child = new Icon { Kind = episodic ? "tv" : "film", Width = Math.Min(18, width / 2), Height = Math.Min(18, width / 2), Foreground = Palette.Get("Faint"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        return tile;
    }
    private string PosterShape(string? id) => id is not null && _serverPosters.GetValueOrDefault(id) is not null ? "img" : "tile";

    private Button IconAction(string icon, string name, Func<Task> action)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Width = 34, Height = 34, ToolTip = name, Foreground = Palette.Get("TextSecondary"), Content = new Icon { Kind = icon, Width = 16, Height = 16 } };
        Motion.SetRadius(button, new CornerRadius(10));
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => await action();
        return button;
    }

    /// <summary>What a line can do from here: pause or resume, cancel (asked once more), remove a refused one.</summary>
    private List<Button> LineActions(BoardLine line, RelayStatus? status)
    {
        var name = line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title;
        var buttons = new List<Button>();
        if (line.Download is { } download)
        {
            var pending = status?.Controls.Any(x => x.Target == download.Id) == true;
            if (pending || !RemoteLines.Controllable(download)) return buttons;
            if (download.Status != "failed")
                buttons.Add(RemoteLines.Paused(download) ? IconAction("play", L($"Reprendre « {name} »", $"Resume “{name}”"), () => ControlServerAsync(download, "resume"))
                    : IconAction("pause", L($"Mettre en pause « {name} »", $"Pause “{name}”"), () => ControlServerAsync(download, "pause")));
            buttons.Add(IconAction("close", L($"Annuler « {name} »", $"Cancel “{name}”"), () => ConfirmServerCancelAsync(download, name)));
        }
        else if (line.Request is { State: not "claimed" } request)
        {
            var label = request.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            buttons.Add(IconAction("close", L($"{label} « {name} »", $"{label} “{name}”"), () => CancelServerRequestAsync(request)));
        }
        else if (line.Pick is { State: "waiting" or "failed" } pick)
        {
            var label = pick.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            buttons.Add(IconAction("close", L($"{label} « {name} »", $"{label} “{name}”"), () => CancelPickAsync(pick)));
        }
        return buttons;
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more.</summary>
    private async Task ConfirmServerCancelAsync(RelayDownload download, string name)
    {
        if (await ConfirmAsync(L($"Annuler « {name} » ?", $"Cancel “{name}”?"),
            _serverLocal ? L("TorLink l’arrête et supprime ce qu’il en a déjà reçu.", "TorLink stops it and deletes what it has already received.") : L("TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", "TorLink stops it and deletes what it has already received on the server PC."),
            L("Annuler le téléchargement", "Cancel the download"), L("Garder", "Keep")))
            await ControlServerAsync(download, "delete");
    }

    private static string LineShape(BoardLine line, RelayStatus? status) => string.Join("|", line.Download is { } d ? $"d{d.Status}{status?.Controls.Any(x => x.Target == d.Id)}" : line.Request is { } r ? $"r{r.State}" : $"p{line.Pick?.State}");

    /// <summary>The wide row at rest: picture, name and quality, then the bar and the columns (or what it waits for), then its buttons.</summary>
    private ServerRowData WideLine(BoardLine line, DateTimeOffset now)
    {
        var status = _serverStatus;
        var download = line.Download;
        var waiting = RemoteBoard.Waiting(line, now) ?? (download is not null && status?.Controls.FirstOrDefault(x => x.Target == download.Id) is { } pending ? RemoteLines.ActionLine(pending.Action) : null);
        var columns = download is not null && waiting is null ? RemoteBoard.Columns(download) : (Amount: "", Rate: "", Left: "", Peers: "");
        string[] texts = waiting is not null ? [waiting] : [RemoteBoard.Percent(download!), columns.Amount, columns.Rate, columns.Left, columns.Peers];
        var shape = $"{LineShape(line, status)}|{waiting is null}|{PosterShape(line.PosterId)}|{_serverNarrow}";
        return new("w:" + line.Key, shape, line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title, texts, download?.Progress, () => MakeWideLine(line, waiting is not null, status));
    }

    private ServerRow MakeWideLine(BoardLine line, bool waits, RelayStatus? status)
    {
        var grid = new Grid();
        void Column(GridLength width) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        Column(new GridLength(36)); Column(new GridLength(1, GridUnitType.Star)); grid.ColumnDefinitions[1].MaxWidth = 290 + 18;
        var narrow = _serverNarrow;
        // Bar, share, size, speed, time left, peers; the size, time left and peers left out when narrow.
        foreach (var width in narrow ? new double[] { -1, 64, 0, 120, 0, 0 } : [-1, 64, 168, 102, 102, 86]) Column(width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width));
        Column(new GridLength(90));
        grid.Children.Add(Thumb(line.PosterId, line.Episodic, 36, 54));
        var text = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(LineName(line, 15));
        text.Children.Add(new TextBlock { Text = line.Quality.Length > 0 ? line.Quality : line.Full ?? "", FontSize = 12.5, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = line.Full });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        TextBlock[] texts; ScaleTransform? bar = null;
        if (waits)
        {
            var failed = line.Download?.Status == "failed" || line.Request?.State == "failed" || line.Pick?.State == "failed";
            var done = line.Download?.Status is "completed" or "seeding";
            var row = new DockPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Icon { Kind = failed ? "warning" : done ? "check" : "clock", Width = 16, Height = 16, Foreground = Palette.Get(failed ? "Warning" : done ? "Success" : "Faint"), Margin = new Thickness(0, 0, 9, 0) });
            var said = new TextBlock { FontSize = 13.5, Foreground = Palette.Get(failed ? "WarningText" : "TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Tag = "keep" };
            row.Children.Add(said);
            Grid.SetColumn(row, 2); Grid.SetColumnSpan(row, 6); grid.Children.Add(row);
            texts = [said];
        }
        else
        {
            var paused = line.Download is { } download && download.Status != "downloading";
            bar = new ScaleTransform(Math.Clamp(line.Download!.Progress, 0, 100) / 100.0, 1);
            var track = new Border
            {
                Height = 4, Margin = new Thickness(18, 0, 0, 0), Background = Palette.Get("Line"), CornerRadius = new CornerRadius(2), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center,
                Child = new Border { Background = Palette.Get(paused ? "Faint" : "Accent"), CornerRadius = new CornerRadius(2), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = bar }
            };
            Grid.SetColumn(track, 2); grid.Children.Add(track);
            var cells = new[] { Cell("", 15, "Text", right: true, FontWeights.Bold), Cell(""), Cell("", right: true), Cell("", right: true), Cell("", right: true) };
            cells[0].Tag = "keep";
            var at = new[] { 3, 4, 5, 6, 7 };
            for (var i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], at[i]); grid.Children.Add(cells[i]); }
            // Narrow: the size, the time left and the peers have no column (their words are in its name for a reader).
            if (narrow) cells[1].Visibility = cells[3].Visibility = cells[4].Visibility = Visibility.Collapsed;
            if (paused) cells[2].Foreground = Palette.Get("Muted");
            texts = narrow ? [cells[0], new TextBlock(), cells[2], new TextBlock(), new TextBlock()] : cells;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in LineActions(line, status)) actions.Children.Add(button);
        Grid.SetColumn(actions, 8); grid.Children.Add(actions);
        var (frame, rule) = RowFrame(grid, 70, new Thickness(12, 8, 8, 8));
        return new("", frame, texts, bar, rule);
    }

    /// <summary>The tight row beside a search: picture, name, bar and one line of state.</summary>
    private ServerRowData CompactLine(BoardLine line, DateTimeOffset now)
    {
        var status = _serverStatus;
        var download = line.Download;
        var said = RemoteBoard.Waiting(line, now) ?? (download is not null && status?.Controls.FirstOrDefault(x => x.Target == download.Id) is { } pending ? RemoteLines.ActionLine(pending.Action) : null) ?? RemoteLines.Download(download!);
        var bar = download is not null && download.Status is "downloading" or "queued" or "paused" or "stopped";
        var shape = $"{LineShape(line, status)}|{bar}|{PosterShape(line.PosterId)}";
        return new("c:" + line.Key, shape, line.Episode.Length > 0 ? $"{line.Title} · {line.Episode}" : line.Title, [said], bar ? download!.Progress : null, () => MakeCompactLine(line, bar, status));
    }

    private ServerRow MakeCompactLine(BoardLine line, bool withBar, RelayStatus? status)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Thumb(line.PosterId, line.Episodic, 36, 54));
        var text = new StackPanel { Margin = new Thickness(13, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(LineName(line, 14));
        ScaleTransform? bar = null;
        if (withBar)
        {
            bar = new ScaleTransform(Math.Clamp(line.Download!.Progress, 0, 100) / 100.0, 1);
            text.Children.Add(new Border
            {
                Height = 4, Margin = new Thickness(0, 8, 0, 6), Background = Palette.Get("Line"), CornerRadius = new CornerRadius(2), ClipToBounds = true,
                Child = new Border { Background = Palette.Get(line.Download.Status == "downloading" ? "Accent" : "Faint"), CornerRadius = new CornerRadius(2), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = bar }
            });
        }
        var failed = line.Download?.Status == "failed" || line.Request?.State == "failed" || line.Pick?.State == "failed";
        var said = new TextBlock { FontSize = 12, Foreground = Palette.Get(failed ? "WarningText" : "Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, withBar ? 0 : 3, 0, 0), Tag = "keep" };
        text.Children.Add(said);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in LineActions(line, status)) actions.Children.Add(button);
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        var (frame, rule) = RowFrame(grid, 0, new Thickness(12, 11, 8, 11));
        return new("", frame, [said], bar, rule);
    }

    // ---- The series followed ---------------------------------------------------------------------------------

    private ServerRowData FollowLine(BoardFollow row, bool compact)
    {
        var follow = row.Follow;
        string[] texts = compact ? [row.Line, string.Join(" · ", new[] { row.NextEpisode, row.Airs, L($"prochaine recherche {row.NextSearch}", $"next search {row.NextSearch}") }.Where(x => x.Length > 0))]
            : [row.Line, row.NextEpisode, row.Airs, row.NextSearch];
        var shape = $"{follow.Query}|{row.Live}|{row.Warning}|{PosterShape(follow.SeriesId)}|{compact}";
        return new((compact ? "c:f:" : "f:") + follow.Id, shape, follow.Name, texts, null, () => MakeFollowLine(row, compact));
    }

    private ServerRow MakeFollowLine(BoardFollow row, bool compact)
    {
        var follow = row.Follow;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 45 : 50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (!compact) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) }); }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        var thumb = Thumb(follow.SeriesId, true, 32, 48); thumb.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(thumb);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new Run(follow.Name));
        if (follow.Query is { Length: > 0 } query) name.Inlines.Add(new Run(L($"   cherchée sous « {query} »", $"   searched as “{query}”")) { FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Faint") });
        text.Children.Add(name);
        var line = new TextBlock { FontSize = 12.5, Foreground = Palette.Get(row.Warning ? "WarningText" : row.Live ? "TextBody" : "Muted"), FontWeight = row.Live ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = "keep" };
        text.Children.Add(line);
        TextBlock[] texts;
        if (compact)
        {
            var next = new TextBlock { FontSize = 12, Foreground = Palette.Get("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0), Tag = "keep" };
            text.Children.Add(next);
            texts = [line, next];
        }
        else
        {
            var episode = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var number = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") };
            var airs = new TextBlock { FontSize = 12, Foreground = Palette.Get("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, Tag = "keep" };
            episode.Children.Add(number); episode.Children.Add(airs);
            Grid.SetColumn(episode, 2); grid.Children.Add(episode);
            var search = new TextBlock { FontSize = 13.5, Foreground = Palette.Get("TextBody"), VerticalAlignment = VerticalAlignment.Center, Tag = "keep" };
            Grid.SetColumn(search, 3); grid.Children.Add(search);
            texts = [line, number, airs, search];
        }
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(IconAction("refresh", L($"Chercher maintenant les nouveaux épisodes de « {follow.Name} »", $"Search now for new episodes of “{follow.Name}”"), () => CheckFollowNowAsync(follow)));
        var edit = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 12, 12) };
        var more = IconAction("more", L($"Plus d’actions pour « {follow.Name} »", $"More for “{follow.Name}”"), () => Task.CompletedTask);
        more.Click += (_, _) => FollowMenu(more, follow, edit);
        actions.Children.Add(more);
        Grid.SetColumn(actions, compact ? 2 : 4); grid.Children.Add(actions);
        var outer = new StackPanel();
        outer.Children.Add(grid);
        var (frame, rule) = RowFrame(outer, compact ? 0 : 60, compact ? new Thickness(12, 10, 8, 10) : new Thickness(12, 6, 8, 6));
        outer.Children.Add(edit);
        return new("", frame, texts, null, rule);
    }

    /// <summary>The ⋯ of a series followed, in the « Écrin » menu: search now, the name searched, its page, stop following.</summary>
    private void FollowMenu(Button anchor, RelayFollow follow, StackPanel edit)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(MenuAction(L("Chercher maintenant", "Search now"), "refresh", () => CheckFollowNowAsync(follow)));
        menu.Items.Add(MenuAction(L("Nom cherché…", "Search name…"), "pencil", () => { EditFollowQuery(edit, follow); return Task.CompletedTask; }));
        if (!_demo) menu.Items.Add(MenuAction(L("Ouvrir la série", "Open the series"), "tv", () => OpenTorLinkItemAsync(follow.SeriesId)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction(L("Ne plus suivre…", "Stop following…"), "close", async () =>
        {
            if (await ConfirmAsync(L($"Ne plus suivre « {follow.Name} » ?", $"Stop following “{follow.Name}”?"),
                L("Le PC serveur ne cherchera plus ses nouveaux épisodes. Ceux déjà dans ta bibliothèque restent.", "The server PC will stop looking for its new episodes. Those already in your library stay."),
                L("Ne plus suivre", "Stop following"), L("Continuer à suivre", "Keep following")))
                await UnfollowAsync(follow);
        }));
        menu.IsOpen = true;
    }

    /// <summary>The name TorLink searches, changed under the row: empty for the series' own.</summary>
    private void EditFollowQuery(StackPanel edit, RelayFollow follow)
    {
        edit.Children.Clear();
        var box = new TextBox { Text = follow.Query ?? follow.Name, Margin = new Thickness(0, 8, 0, 0), MaxLength = RemoteDownloads.MaxQuery };
        AutomationProperties.SetName(box, L("Nom cherché", "Search name"));
        edit.Children.Add(new TextBlock { Text = L("Le nom sous lequel ses épisodes sortent, par exemple son nom japonais. Vide : son nom dans Jellyfin.", "The name its episodes come out under, its Japanese name for example. Empty: its name in Jellyfin."), FontSize = 12, LineHeight = 17, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        edit.Children.Add(box);
        var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        Task Save() { edit.Visibility = Visibility.Collapsed; return RenameFollowAsync(follow, SeriesFollowing.ChosenQuery(box.Text, follow.Name)); }
        box.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await Save(); }
            else if (e.Key == Key.Escape) { e.Handled = true; edit.Visibility = Visibility.Collapsed; }
        };
        buttons.Children.Add(Named(RowButton(L("Enregistrer", "Save"), "check", Save), L("Enregistrer le nom cherché", "Save the search name")));
        buttons.Children.Add(Named(RowButton(L("Annuler", "Cancel"), "close", () => { edit.Visibility = Visibility.Collapsed; return Task.CompletedTask; }), L("Garder le nom cherché", "Keep the search name")));
        edit.Children.Add(buttons);
        edit.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    // ---- The titles placed ------------------------------------------------------------------------------

    /// <summary>The latest titles placed as posters: a click opens the title Jellyfin has; the one Jellyfin is still adding faded.</summary>
    private void FillPosters(WrapPanel panel, IReadOnlyList<RelayPlaced> placed, int count)
    {
        // Drawn again only when something changed: every 3 s would flicker the pictures and lose the mouse.
        var drawn = string.Join("\n", placed.Take(count).Select(x => $"{x.Id}|{x.Title}|{x.Status}|{x.ItemId}|{x.Ok}|{PosterShape(x.ItemId)}"));
        if (panel.Tag is string before && before == drawn && panel.Children.Count > 0) return;
        panel.Tag = drawn;
        panel.Children.Clear();
        foreach (var title in placed.Take(count))
        {
            var (name, sub) = title.Title.Split(" · ", 2) is [var first, var rest] ? (first, rest) : (title.Title, "");
            var status = RemoteLines.Placed(title.Status);
            var adding = title.ItemId is null && title.Ok;
            if (!title.Ok || adding) sub = Sentence(status);
            var content = new StackPanel { Width = 89 };
            var poster = Thumb(title.ItemId, sub.Length > 0 && title.Ok && !adding, 89, 133, 9);
            if (adding || !title.Ok) poster.Opacity = 0.45;
            content.Children.Add(poster);
            content.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 36, LineHeight = 17, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 9, 0, 0) });
            if (sub.Length > 0) content.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Palette.Get(title.Ok ? "Muted" : "WarningText"), TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            var button = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(0), Margin = new Thickness(0, 0, 16, 16), Content = content, VerticalAlignment = VerticalAlignment.Top, Focusable = title.ItemId is not null && !_demo, IsHitTestVisible = title.ItemId is not null && !_demo };
            Motion.SetRadius(button, new CornerRadius(9));
            AutomationProperties.SetName(button, string.Join(", ", new[] { title.Title, status }.Where(x => x.Length > 0)));
            if (title.ItemId is { } id) button.Click += async (_, _) => await OpenTorLinkItemAsync(id);
            panel.Children.Add(button);
        }
    }

    // ---- Pictures from Jellyfin ------------------------------------------------------------------------

    /// <summary>The titles of Jellyfin the page shows a poster of, asked once each (in one request), then the page drawn again.</summary>
    private async Task FetchServerPostersAsync(IEnumerable<string?> ids)
    {
        if (_serverPostersAsking || _demo || _client is not { } client) return;
        var asked = ids.OfType<string>().Where(RemoteDownloads.IsItemId).Distinct(StringComparer.OrdinalIgnoreCase).Where(x => !_serverPosters.ContainsKey(x)).Take(100).ToList();
        if (asked.Count == 0) return;
        _serverPostersAsking = true;
        try
        {
            var found = await client.PostersAsync(asked, _connectionLifetime.Token);
            if (!ReferenceEquals(client, _client)) return;
            foreach (var id in asked) _serverPosters[id] = found.FirstOrDefault(x => string.Equals(x.Id.Replace("-", ""), id.Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (IsExpected(ex)) { foreach (var id in asked) _serverPosters[id] = null; }
        finally { _serverPostersAsking = false; }
        if (_serverScreen && _serverStatus is { } status) RenderServerStatus(status, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// What is left on the disks of this PC's libraries (Réglages → TorLink, else Jellyfin's): each disk once, at most once a
    /// minute; null when none answers. It goes to the relay with the agent's report, nowhere else.
    /// </summary>
    private async Task<long?> MediaFreeAsync()
    {
        if (DateTime.UtcNow - _mediaFree.At < TimeSpan.FromMinutes(1)) return _mediaFree.Bytes;
        _mediaFree = (DateTime.UtcNow, _mediaFree.Bytes);
        if (_torlink is not { } torlink) return null;
        var libraries = torlink.Libraries;
        var bytes = await Task.Run(() => RemoteBoard.FreeSpace([libraries.Movies, libraries.Series, libraries.Anime], Root, Free));
        _mediaFree = (DateTime.UtcNow, bytes);
        return bytes;
        static string? Root(string path)
        {
            try { return Path.GetPathRoot(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return null; }
        }
        static long? Free(string root)
        {
            try { var drive = new DriveInfo(root); return drive.IsReady ? drive.AvailableFreeSpace : null; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
