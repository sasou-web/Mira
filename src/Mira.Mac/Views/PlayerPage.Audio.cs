using Avalonia;
using Avalonia.Controls;
using Mira.Core;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Sortie audio » in the player, as on Windows (AudioOutput): « Sortie » under the audio tracks when the Mac has more
// than one device, the settings applied to the video playing, and a device chosen but unplugged said once.
public sealed partial class PlayerPage
{
    private bool _audioMissingSaid;
    private static string MacDefault => L("Par défaut du Mac", "Mac default");
    private string UnpluggedName => _shell.Settings.AudioDeviceLabel is { Length: > 0 } label ? label : L("Sortie choisie", "Chosen output");

    /// <summary>Réglages → Sortie audio changed while a video plays: device and channels at once, passthrough from the next file.</summary>
    public void AudioChanged() { _audioMissingSaid = false; _mpv?.Audio(_shell.Settings); }
    /// <summary>For the self-check: the devices the player offers under « Sortie ».</summary>
    public int AudioDevices => _mpv?.AudioDevices().Count ?? 0;

    /// <summary>A playback starts on the device chosen; unplugged, said once until it is back.</summary>
    private void SayAudioMissing()
    {
        if (_mpv is null) return;
        if (!_mpv.AudioMissing) { _audioMissingSaid = false; return; }
        if (_audioMissingSaid) return;
        _audioMissingSaid = true;
        _shell.Notice(L($"« {UnpluggedName} » n’est pas branchée : le son passe par la sortie du Mac.", $"“{UnpluggedName}” isn’t plugged in: the sound plays on the Mac’s output."));
    }
    /// <summary>« Sortie » under the audio tracks, when there is more than one device to choose.</summary>
    private void AddAudioDevices(StackPanel list)
    {
        var devices = _mpv?.AudioDevices() ?? [];
        if (devices.Count < 2) return;
        var current = AudioOutput.Device(_shell.Settings.AudioDevice, devices);
        list.Children.Add(new TextBlock { Text = L("SORTIE", "OUTPUT"), Classes = { "label" }, Margin = new Thickness(10, 14, 0, 8) });
        list.Children.Add(Entry(MacDefault, null, current == AudioOutput.Default, () => ChooseAudioDevice(AudioOutput.Default, "")));
        foreach (var (name, label) in devices) list.Children.Add(Entry(label, null, current == name, () => ChooseAudioDevice(name, label)));
    }
    /// <summary>Kept in the settings, as in Réglages: the next videos play there too, while it is plugged in.</summary>
    private void ChooseAudioDevice(string name, string label)
    {
        var settings = _shell.Settings;
        settings.AudioDevice = name; settings.AudioDeviceLabel = label;
        _shell.Profile.SaveSettings(settings);
        AudioChanged();
    }
}
