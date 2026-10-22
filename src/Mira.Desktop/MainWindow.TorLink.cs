using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.TorLink;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// TorLink inside Mira: a page of the rail, with Mira web's screen (MainWindow.ServerDownloads.cs) or TorLink's own
/// terminal interface, and finished downloads placed in the Jellyfin libraries, reported to Jellyfin, then followed until
/// the title appears in the library.
/// </summary>
public partial class MainWindow
{
    private readonly TorLinkState _torlinkState = TorLinkState.ForCurrentUser();
    private readonly DispatcherTimer _torlinkDebounce = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _torlinkPoll = new() { Interval = TimeSpan.FromSeconds(20) };
    // A terminal still not drawn after this delay gets a way out instead of an endless « Ouverture de TorLink… ».
    private readonly DispatcherTimer _torlinkStartWatch = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _torlinkLifetime = new();
    /// <summary>Finished downloads placed in the libraries and followed until Jellyfin has them; null while TorLink is off.</summary>
    private TorLinkPlacement? _torlink;
    private TorLinkImporter? _torlinkImporter => _torlink?.Importer;
    private TorLinkTerminal? _torlinkTerminal;
    private TorLinkInstallation? _torlinkInstallation;
    private bool _torlinkEnabled, _torlinkInstalling, _torlinkLooked, _torlinkRefreshTried;
    private string? _torlinkBlocker, _torlinkAction, _torlinkSecondary;
    // The copy "npx torlnk" left, used by Mira 0.7.4 and earlier: looked for once, only to explain why it is not used.
    private readonly Lazy<bool> _torlinkNpx = new(TorLinkInstallation.NpxCopyFound);
    private static string TorLinkNpxHint => L("Mira n’utilise plus la copie laissée par npx torlnk : installe celui de Mira ou choisis son dossier.", "Mira no longer uses the copy left by npx torlnk: install Mira’s or choose its folder.");
    private TorLinkSetup.Step? _torlinkStep;

    private void InitializeTorLink()
    {
        // Automated visual, player and Windows checks never touch downloads or libraries; the TorLink check only in isolation.
        _torlinkEnabled = !_args.Any(x => x is "--visual-check" or "--player-check" or "--windows-check" or "--public-gallery")
            && (!_args.Contains("--torlink-check") || TorLinkCheckIsolated());
        TorLinkNav.Visibility = _torlinkEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_torlinkEnabled) return;
        _torlink = new TorLinkPlacement(_torlinkState, new TorLinkImporter(_profile.DirectoryPath));
        _torlink.Importer.Changed += entry => Dispatcher.BeginInvoke(() => TorLinkEntryChanged(entry));
        _torlink.HistoryChanged += () => Dispatcher.BeginInvoke(QueueTorLinkImport);
        _torlinkDebounce.Tick += async (_, _) => { _torlinkDebounce.Stop(); await ProcessTorLinkAsync(); };
        _torlinkPoll.Tick += (_, _) => TorLinkPoll();
        _torlinkPoll.Start();
        _torlinkStartWatch.Tick += (_, _) =>
        {
            _torlinkStartWatch.Stop();
            if (_torlinkBlocker is null && _torlinkTerminal?.State is null or TerminalState.Idle or TerminalState.Starting)
                ShowTorLinkPanel("info", L("TorLink tarde à s’afficher", "TorLink is taking a while to show"), L("Le terminal intégré ne répond pas encore. Réessayer recharge la page ; les téléchargements ne sont pas touchés.", "The built-in terminal isn’t answering yet. Trying again reloads the page; downloads aren’t touched."), L("Réessayer", "Try again"), "reload");
        };
        _torlink.Watch();
        StartTorLinkRemote();
        InitializeServerDownloads();
    }
    /// <summary>The TorLink check only works inside its output folder: demo mode, and both --data and TORLINK_STATE_DIR in it.</summary>
    private bool TorLinkCheckIsolated()
    {
        var index = Array.IndexOf(_args, "--torlink-check");
        if (index < 0 || index + 1 >= _args.Length || !_args.Contains("--demo") || !_args.Contains("--data")) return false;
        try
        {
            var output = Path.GetFullPath(_args[index + 1]).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var state = Environment.GetEnvironmentVariable("TORLINK_STATE_DIR");
            return !string.IsNullOrWhiteSpace(state) && Path.GetFullPath(state).StartsWith(output, StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(_profile.DirectoryPath).StartsWith(output, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ---- Automatic placement ------------------------------------------------------------------

    private void QueueTorLinkImport() { if (!_torlinkEnabled) return; _torlinkDebounce.Stop(); _torlinkDebounce.Start(); }

    /// <summary>Safety net for missed file events, retries of downloads still waiting, and Jellyfin availability.</summary>
    private void TorLinkPoll()
    {
        _torlink?.Watch();
        if (_torlink?.PlacementDue() == true) QueueTorLinkImport();
        _ = CheckTorLinkAvailabilityAsync();
    }

    /// <summary>Library folders: manual choices first, otherwise the Jellyfin libraries this PC can reach.</summary>
    private async Task RefreshTorLinkLibrariesAsync()
    {
        if (!_torlinkEnabled || _torlink is not { } torlink) return;
        await torlink.DetectLibrariesAsync(_demo ? null : _client, _torlinkLifetime.Token);
        ApplyTorLinkLibraries(force: true);
    }

    private void ApplyTorLinkLibraries(bool force = false)
    {
        if (_torlink is not { } torlink) return;
        var changed = torlink.Choose(_settings.TorLinkMoviesFolder, _settings.TorLinkSeriesFolder, _settings.TorLinkAnimeFolder);
        RenderTorLinkImports();
        if (force || changed) QueueTorLinkImport();
    }

    private async Task ProcessTorLinkAsync()
    {
        if (_torlink is not { } torlink) return;
        await torlink.PlaceAsync(() => (_settings.TorLinkAutoImport, _settings.TorLinkKeepShared ? ImportMode.KeepSeeding : ImportMode.Move),
            async () => { RenderTorLinkImports(); if (_client is { } client && !_demo) await torlink.NotifyAsync(client, _torlinkLifetime.Token); }, _torlinkLifetime.Token);
    }

    private void RequestTorLinkImport(string id) { _torlink?.Request(id); _ = ProcessTorLinkAsync(); }

    private async Task CheckTorLinkAvailabilityAsync()
    {
        if (_client is { } client && !_demo && _torlink is { } torlink) await torlink.FindIndexedAsync(client, _torlinkLifetime.Token);
    }

    private void TorLinkEntryChanged(TorLinkImportEntry entry)
    {
        RenderTorLinkImports();
        if (_torlink?.FirstOutcome(entry) != true) return;
        if (TorLinkOverlay.Visibility != Visibility.Visible && entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial) TorLinkBadge.Visibility = Visibility.Visible;
        if (TorLinkPlacement.Outcome(entry) is { } text && !_playing) SetNotice(text);
    }

    private async Task ReclassifyTorLinkAsync(string id, MediaKind kind)
    {
        if (_torlink is { } torlink) await torlink.ReclassifyAsync(id, kind, _demo ? null : _client, _torlinkLifetime.Token);
    }

    // ---- Page -----------------------------------------------------------------------------------

    private async void TorLinkNav_Click(object sender, RoutedEventArgs e) => await ShowTorLinkAsync();

    private async Task ShowTorLinkAsync()
    {
        if (!_torlinkEnabled) return;
        // Away from the PC of Jellyfin, TorLink is not offered: it would download next to the wrong library. The page
        // sends searches and downloads to that PC instead, as Mira web does (MainWindow.ServerDownloads.cs).
        var elsewhere = await JellyfinElsewhereAsync();
        if (SettingsOverlay.Visibility == Visibility.Visible) { AutoSaveSettings(); _ = Motion.HideAsync(SettingsOverlay); }
        ClosePreview(); ++_detailVersion; _returnToDetail = null; _ = Motion.HideAsync(DetailOverlay); _ = Motion.HideAsync(GuideOverlay);
        if (TorLinkOverlay.Visibility != Visibility.Visible || !TorLinkOverlay.IsHitTestVisible) Motion.Reveal(TorLinkOverlay, 260, 10);
        TorLinkBadge.Visibility = Visibility.Collapsed;
        UpdateNavigation(); UpdateHeroClock();
        if (elsewhere) { await ShowServerDownloadsAsync(local: false); return; }
        if (_serverScreen && !_serverLocal) HideServerDownloads();
        RenderTorLinkImports(); UpdateTorLinkState();
        _ = RefreshTorLinkLibrariesAsync();
        // Not turned on yet: the list of downloaders, where it is.
        if (!TorLinkActive()) { ShowDownloaders(true); return; }
        await OpenTorLinkViewAsync(restart: false);
    }

    /// <summary>
    /// TorLink on this PC, as chosen: Mira web's screen (searches by « torlnk search », downloads by TorLink without a
    /// window), or TorLink's own terminal. A TorLink to find, to install, to confirm or too old for the screen shows its
    /// panel over the terminal.
    /// </summary>
    private async Task OpenTorLinkViewAsync(bool restart)
    {
        if (!_settings.TorLinkTerminal && (_torlinkInstallation = TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath)) is { HasRuntime: true, Detected: false } found)
        {
            if (found.Outdated)
            {
                HideServerDownloads(); RenderTorLinkImports(); ShowDownloaders(false);
                SetTorLinkBlocker("outdated");
                return;
            }
            ShowDownloaders(false);
            await ShowServerDownloadsAsync(local: true);
            return;
        }
        HideServerDownloads();
        RenderTorLinkImports();
        ShowDownloaders(false);
        await StartTorLinkAsync(restart);
    }

    /// <summary>From one face of TorLink to the other, remembered for the next time.</summary>
    private async void TorLinkView_Click(object sender, RoutedEventArgs e)
    {
        _settings.TorLinkTerminal = _serverScreen && _serverLocal; _profile.SaveSettings(_settings);
        await OpenTorLinkViewAsync(restart: true);
    }

    /// <summary>The header's way between Mira's screen and TorLink's terminal; not over the list of downloaders, nor away from the PC of Jellyfin.</summary>
    private void DescribeTorLinkView()
    {
        var screen = _serverScreen && _serverLocal;
        TorLinkViewButton.Visibility = (_serverScreen && !_serverLocal) || DownloadersPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        TorLinkViewIcon.Kind = screen ? "laptop" : "search";
        TorLinkViewText.Text = screen ? L("Terminal", "Terminal") : L("Écran de Mira", "Mira’s screen");
        var hint = screen ? L("Ouvrir TorLink dans son propre terminal", "Open TorLink in its own terminal") : L("Revenir à l’écran de recherche de Mira", "Back to Mira’s search screen");
        TorLinkViewButton.ToolTip = hint; AutomationProperties.SetName(TorLinkViewButton, hint);
    }

    // ---- Downloaders ------------------------------------------------------------------------------

    /// <summary>
    /// Turned on in the list; before any choice, on when a TorLink is already installed (as before this list existed).
    /// That search reads shortcuts and npm's global folder: it runs once, not at each step of an installation.
    /// </summary>
    private bool TorLinkActive()
    {
        if (_settings.TorLinkActive is { } chosen) return chosen;
        if (!_torlinkLooked) { _torlinkInstallation ??= TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath); _torlinkLooked = true; }
        return _torlinkInstallation is not null;
    }

    /// <summary>The list of downloaders, over the terminal: where TorLink is turned on or off.</summary>
    private void ShowDownloaders(bool show)
    {
        // Mira's screen is drawn over the list: it goes first.
        if (show) HideServerDownloads();
        var active = TorLinkActive(); UpdateTorLinkState();
        if (show && DownloadersPanel.Visibility != Visibility.Visible) Motion.Reveal(DownloadersPanel, 200, 6);
        else if (!show) DownloadersPanel.Visibility = Visibility.Collapsed;
        // Over the list, its card opens TorLink: the header only serves the terminal.
        TorLinkKeysHint.Visibility = TorLinkFolderButton.Visibility = DownloadersButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        TorLinkToggle.IsChecked = active || _torlinkInstalling; TorLinkToggle.IsEnabled = !_torlinkInstalling;
        DescribeTorLinkSetup(); DescribeTorLinkView();
        if (show) _ = Dispatcher.BeginInvoke(() => TorLinkToggle.Focus(), DispatcherPriority.Input);
    }

    private void DescribeTorLinkSetup(string? progress = null, double? share = null)
    {
        // Back on the page during an installation: its last step, not « Désactivé ».
        if (progress is null && _torlinkInstalling && _torlinkStep is { } step) (progress, share) = (step.Text, step.Progress);
        TorLinkSetupTrack.Visibility = share is null ? Visibility.Collapsed : Visibility.Visible;
        if (share is { } value) TorLinkSetupScale.ScaleX = Math.Clamp(value, 0, 1);
        var active = TorLinkActive(); var installation = _torlinkInstallation;
        TorLinkOpenButton.Visibility = active && !_torlinkInstalling ? Visibility.Visible : Visibility.Collapsed;
        TorLinkSetupStatus.Text = progress ?? (installation is null ? L("Désactivé.", "Off.") + (_torlinkNpx.Value ? " " + TorLinkNpxHint : "")
            : (active ? L("Activé", "On") : L("Désactivé", "Off")) + $" · TorLink {installation.Version}" + (installation.Managed ? L(", installé par Mira.", ", installed by Mira.") : $" ({installation.Root}).")
              + (installation.Managed && TorLinkSetup.IsPrepared(_profile.DirectoryPath) ? L(" Sa mise à jour est prête : elle sera utilisée à sa prochaine ouverture.", " Its update is ready: it will be used the next time it opens.") : ""));
    }

    private void Downloaders_Click(object sender, RoutedEventArgs e) => ShowDownloaders(true);
    private async void TorLinkOpen_Click(object sender, RoutedEventArgs e) => await OpenTorLinkViewAsync(restart: false);
    private async void TorLinkToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_torlinkInstalling) return;
        if (TorLinkToggle.IsChecked == true) await ActivateTorLinkAsync(); else await DeactivateTorLinkAsync();
    }

    /// <summary>
    /// TorLink on: an installed TorLink (Mira's, or one found through its shortcut or npm's global folder, confirmed before
    /// its first start) is used as it is; otherwise Mira installs its own Node.js and TorLink, then opens it. A failure
    /// leaves it off, with the reason on the card.
    /// </summary>
    private async Task ActivateTorLinkAsync(bool reinstall = false)
    {
        _torlinkInstallation = TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath);
        if (reinstall || _torlinkInstallation is not { HasRuntime: true })
        {
            _torlinkInstalling = true; ShowDownloaders(true);
            try
            {
                _torlinkStep = null;
                _torlinkInstallation = await TorLinkSetup.InstallAsync(_profile.DirectoryPath, new Progress<TorLinkSetup.Step>(step => { _torlinkStep = step; DescribeTorLinkSetup(step.Text, step.Progress); }), _torlinkLifetime.Token);
                // Mira's own TorLink from now on, not a folder chosen earlier that no longer works or is too old for its screen.
                if (TorLinkInstallation.Validate(_settings.TorLinkPath, _profile.DirectoryPath) is not { Outdated: false }) _settings.TorLinkPath = "";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is InstallException || IsExpected(ex))
            {
                _torlinkInstalling = false; ShowDownloaders(true);
                DescribeTorLinkSetup(ex is InstallException ? ex.Message : Friendly(ex));
                TorLinkToggle.IsChecked = false;
                return;
            }
            finally { _torlinkInstalling = false; }
        }
        _settings.TorLinkActive = true; _profile.SaveSettings(_settings);
        await OpenTorLinkViewAsync(restart: true);
    }

    /// <summary>TorLink off: it quits (its downloads resume when it is turned on again) and stays installed.</summary>
    private async Task DeactivateTorLinkAsync()
    {
        _settings.TorLinkActive = false; _profile.SaveSettings(_settings);
        _torlinkStartWatch.Stop(); SetTorLinkBlocker(null);
        if (_torlinkTerminal is { } terminal) await terminal.StopAsync();
        await TakeBackFromHeadlessAsync();
        ShowDownloaders(true); UpdateTorLinkState();
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
        _torlinkInstallation = TorLinkInstallation.Locate(_settings.TorLinkPath, _profile.DirectoryPath);
        if (_torlinkInstallation is null) { SetTorLinkBlocker("missing"); return; }
        if (!_torlinkInstallation.HasRuntime) { SetTorLinkBlocker("runtime"); return; }
        _torlinkTerminal ??= CreateTorLinkTerminal();
        await _torlinkTerminal.InitializeAsync(TorLinkTerminalHost);
        if (_torlinkTerminal.State == TerminalState.Unavailable) { SetTorLinkBlocker("webview"); return; }
        if (_torlinkTerminal.Running) { SetTorLinkBlocker(null); if (TorLinkOverlay.IsVisible) _torlinkTerminal.Focus(); return; }
        if (_torlinkTerminal.State is TerminalState.Exited or TerminalState.Starting && !restart) { SetTorLinkBlocker(null); return; }
        // Two TorLink would share the same queue and write the same files: the one without a window hands it back first.
        var installation = _torlinkInstallation;
        await TakeBackFromHeadlessAsync();
        if (await Task.Run(() => installation.RunningOutside(_torlinkHeadless.ProcessIds)) is not null) { SetTorLinkBlocker("elsewhere"); return; }
        // Found by searching this PC, neither installed by Mira nor chosen: it runs only once the person says it is theirs.
        if (installation.Detected) { SetTorLinkBlocker("confirm"); return; }
        installation = await TakePreparedTorLinkAsync(installation);
        SetTorLinkBlocker(null);
        PrepareTorLinkStart();
        _torlinkTerminal.Start(installation);
        _ = RefreshTorLinkAsync(installation);
    }

    /// <summary>
    /// Mira's own TorLink, closed at this point: the Node.js and TorLink prepared since its last opening take their place
    /// now. Anything still in use keeps the current version, which starts as it is.
    /// </summary>
    private async Task<TorLinkInstallation> TakePreparedTorLinkAsync(TorLinkInstallation installation)
    {
        var profile = _profile.DirectoryPath;
        if (!installation.Managed || !TorLinkSetup.IsPrepared(profile)) return installation;
        var updated = await Task.Run(() => { TorLinkSetup.Activate(profile); return TorLinkInstallation.Validate(TorLinkPackage.PackageRoot(profile), profile); });
        if (updated is not { HasRuntime: true }) return installation;
        DescribeTorLinkSetup();
        return _torlinkInstallation = updated;
    }

    /// <summary>
    /// Mira's own TorLink older than what this Mira pins (Node.js, TorLink, or installed by an older Mira without the lock):
    /// the new versions are prepared beside it while it runs, once per start of Mira, and take its place at its next opening.
    /// A failure changes nothing and says nothing: the next start of Mira tries again.
    /// </summary>
    private async Task RefreshTorLinkAsync(TorLinkInstallation installation)
    {
        var profile = _profile.DirectoryPath;
        if (_torlinkRefreshTried || !installation.Managed || await Task.Run(() => TorLinkSetup.IsCurrent(profile))) return;
        _torlinkRefreshTried = true;
        try { await TorLinkSetup.PrepareAsync(profile, null, _torlinkLifetime.Token); DescribeTorLinkSetup(); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Just before Mira starts TorLink, closed at this point: the downloads moved into the library stop being shared
    /// (started again, TorLink would fetch pieces of them back), and a TorLink never set up downloads on the library's
    /// drive, where each move is a rename.
    /// </summary>
    private void PrepareTorLinkStart()
    {
        if (_torlink?.PrepareStart() is { } folder)
            SetNotice(L($"TorLink télécharge dans « {folder} », sur le disque de ta bibliothèque : chaque fichier terminé y est déplacé sans copie.", $"TorLink downloads to “{folder}”, on your library’s drive: each finished file is moved there without a copy."));
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
        if (_serverScreen) { DescribeServerAgent(DateTimeOffset.UtcNow); return; }
        var state = _torlinkTerminal?.State ?? TerminalState.Idle;
        if (_torlinkBlocker is not null || state is TerminalState.Running or TerminalState.Exited or TerminalState.Unavailable) _torlinkStartWatch.Stop();
        var (status, dot) = _torlinkInstalling ? (L("Installation…", "Installing…"), "Warning") : !TorLinkActive() ? (L("Désactivé", "Off"), "Dim")
            : _torlinkHeadless.Running && state != TerminalState.Running ? (L("Sans fenêtre", "In the background"), "Success")
            : _torlinkBlocker == "confirm" ? (L("À confirmer", "Needs confirming"), "Warning") : _torlinkBlocker is not null ? (L("Indisponible", "Unavailable"), "Warning") : state switch
            {
                TerminalState.Running => (L("En cours", "Running"), "Success"),
                TerminalState.Starting => (L("Démarrage…", "Starting…"), "Warning"),
                _ => (L("Fermé", "Closed"), "Dim")
            };
        TorLinkStatusText.Text = "TorLink · " + status; TorLinkStatusDot.Fill = Palette.Get(dot);
        switch (_torlinkBlocker)
        {
            case "missing" when _torlinkNpx.Value:
                ShowTorLinkPanel("folder", L("TorLink est introuvable", "Can’t find TorLink"), TorLinkNpxHint, L("Installer celui de Mira", "Install Mira’s"), "install", L("Choisir son dossier…", "Choose its folder…"), "browse"); return;
            case "missing":
                ShowTorLinkPanel("folder", L("TorLink est introuvable", "Can’t find TorLink"), L("Mira peut le réinstaller.", "Mira can reinstall it."), L("Réinstaller", "Reinstall"), "install", L("Choisir son dossier…", "Choose its folder…"), "browse"); return;
            case "runtime":
                ShowTorLinkPanel("info", L("Node.js est nécessaire", "Node.js is needed"), L("TorLink a besoin de Node.js 22 ou plus récent. Mira peut l’installer, avec TorLink.", "TorLink needs Node.js 22 or later. Mira can install it, along with TorLink."), L("Installer", "Install"), "install"); return;
            case "elsewhere":
                ShowTorLinkPanel("info", L("TorLink est déjà ouvert", "TorLink is already open"), L("Il tourne dans une autre fenêtre. Quitte-le (q ou Ctrl+C) puis réessaie : deux TorLink ne peuvent pas gérer les mêmes téléchargements.", "It’s running in another window. Quit it (q or Ctrl+C), then try again: two TorLinks can’t manage the same downloads."), L("Réessayer", "Try again"), "retry"); return;
            case "confirm" when _torlinkInstallation is { } found:
                ShowTorLinkPanel("folder", L("Utiliser ce TorLink ?", "Use this TorLink?"), L($"Mira a trouvé TorLink {found.Version ?? "(version inconnue)"} dans {found.Root}. Il n’a pas été installé par Mira : utilise-le seulement si c’est toi qui l’as installé. Mira s’en souviendra.",
                    $"Mira found TorLink {found.Version ?? "(unknown version)"} in {found.Root}. It wasn’t installed by Mira: only use it if you installed it yourself. Mira will remember."),
                    L("Utiliser ce TorLink", "Use this TorLink"), "trust", L("Installer celui de Mira", "Install Mira’s"), "install"); return;
            // « torlnk search » and « torlnk serve » came later: such a TorLink prints its usage and quits.
            case "outdated" when _torlinkInstallation is { } old:
                ShowTorLinkPanel("download", L("Ce TorLink est trop ancien", "This TorLink is too old"),
                    L($"TorLink {old.Version ?? "(version inconnue)"} ({old.Root}) ne sait ni chercher ni télécharger pour l’écran de Mira et pour Mira web : il faut TorLink {TorLinkRemote.MinimumVersion} ou plus récent. "
                      + (old.Managed ? "Mira peut le mettre à jour" : "Mira peut installer le sien") + " ; ta file de téléchargements et tes réglages de TorLink sont repris.",
                      $"TorLink {old.Version ?? "(unknown version)"} ({old.Root}) can’t search or download for Mira’s screen and Mira web: TorLink {TorLinkRemote.MinimumVersion} or later is needed. "
                      + (old.Managed ? "Mira can update it" : "Mira can install its own") + "; your download queue and TorLink settings carry over."),
                    old.Managed ? L("Mettre à jour", "Update") : L("Installer celui de Mira", "Install Mira’s"), "install", L("Ouvrir son terminal", "Open its terminal"), "terminal"); return;
            case "remote":
                ShowTorLinkPanel("download", L("TorLink télécharge pour un autre appareil", "TorLink is downloading for another device"), L("Une demande est arrivée de Mira web : TorLink continue sans fenêtre. Le rouvrir ici reprend tout, sans rien perdre.", "A request came from Mira web: TorLink carries on without a window. Opening it here again picks everything up, with nothing lost."), L("Rouvrir TorLink ici", "Reopen TorLink here"), "retry"); return;
            case "webview":
                ShowTorLinkPanel("info", L("Terminal intégré indisponible", "Built-in terminal unavailable"), (_torlinkTerminal?.Problem ?? "") + L(" TorLink peut s’ouvrir dans sa propre fenêtre ; ses téléchargements terminés seront quand même rangés.", " TorLink can open in its own window; its finished downloads will still be filed."), L("Ouvrir dans une fenêtre", "Open in a window"), "window"); return;
        }
        switch (state)
        {
            case TerminalState.Running: TorLinkStatePanel.Visibility = Visibility.Collapsed; break;
            case TerminalState.Starting: ShowTorLinkPanel("download", L("Ouverture de TorLink…", "Opening TorLink…"), L("Son interface apparaît ici dans un instant.", "Its interface appears here in a moment."), null, null); break;
            case TerminalState.Exited when _torlinkTerminal?.Problem is { } problem:
                ShowTorLinkPanel("info", _torlinkTerminal.ExitCode is null ? L("TorLink n’a pas démarré", "TorLink didn’t start") : L("TorLink s’est arrêté", "TorLink stopped"), problem, L("Réessayer", "Try again"), "retry"); break;
            case TerminalState.Exited: ShowTorLinkPanel("download", L("TorLink est fermé", "TorLink is closed"), L("Les téléchargements interrompus reprendront à la prochaine ouverture.", "Interrupted downloads will resume the next time it opens."), L("Relancer TorLink", "Start TorLink again"), "retry"); break;
            default: ShowTorLinkPanel("download", L("Ouverture de TorLink…", "Opening TorLink…"), L("Son interface apparaît ici dans un instant.", "Its interface appears here in a moment."), null, null); break;
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
            case "install": await ActivateTorLinkAsync(reinstall: true); break;
            // A TorLink too old for Mira's screen still has its own terminal, remembered as the chosen face.
            case "terminal":
                _settings.TorLinkTerminal = true; _profile.SaveSettings(_settings);
                await OpenTorLinkViewAsync(restart: true);
                break;
            case "reload":
                _torlinkTerminal?.Reload();
                UpdateTorLinkState();
                await StartTorLinkAsync(restart: true);
                break;
            case "browse":
                if (ChooseTorLinkFolder() is { } root) { _settings.TorLinkPath = root; _profile.SaveSettings(_settings); await OpenTorLinkViewAsync(restart: true); }
                break;
            // Confirmed: this folder is the chosen one from now on, asked about no more.
            case "trust" when _torlinkInstallation is { Detected: true } found:
                _settings.TorLinkPath = found.Root; _profile.SaveSettings(_settings);
                await OpenTorLinkViewAsync(restart: true);
                break;
            case "window" when _torlinkInstallation is { Detected: true }:
                SetTorLinkBlocker("confirm");
                break;
            case "window" when _torlinkInstallation is { } installation:
                // Its own window is a TorLink like the one of its shortcut: still only one at a time.
                await TakeBackFromHeadlessAsync();
                if (await Task.Run(() => installation.RunningOutside(_torlinkHeadless.ProcessIds)) is not null) { SetNotice(L("TorLink est déjà ouvert dans une autre fenêtre.", "TorLink is already open in another window.")); break; }
                installation = await TakePreparedTorLinkAsync(installation);
                PrepareTorLinkStart();
                try { installation.OpenInWindow(); SetNotice(L("TorLink s’ouvre dans sa propre fenêtre ; ses téléchargements terminés seront rangés.", "TorLink is opening in its own window; its finished downloads will be filed.")); _ = RefreshTorLinkAsync(installation); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { SetNotice(L("TorLink n’a pas pu s’ouvrir dans une fenêtre.", "TorLink couldn’t open in a window.")); }
                break;
        }
    }

    /// <summary>Folder picker that only accepts a real TorLink folder.</summary>
    private string? ChooseTorLinkFolder()
    {
        var dialog = new OpenFolderDialog { Title = L("Dossier de TorLink (celui qui contient torlink.bat)", "TorLink’s folder (the one with torlink.bat)") };
        if (dialog.ShowDialog(this) != true) return null;
        if (TorLinkInstallation.Validate(dialog.FolderName, _profile.DirectoryPath) is { } found) return found.Root;
        SetNotice(L("Ce dossier ne contient pas TorLink (package torlnk avec dist\\cli.cjs).", "This folder doesn’t contain TorLink (the torlnk package with dist\\cli.cjs)."));
        return null;
    }

    private void TorLinkFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _torlinkState.DownloadDirectory();
        if (Directory.Exists(folder)) OpenInExplorer(folder, null);
        else SetNotice(L("Le dossier de téléchargement de TorLink n’existe pas encore : il sera créé au premier téléchargement.", "TorLink’s download folder doesn’t exist yet: it will be created with the first download."));
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
        _torlinkLifetime.Cancel(); _torlinkPoll.Stop(); _torlinkDebounce.Stop(); _torlinkStartWatch.Stop(); _remoteTimer.Stop();
        _torlink?.Dispose();
        await _torlinkHeadless.StopAsync(); _torlinkHeadless.Dispose();
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
        if (_torlink is not { } torlink || TorLinkImports is null) return;
        TorLinkImportSummary.Text = torlink.Summary(_settings.TorLinkAutoImport);
        TorLinkImports.Children.Clear();
        foreach (var entry in torlink.Importer.Entries.Take(40)) TorLinkImports.Children.Add(ImportRow(entry));
        foreach (var earlier in torlink.Earlier) TorLinkImports.Children.Add(EarlierRow(earlier));
        TorLinkImportsEmpty.Visibility = TorLinkImports.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_serverScreen && _serverLocal) RenderLocalPlaced(torlink);
    }

    private FrameworkElement ImportRow(TorLinkImportEntry entry)
    {
        var (icon, color) = entry.State switch
        {
            TorLinkImportState.Imported when entry.JellyfinId is not null => ("check", "Success"),
            TorLinkImportState.Imported or TorLinkImportState.Partial => (entry.Kind == MediaKind.Movie ? "film" : "series", "TextBody"),
            TorLinkImportState.Waiting or TorLinkImportState.Importing => ("clock", "Warning"),
            TorLinkImportState.Blocked => ("sliders", "Warning"),
            TorLinkImportState.Ignored => ("info", "Faint"),
            _ => ("info", "Danger")
        };
        var actions = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        if (entry.JellyfinId is { } id && !_demo) actions.Children.Add(RowButton(L("Voir", "View"), "play", async () => await OpenTorLinkItemAsync(id)));
        // The folder is looked up on click only: a library on an offline network drive must not freeze this list.
        if (entry.Folders.Count > 0)
            actions.Children.Add(RowButton(L("Dossier", "Folder"), "folder", async () =>
            {
                var folders = entry.Folders; var video = entry.Files.FirstOrDefault(x => x.Role == "video")?.Destination;
                if (await Task.Run(() => folders.FirstOrDefault(Directory.Exists)) is { } folder) OpenInExplorer(folder, video);
                else SetNotice(L("Ce dossier n’existe plus dans la bibliothèque.", "This folder is no longer in the library."));
            }));
        if (entry.State is TorLinkImportState.Waiting or TorLinkImportState.Blocked or TorLinkImportState.Conflict or TorLinkImportState.Failed or TorLinkImportState.Partial)
            actions.Children.Add(RowButton(L("Réessayer", "Try again"), "refresh", () => { RequestTorLinkImport(entry.Id); return Task.CompletedTask; }));
        ContextMenu? menu = null;
        // Only what Mira placed can be moved to another library; files found already there stay put.
        if (entry.State is TorLinkImportState.Imported or TorLinkImportState.Partial && entry.Files.Any(x => !x.Found))
        {
            menu = new ContextMenu();
            foreach (var (kind, label, glyph) in new[] { (MediaKind.Movie, L("Classer comme film", "File as a movie"), "film"), (MediaKind.Series, L("Classer comme série", "File as a show"), "series"), (MediaKind.Anime, L("Classer comme anime", "File as anime"), "series") })
                if (kind != entry.Kind) menu.Items.Add(MenuAction(label, glyph, () => ReclassifyTorLinkAsync(entry.Id, kind)));
            var classify = RowButton(L("Classer", "File as"), "sliders", () => Task.CompletedTask);
            classify.Click += (_, _) => { menu.PlacementTarget = classify; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            actions.Children.Add(classify);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = TorLinkPlacement.TitleOf(entry), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Name });
        text.Children.Add(new TextBlock { Text = TorLinkPlacement.DetailOf(entry), FontSize = 11.5, LineHeight = 17, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        if (actions.Children.Count > 0) text.Children.Add(actions);
        return Row(icon, color, text, menu, TorLinkPlacement.TitleOf(entry) + ", " + TorLinkPlacement.StatusOf(entry));
    }

    private FrameworkElement EarlierRow(TorLinkCompletion completion)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = completion.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = completion.Name });
        var when = completion.CompletedAt == DateTimeOffset.MinValue ? "" : L($" le {completion.CompletedAt.ToLocalTime():d} à {completion.CompletedAt.ToLocalTime():HH:mm}", $" on {Date(completion.CompletedAt.ToLocalTime())} at {Time(completion.CompletedAt.ToLocalTime())}");
        text.Children.Add(new TextBlock { Text = L($"Terminé{when} · pas encore rangé", $"Finished{when} · not filed yet"), FontSize = 11.5, Foreground = Palette.Get("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var actions = new WrapPanel { Margin = new Thickness(-6, 6, 0, 0) };
        actions.Children.Add(RowButton(L("Importer", "Import"), "download", () => { RequestTorLinkImport(completion.Id); return Task.CompletedTask; }));
        text.Children.Add(actions);
        return Row("clock", "Faint", text, null, completion.Name + L(", pas encore rangé", ", not filed yet"));
    }

    private static Border Row(string icon, string color, FrameworkElement content, ContextMenu? menu, string name)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Icon { Kind = icon, Width = 18, Height = 18, Foreground = Palette.Get(color), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        var row = new Border { Background = Palette.Get("Surface"), CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 12, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = grid, ContextMenu = menu };
        AutomationProperties.SetName(row, name);
        return row;
    }

    private Button RowButton(string text, string icon, Func<Task> action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new Icon { Kind = icon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold });
        var button = new Button { Style = (Style)FindResource("Quiet"), Padding = new Thickness(8, 6, 9, 6), Margin = new Thickness(0, 0, 2, 0), Content = content, Foreground = Palette.Get("TextBody") };
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
}
