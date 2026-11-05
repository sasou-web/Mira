using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Playback;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// « Sortie audio » of Réglages, as on Windows (AudioOutput): the device, its channels and passthrough, with « Tester ».
// A device chosen but unplugged leaves the sound to the Mac's default, and stays the choice for when it comes back.
public sealed partial class SettingsPage
{
    private readonly ComboBox _audioDevice = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly TextBlock _soundTestStatus = new() { FontSize = 12, LineHeight = 19, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Avalonia.Thickness(0, 10, 0, 0) };
    private Button? _soundTestButton;
    private MpvPlayer? _soundTest;
    private DispatcherTimer? _soundTestClock;
    private bool _listingDevices;
    private static string MacDefault => L("Par défaut du Mac", "Mac default");
    private static string SoundTestPath => Path.Combine(Path.GetTempPath(), "Mira-sound-test.wav");

    /// <summary>For the self-check: the devices offered besides the Mac's default.</summary>
    public int AudioDevices { get; private set; } = -1;

    private Control[] AudioRows(PlayerSettings settings)
    {
        Avalonia.Automation.AutomationProperties.SetName(_audioDevice, L("Sortie", "Output"));
        _audioDevice.SelectionChanged += (_, _) =>
        {
            if (_listingDevices || _audioDevice.SelectedItem is not ComboBoxItem { Tag: string device } item || device == settings.AudioDevice) return;
            // Its name as macOS gives it, for the day it is unplugged.
            settings.AudioDevice = device;
            settings.AudioDeviceLabel = device == AudioOutput.Default ? "" : item.Content as string ?? "";
            AudioSaved();
        };
        ListAudioDevices(settings, []);
        _ = LoadAudioDevicesAsync(settings);
        // An HDMI choice of Windows' settings is not one a Mac makes: its Dolby Digital and DTS.
        var passthrough = AudioOutput.KnownPassthrough(settings.AudioPassthrough) == AudioOutput.Hdmi ? AudioOutput.Optical : AudioOutput.KnownPassthrough(settings.AudioPassthrough);
        _soundTestButton = Ui.Action(L("Tester", "Test"), "volume", "", SoundTest);
        _soundTestButton.MinWidth = 96; _soundTestButton.Margin = new Avalonia.Thickness(10, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(_soundTestButton, L("Tester la sortie audio", "Test the audio output"));
        var device = new DockPanel { Margin = new Avalonia.Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(_soundTestButton, Dock.Right); device.Children.Add(_soundTestButton); device.Children.Add(_audioDevice);
        return
        [
            Ui.Column(0, CardTitle(L("Sortie audio", "Audio output")),
                Description(L("Par défaut, celle du Mac, qui suit ce que tu branches. Aussi dans le lecteur, sous Audio.", "The Mac’s own by default, which follows what you plug in. Also in the player, under Audio.")),
                device, _soundTestStatus),
            AudioChoice(L("Canaux", "Channels"), AudioOutput.MacChannelChoices, AudioOutput.KnownChannels(settings.AudioChannels), v => settings.AudioChannels = v,
                L("« Automatique » suit le Mac. « Stéréo » pour un casque, 5.1 ou 7.1 pour un ampli.", "“Automatic” follows the Mac. “Stereo” for headphones, 5.1 or 7.1 for an amplifier.")),
            AudioChoice(L("Son envoyé à l’ampli tel quel", "Sound sent to the amplifier as is"), AudioOutput.MacPassthroughChoices, passthrough, v => settings.AudioPassthrough = v,
                L("Pour un ampli ou une barre de son qui décode le Dolby Digital et le DTS. Le volume se règle alors sur l’ampli. À la prochaine lecture.",
                    "For an amplifier or sound bar that decodes Dolby Digital and DTS. The volume is then set on the amplifier. From the next playback.")),
        ];
    }
    private Control AudioChoice(string title, IReadOnlyList<(string Key, string Label, string Details)> choices, string current, Action<string> set, string detail)
    {
        var box = new ComboBox { MinWidth = 210 };
        foreach (var (key, label, details) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = key };
            ToolTip.SetTip(item, details);
            box.Items.Add(item);
        }
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag! == current) ?? box.Items[0];
        Avalonia.Automation.AutomationProperties.SetName(box, title);
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string value }) { set(value); AudioSaved(); } };
        // What the choice does, said under the row: a tooltip is never read by VoiceOver.
        var said = Description("", 12);
        void Say() { said.Text = box.SelectedItem is ComboBoxItem { Tag: string key } ? choices.First(x => x.Key == key).Details : ""; said.IsVisible = said.Text.Length > 0; }
        box.SelectionChanged += (_, _) => Say(); Say();
        return Ui.Column(0, Row(title, detail, box), said);
    }
    private void AudioSaved() { Save(); Shell.Player?.AudioChanged(); }

    /// <summary>The devices: the playing mpv's, else those of an mpv opened for this, off the UI thread.</summary>
    private async Task LoadAudioDevicesAsync(PlayerSettings settings)
    {
        IReadOnlyList<(string Name, string Label)> devices;
        try { devices = await Task.Run(() => { using var probe = MpvPlayer.Probe(settings); return probe.AudioDevices(); }); }
        // No mpv here (a build without Mira.app's): only the Mac's default to offer.
        catch (Exception ex) when (ex is IOException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException) { devices = []; }
        ListAudioDevices(settings, devices);
        AudioDevices = devices.Count;
    }
    private void ListAudioDevices(PlayerSettings settings, IReadOnlyList<(string Name, string Label)> devices)
    {
        _listingDevices = true;
        try
        {
            _audioDevice.Items.Clear();
            _audioDevice.Items.Add(new ComboBoxItem { Content = MacDefault, Tag = AudioOutput.Default });
            foreach (var (name, label) in devices) _audioDevice.Items.Add(new ComboBoxItem { Content = label, Tag = name });
            var chosen = settings.AudioDevice is { Length: > 0 } device ? device : AudioOutput.Default;
            // Unplugged now: still the choice, back as soon as it is plugged in again.
            if (chosen != AudioOutput.Default && devices.All(d => d.Name != chosen))
            {
                var name = settings.AudioDeviceLabel is { Length: > 0 } label ? label : L("Sortie choisie", "Chosen output");
                _audioDevice.Items.Add(new ComboBoxItem { Content = L($"{name} (pas branchée)", $"{name} (not plugged in)"), Tag = chosen });
            }
            _audioDevice.SelectedItem = _audioDevice.Items.OfType<ComboBoxItem>().First(x => (string)x.Tag! == chosen);
        }
        finally { _listingDevices = false; }
    }

    /// <summary>« Tester »: a tone on each speaker in turn, on the output and channels chosen; the file is made here and removed after.</summary>
    private void SoundTest()
    {
        StopSoundTest();
        var settings = Shell.Settings;
        var (wav, speakers) = AudioOutput.TestSound(settings.AudioChannels);
        try
        {
            File.WriteAllBytes(SoundTestPath, wav);
            var engine = MpvPlayer.Sound(new PlayerSettings { MpvPath = settings.MpvPath, AudioDevice = settings.AudioDevice, AudioChannels = settings.AudioChannels });
            _soundTest = engine;
            engine.SetLevel(Math.Clamp(settings.Volume, 40, 100));
            // After the events being read: the engine goes with them.
            engine.Ended += _ => Dispatcher.UIThread.Post(StopSoundTest);
            engine.Error += _ => Dispatcher.UIThread.Post(() => { StopSoundTest(); ShowSoundTest(L("Le son de test n’a pas pu partir sur cette sortie.", "The test tone couldn’t play on this output.")); });
            engine.Load(SoundTestPath, 0);
            var heard = string.Join(L(", puis ", ", then "), speakers);
            var name = settings.AudioDeviceLabel is { Length: > 0 } label ? label : L("Sortie choisie", "Chosen output");
            ShowSoundTest((engine.AudioMissing ? L($"« {name} » n’est pas branchée : le test passe par la sortie du Mac. ", $"“{name}” isn’t plugged in: the test plays on the Mac’s output. ") : "")
                + L($"Tu dois entendre un son : {heard}.", $"You should hear a tone: {heard}.")
                + (AudioOutput.Codecs(settings.AudioPassthrough) is null ? "" : L(" L’envoi à l’ampli se vérifie sur un film en Dolby ou en DTS.", " Sending to the amplifier is checked on a Dolby or DTS film.")));
            _soundTestButton!.IsEnabled = false;
            var until = DateTime.UtcNow + AudioOutput.TestStep * speakers.Count + TimeSpan.FromSeconds(2);
            _soundTestClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _soundTestClock.Tick += (_, _) => { _soundTest?.Poll(); if (DateTime.UtcNow > until) StopSoundTest(); };
            _soundTestClock.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            StopSoundTest(); ShowSoundTest(L("Le son de test n’a pas pu partir : ", "The test tone couldn’t play: ") + ex.Message);
        }
    }
    /// <summary>For the self-check: what « Tester » said, once it is done.</summary>
    public string? SoundTested => _soundTestStatus.IsVisible ? _soundTestStatus.Text : null;
    public void TestSoundForCheck() => SoundTest();
    public Control AudioOutputRow => _soundTestStatus;
    private void ShowSoundTest(string text) { _soundTestStatus.Text = text; _soundTestStatus.IsVisible = true; }
    private void StopSoundTest()
    {
        _soundTestClock?.Stop(); _soundTestClock = null;
        var engine = _soundTest; _soundTest = null; engine?.Dispose();
        if (_soundTestButton is not null) _soundTestButton.IsEnabled = true;
        try { File.Delete(SoundTestPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
