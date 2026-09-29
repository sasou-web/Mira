using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mira.Core;

namespace Mira.Desktop.Services;

public static class ArtworkAccent
{
    private sealed record Palette(Color Color);
    private static readonly ConditionalWeakTable<BitmapSource, Palette> Cache = new();
    public static Color From(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return Color.FromRgb(245, 245, 247);
        return Cache.GetValue(bitmap, image =>
        {
            var sample = new TransformedBitmap(image, new ScaleTransform(64d / image.PixelWidth, 48d / image.PixelHeight));
            var formatted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
            var bytes = new byte[formatted.PixelWidth * formatted.PixelHeight * 4]; formatted.CopyPixels(bytes, formatted.PixelWidth * 4, 0);
            var color = ArtworkPalette.Extract(bytes); return new(Color.FromRgb(color.R, color.G, color.B));
        }).Color;
    }
}
