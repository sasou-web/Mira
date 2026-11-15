using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Coller un lien magnet », as on Windows (maquette B « Écrin » chosen by ogsas on 2026-11-15, MagnetLinks of Core): a
// pasted link opens a window under its button; the qBittorrent of Jellyfin's PC reads what the link holds (through the relay:
// Mira/torlink/look), stopped as soon as it has its list of files; the window says what the library has and proposes what
// it lacks, all of it, or the episodes chosen one by one, then only those are downloaded. The Mac is never the PC of
// Jellyfin: every link goes through the relay.
public sealed partial class DownloadsPage
{
    private sealed class LinkWindow
    {
        public required string Magnet { get; init; }
        public required string Hash { get; init; }
        public required Flyout Flyout { get; init; }
        public required Border Body { get; init; }
        public required Button Anchor { get; init; }
        public required CancellationTokenSource Life { get; init; }
        /// <summary>reading, ready, name (read from the link's name alone), silent, failed, already.</summary>
        public string Stage { get; set; } = "reading";
        public string? Name { get; set; }
        public string? Error { get; set; }
        public LinkContents? Contents { get; set; }
        public LinkPlace? Place { get; set; }
        public bool? InLibrary { get; set; }
        public string? LookedFor { get; set; }
        public MediaItem? Item { get; set; }
        public HashSet<(int Season, int Number)> Owned { get; set; } = [];
        public LinkOption Option { get; set; }
        public HashSet<(int, int)> Chosen { get; } = [];
        public bool ChoiceStarted { get; set; }
        public HashSet<string> Open { get; } = [];
        public LinkEpisode? From { get; set; }
        public bool Shift { get; set; }
        public bool Sending { get; set; }
        public bool Sent { get; set; }
        public bool IsOpen { get; set; } = true;
        public string? Shown { get; set; }
        public List<(LinkEpisode Episode, Button Cell, string Label)> Cells { get; } = [];
        public List<(LinkGroup Group, Button Box, TextBlock Count)> Boxes { get; } = [];
        public TextBlock? ChooseDetail { get; set; }
        public TextBlock? Where { get; set; }
        public Button? Go { get; set; }
        public TextBlock? GoLabel { get; set; }
        public ScrollViewer? Scroll { get; set; }
    }

    private LinkWindow? _link;

    /// <summary>For the self-check: opens the window for a link, what it shows, a choice of its options, and Download.</summary>
    public void OpenLinkForCheck(string magnet) => OpenLinkWindow(magnet);
    public string? LinkShown => _link is { IsOpen: true } link ? link.Stage == "ready" ? "ready|" + link.Option : link.Stage : null;
    public bool LinkGoEnabled => _link?.Go?.IsEffectivelyEnabled == true;
    public void ChooseLinkForCheck(LinkOption option)
    {
        if (_link is { Contents: { } read } link) { PickLinkOption(link, read, option); DrawLinkWindow(link); }
    }
    public Task SendLinkForCheck() => _link is { } link ? SendLinkAsync(link, anyway: false) : Task.CompletedTask;
    public void CloseLinkForCheck() => _link?.Flyout.Hide();

    /// <summary>« Paste a magnet link »: the link on the clipboard opens its window at once; otherwise the field to paste it in.</summary>
    private async Task PasteLinkAsync()
    {
        string? text = null;
        // The clipboard is read on this click (or ⌘V) only, never watched.
        try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) text = (await clipboard.TryGetTextAsync())?.Trim(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { text = null; }
        if (text is { Length: > 0 } && text.Length <= RemoteDownloads.MaxMagnet && RemoteDownloads.InfoHash(text) is not null) { OpenLinkWindow(text); return; }
        OpenMagnet();
    }

    /// <summary>⌘V anywhere on the page but a field: a magnet link on the clipboard opens its window.</summary>
    private void WatchPastes()
    {
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.V || (e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0 || (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Shift)) != 0) return;
            if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox || !IsShowing) return;
            e.Handled = true;
            _ = PasteLinkAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OpenLinkWindow(string magnet)
    {
        if (RemoteDownloads.InfoHash(magnet) is not { } hash) return;
        _link?.Flyout.Hide();
        CloseMagnet();
        // The button under which it opens shows again once the field closed: placed after the layout.
        Dispatcher.UIThread.Post(() => ShowLinkWindow(magnet.Trim(), hash), DispatcherPriority.Loaded);
    }

    private void ShowLinkWindow(string magnet, string hash)
    {
        if (_session is not { } session) return;
        var anchor = MagnetReturn();
        var body = new Border { Width = DetailPage.MissingMenuWidth };
        var flyout = new Flyout { Placement = PlacementMode.Custom, ShowMode = FlyoutShowMode.Standard, Content = body };
        flyout.CustomPopupPlacementCallback = placement => DetailPage.PlaceMissingMenu(anchor, placement);
        var link = _link = new LinkWindow { Magnet = magnet, Hash = hash, Flyout = flyout, Body = body, Anchor = anchor, Life = CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime), Name = RemoteDownloads.DisplayName(magnet) };
        var window = TopLevel.GetTopLevel(anchor);
        void Refit(object? sender, SizeChangedEventArgs e) { if (link.IsOpen) DetailPage.FitMissingMenu(anchor, body); }
        DetailPage.FitMissingMenu(anchor, body);
        if (window is not null) window.SizeChanged += Refit;
        flyout.Closed += (_, _) =>
        {
            link.IsOpen = false;
            if (window is not null) window.SizeChanged -= Refit;
            link.Life.Cancel();
            if (ReferenceEquals(_link, link)) _link = null;
        };
        body.KeyDown += (_, e) => { if (e.Key == Key.Escape) { flyout.Hide(); e.Handled = true; anchor.Focus(); } };
        // Shift held on a number: from the last one clicked to this one (pointer or keyboard).
        body.AddHandler(PointerPressedEvent, (_, e) => link.Shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        body.AddHandler(KeyDownEvent, (_, e) => link.Shift = (e.KeyModifiers & KeyModifiers.Shift) != 0, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        flyout.ShowAt(anchor);
        if (Already(link) is not null) link.Stage = "already";
        DrawLinkWindow(link);
        _ = LookUpLinkAsync(link);
        if (link.Stage == "reading") _ = ReadLinkAsync(link);
    }

    /// <summary>The link's download among those of the PC (by its info hash), or null.</summary>
    private RelayDownload? Already(LinkWindow link) => _status?.Downloads.FirstOrDefault(x => string.Equals(x.Id, link.Hash, StringComparison.OrdinalIgnoreCase));

    private string? LinkPc => _agent?.Name;

    /// <summary>What the link holds, read by the qBittorrent of the server PC through the relay; its name alone otherwise.</summary>
    private async Task ReadLinkAsync(LinkWindow link)
    {
        if (_session is not { } session) return;
        var ct = link.Life.Token;
        link.Stage = "reading"; link.Error = null; DrawLinkWindow(link);
        if (_agent is not { Looks: true }) { link.Stage = "name"; DrawLinkWindow(link); return; }
        RelayLook? read = null;
        try
        {
            var id = await session.Client.TorLinkLookAsync(link.Magnet, ct);
            // The PC comes every few seconds, then asks the link's peers for a minute at most.
            var until = DateTime.UtcNow + MagnetLinks.Patience + TimeSpan.FromSeconds(45);
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                if (await session.Client.TorLinkLookStateAsync(id, ct) is not { } state) { read = new("", "failed", null, null, null, L("Le PC serveur a oublié ce lien : réessaie.", "The server PC forgot this link: try again.")); break; }
                if (state.State is "done" or "silent" or "failed") { read = state; break; }
                if (DateTime.UtcNow > until) { read = new("", "failed", null, null, null, L("Le PC serveur n’a pas répondu.", "The server PC didn’t answer.")); break; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) when (Errors.Expected(ex)) { read = new("", "failed", null, null, null, Errors.Friendly(ex)); }
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

    private static void PickLinkOption(LinkWindow link, LinkContents read, LinkOption chosen)
    {
        link.Option = chosen; link.ChoiceStarted = true;
        // Opened the first time: what the library lacks, checked already.
        if (chosen == LinkOption.Choose && link.Chosen.Count == 0) foreach (var e in MagnetLinks.Lacking(read, link.Owned)) link.Chosen.Add(e.Key);
        if (chosen == LinkOption.Choose && link.Open.Count == 0 && MagnetLinks.Groups(read.Episodes) is { Count: > 1 } groups) link.Open.Add(groups.FirstOrDefault(g => MagnetLinks.State(g, link.Chosen, link.Owned) is LinkGroupState.Some or LinkGroupState.All)?.Label ?? groups[0].Label);
    }

    private static void Propose(LinkWindow link)
    {
        if (link.Contents is not { } contents) return;
        if (!link.ChoiceStarted) link.Option = MagnetLinks.Options(contents, link.Owned).Suggested;
    }

    /// <summary>What the library has of the link's title, its poster, and the episodes it has; asked again once the real name is read.</summary>
    private async Task LookUpLinkAsync(LinkWindow link)
    {
        if (_session is not { } session || link.Name is not { Length: > 0 } name || link.LookedFor == name) return;
        link.LookedFor = name;
        var ct = link.Life.Token;
        var group = ReleaseRanking.Group([new RelayHit(link.Hash, name, 0, 0, 0, "", null)]).FirstOrDefault();
        if (group is null) return;
        try
        {
            var items = await OwnedTitles.FindAsync(OwnedTitles.Lookups([group])[0].Title, [group], async text => (await session.Client.OwnedAsync(text, ct)).Items);
            if (ct.IsCancellationRequested || link.LookedFor != name) return;
            var line = OwnedTitles.Line(group, items);
            link.InLibrary = line is not null;
            link.Item = line is null ? null : items.FirstOrDefault(x => x.Id == line.ItemId);
            HashSet<(int, int)> owned = [];
            if (link.Item is { Type: "Series" } series && !group.Release.Absolute)
                foreach (var episode in (await session.Client.OwnedEpisodesAsync(series.Id, ct)).Items)
                    if (episode.ParentIndexNumber is { } season && episode.IndexNumber is { } first)
                        for (var n = first; n <= (episode.IndexNumberEnd ?? first); n++) owned.Add((season, n));
            if (ct.IsCancellationRequested || link.LookedFor != name) return;
            link.Owned = owned;
            if (link.Item is { } item) _posters[item.Id] = item;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        link.Chosen.RemoveWhere(k => link.Contents?.Episodes.FirstOrDefault(e => e.Key == k) is { } e && MagnetLinks.Owned(e, link.Owned));
        Propose(link);
        if (link.IsOpen) DrawLinkWindow(link);
    }

    // ---- Drawn --------------------------------------------------------------------------------------------------------

    private void DrawLinkWindow(LinkWindow link)
    {
        if (!link.IsOpen) return;
        var offset = link.Scroll?.Offset ?? default;
        link.Cells.Clear(); link.Boxes.Clear(); link.ChooseDetail = null; link.Where = null; link.Go = null; link.GoLabel = null;
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(LinkHead(link));
        var middle = new StackPanel();
        var foot = new StackPanel();
        switch (link.Stage)
        {
            case "already": middle.Children.Add(LinkAlready(link)); foot.Children.Add(LinkAlreadyFoot(link)); break;
            case "reading": LinkReading(LinkPc, middle); foot.Children.Add(LinkFoot(link, enabled: false)); break;
            case "silent" or "failed": LinkTrouble(link, LinkPc, middle); foot.Children.Add(LinkTroubleFoot(link)); break;
            default: LinkProposals(link, middle); foot.Children.Add(LinkFoot(link, enabled: true)); break;
        }
        var scroll = link.Scroll = new ScrollViewer { Content = middle, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        // The list scrolls under the header and the foot, faded where it goes on.
        var fade = new Border
        {
            Height = 28, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, IsVisible = false,
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.Parse("#111111"), 1) } }
        };
        void Faded() => fade.IsVisible = scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 1;
        scroll.ScrollChanged += (_, _) => Faded();
        var area = new Grid(); area.Children.Add(scroll); area.Children.Add(fade);
        Grid.SetRow(area, 1); Grid.SetRow(foot, 2);
        grid.Children.Add(area); grid.Children.Add(foot);
        link.Body.Child = grid;
        RefreshLinkChoice(link);
        if (offset != default) Dispatcher.UIThread.Post(() => { if (link.IsOpen) scroll.Offset = offset; }, DispatcherPriority.Loaded);
        // Into the window when what it shows changes (keyboard and VoiceOver start there), never while it is used.
        var shown = link.Stage + "|" + link.Option;
        var moved = shown != link.Shown && !link.Sending; link.Shown = shown;
        if (moved && link.Stage != "reading")
            Dispatcher.UIThread.Post(() =>
            {
                if (!link.IsOpen) return;
                var first = grid.Children.Skip(1).SelectMany(x => x.GetSelfAndVisualDescendants()).OfType<InputElement>().FirstOrDefault(x => x.Focusable && x.IsEffectivelyVisible && x.IsEffectivelyEnabled);
                first?.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Input);
    }

    /// <summary>Its poster (or the kind's icon), its title, badge and kind, the release's quality under them; the close button.</summary>
    private Control LinkHead(LinkWindow link)
    {
        var head = new Grid { Margin = new Thickness(10, 10, 4, 6) };
        var row = new DockPanel { Margin = new Thickness(0, 0, 40, 0) };
        var release = ReleaseRanking.Parse(link.Name);
        var episodic = link.Contents?.Episodic ?? release.Episode is not null || release.Pack || release.Season is not null;
        Control thumb = link.Name is null && link.Stage is "silent" or "failed"
            ? new Border { Width = 46, Height = 66, CornerRadius = new CornerRadius(8), Background = Surface, BorderBrush = Ui.Raised2, BorderThickness = new Thickness(1), Child = new Icon("link", 18) { Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }
            : Thumb(link.Item?.Id, episodic, 46, 66, 8);
        thumb.Margin = new Thickness(0, 0, 14, 0); thumb.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(thumb, Dock.Left); row.Children.Add(thumb);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new WrapPanel();
        // The title as the library names it once Jellyfin has it (« Longue série », not « Longue Serie » of the release).
        var name = link.Item?.Name ?? (release.Title.Length > 0 ? release.Title : link.Name ?? MagnetLinks.Unnamed);
        var heading = new TextBlock { FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Ui.Ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        heading.Inlines!.Add(new Run(name));
        if (!episodic && release.Year is { } year) heading.Inlines.Add(new Run($" ({year})") { FontWeight = FontWeight.Normal, Foreground = Ui.TextSecondary });
        title.Children.Add(heading);
        if (link.Name is not null && episodic && MagnetLinks.Badge(release) is { Length: > 0 } badge)
            title.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Ui.Raised, Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = badge, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBody } });
        if (link.Name is not null)
            title.Children.Add(new TextBlock { Text = MagnetLinks.KindName(link.Contents?.Kind ?? (episodic ? MediaKind.Series : MediaKind.Movie)), FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
        words.Children.Add(title);
        var under = link.Name is null ? MagnetLinks.Short(link.Magnet, link.Hash) : ReleaseRanking.QualityLine(release);
        if (under.Length > 0)
        {
            var line = new TextBlock { Text = under, FontSize = 13, Foreground = Ui.TextSecondary, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            if (link.Name is { } full) ToolTip.SetTip(line, full);
            words.Children.Add(line);
        }
        row.Children.Add(words);
        head.Children.Add(row);
        var close = Ui.IconButton("close", L("Fermer", "Close"), () => { link.Flyout.Hide(); link.Anchor.Focus(); }, "icon", 15);
        close.Width = close.Height = 32; close.CornerRadius = new CornerRadius(10); close.Foreground = Ui.TextSecondary;
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Margin = new Thickness(0, -4, 0, 0);
        head.Children.Add(close);
        return head;
    }

    /// <summary>« Reading what’s inside… », who is asked, the privacy line, and the lines to come.</summary>
    private static void LinkReading(string? pc, StackPanel middle)
    {
        var line = Ui.Row(9, new Icon("refresh", 15) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = MagnetLinks.Reading, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        line.Margin = new Thickness(14, 6, 14, 0);
        middle.Children.Add(line);
        middle.Children.Add(new TextBlock { Text = MagnetLinks.ReadingHow(pc), FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(38, 3, 14, 0) });
        for (var i = 0; i < 3; i++)
        {
            var row = new StackPanel { Margin = new Thickness(14, 16, 14, 0), Opacity = 1 - i * 0.25 };
            row.Children.Add(new Border { Height = 11, Width = 260 - i * 40, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Background = Ui.Raised2 });
            row.Children.Add(new Border { Height = 9, Width = 150, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = Ui.Raised2, Margin = new Thickness(0, 7, 0, 0), Opacity = 0.7 });
            middle.Children.Add(row);
        }
        middle.Children.Add(new TextBlock { Text = MagnetLinks.Privacy, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 16, 14, 6) });
        AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Polite);
    }

    /// <summary>The library line, then the proposals; « Choose the episodes… » opens its boxes under it.</summary>
    private void LinkProposals(LinkWindow link, StackPanel middle)
    {
        if (link.Contents is { } contents && MagnetLinks.Library(contents, link.Owned, link.InLibrary) is { Length: > 0 } library)
        {
            var some = contents.Episodes.Any(e => MagnetLinks.Owned(e, link.Owned));
            var line = Ui.Row(7, new Icon(some ? "check" : "info", 14) { Foreground = some ? Ui.Success : Ui.Muted, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = library, FontSize = 13, Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center });
            line.Margin = new Thickness(14, 2, 14, 8);
            middle.Children.Add(line);
        }
        if (link.Contents is not { } read)
        {
            // Its name alone: all of it, said so.
            middle.Children.Add(LinkSummary(L("Tout le lien", "Everything in the link"), link.Name is { } n && ReleaseRanking.Readable(n).Full is not null ? ReleaseRanking.Readable(n).Title : null));
            middle.Children.Add(new TextBlock { Text = MagnetLinks.WholeOnly(false), FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 6, 14, 8) });
            return;
        }
        if (!read.Episodic)
        {
            if (read.Films.Count == 0) { middle.Children.Add(new TextBlock { Text = MagnetLinks.NoVideo, FontSize = 13, Foreground = Ui.TextSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 8) }); return; }
            middle.Children.Add(LinkSummary(MagnetLinks.Title(LinkOption.Film, read, link.Owned), MagnetLinks.Detail(LinkOption.Film, read, link.Owned, link.Chosen)));
            if (read.Samples) middle.Children.Add(new TextBlock { Text = MagnetLinks.SampleOut, FontSize = 12.5, Foreground = Ui.Muted, Margin = new Thickness(14, 3, 14, 8) });
            return;
        }
        var (options, suggested) = MagnetLinks.Options(read, link.Owned);
        foreach (var option in options)
        {
            var content = new DockPanel();
            if (option == suggested)
            {
                var tag = new TextBlock { Text = MagnetLinks.Suggested, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(tag, Dock.Right); content.Children.Add(tag);
            }
            if (option == LinkOption.Choose)
            {
                var chevron = new Icon(link.Option == LinkOption.Choose ? "down" : "next", 13) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 2, 0) };
                DockPanel.SetDock(chevron, Dock.Right); content.Children.Add(chevron);
            }
            var detail = new TextBlock { Text = option == LinkOption.Choose && link.Option != LinkOption.Choose ? MagnetLinks.ChooseFrom(read) : MagnetLinks.Detail(option, read, link.Owned, link.Chosen), FontSize = 12.5, FontWeight = FontWeight.Normal, Foreground = Ui.Faint, TextTrimming = TextTrimming.CharacterEllipsis };
            if (option == LinkOption.Choose && link.Option == LinkOption.Choose) link.ChooseDetail = detail;
            content.Children.Add(Ui.Column(2, new TextBlock { Text = MagnetLinks.Title(option, read, link.Owned), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis }, detail));
            var pick = new RadioButton { Classes = { "pack" }, GroupName = "link-option", IsChecked = option == link.Option, Content = content };
            AutomationProperties.SetName(pick, MagnetLinks.Title(option, read, link.Owned));
            var chosen = option;
            pick.IsCheckedChanged += (_, _) =>
            {
                if (pick.IsChecked != true || link.Option == chosen) return;
                PickLinkOption(link, read, chosen);
                // After the radio's own change: the window drawn again around it.
                Dispatcher.UIThread.Post(() => DrawLinkWindow(link));
            };
            middle.Children.Add(pick);
            if (option == LinkOption.Choose && link.Option == LinkOption.Choose) middle.Children.Add(LinkEpisodes(link, read));
        }
    }

    private static Control LinkSummary(string title, string? detail)
    {
        var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 4, 14, 0) };
        text.Inlines!.Add(new Run(title) { FontWeight = FontWeight.Bold, Foreground = Ui.Ink });
        if (detail is { Length: > 0 }) text.Inlines.Add(new Run(" · " + detail) { Foreground = Ui.TextSecondary });
        return text;
    }

    /// <summary>The boxes of « Choose the episodes… »: one grid, or groups that fold (a season each, or 50 numbers).</summary>
    private Control LinkEpisodes(LinkWindow link, LinkContents contents)
    {
        var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
        var groups = MagnetLinks.Groups(contents.Episodes);
        var numbered = MagnetLinks.Numbered(groups);
        var width = MagnetLinks.Digits(contents.Episodes.Max(e => e.Last ?? e.Number));
        if (contents.Episodes.Count > 5)
        {
            var hint = new DockPanel { Margin = new Thickness(8, 2, 4, 8) };
            Button Word(string text, Action act)
            {
                var b = new Button { Classes = { "quiet" }, Content = text, Padding = new Thickness(6, 3), FontSize = 13, Foreground = Ui.TextBody, CornerRadius = new CornerRadius(8) };
                b.Click += (_, _) => { act(); RefreshLinkChoice(link); };
                return b;
            }
            var all = Ui.Row(0, Word(L("Tout", "All"), () => { foreach (var e in MagnetLinks.Lacking(contents, link.Owned)) link.Chosen.Add(e.Key); }),
                new TextBlock { Text = "·", Foreground = Ui.Faint, VerticalAlignment = VerticalAlignment.Center }, Word(L("Rien", "None"), () => link.Chosen.Clear()));
            DockPanel.SetDock(all, Dock.Right); hint.Children.Add(all);
            hint.Children.Add(new TextBlock { Text = MagnetLinks.ShiftHint, FontSize = 12.5, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(hint);
        }
        if (groups is [{ Label: "" } only]) { panel.Children.Add(LinkCells(link, contents, only, numbered, width)); return panel; }
        foreach (var group in groups)
        {
            var open = link.Open.Contains(group.Label);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 2) };
            var box = new Button { Classes = { "quiet" }, Width = 36, Height = 36, Padding = new Thickness(0), CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            box.Click += (_, _) => { MagnetLinks.Toggle(link.Chosen, group, link.Owned); RefreshLinkChoice(link); };
            row.Children.Add(box);
            var line = new DockPanel();
            var chevron = new Icon(open ? "up" : "down", 12) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(chevron, Dock.Right); line.Children.Add(chevron);
            var count = new TextBlock { FontSize = 12.5, FontWeight = FontWeight.Normal, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Dock.Right); line.Children.Add(count);
            var label = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            label.Inlines!.Add(new Run(group.Label));
            label.Inlines.Add(new Run("   " + RemoteLines.Size(group.Episodes.Sum(e => e.Size))) { FontWeight = FontWeight.Normal, FontSize = 13, Foreground = Ui.Faint });
            line.Children.Add(label);
            var header = new Button { Classes = { "quiet" }, Padding = new Thickness(8, 8, 10, 8), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = line };
            AutomationProperties.SetName(header, group.Label);
            header.Click += (_, _) => { if (!link.Open.Remove(group.Label)) link.Open.Add(group.Label); DrawLinkWindow(link); };
            Grid.SetColumn(header, 1); row.Children.Add(header);
            link.Boxes.Add((group, box, count));
            panel.Children.Add(row);
            if (open) panel.Children.Add(LinkCells(link, contents, group, numbered, width, indent: 40));
        }
        return panel;
    }

    /// <summary>A box per episode: white when it goes, outlined when not, quiet with « in library » for one the library has.</summary>
    private Control LinkCells(LinkWindow link, LinkContents contents, LinkGroup group, bool numbered, int width, double indent = 0)
    {
        var cells = new UniformGrid { Columns = numbered ? 10 : 5, Margin = new Thickness(indent, 2, 0, 8) };
        foreach (var episode in group.Episodes)
        {
            var owned = MagnetLinks.Owned(episode, link.Owned);
            var label = MagnetLinks.Cell(episode, numbered, width);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            if (!numbered)
            {
                var under = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                if (owned) under.Children.Add(new Icon("check", 10) { Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                under.Children.Add(new TextBlock { Text = owned ? MagnetLinks.InLibrary : RemoteLines.Size(episode.Size), FontSize = 11.5, FontWeight = FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(under);
            }
            var cell = new Button
            {
                Content = content, Height = numbered ? 38 : 54, Margin = new Thickness(3), Padding = new Thickness(2), BorderThickness = new Thickness(1.2), CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = !owned,
            };
            if (numbered) ToolTip.SetTip(cell, owned ? MagnetLinks.InLibrary : RemoteLines.Size(episode.Size));
            var e = episode;
            cell.Click += (_, _) =>
            {
                if (owned) return;
                MagnetLinks.Click(link.Chosen, contents.Episodes, e, link.Shift ? link.From : null, link.Owned);
                link.From = e;
                RefreshLinkChoice(link);
            };
            link.Cells.Add((episode, cell, label));
            cells.Children.Add(cell);
        }
        return cells;
    }

    /// <summary>The boxes, the groups' marks, « 4 chosen · 5.7 GB », the destination and Download, after a click.</summary>
    private static void RefreshLinkChoice(LinkWindow link)
    {
        foreach (var (episode, cell, label) in link.Cells)
        {
            var owned = MagnetLinks.Owned(episode, link.Owned);
            var on = !owned && link.Chosen.Contains(episode.Key);
            cell.Background = on ? Ui.Ink : Brushes.Transparent;
            cell.Foreground = on ? OnAccent : owned ? Ui.Faint : Ui.TextBody;
            cell.BorderBrush = on ? Ui.Ink : owned ? Ui.Rule : Ui.LineStrong;
            cell.Cursor = owned ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            AutomationProperties.SetName(cell, owned ? $"{label}, {MagnetLinks.InLibrary}" : $"{label}, {RemoteLines.Size(episode.Size)}, {(on ? L("choisi", "chosen") : L("pas choisi", "not chosen"))}");
        }
        foreach (var (group, box, count) in link.Boxes)
        {
            var state = MagnetLinks.State(group, link.Chosen, link.Owned);
            var mark = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5) };
            mark.Background = state is LinkGroupState.All or LinkGroupState.Some ? Ui.Ink : Brushes.Transparent;
            mark.BorderBrush = state is LinkGroupState.None ? Ui.Dim : state is LinkGroupState.Owned ? Ui.Rule : Ui.Ink;
            if (state is LinkGroupState.All or LinkGroupState.Some) mark.Child = new Icon(state == LinkGroupState.All ? "check" : "minus", 13) { Foreground = OnAccent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (state is LinkGroupState.Owned) mark.Child = new Icon("check", 12) { Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
            where.Inlines!.Clear();
            where.Inlines.Add(new Run(into)); where.Inlines.Add(new Run(place) { FontWeight = FontWeight.Bold, Foreground = Ui.Ink });
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
    private Control LinkFoot(LinkWindow link, bool enabled)
    {
        var foot = new StackPanel();
        foot.Children.Add(new Border { Height = 1, Background = Ui.Rule, Margin = new Thickness(6, 8, 6, 0) });
        var bar = new DockPanel { Margin = new Thickness(8, 12, 4, 4) };
        var go = link.Go = LinkPill("download", MagnetLinks.Download, out var label);
        link.GoLabel = label;
        go.IsEnabled = enabled;
        go.Click += async (_, _) => await SendLinkAsync(link, anyway: false);
        DockPanel.SetDock(go, Dock.Right); bar.Children.Add(go);
        var where = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) };
        where.Children.Add(link.Where = new TextBlock { FontSize = 13.5, Foreground = Ui.TextBody, TextTrimming = TextTrimming.CharacterEllipsis });
        if (MagnetLinks.Free(_agent?.Free, LinkPc) is { Length: > 0 } free)
            where.Children.Add(new TextBlock { Text = free, FontSize = 12, Foreground = Ui.Faint, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        bar.Children.Add(where);
        foot.Children.Add(bar);
        if (link.Error is { } error && link.Stage is "ready" or "name") foot.Children.Add(new TextBlock { Text = error, Foreground = Danger, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(14, 6, 14, 4) });
        return foot;
    }

    private static Button LinkPill(string icon, string text, out TextBlock label)
    {
        label = new TextBlock { Text = text, FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var go = new Button
        {
            Classes = { "primary" }, Height = 42, Padding = new Thickness(20, 0), CornerRadius = new CornerRadius(21), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Content = Ui.Row(9, new Icon(icon, 16) { VerticalAlignment = VerticalAlignment.Center }, label)
        };
        AutomationProperties.SetName(go, text);
        return go;
    }

    private static Button LinkQuiet(string text) => new() { Classes = { "quiet" }, Content = text, Padding = new Thickness(14, 9), FontSize = 13.5, Foreground = Ui.Ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(12) };

    /// <summary>Nobody shares the link (or it could not be read): why, « Download anyway » and « Try again ».</summary>
    private static void LinkTrouble(LinkWindow link, string? pc, StackPanel middle)
    {
        var silent = link.Stage == "silent";
        var line = new DockPanel();
        var icon = new Icon("warning", 15) { Foreground = Warning, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
        line.Children.Add(Ui.Column(4,
            new TextBlock { Text = silent ? MagnetLinks.Silent : L("Mira n’a pas pu lire ce lien", "Mira couldn’t read this link"), FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = silent ? MagnetLinks.SilentHow(pc) : link.Error ?? "", FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap }));
        middle.Children.Add(new Border { Background = Ui.Raised, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Margin = new Thickness(6, 4), Child = line });
        middle.Children.Add(new TextBlock { Text = MagnetLinks.AnywayHow, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 4) });
        AutomationProperties.SetLiveSetting(middle, AutomationLiveSetting.Assertive);
    }

    private Control LinkTroubleFoot(LinkWindow link)
    {
        var anyway = LinkQuiet(MagnetLinks.DownloadAnyway);
        anyway.Click += async (_, _) => await SendLinkAsync(link, anyway: true);
        var again = LinkPill("refresh", MagnetLinks.TryAgain, out _);
        again.Click += (_, _) => _ = ReadLinkAsync(link);
        var bar = Ui.Row(0, anyway, again);
        bar.HorizontalAlignment = HorizontalAlignment.Right; bar.Margin = new Thickness(8, 10, 4, 4);
        return bar;
    }

    /// <summary>The link is downloading already: how far, « Show it in the list », « Resume » when it is paused.</summary>
    private Control LinkAlready(LinkWindow link)
    {
        var download = Already(link);
        var words = new StackPanel();
        words.Children.Add(Ui.Row(8, new Icon("info", 14) { Foreground = Ui.TextSecondary, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = MagnetLinks.Already, FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }));
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
            words.Children.Add(new TextBlock { Text = state + size, FontSize = 12.5, Foreground = Ui.Muted, Margin = new Thickness(22, 3, 0, 10), TextWrapping = TextWrapping.Wrap });
            var progress = Math.Clamp(download.Progress, 0, 100);
            var track = new Grid { Height = 4, ColumnDefinitions = new ColumnDefinitions($"{progress}*,{100 - progress}*") };
            var rail = new Border { CornerRadius = new CornerRadius(2), Background = Ui.Raised2 };
            Grid.SetColumnSpan(rail, 2); track.Children.Add(rail);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Ui.TextSecondary });
            words.Children.Add(track);
        }
        return new Border { Background = Ui.Raised, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 14), Margin = new Thickness(6, 4), Child = words };
    }

    private Control LinkAlreadyFoot(LinkWindow link)
    {
        var show = LinkQuiet(MagnetLinks.ShowInList);
        show.Click += (_, _) => { link.Flyout.Hide(); Glide.ToTop(_searchShown ? _queueScroll : _restScroll); };
        var bar = Ui.Row(0, show);
        bar.HorizontalAlignment = HorizontalAlignment.Right; bar.Margin = new Thickness(8, 10, 4, 4);
        if (Already(link) is { Status: "paused" or "stopped" } && _session is { } session)
        {
            var resume = LinkPill("play", MagnetLinks.Resume, out _);
            resume.Click += async (_, _) =>
            {
                resume.IsEnabled = false;
                try { await session.Client.TorLinkControlAsync(link.Hash, "resume", session.Lifetime); }
                catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex), tone: NoticeTone.Error); resume.IsEnabled = true; return; }
                link.Flyout.Hide();
                await LoadStatusAsync();
            };
            bar.Children.Add(resume);
        }
        return bar;
    }

    // ---- Sent ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the window proposes, sent to the server PC: only its files (its qBittorrent leaves the others out) when the link
    /// was read; all of it otherwise, or « anyway » when nobody shares it now. The window closes; its row says the rest.
    /// </summary>
    private async Task SendLinkAsync(LinkWindow link, bool anyway)
    {
        if (link.Sending || _session is not { } session) return;
        var files = !anyway && link.Contents is { } contents ? MagnetLinks.FilesOf(link.Option, contents, link.Owned, link.Chosen) : null;
        link.Sending = true; link.Error = null; RefreshLinkChoice(link);
        string? refused = null;
        try { await session.Client.TorLinkRequestAsync(link.Magnet, session.Lifetime, files); }
        catch (Exception ex) when (Errors.Expected(ex)) { refused = Errors.Friendly(ex); }
        link.Sending = false;
        if (refused is not null)
        {
            link.Error = refused;
            if (link.Stage is "silent" or "failed") Shell.Notice(refused, tone: NoticeTone.Error);
            DrawLinkWindow(link);
            return;
        }
        link.Sent = true;
        link.Flyout.Hide();
        link.Anchor.Focus();
        await SentAsync();
    }
}
