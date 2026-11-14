using System.IO;
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
    /// <summary>« Sortie » of ⋮ → Informations, as on Windows: the device, and the channels it gets or « to the amplifier ».</summary>
    private string AudioOutputInfo(Playback.MpvPlayer mpv)
    {
        var devices = mpv.AudioDevices(); var device = AudioOutput.Device(_shell.Settings.AudioDevice, devices);
        var name = device == AudioOutput.Default ? MacDefault : devices.First(d => d.Name == device).Label;
        return AudioOutput.Shown(name, mpv.Get("audio-out-params/format"), mpv.Get("audio-out-params/hr-channels"));
    }
    /// <summary>
    /// « Son de nuit » (<see cref="NightSound"/>) under the audio tracks, as on Windows: loud passages brought down, quiet
    /// ones raised, at once; kept in the settings for the next videos. Its line says what it does, or why it does nothing.
    /// </summary>
    private void AddNightSound(StackPanel list)
    {
        var settings = _shell.Settings; var chosen = settings.NightSound;
        list.Children.Add(MenuRule());
        list.Children.Add(Switch(NightSound.Name, NightSound.Details(chosen, settings.AudioPassthrough, refused: chosen && _mpv?.NightSoundRefused == true), chosen, () => { ChooseNightSound(!chosen); RefreshMenus(); }));
    }
    /// <summary>For the self-check too: chosen as the menu does.</summary>
    public void ChooseNightSound(bool on)
    {
        var settings = _shell.Settings;
        settings.NightSound = on;
        _shell.Profile.SaveSettings(settings);
        _mpv?.NightSoundChosen(on);
    }
    /// <summary>« Sortie » under the audio tracks, when there is more than one device to choose.</summary>
    private void AddAudioDevices(StackPanel list)
    {
        var devices = _mpv?.AudioDevices() ?? [];
        if (devices.Count < 2) return;
        var current = AudioOutput.Device(_shell.Settings.AudioDevice, devices);
        var heading = MenuHeading(L("Sortie", "Output")); heading.Margin = new Thickness(12, 14, 12, 8);
        list.Children.Add(heading);
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
