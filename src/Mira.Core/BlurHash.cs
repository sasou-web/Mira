namespace Mira.Core;

/// <summary>
/// The BlurHash Jellyfin sends with each image (algorithm by Wolt, https://blurha.sh; the same decoder as Mira web's
/// <c>blurhash.js</c>): a few characters that Mira paints as a soft placeholder until the image itself arrives. Read
/// here, on the device, from what Jellyfin already returned: nothing is asked of anyone.
/// </summary>
public static class BlurHash
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
    /// <summary>The hash of this image of the item (by its type and tag), as Jellyfin sent it in <c>ImageBlurHashes</c>; null without one.</summary>
    public static string? Of(MediaItem item, ImageRef image) =>
        item.ImageBlurHashes is { } all && all.TryGetValue(image.Type, out var byTag) && byTag.TryGetValue(image.Tag, out var hash) && hash.Length >= 6 ? hash : null;
    /// <summary>
    /// The pixels, <paramref name="width"/> × <paramref name="height"/>, in BGRA order with an opaque alpha (what
    /// <c>PixelFormats.Bgra32</c> reads); null when the text is not a BlurHash.
    /// </summary>
    public static byte[]? Decode(string? hash, int width, int height)
    {
        if (string.IsNullOrEmpty(hash) || width is < 1 or > 256 || height is < 1 or > 256) return null;
        if (Number(hash, 0, 1) is not { } size) return null;
        int ny = size / 9 + 1, nx = size % 9 + 1;
        if (hash.Length != 4 + 2 * nx * ny || Number(hash, 1, 1) is not { } quantized || Number(hash, 2, 4) is not { } dc) return null;
        var max = (quantized + 1) / 166d;
        var colors = new double[nx * ny * 3];
        colors[0] = ToLinear(dc >> 16); colors[1] = ToLinear((dc >> 8) & 255); colors[2] = ToLinear(dc & 255);
        for (var i = 1; i < nx * ny; i++)
        {
            if (Number(hash, 4 + i * 2, 2) is not { } ac) return null;
            colors[i * 3] = SignPow((ac / 361 - 9) / 9d) * max;
            colors[i * 3 + 1] = SignPow((ac / 19 % 19 - 9) / 9d) * max;
            colors[i * 3 + 2] = SignPow((ac % 19 - 9) / 9d) * max;
        }
        // The cosines once per column and per row, not once per pixel: a page of posters decodes its placeholders as it builds.
        var cx = new double[width * nx]; var cy = new double[height * ny];
        for (var x = 0; x < width; x++) for (var i = 0; i < nx; i++) cx[x * nx + i] = Math.Cos(Math.PI * x * i / width);
        for (var y = 0; y < height; y++) for (var j = 0; j < ny; j++) cy[y * ny + j] = Math.Cos(Math.PI * y * j / height);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double r = 0, g = 0, b = 0;
                for (var j = 0; j < ny; j++)
                {
                    var row = cy[y * ny + j];
                    for (var i = 0; i < nx; i++)
                    {
                        var basis = cx[x * nx + i] * row; var c = (i + j * nx) * 3;
                        r += colors[c] * basis; g += colors[c + 1] * basis; b += colors[c + 2] * basis;
                    }
                }
                var p = 4 * (x + y * width);
                pixels[p] = ToSrgb(b); pixels[p + 1] = ToSrgb(g); pixels[p + 2] = ToSrgb(r); pixels[p + 3] = 255;
            }
        }
        return pixels;
    }
    /// <summary>
    /// The size to decode for an image shown with this aspect (width over height): <paramref name="longSide"/> pixels on
    /// its long side. Stretched over the card it is as soft as a larger one, for far less work.
    /// </summary>
    public static (int Width, int Height) Size(double aspect, int longSide = 32)
    {
        if (!double.IsFinite(aspect) || aspect <= 0) aspect = 1;
        return aspect >= 1 ? (longSide, Math.Max(4, (int)Math.Round(longSide / aspect))) : (Math.Max(4, (int)Math.Round(longSide * aspect)), longSide);
    }
    private static int? Number(string text, int start, int length)
    {
        var value = 0;
        for (var k = start; k < start + length; k++)
        {
            var digit = Digits.IndexOf(text[k]);
            if (digit < 0) return null;
            value = value * 83 + digit;
        }
        return value;
    }
    private static double ToLinear(int v) { var c = v / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
    private static byte ToSrgb(double v)
    {
        var c = Math.Clamp(v, 0, 1);
        return (byte)Math.Round((c <= .0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - .055) * 255, MidpointRounding.AwayFromZero);
    }
    private static double SignPow(double v) => Math.Sign(v) * v * v;
}
