using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The Downloads page as Mira web's « Télécharger sur le PC serveur » shows it: the best release of each title or episode
/// first (<see cref="ReleaseRanking"/>), what TorLink has under way with its progress and time left
/// (<see cref="RemoteLines"/>), read every 3 s while the page shows, and only then. Away from the PC of Jellyfin, where
/// TorLink runs only (its downloads land next to the library), the page sends it searches and downloads through the relay
/// of the extension (<c>Mira/torlink</c>). On that PC (<see cref="_serverLocal"/>), the same screen runs them with this
/// PC's TorLink: « torlnk search », and TorLink without a window for the downloads (MainWindow.TorLinkRemote.cs); TorLink's
/// own terminal stays one button away (<see cref="PlayerSettings.TorLinkTerminal"/>).
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _serverTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private static readonly TimeSpan ServerSearchWait = TimeSpan.FromSeconds(90);
    private static string TorLinkPageLine => L("Chaque téléchargement terminé rejoint ta bibliothèque.", "Each finished download joins your library.");
    /// <summary>The page shows the PC of Jellyfin's screen, not TorLink.</summary>
    private bool _serverScreen, _serverFilled, _serverReady, _serverLoading, _serverAgain, _serverSearching;
    private int _serverVersion, _serverSearchVersion;
    private RelayAgent? _serverAgent;
    /// <summary>The account whose PC the page shows: another one starts afresh.</summary>
    private JellyfinClient? _serverClient;
    /// <summary>Results sent from this page (search id / result id): « Envoyé » on their button.</summary>
    private readonly HashSet<string> _serverSent = [];
    /// <summary>Rows of the right column by key, their texts changed in place every 3 s: a button under the mouse stays where it is.</summary>
    private readonly Dictionary<string, ServerRow> _serverRows = [];
    private sealed record ServerRow(string Shape, Border View, TextBlock Name, TextBlock Line, ScaleTransform? Bar);
    private sealed record ServerRowData(string Key, string Shape, string Name, string Line, int? Progress, Func<ServerRow> Make);
    /// <summary>The screen runs searches and downloads with this PC's TorLink (the PC of Jellyfin), not through the relay.</summary>
    private bool _serverLocal;
    /// <summary>Why this PC's TorLink cannot download now, in words; null when it can.</summary>
    private string? _localProblem;
    /// <summary>Shown again: what TorLink had under way goes on without a window, as it would have in its terminal.</summary>
    private bool _localResume;
    /// <summary>The magnets of the last search on this PC, by search id / result id: never shown.</summary>
    private readonly Dictionary<string, string> _localMagnets = [];
    private int _localSearches, _serverQueued;

    private void InitializeServerDownloads() => _serverTimer.Tick += async (_, _) => await ServerTickAsync();

    /// <summary>Jellyfin runs on another PC than this one (an account, not the demo): TorLink is not offered here.</summary>
    private async Task<bool> JellyfinElsewhereAsync() => !_demo && _client is { } client && !await JellyfinOnThisPcAsync(client);

    /// <param name="local">On the PC of Jellyfin: this PC's TorLink, not the relay.</param>
    private async Task ShowServerDownloadsAsync(bool local)
    {
        if (!ReferenceEquals(_serverClient, _client) || local != _serverLocal) { ResetServerDownloads(); _serverClient = _client; _serverLocal = local; DescribeServerPlace(); }
        if (!_serverScreen)
        {
            _serverScreen = true;
            // Opened for another account, on this PC's Jellyfin: it would go on downloading here, out of sight.
            if (!local && _torlinkTerminal?.Running == true) await _torlinkTerminal.StopAsync();
        }
        // Looked at again: TorLink may have been closed elsewhere meanwhile.
        _localResume = local; _localProblem = null;
        if (local) _torlinkStartWatch.Stop();
        // TorLink's terminal and the list of downloaders stay out of the way, and out of the keyboard's reach.
        DownloadersPanel.Visibility = TorLinkTerminalHost.Visibility = TorLinkKeysHint.Visibility = Visibility.Collapsed;
        // On this PC, TorLink's folder, its settings and its terminal stay at hand.
        DownloadersButton.Visibility = TorLinkFolderButton.Visibility = TorLinkSettingsButton.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        DescribeTorLinkView();
        if (ServerDownloadsPanel.Visibility != Visibility.Visible) Motion.Reveal(ServerDownloadsPanel, 200, 6);
        if (local) RenderTorLinkImports();
        DescribeServerAgent(DateTimeOffset.UtcNow);
        _serverTimer.Stop(); _serverTimer.Start();
        if (!ServerDownloadsPanel.IsKeyboardFocusWithin) _ = Dispatcher.BeginInvoke(() => ServerQueryBox.Focus(), DispatcherPriority.Input);
        await LoadServerDownloadsAsync();
    }

    /// <summary>TorLink's own page again (its terminal, or the list of downloaders): an account of this PC's Jellyfin.</summary>
    private void HideServerDownloads()
    {
        if (!_serverScreen) return;
        _serverScreen = _serverLocal = false; _serverTimer.Stop(); EndUpgrade();
        ServerDownloadsPanel.Visibility = Visibility.Collapsed; TorLinkTerminalHost.Visibility = TorLinkSettingsButton.Visibility = Visibility.Visible;
        TorLinkSubtitle.Text = TorLinkPageLine;
        ResetServerDownloads();
    }

    private void ResetServerDownloads()
    {
        ++_serverVersion; ++_serverSearchVersion;
        _serverFilled = _serverReady = _serverSearching = false; _serverAgent = null; _serverClient = null;
        _serverSent.Clear(); _serverRows.Clear(); _localMagnets.Clear(); _localProblem = null; _serverQueued = 0;
        ServerQueue.Children.Clear(); ServerFollows.Children.Clear(); ServerPlaced.Children.Clear(); ServerResults.Children.Clear();
        ServerPlacedTitle.Visibility = ServerFollowsTitle.Visibility = ServerProblem.Visibility = Visibility.Collapsed;
        ServerQueueEmpty.Visibility = ServerResultsEmpty.Visibility = Visibility.Visible;
        ServerQueueSummary.Text = ServerSearchStatus.Text = "";
        ServerSearchButton.Content = L("Chercher", "Search"); ServerResults.Opacity = 1;
        CloseServerMagnet();
    }

    /// <summary>The words of the screen: the server PC, or this PC's TorLink.</summary>
    private void DescribeServerPlace()
    {
        var here = _serverLocal;
        ServerQueueTitle.Text = here ? L("Dans TorLink", "In TorLink") : L("Sur le PC serveur", "On the server PC");
        ServerQueueHint.Text = here ? L("Ce que tu télécharges apparaît ici avec sa progression et le temps restant, puis rejoint ta bibliothèque.", "What you download shows up here with its progress and the time left, then joins your library.")
            : L("Ce que tu envoies apparaît ici avec sa progression et le temps restant, puis rejoint ta bibliothèque.", "What you send shows up here with its progress and the time left, then joins your library.");
        ServerResultsHint.Text = here ? L("TorLink le cherche depuis ce PC. Mira regroupe les versions par titre et par épisode, et met la meilleure en avant : la qualité de la source d’abord, le 1080p, puis MULTI ou VF.", "TorLink searches for it from this PC. Mira groups the versions by title and by episode, and puts the best one first: the source’s quality first, 1080p, then MULTI or French audio.")
            : L("TorLink le cherche sur le PC serveur. Mira regroupe les versions par titre et par épisode, et met la meilleure en avant : la qualité de la source d’abord, le 1080p, puis MULTI ou VF.", "TorLink searches for it on the server PC. Mira groups the versions by title and by episode, and puts the best one first: the source’s quality first, 1080p, then MULTI or French audio.");
        ServerMagnetHint.Text = here ? QBittorrentInstalled() ? L("Le lien d’un site que tu connais : qBittorrent le télécharge sur ce PC, puis Mira le range dans ta bibliothèque.", "A link from a site you know: qBittorrent downloads it on this PC, then Mira sorts it into your library.")
                : L("Le lien d’un site que tu connais : TorLink le télécharge sur ce PC, puis Mira le range dans ta bibliothèque.", "A link from a site you know: TorLink downloads it on this PC, then Mira sorts it into your library.")
            : L("Le lien d’un site que tu connais : le PC serveur le télécharge, puis Mira le range dans ta bibliothèque.", "A link from a site you know: the server PC downloads it, then Mira sorts it into your library.");
        ServerMagnetSend.Content = here ? L("Télécharger", "Download") : L("Envoyer au PC serveur", "Send to the server PC");
        ServerPlacedTitle.Text = here ? L("Vers Jellyfin", "To Jellyfin") : L("Rangés dans ta bibliothèque", "Sorted into your library");
        DescribeQBittorrent();
    }

    private async Task ServerTickAsync()
    {
        if (!_serverScreen || TorLinkOverlay.Visibility != Visibility.Visible || !TorLinkOverlay.IsHitTestVisible) { _serverTimer.Stop(); return; }
        // Jellyfin is not asked for nothing: not while Mira is minimized, nor under the settings or a title.
        if (WindowState == WindowState.Minimized || SettingsOverlay.Visibility == Visibility.Visible || DetailOverlay.Visibility == Visibility.Visible) return;
        // Another account meanwhile: its Jellyfin may be on this PC, where TorLink's own page belongs.
        if (!ReferenceEquals(_serverClient, _client)) { await ShowTorLinkAsync(); return; }
        await LoadServerDownloadsAsync();
    }

    // ---- What the PC has under way --------------------------------------------------------------------

    private async Task LoadServerDownloadsAsync()
    {
        var client = _client;
        if (client is null && !_serverLocal) return;
        if (_serverLoading) { _serverAgain = true; return; }
        _serverLoading = true; _serverAgain = false;
        var mine = ++_serverVersion;
        try
        {
            if (_serverLocal) { await LoadLocalDownloadsAsync(mine); return; }
            var status = await client!.TorLinkStatusAsync(_connectionLifetime.Token);
            if (mine != _serverVersion || !ReferenceEquals(client, _client) || !_serverScreen) return;
            ServerProblem.Visibility = Visibility.Collapsed;
            RenderServerStatus(status, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (mine != _serverVersion || !ReferenceEquals(client, _client) || !_serverScreen) return;
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.Forbidden }) ShowServerProblem("users", L("Réservé aux administrateurs de Jellyfin", "For Jellyfin administrators only"), ex.Message);
            else if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) ShowServerProblem("refresh", L("L’extension Mira est à mettre à jour", "The Mira extension needs an update"), ex.Message);
            // A page already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            else if (_serverFilled) TorLinkSubtitle.Text = ServerError(ex);
            else ShowServerProblem("server", L("Le PC serveur ne répond pas", "The server PC isn’t answering"), ServerError(ex));
        }
        finally
        {
            _serverLoading = false;
            if (_serverAgain && _serverScreen) _ = LoadServerDownloadsAsync();
        }
    }

    /// <summary>On the PC of Jellyfin: TorLink's downloads, live from TorLink without a window, otherwise from the queue it last saved.</summary>
    private async Task LoadLocalDownloadsAsync(int mine)
    {
        List<RelayDownload> downloads;
        try { downloads = [.. await RemoteDownloadsAsync(_torlinkLifetime.Token), .. await QBittorrentDownloadsAsync(_torlinkLifetime.Token)]; }
        catch (OperationCanceledException) { return; }
        if (mine != _serverVersion || !_serverScreen || !_serverLocal) return;
        // The series followed are the relay's, on this PC too.
        List<RelayFollow>? follows = null;
        if (_client is { } client)
        {
            try { follows = await client.TorLinkFollowsAsync(_connectionLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { }
            if (mine != _serverVersion || !_serverScreen || !_serverLocal) return;
        }
        if (_localResume)
        {
            _localResume = false;
            // The page's TorLink running, or a queue left under way: it goes on without a window, as it would have in its terminal.
            if (_torlinkHeadless.Client is null && (_torlinkTerminal?.Running == true || downloads.Any(x => x.Status == "stopped"))) _ = ResumeHereAsync();
        }
        RenderServerStatus(new RelayStatus(null, [], [], downloads, [], follows), DateTimeOffset.UtcNow);
    }

    private async Task ResumeHereAsync()
    {
        await StartLocalTorLinkAsync();
        if (_serverScreen && _serverLocal) await LoadServerDownloadsAsync();
    }

    /// <summary>TorLink without a window on this PC, for this screen: null once it runs, otherwise why not, in this PC's words.</summary>
    private async Task<string?> StartLocalTorLinkAsync()
    {
        var installation = _torlinkInstallation = TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath);
        var problem = await StartHeadlessAsync(installation);
        if (problem is not null && DateTime.UtcNow - _remoteElsewhereAt < TimeSpan.FromSeconds(10))
            problem = L("TorLink est déjà ouvert dans une autre fenêtre. Quitte-le (q ou Ctrl+C), puis réessaie : deux TorLink ne peuvent pas gérer les mêmes téléchargements.", "TorLink is already open in another window. Quit it (q or Ctrl+C), then try again: two TorLinks can’t manage the same downloads.");
        _localProblem = problem;
        if (_serverScreen && _serverLocal) DescribeServerAgent(DateTimeOffset.UtcNow);
        return problem;
    }

    /// <summary>A magnet into this PC's TorLink, without a window: null once TorLink has it (new or already there), otherwise why not.</summary>
    private async Task<string?> DownloadHereAsync(string magnet)
    {
        if (await StartLocalTorLinkAsync() is { } refused) return refused;
        if (_torlinkHeadless.Client is not { } serve) return L("TorLink n’a pas répondu.", "TorLink didn’t answer.");
        var ct = _torlinkLifetime.Token;
        try
        {
            var (outcome, error) = await serve.AddAsync(magnet, ct);
            if (outcome is null) return string.IsNullOrEmpty(error) ? L("TorLink a refusé ce téléchargement.", "TorLink refused this download.") : error;
            _headlessIdleSince = null;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return L("TorLink n’a pas répondu.", "TorLink didn’t answer."); }
    }

    /// <summary>A pause, a resume or a cancel by this PC's TorLink: null once done, otherwise why not.</summary>
    private async Task<string?> ControlHereAsync(string id, string action)
    {
        // qBittorrent's (a private tracker's): "" once done.
        if (await ControlQBittorrentAsync(id, action) is { } handled) return handled.Length == 0 ? null : handled;
        if (await StartLocalTorLinkAsync() is { } refused) return refused;
        if (_torlinkHeadless.Client is not { } serve) return L("TorLink n’a pas répondu.", "TorLink didn’t answer.");
        var ct = _torlinkLifetime.Token;
        try { return await serve.ControlAsync(id, action, ct) ? null : L("TorLink n’a plus ce téléchargement.", "TorLink no longer has this download."); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return L("TorLink n’a pas répondu.", "TorLink didn’t answer."); }
    }

    private static string ServerError(Exception ex) => ex is RelayRefusedException ? ex.Message : Friendly(ex);

    private void ShowServerProblem(string icon, string title, string hint)
    {
        ServerProblemIcon.Kind = icon; ServerProblemTitle.Text = title; ServerProblemHint.Text = hint;
        TorLinkStatusText.Text = L("PC serveur · Indisponible", "Server PC · Unavailable"); TorLinkStatusDot.Fill = Palette.Get("Warning");
        TorLinkSubtitle.Text = TorLinkPageLine;
        if (ServerProblem.Visibility != Visibility.Visible) Motion.Reveal(ServerProblem, 200, 0);
    }

    private async void ServerRetry_Click(object sender, RoutedEventArgs e)
    {
        ServerProblemHint.Text = L("Lecture du PC serveur…", "Checking the server PC…");
        await LoadServerDownloadsAsync();
    }

    /// <summary>The header: where the PC stands, in Mira web's words, and its pill.</summary>
    private void DescribeServerAgent(DateTimeOffset now)
    {
        if (_serverLocal)
        {
            // Searches run on their own (« torlnk search »): only downloads wait for TorLink without a window.
            _serverReady = _torlinkInstallation is { Headless: true };
            TorLinkSubtitle.Text = _localProblem ?? TorLinkPageLine;
            var (local, color) = _localProblem is not null ? (L("Indisponible", "Unavailable"), "Warning") : _torlinkHeadless.Running ? (L("En cours", "Running"), "Success") : (L("Prêt", "Ready"), "Success");
            TorLinkStatusText.Text = "TorLink · " + local; TorLinkStatusDot.Fill = Palette.Get(color);
            ServerSearchButton.IsEnabled = _serverReady && !_serverSearching;
            return;
        }
        var (text, ready) = _serverFilled ? RemoteLines.Agent(_serverAgent, now) : (L("Lecture du PC serveur…", "Checking the server PC…"), false);
        _serverReady = ready;
        TorLinkSubtitle.Text = text;
        var (status, dot) = !_serverFilled ? (L("Lecture…", "Checking…"), "Dim") : _serverAgent is null ? (L("Jamais ouvert", "Never opened"), "Dim") : !_serverAgent.Online ? (L("Mira fermé", "Mira closed"), "Dim")
            : !string.IsNullOrEmpty(_serverAgent.Problem) ? (L("Occupé", "Busy"), "Warning") : (L("Prêt", "Ready"), "Success");
        TorLinkStatusText.Text = L("PC serveur · ", "Server PC · ") + status; TorLinkStatusDot.Fill = Palette.Get(dot);
        ServerSearchButton.IsEnabled = ready && !_serverSearching;
    }

    private void RenderServerStatus(RelayStatus status, DateTimeOffset now)
    {
        _serverFilled = true; _serverAgent = status.Agent;
        DescribeServerAgent(now);
        var queue = new List<ServerRowData>();
        // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
        foreach (var request in RemoteLines.Pending(status))
            queue.Add(new($"r:{request.Id}", $"r|{request.State}", Shown(request.Name).Title, RemoteLines.Request(request), null, () => ServerRequestRow(request)));
        foreach (var download in status.Downloads)
        {
            var pending = status.Controls.FirstOrDefault(x => x.Target == download.Id);
            var line = pending is not null && RemoteLines.ActionLine(pending.Action) is { } asked ? asked : RemoteLines.Download(download);
            queue.Add(new($"d:{download.Id}", $"d|{download.Status}|{pending is null}", Shown(download.Name).Title, line, download.Progress, () => ServerDownloadRow(download, pending is null)));
        }
        // On this PC, « Vers Jellyfin » is TorLink's page's own list (RenderLocalPlaced): its folder, a retry, another library.
        var placed = _serverLocal ? new List<ServerRowData>() : status.Placed.Select(x => new ServerRowData($"p:{x.Id}", $"p|{x.ItemId}|{x.Ok}", x.Title, Sentence(RemoteLines.Placed(x.Status)), null, () => ServerPlacedRow(x))).ToList();
        var follows = ServerFollowRows(status.Follows, now);
        UpdateServerRows(ServerQueue, queue);
        UpdateServerRows(ServerFollows, follows);
        ServerFollowsTitle.Visibility = follows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_serverLocal) UpdateServerRows(ServerPlaced, placed);
        foreach (var key in _serverRows.Keys.Except(queue.Concat(follows).Concat(placed).Select(x => x.Key)).ToList()) _serverRows.Remove(key);
        _serverQueued = queue.Count + follows.Count;
        var filed = _serverLocal ? ServerPlaced.Children.Count : placed.Count;
        ServerPlacedTitle.Visibility = filed > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerQueueEmpty.Visibility = queue.Count + follows.Count + filed == 0 ? Visibility.Visible : Visibility.Collapsed;
        var downloading = status.Downloads.Count(x => x.Status == "downloading");
        var waiting = queue.Count - downloading;
        ServerQueueSummary.Text = queue.Count == 0 ? (filed > 0 ? L("Rien en cours. Les derniers titres rangés sont ci-dessous.", "Nothing under way. The latest titles filed are below.") : "")
            : string.Join(" · ", new[] { downloading > 0 ? L($"{downloading} en cours", $"{downloading} downloading") : "", waiting > 0 ? L($"{waiting} en attente", $"{waiting} waiting") : "" }.Where(x => x.Length > 0));
    }

    /// <summary>On the PC of Jellyfin, the right column ends with « Vers Jellyfin » as TorLink's page shows it.</summary>
    private void RenderLocalPlaced(TorLinkPlacement torlink)
    {
        ServerPlaced.Children.Clear();
        foreach (var entry in torlink.Importer.Entries.Take(40)) ServerPlaced.Children.Add(ImportRow(entry));
        foreach (var earlier in torlink.Earlier) ServerPlaced.Children.Add(EarlierRow(earlier));
        ServerPlacedTitle.Visibility = ServerPlaced.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerQueueEmpty.Visibility = _serverQueued + ServerPlaced.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A release called by the title read from its name, the name itself under it (as Mira web).</summary>
    private static (string Title, string? Full) Shown(string name) => ReleaseRanking.Readable(name) is { Title.Length: > 0 } shown ? shown : (name, null);

    private void UpdateServerRows(StackPanel panel, List<ServerRowData> rows)
    {
        var next = new List<UIElement>();
        foreach (var data in rows)
        {
            if (_serverRows.TryGetValue(data.Key, out var kept) && kept.Shape == data.Shape)
            {
                if (kept.Name.Text != data.Name) kept.Name.Text = data.Name;
                if (kept.Line.Text != data.Line) kept.Line.Text = data.Line;
                if (kept.Bar is not null && data.Progress is { } progress) kept.Bar.ScaleX = Math.Clamp(progress, 0, 100) / 100.0;
                AutomationProperties.SetName(kept.View, $"{data.Name}, {data.Line}");
                next.Add(kept.View);
                continue;
            }
            var made = data.Make() with { Shape = data.Shape };
            _serverRows[data.Key] = made; next.Add(made.View);
        }
        if (next.Count == panel.Children.Count && next.Select((x, i) => ReferenceEquals(x, panel.Children[i])).All(x => x)) return;
        panel.Children.Clear();
        foreach (var view in next) panel.Children.Add(view);
    }

    private ServerRow ServerRowView(string icon, string color, string name, string line, int? progress, bool failed, StackPanel? actions, string? full = null)
    {
        var nameText = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = full ?? name };
        var lineText = new TextBlock { Text = line, FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get(failed ? "WarningText" : "Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(nameText); text.Children.Add(lineText);
        if (full is not null)
            text.Children.Add(new TextBlock { Text = full, FontSize = 11, LineHeight = 15, Foreground = Palette.Get("Faint"), TextWrapping = TextWrapping.Wrap, MaxHeight = 31, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = full });
        ScaleTransform? bar = null;
        if (progress is { } value)
        {
            bar = new ScaleTransform(Math.Clamp(value, 0, 100) / 100.0, 1);
            text.Children.Add(new Border
            {
                Height = 3, Margin = new Thickness(0, 9, 0, 2), Background = Palette.Get("Line"), CornerRadius = new CornerRadius(1.5), ClipToBounds = true,
                Child = new Border { Background = Palette.Get("Accent"), CornerRadius = new CornerRadius(1.5), RenderTransformOrigin = new Point(0, 0.5), RenderTransform = bar }
            });
        }
        if (actions is { Children.Count: > 0 }) text.Children.Add(actions);
        return new("", Row(icon, color, text, null, $"{name}, {line}"), nameText, lineText, bar);
    }

    private ServerRow ServerRequestRow(RelayRequest request)
    {
        var (name, full) = Shown(request.Name);
        var actions = new StackPanel();
        if (request.State != "claimed")
        {
            var label = request.State == "waiting" ? L("Annuler", "Cancel") : L("Retirer", "Remove");
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            buttons.Children.Add(Named(RowButton(label, "close", () => CancelServerRequestAsync(request)), L($"{label} « {name} »", $"{label} “{name}”")));
            actions.Children.Add(buttons);
        }
        var failed = request.State == "failed";
        return ServerRowView(failed ? "warning" : "clock", failed ? "Warning" : "Faint", name, RemoteLines.Request(request), null, failed, actions, full);
    }

    private ServerRow ServerDownloadRow(RelayDownload download, bool offer)
    {
        var (name, full) = Shown(download.Name);
        var actions = new StackPanel();
        if (offer && RemoteLines.Controllable(download))
        {
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            if (download.Status != "failed")
                buttons.Children.Add(RemoteLines.Paused(download)
                    ? Named(RowButton(L("Reprendre", "Resume"), "play", () => ControlServerAsync(download, "resume")), L($"Reprendre « {name} »", $"Resume “{name}”"))
                    : Named(RowButton("Pause", "pause", () => ControlServerAsync(download, "pause")), L($"Mettre en pause « {name} »", $"Pause “{name}”")));
            buttons.Children.Add(Named(RowButton(L("Annuler", "Cancel"), "close", () => { ConfirmServerCancel(actions, download); return Task.CompletedTask; }), L($"Annuler « {name} »", $"Cancel “{name}”")));
            actions.Children.Add(buttons);
        }
        var (icon, color) = download.Status switch { "failed" => ("warning", "Warning"), "completed" or "seeding" => ("check", "Success"), "downloading" => ("download", "TextBody"), _ => ("clock", "Faint") };
        return ServerRowView(icon, color, name, RemoteLines.Download(download), download.Progress, download.Status == "failed", actions, full);
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more, in the row.</summary>
    private void ConfirmServerCancel(StackPanel actions, RelayDownload download)
    {
        var kept = actions.Children.Cast<UIElement>().ToList();
        actions.Children.Clear();
        actions.Children.Add(new TextBlock { Text = _serverLocal ? L("TorLink l’arrête et supprime ce qu’il en a déjà reçu.", "TorLink stops it and deletes what it has already received.") : L("TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", "TorLink stops it and deletes what it has already received on the server PC."), FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get("WarningText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        var confirm = Named(RowButton(L("Annuler le téléchargement", "Cancel the download"), "close", () => ControlServerAsync(download, "delete")), L($"Annuler le téléchargement de « {download.Name} »", $"Cancel the download of “{download.Name}”"));
        confirm.Foreground = Palette.Get("Danger");
        buttons.Children.Add(confirm);
        buttons.Children.Add(Named(RowButton(L("Garder", "Keep"), "check", () =>
        {
            actions.Children.Clear();
            foreach (var element in kept) actions.Children.Add(element);
            return Task.CompletedTask;
        }), L($"Garder « {download.Name} »", $"Keep “{download.Name}”")));
        actions.Children.Add(buttons);
        _ = Dispatcher.BeginInvoke(() => confirm.Focus(), DispatcherPriority.Input);
    }

    private ServerRow ServerPlacedRow(RelayPlaced placed)
    {
        var actions = new StackPanel();
        if (placed.ItemId is { } id && !_demo)
        {
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            buttons.Children.Add(Named(RowButton(L("Voir", "View"), "play", () => OpenTorLinkItemAsync(id)), L($"Voir « {placed.Title} »", $"View “{placed.Title}”")));
            actions.Children.Add(buttons);
        }
        return ServerRowView(placed.Ok ? "check" : "warning", placed.Ok ? "Success" : "Warning", placed.Title, Sentence(RemoteLines.Placed(placed.Status)), null, false, actions);
    }

    private static Button Named(Button button, string name) { AutomationProperties.SetName(button, name); return button; }

    private async Task CancelServerRequestAsync(RelayRequest request)
    {
        if (_client is not { } client) return;
        try
        {
            SetNotice(await client.TorLinkCancelAsync(request.Id, _connectionLifetime.Token) switch
            {
                true => request.State == "waiting" ? L("Demande annulée.", "Request canceled.") : L("Retiré de la liste.", "Removed from the list."),
                false => L("Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.", "The server PC has already taken it: cancel it in its list as soon as it shows up there."),
                null => L("Cette demande n’est plus là.", "This request is no longer there.")
            });
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        await LoadServerDownloadsAsync();
    }

    private async Task ControlServerAsync(RelayDownload download, string action)
    {
        if (_serverLocal)
        {
            SetNotice(await ControlHereAsync(download.Id, action) ?? action switch
            {
                "pause" => L("Téléchargement mis en pause.", "Download paused."),
                "resume" => L("Téléchargement repris.", "Download resumed."),
                _ => L("Téléchargement annulé.", "Download canceled.")
            });
            await LoadServerDownloadsAsync();
            return;
        }
        if (_client is not { } client) return;
        try { await client.TorLinkControlAsync(download.Id, action, _connectionLifetime.Token); SetNotice(RemoteLines.ActionLine(action) ?? ""); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        await LoadServerDownloadsAsync();
    }

    // ---- Searching ------------------------------------------------------------------------------------

    private void ServerQuery_Changed(object sender, TextChangedEventArgs e)
    {
        ServerQueryPlaceholder.Visibility = ServerQueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerQueryClear.Visibility = ServerQueryBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void ServerQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchServerAsync(); }
        // Escape first clears the search, then (a second press) goes back as usual.
        else if (e.Key == Key.Escape && ServerQueryBox.Text.Length > 0) { e.Handled = true; ServerQueryBox.Clear(); }
    }
    private void ServerQueryClear_Click(object sender, RoutedEventArgs e) { ServerQueryBox.Clear(); ServerQueryBox.Focus(); }
    private async void ServerSearch_Click(object sender, RoutedEventArgs e) => await SearchServerAsync();

    /// <summary>
    /// A search TorLink runs on the PC of Jellyfin, in films, shows and anime at once, followed every second until it
    /// answers (90 s at most).
    /// </summary>
    private async Task SearchServerAsync()
    {
        var client = _client;
        if (client is null && !_serverLocal) return;
        if (RemoteDownloads.CleanQuery(ServerQueryBox.Text) is not { } query) { ServerQueryBox.Focus(); return; }
        if (!_serverReady) { ServerSearchStatus.Text = _serverLocal ? L("TorLink n’est pas prêt sur ce PC.", "TorLink isn’t ready on this PC.") : RemoteLines.Agent(_serverAgent, DateTimeOffset.UtcNow).Text; return; }
        var mine = ++_serverSearchVersion; var ct = _connectionLifetime.Token;
        _serverSearching = true; ServerSearchButton.IsEnabled = false; ServerSearchButton.Content = L("Recherche…", "Searching…");
        ServerSearchStatus.Text = _serverLocal ? L($"Recherche de « {query} » dans les films, les séries et les animes…", $"Searching for “{query}” in movies, shows and anime…")
            : L($"Recherche de « {query} » dans les films, les séries et les animes sur le PC serveur…", $"Searching for “{query}” in movies, shows and anime on the server PC…");
        ServerResults.Opacity = 0.45;
        void End(string text) { ServerSearchStatus.Text = text; ServerResults.Children.Clear(); ServerResultsEmpty.Visibility = Visibility.Collapsed; }
        try
        {
            if (_serverLocal)
            {
                if (_torlinkInstallation is not { Headless: true } installation) { End(L("TorLink n’est pas prêt sur ce PC.", "TorLink isn’t ready on this PC.")); return; }
                // One search of TorLink in each category, side by side, mixed as the relay does.
                var parts = await Task.WhenAll(RemoteDownloads.Categories.Select(async kind =>
                {
                    var (hits, error) = await _torlinkHeadless.SearchAsync(installation, query, kind, ct);
                    return (kind, (IReadOnlyList<AgentHit>)hits, error);
                }));
                if (mine != _serverSearchVersion) return;
                var (found, failed) = RemoteDownloads.Combine(parts, x => x.Id);
                if (failed is not null) { End(failed); return; }
                var local = $"here-{++_localSearches}";
                // The magnets stay here, as the relay keeps them: the buttons only name a result.
                _localMagnets.Clear();
                foreach (var (_, hit) in found) _localMagnets[local + "/" + hit.Id] = hit.Magnet;
                ShowServerHits(local, query, [.. found.Select(x => new RelayHit(x.Hit.Id, x.Hit.Name, x.Hit.Size, x.Hit.Seeders, x.Hit.Leechers, x.Hit.Source, x.Hit.Added, x.Category))]);
                return;
            }
            var id = await client!.TorLinkSearchAsync(query, RemoteDownloads.All, ct);
            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);
                if (mine != _serverSearchVersion) return;
                if (DateTime.UtcNow - started > ServerSearchWait) { End(L("Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche.", "The server PC didn’t answer in time. Check that Mira is open there, then search again.")); return; }
                RelaySearch? state;
                // A hiccup on the way: asked again a second later.
                try { state = await client.TorLinkSearchStateAsync(id, ct); }
                catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested && ex is not RelayRefusedException) { continue; }
                if (mine != _serverSearchVersion) return;
                // Forgotten (Jellyfin restarted meanwhile): it will not come back.
                if (state is null) { End(L("Cette recherche a été perdue : relance-la.", "This search was lost: run it again.")); return; }
                if (state.State == "failed") { End(string.IsNullOrEmpty(state.Error) ? L("La recherche n’a pas abouti.", "The search didn’t go through.") : state.Error); return; }
                if (state.State == "done") { ShowServerHits(id, query, state.Results ?? []); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { if (mine == _serverSearchVersion) End(ServerError(ex)); }
        finally
        {
            if (mine == _serverSearchVersion)
            {
                _serverSearching = false; ServerSearchButton.Content = L("Chercher", "Search"); ServerSearchButton.IsEnabled = _serverReady; ServerResults.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// The results by title and episode (or season, or film), the best release of each first: its quality and source,
    /// its size and sources, its full name; the other releases folded under it.
    /// </summary>
    private void ShowServerHits(string searchId, string query, IReadOnlyList<RelayHit> hits)
    {
        ServerResultsEmpty.Visibility = Visibility.Collapsed; ServerResults.Children.Clear(); SmoothScroll.Jump(ServerResultsScroll);
        // « Chercher une meilleure version »: only this title, in a better picture than its file.
        if (_upgrade is { } upgrade)
        {
            var better = TitleUpgrade.Candidates(upgrade, hits);
            ServerSearchStatus.Text = better.Count == 0
                ? L($"Aucune version meilleure que l’actuelle ({TitleUpgrade.Line(TitleUpgrade.Current(upgrade))}) parmi les {hits.Count} résultats pour « {query} ». Réessaie plus tard, ou avec un autre nom.",
                    $"No version better than the current one ({TitleUpgrade.Line(TitleUpgrade.Current(upgrade))}) among the {hits.Count} results for “{query}”. Try again later, or with another name.")
                : L($"{Count(better.Count, "version meilleure", "versions meilleures", "better version", "better versions")} que l’actuelle, la meilleure en premier.", $"{Count(better.Count, "version meilleure", "versions meilleures", "better version", "better versions")} than the current one, the best first.");
            foreach (var group in ReleaseRanking.Group(better)) ServerResults.Children.Add(ServerGroupView(searchId, group));
            return;
        }
        if (hits.Count == 0) { ServerSearchStatus.Text = L($"Aucun résultat pour « {query} » dans les films, les séries et les animes. Essaie un autre titre.", $"No results for “{query}” in movies, shows and anime. Try another title."); return; }
        var groups = ReleaseRanking.Group(hits);
        var (versions, titles) = (Count(hits.Count, "version", "versions", "version", "versions"), Count(groups.Count, "titre", "titres", "title", "titles"));
        ServerSearchStatus.Text = L($"{versions} pour « {query} », regroupées en {titles}. La meilleure de chacun en premier.", $"{versions} for “{query}”, grouped into {titles}. The best of each comes first.");
        var views = groups.Select(group => (Group: group, View: ServerGroupView(searchId, group))).ToList();
        foreach (var (_, view) in views) ServerResults.Children.Add(view);
        _ = MarkOwnedAsync(views);
    }

    /// <summary>
    /// « Déjà dans ta bibliothèque » under a title Jellyfin has (OwnedTitles), so that it is not downloaded twice: Jellyfin
    /// asked once per title found, the episodes of a series once, after the results show.
    /// </summary>
    private async Task MarkOwnedAsync(List<(ReleaseGroup Group, Border View)> views)
    {
        if (_client is not { } client || _demo) return;
        var ct = _connectionLifetime.Token;
        await Task.WhenAll(OwnedTitles.Lookups(views.Select(x => x.Group)).Select(async lookup =>
        {
            var mine = views.Where(x => OwnedTitles.LookupKey(x.Group) == lookup.Key).ToList();
            List<MediaItem> items, episodes = [];
            try
            {
                items = (await client.OwnedAsync(lookup.Title, ct)).Items;
                if (!lookup.Film && OwnedTitles.Line(mine[0].Group, items)?.ItemId is { } series && mine.Any(x => !x.Group.Release.Absolute))
                    episodes = (await client.OwnedEpisodesAsync(series, ct)).Items;
            }
            catch (Exception ex) when (IsExpected(ex)) { return; } // Nothing said rather than a guess.
            foreach (var (group, view) in mine)
            {
                if (!ServerResults.Children.Contains(view) || view.Child is not StackPanel panel || OwnedTitles.Line(group, items, episodes) is not { } line) continue;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Icon { Kind = line.Owned ? "check" : "info", Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0) });
                content.Children.Add(new TextBlock { Text = line.Text, FontSize = 12.5, FontWeight = FontWeights.SemiBold });
                var button = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 5, 9, 5), Margin = new Thickness(-8, -6, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, Content = content, Foreground = Palette.Get(line.Owned ? "Warning" : "TextSecondary") };
                AutomationProperties.SetName(button, L($"{line.Text} : ouvrir « {group.Title} »", $"{line.Text}: open “{group.Title}”"));
                var id = line.ItemId;
                button.Click += async (_, _) => await OpenTorLinkItemAsync(id);
                panel.Children.Insert(1, button);
            }
        }));
    }

    private Border ServerGroupView(string searchId, ReleaseGroup group)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (group.Label.Length > 0)
        {
            var episode = new Border { CornerRadius = Palette.Small, Background = Palette.Get("Raised"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = group.Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") } };
            DockPanel.SetDock(episode, Dock.Right); head.Children.Add(episode);
        }
        if (RemoteLines.KindLabel(group.Category) is { Length: > 0 } kind)
        {
            var tag = new TextBlock { Text = kind, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tag, Dock.Right); head.Children.Add(tag);
        }
        head.Children.Add(new TextBlock { Text = group.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = group.Title });
        panel.Children.Add(head);
        panel.Children.Add(ServerReleaseView(searchId, group.Best, best: true));
        if (group.Others.Count > 0)
        {
            var others = new StackPanel { Visibility = Visibility.Collapsed };
            foreach (var entry in group.Others) others.Children.Add(ServerReleaseView(searchId, entry, best: false));
            var more = Count(group.Others.Count, "autre version", "autres versions", "other version", "other versions");
            var text = new TextBlock { Text = more, FontSize = 12, FontWeight = FontWeights.SemiBold };
            var chevron = new Icon { Kind = "down", Width = 14, Height = 14, Foreground = Palette.Get("TextBody"), Margin = new Thickness(6, 0, 0, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal }; content.Children.Add(text); content.Children.Add(chevron);
            var toggle = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 6, 9, 6), Margin = new Thickness(-8, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Content = content, Foreground = Palette.Get("TextBody") };
            AutomationProperties.SetName(toggle, L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            toggle.Click += (_, _) =>
            {
                var open = others.Visibility != Visibility.Visible;
                others.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                text.Text = open ? L("Masquer les autres versions", "Hide the other versions") : more; chevron.Kind = open ? "up" : "down";
                AutomationProperties.SetName(toggle, open ? L("Masquer les autres versions", "Hide the other versions") : L($"{more} de {group.Title}", $"{more} of {group.Title}"));
            };
            panel.Children.Add(toggle); panel.Children.Add(others);
        }
        var card = new Border { Background = Palette.Get("Surface"), CornerRadius = Palette.Card, Padding = new Thickness(18, 16, 18, 14), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        AutomationProperties.SetName(card, string.Join(" · ", new[] { group.Title, group.Label, RemoteLines.KindLabel(group.Category) }.Where(x => x.Length > 0)));
        return card;
    }

    private FrameworkElement ServerReleaseView(string searchId, RankedRelease entry, bool best)
    {
        var hit = entry.Hit;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var quality = new WrapPanel();
        quality.Children.Add(new TextBlock { Text = ReleaseRanking.QualityLine(entry.Release) is { Length: > 0 } line ? line : L("Qualité non précisée", "Quality not given"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (best) quality.Children.Add(new Border { CornerRadius = Palette.Small, Background = Palette.Get("Accent"), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = L("Meilleur choix", "Best pick"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("OnAccent") } });
        text.Children.Add(quality);
        text.Children.Add(new TextBlock { Text = RemoteLines.Hit(hit), FontSize = 12, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 4, 0, 0) });
        text.Children.Add(new TextBlock { Text = hit.Name, FontSize = 11.5, LineHeight = 16, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 33, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = hit.Name });
        if (ReleaseRanking.Warning(entry) is { Length: > 0 } caution)
            text.Children.Add(new TextBlock { Text = caution, FontSize = 12, Foreground = Palette.Get("WarningText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);
        var key = searchId + "/" + hit.Id;
        var button = new Button { Style = (Style)FindResource(best ? "Primary" : "Quiet"), Padding = new Thickness(16, 9, 16, 9), VerticalAlignment = VerticalAlignment.Center };
        ShowSendState(button, _serverSent.Contains(key), best);
        AutomationProperties.SetName(button, _upgrade is null ? L($"Télécharger {hit.Name}", $"Download {hit.Name}") : L($"Remplacer par {hit.Name}", $"Replace with {hit.Name}"));
        button.Click += async (_, _) => await SendServerHitAsync(searchId, hit, button, best);
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        if (best) return grid;
        // The other versions, each under a hairline.
        return new Border { BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private void ShowSendState(Button button, bool sent, bool best)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var replace = _upgrade is not null;
        content.Children.Add(new Icon { Kind = sent ? "check" : replace ? "sparkle" : "download", Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0), Foreground = Palette.Get(best && !sent ? "OnAccent" : "TextBody") });
        content.Children.Add(new TextBlock { Text = sent ? replace ? L("Demandé", "Requested") : L("Envoyé", "Sent") : replace ? L("Remplacer", "Replace") : L("Télécharger", "Download"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        button.Content = content; button.IsEnabled = !sent;
    }

    private async Task SendServerHitAsync(string searchId, RelayHit hit, Button button, bool best)
    {
        var client = _client;
        if (client is null && !_serverLocal) return;
        // One better version at a time: the second would only replace the first.
        if (_upgrade is not null && _upgradeSent) { SetNotice(L("Une meilleure version de ce titre est déjà demandée : elle s’affiche à droite.", "A better version of this title is already requested: it shows on the right.")); return; }
        var replace = _upgrade?.Id;
        button.IsEnabled = false;
        try
        {
            if (!_serverLocal) await client!.TorLinkRequestAsync(searchId, hit, _connectionLifetime.Token, replace);
            else if (!_localMagnets.TryGetValue(searchId + "/" + hit.Id, out var magnet)) { button.IsEnabled = true; SetNotice(L("Ce résultat n’est plus là : relance la recherche.", "This result is gone: search again.")); return; }
            else if (replace is not null && (RemoteDownloads.InfoHash(magnet) is not { } hash ? L("Ce n’est pas un lien magnet.", "That’s not a magnet link.") : await ExpectReplacementAsync(hash, replace, _torlinkLifetime.Token)) is { } refusal)
            { button.IsEnabled = true; SetNotice(refusal); return; }
            else if (await DownloadHereAsync(magnet) is { } refused) { button.IsEnabled = true; SetNotice(refused); return; }
            _serverSent.Add(searchId + "/" + hit.Id); ShowSendState(button, sent: true, best);
            if (replace is not null) _upgradeSent = true;
            await SentToServerAsync(replace is not null);
        }
        catch (Exception ex) when (IsExpected(ex)) { button.IsEnabled = true; SetNotice(ServerError(ex)); }
    }

    /// <summary>Its row, with its progress, is at the top of the right column.</summary>
    private async Task SentToServerAsync(bool replacing = false, bool qbittorrent = false)
    {
        SetNotice(replacing ? L("Demandé : la nouvelle version prendra la place de l’ancienne une fois téléchargée et vérifiée.", "Requested: the new version will take the old one’s place once downloaded and checked.")
            : qbittorrent ? L("qBittorrent le télécharge : sa progression s’affiche à droite.", "qBittorrent is downloading it: its progress shows on the right.")
            : _serverLocal ? L("TorLink le télécharge : sa progression s’affiche à droite.", "TorLink is downloading it: its progress shows on the right.")
            : _serverReady ? L("Envoyé au PC serveur : sa progression s’affiche à droite.", "Sent to the server PC: its progress shows on the right.") : L("Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.", "Request kept: the server PC will do it as soon as Mira is open there."));
        SmoothScroll.Jump(ServerQueueScroll);
        await LoadServerDownloadsAsync();
    }

    // ---- A magnet link --------------------------------------------------------------------------------

    private void ServerMagnet_Click(object sender, RoutedEventArgs e)
    {
        ServerMagnetRow.Visibility = Visibility.Visible; ServerMagnetButton.Visibility = ServerMagnetError.Visibility = Visibility.Collapsed;
        _ = Dispatcher.BeginInvoke(() => ServerMagnetBox.Focus(), DispatcherPriority.Input);
    }
    private void ServerMagnetClose_Click(object sender, RoutedEventArgs e) { CloseServerMagnet(); ServerMagnetButton.Focus(); }
    private void CloseServerMagnet()
    {
        ServerMagnetRow.Visibility = ServerMagnetError.Visibility = Visibility.Collapsed; ServerMagnetButton.Visibility = Visibility.Visible;
        ServerMagnetBox.Clear(); ServerMagnetSend.IsEnabled = true;
    }
    private void ServerMagnet_Changed(object sender, TextChangedEventArgs e)
    {
        ServerMagnetPlaceholder.Visibility = ServerMagnetBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerMagnetError.Visibility = Visibility.Collapsed;
    }
    private async void ServerMagnet_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SendServerMagnetAsync(); }
        else if (e.Key == Key.Escape) { e.Handled = true; CloseServerMagnet(); ServerMagnetButton.Focus(); }
    }
    private async void ServerMagnetSend_Click(object sender, RoutedEventArgs e) => await SendServerMagnetAsync();

    private async Task SendServerMagnetAsync()
    {
        var client = _client;
        if ((client is null && !_serverLocal) || !ServerMagnetSend.IsEnabled) return;
        var magnet = ServerMagnetBox.Text.Trim();
        if (RemoteDownloads.InfoHash(magnet) is null) { ShowMagnetError(L("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).", "That’s not a magnet link (“magnet:?xt=urn:btih:…”).")); ServerMagnetBox.Focus(); return; }
        ServerMagnetSend.IsEnabled = false;
        try
        {
            // On this PC, qBittorrent takes it when it is installed: a pasted link is often a private tracker's.
            var qbittorrent = _serverLocal && QBittorrentInstalled();
            if (!_serverLocal) await client!.TorLinkRequestAsync(magnet, _connectionLifetime.Token);
            else if (await (qbittorrent ? AddToQBittorrentAsync(magnet, null) : DownloadHereAsync(magnet)) is { } refused) { ShowMagnetError(refused); return; }
            CloseServerMagnet(); ServerMagnetButton.Focus();
            await SentToServerAsync(qbittorrent: qbittorrent);
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowMagnetError(ServerError(ex)); }
        finally { ServerMagnetSend.IsEnabled = true; }
    }
    private void ShowMagnetError(string text) { ServerMagnetError.Text = text; ServerMagnetError.Visibility = Visibility.Visible; }
}
