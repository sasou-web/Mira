using System.IO;
using Mira.Checks;
using Mira.Core;
using Mira.Desktop.Playback;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The player's own safety with the libmpv Mira installs (PlayerSafety): https:// against three certificates, the
    /// token sent only to the trusted one; yt-dlp never run; and with --track-media &lt;folder&gt; (the films of
    /// tools/player-check.ps1), the subtitles a film starts with, for a Mira in English and in French.
    /// </summary>
    private async Task RunPlayerSafetyChecksAsync(List<string> checks, Action<bool, string> require)
    {
        var settings = new PlayerSettings { MpvPath = _settings.MpvPath, AudioLanguage = "jpn,ja,eng,en,fre,fra,fr", SubtitleLanguage = "eng,en,fre,fra,fr" };
        var lines = await PlayerSafety.HttpsAsync(network => new MpvEngine(IntPtr.Zero, settings, headless: true) { Network = network });
        var index = Array.IndexOf(_args, "--track-media");
        if (index >= 0 && index + 1 < _args.Length)
            lines.AddRange(await PlayerSafety.TracksAsync(languages => new MpvEngine(IntPtr.Zero, new PlayerSettings { MpvPath = _settings.MpvPath, AudioLanguage = languages.Audio, SubtitleLanguage = languages.Subtitles }, headless: true), _args[index + 1]));
        else checks.Add("INFO no track media (--track-media): the subtitles at the start not checked");
        foreach (var line in lines)
            if (line.Info) checks.Add("INFO " + line.Message);
            else require(line.Passed, line.Message);
    }
}
