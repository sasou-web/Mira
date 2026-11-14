using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Drawing = System.Drawing;

namespace Mira.Desktop;

// Player fixture: the mini-player's ✕. The video slides out by the edge and never shows again in the window (0.9.9:
// « quand on ferme le mini lecteur il réapparaît en flashbang avant de partir »).
public partial class MainWindow
{
    /// <summary>
    /// Watches the video's own window, a millisecond apart, from the click on ✕ until <see cref="VideoWatch.Stop"/>: once
    /// it has left Mira's window, is it ever seen in it again?
    /// </summary>
    private sealed class VideoWatch
    {
        private readonly Thread _thread;
        private volatile bool _stop;
        public bool Left { get; private set; }
        public int CameBack { get; private set; }
        /// <summary>The screen where the mini-player was, as it closes: time (ms), mean brightness (0-255), the video's window showing.</summary>
        public List<(long Ms, int Light, bool Video)> Screen { get; } = [];
        public VideoWatch(IntPtr video, IntPtr window)
        {
            GetWindowRect(video, out var area);
            _thread = new Thread(() =>
            {
                var clock = Stopwatch.StartNew(); var lastShot = -10L;
                using var shot = new Drawing.Bitmap(Math.Max(1, area.Right - area.Left), Math.Max(1, area.Bottom - area.Top), Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var graphics = Drawing.Graphics.FromImage(shot);
                while (!_stop)
                {
                    var shows = Shows(video, window);
                    if (!shows) Left = true; else if (Left) CameBack++;
                    if (clock.ElapsedMilliseconds - lastShot >= 6)
                    {
                        lastShot = clock.ElapsedMilliseconds;
                        try { graphics.CopyFromScreen(area.Left, area.Top, 0, 0, shot.Size); Screen.Add((lastShot, Light(shot), shows)); }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ExternalException) { }
                    }
                    Thread.Sleep(1);
                }
            })
            { IsBackground = true, Name = "Mira check: video watch" };
            _thread.Start();
        }
        /// <summary>Mean brightness, every fourth pixel of every fourth row.</summary>
        private static int Light(Drawing.Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                long sum = 0, count = 0; var row = new int[bitmap.Width];
                for (var y = 0; y < bitmap.Height; y += 4)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < row.Length; x += 4) { var p = row[x]; sum += ((p >> 16 & 255) * 3 + (p >> 8 & 255) * 6 + (p & 255)) / 10; count++; }
                }
                return count == 0 ? 0 : (int)(sum / count);
            }
            finally { bitmap.UnlockBits(data); }
        }
        /// <summary>The brightness over time, a point where it changes (by more than 6) or the video's window shows or goes.</summary>
        public string Story()
        {
            var points = new List<string>(); (long Ms, int Light, bool Video)? last = null;
            foreach (var point in Screen)
            {
                if (last is { } before && Math.Abs(before.Light - point.Light) <= 6 && before.Video == point.Video) continue;
                points.Add($"{point.Ms} ms {point.Light}{(point.Video ? " video" : "")}"); last = point;
            }
            return string.Join(", ", points.Take(60));
        }
        public void Stop() { _stop = true; _thread.Join(); }
        /// <summary>The video's window shows something inside Mira's: visible, a picture left by its region, over the client area.</summary>
        private static bool Shows(IntPtr video, IntPtr window)
        {
            if (!IsWindowVisible(video) || !GetWindowRect(video, out var box)) return false;
            if (GetWindowRgnBox(video, out _) == 1) return false; // NULLREGION: concealed
            var origin = new WatchPoint(); ClientToScreen(window, ref origin);
            if (!GetClientRect(window, out var client)) return false;
            int left = Math.Max(box.Left, origin.X), right = Math.Min(box.Right, origin.X + client.Right);
            int top = Math.Max(box.Top, origin.Y), bottom = Math.Min(box.Bottom, origin.Y + client.Bottom);
            return right - left > 2 && bottom - top > 2;
        }
        [StructLayout(LayoutKind.Sequential)] private struct WatchPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct WatchRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out WatchRect rect);
        [DllImport("user32")] private static extern bool GetClientRect(IntPtr hwnd, out WatchRect rect);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hwnd, ref WatchPoint point);
        [DllImport("user32")] private static extern int GetWindowRgnBox(IntPtr hwnd, out WatchRect rect);
    }
}
