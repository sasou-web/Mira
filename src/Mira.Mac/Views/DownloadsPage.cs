using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core.TorLink;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// Téléchargements, for the administrators of Jellyfin. TorLink runs on the PC of Jellyfin only (Mira for Windows), where
/// its downloads land next to the library; this page sends it searches and downloads through the relay of the extension
/// (<c>Mira/torlink</c>), as Mira web's « Télécharger sur le PC serveur » and Mira for Windows away from that PC do: the
/// best release of each title or episode first (<see cref="ReleaseRanking"/>), what the PC has under way with its
/// progress and time left (<see cref="RemoteLines"/>), read every 3 s while the page shows, and only then.
/// </summary>
public sealed class DownloadsPage : Page
{
    public override string Rail => "downloads";
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(90);
    private const string PageLine = "Chaque téléchargement terminé rejoint ta bibliothèque.";
    // The Windows app's states and secondary ink (Themes/Cinema.xaml).
    private static readonly IBrush Success = Ui.Brush("#86C6A2"), Warning = Ui.Brush("#D5B787"), WarningText = Ui.Brush("#E9DCC3"), Danger = Ui.Brush("#F3AD99"),
        Secondary = Ui.Brush("#C5C5CA"), Line = Ui.Brush("#26262A"), Surface = Ui.Brush("#111113"), Accent = Ui.Brush("#F5F5F7"), OnAccent = Ui.Brush("#0B0B0C");
    private readonly Session? _session;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly TextBlock _subtitle = new() { Text = "Lecture du PC serveur…", FontSize = 14, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _pillText = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _pillDot = new() { Width = 8, Height = 8, Fill = Ui.Faint, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _query = new() { Watermark = "Un film, une série, un anime…", MaxLength = 200 };
    private readonly Button _search = new() { Content = "Chercher", Classes = { "primary" }, Padding = new Thickness(22, 11), Margin = new Thickness(10, 0, 0, 0), IsEnabled = false };
    private readonly List<(string Key, Button Chip)> _chips = [];
    private string _category = "movies";
    private readonly Button _magnetButton;
    private readonly Border _magnetPanel;
    private readonly TextBox _magnet = new() { Watermark = "magnet:?xt=urn:btih:…", FontSize = 14, Padding = new Thickness(12, 9) };
    private readonly Button _magnetSend = new() { Content = "Envoyer au PC serveur", Classes = { "primary" }, Padding = new Thickness(18, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _magnetError = new() { FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
    private readonly TextBlock _searchStatus = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _results = new() { Margin = new Thickness(0, 0, 0, 40) };
    private readonly ScrollViewer _resultsScroll;
    private readonly Control _resultsEmpty;
    private readonly TextBlock _summary = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _queue = new(), _placed = new();
    private readonly TextBlock _placedTitle = new() { Text = "Rangés dans ta bibliothèque", FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 14, 0, 10), IsVisible = false };
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

    public DownloadsPage(MainWindow shell) : base(shell)
    {
        _session = shell.Session;
        // —— Header: the page, where the PC stands, and its pill ——
        var pill = new Border
        {
            Background = Ui.Raised, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 10, 0, 0),
            Child = Ui.Row(8, _pillDot, _pillText)
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 26) };
        DockPanel.SetDock(pill, Dock.Right); header.Children.Add(pill);
        header.Children.Add(Ui.Column(0, Ui.Text("Téléchargements", 34, FontWeight.ExtraBold), _subtitle));

        // —— Left: the search, a magnet link, the results ——
        _query.InnerLeftContent = new Icon("search", 17) { Foreground = Ui.Faint, Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false };
        var clear = Ui.IconButton("close", "Effacer · Échap", () => { _query.Text = ""; _query.Focus(); }, "icon", 14);
        clear.Width = clear.Height = 30; clear.Margin = new Thickness(0, 0, 6, 0); clear.IsVisible = false;
        AutomationProperties.SetName(clear, "Effacer la recherche");
        _query.InnerRightContent = clear;
        _query.TextChanged += (_, _) => clear.IsVisible = !string.IsNullOrEmpty(_query.Text);
        _query.Padding = new Thickness(10, 11, 4, 11);
        AutomationProperties.SetName(_query, "Que veux-tu télécharger ?");
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SearchAsync(); }
            // Échap first clears the search, then (a second press) goes back as usual.
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_query.Text)) { e.Handled = true; _query.Text = ""; }
        };
        _search.Click += (_, _) => _ = SearchAsync();
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        bar.Children.Add(_query); Grid.SetColumn(_search, 1); bar.Children.Add(_search);

        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(kinds, "Catégorie");
        foreach (var (key, label) in RemoteLines.Categories)
        {
            var chip = new Button { Content = label, Classes = { "chip" } };
            chip.Classes.Set("on", key == _category);
            chip.Click += (_, _) => { _category = key; foreach (var (other, button) in _chips) button.Classes.Set("on", other == key); };
            _chips.Add((key, chip)); kinds.Children.Add(chip);
        }
        _magnetButton = SmallButton("Coller un lien magnet", "link", OpenMagnet);
        var tools = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_magnetButton, Dock.Right); tools.Children.Add(_magnetButton); tools.Children.Add(kinds);

        AutomationProperties.SetName(_magnet, "Lien magnet");
        _magnet.TextChanged += (_, _) => _magnetError.IsVisible = false;
        _magnet.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; _ = SendMagnetAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseMagnet(); _magnetButton.Focus(); }
        };
        _magnetSend.Click += (_, _) => _ = SendMagnetAsync();
        var close = new Button { Content = "Fermer", Classes = { "quiet" }, Padding = new Thickness(14, 9), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => { CloseMagnet(); _magnetButton.Focus(); };
        var magnetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        magnetRow.Children.Add(_magnet);
        Grid.SetColumn(_magnetSend, 1); magnetRow.Children.Add(_magnetSend);
        Grid.SetColumn(close, 2); magnetRow.Children.Add(close);
        _magnetPanel = new Border
        {
            Background = Surface, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14), Margin = new Thickness(0, 12, 0, 0), IsVisible = false,
            Child = Ui.Column(0,
                new TextBlock { Text = "Coller un lien magnet", FontSize = 14, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Le lien d’un site que tu connais : TorLink le télécharge sur le PC serveur, puis Mira le range dans ta bibliothèque.", FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) },
                magnetRow, _magnetError)
        };
        _searchStatus.Margin = new Thickness(0, 16, 0, 14);
        var top = Ui.Column(0, bar, tools, _magnetPanel, _searchStatus);
        _resultsScroll = new ScrollViewer { Content = _results };
        _resultsEmpty = Empty("search", "Cherche un film, une série ou un anime",
            "TorLink le cherche sur le PC serveur. Mira regroupe les versions par titre et par épisode, et met la meilleure en avant : la qualité de la source d’abord, le 1080p, puis MULTI ou VF.",
            "Ne télécharge que ce que tu as le droit d’obtenir.");
        var found = new Grid(); found.Children.Add(_resultsScroll); found.Children.Add(_resultsEmpty);
        var left = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); left.Children.Add(top); left.Children.Add(found);

        // —— Right: what the PC of Jellyfin has under way, then what it put in the library ——
        var queueHead = Ui.Column(0, new TextBlock { Text = "Sur le PC serveur", FontSize = 19, FontWeight = FontWeight.Bold }, _summary);
        queueHead.Margin = new Thickness(2, 0, 0, 16);
        var list = Ui.Column(0, _queue, _placedTitle, _placed);
        list.Margin = new Thickness(0, 0, 0, 40);
        _queueScroll = new ScrollViewer { Content = list };
        _queueEmpty = Empty("download", "Rien en cours", "Ce que tu envoies apparaît ici avec sa progression et le temps restant, puis rejoint ta bibliothèque.", null);
        var queued = new Grid(); queued.Children.Add(_queueScroll); queued.Children.Add(_queueEmpty);
        var right = new DockPanel();
        DockPanel.SetDock(queueHead, Dock.Top); right.Children.Add(queueHead); right.Children.Add(queued);

        // —— Why the PC cannot be reached, over both columns ——
        var retry = new Button { Content = "Réessayer", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(20, 11) };
        retry.Click += async (_, _) => { _problemHint.Text = "Lecture du PC serveur…"; await LoadStatusAsync(); };
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

    public override Task LoadAsync() => LoadStatusAsync();
    public override Task RefreshAsync() => LoadStatusAsync();

    private async Task TickAsync()
    {
        // Not under the player, nor while Mira is in the Dock.
        if (Shell.Player is not null || Shell.WindowState == WindowState.Minimized) return;
        await LoadStatusAsync();
    }

    private static Control Empty(string icon, string title, string text, string? note)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 540, Margin = new Thickness(2, 18, 0, 0) };
        panel.Children.Add(new Icon(icon, 28) { Foreground = Ui.Faint, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        if (note is not null) panel.Children.Add(new TextBlock { Text = note, FontSize = 13, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        return panel;
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
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.Forbidden }) ShowProblem("users", "Réservé aux administrateurs de Jellyfin", ex.Message);
            else if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) ShowProblem("refresh", "L’extension Mira est à mettre à jour", ex.Message);
            // A page already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            else if (_filled) _subtitle.Text = Errors.Friendly(ex);
            else ShowProblem("server", "Le PC serveur ne répond pas", Errors.Friendly(ex));
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
        _pillText.Text = "PC serveur · Indisponible"; _pillDot.Fill = Warning;
        _subtitle.Text = PageLine;
        _problem.IsVisible = true;
    }

    /// <summary>The header: where the PC stands, in Mira web's words, and its pill.</summary>
    private void DescribeAgent(DateTimeOffset now)
    {
        var (text, ready) = _filled ? RemoteLines.Agent(_agent, now) : ("Lecture du PC serveur…", false);
        _ready = ready;
        _subtitle.Text = text;
        var (status, dot) = !_filled ? ("Lecture…", Ui.Faint) : _agent is null ? ("Jamais ouvert", Ui.Faint) : !_agent.Online ? ("Mira fermé", Ui.Faint)
            : !string.IsNullOrEmpty(_agent.Problem) ? ("Occupé", Warning) : ("Prêt", Success);
        _pillText.Text = "PC serveur · " + status; _pillDot.Fill = dot;
        _search.IsEnabled = ready && !_searching;
    }

    private void Render(RelayStatus status, DateTimeOffset now)
    {
        _filled = true; _agent = status.Agent;
        DescribeAgent(now);
        var queue = new List<RowData>();
        // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
        foreach (var request in RemoteLines.Pending(status))
            queue.Add(new($"r:{request.Id}", $"r|{request.State}", request.Name, RemoteLines.Request(request), null, () => RequestRow(request)));
        foreach (var download in status.Downloads)
        {
            var pending = status.Controls.FirstOrDefault(x => x.Target == download.Id);
            var line = pending is not null && RemoteLines.ActionLine(pending.Action) is { } asked ? asked : RemoteLines.Download(download);
            queue.Add(new($"d:{download.Id}", $"d|{download.Status}|{pending is null}", download.Name, line, download.Progress, () => DownloadRow(download, pending is null)));
        }
        var placed = status.Placed.Select(x => new RowData($"p:{x.Id}", $"p|{x.ItemId}|{x.Ok}", x.Title, Sentence(x.Status), null, () => PlacedRow(x))).ToList();
        UpdateRows(_queue, queue); UpdateRows(_placed, placed);
        foreach (var key in _rows.Keys.Except(queue.Concat(placed).Select(x => x.Key)).ToList()) _rows.Remove(key);
        _placedTitle.IsVisible = placed.Count > 0;
        _queueEmpty.IsVisible = queue.Count + placed.Count == 0;
        var downloading = status.Downloads.Count(x => x.Status == "downloading");
        var waiting = queue.Count - downloading;
        _summary.Text = queue.Count == 0 ? (placed.Count > 0 ? "Rien en cours. Les derniers titres rangés sont ci-dessous." : "")
            : string.Join(" · ", new[] { downloading > 0 ? $"{downloading} en cours" : "", waiting > 0 ? $"{waiting} en attente" : "" }.Where(x => x.Length > 0));
        _summary.IsVisible = _summary.Text.Length > 0;
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
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

    private static QueueRow RowView(string icon, IBrush color, string name, string line, int? progress, bool failed, Panel actions)
    {
        var nameText = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(nameText, name);
        var lineText = new TextBlock { Text = line, FontSize = 12.5, Foreground = failed ? WarningText : Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(nameText); text.Children.Add(lineText);
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
        var actions = new StackPanel();
        if (request.State != "claimed")
        {
            var label = request.State == "waiting" ? "Annuler" : "Retirer";
            var buttons = RowButtons();
            buttons.Children.Add(SmallButton(label, "close", () => _ = CancelRequestAsync(request), $"{label} « {request.Name} »"));
            actions.Children.Add(buttons);
        }
        var failed = request.State == "failed";
        return RowView(failed ? "warning" : "clock", failed ? Warning : Ui.Faint, request.Name, RemoteLines.Request(request), null, failed, actions);
    }

    private QueueRow DownloadRow(RelayDownload download, bool offer)
    {
        var actions = new StackPanel();
        if (offer && RemoteLines.Controllable(download))
        {
            var buttons = RowButtons();
            if (download.Status != "failed")
                buttons.Children.Add(RemoteLines.Paused(download)
                    ? SmallButton("Reprendre", "play", () => _ = ControlAsync(download, "resume"), $"Reprendre « {download.Name} »")
                    : SmallButton("Pause", "pause", () => _ = ControlAsync(download, "pause"), $"Mettre en pause « {download.Name} »"));
            buttons.Children.Add(SmallButton("Annuler", "close", () => ConfirmCancel(actions, download), $"Annuler « {download.Name} »"));
            actions.Children.Add(buttons);
        }
        var (icon, color) = download.Status switch { "failed" => ("warning", Warning), "completed" or "seeding" => ("check", Success), "downloading" => ("download", Ui.Ink), _ => ("clock", Ui.Faint) };
        return RowView(icon, color, download.Name, RemoteLines.Download(download), download.Progress, download.Status == "failed", actions);
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more, in the row.</summary>
    private void ConfirmCancel(StackPanel actions, RelayDownload download)
    {
        var kept = actions.Children.ToList();
        actions.Children.Clear();
        actions.Children.Add(new TextBlock { Text = "TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", FontSize = 12.5, Foreground = WarningText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = RowButtons();
        buttons.Margin = new Thickness(-10, 4, 0, 0);
        var confirm = SmallButton("Annuler le téléchargement", "close", () => _ = ControlAsync(download, "delete"), $"Annuler le téléchargement de « {download.Name} »");
        confirm.Foreground = Danger;
        buttons.Children.Add(confirm);
        buttons.Children.Add(SmallButton("Garder", "check", () => { actions.Children.Clear(); actions.Children.AddRange(kept); }, $"Garder « {download.Name} »"));
        actions.Children.Add(buttons);
        Dispatcher.UIThread.Post(() => confirm.Focus(), DispatcherPriority.Input);
    }

    private QueueRow PlacedRow(RelayPlaced placed)
    {
        var actions = new StackPanel();
        if (placed.ItemId is { } id)
        {
            var buttons = RowButtons();
            buttons.Children.Add(SmallButton("Voir", "play", () => _ = OpenAsync(id), $"Voir « {placed.Title} »"));
            actions.Children.Add(buttons);
        }
        return RowView(placed.Ok ? "check" : "warning", placed.Ok ? Success : Warning, placed.Title, Sentence(placed.Status), null, false, actions);
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
                true => request.State == "waiting" ? "Demande annulée." : "Retiré de la liste.",
                false => "Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.",
                null => "Cette demande n’est plus là."
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

    /// <summary>A search TorLink runs on the PC of Jellyfin, followed every second until it answers (90 s at most).</summary>
    private async Task SearchAsync()
    {
        if (_session is not { } session) return;
        if (RemoteDownloads.CleanQuery(_query.Text) is not { } query) { _query.Focus(); return; }
        if (!_ready) { _searchStatus.Text = RemoteLines.Agent(_agent, DateTimeOffset.UtcNow).Text; return; }
        var category = _category; var label = RemoteLines.CategoryLabel(category);
        var mine = ++_searchVersion; var ct = session.Lifetime;
        _searching = true; _search.IsEnabled = false; _search.Content = "Recherche…";
        _searchStatus.Text = $"Recherche de « {query} » en {label} sur le PC serveur…";
        _results.Opacity = 0.45;
        void End(string text) { _searchStatus.Text = text; _results.Children.Clear(); _resultsEmpty.IsVisible = false; }
        try
        {
            var id = await session.Client.TorLinkSearchAsync(query, category, ct);
            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);
                if (mine != _searchVersion) return;
                if (DateTime.UtcNow - started > SearchWait) { End("Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche."); return; }
                RelaySearch? state;
                // A hiccup on the way: asked again a second later.
                try { state = await session.Client.TorLinkSearchStateAsync(id, ct); }
                catch (Exception ex) when (Errors.Expected(ex) && !ct.IsCancellationRequested && ex is not RelayRefusedException) { continue; }
                if (mine != _searchVersion) return;
                // Forgotten (Jellyfin restarted meanwhile): it will not come back.
                if (state is null) { End("Cette recherche a été perdue : relance-la."); return; }
                if (state.State == "failed") { End(string.IsNullOrEmpty(state.Error) ? "La recherche n’a pas abouti." : state.Error); return; }
                if (state.State == "done") { ShowHits(id, query, category, state.Results ?? []); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (Errors.Expected(ex)) { if (mine == _searchVersion) End(Errors.Friendly(ex)); }
        finally
        {
            if (mine == _searchVersion) { _searching = false; _search.Content = "Chercher"; _search.IsEnabled = _ready; _results.Opacity = 1; }
        }
    }

    /// <summary>
    /// The results by title and episode (or season, or film), the best release of each first: its quality and source,
    /// its size and sources, its full name; the other releases folded under it.
    /// </summary>
    private void ShowHits(string searchId, string query, string category, IReadOnlyList<RelayHit> hits)
    {
        var label = RemoteLines.CategoryLabel(category);
        _resultsEmpty.IsVisible = false; _results.Children.Clear(); _resultsScroll.Offset = default; _firstBest = null;
        if (hits.Count == 0) { _searchStatus.Text = $"Aucun résultat pour « {query} » en {label}. Essaie un autre titre, ou une autre catégorie."; return; }
        var groups = ReleaseRanking.Group(hits, category);
        _searchStatus.Text = $"{Count(hits.Count, "version", "versions")} pour « {query} » en {label}, regroupées en {Count(groups.Count, "titre", "titres")}. La meilleure de chacun en premier.";
        foreach (var group in groups) _results.Children.Add(GroupView(searchId, group));
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

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
        var title = new TextBlock { Text = group.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(title, group.Title);
        head.Children.Add(title);
        panel.Children.Add(head);
        panel.Children.Add(ReleaseView(searchId, group.Best, best: true));
        if (group.Others.Count > 0)
        {
            var others = new StackPanel { IsVisible = false };
            foreach (var entry in group.Others) others.Children.Add(ReleaseView(searchId, entry, best: false));
            var more = Count(group.Others.Count, "autre version", "autres versions");
            var text = new TextBlock { Text = more, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var chevron = new Icon("down", 14) { VerticalAlignment = VerticalAlignment.Center };
            var toggle = new Button { Classes = { "quiet" }, Padding = new Thickness(10, 6), Margin = new Thickness(-10, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Foreground = Secondary, Content = Ui.Row(6, text, chevron) };
            AutomationProperties.SetName(toggle, $"{more} de {group.Title}");
            toggle.Click += (_, _) =>
            {
                others.IsVisible = !others.IsVisible;
                text.Text = others.IsVisible ? "Masquer les autres versions" : more; chevron.Kind = others.IsVisible ? "up" : "down";
                AutomationProperties.SetName(toggle, others.IsVisible ? "Masquer les autres versions" : $"{more} de {group.Title}");
            };
            panel.Children.Add(toggle); panel.Children.Add(others);
        }
        var card = new Border { Background = Surface, CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        AutomationProperties.SetName(card, group.Label.Length > 0 ? $"{group.Title} · {group.Label}" : group.Title);
        return card;
    }

    private Control ReleaseView(string searchId, RankedRelease entry, bool best)
    {
        var hit = entry.Hit;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var quality = new WrapPanel();
        quality.Children.Add(new TextBlock { Text = ReleaseRanking.QualityLine(entry.Release) is { Length: > 0 } line ? line : "Qualité non précisée", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (best)
            quality.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5), Background = Accent, Padding = new Thickness(7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "Meilleur choix", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = OnAccent }
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
        AutomationProperties.SetName(button, $"Télécharger {hit.Name}");
        button.Click += async (_, _) => await SendHitAsync(searchId, hit, button);
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        if (best) return grid;
        // The other versions, each under a hairline.
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private static void ShowSendState(Button button, bool sent)
    {
        button.Content = Ui.Row(7, new Icon(sent ? "check" : "download", 15) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = sent ? "Envoyé" : "Télécharger", FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        button.IsEnabled = !sent;
    }

    private async Task SendHitAsync(string searchId, RelayHit hit, Button button)
    {
        if (_session is not { } session) return;
        button.IsEnabled = false;
        try
        {
            await session.Client.TorLinkRequestAsync(searchId, hit, session.Lifetime);
            _sent.Add(searchId + "/" + hit.Id); ShowSendState(button, sent: true);
            await SentAsync();
        }
        catch (Exception ex) when (Errors.Expected(ex)) { button.IsEnabled = true; Shell.Notice(Errors.Friendly(ex)); }
    }

    /// <summary>Its row, with its progress, is at the top of the right column.</summary>
    private async Task SentAsync()
    {
        Shell.Notice(_ready ? "Envoyé au PC serveur : sa progression s’affiche à droite." : "Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.");
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
        if (RemoteDownloads.InfoHash(magnet) is null) { ShowMagnetError("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »)."); _magnet.Focus(); return; }
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
