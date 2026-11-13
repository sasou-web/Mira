using System.IO;
using System.Windows.Media.Imaging;

namespace Mira.Desktop.Services;

/// <summary>
/// A picture's file, for its decoder to read once (BitmapCacheOption.OnLoad), let go of once closed. A BitmapImage keeps
/// its StreamSource for as long as it lives, and a MemoryStream its buffer even closed: each picture in memory kept its
/// whole file beside its pixels (posters, backdrops, stills, trickplay tiles), managed bytes that every collection then
/// had to keep and move. On a 4K 240 Hz screen, the first collection after a fast scroll through new pages kept 20 to
/// 27 MB and held a frame 9 to 26 ms.
/// </summary>
internal sealed class PictureBytes(byte[] bytes) : Stream
{
    /// <summary>The picture in <paramref name="bytes"/>, decoded <paramref name="pixelWidth"/> wide and frozen, without its file.</summary>
    internal static BitmapImage Decode(byte[] bytes, int pixelWidth)
    {
        using var stream = new PictureBytes(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = pixelWidth; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    private byte[] _bytes = bytes;
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _bytes.Length;
    public override long Position { get => _position; set { ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
    public override int Read(byte[] buffer, int offset, int count) { ValidateBufferArguments(buffer, offset, count); return Read(buffer.AsSpan(offset, count)); }
    public override int Read(Span<byte> buffer)
    {
        var left = _bytes.Length - _position;
        if (left <= 0 || buffer.IsEmpty) return 0;
        var count = (int)Math.Min(left, buffer.Length);
        _bytes.AsSpan((int)_position, count).CopyTo(buffer); _position += count;
        return count;
    }
    public override int ReadByte() => _position < _bytes.Length ? _bytes[_position++] : -1;
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        SeekOrigin.End => _bytes.Length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _bytes = []; _position = 0; base.Dispose(disposing); }
}
