using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>Automatic updates: Réglages → Mises à jour, the "ready" notice, and the installation when Mira closes.</summary>
public partial class MainWindow
{
    private Updater? _updater;
    private bool _restartForUpdate, _sessionEnding, _updateNoticePending, _restartChecking;
    private Version? _announcedUpdate;
    private UpdateResult? _updateResult;

    private void InitializeUpdates()
    {
        // Read before anything else: the outcome of an installation started when Mira last closed.
        _updateResult = Updater.TakeResult(_profile.DirectoryPath);
        _updater = Updater.Create(_args, _profile.DirectoryPath, App.ShellValidation);
        // Windows is shutting down: an installer started now would be cut off halfway.
        Application.Current.SessionEnding += (_, _) => _sessionEnding = true;
        if (_updater is { } updater)
        {
            updater.Changed += () => Dispatcher.BeginInvoke(ShowUpdateState);
            _ = updater.RunAsync(() => _settings.AutoUpdate);
        }
        ShowUpdateState();
        // The portable's libraries of earlier builds, extracted by .NET and never removed.
        if (!App.ShellValidation) _ = Task.Run(PortableExtraction.CleanOld);
    }

    private void ShowUpdateState()
    {
        UpdateVersionText.Text = $"Mira {JellyfinClient.AppVersion}";
        if (_updater is not { } updater)
        {
            SetUpdateStatus(L("Mises à jour désactivées pendant cette session de validation.", "Updates are off during this validation session."));
            CheckUpdatesButton.IsEnabled = false; UpdateActions.Visibility = UpdateProgressTrack.Visibility = Visibility.Collapsed; UpdateInstallText.Text = "";
            return;
        }
        var version = updater.Offer?.Version.ToString(3);
        var checkedAt = updater.LastCheck is { } time ? L($" Dernière vérification à {time:HH:mm}.", $" Last checked at {Time(time)}.") : "";
        SetUpdateStatus(updater.State switch
        {
            UpdateState.Disabled => L("Copie de développement : elle ne se met pas à jour automatiquement.", "Development copy: it doesn’t update automatically."),
            UpdateState.Checking => L("Recherche d’une nouvelle version…", "Looking for a new version…"),
            UpdateState.Downloading => L($"Téléchargement de Mira {version}… {Math.Floor(updater.Progress * 100):0} %", $"Downloading Mira {version}… {Math.Floor(updater.Progress * 100):0}%"),
            UpdateState.Ready when updater.AutomaticInstallSuspended => L($"Mira {version} n’a pas pu s’installer deux fois de suite : redémarre pour réessayer. Le détail est dans data\\updates.", $"Mira {version} failed to install twice in a row: restart to try again. The details are in data\\updates."),
            UpdateState.Ready when _settings.AutoUpdate => L($"Mira {version} est prête : elle s’installera quand tu fermeras Mira, ou tout de suite avec un redémarrage.", $"Mira {version} is ready: it’ll install when you close Mira, or right away with a restart."),
            UpdateState.Ready => L($"Mira {version} est prête : redémarre pour l’installer.", $"Mira {version} is ready: restart to install it."),
            UpdateState.UpToDate => L("Mira est à jour.", "Mira is up to date.") + checkedAt,
            UpdateState.Failed => updater.Error + checkedAt,
            _ => _settings.AutoUpdate ? L("La recherche démarre quelques secondes après l’ouverture de Mira.", "The check starts a few seconds after Mira opens.") : L("La recherche automatique est désactivée.", "Automatic checking is off.")
        });
        CheckUpdatesButton.IsEnabled = updater.State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed;
        UpdateProgressTrack.Visibility = updater.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressScale.ScaleX = Math.Clamp(updater.Progress, 0, 1);
        RestartUpdateButton.Visibility = updater.State == UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
        RestartUpdateButton.IsEnabled = !_restartChecking;
        UpdateNotesButton.Visibility = ReleasePage(updater.Offer) is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions.Visibility = RestartUpdateButton.Visibility == Visibility.Visible || UpdateNotesButton.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallText.Text = updater.Kind switch
        {
            InstallKind.Installer => L($"Installé avec l’installateur, dans {updater.AppPath}.", $"Installed with the installer, in {updater.AppPath}."),
            InstallKind.Portable => L($"Exécutable portable : {updater.AppPath}.", $"Portable executable: {updater.AppPath}."),
            InstallKind.Folder => L($"Dossier de l’application : {updater.AppPath}.", $"App folder: {updater.AppPath}."),
            _ => L($"Copie de développement : {updater.AppPath}.", $"Development copy: {updater.AppPath}.")
        } + (updater.State == UpdateState.Disabled ? "" : L(" Chaque version téléchargée attend son installation dans data\\updates, puis y est effacée.", " Each downloaded version waits in data\\updates to be installed, then is erased from there."));
        if (updater.State == UpdateState.Ready && updater.Offer is { } offer && _announcedUpdate != offer.Version && !updater.AutomaticInstallSuspended) { _announcedUpdate = offer.Version; AnnounceUpdate(offer); }
    }
    /// <summary>Screen readers hear the new state (the text is a polite live region).</summary>
    private void SetUpdateStatus(string text)
    {
        if (UpdateStatusText.Text == text) return;
        UpdateStatusText.Text = text;
        UIElementAutomationPeer.CreatePeerForElement(UpdateStatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private void AnnounceUpdate(UpdateOffer offer)
    {
        // Over the native video a notice would stay hidden: it waits until the full player is left.
        if (_playing && !_miniPlayer) { _updateNoticePending = true; return; }
        _updateNoticePending = false;
        SetNotice(_settings.AutoUpdate ? L($"Mira {offer.Version.ToString(3)} est prête : elle s’installera quand tu fermeras Mira.", $"Mira {offer.Version.ToString(3)} is ready: it’ll install when you close Mira.") : L($"Mira {offer.Version.ToString(3)} est prête.", $"Mira {offer.Version.ToString(3)} is ready."), L("Redémarrer", "Restart"), RestartForUpdate);
    }
    private void ShowPendingUpdateNotice() { if (_updateNoticePending && _updater?.Offer is { } offer) AnnounceUpdate(offer); }
    /// <summary>After an installation Mira says whether the new version is the one running: from what the updater recorded
    /// before closing, or from --updated-from for a validation profile. A success shown by the "what's new" screen needs no notice.</summary>
    private void AnnounceUpdateResult(bool whatsNewShown)
    {
        if (_updateResult is { } result)
        {
            if (!result.Installed) SetNotice(L($"La mise à jour vers Mira {result.Version.ToString(3)} n’a pas pu s’installer : Mira reste en version {JellyfinClient.AppVersion}. Le détail est dans data\\updates.", $"The update to Mira {result.Version.ToString(3)} couldn’t be installed: Mira stays at version {JellyfinClient.AppVersion}. The details are in data\\updates."), null, null, sticky: true);
            else if (!whatsNewShown) SetNotice(L($"Mira est passée en version {JellyfinClient.AppVersion}.", $"Mira is now at version {JellyfinClient.AppVersion}."));
            return;
        }
        var index = Array.IndexOf(_args, "--updated-from");
        if (index < 0 || index + 1 >= _args.Length) return;
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion); var previous = ReleaseFeed.ParseVersion(_args[index + 1]);
        if (current is not null && previous is not null && current > previous) { if (!whatsNewShown) SetNotice(L($"Mira est passée en version {current.ToString(3)}.", $"Mira is now at version {current.ToString(3)}.")); }
        else SetNotice(L($"La mise à jour n’a pas pu s’installer : Mira reste en version {JellyfinClient.AppVersion}. Le détail est dans data\\updates.", $"The update couldn’t be installed: Mira stays at version {JellyfinClient.AppVersion}. The details are in data\\updates."), null, null, sticky: true);
    }
    private async void RestartForUpdate()
    {
        if (_updater is not { State: UpdateState.Ready } updater || _restartChecking) return;
        // Checked before closing, off the interface thread: a download that went missing leaves Mira open.
        _restartChecking = true; ShowUpdateState();
        var intact = await updater.VerifyAsync();
        _restartChecking = false; ShowUpdateState();
        if (!intact) { SetNotice(L("La mise à jour téléchargée n’est plus valable : elle sera téléchargée de nouveau.", "The downloaded update is no longer valid: it’ll be downloaded again.")); _ = updater.CheckAsync(); return; }
        _restartForUpdate = true; Close();
    }
    /// <summary>Called last when Mira closes, once playback, TorLink and the synchronisation are stopped.</summary>
    private async Task ApplyUpdateAtCloseAsync()
    {
        // Restarted for a new language (RestartMira): a ready update is installed on the way only when Mira installs them by itself.
        var restart = _restartForUpdate || _restartAtClose;
        var started = _updater is { } updater && !_sessionEnding && updater.State == UpdateState.Ready && (_settings.AutoUpdate || _restartForUpdate) && await updater.ApplyAsync(restart);
        // A restart was asked for and nothing will reopen Mira: start it again once this process has ended.
        if (restart && !started)
        {
            var again = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            again.ArgumentList.Add("--after"); again.ArgumentList.Add(Environment.ProcessId.ToString());
            var data = Array.IndexOf(_args, "--data");
            if (data >= 0 && data + 1 < _args.Length) { again.ArgumentList.Add("--data"); again.ArgumentList.Add(_args[data + 1]); }
            try { using var _ = Process.Start(again); } catch (System.ComponentModel.Win32Exception) { }
        }
        _updater?.Dispose();
    }
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) { _ = _updater?.CheckAsync(); ShowUpdateState(); }
    private void RestartToUpdate_Click(object sender, RoutedEventArgs e) => RestartForUpdate();
    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ReleasePage(_updater?.Offer) is not { } page) return;
        try { using var _ = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { SetNotice(L("Le navigateur n’a pas pu s’ouvrir.", "The browser couldn’t open.")); }
    }
    /// <summary>Only Mira's own release pages are opened in the browser.</summary>
    private static Uri? ReleasePage(UpdateOffer? offer) => ReleaseFeed.ReleasePage(offer?.Page);
}
