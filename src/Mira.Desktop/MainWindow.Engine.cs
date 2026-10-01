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
            EngineStatus.Text = found is null ? "Aucun moteur mpv sur ce PC. Mira peut l’installer pour toi (31 Mo, depuis les builds Windows de mpv), ou tu peux choisir une bibliothèque mpv 64 bits ci-dessous."
                : string.Equals(found, MpvInstaller.LibraryPath(AppFiles.ProfileDirectory), StringComparison.OrdinalIgnoreCase) ? "Prêt pour la lecture · moteur installé par Mira."
                : "Prêt pour la lecture · moteur détecté sur ce PC.";
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
        Show("Téléchargement du moteur mpv…");
        var progress = new Progress<double>(p => Show(p < .8 ? $"Téléchargement du moteur mpv… {Math.Floor(p / .8 * 100):0} %" : "Vérification et installation du moteur…"));
        try
        {
            var path = await MpvInstaller.InstallAsync(AppFiles.ProfileDirectory, progress: progress);
            _engineInstalling = false;
            MpvPathBox.Text = path; ShowEngineState();
            SetNotice("Le moteur vidéo mpv est installé : la lecture est prête.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            var message = ex is InstallException ? ex.Message : "Le moteur vidéo n’a pas pu être installé. Réessaie dans un instant.";
            _engineInstalling = false; ShowEngineState(); EngineStatus.Text = message; SetNotice(message);
            return false;
        }
        finally { _engineInstalling = false; }
    }
    /// <summary>"Installer" on the notice shown when playback had no engine: the title starts once it is ready.</summary>
    private async Task InstallEngineThenPlayAsync(MediaItem item, bool keepMini)
    {
        if (await InstallEngineAsync(inNotice: true) && !_playing) await PlayAsync(item, keepMini);
    }
}
