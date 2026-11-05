using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mira.Checks;

internal sealed partial class StandInJellyfin
{
    private static partial byte[] Encode(byte[] pixels, int width, int height, bool png)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, png ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, width * 4);
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream);
        return stream.ToArray();
    }
}
