using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Drawing = System.Drawing;

namespace Mira.Desktop;

// Player fixture: the controls in a capture of the window (a screenshot, a Discord or OBS share), and the clicks that
// must never pause (the bands of the bars, the click that brings Mira to the front, a click reaching mpv's window).
public partial class MainWindow
{
    private async Task RunPlayerCaptureChecksAsync(string output, List<string> checks, Action<bool, string> require)
    {
        var mpv = _mpv!;
        var window = new WindowInteropHelper(this).Handle; var overlay = PlayerOverlayLayer.Handle;
        require(overlay != IntPtr.Zero && GetParent(overlay) == window && (GetWindowLong(overlay, -16) & 0x40000000) != 0 && (GetWindowLong(overlay, -20) & 0x80000) != 0,
            "the controls are a layered child window of Mira's window");
        var above = false;
        for (var next = GetWindow(overlay, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) if (next == _videoHost?.Handle) { above = true; break; }
        require(above, "the controls' window sits above the video's");
        var row = TransportLayer.Handle; var rowAbove = false;
        for (var next = GetWindow(row, 2); next != IntPtr.Zero; next = GetWindow(next, 2)) if (next == overlay) { rowAbove = true; break; }
        GetWindowRect(row, out var rowRect); GetWindowRect(overlay, out var whole);
        var slot = PlayerTransportSlot.PointToScreen(new Point());
        require(row != IntPtr.Zero && GetParent(row) == window && (GetWindowLong(row, -20) & 0x80000) != 0 && rowAbove && Math.Abs(rowRect.Left - slot.X) <= 1 && Math.Abs(rowRect.Top - slot.Y) <= 1
            && (rowRect.Bottom - rowRect.Top) * 4 < whole.Bottom - whole.Top,
            $"the buttons' row has a layered window of its own size, over its slot and above the controls' ({rowRect.Right - rowRect.Left} × {rowRect.Bottom - rowRect.Top} px of {whole.Right - whole.Left} × {whole.Bottom - whole.Top})");

        mpv.Set("pause", "yes"); await Task.Delay(250); mpv.Poll();
        HidePlayerControls(); await Task.Delay(450);
        using var hidden = CaptureWindow(window);
        ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30); await Task.Delay(450);
        using var shown = CaptureWindow(window);
        shown.Save(Path.Combine(output, "player-window-capture.png"), Drawing.Imaging.ImageFormat.Png);
        var bar = Pixels(PlayerControlsBody, window); var picture = Pixels(PlayerSurface, window, .3);
        checks.Add($"INFO window capture {shown.Width}x{shown.Height}; video seen in its centre: {Colourful(shown, picture):P0} of colourful pixels");
        var changed = Changed(hidden, shown, bar);
        require(changed > .04, $"a capture of the window shows the controls over the video ({changed:P0} of the bottom bar changes when they show)");

        // The bands of the bars, shaded or hidden, never pause; the picture between them does.
        Point Middle(FrameworkElement element) => InSurface(element, new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        var title = InSurface(PlayingTitle, new Point(0, -6));
        require(InBarBand(Middle(PlayerTracksButton)) && InBarBand(Middle(SeekBar)) && InBarBand(Middle(PauseButton)) && InBarBand(new Point(PlayerSurface.ActualWidth / 2, title.Y))
            && InBarBand(Middle(PlayerBackButton)) && InBarBand(new Point(PlayerSurface.ActualWidth / 2, 20)) && !InBarBand(new Point(PlayerSurface.ActualWidth / 2, PlayerSurface.ActualHeight / 2)),
            "a click in the band of the top or bottom bar never pauses, one on the picture between them does");
        HidePlayerControls();
        require(InBarBand(Middle(PlayerTracksButton)) && !PlayerControls.IsHitTestVisible, "the bands keep their place while the controls are hidden");
        ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30);

        // The click that brings Mira to the front: Windows asks the controls' window first (WM_MOUSEACTIVATE).
        if (GetActiveWindow() != window)
        {
            SendMessage(overlay, 0x0021, window, new IntPtr((0x0201 << 16) | 1));
            require(PlayerOverlayLayer.TakeActivatingClick() && !PlayerOverlayLayer.TakeActivatingClick(), "the click that brings Mira to the front is told apart, once");
        }
        else checks.Add("INFO Mira is the active window here: the activating click is not exercised");
        LeavePlayerControls();
        require(PlayerOverlayLayer.IsOpen && !_controlsVisible && PlayerSurface.Cursor == Cursors.Arrow, "in the background, the controls leave but their surface stays over the video");
        ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddSeconds(30);

        // mpv's own window, under the controls' surface: a click there shows the controls, never pauses.
        var motion = false; void Seen(string message) { if (message == "mira-motion") motion = true; }
        mpv.Message += Seen;
        var paused = mpv.Flag("pause");
        mpv.Command("keypress", "MBTN_LEFT"); await Task.Delay(400); mpv.Poll(); await Task.Delay(100);
        mpv.Message -= Seen;
        require(mpv.Flag("pause") == paused, "a click reaching mpv's own window never pauses");
        checks.Add($"INFO that click reached Mira as a movement: {motion}");
    }
    /// <summary>The window as Windows composes it, child windows included (PW_RENDERFULLCONTENT), as a capture sees it.</summary>
    private static Drawing.Bitmap CaptureWindow(IntPtr window)
    {
        GetWindowRect(window, out var rect);
        var bitmap = new Drawing.Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top), Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        var dc = graphics.GetHdc();
        try { if (!PrintWindow(window, dc, 2)) throw new InvalidOperationException("PrintWindow failed: " + Marshal.GetLastWin32Error()); }
        finally { graphics.ReleaseHdc(dc); }
        return bitmap;
    }
    /// <summary>An element's place in the window's capture, in physical pixels; <paramref name="inset"/> keeps its centre.</summary>
    private static Drawing.Rectangle Pixels(FrameworkElement element, IntPtr window, double inset = 0)
    {
        GetWindowRect(window, out var rect);
        var topLeft = element.PointToScreen(new Point(element.ActualWidth * inset, element.ActualHeight * inset));
        var bottomRight = element.PointToScreen(new Point(element.ActualWidth * (1 - inset), element.ActualHeight * (1 - inset)));
        return Drawing.Rectangle.FromLTRB((int)topLeft.X - rect.Left, (int)topLeft.Y - rect.Top, (int)bottomRight.X - rect.Left, (int)bottomRight.Y - rect.Top);
    }
    private static int[] Argb(Drawing.Bitmap bitmap, Drawing.Rectangle area)
    {
        area.Intersect(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (area.Width <= 0 || area.Height <= 0) return [];
        var data = bitmap.LockBits(area, Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[area.Width * area.Height];
            for (var row = 0; row < area.Height; row++) Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * area.Width, area.Width);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }
    /// <summary>Share of the pixels of <paramref name="area"/> that differ clearly between two captures.</summary>
    private static double Changed(Drawing.Bitmap before, Drawing.Bitmap after, Drawing.Rectangle area)
    {
        var a = Argb(before, area); var b = Argb(after, area);
        if (a.Length == 0 || a.Length != b.Length) return 0;
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var difference = Math.Max(Math.Abs((a[i] >> 16 & 255) - (b[i] >> 16 & 255)), Math.Max(Math.Abs((a[i] >> 8 & 255) - (b[i] >> 8 & 255)), Math.Abs((a[i] & 255) - (b[i] & 255))));
            if (difference > 24) changed++;
        }
        return changed / (double)a.Length;
    }
    /// <summary>Share of saturated pixels (the test pattern), not black, grey or white.</summary>
    private static double Colourful(Drawing.Bitmap bitmap, Drawing.Rectangle area)
    {
        var pixels = Argb(bitmap, area);
        if (pixels.Length == 0) return 0;
        var count = 0;
        foreach (var pixel in pixels)
        {
            int r = pixel >> 16 & 255, g = pixel >> 8 & 255, b = pixel & 255;
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 40) count++;
        }
        return count / (double)pixels.Length;
    }
    [DllImport("user32")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
}
