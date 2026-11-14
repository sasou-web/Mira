using System.Diagnostics;
using System.IO;
using Mira.Core;
using Mira.Desktop.Services;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// HDR clips of tools/player-check.ps1 (--colour-media &lt;folder&gt;: hdr10.mkv, hlg.mkv, HEVC 10 bits made by ffmpeg's
    /// libx265): mpv reads their range and peak, « Couleurs » of ⋮ → Informations says it, and on the runner's screen
    /// (no HDR) the picture is tone-mapped to SDR, never sent as HDR. Windows' HDR on a real screen stays to be tried.
    /// </summary>
    private async Task RunColourChecksAsync(List<string> checks, Action<bool, string> require)
    {
        var index = Array.IndexOf(_args, "--colour-media");
        if (index < 0 || index + 1 >= _args.Length) { checks.Add("INFO no HDR clips (--colour-media): colours not checked"); return; }
        var folder = _args[index + 1]; var kept = _testMedia;
        try
        {
            foreach (var (file, expected) in new[] { ("hdr10.mkv", "HDR10 · 1000 nits"), ("hlg.mkv", "HLG") })
            {
                _testMedia = Path.Combine(folder, file);
                await PlayAsync(DemoLibrary.Items()[0]);
                var timer = Stopwatch.StartNew();
                while (!(_loaded && !_videoWaits && _mpv is { } playing && PictureColours.Source(playing.Get) is not null))
                {
                    if (timer.Elapsed.TotalSeconds > 15) throw new TimeoutException($"mpv did not read the colours of {file}.");
                    await Task.Delay(50);
                }
                _mpv!.Set("pause", "yes"); await Task.Delay(300);
                var source = PictureColours.Source(_mpv.Get); var hdrOut = PictureColours.HdrOut(_mpv.Get);
                checks.Add($"INFO {file}: gamma {_mpv.Get("video-params/gamma")}, primaries {_mpv.Get("video-params/primaries")}, max-cll {_mpv.Get("video-params/max-cll")}, max-luma {_mpv.Get("video-params/max-luma")}, pixel format {_mpv.Get("video-params/pixelformat")}, decoding {_mpv.Get("hwdec-current")}, vo {_mpv.Get("current-vo")}, to the screen: gamma {_mpv.Get("video-target-params/gamma")}, primaries {_mpv.Get("video-target-params/primaries")}, peak {_mpv.Get("video-target-params/max-luma")}");
                require(source == expected, $"{file}: mpv reads it as {source} (expected {expected})");
                require(hdrOut != true, $"{file}: tone-mapped on this screen without HDR, never sent as HDR ({PictureColours.Line(source, hdrOut)})");
                BuildMoreMenu();
                require(PlaybackInfoText.Contains(PictureColours.Line(source, hdrOut), StringComparison.Ordinal) && PlaybackInfoText.Contains(L("10 bits", "10-bit"), StringComparison.Ordinal),
                    $"{file}: ⋮ → Informations says its colours and its 10 bits ({PlaybackInfoText.Replace('\n', ' ')})");
                await StopPlaybackAsync(); ReturnToLibrary(); await _engineReleased;
            }
        }
        finally { _testMedia = kept; }
    }
}
