using SkiaSharp;

namespace Mira.Checks;

internal sealed partial class StandInJellyfin
{
    private static partial byte[] Encode(byte[] pixels, int width, int height, bool png)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, png ? SKAlphaType.Premul : SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), width * 4 * height);
        using var data = bitmap.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 85);
        return Kept(data.AsSpan());
    }
}
