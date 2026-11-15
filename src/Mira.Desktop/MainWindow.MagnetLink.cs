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

// « Coller un lien magnet » (mock-up B « Écrin », chosen by ogsas on 2026-11-15, MagnetLinks of Core): a window under the
// button says what the link holds and puts forward what to download of it, Mira's proposal checked already. The link is
// read by the qBittorrent of the PC of Jellyfin (this PC's, or the server PC's through the relay): its files, how Mira will
// place them, what the library already has. « Choose the episodes… » opens a box per episode in the same window, grouped by
// season or by 50 numbers. Without qBittorrent there, the window proposes from the link's name alone: all of it.
public partial class MainWindow
{
    private sealed class LinkWindow
    {
        public required string Magnet { get; init; }
        public required string Hash { get; init; }
        public required Popup Popup { get; init; }
        public required Squircle Body { get; init; }
        public required FrameworkElement Anchor { get; init; }
        public required CancellationTokenSource Life { get; init; }
        /// <summary>reading, ready, name (read from the link's name alone), silent, failed, already.</summary>
        public string Stage { get; set; } = "reading";
        public string? Name { get; set; }
        public string? Error { get; set; }
        public LinkContents? Contents { get; set; }
        public LinkPlace? Place { get; set; }
        public bool ReadHere { get; set; }
        /// <summary>When the reading began, for the time left before « No one is sharing ».</summary>
        public DateTime ReadingSince { get; set; } = DateTime.UtcNow;
        // What the library has: null until Jellyfin answered (nothing said rather than a guess).
        public bool? InLibrary { get; set; }
        public string? LookedFor { get; set; }
        public MediaItem? Item { get; set; }
        public HashSet<(int Season, int Number)> Owned { get; set; } = [];
        public LinkOption Option { get; set; }
        public HashSet<(int, int)> Chosen { get; } = [];
        public bool ChoiceStarted { get; set; }
        public HashSet<string> Open { get; } = [];
        public LinkEpisode? From { get; set; }
        public bool Sending { get; set; }
        public bool Sent { get; set; }
        public string? Shown { get; set; }
        // Updated in place when an episode is clicked: the rest of the window, its scroll and its focus stay.
        public List<(LinkEpisode Episode, Button Cell)> Cells { get; } = [];
        public List<(LinkGroup Group, Button Box, TextBlock Count)> Boxes { get; } = [];
        public TextBlock? ChooseDetail { get; set; }
        public TextBlock? Where { get; set; }
        public Button? Go { get; set; }
        public TextBlock? GoLabel { get; set; }
        public ScrollViewer? Scroll { get; set; }
    }

    private LinkWindow? _link;

    /// <summary>« Paste a magnet link »: the link on the clipboard opens its window at once; otherwise the field to paste it in.</summary>
    private bool OpenClipboardLink()
    {
        string text;
        // The clipboard is read on this click (or Ctrl+V) only, never watched.
        try { text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
        if (RemoteDownloads.InfoHash(text) is null || text.Length > RemoteDownloads.MaxMagnet) return false;
        OpenLinkWindow(text);
        return true;
    }

    /// <summary>Ctrl+V anywhere on the page but a field: a magnet link on the clipboard opens its window.</summary>
    private void ServerDownloads_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox || !_serverScreen) return;
        if (OpenClipboardLink()) e.Handled = true;
    }

    private LinkWindow? OpenLinkWindow(string magnet, bool sample = false)
    {
        if (RemoteDownloads.InfoHash(magnet) is not { } hash) return null;
        CloseLinkWindow();
        CloseServerMagnet();
        UpdateLayout();
        var anchor = MagnetReturn();
        var body = new Squircle { Style = (Style)FindResource("PopoverWindow"), Width = MissingMenuWidth };
        TextElement.SetFontFamily(body, (FontFamily)FindResource("AppFont")); TextElement.SetForeground(body, Palette.Get("Text"));
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Custom, StaysOpen = false, AllowsTransparency = true, Child = body, PopupAnimation = PopupAnimation.None };
        popup.CustomPopupPlacementCallback = (size, _, _) => [new CustomPopupPlacement(MissingMenuPlace(anchor, size), PopupPrimaryAxis.None)];
        var link = _link = new LinkWindow { Magnet = magnet.Trim(), Hash = hash, Popup = popup, Body = body, Anchor = anchor, Life = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime.Token), Name = RemoteDownloads.DisplayName(magnet) };
        FitMissingMenu(anchor, body);
        // The window resized or the page scrolled under it: it follows its button, still inside Mira's window.
        void Refit(object? sender, EventArgs e) { if (!popup.IsOpen) return; FitMissingMenu(anchor, body); NudgeMissingMenu(popup); }
        ScrollChangedEventHandler scrolled = (s, e) => { if (e.OriginalSource is not ScrollViewer v || !ReferenceEquals(v, link.Scroll)) Refit(s, e); };
        SizeChanged += Refit;
        AddHandler(ScrollViewer.ScrollChangedEvent, scrolled);
        popup.Closed += (_, _) =>
        {
            SizeChanged -= Refit;
            RemoveHandler(ScrollViewer.ScrollChangedEvent, scrolled);
            link.Life.Cancel();
            // Read here and closed without downloading: out of qBittorrent at once.
            if (link.ReadHere && !link.Sent) { _linksRead.Remove(link.Hash); _ = ForgetLinkAsync(link.Hash); }
            else _linksRead.Remove(link.Hash);
            if (ReferenceEquals(_link, link)) _link = null;
        };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; anchor.Focus(); } };
        popup.IsOpen = true;
        // Already there: what it is doing, never read again.
        if (Already(link) is not null) link.Stage = "already";
        DrawLinkWindow(link);
        // The public gallery fills the window itself (fictional link, nothing asked).
        if (sample) return link;
        _ = LookUpLinkAsync(link);
        if (link.Stage == "reading") _ = ReadLinkAsync(link);
        return link;
    }

    private void CloseLinkWindow() { if (_link is { Popup.IsOpen: true } open) open.Popup.IsOpen = false; }

    /// <summary>The link's download among those of the PC (by its info hash), or null.</summary>
    private RelayDownload? Already(LinkWindow link) => _serverStatus?.Downloads.FirstOrDefault(x => string.Equals(x.Id, link.Hash, StringComparison.OrdinalIgnoreCase));

    /// <summary>The PC that reads and downloads, by name; null on this PC.</summary>
    private string? LinkPc => _serverLocal ? null : _serverAgent?.Name;

    /// <summary>What the link holds, read by qBittorrent here or on the server PC (through the relay); its name alone otherwise.</summary>
    private async Task ReadLinkAsync(LinkWindow link)
    {
        var ct = link.Life.Token;
        link.Stage = "reading"; link.Error = null; link.ReadingSince = DateTime.UtcNow; DrawLinkWindow(link);
        AgentLookOutcome? read = null;
        try
        {
            if (_serverLocal)
            {
                if (!QBittorrentInstalled() || _demo) { link.Stage = "name"; DrawLinkWindow(link); return; }
                link.ReadHere = true; _linksRead.Add(link.Hash);
                read = await LookWithQBittorrentAsync("", link.Magnet, ct);
            }
            else if (_client is { } client)
            {
                if (_serverAgent is not { Looks: true }) { link.Stage = "name"; DrawLinkWindow(link); return; }
                var id = await client.TorLinkLookAsync(link.Magnet, ct);
                // The PC comes every few seconds, then asks the link's peers for a minute at most.
                var until = DateTime.UtcNow + MagnetLinks.Patience + TimeSpan.FromSeconds(45);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    if (await client.TorLinkLookStateAsync(id, ct) is not { } state) { read = new("", "failed", null, null, null, L("Le PC serveur a oublié ce lien : réessaie.", "The server PC forgot this link: try again.")); break; }
                    if (state.State is "done" or "silent" or "failed") { read = new("", state.State, state.Name, state.Files, state.Place, state.Error); break; }
                    if (DateTime.UtcNow > until) { read = new("", "failed", null, null, null, L("Le PC serveur n’a pas répondu.", "The server PC didn’t answer.")); break; }
                }
            }
            else return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (IsExpected(ex)) { read = new("", "failed", null, null, null, ServerError(ex)); }
        if (ct.IsCancellationRequested || read is null) return;
        if (read.State == "done")
        {
            link.Name = read.Name ?? link.Name;
            link.Contents = MagnetLinks.Read(link.Name ?? "", read.Files ?? []);
            link.Place = read.Place;
            link.Stage = "ready";
            Propose(link);
            _ = LookUpLinkAsync(link);
        }
        else { link.Stage = read.State == "silent" ? "silent" : "failed"; link.Error = read.Error; }
        DrawLinkWindow(link);
    }

    /// <summary>One of the window's options picked by the person.</summary>
    private static void PickLinkOption(LinkWindow link, LinkContents read, LinkOption chosen)
    {
        link.Option = chosen; link.ChoiceStarted = true;
        // Opened the first time: what the library lacks, checked already.
        if (chosen == LinkOption.Choose && link.Chosen.Count == 0) foreach (var e in MagnetLinks.Lacking(read, link.Owned)) link.Chosen.Add(e.Key);
        if (chosen == LinkOption.Choose && link.Open.Count == 0 && MagnetLinks.Groups(read.Episodes) is { Count: > 1 } groups) link.Open.Add(groups.FirstOrDefault(g => MagnetLinks.State(g, link.Chosen, link.Owned) is LinkGroupState.Some or LinkGroupState.All)?.Label ?? groups[0].Label);
    }

    /// <summary>Mira's proposal checked: the episodes the library lacks, or all of it.</summary>
    private static void Propose(LinkWindow link)
    {
        if (link.Contents is not { } contents) return;
        if (!link.ChoiceStarted) link.Option = MagnetLinks.Options(contents, link.Owned).Suggested;
    }

    /// <summary>
    /// What the library has of the link's title (Jellyfin asked by name, as « Déjà dans ta bibliothèque » does), its poster,
    /// and the episodes it has. Asked again once the link's real name is read.
    /// </summary>
    private async Task LookUpLinkAsync(LinkWindow link)
    {
        if (_client is not { } client || _demo || link.Name is not { Length: > 0 } name || link.LookedFor == name) return;
        link.LookedFor = name;
        var ct = link.Life.Token;
        var group = ReleaseRanking.Group([new RelayHit(link.Hash, name, 0, 0, 0, "", null)]).FirstOrDefault();
        if (group is null) return;
        try
        {
            var items = await OwnedTitles.FindAsync(OwnedTitles.Lookups([group])[0].Title, [group], async text => (await client.OwnedAsync(text, ct)).Items);
            if (ct.IsCancellationRequested || link.LookedFor != name) return;
            var line = OwnedTitles.Line(group, items);
            link.InLibrary = line is not null;
            link.Item = line is null ? null : items.FirstOrDefault(x => x.Id == line.ItemId);
            HashSet<(int, int)> owned = [];
            if (link.Item is { Type: "Series" } series && !group.Release.Absolute)
                foreach (var episode in (await client.OwnedEpisodesAsync(series.Id, ct)).Items)
                    if (episode.ParentIndexNumber is { } season && episode.IndexNumber is { } first)
                        for (var n = first; n <= (episode.IndexNumberEnd ?? first); n++) owned.Add((season, n));
            if (ct.IsCancellationRequested || link.LookedFor != name) return;
            link.Owned = owned;
            if (link.Item is { } item) _serverPosters[item.Id] = item;
        }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        // Episodes of the library are never chosen.
        link.Chosen.RemoveWhere(k => link.Contents?.Episodes.FirstOrDefault(e => e.Key == k) is { } e && MagnetLinks.Owned(e, link.Owned));
        Propose(link);
        if (link.Popup.IsOpen) DrawLinkWindow(link);
    }

    // ---- Drawn --------------------------------------------------------------------------------------------------------

    private void DrawLinkWindow(LinkWindow link)
    {
        if (!link.Popup.IsOpen) return;
        var offset = link.Scroll?.VerticalOffset ?? 0;
        link.Cells.Clear(); link.Boxes.Clear(); link.ChooseDetail = null; link.Where = null; link.Go = null; link.GoLabel = null;
        var grid = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition(), new RowDefinition { Height = GridLength.Auto } } };
        grid.Children.Add(LinkHead(link));
        var middle = new StackPanel();
        var foot = new StackPanel();
        switch (link.Stage)
        {
            case "already": middle.Children.Add(LinkAlready(link)); foot.Children.Add(LinkAlreadyFoot(link)); break;
            case "reading": LinkReading(link, LinkPc, _serverLocal, middle); foot.Children.Add(LinkFoot(link, enabled: false)); break;
            case "silent" or "failed": LinkTrouble(link, LinkPc, _serverLocal, middle); foot.Children.Add(LinkTroubleFoot(link)); break;
            default: LinkProposals(link, middle); foot.Children.Add(LinkFoot(link, enabled: true)); break;
        }
        var scroll = link.Scroll = new ScrollViewer { Content = middle, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        // The list scrolls under the header and the foot, faded where it goes on.
        var fade = new Border
        {
            Height = 28, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Colors.Transparent, ((SolidColorBrush)Palette.Get("Surface")).Color, 90)
        };
        scroll.ScrollChanged += (_, _) => fade.Visibility = scroll.VerticalOffset < scroll.ScrollableHeight - 1 ? Visibility.Visible : Visibility.Collapsed;
        var area = new Grid(); area.Children.Add(scroll); area.Children.Add(fade);
        Grid.SetRow(area, 1); Grid.SetRow(foot, 2);
        grid.Children.Add(area); grid.Children.Add(foot);
        link.Body.Child = grid;
        RefreshLinkChoice(link);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!link.Popup.IsOpen) return;
            if (offset > 0) scroll.ScrollToVerticalOffset(offset);
            NudgeMissingMenu(link.Popup);
        }, DispatcherPriority.Loaded);
        // Into the window when what it shows changes (keyboard and screen readers start there), never while it is used.
        var shown = link.Stage + "|" + link.Option;
        var moved = shown != link.Shown && !link.Sending; link.Shown = shown;
        if (moved && link.Stage != "reading") _ = Dispatcher.BeginInvoke(() => grid.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Input);
    }

    /// <summary>Its poster (or the kind's icon), its title, badge and kind, the release's quality under them; the close button.</summary>
    private FrameworkElement LinkHead(LinkWindow link)
    {
        var head = new Grid { Margin = new Thickness(10, 10, 4, 6) };
        var row = new DockPanel { Margin = new Thickness(0, 0, 40, 0) };
        var release = ReleaseRanking.Parse(link.Name);
        var episodic = link.Contents?.Episodic ?? release.Episode is not null || release.Pack || release.Season is not null;
        var thumb = link.Name is null && link.Stage is "silent" or "failed" ? LinkIconTile("link") : Thumb(link.Item?.Id, episodic, 46, 66, 8);
        thumb.Margin = new Thickness(0, 0, 14, 0); thumb.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new WrapPanel();
        // The title as the library names it once Jellyfin has it (« Longue série », not « Longue Serie » of the release).
        var name = link.Item?.Name ?? (release.Title.Length > 0 ? release.Title : link.Name ?? MagnetLinks.Unnamed);
        var heading = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Palette.Get("Text"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        heading.Inlines.Add(new Run(name));
        if (!episodic && release.Year is { } year) heading.Inlines.Add(new Run($" ({year})") { FontWeight = FontWeights.Normal, Foreground = Palette.Get("TextSecondary") });
        title.Children.Add(heading);
        if (link.Name is not null && episodic && MagnetLinks.Badge(release) is { Length: > 0 } badge)
            title.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Palette.Get("Raised"), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = badge, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") } });
        if (link.Name is not null)
            title.Children.Add(new TextBlock { Text = MagnetLinks.KindName(link.Contents?.Kind ?? (episodic ? MediaKind.Series : MediaKind.Movie)), FontSize = 12.5, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center });
        words.Children.Add(title);
        var under = link.Name is null ? MagnetLinks.Short(link.Magnet, link.Hash) : ReleaseRanking.QualityLine(release);
        if (under.Length > 0) words.Children.Add(new TextBlock { Text = under, FontSize = 13, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = link.Name });
        row.Children.Add(words);
        head.Children.Add(row);
        var close = new Button { Style = (Style)FindResource("Quiet"), Width = 32, Height = 32, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, 0, 0), Foreground = Palette.Get("TextSecondary"), Content = new Icon { Kind = "close", Width = 15, Height = 15 } };
        Motion.SetRadius(close, new CornerRadius(10));
        AutomationProperties.SetName(close, L("Fermer", "Close")); close.Click += (_, _) => { link.Popup.IsOpen = false; link.Anchor.Focus(); };
        head.Children.Add(close);
        return head;
    }

    private static Border LinkIconTile(string icon) => new()
    {
        Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Background = Palette.Get("Surface"), BorderBrush = Palette.Get("Raised2"), BorderThickness = new Thickness(1),
        Child = new Icon { Kind = icon, Width = 18, Height = 18, Foreground = Palette.Get("Faint"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    /// <summary>« Reading what’s inside… », who is asked, the privacy line, and the lines to come.</summary>
    private static void LinkReading(LinkWindow link, string? pc, bool here, StackPanel middle)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 14, 0) };
        // The icon turns and the time left counts down, so the 60 s of a slow link never look stuck.
        var spin = new RotateTransform();
        line.Children.Add(new Icon { Kind = "refresh", Width = 15, Height = 15, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = spin });
        spin.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(Motion.Reduced ? 2400 : 1100)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        line.Children.Add(new TextBlock { Text = MagnetLinks.Reading, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var left = new TextBlock { Text = MagnetLinks.ReadingLeft(DateTime.UtcNow - link.ReadingSince), FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(left);
        var clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) =>
        {
            if (link.Stage != "reading" || !link.Popup.IsOpen || PresentationSource.FromVisual(left) is null) { clock.Stop(); return; }
            left.Text = MagnetLinks.ReadingLeft(DateTime.UtcNow - link.ReadingSince);
        };
        clock.Start();
        middle.Children.Add(line);
        middle.Children.Add(new TextBlock { Text = MagnetLinks.ReadingHow(pc, here), FontSize = 12.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(38, 3, 14, 0) });
        for (var i = 0; i < 3; i++)
        {
            var row = new StackPanel { Margin = new Thickness(14, 16, 14, 0), Opacity = 1 - i * 0.25 };
            row.Children.Add(new Border { Height = 11, Width = 260 - i * 40, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Palette.Get("Raised2") });
            row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Palette.Get("Raised2"), Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
            middle.Children.Add(row);
        }
        middle.Children.Add(new TextBlock { Text = MagnetLinks.Privacy(here), FontSize = 12, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 16, 14, 6) });
        AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Polite);
    }

    /// <summary>The library line, then the proposals; « Choose the episodes… » opens its boxes under it.</summary>
    private void LinkProposals(LinkWindow link, StackPanel middle)
    {
        if (link.Contents is { } contents && MagnetLinks.Library(contents, link.Owned, link.InLibrary) is { Length: > 0 } library)
        {
            var some = contents.Episodes.Any(e => MagnetLinks.Owned(e, link.Owned));
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 2, 14, 8) };
            line.Children.Add(new Icon { Kind = some ? "check" : "info", Width = 14, Height = 14, Foreground = Palette.Get(some ? "Success" : "Muted"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = library, FontSize = 13, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
            middle.Children.Add(line);
        }
        if (link.Contents is not { } read)
        {
            // Its name alone: all of it, said so.
            middle.Children.Add(LinkSummary(L("Tout le lien", "Everything in the link"), link.Name is { } n && ReleaseRanking.Readable(n).Full is not null ? ReleaseRanking.Readable(n).Title : null));
            middle.Children.Add(new TextBlock { Text = MagnetLinks.WholeOnly(_serverLocal), FontSize = 12.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 6, 14, 8) });
            return;
        }
        if (!read.Episodic)
        {
            if (read.Films.Count == 0) { middle.Children.Add(new TextBlock { Text = MagnetLinks.NoVideo, FontSize = 13, Foreground = Palette.Get("TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 8) }); return; }
            middle.Children.Add(LinkSummary(MagnetLinks.Title(LinkOption.Film, read, link.Owned), MagnetLinks.Detail(LinkOption.Film, read, link.Owned, link.Chosen)));
            if (read.Samples) middle.Children.Add(new TextBlock { Text = MagnetLinks.SampleOut, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(14, 3, 14, 8) });
            return;
        }
        var (options, suggested) = MagnetLinks.Options(read, link.Owned);
        foreach (var option in options)
        {
            var content = new DockPanel();
            if (option == suggested)
            {
                var tag = new TextBlock { Text = MagnetLinks.Suggested, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(tag, Dock.Right); content.Children.Add(tag);
            }
            if (option == LinkOption.Choose)
            {
                var chevron = new Icon { Kind = link.Option == LinkOption.Choose ? "down" : "next", Width = 13, Height = 13, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
                DockPanel.SetDock(chevron, Dock.Right); content.Children.Add(chevron);
            }
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = MagnetLinks.Title(option, read, link.Owned), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = option == LinkOption.Choose && link.Option != LinkOption.Choose ? MagnetLinks.ChooseFrom(read) : MagnetLinks.Detail(option, read, link.Owned, link.Chosen), FontSize = 12.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            if (option == LinkOption.Choose && link.Option == LinkOption.Choose) link.ChooseDetail = detail;
            text.Children.Add(detail);
            content.Children.Add(text);
            var pick = new RadioButton { Style = (Style)FindResource("PopoverRadio"), GroupName = "link-option", IsChecked = option == link.Option, Content = content };
            AutomationProperties.SetName(pick, MagnetLinks.Title(option, read, link.Owned));
            var chosen = option;
            pick.Checked += (_, _) =>
            {
                if (link.Option == chosen) return;
                PickLinkOption(link, read, chosen);
                DrawLinkWindow(link);
            };
            middle.Children.Add(pick);
            if (option == LinkOption.Choose && link.Option == LinkOption.Choose) middle.Children.Add(LinkEpisodes(link, read));
        }
    }

    /// <summary>« The film and its 2 subtitle files · 8.4 GB »: what goes when there is nothing to choose.</summary>
    private static FrameworkElement LinkSummary(string title, string? detail)
    {
        var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 0) };
        text.Inlines.Add(new Run(title) { FontWeight = FontWeights.Bold, Foreground = Palette.Get("Text") });
        if (detail is { Length: > 0 }) text.Inlines.Add(new Run(" · " + detail) { Foreground = Palette.Get("TextSecondary") });
        return text;
    }

    /// <summary>The boxes of « Choose the episodes… »: one grid, or groups that fold (a season each, or 50 numbers).</summary>
    private FrameworkElement LinkEpisodes(LinkWindow link, LinkContents contents)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
        var groups = MagnetLinks.Groups(contents.Episodes);
        var numbered = MagnetLinks.Numbered(groups);
        var width = MagnetLinks.Digits(contents.Episodes.Max(e => e.Last ?? e.Number));
        // « Click a number, or Shift-click… » and « All · None », once there is more than a row to choose from.
        if (contents.Episodes.Count > 5)
        {
            var hint = new DockPanel { Margin = new Thickness(8, 2, 4, 8) };
            var all = new StackPanel { Orientation = Orientation.Horizontal };
            Button Word(string text, Action act)
            {
                var b = new Button { Style = (Style)FindResource("Quiet"), Content = text, Padding = new Thickness(6, 3, 6, 3), FontSize = 13, Foreground = Palette.Get("TextBody") };
                Motion.SetRadius(b, new CornerRadius(8));
                b.Click += (_, _) => { act(); RefreshLinkChoice(link); };
                return b;
            }
            all.Children.Add(Word(L("Tout", "All"), () => { foreach (var e in MagnetLinks.Lacking(contents, link.Owned)) link.Chosen.Add(e.Key); }));
            all.Children.Add(new TextBlock { Text = "·", Foreground = Palette.Get("Faint"), VerticalAlignment = VerticalAlignment.Center });
            all.Children.Add(Word(L("Rien", "None"), () => link.Chosen.Clear()));
            DockPanel.SetDock(all, Dock.Right); hint.Children.Add(all);
            hint.Children.Add(new TextBlock { Text = MagnetLinks.ShiftHint, FontSize = 12.5, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(hint);
        }
        if (groups is [{ Label: "" } only]) { panel.Children.Add(LinkCells(link, contents, only, numbered, width)); return panel; }
        foreach (var group in groups)
        {
            var open = link.Open.Contains(group.Label);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var box = new Button { Style = (Style)FindResource("Quiet"), Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            Motion.SetRadius(box, new CornerRadius(9));
            box.Click += (_, _) => { MagnetLinks.Toggle(link.Chosen, group, link.Owned); RefreshLinkChoice(link); };
            row.Children.Add(box);
            var header = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Motion.SetRadius(header, new CornerRadius(10));
            var line = new DockPanel();
            var chevron = new Icon { Kind = open ? "up" : "down", Width = 12, Height = 12, Foreground = Palette.Get("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(chevron, Dock.Right); line.Children.Add(chevron);
            var count = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Normal, Foreground = Palette.Get("Muted"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Dock.Right); line.Children.Add(count);
            var label = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            label.Inlines.Add(new Run(group.Label));
            label.Inlines.Add(new Run("   " + RemoteLines.Size(group.Episodes.Sum(e => e.Size))) { FontWeight = FontWeights.Normal, FontSize = 13, Foreground = Palette.Get("Faint") });
            line.Children.Add(label);
            header.Content = line;
            AutomationProperties.SetName(header, group.Label);
            header.Click += (_, _) =>
            {
                if (!link.Open.Remove(group.Label)) link.Open.Add(group.Label);
                DrawLinkWindow(link);
            };
            Grid.SetColumn(header, 1); row.Children.Add(header);
            link.Boxes.Add((group, box, count));
            panel.Children.Add(row);
            if (open) panel.Children.Add(LinkCells(link, contents, group, numbered, width, indent: 40));
        }
        return panel;
    }

    /// <summary>A box per episode: white when it goes, outlined when not, quiet with « in library » for one the library has.</summary>
    private FrameworkElement LinkCells(LinkWindow link, LinkContents contents, LinkGroup group, bool numbered, int width, double indent = 0)
    {
        var columns = numbered ? 10 : 5;
        var cells = new UniformGrid { Columns = columns, Margin = new Thickness(indent, 2, 0, 8) };
        foreach (var episode in group.Episodes)
        {
            var owned = MagnetLinks.Owned(episode, link.Owned);
            var label = MagnetLinks.Cell(episode, numbered, width);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            if (!numbered)
            {
                var under = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                if (owned) under.Children.Add(new Icon { Kind = "check", Width = 10, Height = 10, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                under.Children.Add(new TextBlock { Text = owned ? MagnetLinks.InLibrary : RemoteLines.Size(episode.Size), FontSize = 11.5, FontWeight = FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(under);
            }
            var cell = new Button { Content = content, Height = numbered ? 38 : 54, Margin = new Thickness(3), Padding = new Thickness(2), BorderThickness = new Thickness(1.2) };
            Motion.SetRadius(cell, new CornerRadius(10));
            cell.ToolTip = numbered ? (owned ? MagnetLinks.InLibrary : RemoteLines.Size(episode.Size)) : null;
            var name = owned ? $"{label}, {MagnetLinks.InLibrary}" : $"{label}, {RemoteLines.Size(episode.Size)}";
            AutomationProperties.SetName(cell, name);
            cell.Focusable = !owned;
            var e = episode;
            cell.Click += (_, _) =>
            {
                if (owned) return;
                var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                MagnetLinks.Click(link.Chosen, contents.Episodes, e, shift ? link.From : null, link.Owned);
                link.From = e;
                RefreshLinkChoice(link);
            };
            link.Cells.Add((episode, cell));
            cells.Children.Add(cell);
        }
        return cells;
    }

    /// <summary>The boxes, the groups' marks, « 4 chosen · 5.7 GB », the destination and Download, after a click.</summary>
    private void RefreshLinkChoice(LinkWindow link)
    {
        foreach (var (episode, cell) in link.Cells)
        {
            var owned = MagnetLinks.Owned(episode, link.Owned);
            var on = !owned && link.Chosen.Contains(episode.Key);
            cell.Background = on ? Palette.Get("Text") : Brushes.Transparent;
            cell.Foreground = on ? Palette.Get("OnAccent") : Palette.Get(owned ? "Faint" : "TextBody");
            cell.BorderBrush = on ? Palette.Get("Text") : Palette.Get(owned ? "Rule" : "LineStrong");
            cell.Cursor = owned ? Cursors.Arrow : Cursors.Hand;
            AutomationProperties.SetItemStatus(cell, owned ? "" : on ? L("choisi", "chosen") : L("pas choisi", "not chosen"));
        }
        foreach (var (group, box, count) in link.Boxes)
        {
            var state = MagnetLinks.State(group, link.Chosen, link.Owned);
            var mark = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5) };
            mark.Background = state is LinkGroupState.All or LinkGroupState.Some ? Palette.Get("Text") : Brushes.Transparent;
            mark.BorderBrush = state is LinkGroupState.None ? Palette.Get("Dim") : state is LinkGroupState.Owned ? Palette.Get("Rule") : Palette.Get("Text");
            if (state is LinkGroupState.All or LinkGroupState.Some) mark.Child = new Icon { Kind = state == LinkGroupState.All ? "check" : "minus", Width = 13, Height = 13, Foreground = Palette.Get("OnAccent") };
            if (state is LinkGroupState.Owned) mark.Child = new Icon { Kind = "check", Width = 12, Height = 12, Foreground = Palette.Get("Faint") };
            box.Content = mark;
            box.IsEnabled = state != LinkGroupState.Owned;
            var n = group.Episodes.Count(e => link.Chosen.Contains(e.Key) && !MagnetLinks.Owned(e, link.Owned));
            count.Text = state == LinkGroupState.Owned ? MagnetLinks.InLibrary : n > 0 ? L($"{n} {(n == 1 ? "choisi" : "choisis")}", $"{n} chosen") : "";
            AutomationProperties.SetName(box, $"{group.Label} : {(state == LinkGroupState.All ? L("tout choisi", "all chosen") : state == LinkGroupState.Some ? L("en partie", "some chosen") : state == LinkGroupState.Owned ? MagnetLinks.InLibrary : L("rien de choisi", "none chosen"))}");
        }
        if (link.ChooseDetail is { } detail && link.Contents is { } contents) detail.Text = MagnetLinks.Chosen(contents, link.Chosen);
        if (link.Where is { } where)
        {
            var taken = link.Contents is { Episodic: true } c ? MagnetLinks.Taken(link.Option, c, link.Owned, link.Chosen) : [];
            var (into, place) = MagnetLinks.Destination(link.Place, taken.Select(e => e.Season).ToList(), link.Contents?.Episodic == true);
            where.Inlines.Clear();
            where.Inlines.Add(new Run(into)); where.Inlines.Add(new Run(place) { FontWeight = FontWeights.Bold, Foreground = Palette.Get("Text") });
        }
        if (link.Go is { } go)
        {
            var nothing = link.Contents is { Episodic: true } read && link.Option == LinkOption.Choose && !read.Episodes.Any(e => link.Chosen.Contains(e.Key) && !MagnetLinks.Owned(e, link.Owned));
            var words = link.Sending ? L("Envoi…", "Sending…") : MagnetLinks.Download;
            if (link.GoLabel is { } label) label.Text = words;
            AutomationProperties.SetName(go, words);
            go.IsEnabled = link.Stage is "ready" or "name" && !nothing && !link.Sending;
        }
    }

    /// <summary>A line, where it goes and the room left on the PC, Download on the right.</summary>
    private FrameworkElement LinkFoot(LinkWindow link, bool enabled)
    {
        var foot = new StackPanel();
        foot.Children.Add(new Border { Style = (Style)FindResource("PlayerMenuRule"), Margin = new Thickness(6, 8, 6, 0) });
        var bar = new DockPanel { Margin = new Thickness(8, 12, 4, 4), LastChildFill = true };
        var go = link.Go = LinkPill("download", MagnetLinks.Download, out var label);
        link.GoLabel = label;
        go.IsEnabled = enabled;
        go.Click += async (_, _) => await SendLinkAsync(link, anyway: false);
        DockPanel.SetDock(go, Dock.Right); bar.Children.Add(go);
        var where = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) };
        where.Children.Add(link.Where = new TextBlock { FontSize = 13.5, Foreground = Palette.Get("TextBody"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (MagnetLinks.Free(_serverLocal ? _mediaFree.Bytes : _serverAgent?.Free, LinkPc) is { Length: > 0 } free)
            where.Children.Add(new TextBlock { Text = free, FontSize = 12, Foreground = Palette.Get("Faint"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        bar.Children.Add(where);
        foot.Children.Add(bar);
        if (link.Error is { } error && link.Stage is "ready" or "name") foot.Children.Add(new TextBlock { Text = error, Foreground = Palette.Get("Danger"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(14, 6, 14, 4) });
        return foot;
    }

    /// <summary>The white pill of the window: an icon and its words.</summary>
    private Button LinkPill(string icon, string text, out TextBlock label)
    {
        var go = new Button { Style = (Style)FindResource("Primary"), Height = 42, Padding = new Thickness(20, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        Motion.SetRadius(go, new CornerRadius(21));
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Kind = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(label = new TextBlock { Text = text, FontSize = 14.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        go.Content = content;
        AutomationProperties.SetName(go, text);
        return go;
    }

    private Button LinkQuiet(string text)
    {
        var button = new Button { Style = (Style)FindResource("Quiet"), Content = text, Padding = new Thickness(14, 9, 14, 9), FontSize = 13.5, Foreground = Palette.Get("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Motion.SetRadius(button, new CornerRadius(12));
        return button;
    }

    /// <summary>Nobody shares the link (or it could not be read): why, « Download anyway » and « Try again ».</summary>
    private static void LinkTrouble(LinkWindow link, string? pc, bool here, StackPanel middle)
    {
        var silent = link.Stage == "silent";
        var card = new Squircle { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(6, 4, 6, 4) };
        var line = new DockPanel();
        var icon = new Icon { Kind = "warning", Width = 15, Height = 15, Foreground = Palette.Get("Warning"), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = silent ? MagnetLinks.Silent : L("Mira n’a pas pu lire ce lien", "Mira couldn’t read this link"), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        words.Children.Add(new TextBlock { Text = silent ? MagnetLinks.SilentHow(pc, here) : link.Error ?? "", FontSize = 12.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        line.Children.Add(words);
        card.Child = line;
        middle.Children.Add(card);
        middle.Children.Add(new TextBlock { Text = MagnetLinks.AnywayHow, FontSize = 12, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 4) });
        AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Assertive);
    }

    private FrameworkElement LinkTroubleFoot(LinkWindow link)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 10, 4, 4) };
        var anyway = LinkQuiet(MagnetLinks.DownloadAnyway);
        anyway.Click += async (_, _) => await SendLinkAsync(link, anyway: true);
        var again = LinkPill("refresh", MagnetLinks.TryAgain, out _);
        again.Click += (_, _) => _ = ReadLinkAsync(link);
        bar.Children.Add(anyway); bar.Children.Add(again);
        return bar;
    }

    /// <summary>The link is downloading already: how far, « Show it in the list », « Resume » when it is paused.</summary>
    private FrameworkElement LinkAlready(LinkWindow link)
    {
        var download = Already(link);
        var card = new Squircle { Background = Palette.Get("Raised"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(6, 4, 6, 4) };
        var words = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new Icon { Kind = "info", Width = 14, Height = 14, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = _serverLocal ? L("Déjà sur ce PC", "Already on this PC") : MagnetLinks.Already, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        words.Children.Add(line);
        if (download is not null)
        {
            var size = download.Size > 0 ? L($" · {RemoteLines.Size(download.Downloaded)} sur {RemoteLines.Size(download.Size)}", $" · {RemoteLines.Size(download.Downloaded)} of {RemoteLines.Size(download.Size)}") : "";
            var state = download.Status switch
            {
                "paused" or "stopped" => L($"En pause à {download.Progress} %", $"Paused at {download.Progress}%"),
                "failed" => download.Error ?? L("En échec", "Failed"),
                "queued" => L("En attente", "Waiting"),
                _ => L($"{download.Progress} %", $"{download.Progress}%"),
            };
            words.Children.Add(new TextBlock { Text = state + size, FontSize = 12.5, Foreground = Palette.Get("Muted"), Margin = new Thickness(22, 3, 0, 10), TextWrapping = TextWrapping.Wrap });
            var track = new Grid { Height = 4, Margin = new Thickness(0, 0, 0, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(download.Progress, 0, 100), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - Math.Clamp(download.Progress, 0, 100), GridUnitType.Star) });
            var rail = new Border { CornerRadius = new CornerRadius(2), Background = Palette.Get("Raised2") };
            Grid.SetColumnSpan(rail, 2); track.Children.Add(rail);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Palette.Get("TextSecondary") });
            words.Children.Add(track);
        }
        card.Child = words;
        return card;
    }

    private FrameworkElement LinkAlreadyFoot(LinkWindow link)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 10, 4, 4) };
        var show = LinkQuiet(MagnetLinks.ShowInList);
        show.Click += (_, _) => { link.Popup.IsOpen = false; SmoothScroll.Jump(_serverSearchShown ? ServerQueueScroll : ServerRestScroll); };
        bar.Children.Add(show);
        if (Already(link) is { Status: "paused" or "stopped" })
        {
            var resume = LinkPill("play", MagnetLinks.Resume, out _);
            resume.Click += async (_, _) =>
            {
                resume.IsEnabled = false;
                try
                {
                    if (_serverLocal) { if (await ControlHereAsync(link.Hash, "resume") is { } refused) { SetNotice(refused); resume.IsEnabled = true; return; } }
                    else if (_client is { } client) await client.TorLinkControlAsync(link.Hash, "resume", _connectionLifetime.Token);
                }
                catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); resume.IsEnabled = true; return; }
                link.Popup.IsOpen = false;
                await LoadServerDownloadsAsync();
            };
            bar.Children.Add(resume);
        }
        return bar;
    }

    // ---- Sent ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the window proposes, downloaded: only its files (qBittorrent leaves the others out) when the link was read; all of
    /// it otherwise, or « anyway » when nobody shares it now. The window closes; its row at the top of the list says the rest.
    /// </summary>
    private async Task SendLinkAsync(LinkWindow link, bool anyway)
    {
        if (link.Sending || (!_serverLocal && _client is null)) return;
        var files = !anyway && link.Contents is { } contents ? MagnetLinks.FilesOf(link.Option, contents, link.Owned, link.Chosen) : null;
        link.Sending = true; link.Error = null; RefreshLinkChoice(link);
        string? refused = null;
        try
        {
            if (_serverLocal)
                refused = link.Contents is not null && !anyway ? await DownloadLinkAsync(link.Magnet, files)
                    : QBittorrentInstalled() ? await AddToQBittorrentAsync(link.Magnet, null) : await DownloadHereAsync(link.Magnet);
            else await _client!.TorLinkRequestAsync(link.Magnet, _connectionLifetime.Token, files);
        }
        catch (Exception ex) when (IsExpected(ex)) { refused = ServerError(ex); }
        link.Sending = false;
        if (refused is not null)
        {
            link.Error = refused;
            if (link.Stage is "silent" or "failed") SetNotice(refused);
            DrawLinkWindow(link);
            return;
        }
        link.Sent = true;
        link.Popup.IsOpen = false;
        link.Anchor.Focus();
        await SentToServerAsync();
    }
}
