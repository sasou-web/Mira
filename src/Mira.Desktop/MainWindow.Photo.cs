using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop;

// This account's photo (Réglages → Jellyfin, and the rail's account button): kept by Jellyfin, so it follows the
// account on every device. A picture chosen here is turned as the camera held it, cut square around its middle and
// sent as a JPEG of 512 px at most: nothing else of the file (name, place, date) leaves this PC.
public partial class MainWindow
{
    private const int PhotoSide = 512;
    private bool _photoBusy;

    /// <summary>The photo of the account now signed in, read once per connection and after each change.</summary>
    private async Task ShowPhotoAsync()
    {
        if (_demo || _client is not { } client) { PhotoRow.Visibility = Visibility.Collapsed; SetPhoto(null); return; }
        PhotoLetter.Text = AvatarLetter.Text; PhotoRow.Visibility = Visibility.Visible;
        byte[]? bytes;
        try { bytes = await client.PhotoAsync(PhotoSide, _connectionLifetime.Token); }
        catch (Exception ex) when (IsExpected(ex)) { return; }
        if (!ReferenceEquals(client, _client)) return;
        SetPhoto(bytes);
    }
    private void SetPhoto(byte[]? bytes)
    {
        ImageBrush? brush = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 160; image.StreamSource = new MemoryStream(bytes); image.EndInit(); image.Freeze();
                brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill }; brush.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException) { brush = null; }
        }
        AvatarPhoto.Fill = PhotoImage.Fill = brush;
        AvatarPhoto.Visibility = PhotoImage.Visibility = PhotoRemoveButton.Visibility = brush is null ? Visibility.Collapsed : Visibility.Visible;
        PhotoChooseButton.Content = brush is null ? L("Choisir une photo…", "Choose a photo…") : L("Changer de photo…", "Change photo…");
    }
    private void SayPhoto(string text) { PhotoStatus.Text = text; PhotoStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }

    private async void ChoosePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _photoBusy) return;
        var dialog = new OpenFileDialog { Title = L("Choisir ta photo", "Choose your photo"), Filter = L("Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp|Tous les fichiers|*.*", "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.webp|All files|*.*") };
        if (dialog.ShowDialog(this) != true) return;
        byte[] jpeg;
        try { jpeg = SquareJpeg(dialog.FileName); }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { SayPhoto(L("Cette image ne s’ouvre pas. Choisis une photo en JPEG ou en PNG.", "This picture won’t open. Choose a photo in JPEG or PNG.")); return; }
        await ChangePhotoAsync(client, () => client.SetPhotoAsync(jpeg, _connectionLifetime.Token), L("Photo changée.", "Photo changed."));
    }
    private async void RemovePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not { } client || _photoBusy) return;
        await ChangePhotoAsync(client, () => client.RemovePhotoAsync(_connectionLifetime.Token), L("Photo retirée.", "Photo removed."));
    }
    private async Task ChangePhotoAsync(JellyfinClient client, Func<Task> change, string done)
    {
        _photoBusy = true; PhotoChooseButton.IsEnabled = PhotoRemoveButton.IsEnabled = false; SayPhoto(L("Envoi à Jellyfin…", "Sending to Jellyfin…"));
        var ct = _connectionLifetime.Token;
        try
        {
            await change();
            if (!ReferenceEquals(client, _client)) return;
            SayPhoto(done);
            await ShowPhotoAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpected(ex)) { SayPhoto(Friendly(ex)); }
        finally { _photoBusy = false; PhotoChooseButton.IsEnabled = PhotoRemoveButton.IsEnabled = true; }
    }

    /// <summary>A picture made a square JPEG of <see cref="PhotoSide"/> px at most, turned as its EXIF says.</summary>
    internal static byte[] SquareJpeg(string path)
    {
        BitmapFrame frame;
        using (var file = File.OpenRead(path)) frame = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource source = frame;
        // EXIF orientation (274): 3 = upside down, 6 = turned right, 8 = turned left; mirrored ones are rare from a camera.
        var orientation = 1;
        try { if ((frame.Metadata as BitmapMetadata)?.GetQuery("/app1/ifd/{ushort=274}") is ushort value) orientation = value; }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        var angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle != 0) source = new TransformedBitmap(source, new RotateTransform(angle));
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        source = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        if (side > PhotoSide) source = new TransformedBitmap(source, new ScaleTransform((double)PhotoSide / side, (double)PhotoSide / side));
        // JPEG has no transparency: a PNG's transparent parts become black, not random.
        if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Bgr32) source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
