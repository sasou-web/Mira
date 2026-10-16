using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The Downloads page away from the PC of Jellyfin. TorLink runs on that PC only, where its downloads land next to the
/// library; this page sends it searches and downloads through the relay of the extension (<c>Mira/torlink</c>), as Mira
/// web's « Télécharger sur le PC serveur » does: the best release of each title or episode first
/// (<see cref="ReleaseRanking"/>), what the PC has under way with its progress and time left (<see cref="RemoteLines"/>),
/// read every 3 s while the page shows, and only then.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _serverTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private static readonly TimeSpan ServerSearchWait = TimeSpan.FromSeconds(90);
    private const string TorLinkPageLine = "Chaque téléchargement terminé rejoint ta bibliothèque.";
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

    private void InitializeServerDownloads() => _serverTimer.Tick += async (_, _) => await ServerTickAsync();

    /// <summary>Jellyfin runs on another PC than this one (an account, not the demo): TorLink is not offered here.</summary>
    private async Task<bool> JellyfinElsewhereAsync() => !_demo && _client is { } client && !await JellyfinOnThisPcAsync(client);

    private async Task ShowServerDownloadsAsync()
    {
        if (!ReferenceEquals(_serverClient, _client)) { ResetServerDownloads(); _serverClient = _client; }
        if (!_serverScreen)
        {
            _serverScreen = true;
            // Opened for another account, on this PC's Jellyfin: it would go on downloading here, out of sight.
            if (_torlinkTerminal?.Running == true) await _torlinkTerminal.StopAsync();
        }
        DownloadersPanel.Visibility = TorLinkTerminalHost.Visibility = Visibility.Collapsed;
        DownloadersButton.Visibility = TorLinkFolderButton.Visibility = TorLinkSettingsButton.Visibility = TorLinkKeysHint.Visibility = Visibility.Collapsed;
        if (ServerDownloadsPanel.Visibility != Visibility.Visible) Motion.Reveal(ServerDownloadsPanel, 200, 6);
        DescribeServerAgent(DateTimeOffset.UtcNow);
        _serverTimer.Stop(); _serverTimer.Start();
        if (!ServerDownloadsPanel.IsKeyboardFocusWithin) _ = Dispatcher.BeginInvoke(() => ServerQueryBox.Focus(), DispatcherPriority.Input);
        await LoadServerDownloadsAsync();
    }

    /// <summary>TorLink's own page again: an account of this PC's Jellyfin.</summary>
    private void HideServerDownloads()
    {
        if (!_serverScreen) return;
        _serverScreen = false; _serverTimer.Stop();
        ServerDownloadsPanel.Visibility = Visibility.Collapsed; TorLinkTerminalHost.Visibility = TorLinkSettingsButton.Visibility = Visibility.Visible;
        TorLinkSubtitle.Text = TorLinkPageLine;
        ResetServerDownloads();
    }

    private void ResetServerDownloads()
    {
        ++_serverVersion; ++_serverSearchVersion;
        _serverFilled = _serverReady = _serverSearching = false; _serverAgent = null; _serverClient = null;
        _serverSent.Clear(); _serverRows.Clear();
        ServerQueue.Children.Clear(); ServerPlaced.Children.Clear(); ServerResults.Children.Clear();
        ServerPlacedTitle.Visibility = ServerProblem.Visibility = Visibility.Collapsed;
        ServerQueueEmpty.Visibility = ServerResultsEmpty.Visibility = Visibility.Visible;
        ServerQueueSummary.Text = ServerSearchStatus.Text = "";
        ServerSearchButton.Content = "Chercher"; ServerResults.Opacity = 1;
        CloseServerMagnet();
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
        if (_client is not { } client) return;
        if (_serverLoading) { _serverAgain = true; return; }
        _serverLoading = true; _serverAgain = false;
        var mine = ++_serverVersion;
        try
        {
            var status = await client.TorLinkStatusAsync(_connectionLifetime.Token);
            if (mine != _serverVersion || !ReferenceEquals(client, _client) || !_serverScreen) return;
            ServerProblem.Visibility = Visibility.Collapsed;
            RenderServerStatus(status, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (mine != _serverVersion || !ReferenceEquals(client, _client) || !_serverScreen) return;
            if (ex is RelayRefusedException { StatusCode: HttpStatusCode.Forbidden }) ShowServerProblem("users", "Réservé aux administrateurs de Jellyfin", ex.Message);
            else if (ex is RelayRefusedException { StatusCode: HttpStatusCode.NotFound }) ShowServerProblem("refresh", "L’extension Mira est à mettre à jour", ex.Message);
            // A page already filled keeps what it shows (a hiccup on Tailscale); an empty one says why.
            else if (_serverFilled) TorLinkSubtitle.Text = ServerError(ex);
            else ShowServerProblem("server", "Le PC serveur ne répond pas", ServerError(ex));
        }
        finally
        {
            _serverLoading = false;
            if (_serverAgain && _serverScreen) _ = LoadServerDownloadsAsync();
        }
    }

    private static string ServerError(Exception ex) => ex is RelayRefusedException ? ex.Message : Friendly(ex);

    private void ShowServerProblem(string icon, string title, string hint)
    {
        ServerProblemIcon.Kind = icon; ServerProblemTitle.Text = title; ServerProblemHint.Text = hint;
        TorLinkStatusText.Text = "PC serveur · Indisponible"; TorLinkStatusDot.Fill = Palette.Get("Warning");
        TorLinkSubtitle.Text = TorLinkPageLine;
        if (ServerProblem.Visibility != Visibility.Visible) Motion.Reveal(ServerProblem, 200, 0);
    }

    private async void ServerRetry_Click(object sender, RoutedEventArgs e)
    {
        ServerProblemHint.Text = "Lecture du PC serveur…";
        await LoadServerDownloadsAsync();
    }

    /// <summary>The header: where the PC stands, in Mira web's words, and its pill.</summary>
    private void DescribeServerAgent(DateTimeOffset now)
    {
        var (text, ready) = _serverFilled ? RemoteLines.Agent(_serverAgent, now) : ("Lecture du PC serveur…", false);
        _serverReady = ready;
        TorLinkSubtitle.Text = text;
        var (status, dot) = !_serverFilled ? ("Lecture…", "Dim") : _serverAgent is null ? ("Jamais ouvert", "Dim") : !_serverAgent.Online ? ("Mira fermé", "Dim")
            : !string.IsNullOrEmpty(_serverAgent.Problem) ? ("Occupé", "Warning") : ("Prêt", "Success");
        TorLinkStatusText.Text = "PC serveur · " + status; TorLinkStatusDot.Fill = Palette.Get(dot);
        ServerSearchButton.IsEnabled = ready && !_serverSearching;
    }

    private void RenderServerStatus(RelayStatus status, DateTimeOffset now)
    {
        _serverFilled = true; _serverAgent = status.Agent;
        DescribeServerAgent(now);
        var queue = new List<ServerRowData>();
        // A request TorLink has now shows as its download; one sent and not listed yet, as sent.
        foreach (var request in RemoteLines.Pending(status))
            queue.Add(new($"r:{request.Id}", $"r|{request.State}", request.Name, RemoteLines.Request(request), null, () => ServerRequestRow(request)));
        foreach (var download in status.Downloads)
        {
            var pending = status.Controls.FirstOrDefault(x => x.Target == download.Id);
            var line = pending is not null && RemoteLines.ActionLine(pending.Action) is { } asked ? asked : RemoteLines.Download(download);
            queue.Add(new($"d:{download.Id}", $"d|{download.Status}|{pending is null}", download.Name, line, download.Progress, () => ServerDownloadRow(download, pending is null)));
        }
        var placed = status.Placed.Select(x => new ServerRowData($"p:{x.Id}", $"p|{x.ItemId}|{x.Ok}", x.Title, Sentence(x.Status), null, () => ServerPlacedRow(x))).ToList();
        UpdateServerRows(ServerQueue, queue); UpdateServerRows(ServerPlaced, placed);
        foreach (var key in _serverRows.Keys.Except(queue.Concat(placed).Select(x => x.Key)).ToList()) _serverRows.Remove(key);
        ServerPlacedTitle.Visibility = placed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ServerQueueEmpty.Visibility = queue.Count + placed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var downloading = status.Downloads.Count(x => x.Status == "downloading");
        var waiting = queue.Count - downloading;
        ServerQueueSummary.Text = queue.Count == 0 ? (placed.Count > 0 ? "Rien en cours. Les derniers titres rangés sont ci-dessous." : "")
            : string.Join(" · ", new[] { downloading > 0 ? $"{downloading} en cours" : "", waiting > 0 ? $"{waiting} en attente" : "" }.Where(x => x.Length > 0));
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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

    private ServerRow ServerRowView(string icon, string color, string name, string line, int? progress, bool failed, StackPanel? actions)
    {
        var nameText = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name };
        var lineText = new TextBlock { Text = line, FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get(failed ? "WarningText" : "Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(nameText); text.Children.Add(lineText);
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
        var actions = new StackPanel();
        if (request.State != "claimed")
        {
            var label = request.State == "waiting" ? "Annuler" : "Retirer";
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            buttons.Children.Add(Named(RowButton(label, "close", () => CancelServerRequestAsync(request)), $"{label} « {request.Name} »"));
            actions.Children.Add(buttons);
        }
        var failed = request.State == "failed";
        return ServerRowView(failed ? "warning" : "clock", failed ? "Warning" : "Faint", request.Name, RemoteLines.Request(request), null, failed, actions);
    }

    private ServerRow ServerDownloadRow(RelayDownload download, bool offer)
    {
        var actions = new StackPanel();
        if (offer && RemoteLines.Controllable(download))
        {
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            if (download.Status != "failed")
                buttons.Children.Add(RemoteLines.Paused(download)
                    ? Named(RowButton("Reprendre", "play", () => ControlServerAsync(download, "resume")), $"Reprendre « {download.Name} »")
                    : Named(RowButton("Pause", "pause", () => ControlServerAsync(download, "pause")), $"Mettre en pause « {download.Name} »"));
            buttons.Children.Add(Named(RowButton("Annuler", "close", () => { ConfirmServerCancel(actions, download); return Task.CompletedTask; }), $"Annuler « {download.Name} »"));
            actions.Children.Add(buttons);
        }
        var (icon, color) = download.Status switch { "failed" => ("warning", "Warning"), "completed" or "seeding" => ("check", "Success"), "downloading" => ("download", "TextBody"), _ => ("clock", "Faint") };
        return ServerRowView(icon, color, download.Name, RemoteLines.Download(download), download.Progress, download.Status == "failed", actions);
    }

    /// <summary>« Annuler » deletes what TorLink already has: asked once more, in the row.</summary>
    private void ConfirmServerCancel(StackPanel actions, RelayDownload download)
    {
        var kept = actions.Children.Cast<UIElement>().ToList();
        actions.Children.Clear();
        actions.Children.Add(new TextBlock { Text = "TorLink l’arrête et supprime ce qu’il en a déjà reçu sur le PC serveur.", FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get("WarningText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new WrapPanel { Margin = new Thickness(-6, 4, 0, 0) };
        var confirm = Named(RowButton("Annuler le téléchargement", "close", () => ControlServerAsync(download, "delete")), $"Annuler le téléchargement de « {download.Name} »");
        confirm.Foreground = Palette.Get("Danger");
        buttons.Children.Add(confirm);
        buttons.Children.Add(Named(RowButton("Garder", "check", () =>
        {
            actions.Children.Clear();
            foreach (var element in kept) actions.Children.Add(element);
            return Task.CompletedTask;
        }), $"Garder « {download.Name} »"));
        actions.Children.Add(buttons);
        _ = Dispatcher.BeginInvoke(() => confirm.Focus(), DispatcherPriority.Input);
    }

    private ServerRow ServerPlacedRow(RelayPlaced placed)
    {
        var actions = new StackPanel();
        if (placed.ItemId is { } id && !_demo)
        {
            var buttons = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
            buttons.Children.Add(Named(RowButton("Voir", "play", () => OpenTorLinkItemAsync(id)), $"Voir « {placed.Title} »"));
            actions.Children.Add(buttons);
        }
        return ServerRowView(placed.Ok ? "check" : "warning", placed.Ok ? "Success" : "Warning", placed.Title, Sentence(placed.Status), null, false, actions);
    }

    private static Button Named(Button button, string name) { AutomationProperties.SetName(button, name); return button; }

    private async Task CancelServerRequestAsync(RelayRequest request)
    {
        if (_client is not { } client) return;
        try
        {
            SetNotice(await client.TorLinkCancelAsync(request.Id, _connectionLifetime.Token) switch
            {
                true => request.State == "waiting" ? "Demande annulée." : "Retiré de la liste.",
                false => "Le PC serveur l’a déjà pris : annule-le dans sa liste dès qu’il y apparaît.",
                null => "Cette demande n’est plus là."
            });
        }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(ServerError(ex)); }
        await LoadServerDownloadsAsync();
    }

    private async Task ControlServerAsync(RelayDownload download, string action)
    {
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

    /// <summary>A search TorLink runs on the PC of Jellyfin, followed every second until it answers (90 s at most).</summary>
    private async Task SearchServerAsync()
    {
        if (_client is not { } client) return;
        if (RemoteDownloads.CleanQuery(ServerQueryBox.Text) is not { } query) { ServerQueryBox.Focus(); return; }
        if (!_serverReady) { ServerSearchStatus.Text = RemoteLines.Agent(_serverAgent, DateTimeOffset.UtcNow).Text; return; }
        var category = (ServerCategory.SelectedItem as ComboBoxItem)?.Tag as string ?? "movies";
        var label = RemoteLines.CategoryLabel(category);
        var mine = ++_serverSearchVersion; var ct = _connectionLifetime.Token;
        _serverSearching = true; ServerSearchButton.IsEnabled = false; ServerSearchButton.Content = "Recherche…";
        ServerSearchStatus.Text = $"Recherche de « {query} » en {label} sur le PC serveur…";
        ServerResults.Opacity = 0.45;
        void End(string text) { ServerSearchStatus.Text = text; ServerResults.Children.Clear(); ServerResultsEmpty.Visibility = Visibility.Collapsed; }
        try
        {
            var id = await client.TorLinkSearchAsync(query, category, ct);
            var started = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(1000, ct);
                if (mine != _serverSearchVersion) return;
                if (DateTime.UtcNow - started > ServerSearchWait) { End("Le PC serveur n’a pas répondu à temps. Vérifie que Mira y est ouvert, puis relance la recherche."); return; }
                RelaySearch? state;
                // A hiccup on the way: asked again a second later.
                try { state = await client.TorLinkSearchStateAsync(id, ct); }
                catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested && ex is not RelayRefusedException) { continue; }
                if (mine != _serverSearchVersion) return;
                // Forgotten (Jellyfin restarted meanwhile): it will not come back.
                if (state is null) { End("Cette recherche a été perdue : relance-la."); return; }
                if (state.State == "failed") { End(string.IsNullOrEmpty(state.Error) ? "La recherche n’a pas abouti." : state.Error); return; }
                if (state.State == "done") { ShowServerHits(id, query, category, state.Results ?? []); return; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { if (mine == _serverSearchVersion) End(ServerError(ex)); }
        finally
        {
            if (mine == _serverSearchVersion)
            {
                _serverSearching = false; ServerSearchButton.Content = "Chercher"; ServerSearchButton.IsEnabled = _serverReady; ServerResults.Opacity = 1;
            }
        }
    }

    /// <summary>
    /// The results by title and episode (or season, or film), the best release of each first: its quality and source,
    /// its size and sources, its full name; the other releases folded under it.
    /// </summary>
    private void ShowServerHits(string searchId, string query, string category, IReadOnlyList<RelayHit> hits)
    {
        var label = RemoteLines.CategoryLabel(category);
        ServerResultsEmpty.Visibility = Visibility.Collapsed; ServerResults.Children.Clear(); SmoothScroll.Jump(ServerResultsScroll);
        if (hits.Count == 0) { ServerSearchStatus.Text = $"Aucun résultat pour « {query} » en {label}. Essaie un autre titre, ou une autre catégorie."; return; }
        var groups = ReleaseRanking.Group(hits, category);
        ServerSearchStatus.Text = $"{Count(hits.Count, "version", "versions")} pour « {query} » en {label}, regroupées en {Count(groups.Count, "titre", "titres")}. La meilleure de chacun en premier.";
        foreach (var group in groups) ServerResults.Children.Add(ServerGroupView(searchId, group));
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private Border ServerGroupView(string searchId, ReleaseGroup group)
    {
        var panel = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (group.Label.Length > 0)
        {
            var episode = new Border { CornerRadius = new CornerRadius(4), Background = Palette.Get("Raised"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = group.Label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("TextBody") } };
            DockPanel.SetDock(episode, Dock.Right); head.Children.Add(episode);
        }
        head.Children.Add(new TextBlock { Text = group.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = group.Title });
        panel.Children.Add(head);
        panel.Children.Add(ServerReleaseView(searchId, group.Best, best: true));
        if (group.Others.Count > 0)
        {
            var others = new StackPanel { Visibility = Visibility.Collapsed };
            foreach (var entry in group.Others) others.Children.Add(ServerReleaseView(searchId, entry, best: false));
            var more = Count(group.Others.Count, "autre version", "autres versions");
            var text = new TextBlock { Text = more, FontSize = 12, FontWeight = FontWeights.SemiBold };
            var chevron = new Icon { Kind = "down", Width = 14, Height = 14, Foreground = Palette.Get("TextBody"), Margin = new Thickness(6, 0, 0, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal }; content.Children.Add(text); content.Children.Add(chevron);
            var toggle = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 6, 9, 6), Margin = new Thickness(-8, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Content = content, Foreground = Palette.Get("TextBody") };
            AutomationProperties.SetName(toggle, $"{more} de {group.Title}");
            toggle.Click += (_, _) =>
            {
                var open = others.Visibility != Visibility.Visible;
                others.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                text.Text = open ? "Masquer les autres versions" : more; chevron.Kind = open ? "up" : "down";
                AutomationProperties.SetName(toggle, open ? "Masquer les autres versions" : $"{more} de {group.Title}");
            };
            panel.Children.Add(toggle); panel.Children.Add(others);
        }
        var card = new Border { Background = Palette.Get("Surface"), CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 16, 18, 14), Margin = new Thickness(0, 0, 0, 12), Child = panel };
        AutomationProperties.SetName(card, group.Label.Length > 0 ? $"{group.Title} · {group.Label}" : group.Title);
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
        quality.Children.Add(new TextBlock { Text = ReleaseRanking.QualityLine(entry.Release) is { Length: > 0 } line ? line : "Qualité non précisée", FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (best) quality.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Palette.Get("Accent"), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "Meilleur choix", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Palette.Get("OnAccent") } });
        text.Children.Add(quality);
        text.Children.Add(new TextBlock { Text = RemoteLines.Hit(hit), FontSize = 12, Foreground = Palette.Get("TextSecondary"), Margin = new Thickness(0, 4, 0, 0) });
        text.Children.Add(new TextBlock { Text = hit.Name, FontSize = 11.5, LineHeight = 16, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 33, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0), ToolTip = hit.Name });
        if (ReleaseRanking.Warning(entry) is { Length: > 0 } caution)
            text.Children.Add(new TextBlock { Text = caution, FontSize = 12, Foreground = Palette.Get("WarningText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        grid.Children.Add(text);
        var key = searchId + "/" + hit.Id;
        var button = new Button { Style = (Style)FindResource(best ? "Primary" : "Quiet"), Padding = new Thickness(16, 9, 16, 9), VerticalAlignment = VerticalAlignment.Center };
        ShowSendState(button, _serverSent.Contains(key), best);
        AutomationProperties.SetName(button, $"Télécharger {hit.Name}");
        button.Click += async (_, _) => await SendServerHitAsync(searchId, hit, button, best);
        Grid.SetColumn(button, 1); grid.Children.Add(button);
        if (best) return grid;
        // The other versions, each under a hairline.
        return new Border { BorderBrush = Palette.Get("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0), Margin = new Thickness(0, 12, 0, 0), Child = grid };
    }

    private void ShowSendState(Button button, bool sent, bool best)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Kind = sent ? "check" : "download", Width = 15, Height = 15, Margin = new Thickness(0, 0, 7, 0), Foreground = Palette.Get(best && !sent ? "OnAccent" : "TextBody") });
        content.Children.Add(new TextBlock { Text = sent ? "Envoyé" : "Télécharger", FontSize = 13, FontWeight = FontWeights.SemiBold });
        button.Content = content; button.IsEnabled = !sent;
    }

    private async Task SendServerHitAsync(string searchId, RelayHit hit, Button button, bool best)
    {
        if (_client is not { } client) return;
        button.IsEnabled = false;
        try
        {
            await client.TorLinkRequestAsync(searchId, hit, _connectionLifetime.Token);
            _serverSent.Add(searchId + "/" + hit.Id); ShowSendState(button, sent: true, best);
            await SentToServerAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { button.IsEnabled = true; SetNotice(ServerError(ex)); }
    }

    /// <summary>Its row, with its progress, is at the top of the right column.</summary>
    private async Task SentToServerAsync()
    {
        SetNotice(_serverReady ? "Envoyé au PC serveur : sa progression s’affiche à droite." : "Demande gardée : le PC serveur la fera dès que Mira y sera ouvert.");
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
        if (_client is not { } client || !ServerMagnetSend.IsEnabled) return;
        var magnet = ServerMagnetBox.Text.Trim();
        if (RemoteDownloads.InfoHash(magnet) is null) { ShowMagnetError("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »)."); ServerMagnetBox.Focus(); return; }
        ServerMagnetSend.IsEnabled = false;
        try
        {
            await client.TorLinkRequestAsync(magnet, _connectionLifetime.Token);
            CloseServerMagnet(); ServerMagnetButton.Focus();
            await SentToServerAsync();
        }
        catch (Exception ex) when (IsExpected(ex)) { ShowMagnetError(ServerError(ex)); }
        finally { ServerMagnetSend.IsEnabled = true; }
    }
    private void ShowMagnetError(string text) { ServerMagnetError.Text = text; ServerMagnetError.Visibility = Visibility.Visible; }
}
