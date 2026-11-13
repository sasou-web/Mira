using System.Buffers;
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
        // Written into a borrowed buffer as large as the picture's pixels (more than its file ever takes): a growing stream
        // and its copy were garbage several times the file's size, the check's own, that Mira's collections paid for.
        var buffer = ArrayPool<byte>.Shared.Rent(width * height * 4 + 65536);
        try
        {
            using var stream = new MemoryStream(buffer, 0, buffer.Length, writable: true, publiclyVisible: true);
            stream.SetLength(0); encoder.Save(stream);
            return Kept(buffer.AsSpan(0, (int)stream.Length));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
