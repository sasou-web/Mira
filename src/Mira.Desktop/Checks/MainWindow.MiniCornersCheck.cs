using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using Mira.Desktop.Playback;
using Drawing = System.Drawing;

namespace Mira.Desktop;

// Player fixture: the mini-player's corners as the screen shows them. A window region keeps or drops whole pixels, in
// steps (0.9.9: « les bords du mini-player sont pas lisses ») ; Windows' compositor now draws the curve over the video.
public partial class MainWindow
{
    private async Task CheckMiniCornersAsync(string output, List<string> checks, Action<bool, string> require)
    {
        if (_videoHost is not { } video) return;
        if (video.Corners is null) { checks.Add("INFO no compositor layer on this Windows: the mini-player's corners are the region's"); return; }
        await Task.Delay(300);
        var window = new WindowInteropHelper(this).Handle;
        GetWindowRect(video.Handle, out var box);
        var radius = (int)Math.Round(MiniRadius * VisualTreeHelper.GetDpi(this).DpiScaleX);
        // Only what the screen shows: Mira's window, there, in front.
        var probe = new CornerSpot { X = box.Left + radius, Y = box.Top + radius };
        if (CornerAncestor(CornerWindowAt(probe), 2) != window) { checks.Add("INFO the mini-player's corner is not on the screen here: its corners are not measured"); return; }
        const int margin = 10; var size = radius + margin;
        using var shot = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(shot)) graphics.CopyFromScreen(box.Left - margin, box.Top - margin, 0, 0, new Drawing.Size(size, size));
        // Eight times larger, pixel by pixel, to look at.
        using (var large = new Drawing.Bitmap(size * 8, size * 8))
        {
            using (var graphics = Drawing.Graphics.FromImage(large))
            {
                graphics.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor; graphics.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
                graphics.DrawImage(shot, 0, 0, size * 8, size * 8);
            }
            large.Save(Path.Combine(output, "mini-corner.png"), Drawing.Imaging.ImageFormat.Png);
        }
        // Past the curve by more than a pixel: the page, never the picture. Well inside the band: the picture.
        int outside = 0, stray = 0, inside = 0, picture = 0;
        for (var y = 0; y < radius; y++)
            for (var x = 0; x < radius; x++)
            {
                var distance = Math.Sqrt(Math.Pow(radius - (x + .5), 2) + Math.Pow(radius - (y + .5), 2));
                var pixel = shot.GetPixel(x + margin, y + margin);
                var colourful = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) > 40;
                if (distance > radius + 1) { outside++; if (colourful) stray++; }
                else if (distance < radius - VideoCorners.Band - 1.5) { inside++; if (colourful) picture++; }
            }
        checks.Add($"INFO mini-player corner on screen ({radius} px radius): {stray} of {outside} pixels past the curve show the picture, {picture} of {inside} inside it");
        if (picture * 2 > inside) require(stray == 0, $"the mini-player's corner is cut at its curve, no step of the picture past it ({stray} of {outside} pixels)");
        else checks.Add("INFO the picture is dark at the mini-player's corner here: only the capture shows its curve");
    }
    [StructLayout(LayoutKind.Sequential)] private struct CornerSpot { public int X, Y; }
    [DllImport("user32", EntryPoint = "WindowFromPoint")] private static extern IntPtr CornerWindowAt(CornerSpot point);
    [DllImport("user32", EntryPoint = "GetAncestor")] private static extern IntPtr CornerAncestor(IntPtr window, uint flags);
}
