using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Mira.Core;
using Mira.Desktop.Playback;
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

        // Its first picture shown first: never out to the small window while it still rises from black.
        await until(() => !_videoWaits);
        SetMiniPlayer(true); await Task.Delay(150);
        EnterPip(); await Task.Delay(600);
        var pip = _pip;
        var small = pip is null ? IntPtr.Zero : new WindowInteropHelper(pip).Handle;
        require(pip is { IsVisible: true, Topmost: true } && small != IntPtr.Zero && (GetWindowLong(small, -20) & 0x8) != 0, "picture in picture: a window of its own, above the others (WS_EX_TOPMOST)");
        require(GetParent(video.Handle) == small && ReferenceEquals(engine, _mpv) && _playing && _loaded, "picture in picture: the video's own window moved into it, the same engine playing on");
        require(_miniPlayer && LibraryShell.IsVisible && !PlayerShell.IsVisible && !MiniFrame.IsVisible && !PlayerOverlayLayer.IsOpen, "picture in picture: Mira's window shows the library, the mini-player's place left empty");
        var controls = pip!.Controls.Handle; var above = false;
        for (var next = GetWindow(controls, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) if (next == video.Handle) { above = true; break; }
        var layered = (GetWindowLong(controls, -20) & 0x80000) != 0; var parent = GetParent(controls);
        require(controls != IntPtr.Zero && parent == small && layered && above,
            $"picture in picture: its controls are a layered child window above the video (window {controls != IntPtr.Zero}, in the small window {parent == small}, layered {layered}, above {above})");
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
        // There, lighter controls, every row in it, and the subtitles above them while they show.
        pip.ShowControls(); await Task.Delay(400);
        using (var shown = CaptureWindow(small)) shown.Save(Path.Combine(output, "pip-window-small.png"), Drawing.Imaging.ImageFormat.Png);
        var usual = _pipMargin; var own = SubtitleStyle.Height(_settings.SubtitleHeight);
        double Margin() => engine.Number("sub-margin-y", -1); double Position() => engine.Number("sub-pos", -1);
        var lifted = PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, own);
        require(pip.Fit is { Small: true, Jumps: true } && lifted is { } at && Math.Abs(Position() - at) < .5 && Margin() == 0,
            $"picture in picture: in the smallest window, lighter controls and the subtitles above them ({pip.Fit.Play} px play button, sub-pos {Position()} for {lifted}, sub-margin-y {Margin()})");
        await RequireSubtitlesAbove(pip, small, engine, require, checks);
        pip.Place(bounds, direct: true); await Task.Delay(500);

        // As a capture sees it: the controls over the video, and the subtitles in the picture.
        pip.ShowControls(); await Task.Delay(400);
        using (var shown = CaptureWindow(small)) shown.Save(Path.Combine(output, "pip-window.png"), Drawing.Imaging.ImageFormat.Png);
        lifted = PictureInPicture.SubtitleLift(pip.Fit.Lift, pip.VideoHeight, own);
        require(!pip.Fit.Small && lifted is { } there && Math.Abs(Position() - there) < .5 && Margin() == 0, $"picture in picture: while its controls show, the subtitles go above them (sub-pos {Position()} for {lifted}, sub-margin-y {Margin()})");
        await RequireSubtitlesAbove(pip, small, engine, require, checks);
        pip.HideControlsNow(); await Task.Delay(500);
        require(Math.Abs(Margin() - usual) < .5 && Math.Abs(Position() - own) < .5, $"picture in picture: the controls gone, the subtitles back at their place (sub-margin-y {Margin()}, sub-pos {Position()})");
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

        // Maquette A (2026-11-14): the player's seek bar above the time, its line in the room over the row; the sound
        // beside the subtitles; cut, a pill says it once the controls go, then nothing.
        pip.ShowControls(); await Task.Delay(400);
        var bar = pip.SeekBar; var line = pip.VideoHeight - bar.TranslatePoint(new Point(0, bar.ActualHeight / 2), (UIElement)pip.Content).Y;
        var wanted = PictureInPicture.Controls.Margin + pip.Fit.Corner + pip.Fit.SeekRoom / 2;
        require(bar.IsEnabled && bar.IsVisible && bar.ActualWidth > pip.VideoHeight && Math.Abs(line - wanted) < 1.5,
            $"picture in picture: the seek bar above the time ({bar.ActualWidth:0} px wide, its line {line:0.#} px above the bottom for {wanted:0.#}, enabled {bar.IsEnabled})");
        // The check plays at volume 0, which is « sound off » too: the sound is put back first, then cut by the button.
        bool Off() => engine.Flag("mute") || VolumeSlider.Value <= 0;
        var (volume, muted) = (VolumeSlider.Value, engine.Flag("mute"));
        if (Off()) { Mute_Click(this, new()); UpdatePip(); }
        pip.ShowControls(); await Task.Delay(100);
        Mute_Click(this, new()); UpdatePip(); pip.HideControlsNow(); await Task.Delay(400);
        require(Off() && pip.MutedPillShown, $"picture in picture: the sound cut, a pill says it once the controls go (mute {engine.Flag("mute")}, volume {VolumeSlider.Value:0}; {pip.ControlsState})");
        Mute_Click(this, new()); UpdatePip(); await Task.Delay(300);
        require(!Off() && !pip.MutedPillShown, $"picture in picture: the sound back, the pill gone (mute {engine.Flag("mute")}, volume {VolumeSlider.Value:0})");
        VolumeSlider.Value = volume; engine.Set("mute", muted ? "yes" : "no"); _muted = muted; UpdateMuteIcon(); UpdatePip();

        // A real click, through Windows' own input as a mouse gives it, while the small window is not the active one: the
        // pointer over it shows its controls, and the click lands on the play button, in the controls' window above the
        // video's (never in mpv's own). It moves the real pointer: only on a CI runner, never on someone's PC.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            pip.HideControlsNow(); await Task.Delay(500);
            var button = pip.PlayButtonCenter();
            require(button is not null, "picture in picture: its play button is on the screen");
            var (x, y) = ((int)Math.Round(button!.Value.X), (int)Math.Round(button.Value.Y));
            var under = WindowFromPoint(new PointerSpot { X = x, Y = y });
            var paused = engine.Flag("pause"); var active = GetForegroundWindow() == small;
            SetCursorPos(x - 40, y); await Task.Delay(80); SetCursorPos(x, y); await Task.Delay(500);
            require(pip.ControlsShown, "picture in picture: the real pointer over it shows its controls");
            ClickPointer(); await Task.Delay(700);
            var after = engine.Flag("pause");
            require(after != paused, $"picture in picture: a real click on its play button reaches it (paused {paused} then {after}; active before {active}; window under the pointer: {(under == controls ? "the controls'" : under == video.Handle ? "the video's" : $"0x{under:X}")})");
            ClickPointer(); await Task.Delay(700);
            require(engine.Flag("pause") == paused, "picture in picture: a second real click on it plays or pauses again");
            await RealPipDrag(pip, controls, engine, require);
            // The pointer away from it: the controls go as they would.
            SetCursorPos((int)bounds.Left - 60, (int)bounds.Top - 60); await Task.Delay(300);
        }
        else checks.Add("INFO picture in picture: real click not tried here (it moves the pointer: only on a CI runner)");

        // The next title plays in the small window, with a new engine sized again.
        await PlayAsync(DemoLibrary.Items()[1], keepMini: true); await until(() => _loaded);
        _mpv!.Set("pause", "yes"); await Task.Delay(300);
        require(_pip == pip && GetParent(video.Handle) == small && !ReferenceEquals(engine, _mpv) && Math.Abs(_mpv.Number("sub-scale") - PictureInPicture.SubtitleScale(_settings.SubtitleSize, pip.VideoHeight)) < .01,
            "picture in picture: the next title plays in it, its subtitles sized again");
        engine = _mpv;

        // Back to Mira: the whole player, the same engine, mpv's own subtitle size.
        ReturnFromPip(); await Task.Delay(400);
        // The bottom bar shown or not: the subtitles above it while it shows (LiftSubtitlesOverBar), at their place otherwise.
        HidePlayerControls(); await Task.Delay(100);
        require(_pip is null && !pip.IsVisible && !_miniPlayer && PlayerShell.IsVisible && GetParent(video.Handle) == main && ReferenceEquals(engine, _mpv) && Math.Abs(engine.Number("sub-scale") - 1) < .001
            && Math.Abs(engine.Number("sub-margin-y") - usual) < .5 && Math.Abs(engine.Number("sub-pos") - own) < .5, "picture in picture: back in Mira, the whole player with the same engine, subtitles at their own size and place");
        ShowPlayerControls(); LiftSubtitlesOverBar(); await Task.Delay(100);
        var barTop = PlayerControlsBody.TranslatePoint(new Point(), PlayerSurface).Y;
        var barLift = PictureInPicture.SubtitleLift(PlayerSurface.ActualHeight - barTop + 8, PlayerSurface.ActualHeight, own);
        require(barLift is { } barAt && Math.Abs(engine.Number("sub-pos") - barAt) < .5 && engine.Number("sub-margin-y") == 0,
            $"player: while the bottom bar shows, the subtitles go above its title and time (sub-pos {engine.Get("sub-pos")} for {barLift}, sub-margin-y {engine.Get("sub-margin-y")})");
        HidePlayerControls(); await Task.Delay(100);
        require(Math.Abs(engine.Number("sub-margin-y") - usual) < .5 && Math.Abs(engine.Number("sub-pos") - own) < .5, $"player: the bar gone, the subtitles back at their place (sub-margin-y {engine.Get("sub-margin-y")}, sub-pos {engine.Get("sub-pos")})");

        // From the whole player, then « Fermer »: playback stopped, the video back in Mira's window, no window left.
        EnterPip(); await Task.Delay(500);
        require(_pip is not null && !PlayerShell.IsVisible && LibraryShell.IsVisible, "picture in picture: opened from the whole player too");
        var last = _pip!;
        StopFromPip(); await until(() => !_playing && _pip is null);
        require(!_playing && _mpv is null && !last.IsVisible && GetParent(video.Handle) == main && LibraryShell.IsVisible, "picture in picture: « Fermer » stops the playback and leaves no window behind");
    }
    /// <summary>
    /// The subtitles as drawn, not only as set: with the controls showing (and the video paused), the window captured with
    /// and without them; their lowest row lies above the time and the subtitles' button, and not much higher (a recent
    /// mpv grows sub-margin-y with sub-scale: the small window's subtitles once went halfway up, over the play button).
    /// </summary>
    private async Task RequireSubtitlesAbove(PipWindow pip, IntPtr window, MpvEngine engine, Action<bool, string> require, List<string> checks)
    {
        // The controls kept on all along: without the pointer over the window they go after 0.6 s, the subtitles with them.
        async Task Hold() { for (var i = 0; i < 5; i++) { pip.ShowControls(); await Task.Delay(100); } pip.ShowControls(); }
        await Hold();
        using var with = CaptureWindow(window);
        engine.Set("sub-visibility", "no"); await Hold();
        using var without = CaptureWindow(window);
        engine.Set("sub-visibility", "yes"); await Hold();
        if (!pip.ControlsShown) { require(false, "picture in picture: its controls stayed on while the subtitles were measured"); return; }
        // The runner may not hand mpv's picture to a capture: then the subtitles cannot be seen there either.
        if (Colourful(without, new Drawing.Rectangle(without.Width / 4, without.Height / 4, without.Width / 2, without.Height / 4)) < .2)
        { checks.Add("INFO picture in picture: the subtitles' place not measured (no video in the capture)"); return; }
        var area = new Drawing.Rectangle(with.Width / 8, 0, with.Width * 3 / 4, with.Height);
        var a = Argb(without, area); var b = Argb(with, area);
        int top = -1, bottom = -1;
        for (var row = 0; row < area.Height && a.Length == b.Length; row++)
        {
            var changed = 0;
            for (var i = row * area.Width; i < (row + 1) * area.Width; i++)
                if (Math.Max(Math.Abs((a[i] >> 16 & 255) - (b[i] >> 16 & 255)), Math.Max(Math.Abs((a[i] >> 8 & 255) - (b[i] >> 8 & 255)), Math.Abs((a[i] & 255) - (b[i] & 255)))) > 24) changed++;
            if (changed < 2) continue;
            if (top < 0) top = row;
            bottom = row;
        }
        // From the window's bottom edge, in device-independent pixels; the controls' glyphs reach Lift - 8 from it.
        var scale = with.Height / pip.ActualHeight;
        var lowest = (with.Height - 1 - bottom) / scale; var highest = (with.Height - top) / scale;
        var fit = pip.Fit;
        checks.Add($"INFO picture in picture {with.Width}x{with.Height}: subtitles drawn {lowest:0} to {highest:0} px above the bottom, controls up to {fit.Lift - 8:0}");
        require(top >= 0 && lowest >= fit.Lift - 8 && lowest <= fit.Lift + 14,
            $"picture in picture: the subtitles drawn just above its controls ({(top < 0 ? "none seen" : $"{lowest:0} px above the bottom")}, wanted {fit.Lift - 8:0} to {fit.Lift + 14:0})");
    }
    /// <summary>
    /// A real drag, through Windows' own input: pressed on the picture left of the buttons, moved, let go. The small window
    /// follows the pointer all along and stays where it was let go, its controls shown, the playback neither paused nor
    /// played by it; then dragged back. (0.9.10 on a real PC, once: no controls under the pointer, the window would not move.)
    /// </summary>
    private async Task RealPipDrag(PipWindow pip, IntPtr controls, MpvEngine engine, Action<bool, string> require)
    {
        // Past the double click's time: the press is never the second click of the clicks before.
        await Task.Delay(700);
        var start = pip.Bounds(); var paused = engine.Flag("pause");
        var (x, y) = ((int)Math.Round(start.Left + start.Width * .15), (int)Math.Round(start.Top + start.Height / 2));
        var under = WindowFromPoint(new PointerSpot { X = x, Y = y });
        // Mira's own window holding the pointer, as once on a real PC (0.9.10: over the small window, no controls and no
        // drag): Windows sends that window every move over the small one, until the small window takes it back.
        pip.HideControlsNow(); await Task.Delay(300);
        var held = LibraryShell.CaptureMouse();
        SetCursorPos(x - 20, y); await Task.Delay(60); SetCursorPos(x, y); await Task.Delay(900);
        require(held && Mouse.Captured is null && pip.ControlsShown,
            $"picture in picture: the pointer held by Mira's window is taken back over the small one, its controls shown (held {held}, then by {Mouse.Captured?.GetType().Name ?? "nothing"}, controls {pip.ControlsShown})");
        async Task<(double Left, double Top)> Drag(int dx, int dy, int steps)
        {
            var from = pip.Bounds();
            // Where the window is once it is within 3 px of the pointer's move, or when the time is up.
            async Task<ScreenFit.Pixels> Settled(int wantX, int wantY, int ms)
            {
                var until = Environment.TickCount64 + ms;
                while (true)
                {
                    var now = pip.Bounds();
                    if ((Math.Abs(now.Left - from.Left - wantX) <= 3 && Math.Abs(now.Top - from.Top - wantY) <= 3) || Environment.TickCount64 >= until) return now;
                    await Task.Delay(15);
                }
            }
            PressPointer(down: true); await Task.Delay(80);
            (double, double) midway = default;
            for (var i = 1; i <= steps; i++)
            {
                SetCursorPos(x + dx * i / steps, y + dy * i / steps); await Task.Delay(40);
                // Halfway, read once the move has come through (a busy runner can hold it past the 40 ms).
                if (i == steps / 2) { var now = await Settled(dx / 2, dy / 2, 300); midway = (now.Left - from.Left, now.Top - from.Top); }
            }
            PressPointer(down: false); await Task.Delay(150);
            await Settled(dx, dy, 1000);
            return midway;
        }
        var (midX, midY) = await Drag(-90, -40, 6);
        var moved = pip.Bounds();
        var where = $"pressed at {x - (int)start.Left},{y - (int)start.Top} on window 0x{under:X} ({(under == controls ? "the controls'" : "not the controls'")}), moved {midX:0},{midY:0} halfway, {moved.Left - start.Left:0},{moved.Top - start.Top:0} at the end";
        require(Math.Abs(midX + 45) <= 3 && Math.Abs(midY + 20) <= 3, $"picture in picture: a real drag moves it with the pointer ({where})");
        require(Math.Abs(moved.Left - start.Left + 90) <= 3 && Math.Abs(moved.Top - start.Top + 40) <= 3 && moved.Width == start.Width && moved.Height == start.Height,
            $"picture in picture: let go, it stays where it was dropped, at its size ({where})");
        require(engine.Flag("pause") == paused && pip.ControlsShown, $"picture in picture: a drag neither plays nor pauses, its controls stay (paused {paused} then {engine.Flag("pause")})");
        // Back where it was, by the same way: dragged from the same place of the picture.
        (x, y) = ((int)Math.Round(moved.Left + moved.Width * .15), (int)Math.Round(moved.Top + moved.Height / 2));
        SetCursorPos(x, y); await Task.Delay(700);
        await Drag(90, 40, 6);
        var back = pip.Bounds();
        require(Math.Abs(back.Left - start.Left) <= 3 && Math.Abs(back.Top - start.Top) <= 3, $"picture in picture: dragged back to its place ({back.Left - start.Left:0},{back.Top - start.Top:0} from it)");
    }
    /// <summary>The left button pressed or let go where the pointer is, as the mouse sends it.</summary>
    private static void PressPointer(bool down)
    {
        // MOUSEEVENTF_LEFTDOWN or MOUSEEVENTF_LEFTUP.
        var inputs = new[] { new PointerInput { Type = 0, Mouse = new PointerEvent { Flags = down ? 0x0002u : 0x0004u } } };
        if (SendPointer(1, inputs, Marshal.SizeOf<PointerInput>()) != 1) throw new InvalidOperationException("Windows refused the press.");
    }
    /// <summary>A left click where the pointer is, as the mouse sends it.</summary>
    private static void ClickPointer()
    {
        // MOUSEEVENTF_LEFTDOWN, then MOUSEEVENTF_LEFTUP.
        var inputs = new[] { new PointerInput { Type = 0, Mouse = new PointerEvent { Flags = 0x0002 } }, new PointerInput { Type = 0, Mouse = new PointerEvent { Flags = 0x0004 } } };
        if (SendPointer((uint)inputs.Length, inputs, Marshal.SizeOf<PointerInput>()) != inputs.Length) throw new InvalidOperationException("Windows refused the click.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointerSpot { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PointerEvent { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct PointerInput { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public PointerEvent Mouse; }
    [DllImport("user32", EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendPointer(uint count, PointerInput[] inputs, int size);
    [DllImport("user32")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern IntPtr WindowFromPoint(PointerSpot point);
}
