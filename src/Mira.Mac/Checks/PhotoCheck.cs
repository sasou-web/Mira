using Mira.Mac.Views;
using SkiaSharp;

namespace Mira.Mac.Checks;

/// <summary>
/// The account's photo picked from an iPhone's HEIC (--photos: tools/mac/check, 1200 × 900, red, green and blue thirds
/// under a white band; « photo-turned » kept as the sensor saw it and turned for display by HEIF's irot and EXIF's
/// orientation 6, as an iPhone held upright writes it): made the square JPEG sent to Jellyfin, then read back, each colour
/// where it belongs. Skia alone is said too: the Skia of the Mac has no HEIF. Off macOS, nothing reads HEIC: only said.
/// </summary>
internal static class PhotoCheck
{
    private static readonly SKColor Red = new(220, 30, 30), Green = new(30, 200, 30), Blue = new(30, 30, 220), White = new(250, 250, 250);

    public static void Run(string folder, Action<bool, string, string> check)
    {
        foreach (var (file, turned) in new[] { ("photo.heic", false), ("photo-turned.heic", true) })
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, file));
            if (!turned)
            {
                using var alone = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(alone);
                check(true, $"(info) photo : Skia seul {(codec is null ? "ne lit pas" : "lit")} le HEIC", "");
            }
            var name = $"photo : {file} devient la photo du compte" + (turned ? ", tournée comme sur l’iPhone" : "");
            if (!OperatingSystem.IsMacOS()) { check(true, "(info) " + name + " : macOS seulement", ""); continue; }
            byte[] jpeg;
            try { jpeg = MainWindow.SquareJpeg(new MemoryStream(bytes)); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { check(false, name, ex.Message); continue; }
            using var image = SKBitmap.Decode(jpeg);
            if (image is null) { check(false, name, "JPEG illisible"); continue; }
            // The 512 px square cut around the middle: thirds from left to right, band on top; turned, from top to
            // bottom, band on the right.
            (int X, int Y, SKColor Colour)[] marks = turned
                ? [(256, 70, Red), (256, 256, Green), (256, 440, Blue), (490, 256, White)]
                : [(70, 300, Red), (256, 300, Green), (440, 300, Blue), (256, 25, White)];
            var square = image.Width == 512 && image.Height == 512;
            List<string> wrong = !square ? [] : marks.Where(m => !Near(image.GetPixel(m.X, m.Y), m.Colour)).Select(m => $"({m.X}, {m.Y}) {image.GetPixel(m.X, m.Y)} au lieu de {m.Colour}").ToList();
            check(square && wrong.Count == 0, name, $"{image.Width} × {image.Height}" + (wrong.Count > 0 ? " · " + string.Join(", ", wrong) : ""));
        }
    }

    // Two lossy encodings (HEIC, then the JPEG sent) and a resampling: near, not equal.
    private static bool Near(SKColor seen, SKColor wanted) =>
        Math.Abs(seen.Red - wanted.Red) <= 40 && Math.Abs(seen.Green - wanted.Green) <= 40 && Math.Abs(seen.Blue - wanted.Blue) <= 40;
}
