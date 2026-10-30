using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Mira.Core;
using Mira.Mac.Services;
using SkiaSharp;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

// This account's photo, as on Windows (the rail's account button, Réglages → Compte): kept by Jellyfin, so it follows
// the account on every device. A picture chosen here is turned as the camera held it, cut square around its middle and
// sent as a JPEG of 512 px at most: nothing else of the file (name, place, date) leaves this Mac.
public sealed partial class MainWindow
{
    private const int PhotoSide = 512;
    /// <summary>The photo shown in the rail and the settings; null without one.</summary>
    public Bitmap? Photo { get; private set; }
    public event Action? PhotoChanged;

    /// <summary>Read once per sign-in and after each change.</summary>
    public async Task ShowPhotoAsync()
    {
        if (Session is not { } session) { SetPhoto(null); return; }
        byte[]? bytes;
        try { bytes = await session.Client.PhotoAsync(PhotoSide, session.Lifetime); }
        catch (Exception ex) when (Errors.Expected(ex)) { return; }
        if (!ReferenceEquals(session, Session)) return;
        SetPhoto(bytes);
    }
    private void SetPhoto(byte[]? bytes)
    {
        Bitmap? bitmap = null;
        if (bytes is { Length: > 0 })
        {
            try { using var stream = new MemoryStream(bytes); bitmap = new Bitmap(stream); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException) { bitmap = null; }
        }
        Photo = bitmap;
        _avatarPhoto.Fill = bitmap is null ? null : new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        _avatarPhoto.IsVisible = bitmap is not null;
        PhotoChanged?.Invoke();
    }
    /// <summary>« Choisir une photo… »: the file picked, made a square JPEG, sent; the error said in words.</summary>
    public async Task<string?> ChoosePhotoAsync()
    {
        if (Session is not { } session) return null;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = L("Choisir ta photo", "Choose your photo"), AllowMultiple = false, FileTypeFilter = [FilePickerFileTypes.ImageAll] });
        if (files.Count == 0) return null;
        byte[] jpeg;
        try { await using var stream = await files[0].OpenReadAsync(); jpeg = SquareJpeg(stream); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return L("Cette image ne s’ouvre pas. Choisis une photo en JPEG, PNG ou HEIC.", "This picture won’t open. Choose a photo in JPEG, PNG or HEIC."); }
        return await ChangePhotoAsync(session, () => session.Client.SetPhotoAsync(jpeg, session.Lifetime), L("Photo changée.", "Photo changed."));
    }
    public Task<string?> RemovePhotoAsync() =>
        Session is { } session ? ChangePhotoAsync(session, () => session.Client.RemovePhotoAsync(session.Lifetime), L("Photo retirée.", "Photo removed.")) : Task.FromResult<string?>(null);
    private async Task<string?> ChangePhotoAsync(Session session, Func<Task> change, string done)
    {
        try { await change(); }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { return null; }
        catch (Exception ex) when (Errors.Expected(ex)) { return Errors.Friendly(ex); }
        await ShowPhotoAsync();
        return done;
    }

    /// <summary>A picture made a square JPEG of <see cref="PhotoSide"/> px at most, turned as its EXIF says.</summary>
    internal static byte[] SquareJpeg(Stream stream)
    {
        using var data = SKData.Create(stream) ?? throw new InvalidOperationException("unreadable");
        using var codec = SKCodec.Create(data) ?? throw new NotSupportedException("format");
        using var decoded = SKBitmap.Decode(codec) ?? throw new NotSupportedException("format");
        var side = Math.Min(decoded.Width, decoded.Height);
        var size = Math.Min(PhotoSide, side);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        // The camera's orientation: drawn turned around the middle of the square.
        canvas.Translate(size / 2f, size / 2f);
        canvas.RotateDegrees(codec.EncodedOrigin switch { SKEncodedOrigin.BottomRight => 180, SKEncodedOrigin.RightTop => 90, SKEncodedOrigin.LeftBottom => 270, _ => 0 });
        canvas.Translate(-size / 2f, -size / 2f);
        var source = SKRect.Create((decoded.Width - side) / 2f, (decoded.Height - side) / 2f, side, side);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(decoded, source, SKRect.Create(0, 0, size, size), paint);
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return jpeg.ToArray();
    }
}
