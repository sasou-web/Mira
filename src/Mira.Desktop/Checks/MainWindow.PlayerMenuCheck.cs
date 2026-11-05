using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Playback;
using Drawing = System.Drawing;

namespace Mira.Desktop;

// Player fixture: the pause and play flash in the middle of the picture as a capture of the window shows it, and the
// presses made while a menu is open, through Windows' own input: they close it and go no further.
public partial class MainWindow
{
    private async Task RunPlayerMenuChecksAsync(string output, List<string> checks, Action<bool, string> require)
    {
        var mpv = _mpv!; var window = new WindowInteropHelper(this).Handle;
        async Task Keep() { var shown = _controlsVisible; ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30); if (!shown) await Task.Delay(300); }
        PlayerOptionsPopup.IsOpen = false; HidePlayState();
        mpv.Set("pause", "yes"); await Task.Delay(250); mpv.Poll(); await Keep(); await Task.Delay(200);

        // The flash at its full size (from 70 to 360 ms after the press), over the still picture: the pause, then the play,
        // side by side in player-play-state.png for the log's previews. Judged once the presses below are done.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        GetWindowRect(window, out var frame);
        var middle = PlayerShell.PointToScreen(new Point(PlayerShell.ActualWidth / 2, PlayerShell.ActualHeight / 2));
        var (cx, cy) = ((int)Math.Round(middle.X) - frame.Left, (int)Math.Round(middle.Y) - frame.Top);
        using var still = CaptureWindow(window);
        using var pause = await FlashCapture(window, true);
        using var play = await FlashCapture(window, false);
        ShowPlayState(true); await Task.Delay(TimeSpan.FromMilliseconds(PlayStateBadge.End + 250));
        using var after = CaptureWindow(window);
        var crop = new Drawing.Size((int)(300 * scale), (int)(200 * scale));
        using (var both = new Drawing.Bitmap(crop.Width * 2 + 8, crop.Height))
        {
            using (var graphics = Drawing.Graphics.FromImage(both))
            {
                graphics.Clear(Drawing.Color.Black);
                var source = new Drawing.Rectangle(cx - crop.Width / 2, cy - crop.Height / 2, crop.Width, crop.Height);
                graphics.DrawImage(pause, new Drawing.Rectangle(0, 0, crop.Width, crop.Height), source, Drawing.GraphicsUnit.Pixel);
                graphics.DrawImage(play, new Drawing.Rectangle(crop.Width + 8, 0, crop.Width, crop.Height), source, Drawing.GraphicsUnit.Pixel);
            }
            both.Save(Path.Combine(output, "player-play-state.png"), Drawing.Imaging.ImageFormat.Png);
        }

        // Real presses, as a mouse gives them, with the window on the screen: they move the pointer, so only on a CI runner.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") await RealMenuPresses(window, checks, require, Keep);
        else checks.Add("INFO the menus with real presses: not tried here (they move the pointer: only on a CI runner)");

        // The icon alone: white where it is, the picture around it barely touched by its shadows (the disc it had before
        // took 88 % of the light out of its square), nothing left once gone.
        var half = (int)Math.Round(PlayStateBadge.Glyph / 2 * scale);
        var icon = new Drawing.Rectangle(cx - half, cy - half, half * 2, half * 2);
        static bool Light(int p) => (p >> 16 & 255) >= 230 && (p >> 8 & 255) >= 230 && (p & 255) >= 230;
        static double Luma(int p) => .2126 * (p >> 16 & 255) + .7152 * (p >> 8 & 255) + .0722 * (p & 255);
        foreach (var (shot, name) in new[] { (pause, "pause"), (play, "play") })
        {
            var a = Argb(still, icon); var b = Argb(shot, icon);
            if (a.Length == 0 || a.Length != b.Length) { require(false, $"the {name} flash: its square is in the capture"); return; }
            var drawn = Changed(still, shot, icon);
            var white = (b.Count(Light) - a.Count(Light)) / (double)b.Length;
            // Around the icon, in its square: the light the shadows took from the picture.
            double before = 0, now = 0;
            for (var i = 0; i < b.Length; i++) if (!Light(b[i])) { before += Luma(a[i]); now += Luma(b[i]); }
            var shade = before <= 0 ? 0 : 1 - now / before;
            checks.Add($"INFO the {name} flash: {drawn:P0} of its square changed, {white:P0} of it white, {shade:P0} of the light around the icon taken by its shadows");
            require(drawn > .1 && white > .08 && shade < .35,
                $"the {name} flash is the white icon alone, its shadows barely darkening the picture around it ({drawn:P0} of its square drawn, {white:P0} white; {shade:P0} darker around it)");
        }
        var left = Changed(still, after, icon);
        require(left < .01, $"the flash leaves nothing over the picture once gone ({left:P1} of its square still changed)");
    }
    /// <summary>The window as a capture sees it, a quarter of a second after the flash was asked for.</summary>
    private async Task<Drawing.Bitmap> FlashCapture(IntPtr window, bool paused)
    {
        ShowPlayState(paused); await Task.Delay(250);
        var shot = CaptureWindow(window);
        HidePlayState(); await Task.Delay(150);
        return shot;
    }
    /// <summary>
    /// The audio and subtitles menu opened by a press on its button (Mira comes to the front), then: a press on the
    /// picture closes it and nothing else, never a pause; two quick ones, the first closing it, are a click, never a
    /// double click (full screen); a press on ⋮ opens ⋮'s menu, and ⋮ again closes it.
    /// </summary>
    private async Task RealMenuPresses(IntPtr window, List<string> checks, Action<bool, string> require, Func<Task> keep)
    {
        var mpv = _mpv!; var placed = (Left, Top);
        var work = FullscreenWindow.WorkArea(window);
        var dpi = VisualTreeHelper.GetDpi(this);
        GetWindowRect(window, out var outer);
        // Its bottom right corner on the screen's: the picture and the buttons' row in view, above the taskbar.
        Left = (work.Right - (outer.Right - outer.Left)) / dpi.DpiScaleX; Top = (work.Bottom - (outer.Bottom - outer.Top)) / dpi.DpiScaleY;
        UpdateLayout(); await Task.Delay(500); await keep();
        try
        {
            (int X, int Y) Spot(FrameworkElement element, double x = .5, double y = .5)
            { var point = element.PointToScreen(new Point(element.ActualWidth * x, element.ActualHeight * y)); return ((int)Math.Round(point.X), (int)Math.Round(point.Y)); }
            string Under((int X, int Y) spot)
            {
                var under = WindowFromPoint(new PointerSpot { X = spot.X, Y = spot.Y });
                return under == PlayerOverlayLayer.Handle ? "the controls'" : under == TransportLayer.Handle ? "the buttons' row's" : under == _videoHost?.Handle ? "the video's"
                    : PresentationSource.FromVisual(PlayerOptionsPopup.Child) is HwndSource menu && under == menu.Handle ? "the menu's" : under == window ? "Mira's" : $"0x{under:X}";
            }
            bool OnScreen((int X, int Y) spot) => spot.X >= work.Left && spot.X < work.Right && spot.Y >= work.Top && spot.Y < work.Bottom;
            var tracks = Spot(PlayerTracksButton); var more = Spot(PlayerMoreButton);
            require(PlayerTracksButton.IsVisible && PlayerMoreButton.IsVisible && OnScreen(tracks) && OnScreen(more), $"real presses: the menus' buttons on the screen ({tracks}, {more} in {work})");
            async Task OpenTracks(string step)
            {
                await keep(); await Press(tracks); await Task.Delay(500);
                require(PlayerOptionsPopup.IsOpen && TracksMenu.IsVisible && GetForegroundWindow() == window,
                    $"{step}: a real press on the audio and subtitles button opens its menu, Mira in front (open {PlayerOptionsPopup.IsOpen}; in front {GetForegroundWindow() == window}; under the pointer {Under(tracks)})");
            }
            await OpenTracks("real presses");
            // A spot of the picture away from the menu, which opens above its button on the right.
            var picture = Spot(PlayerShell, .22, .45);
            var menu = PlayerOptionsPopup.Child.PointToScreen(new Point());
            require(OnScreen(picture) && !InBarBand(PlayerSurface.PointFromScreen(new Point(picture.X, picture.Y))) && picture.X < menu.X && Under(picture) == "the controls'",
                $"real presses: a spot of the picture beside the menu ({picture}, the menu from {menu.X:0}; under it {Under(picture)})");

            var paused = mpv.Flag("pause"); var full = _fullscreen;
            await Press(picture); await Task.Delay(650); mpv.Poll();
            require(!PlayerOptionsPopup.IsOpen && mpv.Flag("pause") == paused && _fullscreen == full,
                $"a real press on the picture while a menu is open closes the menu and goes no further (open {PlayerOptionsPopup.IsOpen}; paused {paused} then {mpv.Flag("pause")}; full screen {_fullscreen})");

            await OpenTracks("two quick presses");
            await Press(picture); await Task.Delay(150); await Press(picture); await Task.Delay(700); mpv.Poll();
            require(!PlayerOptionsPopup.IsOpen && mpv.Flag("pause") != paused && _fullscreen == full,
                $"two quick real presses on the picture, the first closing the menu: the second is a click, never a double click (paused {paused} then {mpv.Flag("pause")}; full screen {_fullscreen})");
            mpv.Set("pause", paused ? "yes" : "no"); await Task.Delay(250); mpv.Poll();

            await OpenTracks("another menu");
            await Press(more); await Task.Delay(500);
            require(PlayerOptionsPopup.IsOpen && MoreMenu.IsVisible && ReferenceEquals(_menuAnchor, PlayerMoreButton) && mpv.Flag("pause") == paused,
                $"with a menu open, a real press on ⋮ opens ⋮'s menu (open {PlayerOptionsPopup.IsOpen}; ⋮'s {MoreMenu.IsVisible}; under the pointer {Under(more)})");
            await Press(more); await Task.Delay(600);
            require(!PlayerOptionsPopup.IsOpen && mpv.Flag("pause") == paused, $"a real press on ⋮ again closes its menu (open {PlayerOptionsPopup.IsOpen})");
            // Past the double-click time, so that no later press counts these.
            await Task.Delay(400);
        }
        finally
        {
            PlayerOptionsPopup.IsOpen = false;
            (Left, Top) = placed; UpdateLayout(); await Task.Delay(300);
            ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }
    /// <summary>A left press at this spot of the screen, held as a finger does, then let go.</summary>
    private static async Task Press((int X, int Y) spot)
    {
        SetCursorPos(spot.X, spot.Y); await Task.Delay(80);
        // MOUSEEVENTF_LEFTDOWN, then MOUSEEVENTF_LEFTUP.
        foreach (var flags in new uint[] { 0x0002, 0x0004 })
        {
            var input = new[] { new PointerInput { Type = 0, Mouse = new PointerEvent { Flags = flags } } };
            if (SendPointer(1, input, System.Runtime.InteropServices.Marshal.SizeOf<PointerInput>()) != 1) throw new InvalidOperationException("Windows refused the press.");
            if (flags == 0x0002) await Task.Delay(90);
        }
    }
}
