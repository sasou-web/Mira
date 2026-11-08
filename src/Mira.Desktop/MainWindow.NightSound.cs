using System.Windows;
using Mira.Core;

namespace Mira.Desktop;

// « Son de nuit » (NightSound) under the audio tracks of the player's Audio menu: loud passages brought down, quiet ones
// raised, at once while the video plays; kept in the settings for the next videos.
public partial class MainWindow
{
    /// <summary>The menu's line: checked as chosen, and what it does or why it does nothing now (passthrough, an mpv without it).</summary>
    private void ShowNightSound()
    {
        var chosen = _settings.NightSound;
        NightSoundCheck.IsChecked = chosen;
        NightSoundDetails.Text = NightSound.Details(chosen, _settings.AudioPassthrough, refused: chosen && _mpv?.NightSoundRefused == true);
        System.Windows.Automation.AutomationProperties.SetName(NightSoundCheck, NightSound.Name + " · " + NightSoundDetails.Text);
    }
    private void NightSound_Click(object sender, RoutedEventArgs e)
    {
        _settings.NightSound = NightSoundCheck.IsChecked == true;
        if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
        _mpv?.NightSoundChosen(_settings.NightSound);
        ShowNightSound(); ShowPlayerControls();
    }
}
