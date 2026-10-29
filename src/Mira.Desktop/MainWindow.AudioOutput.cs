using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Playback;

namespace Mira.Desktop;

/// <summary>
/// « Sortie audio » (Réglages → Audio et sous-titres, <see cref="AudioOutput"/>): the device, its channels and
/// passthrough, with « Tester »; the player's Audio menu switches the device while a video plays. A device chosen but
/// unplugged leaves the sound to Windows' default, said once until it comes back.
/// </summary>
public partial class MainWindow
{
    private MpvEngine? _soundTest;
    private DispatcherTimer? _soundTestClock;
    private bool _audioMissingSaid;
    private int _audioDevicesVersion;
    private static string WindowsDefault => L("Par défaut de Windows", "Windows default");

    /// <summary>The choices, then the devices: the playing mpv's, else those of an mpv opened for this, off the UI thread.</summary>
    private async Task ShowAudioSettingsAsync()
    {
        if (AudioChannelsChoice.Items.Count == 0) foreach (var (key, label, details) in AudioOutput.ChannelChoices) AudioChannelsChoice.Items.Add(new ComboBoxItem { Content = label, Tag = key, ToolTip = details });
        if (PassthroughChoice.Items.Count == 0) foreach (var (key, label, details) in AudioOutput.PassthroughChoices) PassthroughChoice.Items.Add(new ComboBoxItem { Content = label, Tag = key, ToolTip = details });
        SelectChoice(AudioChannelsChoice, AudioOutput.KnownChannels(_settings.AudioChannels)); SelectChoice(PassthroughChoice, AudioOutput.KnownPassthrough(_settings.AudioPassthrough));
        var version = ++_audioDevicesVersion;
        ListAudioDevices(_mpv?.AudioDevices() ?? []);
        if (_mpv is not null) return;
        var settings = new PlayerSettings { MpvPath = _settings.MpvPath };
        IReadOnlyList<(string Name, string Label)> devices;
        try { devices = await Task.Run(() => { using var probe = new MpvEngine(IntPtr.Zero, settings, headless: true); return probe.AudioDevices(); }); }
        // No mpv yet (Réglages → Lecture installs it): only Windows' default to offer.
        catch (Exception ex) when (ex is IOException or EntryPointNotFoundException or BadImageFormatException) { devices = []; }
        if (version != _audioDevicesVersion || SettingsOverlay.Visibility != Visibility.Visible) return;
        var busy = _settingsBusy; _settingsBusy = true;
        try { ListAudioDevices(devices); } finally { _settingsBusy = busy; }
    }
    private void ListAudioDevices(IReadOnlyList<(string Name, string Label)> devices)
    {
        AudioDeviceChoice.Items.Clear();
        AudioDeviceChoice.Items.Add(new ComboBoxItem { Content = WindowsDefault, Tag = AudioOutput.Default });
        foreach (var (name, label) in devices) AudioDeviceChoice.Items.Add(new ComboBoxItem { Content = label, Tag = name });
        // Unplugged now: still the choice, back as soon as it is plugged in again.
        if (_settings.AudioDevice is { Length: > 0 } chosen && chosen != AudioOutput.Default && devices.All(d => d.Name != chosen))
            AudioDeviceChoice.Items.Add(new ComboBoxItem { Content = L($"{UnpluggedName} (pas branchée)", $"{UnpluggedName} (not plugged in)"), Tag = chosen });
        SelectChoice(AudioDeviceChoice, _settings.AudioDevice is { Length: > 0 } device ? device : AudioOutput.Default);
    }
    private string UnpluggedName => _settings.AudioDeviceLabel is { Length: > 0 } label ? label : L("Sortie choisie", "Chosen output");
    private bool AudioSettingsChanged() =>
        AudioDeviceChoice.Items.Count > 0 && Choice(AudioDeviceChoice) != (_settings.AudioDevice is { Length: > 0 } device ? device : AudioOutput.Default)
        || AudioChannelsChoice.Items.Count > 0 && Choice(AudioChannelsChoice) != AudioOutput.KnownChannels(_settings.AudioChannels)
        || PassthroughChoice.Items.Count > 0 && Choice(PassthroughChoice) != AudioOutput.KnownPassthrough(_settings.AudioPassthrough);
    /// <summary>From the page to the settings, and to the video playing: device and channels at once, passthrough from the next playback.</summary>
    private void ApplyAudioSettings()
    {
        if (AudioDeviceChoice.Items.Count > 0 && Choice(AudioDeviceChoice) is { Length: > 0 } device && device != _settings.AudioDevice)
        {
            // Another device is one the list has now: its name as Windows gives it, for the day it is unplugged.
            _settings.AudioDevice = device; _audioMissingSaid = false;
            _settings.AudioDeviceLabel = device == AudioOutput.Default ? "" : (AudioDeviceChoice.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        }
        if (AudioChannelsChoice.Items.Count > 0) _settings.AudioChannels = AudioOutput.KnownChannels(Choice(AudioChannelsChoice));
        if (PassthroughChoice.Items.Count > 0) _settings.AudioPassthrough = AudioOutput.KnownPassthrough(Choice(PassthroughChoice));
        _mpv?.Audio(_settings);
    }

    /// <summary>« Tester »: a tone on each speaker in turn, on the output and channels of the page.</summary>
    private void SoundTest_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveSettings(); StopSoundTest();
        var (wav, speakers) = AudioOutput.TestSound(_settings.AudioChannels);
        var path = Path.Combine(Path.GetTempPath(), "Mira-sound-test.wav");
        try
        {
            File.WriteAllBytes(path, wav);
            var settings = new PlayerSettings { MpvPath = _settings.MpvPath, Volume = Math.Clamp(_settings.Volume, 40, 100), AudioDevice = _settings.AudioDevice, AudioChannels = _settings.AudioChannels };
            var engine = MpvEngine.Sound(settings); _soundTest = engine;
            // After the events being read: the engine goes with them.
            engine.Ended += _ => Dispatcher.BeginInvoke(StopSoundTest);
            engine.Error += _ => Dispatcher.BeginInvoke(() => { StopSoundTest(); ShowSoundTest(L("Le son de test n’a pas pu partir sur cette sortie.", "The test tone couldn’t play on this output.")); });
            engine.Load(path, 0);
            var heard = string.Join(L(", puis ", ", then "), speakers);
            ShowSoundTest((engine.AudioMissing ? L($"« {UnpluggedName} » n’est pas branchée : le test passe par la sortie de Windows. ", $"“{UnpluggedName}” isn’t plugged in: the test plays on Windows’ output. ") : "")
                + L($"Tu dois entendre un son : {heard}.", $"You should hear a tone: {heard}.")
                + (AudioOutput.Codecs(_settings.AudioPassthrough) is null ? "" : L(" L’envoi à l’ampli se vérifie sur un film en Dolby ou en DTS (lecteur → ⋮ → Informations).", " Sending to the amplifier is checked on a Dolby or DTS film (player → ⋮ → Info).")));
            SoundTestButton.IsEnabled = false;
            var until = DateTime.UtcNow + AudioOutput.TestStep * speakers.Count + TimeSpan.FromSeconds(2);
            _soundTestClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _soundTestClock.Tick += (_, _) => { _soundTest?.Poll(); if (DateTime.UtcNow > until) StopSoundTest(); };
            _soundTestClock.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or BadImageFormatException)
        {
            StopSoundTest(); ShowSoundTest(ex is FileNotFoundException ? ex.Message : L("Le son de test n’a pas pu partir : ", "The test tone couldn’t play: ") + ex.Message);
        }
    }
    private void ShowSoundTest(string text) { SoundTestStatus.Text = text; SoundTestStatus.Visibility = Visibility.Visible; }
    private void StopSoundTest()
    {
        _soundTestClock?.Stop(); _soundTestClock = null;
        var engine = _soundTest; _soundTest = null; engine?.Dispose();
        SoundTestButton.IsEnabled = true;
        try { File.Delete(Path.Combine(Path.GetTempPath(), "Mira-sound-test.wav")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A playback starts on the device chosen; unplugged, said once until it is back.</summary>
    private void SayAudioMissing()
    {
        if (_mpv is null) return;
        if (!_mpv.AudioMissing) { _audioMissingSaid = false; return; }
        if (_audioMissingSaid) return;
        _audioMissingSaid = true;
        ShowPlayerStatus(L($"« {UnpluggedName} » n’est pas branchée : le son passe par la sortie de Windows.", $"“{UnpluggedName}” isn’t plugged in: the sound plays on Windows’ output."), 6);
    }
    /// <summary>« Sortie » of the player's Audio menu, when there is more than one device to choose.</summary>
    private void BuildAudioDeviceChoices()
    {
        AudioDeviceList.Children.Clear();
        var devices = _mpv?.AudioDevices() ?? [];
        AudioDeviceSection.Visibility = devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (devices.Count < 2) return;
        var current = AudioOutput.Device(_settings.AudioDevice, devices);
        AudioDeviceList.Children.Add(MenuChoice("audio-device", WindowsDefault, "", current == AudioOutput.Default, () => ChooseAudioDevice(AudioOutput.Default, "")));
        foreach (var (name, label) in devices) AudioDeviceList.Children.Add(MenuChoice("audio-device", label, "", current == name, () => ChooseAudioDevice(name, label)));
    }
    /// <summary>Kept in the settings, as in Réglages: the next videos play there too, while it is plugged in.</summary>
    private void ChooseAudioDevice(string name, string label)
    {
        _settings.AudioDevice = name; _settings.AudioDeviceLabel = label; _audioMissingSaid = false;
        if (!TrySaveSettings()) SetNotice(SettingsNotSaved);
        _mpv?.Audio(_settings); ShowPlayerControls();
    }
    /// <summary>« Sortie » of ⋮ → Informations: the device, and the channels it gets or « to the amplifier ».</summary>
    private string AudioOutputInfo()
    {
        if (_mpv is null) return "—";
        var devices = _mpv.AudioDevices(); var device = AudioOutput.Device(_settings.AudioDevice, devices);
        var name = device == AudioOutput.Default ? WindowsDefault : devices.First(d => d.Name == device).Label;
        var how = (_mpv.Get("audio-out-params/format") ?? "").StartsWith("spdif", StringComparison.Ordinal) ? L("vers l’ampli, sans décodage", "to the amplifier, undecoded")
            : _mpv.Get("audio-out-params/hr-channels") is { Length: > 0 } channels ? channels == "stereo" ? L("stéréo", "stereo") : channels : null;
        return how is null ? name : $"{name} · {how}";
    }
}
