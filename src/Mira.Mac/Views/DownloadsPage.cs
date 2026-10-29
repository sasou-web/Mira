using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Téléchargements, for the administrators of Jellyfin. TorLink runs on the PC of Jellyfin only (Mira for Windows), where
/// its downloads land next to the library; this page sends it searches and downloads through the relay of the extension
/// (<c>Mira/torlink</c>), as Mira web's « Télécharger sur le PC serveur » and Mira for Windows away from that PC do: the
/// best release of each title or episode first (<see cref="ReleaseRanking"/>), what the PC has under way with its
/// progress and time left (<see cref="RemoteLines"/>), read every 3 s while the page shows, and only then.
/// « Chercher une meilleure version » of a film or an episode opens it for that title: its search made, its results
/// only those better than its file (<see cref="TitleUpgrade"/>), each one asked as its replacement.
/// </summary>
public sealed class DownloadsPage : Page
{
    public override string Rail => "downloads";
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(90);
    private static string PageLine => L("Chaque téléchargement terminé rejoint ta bibliothèque.", "Each finished download joins your library.");
    // The Windows app's states and secondary ink (Themes/Cinema.xaml).
    private static readonly IBrush Success = Ui.Brush("#86C6A2"), Warning = Ui.Brush("#D5B787"), WarningText = Ui.Brush("#E9DCC3"), Danger = Ui.Brush("#F3AD99"),
        Secondary = Ui.Brush("#C5C5CA"), Line = Ui.Brush("#26262A"), Surface = Ui.Brush("#111113"), Accent = Ui.Brush("#F5F5F7"), OnAccent = Ui.Brush("#0B0B0C");
    private readonly Session? _session;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBlock _subtitle = new() { Text = L("Lecture du PC serveur…", "Reading the server PC…"), FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _pillText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _pillDot = new() { Width = 8, Height = 8, Fill = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _query = new() { Watermark = L("Un film, une série, un anime…", "A movie, a show, an anime…"), MaxLength = 200 };
    private readonly Button _search = new() { Content = L("Chercher", "Search"), Classes = { "primary" }, Padding = new Thickness(22, 11), Margin = new Thickness(10, 0, 0, 0), IsEnabled = false };
    private readonly Button _magnetButton;
    private readonly Border _magnetPanel;
    private readonly TextBox _magnet = new() { Watermark = "magnet:?xt=urn:btih:…", FontSize = 14, Padding = new Thickness(12, 9) };
    private readonly Button _magnetSend = new() { Content = L("Envoyer au PC serveur", "Send to the server PC"), Classes = { "primary" }, Padding = new Thickness(18, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _magnetError = new() { FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
    private readonly TextBlock _searchStatus = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 0, 0, 40) };
    private readonly ScrollViewer _resultsScroll;
    private readonly Control _resultsEmpty;
    private readonly TextBlock _summary = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _queue = new(), _follows = new(), _placed = new();
    private readonly TextBlock _followsTitle = new() { Text = L("Séries suivies", "Followed series"), FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 14, 0, 10), IsVisible = false };
    private readonly TextBlock _placedTitle = new() { Text = L("Rangés dans ta bibliothèque", "Added to your library"), FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 14, 0, 10), IsVisible = false };
    private readonly ScrollViewer _queueScroll;
    private readonly Control _queueEmpty;
    private readonly Grid _problem = new() { Background = Ui.Brush("#070708"), IsVisible = false };
    private readonly Icon _problemIcon = new("server", 40) { Foreground = Ui.Faint, Margin = new Thickness(0, 0, 0, 16) };
    private readonly TextBlock _problemTitle = new() { FontSize = 20, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _problemHint = new() { FontSize = 14, Foreground = Ui.Muted, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    /// <summary>Jellyfin answered once: the page shows the PC, and a hiccup afterwards keeps it on screen.</summary>
    private bool _filled, _ready, _loading, _again, _searching;
    private int _searchVersion;
    private RelayAgent? _agent;
    /// <summary>Results sent from this page (search id / result id): « Envoyé » on their button.</summary>
    private readonly HashSet<string> _sent = [];
    /// <summary>Rows of the right column by key, their texts changed in place every 3 s: a button under the pointer stays where it is.</summary>
    private readonly Dictionary<string, QueueRow> _rows = [];
    private sealed record QueueRow(string Shape, Border View, TextBlock Name, TextBlock Line, Border? Bar);
    private sealed record RowData(string Key, string Shape, string Name, string Line, int? Progress, Func<QueueRow> Make);

    /// <summary>What the PC has under way and sent from here, as shown (the self-check).</summary>
    public int QueueCount => _queue.Children.Count + _placed.Children.Count;
    public int ResultGroups => _results.Children.Count;
    public bool Ready => _ready;
    /// <summary>Types a search and runs it, as the field and Entrée do (the self-check).</summary>
    public Task SearchAsync(string text) { _query.Text = text; return SearchAsync(); }
    /// <summary>« Télécharger » on the best release of the first title found (the self-check).</summary>
    public Task SendBestAsync() => _firstBest is var (search, hit, button) ? SendHitAsync(search, hit, button) : Task.CompletedTask;
    private (string Search, RelayHit Hit, Button Button)? _firstBest;
    /// <summary>« Chercher une meilleure version »: the title asked for, then as Jellyfin has it (its file); whether a better version of it was asked.</summary>
    private readonly MediaItem? _upgradeOf;
    private MediaItem? _upgrade;
    private bool _upgradeStarted, _upgradeSent;
    private readonly Border _upgradeBanner;
    private readonly TextBlock _upgradeTitle = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _upgradeLine = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    public DownloadsPage(MainWindow shell, MediaItem? upgrade = null) : base(shell)
    {
        _session = shell.Session;
        _upgradeOf = upgrade;
        // —— Header: the page, where the PC stands, and its pill ——
        var pill = new Border
        {
            Background = Ui.Raised, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 10, 0, 0),
            Child = Ui.Row(8, _pillDot, _pillText)
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 26) };
        DockPanel.SetDock(pill, Dock.Right); header.Children.Add(pill);
        header.Children.Add(Ui.Column(0, Ui.Heading(L("Téléchargements", "Downloads"), 34, 1, FontWeight.ExtraBold), _subtitle));

        // —— Left: the search, a magnet link, the results ——
        _query.InnerLeftContent = new Icon("search", 17) { Foreground = Ui.Faint, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false };
        var clear = Ui.IconButton("close", L("Effacer · Échap", "Clear · Esc"), () => { _query.Text = ""; _query.Focus(); }, "icon", 14);
        clear.Width = clear.Height = 30; clear.Margin = new Thickness(0, 0, 6, 0); clear.IsVisible = false;
        AutomationProperties.SetName(clear, L("Effacer la recherche", "Clear the search"));
        _query.InnerRightContent = clear;
        _query.TextChanged += (_, _) => clear.IsVisible = !string.IsNullOrEmpty(_query.Text);
        _query.Padding = new Thickness(10, 11, 4, 11);
        AutomationProperties.SetName(_query, L("Que veux-tu télécharger ?", "What do you want to download?"));
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
            // Échap first clears the search, then (a second press) goes back as usual.
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_query.Text)) { e.Handled = true; _query.Text = ""; }
        };
        _search.Click += (_, _) => _ = SearchAsync();
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bar.Children.Add(_query); Grid.SetColumn(_search, 1); bar.Children.Add(_search);

        _magnetButton = SmallButton(L("Coller un lien magnet", "Paste a magnet link"), "link", OpenMagnet);
        _magnetButton.HorizontalAlignment = HorizontalAlignment.Right;
        var tools = new Panel { Margin = new Thickness(0, 12, 0, 0), Children = { _magnetButton } };

        AutomationProperties.SetName(_magnet, L("Lien magnet", "Magnet link"));
        _magnet.TextChanged += (_, _) => _magnetError.IsVisible = false;
        _magnet.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SendMagnetAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseMagnet(); _magnetButton.Focus(); }
        };
        _magnetSend.Click += (_, _) => _ = SendMagnetAsync();
        var close = new Button { Content = L("Fermer", "Close"), Classes = { "quiet" }, Padding = new Thickness(14, 9), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => { CloseMagnet(); _magnetButton.Focus(); };
        var magnetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        magnetRow.Children.Add(_magnet);
        Grid.SetColumn(_magnetSend, 1); magnetRow.Children.Add(_magnetSend);
        Grid.SetColumn(close, 2); magnetRow.Children.Add(close);
        _magnetPanel = new Border
        {
            Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14), Margin = new Thickness(0, 12, 0, 0), IsVisible = false,
            Child = Ui.Column(0,
                new TextBlock { Text = L("Coller un lien magnet", "Paste a magnet link"), FontSize = 14, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = L("Le lien d’un site que tu connais : TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque.", "A link from a site you know: TorLink downloads it on the server PC, then Mira adds it to your library."), FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
                magnetRow, _magnetError)
        };
        _searchStatus.Margin = new Thickness(0, 16, 0, 14);
        // « Chercher une meilleure version »: the title replaced, what its file is now, what happens to it; closed, the page as it is.
        var endUpgrade = Ui.IconButton("close", L("Ne plus chercher de meilleure version", "Stop looking for a better version"), EndUpgrade, "icon", 14);
        endUpgrade.Width = endUpgrade.Height = 30; endUpgrade.VerticalAlignment = VerticalAlignment.Top;
        var upgradeHead = new DockPanel();
        DockPanel.SetDock(endUpgrade, Dock.Right); upgradeHead.Children.Add(endUpgrade);
        upgradeHead.Children.Add(Ui.Row(10, new Icon("sparkle", 18) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) }, Ui.Column(0, _upgradeTitle, _upgradeLine)));
        _upgradeBanner = new Border { Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 10, 14), Margin = new Thickness(0, 0, 0, 14), IsVisible = false, Child = upgradeHead };
        var top = Ui.Column(0, _upgradeBanner, bar, tools, _magnetPanel, _searchStatus);
        _resultsScroll = new ScrollViewer { Content = _results };
        _resultsEmpty = Empty("search", L("Cherche un film, une série ou un anime", "Search for a movie, a show or an anime"),
            L("TorLink le cherche sur le PC serveur. Mira regroupe les versions par titre et par épisode, et met la meilleure en avant : la qualité de la source d’abord, le 1080p, puis MULTI ou VF.",
                "TorLink searches for it on the server PC. Mira groups the versions by title and by episode, and puts the best one first: the quality of the source first, then 1080p, then MULTI or French audio."),
            L("Ne télécharge que ce que tu as le droit d’obtenir.", "Only download what you have the right to get."));
        var found = new Grid(); found.Children.Add(_resultsScroll); found.Children.Add(_resultsEmpty);
        var left = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); left.Children.Add(top); left.Children.Add(found);

        // —— Right: what the PC of Jellyfin has under way, then what it put in the library ——
        var queueHead = Ui.Column(0, new TextBlock { Text = L("Sur le PC serveur", "On the server PC"), FontSize = 19, FontWeight = FontWeight.Bold }, _summary);
        queueHead.Margin = new Thickness(2, 0, 0, 16);
        var list = Ui.Column(0, _queue, _followsTitle, _follows, _placedTitle, _placed);
        list.Margin = new Thickness(0, 0, 0, 40);
        _queueScroll = new ScrollViewer { Content = list };
        _queueEmpty = Empty("download", L("Rien en cours", "Nothing under way"), L("Ce que tu envoies apparaît ici avec sa progression et le temps restant, puis rejoint ta bibliothèque.", "What you send shows up here with its progress and time left, then joins your library."), null);
        var queued = new Grid(); queued.Children.Add(_queueScroll); queued.Children.Add(_queueEmpty);
        var right = new DockPanel();
        DockPanel.SetDock(queueHead, Dock.Top); right.Children.Add(queueHead); right.Children.Add(queued);

        // —— Why the PC cannot be reached, over both columns ——
        var retry = new Button { Content = L("Réessayer", "Try again"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(20, 11) };
        retry.Click += async (_, _) => { _problemHint.Text = L("Lecture du PC serveur…", "Reading the server PC…"); await LoadStatusAsync(); };
        _problemIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _problem.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480, Margin = new Thickness(24, 24, 24, 80), Children = { _problemIcon, _problemTitle, _problemHint, retry } });

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,36,340") };
        columns.Children.Add(left);
        Grid.SetColumn(right, 2); columns.Children.Add(right);
        Grid.SetColumnSpan(_problem, 3); columns.Children.Add(_problem);
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(56, 44 + shell.TitleBarInset, 56, 0) };
        page.Children.Add(header);
        Grid.SetRow(columns, 1); page.Children.Add(columns);
        Content = page;

        DescribeAgent(DateTimeOffset.UtcNow);
        _timer.Tick += async (_, _) => await TickAsync();
        // Jellyfin is asked only while the page shows: not from the history, behind a title opened by « Voir ».
        AttachedToVisualTree += (_, _) => { _timer.Start(); Dispatcher.UIThread.Post(() => _query.Focus(), DispatcherPriority.Input); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public override async Task LoadAsync()
    {
        await LoadStatusAsync();
        if (_upgradeOf is { } wanted && !_upgradeStarted) { _upgradeStarted = true; await StartUpgradeAsync(wanted); }
    }

    /// <summary>« Chercher une meilleure version »: the title as Jellyfin has it now (its file, its picture), then its search.</summary>
    private async Task StartUpgradeAsync(MediaItem wanted)
    {
        if (_session is not { } session) return;
        MediaItem item;
        try { item = await session.Client.ItemAsync(wanted.Id, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { if (!session.Lifetime.IsCancellationRequested) Shell.Notice(Errors.Friendly(ex)); return; }
        _upgrade = item; _upgradeSent = false;
        _upgradeTitle.Text = L($"Meilleure version de « {TitleUpgrade.Title(item)} »", $"Better version of “{TitleUpgrade.Title(item)}”");
        _upgradeLine.Text = L($"Actuellement : {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Seules les versions meilleures sont montrées. Une fois téléchargée et vérifiée (même durée, lisible par Jellyfin), la nouvelle prend la place de l’ancienne, qui part à la Corbeille du PC serveur.",
            $"Currently: {TitleUpgrade.Line(TitleUpgrade.Current(item))}. Only better versions are shown. Once downloaded and checked (same length, readable by Jellyfin), the new one takes the old one’s place, which goes to the server PC’s Recycle Bin.");
        _upgradeBanner.IsVisible = true;
        AutomationProperties.SetName(_upgradeBanner, _upgradeTitle.Text);
        _query.Text = TitleUpgrade.Query(item);
        await SearchAsync();
    }

    /// <summary>Back to the page as it is: every result, downloaded as a new title.</summary>
    private void EndUpgrade()
    {
        _upgrade = null; _upgradeSent = false; _upgradeBanner.IsVisible = false;
        _searchVersion++; _results.Children.Clear(); _resultsEmpty.IsVisible = true; _searchStatus.Text = ""; _firstBest = null;
        _query.Text = ""; _query.Focus();
    }
    public override Task RefreshAsync() => LoadStatusAsync();

    private async Task TickAsync()
    {
        // Not under the player, nor while Mira is in the Dock.
        if (Shell.Player is not null || Shell.WindowState == WindowState.Minimized) return;
        await LoadStatusAsync();
    }

    private static EmptyState Empty(string icon, string title, string text, string? note)
    {
        var empty = new EmptyState(centered: false) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 18, 0, 0) };
        empty.Show(icon, title, text, note: note);
        return empty;
    }

    /// <summary>A small quiet button of a row or a tool: its icon, its label, and the name VoiceOver reads.</summary>
    private static Button SmallButton(string label, string icon, Action click, string? name = null)
    {
        var button = new Button
        {
            Classes = { "quiet" }, Padding = new Thickness(10, 6), FontSize = 13,
            Content = Ui.Row(7, new Icon(icon, 15) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center })
        };
        button.Click += (_, _) => click();
        AutomationProperties.SetName(button, name ?? label);
        return button;
    }

    // —— What the PC has under way ——

    private async Task LoadStatusAsync()
    {
        if (_session is not { } session) return;
        if (_loading) { _again = true; return; }
        _loading = true; _again = false;
        try
        {
            var status = await session.Client.TorLinkStatusAsync(session.Lifetime);
            _problem.IsVisible = false;
            Render(status, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            if (session.Lifetime.IsCancellationRequested) return;
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.Forbidden }) ShowProblem("users", L("Réservé aux administrateurs de Jellyfin", "For Jellyfin administrators only"), ex.Message);
            else if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) ShowProblem("refresh", L("L’extension Mira est à mettre à jour", "The Mira extension needs an update"), ex.Message);
            // A page already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            else if (_filled) _subtitle.Text = Errors.Friendly(ex);
            else ShowProblem("server", L("Le PC serveur ne répond pas", "The server PC isn’t answering"), Errors.Friendly(ex));
        }
        finally
        {
            _loading = false;
            if (_again) _ = LoadStatusAsync();
        }
    }

    private void ShowProblem(string icon, string title, string hint)
    {
        _problemIcon.Kind = icon; _problemTitle.Text = title; _problemHint.Text = hint;
        _pillText.Text = L("PC serveur · Indisponible", "Server PC · Unavailable"); _pillDot.Fill = Warning;
        _subtitle.Text = PageLine;
        _problem.IsVisible = true;
    }

    /// <summary>The header: where the PC stands, in Mira web's words, and its pill.</summary>
    private void DescribeAgent(DateTimeOffset now)
    {
        var (text, ready) = _filled ? RemoteLines.Agent(_agent, now) : (L("Lecture du PC serveur…", "Reading the server PC…"), false);
        _ready = ready;
        _subtitle.Text = text;
        var (status, dot) = !_filled ? (L("Lecture…", "Reading…"), Ui.Faint) : _agent is null ? (L("Jamais ouvert", "Never opened"), Ui.Faint) : !_agent.Online ? (L("Mira fermé", "Mira closed"), Ui.Faint)
            : !string.IsNullOrEmpty(_agent.Problem) ? (L("Occupé", "Busy"), Warning) : (L("Prêt", "Ready"), Success);
        _pillText.Text = L("PC serveur · ", "Server PC · ") + status; _pillDot.Fill = dot;
        _search.IsEnabled = ready && !_searching;
    }

    private void Render(RelayStatus status, DateTimeOffset now)
    {
        _filled = true; _agent = status.Agent;
        DescribeAgent(now);
        var queue = new List<RowData>();
        // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
        foreach (var request in RemoteLines.Pending(status))
            queue.Add(new($"r:{request.Id}", $"r|{request.State}", Shown(request.Name).Title, RemoteLines.Request(request), null, () => RequestRow(request)));
        foreach (var download in status.Downloads)
        {
            var pending = status.Controls.FirstOrDefault(x => x.Target == download.Id);
            var line = pending is not null && RemoteLines.ActionLine(pending.Action) is { } asked ? asked : RemoteLines.Download(download);
            queue.Add(new($"d:{download.Id}", $"d|{download.Status}|{pending is null}", Shown(download.Name).Title, line, download.Progress, () => DownloadRow(download, pending is null)));
        }
        var placed = status.Placed.Select(x => new RowData($"p:{x.Id}", $"p|{x.ItemId}|{x.Ok}", x.Title, Sentence(x.Status), null, () => PlacedRow(x))).ToList();
        var follows = FollowRows(status.Follows, now);
        UpdateRows(_queue, queue); UpdateRows(_follows, follows); UpdateRows(_placed, placed);
        foreach (var key in _rows.Keys.Except(queue.Concat(follows).Concat(placed).Select(x => x.Key)).ToList()) _rows.Remove(key);
        _placedTitle.IsVisible = placed.Count > 0;
        _followsTitle.IsVisible = follows.Count > 0;
        _queueEmpty.IsVisible = queue.Count + follows.Count + placed.Count == 0;
        var downloading = status.Downloads.Count(x => x.Status == "downloading");
        var waiting = queue.Count - downloading;
        _summary.Text = queue.Count == 0 ? (placed.Count > 0 ? L("Rien en cours. Les derniers titres rangés sont ci-dessous.", "Nothing under way. The latest titles added are below.") : "")
            : string.Join(" · ", new[] { downloading > 0 ? L($"{downloading} en cours", $"{downloading} downloading") : "", waiting > 0 ? L($"{waiting} en attente", $"{waiting} waiting") : "" }.Where(x => x.Length > 0));
        _summary.IsVisible = _summary.Text.Length > 0;
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A release called by the title read from its name, the name itself under it (as Mira web).</summary>
    private static (string Title, string? Full) Shown(string name) => ReleaseRanking.Readable(name) is { Title.Length: > 0 } shown ? shown : (name, null);
    private static double Share(int progress) => Math.Clamp(progress, 0, 100) / 100.0;

    private void UpdateRows(StackPanel panel, List<RowData> rows)
    {
        var next = new List<Control>();
        foreach (var data in rows)
        {
            if (_rows.TryGetValue(data.Key, out var kept) && kept.Shape == data.Shape)
            {
                if (kept.Name.Text != data.Name) kept.Name.Text = data.Name;
                if (kept.Line.Text != data.Line) kept.Line.Text = data.Line;
                if (kept.Bar is not null && data.Progress is { } progress) kept.Bar.RenderTransform = new ScaleTransform(Share(progress), 1);
                AutomationProperties.SetName(kept.View, $"{data.Name}, {data.Line}");
                next.Add(kept.View);
                continue;
            }
            var made = data.Make() with { Shape = data.Shape };
            _rows[data.Key] = made; next.Add(made.View);
        }
        if (next.Count == panel.Children.Count && next.Select((x, i) => ReferenceEquals(x, panel.Children[i])).All(x => x)) return;
        panel.Children.Clear();
        panel.Children.AddRange(next);
    }

    private static QueueRow RowView(string icon, IBrush color, string name, string line, int? progress, bool failed, Panel actions, string? full = null)
    {
        var nameText = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(nameText, full ?? name);
        var lineText = new TextBlock { Text = line, FontSize = 12.5, Foreground = failed ? WarningText : Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(nameText); text.Children.Add(lineText);
        if (full is not null)
        {
            var fullText = new TextBlock { Text = full, FontSize = 11.5, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) };
            ToolTip.SetTip(fullText, full);
            text.Children.Add(fullText);
        }
        Border? bar = null;
        if (progress is { } value)
        {
            bar = new Border { Background = Accent, CornerRadius = new CornerRadius(1.5), RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative), RenderTransform = new ScaleTransform(Share(value), 1) };
            text.Children.Add(new Border { Height = 3, Margin = new Thickness(0, 10, 0, 2), Background = Line, CornerRadius = new CornerRadius(1.5), ClipToBounds = true, Child = bar });
        }
        if (actions.Children.Count > 0) text.Children.Add(actions);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new Icon(icon, 18) { Foreground = color, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) });
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var view = new Border { Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Margin = new Thickness(0, 0, 0, 8), Child = grid };
        AutomationProperties.SetName(view, $"{name}, {line}");
        return new("", view, nameText, lineText, bar);
    }

    private static WrapPanel RowButtons() => new() { Margin = new Thickness(-10, 6, 0, 0) };

    private QueueRow RequestRow(RelayRequest request)
    {
        var (name, full) = Shown(request.Name);
        var actions = new StackPanel();
        if (request.State != "claimed")
        {
            var label = request.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            var buttons = RowButtons();
            buttons.Children.Add(SmallButton(label, "close", () => _ = CancelRequestAsync(request), label + L($" « {name} »", $" “{name}”")));
            actions.Children.Add(buttons);
        }
        var failed = request.State == "failed";
        return RowView(failed ? "warning" : "clock", failed ? Warning : Ui.Faint, name, RemoteLines.Request(request), null, failed, actions, full);
    }

    private QueueRow DownloadRow(RelayDownload download, bool offer)
    {
        var (name, full) = Shown(download.Name);
        var actions = new StackPanel();
        if (offer && RemoteLines.Controllable(download))
        {
            var buttons = RowButtons();
            if (download.Status != "failed")
                buttons.Children.Add(RemoteLines.Paused(download)
                    ? SmallButton(L("Reprendre", "Resume"), "play", () => _ = ControlAsync(download, "resume"), L($"Reprendre « {name} »", $"Resume “{name}”"))
                    : SmallButton(L("Pause", "Pause"), "pause", () => _ = ControlAsync(download, "pause"), L($"Mettre en pause « {name} »", $"Pause “{name}”")));
            buttons.Children.Add(SmallButton(L("Annuler", "Cancel"), "close", () => ConfirmCancel(actions, download), L($"Annuler « {name} »", $"Cancel “{name}”")));
            actions.Children.Add(buttons);
        }
        var (icon, color) = download.Status switch { "failed" => ("warning", Warning), "completed" or "seeding" => ("check", Success), "downloading" => ("download", Ui.Ink), _ => ("clock", Ui.Faint) };
        return RowView(icon, color, name, RemoteLines.Download(download), download.Progress, download.Status == "failed", actions, full);
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more, in the row.</summary>
    private void ConfirmCancel(StackPanel actions, RelayDownload download)
    {
        var kept = actions.Children.ToList();
        actions.Children.Clear();
        actions.Children.Add(new TextBlock { Text = L("TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", "TorLink stops it and deletes what it already received on the server PC."), FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = RowButtons();
        buttons.Margin = new Thickness(-10, 4, 0, 0);
        var confirm = SmallButton(L("Annuler le téléchargement", "Cancel the download"), "close", () => _ = ControlAsync(download, "delete"), L($"Annuler le téléchargement de « {download.Name} »", $"Cancel the download of “{download.Name}”"));
        confirm.Foreground = Danger;
        buttons.Children.Add(confirm);
        buttons.Children.Add(SmallButton(L("Garder", "Keep"), "check", () => { actions.Children.Clear(); actions.Children.AddRange(kept); }, L($"Garder « {download.Name} »", $"Keep “{download.Name}”")));
        actions.Children.Add(buttons);
        Dispatcher.UIThread.Post(() => confirm.Focus(), DispatcherPriority.Input);
    }

    private QueueRow PlacedRow(RelayPlaced placed)
    {
        var actions = new StackPanel();
        if (placed.ItemId is { } id)
        {
            var buttons = RowButtons();
            buttons.Children.Add(SmallButton(L("Voir", "View"), "play", () => _ = OpenAsync(id), L($"Voir « {placed.Title} »", $"View “{placed.Title}”")));
            actions.Children.Add(buttons);
        }
        return RowView(placed.Ok ? "check" : "warning", placed.Ok ? Success : Warning, placed.Title, Sentence(placed.Status), null, false, actions);
    }

    // —— « Suivre une série »: the series followed, their releases chosen ——

    /// <summary>Each series followed, what its last check found and its latest episode taken, then the releases chosen, with « Annuler ».</summary>
    private List<RowData> FollowRows(IReadOnlyList<RelayFollow>? follows, DateTimeOffset now)
    {
        var rows = new List<RowData>();
        foreach (var follow in follows ?? [])
        {
            var line = string.Join("\n", new[] { RemoteLines.Follow(follow, now), RemoteLines.FollowNote(follow.Note), follow.Taken.FirstOrDefault() is { } taken ? RemoteLines.Taken(taken, now) : "" }.Where(x => x.Length > 0));
            rows.Add(new($"f:{follow.Id}", $"f|{follow.Query}", follow.Name, line, null, () => FollowRow(follow, line)));
            foreach (var pick in follow.Picks)
                rows.Add(new($"fp:{pick.Id}", $"fp|{pick.State}", Shown(pick.Name).Title, RemoteLines.Pick(pick, now), null, () => PickRow(pick, now)));
        }
        return rows;
    }

    private QueueRow FollowRow(RelayFollow follow, string line)
    {
        var actions = new StackPanel();
        var buttons = RowButtons();
        buttons.Children.Add(SmallButton(L("Chercher maintenant", "Search now"), "refresh", () => _ = FollowActionAsync(c => c.TorLinkCheckFollowAsync(follow.Id, _session!.Lifetime), L($"Le PC serveur cherche les nouveaux épisodes de « {follow.Name} ».", $"The server PC is looking for new episodes of “{follow.Name}”.")),
            L($"Chercher maintenant les nouveaux épisodes de « {follow.Name} »", $"Search now for new episodes of “{follow.Name}”")));
        buttons.Children.Add(SmallButton(L("Nom cherché", "Search name"), "search", () => EditQuery(actions, follow), L($"Changer le nom cherché pour « {follow.Name} »", $"Change the search name for “{follow.Name}”")));
        buttons.Children.Add(SmallButton(L("Ne plus suivre", "Stop following"), "close", () => _ = FollowActionAsync(c => c.TorLinkUnfollowAsync(follow.Id, _session!.Lifetime), L($"« {follow.Name} » n’est plus suivie.", $"No longer following “{follow.Name}”.")),
            L($"Ne plus suivre « {follow.Name} »", $"Stop following “{follow.Name}”")));
        actions.Children.Add(buttons);
        return RowView("series", Ui.Ink, follow.Name, line, null, follow.Note?.Kind is "failed" or "missing" or "unknown", actions, follow.Query is { } query ? L($"Cherchée sous « {query} »", $"Searched as “{query}”") : null);
    }

    /// <summary>The name TorLink searches, changed in the row: empty for the series' own.</summary>
    private void EditQuery(StackPanel actions, RelayFollow follow)
    {
        var kept = actions.Children.ToList();
        actions.Children.Clear();
        var box = new TextBox { Text = follow.Query ?? follow.Name, Margin = new Thickness(0, 8, 0, 0), MaxLength = RemoteDownloads.MaxQuery };
        AutomationProperties.SetName(box, L("Nom cherché", "Search name"));
        actions.Children.Add(new TextBlock { Text = L("Le nom sous lequel ses épisodes sortent, par exemple son nom japonais. Vide : son nom dans Jellyfin.", "The name its episodes come out under, its Japanese name for example. Empty: its name in Jellyfin."), FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        actions.Children.Add(box);
        void Save()
        {
            var query = RemoteDownloads.CleanQuery(box.Text);
            query = query is null || ReleaseRanking.TitleKey(query) == ReleaseRanking.TitleKey(follow.Name) ? null : query;
            _ = FollowActionAsync(c => c.TorLinkFollowAsync(follow.SeriesId, follow.Name, query, _session!.Lifetime), L("Nom cherché changé : le PC serveur cherche de nouveau.", "Search name changed: the server PC is searching again."));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Save(); } };
        var buttons = RowButtons();
        buttons.Margin = new Thickness(-10, 4, 0, 0);
        buttons.Children.Add(SmallButton(L("Enregistrer", "Save"), "check", Save, L("Enregistrer le nom cherché", "Save the search name")));
        buttons.Children.Add(SmallButton(L("Annuler", "Cancel"), "close", () => { actions.Children.Clear(); actions.Children.AddRange(kept); }, L("Garder le nom cherché", "Keep the search name")));
        actions.Children.Add(buttons);
        Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
    }

    private QueueRow PickRow(RelayPick pick, DateTimeOffset now)
    {
        var (name, full) = Shown(pick.Name);
        var actions = new StackPanel();
        if (pick.State is "waiting" or "failed")
        {
            var label = pick.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            var buttons = RowButtons();
            buttons.Children.Add(SmallButton(label, "close", () => _ = CancelPickAsync(pick), label + L($" « {name} »", $" “{name}”")));
            actions.Children.Add(buttons);
        }
        var failed = pick.State == "failed";
        return RowView(failed ? "warning" : "clock", failed ? Warning : Ui.Faint, name, RemoteLines.Pick(pick, now), null, failed, actions, full);
    }

    private async Task FollowActionAsync(Func<JellyfinClient, Task> action, string done)
    {
        if (_session is not { } session) return;
        try { await action(session.Client); Shell.Notice(done); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
        await LoadStatusAsync();
    }

    private async Task CancelPickAsync(RelayPick pick)
    {
        if (_session is not { } session) return;
        try
        {
            Shell.Notice(await session.Client.TorLinkCancelAsync(pick.Id, session.Lifetime) switch
            {
                true => pick.State == "waiting" ? L($"{pick.Episode} annulé : ce fichier ne sera plus pris, un autre de l’épisode pourra l’être.", $"{pick.Episode} canceled: this file won’t be taken again, another one of the episode may be.") : L("Retiré de la liste.", "Removed from the list."),
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC already took it: cancel it in its list as soon as it shows up there."),
                null => L("Cet épisode n’est plus en attente.", "This episode is no longer waiting.")
            });
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
        await LoadStatusAsync();
    }

    /// <summary>« Voir »: the title's page, as from the library.</summary>
    private async Task OpenAsync(string id)
    {
        if (_session is not { } session) return;
        try { Shell.Open(await session.ItemAsync(id)); }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
    }

    private async Task CancelRequestAsync(RelayRequest request)
    {
        if (_session is not { } session) return;
        try
        {
            Shell.Notice(await session.Client.TorLinkCancelAsync(request.Id, session.Lifetime) switch
            {
                true => request.State == "waiting" ? L("Demande annulée.", "Request canceled.") : L("Retiré de la liste.", "Removed from the list."),
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC already took it: cancel it in its list as soon as it shows up there."),
                null => L("Cette demande n’est plus là.", "This request is no longer there.")
            });
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
        await LoadStatusAsync();
    }

    private async Task ControlAsync(RelayDownload download, string action)
    {
        if (_session is not { } session) return;
        try
        {
            await session.Client.TorLinkControlAsync(download.Id, action, session.Lifetime);
            if (RemoteLines.ActionLine(action) is { } said) Shell.Notice(said);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { Shell.Notice(Errors.Friendly(ex)); }
        await LoadStatusAsync();
    }

    // —— Searching ——

    /// <summary>
    /// A search TorLink runs on the PC of Jellyfin, in films, shows and anime at once, followed every second until it
    /// answers (90 s at most).
    /// </summary>
    private async Task SearchAsync()
    {
        if (_session is not { } session) return;
        if (RemoteDownloads.CleanQuery(_query.Text) is not { } query) { _query.Focus(); return; }
        if (!_ready) { _searchStatus.Text = RemoteLines.Agent(_agent, DateTimeOffset.UtcNow).Text; return; }
        var mine = ++_searchVersion; var ct = session.Lifetime;
        _searching = true; _search.IsEnabled = false; _search.Content = L("Recherche…", "Searching…");
        _searchStatus.Text = L($"Recherche de « {query} » dans les films, les séries et les animes sur le PC serveur…", $"Searching for “{query}” in movies, shows and anime on the server PC…");
        _results.Opacity = 0.45;
        void End(string text) { _searchStatus.Text = text; _results.Children.Clear(); _resultsEmpty.IsVisible = false; }
        try
        {
            var id = await session.Client.TorLinkSearchAsync(query, RemoteDownloads.All, ct);
            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);
                if (mine != _searchVersion) return;
                if (DateTime.UtcNow - started > SearchWait) { End(L("Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche.", "The server PC didn’t answer in time. Check that Mira is open there, then search again.")); return; }
                RelaySearch? state;
                // A hiccup on the way: asked again a second later.
                try { state = await session.Client.TorLinkSearchStateAsync(id, ct); }
                catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested && ex is not RelayRefusedException) { continue; }
                if (mine != _searchVersion) return;
                // Forgotten (Jellyfin restarted meanwhile): it will not come back.
                if (state is null) { End(L("Cette recherche a été perdue : relance-la.", "This search was lost: run it again.")); return; }
                if (state.State == "failed") { End(string.IsNullOrEmpty(state.Error) ? L("La recherche n’a pas abouti.", "The search didn’t work.") : state.Error); return; }
                if (state.State == "done") { ShowHits(id, query, state.Results ?? []); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { if (mine == _searchVersion) End(Errors.Friendly(ex)); }
        finally
        {
            if (mine == _searchVersion) { _searching = false; _search.Content = L("Chercher", "Search"); _search.IsEnabled = _ready; _results.Opacity = 1; }
        }
    }

    /// <summary>
    /// The results by title and episode (or season, or film), the best release of each first: its quality and source,
    /// its size and sources, its full name; the other releases folded under it.
    /// </summary>
    private void ShowHits(string searchId, string query, IReadOnlyList<RelayHit> hits)
    {
        _resultsEmpty.IsVisible = false; _results.Children.Clear(); _resultsScroll.Offset = default; _firstBest = null;
        // « Chercher une meilleure version »: only this title, in a better picture than its file.
        if (_upgrade is { } upgrade)
        {
            var better = TitleUpgrade.Candidates(upgrade, hits);
            var now = TitleUpgrade.Line(TitleUpgrade.Current(upgrade));
            var count = Count(better.Count, "version meilleure", "versions meilleures", "better version", "better versions");
            _searchStatus.Text = better.Count == 0
                ? L($"Aucune version meilleure que l’actuelle ({now}) parmi les {hits.Count} résultats pour « {query} ». Réessaie plus tard, ou avec un autre nom.",
                    $"No version better than the current one ({now}) among the {hits.Count} results for “{query}”. Try again later, or with another name.")
                : L($"{count} que l’actuelle, la meilleure en premier.", $"{count} than the current one, the best first.");
            foreach (var group in ReleaseRanking.Group(better)) _results.Children.Add(GroupView(searchId, group));
            return;
        }
        if (hits.Count == 0) { _searchStatus.Text = L($"Aucun résultat pour « {query} » dans les films, les séries et les animes. Essaie un autre titre.", $"No results for “{query}” in movies, shows and anime. Try another title."); return; }
        var groups = ReleaseRanking.Group(hits);
        var versions = Count(hits.Count, "version", "versions", "version", "versions"); var titles = Count(groups.Count, "titre", "titres", "title", "titles");
        _searchStatus.Text = L($"{versions} pour « {query} », regroupées en {titles}. La meilleure de chacun en premier.", $"{versions} for “{query}”, grouped into {titles}. The best of each first.");
        var views = groups.Select(group => (Group: group, View: GroupView(searchId, group))).ToList();
        foreach (var (_, view) in views) _results.Children.Add(view);
        _ = MarkOwnedAsync(views);
    }

    /// <summary>
    /// « Déjà dans ta bibliothèque » under a title Jellyfin has (OwnedTitles), so that it is not downloaded twice: Jellyfin
    /// asked once per title found, the episodes of a series once, after the results show.
    /// </summary>
    private async Task MarkOwnedAsync(List<(ReleaseGroup Group, Border View)> views)
    {
        if (_session is not { } session) return;
        await Task.WhenAll(OwnedTitles.Lookups(views.Select(x => x.Group)).Select(async lookup =>
        {
            var mine = views.Where(x => OwnedTitles.LookupKey(x.Group) == lookup.Key).ToList();
            List<MediaItem> items, episodes = [];
            try
            {
                items = (await session.Client.OwnedAsync(lookup.Title)).Items;
                if (!lookup.Film && OwnedTitles.Line(mine[0].Group, items)?.ItemId is { } series && mine.Any(x => !x.Group.Release.Absolute))
                    episodes = (await session.Client.OwnedEpisodesAsync(series)).Items;
            }
            catch (Exception ex) when (Errors.Expected(ex)) { return; } // Nothing said rather than a guess.
            foreach (var (group, view) in mine)
            {
                if (!_results.Children.Contains(view) || view.Child is not StackPanel panel || OwnedTitles.Line(group, items, episodes) is not { } line) continue;
                var button = new Button
                {
                    Classes = { "quiet" }, Padding = new Thickness(10, 5), Margin = new Thickness(-10, -6, 0, 8), HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = line.Owned ? Warning : Secondary,
                    Content = Ui.Row(7, new Icon(line.Owned ? "check" : "info", 14) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = line.Text, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }),
                };
                AutomationProperties.SetName(button, L($"{line.Text} : ouvrir « {group.Title} »", $"{line.Text}: open “{group.Title}”"));
                var id = line.ItemId;
                button.Click += (_, _) => _ = OpenAsync(id);
                panel.Children.Insert(1, button);
            }
        }));
    }

    private Border GroupView(string searchId, ReleaseGroup group)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (group.Label.Length > 0)
        {
            var episode = new Border
            {
                CornerRadius = new CornerRadius(6), Background = Ui.Raised, Padding = new Thickness(9, 4), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = group.Label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary }
            };
            DockPanel.SetDock(episode, Dock.Right); head.Children.Add(episode);
        }
        if (RemoteLines.KindLabel(group.Category) is { Length: > 0 } kind)
        {
            var tag = new TextBlock { Text = kind, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Secondary, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tag, Dock.Right); head.Children.Add(tag);
        }
        var title = new TextBlock { Text = group.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, group.Title);
        head.Children.Add(title);
        panel.Children.Add(head);
        panel.Children.Add(ReleaseView(searchId, group.Best, best: true));
        if (group.Others.Count > 0)
        {
            var others = new StackPanel { IsVisible = false };
            foreach (var entry in group.Others) others.Children.Add(ReleaseView(searchId, entry, best: false));
            var more = Count(group.Others.Count, "autre version", "autres versions", "other version", "other versions");
            var text = new TextBlock { Text = more, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new Icon("down", 14) { VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Button { Classes = { "quiet" }, Padding = new Thickness(10, 6), Margin = new Thickness(-10, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Secondary, Content = Ui.Row(6, text, chevron) };
            AutomationProperties.SetName(toggle, L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            toggle.Click += (_, _) =>
            {
                others.IsVisible = !others.IsVisible;
                text.Text = others.IsVisible ? L("Masquer les autres versions", "Hide the other versions") : more; chevron.Kind = others.IsVisible ? "up" : "down";
                AutomationProperties.SetName(toggle, others.IsVisible ? L("Masquer les autres versions", "Hide the other versions") : L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            };
            panel.Children.Add(toggle); panel.Children.Add(others);
        }
        var card = new Border { Background = Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        AutomationProperties.SetName(card, string.Join(" · ", new[] { group.Title, group.Label, RemoteLines.KindLabel(group.Category) }.Where(x => x.Length > 0)));
        return card;
    }

    private Control ReleaseView(string searchId, RankedRelease entry, bool best)
    {
        var hit = entry.Hit;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var quality = new WrapPanel();
        quality.Children.Add(new TextBlock { Text = ReleaseRanking.QualityLine(entry.Release) is { Length: > 0 } line ? line : L("Qualité non précisée", "Quality not given"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (best)
            quality.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Accent, Padding = new Thickness(7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = L("Meilleur choix", "Best pick"), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = OnAccent }
            });
        text.Children.Add(quality);
        text.Children.Add(new TextBlock { Text = RemoteLines.Hit(hit), FontSize = 12.5, Foreground = Secondary, Margin = new Thickness(0, 4, 0, 0) });
        var name = new TextBlock { Text = hit.Name, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        ToolTip.SetTip(name, hit.Name);
        text.Children.Add(name);
        if (ReleaseRanking.Warning(entry) is { Length: > 0 } caution)
            text.Children.Add(new TextBlock { Text = caution, FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);
        var key = searchId + "/" + hit.Id;
        var button = new Button { Padding = new Thickness(16, 9), VerticalAlignment = VerticalAlignment.Center };
        if (best) _firstBest ??= (searchId, hit, button);
        if (best) button.Classes.Add("primary");
        ShowSendState(button, _sent.Contains(key));
        AutomationProperties.SetName(button, _upgrade is null ? L($"Télécharger {hit.Name}", $"Download {hit.Name}") : L($"Remplacer par {hit.Name}", $"Replace with {hit.Name}"));
        button.Click += async (_, _) => await SendHitAsync(searchId, hit, button);
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        if (best) return grid;
        // The other versions, each under a hairline.
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private void ShowSendState(Button button, bool sent)
    {
        var replace = _upgrade is not null;
        button.Content = Ui.Row(7, new Icon(sent ? "check" : replace ? "sparkle" : "download", 15) { VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = sent ? replace ? L("Demandé", "Requested") : L("Envoyé", "Sent") : replace ? L("Remplacer", "Replace") : L("Télécharger", "Download"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        button.IsEnabled = !sent;
    }

    private async Task SendHitAsync(string searchId, RelayHit hit, Button button)
    {
        if (_session is not { } session) return;
        var replace = _upgrade?.Id;
        // One better version at a time: the second would only replace the first. A Mira of 0.8.5 on the PC would keep both.
        if (replace is not null && _upgradeSent) { Shell.Notice(L("Une meilleure version de ce titre est déjà demandée : elle s’affiche à droite.", "A better version of this title is already requested: it shows on the right.")); return; }
        if (replace is not null && _agent is { Replaces: false }) { Shell.Notice(L("Mira sur le PC serveur ne sait pas encore remplacer un titre : mets-le à jour.", "Mira on the server PC can’t replace a title yet: update it.")); return; }
        button.IsEnabled = false;
        try
        {
            await session.Client.TorLinkRequestAsync(searchId, hit, session.Lifetime, replace);
            _sent.Add(searchId + "/" + hit.Id); ShowSendState(button, sent: true);
            if (replace is not null) _upgradeSent = true;
            await SentAsync(replace is not null);
        }
        catch (Exception ex) when (Errors.Expected(ex)) { button.IsEnabled = true; Shell.Notice(Errors.Friendly(ex)); }
    }

    /// <summary>Its row, with its progress, is at the top of the right column.</summary>
    private async Task SentAsync(bool replacing = false)
    {
        Shell.Notice(replacing ? L("Demandé au PC serveur : la nouvelle version prendra la place de l’ancienne une fois téléchargée et vérifiée.", "Requested from the server PC: the new version will take the old one’s place once downloaded and checked.")
            : _ready ? L("Envoyé au PC serveur : sa progression s’affiche à droite.", "Sent to the server PC: its progress shows on the right.") : L("Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.", "Request kept: the server PC will do it as soon as Mira is open there."));
        _queueScroll.Offset = default;
        await LoadStatusAsync();
    }

    // —— A magnet link ——

    private void OpenMagnet()
    {
        _magnetPanel.IsVisible = true; _magnetButton.IsVisible = _magnetError.IsVisible = false;
        Dispatcher.UIThread.Post(() => _magnet.Focus(), DispatcherPriority.Input);
    }
    private void CloseMagnet()
    {
        _magnetPanel.IsVisible = _magnetError.IsVisible = false; _magnetButton.IsVisible = true;
        _magnet.Text = ""; _magnetSend.IsEnabled = true;
    }

    private async Task SendMagnetAsync()
    {
        if (_session is not { } session || !_magnetSend.IsEnabled) return;
        var magnet = (_magnet.Text ?? "").Trim();
        if (RemoteDownloads.InfoHash(magnet) is null) { ShowMagnetError(L("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).", "That’s not a magnet link (“magnet:?xt=urn:btih:…”).")); _magnet.Focus(); return; }
        _magnetSend.IsEnabled = false;
        try
        {
            await session.Client.TorLinkRequestAsync(magnet, session.Lifetime);
            CloseMagnet(); _magnetButton.Focus();
            await SentAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { ShowMagnetError(Errors.Friendly(ex)); }
        finally { _magnetSend.IsEnabled = true; }
    }
    private void ShowMagnetError(string text) { _magnetError.Text = text; _magnetError.IsVisible = true; }
}
