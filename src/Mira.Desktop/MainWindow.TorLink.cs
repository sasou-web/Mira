using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;
using Mira.Desktop.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// TorLink inside Mira: its own terminal interface on a page of the rail, and finished downloads placed in the
/// Jellyfin libraries, reported to Jellyfin, then followed until the title appears in the library.
/// </summary>
public partial class MainWindow
{
    private readonly TorLinkState _torlinkState = TorLinkState.ForCurrentUser();
    private readonly DispatcherTimer _torlinkDebounce = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _torlinkPoll = new() { Interval = TimeSpan.FromSeconds(20) };
    // A terminal still not drawn after this delay gets a way out instead of an endless « Ouverture de TorLink… ».
    private readonly DispatcherTimer _torlinkStartWatch = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _torlinkLifetime = new();
    private readonly HashSet<string> _torlinkRequested = [], _torlinkAnnounced = [];
    private TorLinkImporter? _torlinkImporter;
    private TorLinkTerminal? _torlinkTerminal;
    private TorLinkInstallation? _torlinkInstallation;
    private FileSystemWatcher? _torlinkWatcher;
    private MediaLibraries _torlinkLibraries = new(null, null, null), _torlinkDetected = new(null, null, null);
    private List<TorLinkCompletion> _torlinkEarlier = [];
    private (DateTime Stamp, long Length) _torlinkHistoryStamp;
    private bool _torlinkEnabled, _torlinkReady, _torlinkBusy, _torlinkAgain, _torlinkChecking;
    private string? _torlinkBlocker, _torlinkAction, _torlinkSecondary;

    private void InitializeTorLink()
    {
        // Automated visual, player and Windows checks never touch downloads or libraries; the TorLink check only in isolation.
        _torlinkEnabled = !_args.Any(x => x is "--visual-check" or "--player-check" or "--windows-check" or "--public-gallery")
            && (!_args.Contains("--torlink-check") || TorLinkCheckIsolated());
        TorLinkNav.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_torlinkEnabled) return;
        _torlinkImporter = new TorLinkImporter(_profile.DirectoryPath);
        _torlinkImporter.Changed += entry => Dispatcher.BeginInvoke(() => TorLinkEntryChanged(entry));
        _torlinkDebounce.Tick += async (_, _) => { _torlinkDebounce.Stop(); await ProcessTorLinkAsync(); };
        _torlinkPoll.Tick += (_, _) => TorLinkPoll();
        _torlinkPoll.Start();
        _torlinkStartWatch.Tick += (_, _) =>
        {
            _torlinkStartWatch.Stop();
            if (_torlinkBlocker is null && _torlinkTerminal?.State is null or TerminalState.Idle or TerminalState.Starting)
                ShowTorLinkPanel("info", "TorLink tarde à s’afficher", "Le terminal intégré ne répond pas encore. Réessayer recharge la page ; les téléchargements ne sont pas touchés.", "Réessayer", "reload");
        };
        WatchTorLinkHistory();
    }

    // ---- Automatic placement ------------------------------------------------------------------

    /// <summary>TorLink rewrites history.json each time a download finishes: that is the signal to import.</summary>
    private void WatchTorLinkHistory()
    {
        if (_torlinkWatcher is not null || !Directory.Exists(_torlinkState.DataDirectory)) return;
        try
        {
            var watcher = new FileSystemWatcher(_torlinkState.DataDirectory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            void Changed(string? name) { if (string.Equals(name, "history.json", StringComparison.OrdinalIgnoreCase)) Dispatcher.BeginInvoke(QueueTorLinkImport); }
            watcher.Changed += (_, e) => Changed(e.Name); watcher.Created += (_, e) => Changed(e.Name); watcher.Renamed += (_, e) => Changed(e.Name);
            watcher.EnableRaisingEvents = true;
            _torlinkWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    private void QueueTorLinkImport() { if (!_torlinkEnabled) return; _torlinkDebounce.Stop(); _torlinkDebounce.Start(); }

    /// <summary>Safety net for missed file events, retries of downloads still waiting, and Jellyfin availability.</summary>
    private void TorLinkPoll()
    {
        WatchTorLinkHistory();
        if (HistoryStamp() != _torlinkHistoryStamp || _torlinkImporter?.Entries.Any(x => x.State == TorLinkImportState.Waiting) == true) QueueTorLinkImport();
        _ = CheckTorLinkAvailabilityAsync();
    }

    private (DateTime, long) HistoryStamp()
    {
        try { var file = new FileInfo(_torlinkState.HistoryFile); return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    /// <summary>Library folders: manual choices first, otherwise the Jellyfin libraries this PC can reach.</summary>
    private async Task RefreshTorLinkLibrariesAsync()
    {
        if (!_torlinkEnabled) return;
        List<VirtualFolder>? folders = null;
        if (_client is { } client && !_demo)
        {
            try { folders = await client.VirtualFoldersAsync(_torlinkLifetime.Token); }
            catch (Exception ex) when (IsExpected(ex)) { }
        }
        // A remote or containerised server may describe paths this PC does not have: those are not used.
        var detected = MediaLibraries.FromJellyfin(folders);
        _torlinkDetected = await Task.Run(() => new MediaLibraries(Reachable(detected.Movies), Reachable(detected.Series), Reachable(detected.Anime)));
        _torlinkReady = true;
        ApplyTorLinkLibraries(force: true);
    }

    private void ApplyTorLinkLibraries(bool force = false)
    {
        var previous = _torlinkLibraries;
        _torlinkLibraries = new(Manual(_settings.TorLinkMoviesFolder) ?? _torlinkDetected.Movies, Manual(_settings.TorLinkSeriesFolder) ?? _torlinkDetected.Series, Manual(_settings.TorLinkAnimeFolder) ?? _torlinkDetected.Anime);
        RenderTorLinkImports();
        if (force || _torlinkLibraries != previous) QueueTorLinkImport();
    }

    private static string? Manual(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Reachable(string? path)
    {
        try { return path is not null && Directory.Exists(path) ? path : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private async Task ProcessTorLinkAsync()
    {
        if (_torlinkImporter is not { } importer || !_torlinkReady || _torlinkLifetime.IsCancellationRequested) return;
        if (_torlinkBusy) { _torlinkAgain = true; return; }
        _torlinkBusy = true;
        try
        {
            do
            {
                _torlinkAgain = false;
                _torlinkHistoryStamp = HistoryStamp();
                var libraries = _torlinkLibraries; var automatic = _settings.TorLinkAutoImport; var token = _torlinkLifetime.Token;
                var mode = _settings.TorLinkKeepSeeding ? ImportMode.KeepSeeding : ImportMode.Move;
                var requested = _torlinkRequested.ToList(); _torlinkRequested.Clear();
                var state = _torlinkState;
                // File work stays on the thread pool: a copy never slows the interface or the video.
                _torlinkEarlier = await Task.Run(async () =>
                {
                    await importer.ProcessAsync(state, libraries, mode, automatic, requested, token);
                    var known = importer.Entries.Select(x => x.Id).ToHashSet();
                    return (state.ReadHistory() ?? []).Where(x => !known.Contains(x.Id)).Take(5).ToList();
                }, token);
                RenderTorLinkImports();
                await NotifyTorLinkAsync();
            } while (_torlinkAgain && !_torlinkLifetime.IsCancellationRequested);
        }
        catch (OperationCanceledException) { }
        finally { _torlinkBusy = false; }
    }

    private void RequestTorLinkImport(string id) { _torlinkRequested.Add(id); _ = ProcessTorLinkAsync(); }

    /// <summary>Tells Jellyfin where new titles arrived; its own folder monitor would find them too, a little later.</summary>
    private async Task NotifyTorLinkAsync()
    {
        if (_client is not { } client || _demo || _torlinkImporter is not { } importer) return;
        foreach (var entry in importer.Entries.Where(x => !x.Notified && x.State is TorLinkImportState.Imported or TorLinkImportState.Partial && x.Folders.Count > 0).Take(10).ToList())
        {
            try { if (await client.ReportMediaChangedAsync(entry.Folders, ct: _torlinkLifetime.Token)) importer.MarkNotified(entry.Id); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Finds the imported titles Jellyfin has indexed, by their exact file paths: one request for all of them.</summary>
    private async Task CheckTorLinkAvailabilityAsync()
    {
        if (_torlinkChecking || _client is not { } client || _demo || _torlinkImporter is not { } importer) return;
        var pending = importer.Entries.Where(x => x.JellyfinId is null && x.State is TorLinkImportState.Imported or TorLinkImportState.Partial
            && DateTimeOffset.UtcNow - x.UpdatedAt < TimeSpan.FromHours(6) && x.Files.Any(f => f.Role == "video")).Take(20).ToList();
        if (pending.Count == 0) return;
        _torlinkChecking = true;
        try
        {
            var found = await client.FindIndexedAsync(pending.SelectMany(x => x.Files.Where(f => f.Role == "video").Select(f => f.Destination)).ToList(), _torlinkLifetime.Token);
            foreach (var entry in pending)
                if (entry.Files.Where(f => f.Role == "video").Select(f => found.GetValueOrDefault(f.Destination)).FirstOrDefault(x => x is not null) is { } id)
                    importer.MarkAvailable(entry.Id, id);
        }
        catch (Exception ex) when (IsExpected(ex)) { }
        finally { _torlinkChecking = false; }
    }

    private void TorLinkEntryChanged(TorLinkImportEntry entry)
    {
        RenderTorLinkImports();
        if (entry.State is TorLinkImportState.Importing or TorLinkImportState.Waiting) return;
        // One message per outcome, not one per step.
        if (!_torlinkAnnounced.Add($"{entry.Id}:{entry.State}:{entry.JellyfinId is not null}")) return;
        if (TorLinkOverlay.Visibility != Visibility.Visible && entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial) TorLinkBadge.Visibility = Visibility.Visible;
        var text = entry.State switch
        {
            TorLinkImportState.Imported when entry.JellyfinId is not null => $"« {TitleOf(entry)} » est disponible dans ta bibliothèque.",
            TorLinkImportState.Imported => $"« {TitleOf(entry)} » rejoint {KindName(entry.Kind)}. Jellyfin l’ajoute à la bibliothèque.",
            TorLinkImportState.Partial => $"« {TitleOf(entry)} » : une partie des fichiers est rangée. Détails dans TorLink.",
            TorLinkImportState.Conflict => $"« {TitleOf(entry)} » est déjà dans la bibliothèque : rien n’a été remplacé.",
            TorLinkImportState.Failed => $"« {TitleOf(entry)} » n’a pas pu être rangé. Détails dans TorLink.",
            TorLinkImportState.Blocked => "TorLink : choisis les dossiers de ta bibliothèque dans Réglages → TorLink.",
            _ => null
        };
        if (text is not null && !_playing) SetNotice(text);
    }

    private async Task ReclassifyTorLinkAsync(string id, MediaKind kind)
    {
        if (_torlinkImporter is not { } importer) return;
        var libraries = _torlinkLibraries; var token = _torlinkLifetime.Token;
        try
        {
            var (entry, left) = await Task.Run(() => importer.ReclassifyAsync(id, kind, libraries, token), token);
            if (entry is { State: TorLinkImportState.Imported or TorLinkImportState.Partial } && _client is { } client && !_demo
                && await client.ReportMediaChangedAsync(entry.Folders, left, token)) importer.MarkNotified(entry.Id);
        }
        catch (OperationCanceledException) { }
    }

    // ---- Page -----------------------------------------------------------------------------------

    private async void TorLinkNav_Click(object sender, RoutedEventArgs e) => await ShowTorLinkAsync();

    private async Task ShowTorLinkAsync()
    {
        if (!_torlinkEnabled) return;
        if (SettingsOverlay.Visibility == Visibility.Visible) { AutoSaveSettings(); _ = Motion.HideAsync(SettingsOverlay); }
        ClosePreview(); ++_detailVersion; _returnToDetail = null; _ = Motion.HideAsync(DetailOverlay); _ = Motion.HideAsync(GuideOverlay);
        if (TorLinkOverlay.Visibility != Visibility.Visible || !TorLinkOverlay.IsHitTestVisible) Motion.Reveal(TorLinkOverlay, 260, 10);
        TorLinkBadge.Visibility = Visibility.Collapsed;
        UpdateNavigation(); UpdateHeroClock(); RenderTorLinkImports(); UpdateTorLinkState();
        _ = RefreshTorLinkLibrariesAsync();
        await StartTorLinkAsync(restart: false);
    }

    private async Task CloseTorLinkAsync()
    {
        if (TorLinkOverlay.IsKeyboardFocusWithin) TorLinkNav.Focus();
        await Motion.HideAsync(TorLinkOverlay, 170);
        UpdateNavigation(); UpdateHeroClock();
    }

    /// <summary>Opens TorLink in the page: found through its shortcut or the chosen folder, run once, never twice.</summary>
    private async Task StartTorLinkAsync(bool restart)
    {
        if (_torlinkTerminal?.Running != true) { _torlinkStartWatch.Stop(); _torlinkStartWatch.Start(); }
        _torlinkInstallation = TorLinkInstallation.Locate(_settings.TorLinkPath);
        if (_torlinkInstallation is null) { SetTorLinkBlocker("missing"); return; }
        if (!_torlinkInstallation.HasRuntime) { SetTorLinkBlocker("runtime"); return; }
        _torlinkTerminal ??= CreateTorLinkTerminal();
        await _torlinkTerminal.InitializeAsync(TorLinkTerminalHost);
        if (_torlinkTerminal.State == TerminalState.Unavailable) { SetTorLinkBlocker("webview"); return; }
        if (_torlinkTerminal.Running) { SetTorLinkBlocker(null); if (TorLinkOverlay.IsVisible) _torlinkTerminal.Focus(); return; }
        if (_torlinkTerminal.State is TerminalState.Exited or TerminalState.Starting && !restart) { SetTorLinkBlocker(null); return; }
        // Two TorLink would share the same queue and write the same files.
        var installation = _torlinkInstallation;
        if (await Task.Run(() => installation.RunningElsewhere(null)) is not null) { SetTorLinkBlocker("elsewhere"); return; }
        SetTorLinkBlocker(null);
        _torlinkTerminal.Start(installation);
    }

    private TorLinkTerminal CreateTorLinkTerminal()
    {
        var terminal = new TorLinkTerminal(Dispatcher, Path.Combine(_profile.DirectoryPath, "webview2"));
        terminal.StateChanged += UpdateTorLinkState;
        terminal.BackRequested += () => { if (TorLinkOverlay.IsVisible && TorLinkOverlay.IsHitTestVisible) GoBack(); };
        return terminal;
    }

    private void SetTorLinkBlocker(string? blocker) { _torlinkBlocker = blocker; UpdateTorLinkState(); }

    private void UpdateTorLinkState()
    {
        var state = _torlinkTerminal?.State ?? TerminalState.Idle;
        if (_torlinkBlocker is not null || state is TerminalState.Running or TerminalState.Exited or TerminalState.Unavailable) _torlinkStartWatch.Stop();
        var (status, dot) = _torlinkBlocker is not null ? ("Indisponible", "#D5B787") : state switch
        {
            TerminalState.Running => ("En cours", "#86C6A2"),
            TerminalState.Starting => ("Démarrage…", "#D5B787"),
            _ => ("Fermé", "#6B6B74")
        };
        TorLinkStatusText.Text = status; TorLinkStatusDot.Fill = Brush(dot);
        switch (_torlinkBlocker)
        {
            case "missing":
                ShowTorLinkPanel("folder", "TorLink est introuvable", "Indique le dossier de TorLink, celui qui contient torlink.bat. Mira le retrouvera ensuite tout seul.", "Choisir le dossier…", "browse"); return;
            case "runtime":
                ShowTorLinkPanel("info", "Node.js est nécessaire", "TorLink a besoin de Node.js 22 ou plus récent, dans son dossier « node » ou installé sur ce PC (nodejs.org).", "Réessayer", "retry"); return;
            case "elsewhere":
                ShowTorLinkPanel("info", "TorLink est déjà ouvert", "Il tourne dans une autre fenêtre. Quitte-le (q ou Ctrl+C) puis réessaie : deux TorLink ne peuvent pas gérer les mêmes téléchargements.", "Réessayer", "retry"); return;
            case "webview":
                ShowTorLinkPanel("info", "Terminal intégré indisponible", (_torlinkTerminal?.Problem ?? "") + " TorLink peut s’ouvrir dans sa propre fenêtre ; ses téléchargements terminés seront quand même rangés.", "Ouvrir dans une fenêtre", "window"); return;
        }
        switch (state)
        {
            case TerminalState.Running: TorLinkStatePanel.Visibility = Visibility.Collapsed; break;
            case TerminalState.Starting: ShowTorLinkPanel("download", "Ouverture de TorLink…", "Son interface apparaît ici dans un instant.", null, null); break;
            case TerminalState.Exited when _torlinkTerminal?.Problem is { } problem:
                ShowTorLinkPanel("info", _torlinkTerminal.ExitCode is null ? "TorLink n’a pas démarré" : "TorLink s’est arrêté", problem, "Réessayer", "retry"); break;
            case TerminalState.Exited: ShowTorLinkPanel("download", "TorLink est fermé", "Les téléchargements interrompus reprendront à la prochaine ouverture.", "Relancer TorLink", "retry"); break;
            default: ShowTorLinkPanel("download", "Ouverture de TorLink…", "Son interface apparaît ici dans un instant.", null, null); break;
        }
    }

    private void ShowTorLinkPanel(string icon, string title, string hint, string? action, string? kind, string? secondary = null, string? secondaryKind = null)
    {
        TorLinkStateIcon.Kind = icon; TorLinkStateTitle.Text = title; TorLinkStateHint.Text = hint.Trim();
        TorLinkPrimaryAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        TorLinkPrimaryAction.Content = action; _torlinkAction = kind;
        TorLinkSecondaryAction.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        TorLinkSecondaryAction.Content = secondary; _torlinkSecondary = secondaryKind;
        if (TorLinkStatePanel.Visibility != Visibility.Visible) Motion.Reveal(TorLinkStatePanel, 200, 0);
    }

    private async void TorLinkPrimary_Click(object sender, RoutedEventArgs e) => await RunTorLinkActionAsync(_torlinkAction);
    private async void TorLinkSecondary_Click(object sender, RoutedEventArgs e) => await RunTorLinkActionAsync(_torlinkSecondary);
    private async Task RunTorLinkActionAsync(string? action)
    {
        switch (action)
        {
            case "retry": await StartTorLinkAsync(restart: true); break;
            case "reload":
                _torlinkTerminal?.Reload();
                UpdateTorLinkState();
                await StartTorLinkAsync(restart: true);
                break;
            case "browse":
                if (ChooseTorLinkFolder() is { } root) { _settings.TorLinkPath = root; _profile.SaveSettings(_settings); await StartTorLinkAsync(restart: true); }
                break;
            case "window" when _torlinkInstallation is { } installation:
                // Its own window is a TorLink like the one of its shortcut: still only one at a time.
                if (await Task.Run(() => installation.RunningElsewhere(null)) is not null) { SetNotice("TorLink est déjà ouvert dans une autre fenêtre."); break; }
                try { installation.OpenInWindow(); SetNotice("TorLink s’ouvre dans sa propre fenêtre ; ses téléchargements terminés seront rangés."); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { SetNotice("TorLink n’a pas pu s’ouvrir dans une fenêtre."); }
                break;
        }
    }

    /// <summary>Folder picker that only accepts a real TorLink folder.</summary>
    private string? ChooseTorLinkFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Dossier de TorLink (celui qui contient torlink.bat)" };
        if (dialog.ShowDialog(this) != true) return null;
        if (TorLinkInstallation.Validate(dialog.FolderName) is { } found) return found.Root;
        SetNotice("Ce dossier ne contient pas TorLink (package torlnk avec dist\\cli.cjs).");
        return null;
    }

    private void TorLinkFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _torlinkState.DownloadDirectory();
        if (Directory.Exists(folder)) OpenInExplorer(folder, null);
        else SetNotice("Le dossier de téléchargement de TorLink n’existe pas encore : il sera créé au premier téléchargement.");
    }

    private void TorLinkSettings_Click(object sender, RoutedEventArgs e)
    {
        Settings_Click(this, new());
        SelectSettingsTab("torlink");
        _ = Dispatcher.BeginInvoke(() => TorLinkSettingsNav.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Keys WebView2 forwards from the terminal (arrows, Escape, shortcuts) belong to TorLink, not to Mira.</summary>
    private bool FromTorLinkTerminal(RoutedEventArgs e) => _torlinkTerminal?.View is { } view && (ReferenceEquals(e.OriginalSource, view) || view.IsKeyboardFocusWithin);

    private async Task StopTorLinkAsync()
    {
        _torlinkLifetime.Cancel(); _torlinkPoll.Stop(); _torlinkDebounce.Stop(); _torlinkStartWatch.Stop();
        _torlinkWatcher?.Dispose(); _torlinkWatcher = null;
        if (_torlinkTerminal is { } terminal)
        {
            _torlinkTerminal = null;
            await terminal.StopAsync();
            terminal.Dispose();
        }
    }

    // ---- Import list ------------------------------------------------------------------------------

    private void RenderTorLinkImports()
    {
        if (_torlinkImporter is not { } importer || TorLinkImports is null) return;
        TorLinkImportSummary.Text = ImportSummary();
        TorLinkImports.Children.Clear();
        foreach (var entry in importer.Entries.Take(40)) TorLinkImports.Children.Add(ImportRow(entry));
        foreach (var earlier in _torlinkEarlier) TorLinkImports.Children.Add(EarlierRow(earlier));
        TorLinkImportsEmpty.Visibility = TorLinkImports.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string ImportSummary()
    {
        var libraries = _torlinkLibraries;
        var missing = new[] { ("Films", libraries.RootFor(MediaKind.Movie)), ("Séries", libraries.RootFor(MediaKind.Series)) }.Where(x => x.Item2 is null).Select(x => x.Item1).ToList();
        if (!_settings.TorLinkAutoImport) return "Rangement automatique désactivé : « Importer » range un téléchargement à la demande.";
        if (missing.Count == 2) return "Choisis les dossiers de ta bibliothèque dans Réglages → TorLink pour ranger les téléchargements.";
        if (missing.Count == 1) return $"Rangement automatique. Dossier {missing[0]} à choisir dans Réglages → TorLink.";
        return libraries.Anime is null ? "Rangement automatique dans Films et Séries (les animes rejoignent les séries)." : "Rangement automatique dans Films, Séries et Animes.";
    }

    private FrameworkElement ImportRow(TorLinkImportEntry entry)
    {
        var (icon, color) = entry.State switch
        {
            TorLinkImportState.Imported when entry.JellyfinId is not null => ("check", "#86C6A2"),
            TorLinkImportState.Imported or TorLinkImportState.Partial => (entry.Kind == MediaKind.Movie ? "film" : "series", "#D6D6D8"),
            TorLinkImportState.Waiting or TorLinkImportState.Importing => ("clock", "#D5B787"),
            TorLinkImportState.Blocked => ("sliders", "#D5B787"),
            TorLinkImportState.Ignored => ("info", "#7C7C85"),
            _ => ("info", "#F3AD99")
        };
        var actions = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        if (entry.JellyfinId is { } id && !_demo) actions.Children.Add(RowButton("Voir", "play", async () => await OpenTorLinkItemAsync(id)));
        // The folder is looked up on click only: a library on an offline network drive must not freeze this list.
        if (entry.Folders.Count > 0)
            actions.Children.Add(RowButton("Dossier", "folder", async () =>
            {
                var folders = entry.Folders; var video = entry.Files.FirstOrDefault(x => x.Role == "video")?.Destination;
                if (await Task.Run(() => folders.FirstOrDefault(Directory.Exists)) is { } folder) OpenInExplorer(folder, video);
                else SetNotice("Ce dossier n’existe plus dans la bibliothèque.");
            }));
        if (entry.State is TorLinkImportState.Waiting or TorLinkImportState.Blocked or TorLinkImportState.Conflict or TorLinkImportState.Failed or TorLinkImportState.Partial)
            actions.Children.Add(RowButton("Réessayer", "refresh", () => { RequestTorLinkImport(entry.Id); return Task.CompletedTask; }));
        ContextMenu? menu = null;
        // Only what Mira placed can be moved to another library; files found already there stay put.
        if (entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial && entry.Files.Any(x => !x.Found))
        {
            menu = new ContextMenu();
            foreach (var (kind, label, glyph) in new[] { (MediaKind.Movie, "Classer comme film", "film"), (MediaKind.Series, "Classer comme série", "series"), (MediaKind.Anime, "Classer comme anime", "series") })
                if (kind != entry.Kind) menu.Items.Add(MenuAction(label, glyph, () => ReclassifyTorLinkAsync(entry.Id, kind)));
            var classify = RowButton("Classer", "sliders", () => Task.CompletedTask);
            classify.Click += (_, _) => { menu.PlacementTarget = classify; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            actions.Children.Add(classify);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = TitleOf(entry), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Name });
        text.Children.Add(new TextBlock { Text = DetailOf(entry), FontSize = 11.5, LineHeight = 17, Foreground = Brush("#96969D"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (actions.Children.Count > 0) text.Children.Add(actions);
        return Row(icon, color, text, menu, TitleOf(entry) + ", " + StatusOf(entry));
    }

    private FrameworkElement EarlierRow(TorLinkCompletion completion)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = completion.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = completion.Name });
        var when = completion.CompletedAt == DateTimeOffset.MinValue ? "" : $" le {completion.CompletedAt.ToLocalTime():d} à {completion.CompletedAt.ToLocalTime():HH:mm}";
        text.Children.Add(new TextBlock { Text = $"Terminé{when} · pas encore rangé", FontSize = 11.5, Foreground = Brush("#96969D"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var actions = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        actions.Children.Add(RowButton("Importer", "download", () => { RequestTorLinkImport(completion.Id); return Task.CompletedTask; }));
        text.Children.Add(actions);
        return Row("clock", "#7C7C85", text, null, completion.Name + ", pas encore rangé");
    }

    private static Border Row(string icon, string color, FrameworkElement content, ContextMenu? menu, string name)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        var row = new Border { Background = Brush("#101012"), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 12, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid, ContextMenu = menu };
        AutomationProperties.SetName(row, name);
        return row;
    }

    private Button RowButton(string text, string icon, Func<Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Kind = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold });
        var button = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 6, 9, 6), Margin = new Thickness(0, 0, 2, 0), Content = content, Foreground = Brush("#D6D6DA") };
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task OpenTorLinkItemAsync(string id)
    {
        if (_client is not { } client) return;
        try { await ShowDetailsAsync(await client.ItemAsync(id)); }
        catch (Exception ex) when (IsExpected(ex)) { SetNotice(Friendly(ex)); }
    }

    private static void OpenInExplorer(string folder, string? file)
    {
        try
        {
            var arguments = file is not null && File.Exists(file) ? "/select,\"" + file + "\"" : "\"" + folder + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
    }

    private static string TitleOf(TorLinkImportEntry entry) => entry.Title.Length > 0 ? entry.Title : entry.Name;
    private static string KindName(MediaKind? kind) => kind switch { MediaKind.Movie => "Films", MediaKind.Series => "Séries", MediaKind.Anime => "Animes", _ => "la bibliothèque" };
    private static string StatusOf(TorLinkImportEntry entry) => entry.State switch
    {
        TorLinkImportState.Waiting => "en attente des fichiers",
        TorLinkImportState.Blocked => "dossier de bibliothèque à choisir",
        TorLinkImportState.Importing => "rangement en cours…",
        TorLinkImportState.Imported => entry.JellyfinId is not null ? "disponible dans Jellyfin" : entry.Notified ? "Jellyfin l’ajoute" : "rangé",
        TorLinkImportState.Partial => "rangé en partie",
        TorLinkImportState.Conflict => "déjà dans la bibliothèque",
        TorLinkImportState.Failed => "échec",
        _ => "ignoré"
    };
    private static string DetailOf(TorLinkImportEntry entry)
    {
        var status = StatusOf(entry);
        var line = entry.Kind is { } kind && entry.State is not (TorLinkImportState.Ignored or TorLinkImportState.Waiting) ? $"{KindName(kind)} · {status}" : char.ToUpperInvariant(status[0]) + status[1..];
        if (entry.Method is { } method && entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial) line += " · " + method;
        return entry.Message is { Length: > 0 } message ? line + "\n" + message : line;
    }
}
