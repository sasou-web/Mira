using System.IO;
using System.Windows;
using System.Windows.Interop;
using Mira.Core;
using Mira.Desktop.Services;
using Drawing = System.Drawing;

namespace Mira.Desktop;

// Player fixture: « Image dans l’image », from the mini-player and from the whole player. The video's own window moves
// into the small window and back with the same engine, the small window stays above the others, the subtitles show in
// it at a readable size, the next title plays there, and « Fermer » stops without leaving a window behind.
public partial class MainWindow
{
    private async Task RunPipChecksAsync(string output, List<string> checks, Action<bool, string> require, Func<Func<bool>, Task> until)
    {
        var engine = _mpv!; var video = _videoHost!; var main = new WindowInteropHelper(this).Handle;
        // A subtitle over the whole clip, as a film's would be.
        var subtitles = Path.Combine(output, "pip-subtitles.srt");
        await File.WriteAllTextAsync(subtitles, "1\r\n00:00:00,000 --> 00:00:59,000\r\nMIRA PIP SUBTITLES\r\n");
        engine.Command("sub-add", subtitles, "select"); engine.Set("pause", "yes");
        await until(() => engine.Get("sid") is not (null or "no" or ""));

        SetMiniPlayer(true); await Task.Delay(150);
        EnterPip(); await Task.Delay(600);
        var pip = _pip;
        var small = pip is null ? IntPtr.Zero : new WindowInteropHelper(pip).Handle;
        require(pip is { IsVisible: true, Topmost: true } && small != IntPtr.Zero && (GetWindowLong(small, -20) & 0x8) != 0, "picture in picture: a window of its own, above the others (WS_EX_TOPMOST)");
        require(GetParent(video.Handle) == small && ReferenceEquals(engine, _mpv) && _playing && _loaded, "picture in picture: the video's own window moved into it, the same engine playing on");
        require(_miniPlayer && LibraryShell.IsVisible && !PlayerShell.IsVisible && !MiniFrame.IsVisible && !PlayerOverlayLayer.IsOpen, "picture in picture: Mira's window shows the library, the mini-player's place left empty");
        var controls = pip!.Controls.Handle; var above = false;
        for (var next = GetWindow(controls, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) if (next == video.Handle) { above = true; break; }
        require(controls != IntPtr.Zero && GetParent(controls) == small && (GetWindowLong(controls, -20) & 0x80000) != 0 && above, "picture in picture: its controls are a layered child window above the video");
        var bounds = pip.Bounds();
        require(Math.Abs(bounds.Width / bounds.Height - 16d / 9) < .02, $"picture in picture: the video's shape ({bounds.Width} × {bounds.Height} px)");

        // Subtitles: mpv's own, raised for the small window.
        var expected = PictureInPicture.SubtitleScale(_settings.SubtitleSize, pip.VideoHeight);
        require(Math.Abs(engine.Number("sub-scale") - expected) < .01 && engine.Get("sub-visibility") == "yes", $"picture in picture: subtitles shown, sized for {pip.VideoHeight:0} high (sub-scale {engine.Get("sub-scale")})");
        var screen = PictureInPicture.ScreenOf(bounds, Screens.All())!;
        var smallest = PictureInPicture.Resize(bounds, PictureInPicture.Edge.TopLeft, bounds.Width, bounds.Height, pip.Aspect, screen);
        pip.Place(smallest, direct: true); await Task.Delay(500);
        expected = PictureInPicture.SubtitleScale(_settings.SubtitleSize, pip.VideoHeight);
        require(expected > 1.5 && Math.Abs(engine.Number("sub-scale") - expected) < .01, $"picture in picture: made smaller ({pip.Bounds().Width} × {pip.Bounds().Height} px), the subtitles grow to stay readable (sub-scale {engine.Get("sub-scale")})");
        pip.Place(bounds, direct: true); await Task.Delay(500);

        // As a capture sees it: the controls over the video, and the subtitles in the picture.
        pip.ShowControls(); await Task.Delay(400);
        using (var shown = CaptureWindow(small)) shown.Save(Path.Combine(output, "pip-window.png"), Drawing.Imaging.ImageFormat.Png);
        pip.HideControlsNow(); await Task.Delay(500);
        using var withText = CaptureWindow(small);
        engine.Set("sub-visibility", "no"); await Task.Delay(500);
        using var without = CaptureWindow(small);
        engine.Set("sub-visibility", "yes");
        var lines = new Drawing.Rectangle(withText.Width / 8, withText.Height * 3 / 5, withText.Width * 3 / 4, withText.Height * 3 / 10);
        var picture = new Drawing.Rectangle(withText.Width / 4, withText.Height / 4, withText.Width / 2, withText.Height / 4);
        var seen = Colourful(without, picture); var text = Changed(without, withText, lines);
        checks.Add($"INFO picture in picture capture {withText.Width}x{withText.Height}: video seen {seen:P0}, subtitles change {text:P1} of their band");
        // The runner may not hand mpv's picture to a capture: then the subtitles cannot be seen there either.
        if (seen > .2) require(text > .005, $"picture in picture: the subtitles are drawn in the small window ({text:P1} of their band)");

        // The next title plays in the small window, with a new engine sized again.
        await PlayAsync(DemoLibrary.Items()[1], keepMini: true); await until(() => _loaded);
        _mpv!.Set("pause", "yes"); await Task.Delay(300);
        require(_pip == pip && GetParent(video.Handle) == small && !ReferenceEquals(engine, _mpv) && Math.Abs(_mpv.Number("sub-scale") - PictureInPicture.SubtitleScale(_settings.SubtitleSize, pip.VideoHeight)) < .01,
            "picture in picture: the next title plays in it, its subtitles sized again");
        engine = _mpv;

        // Back to Mira: the whole player, the same engine, mpv's own subtitle size.
        ReturnFromPip(); await Task.Delay(400);
        require(_pip is null && !pip.IsVisible && !_miniPlayer && PlayerShell.IsVisible && GetParent(video.Handle) == main && ReferenceEquals(engine, _mpv) && Math.Abs(engine.Number("sub-scale") - 1) < .001,
            "picture in picture: back in Mira, the whole player with the same engine, subtitles at their own size");

        // From the whole player, then « Fermer »: playback stopped, the video back in Mira's window, no window left.
        EnterPip(); await Task.Delay(500);
        require(_pip is not null && !PlayerShell.IsVisible && LibraryShell.IsVisible, "picture in picture: opened from the whole player too");
        var last = _pip!;
        StopFromPip(); await until(() => !_playing && _pip is null);
        require(!_playing && _mpv is null && !last.IsVisible && GetParent(video.Handle) == main && LibraryShell.IsVisible, "picture in picture: « Fermer » stops the playback and leaves no window behind");
    }
}
