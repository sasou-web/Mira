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
    }

    private void ShowUpdateState()
    {
        UpdateVersionText.Text = $"Mira {JellyfinClient.AppVersion}";
        if (_updater is not { } updater)
        {
            SetUpdateStatus("Mises à jour désactivées pendant cette session de validation.");
            CheckUpdatesButton.IsEnabled = false; UpdateActions.Visibility = UpdateProgressTrack.Visibility = Visibility.Collapsed; UpdateInstallText.Text = "";
            return;
        }
        var version = updater.Offer?.Version.ToString(3);
        var checkedAt = updater.LastCheck is { } time ? $" Dernière vérification à {time:HH:mm}." : "";
        SetUpdateStatus(updater.State switch
        {
            UpdateState.Disabled => "Copie de développement : elle ne se met pas à jour automatiquement.",
            UpdateState.Checking => "Recherche d’une nouvelle version…",
            UpdateState.Downloading => $"Téléchargement de Mira {version}… {Math.Floor(updater.Progress * 100):0} %",
            UpdateState.Ready when updater.AutomaticInstallSuspended => $"Mira {version} n’a pas pu s’installer deux fois de suite : redémarre pour réessayer. Le détail est dans data\\updates.",
            UpdateState.Ready when _settings.AutoUpdate => $"Mira {version} est prête : elle s’installera quand tu fermeras Mira, ou tout de suite avec un redémarrage.",
            UpdateState.Ready => $"Mira {version} est prête : redémarre pour l’installer.",
            UpdateState.UpToDate => "Mira est à jour." + checkedAt,
            UpdateState.Failed => updater.Error + checkedAt,
            _ => _settings.AutoUpdate ? "La recherche démarre quelques secondes après l’ouverture de Mira." : "La recherche automatique est désactivée."
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
            InstallKind.Installer => $"Installé avec l’installateur, dans {updater.AppPath}.",
            InstallKind.Portable => $"Exécutable portable : {updater.AppPath}.",
            InstallKind.Folder => $"Dossier de l’application : {updater.AppPath}.",
            _ => $"Copie de développement : {updater.AppPath}."
        } + (updater.State == UpdateState.Disabled ? "" : " Chaque version téléchargée attend son installation dans data\\updates, puis y est effacée.");
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
        SetNotice(_settings.AutoUpdate ? $"Mira {offer.Version.ToString(3)} est prête : elle s’installera quand tu fermeras Mira." : $"Mira {offer.Version.ToString(3)} est prête.", "Redémarrer", RestartForUpdate);
    }
    private void ShowPendingUpdateNotice() { if (_updateNoticePending && _updater?.Offer is { } offer) AnnounceUpdate(offer); }
    /// <summary>After an installation Mira says whether the new version is the one running: from what the updater recorded
    /// before closing, or from --updated-from for a validation profile.</summary>
    private void AnnounceUpdateResult()
    {
        if (_updateResult is { } result)
        {
            SetNotice(result.Installed
                ? $"Mira est passée en version {JellyfinClient.AppVersion}."
                : $"La mise à jour vers Mira {result.Version.ToString(3)} n’a pas pu s’installer : Mira reste en version {JellyfinClient.AppVersion}. Le détail est dans data\\updates.");
            return;
        }
        var index = Array.IndexOf(_args, "--updated-from");
        if (index < 0 || index + 1 >= _args.Length) return;
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion); var previous = ReleaseFeed.ParseVersion(_args[index + 1]);
        SetNotice(current is not null && previous is not null && current > previous
            ? $"Mira est passée en version {current.ToString(3)}."
            : $"La mise à jour n’a pas pu s’installer : Mira reste en version {JellyfinClient.AppVersion}. Le détail est dans data\\updates.");
    }
    private async void RestartForUpdate()
    {
        if (_updater is not { State: UpdateState.Ready } updater || _restartChecking) return;
        // Checked before closing, off the interface thread: a download that went missing leaves Mira open.
        _restartChecking = true; ShowUpdateState();
        var intact = await updater.VerifyAsync();
        _restartChecking = false; ShowUpdateState();
        if (!intact) { SetNotice("La mise à jour téléchargée n’est plus valable : elle sera téléchargée de nouveau."); _ = updater.CheckAsync(); return; }
        _restartForUpdate = true; Close();
    }
    /// <summary>Called last when Mira closes, once playback, TorLink and the synchronisation are stopped.</summary>
    private void ApplyUpdateAtClose()
    {
        if (_updater is not { } updater) return;
        var started = !_sessionEnding && updater.State == UpdateState.Ready && (_settings.AutoUpdate || _restartForUpdate) && updater.Apply(_restartForUpdate);
        // A restart was asked for and nothing will reopen Mira: start it again once this process has ended.
        if (_restartForUpdate && !started)
        {
            var again = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            again.ArgumentList.Add("--after"); again.ArgumentList.Add(Environment.ProcessId.ToString());
            var data = Array.IndexOf(_args, "--data");
            if (data >= 0 && data + 1 < _args.Length) { again.ArgumentList.Add("--data"); again.ArgumentList.Add(_args[data + 1]); }
            try { using var _ = Process.Start(again); } catch (System.ComponentModel.Win32Exception) { }
        }
        updater.Dispose();
    }
    private void CheckUpdates_Click(object sender, RoutedEventArgs e) { _ = _updater?.CheckAsync(); ShowUpdateState(); }
    private void RestartToUpdate_Click(object sender, RoutedEventArgs e) => RestartForUpdate();
    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ReleasePage(_updater?.Offer) is not { } page) return;
        try { using var _ = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { SetNotice("Le navigateur n’a pas pu s’ouvrir."); }
    }
    /// <summary>Only Mira's own release pages are opened in the browser.</summary>
    private static Uri? ReleasePage(UpdateOffer? offer) =>
        offer?.Page is { Scheme: "https", Host: "github.com" } page && page.AbsolutePath.StartsWith("/sasou-web/Mira/releases/", StringComparison.Ordinal) ? page : null;
}
