using System.Diagnostics;
using System.IO;
using Mira.Core;
using Mira.Desktop.Playback;

namespace Mira.Desktop;

// « Sortie audio »: the test tone as mpv reads it, and what « Tester » does on this PC (a runner may have no sound device).
public partial class MainWindow
{
    private async Task RunSoundTestCheckAsync(string output, List<string> checks, Action<bool, string> require, Func<Func<bool>, Task> until)
    {
        var (wav, speakers) = AudioOutput.TestSound("5.1,stereo"); var path = Path.Combine(output, "sound-test-5.1.wav"); File.WriteAllBytes(path, wav);
        using (var probe = new MpvEngine(IntPtr.Zero, new PlayerSettings { MpvPath = _settings.MpvPath }, headless: true))
        {
            var loaded = false; probe.FileLoaded += () => loaded = true;
            probe.Load(path, 0, paused: true);
            await until(() => { probe.Poll(); return loaded && probe.Number("audio-params/channel-count") > 0; });
            require(probe.Number("audio-params/channel-count") == 6 && probe.Get("audio-params/hr-channels") == "5.1" && probe.Number("audio-params/samplerate") == AudioOutput.TestRate && speakers.Count == 6,
                $"the 5.1 test tone reads as 5.1 in mpv: {probe.Get("audio-params/hr-channels")}, {probe.Get("audio-params/samplerate")} Hz");
            require(Math.Abs(probe.Number("duration") - 4.5) < .05, $"the 5.1 test tone lasts one step per speaker: {probe.Get("duration")} s");
        }
        // « Tester » itself, through the page: played to its end, or said, never stuck.
        SoundTest_Click(this, new());
        var timer = Stopwatch.StartNew();
        while (_soundTest is not null && timer.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(100);
        require(_soundTest is null && SoundTestButton.IsEnabled && SoundTestStatus.Text.Length > 0, "« Tester » ends by itself and gives the button back");
        checks.Add($"INFO « Tester » on this PC after {timer.Elapsed.TotalSeconds:0.0} s: {SoundTestStatus.Text}");
    }
}
