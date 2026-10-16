using System.IO;
using System.Windows;
using Mira.Core;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>The mpv engine: one already on this PC, or the build Mira installs into its profile on request.</summary>
public partial class MainWindow
{
    private bool _engineInstalling;
    /// <summary>Réglages → Lecture: where the engine comes from, or the button that installs it.</summary>
    private void ShowEngineState()
    {
        var found = MpvEngine.FindLibrary(_settings.MpvPath);
        if (!_engineInstalling)
            EngineStatus.Text = found is null ? L("Aucun moteur mpv sur ce PC. Mira peut l’installer pour toi (31 Mo, depuis les builds Windows de mpv), ou tu peux choisir une bibliothèque mpv 64 bits ci-dessous.", "No mpv engine on this PC. Mira can install it for you (31 MB, from mpv’s Windows builds), or you can choose a 64-bit mpv library below.")
                : string.Equals(found, MpvInstaller.LibraryPath(AppFiles.ProfileDirectory), StringComparison.OrdinalIgnoreCase)
                    ? L("Prêt pour la lecture · moteur installé par Mira.", "Ready to play · engine installed by Mira.") + (MpvInstaller.IsPrepared(AppFiles.ProfileDirectory) ? L(" Sa mise à jour est prête : elle sera utilisée au prochain démarrage de Mira.", " Its update is ready: it’ll be used the next time Mira starts.") : "")
                : L("Prêt pour la lecture · moteur détecté sur ce PC.", "Ready to play · engine found on this PC.");
        EngineCheck.Visibility = found is null ? Visibility.Collapsed : Visibility.Visible;
        InstallEngineButton.Visibility = found is null || _engineInstalling ? Visibility.Visible : Visibility.Collapsed;
        InstallEngineButton.IsEnabled = !_engineInstalling;
    }
    private async void InstallEngine_Click(object sender, RoutedEventArgs e) => await InstallEngineAsync();
    /// <summary>Downloads, checks and installs the engine; true once playback can start. <paramref name="inNotice"/>:
    /// the progress also shows in the notice, for an install started from the library rather than from the settings.</summary>
    private async Task<bool> InstallEngineAsync(bool inNotice = false)
    {
        if (_engineInstalling) return false;
        _engineInstalling = true; ShowEngineState();
        var shown = "";
        void Show(string text)
        {
            if (text == shown) return;
            shown = text; EngineStatus.Text = text;
            if (inNotice) SetNotice(text);
        }
        Show(L("Téléchargement du moteur mpv…", "Downloading the mpv engine…"));
        var progress = new Progress<double>(p => Show(p < .8 ? L($"Téléchargement du moteur mpv… {Math.Floor(p / .8 * 100):0} %", $"Downloading the mpv engine… {Math.Floor(p / .8 * 100):0}%") : L("Vérification et installation du moteur…", "Checking and installing the engine…")));
        try
        {
            var path = await MpvInstaller.InstallAsync(AppFiles.ProfileDirectory, progress: progress);
            _engineInstalling = false;
            MpvPathBox.Text = path; ShowEngineState();
            SetNotice(L("Le moteur vidéo mpv est installé : la lecture est prête.", "The mpv video engine is installed: playback is ready."));
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            var message = ex is InstallException ? ex.Message : L("Le moteur vidéo n’a pas pu être installé. Réessaie dans un instant.", "The video engine couldn’t be installed. Try again in a moment.");
            _engineInstalling = false; ShowEngineState(); EngineStatus.Text = message; SetNotice(message);
            return false;
        }
        finally { _engineInstalling = false; }
    }
    /// <summary>
    /// Mira's own engine older than the build this version pins: the new one is downloaded and checked beside it
    /// ("data\mpv-next"), and replaces it at the next start, before anything loads it (App). An engine chosen in the
    /// settings is left alone. A failed download changes nothing and says nothing: the next start tries again.
    /// </summary>
    private async Task RefreshEngineAsync()
    {
        var profile = AppFiles.ProfileDirectory;
        if (!string.Equals(MpvEngine.FindLibrary(_settings.MpvPath), MpvInstaller.LibraryPath(profile), StringComparison.OrdinalIgnoreCase) || !MpvInstaller.IsOutdated(profile)) return;
        try
        {
            // After the start's own requests: the library and its images come first.
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (await MpvInstaller.PrepareUpdateAsync(profile) && SettingsOverlay.Visibility == Visibility.Visible) ShowEngineState();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { }
    }
    /// <summary>"Installer" on the notice shown when playback had no engine: the title starts once it is ready.</summary>
    private async Task InstallEngineThenPlayAsync(MediaItem item, bool keepMini, bool fromStart)
    {
        if (await InstallEngineAsync(inNotice: true) && !_playing) await PlayAsync(item, keepMini, fromStart: fromStart);
    }
}
