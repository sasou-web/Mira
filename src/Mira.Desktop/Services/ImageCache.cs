using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Mira.Core;

namespace Mira.Desktop.Services;

public sealed class ImageCache
{
    /// <summary>Artwork of every account on this PC, on disk: trimmed back to <see cref="DiskKeep"/> past this size.</summary>
    public const long DiskBudget = 1L << 30, DiskKeep = 768L << 20;
    /// <summary>
    /// Decoded bitmaps kept for reuse, emptied when the window is minimized (a 1920 px backdrop weighs ~8 MB, a poster
    /// shown at 150 % ~0.6 MB). Cards on screen keep their own.
    /// </summary>
    public const long MemoryBudget = ImageMemory<BitmapSource>.DefaultLimit, MemoryKeep = ImageMemory<BitmapSource>.DefaultTarget;
    /// <summary>The widest a poster card gets, in device-independent units (see MainWindow.PosterLayout).</summary>
    public const double PosterWidth = 230;
    /// <summary>The episode thumbnails of a title page, and the banner's logo at most.</summary>
    public const double EpisodeWidth = 160, LogoWidth = 540;
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    private ImageMemory<BitmapSource>? _memory;
    /// <summary>Made on first use, once the init-only budget is known.</summary>
    private ImageMemory<BitmapSource> Memory => LazyInitializer.EnsureInitialized(ref _memory, () => new(MemoryLimit, MemoryTarget, Size));
    public ImageCache(JellyfinClient client, string directory)
    {
        _client = client;
        _directory = AccountData.ImageFolder(directory, client.Connection); Directory.CreateDirectory(_directory);
        var root = Path.GetDirectoryName(_directory)!;
        // Artwork replaced on the server, or of an account no longer used, leaves with the oldest files.
        _ = Task.Run(() => DiskCache.Trim(root, DiskBudget, DiskKeep));
    }
    /// <summary>Estimated size of the decoded bitmaps held for reuse.</summary>
    internal long MemoryBytes => Memory.Bytes;
    /// <summary>Requests finished and accounted for.</summary>
    internal int Settled => Memory.Settled;
    internal long MemoryLimit { get; init; } = MemoryBudget;
    internal long MemoryTarget { get; init; } = MemoryKeep;
    /// <summary>
    /// The highest scale of the screens connected (1.5 at 150 %), so that a card moved to another screen stays sharp, and
    /// the widest of them in pixels, which a backdrop covers at most.
    /// </summary>
    public double Scale { get; set; } = 1;
    public int ScreenPixels { get; set; } = 1920;
    /// <summary>The width, in pixels, to decode an image shown <paramref name="width"/> units wide.</summary>
    public int Pixels(double width, int max = 1280) => Artwork.Pixels(width, Scale, max);
    /// <summary>Backdrops cover the widest screen; 2560 px at most, as before.</summary>
    private int BackdropPixels => Artwork.Pixels(ScreenPixels, 1, 2560);
    /// <summary>Lets go of the bitmaps held for reuse: those on screen stay, and the others come back from the disk.</summary>
    public void ReleaseMemory() => Memory.Clear();
    public Task<BitmapSource?> GetAsync(MediaItem item, bool backdrop = false, int? targetWidth = null)
    {
        var image = backdrop ? Artwork.Backdrop(item) : Artwork.Poster(item);
        return Load(image, targetWidth ?? (backdrop ? BackdropPixels : Pixels(PosterWidth)));
    }
    /// <summary>A wide card: for an episode, art of its season or of the episode itself before the series' (see Artwork.Landscape).</summary>
    public Task<BitmapSource?> LandscapeAsync(MediaItem item, int width) => Load(Artwork.Landscape(item), width);
    private Task<BitmapSource?> Load(ImageRef image, int width) => Cached($"{image.ItemId}-{image.Type}-{image.Tag}-{width}", image.ItemId, image.Type, width);
    public Task<BitmapSource?> LogoAsync(MediaItem item)
    {
        var id = item.ImageTags.ContainsKey("Logo") ? item.Id : item.ParentLogoItemId ?? item.SeriesId;
        if (id is null) return Task.FromResult<BitmapSource?>(null);
        var tag = item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? "";
        var width = Pixels(LogoWidth);
        return Cached($"{id}-Logo-{tag}-{width}", id, "Logo", width);
    }
    public Task<BitmapSource?> EpisodeAsync(MediaItem item)
    {
        var type = item.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        var width = Pixels(EpisodeWidth);
        return Cached($"{item.Id}-{type}-{item.ImageTags.GetValueOrDefault(type)}-{width}", item.Id, type, width);
    }
    /// <summary>One request per image, shared by every caller; the least recently used leave past the budget.</summary>
    private Task<BitmapSource?> Cached(string key, string id, string type, int width) => Memory.Get(key, () => FetchAsync(key, id, type, width));
    private static long Size(BitmapSource bitmap) => (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
    private async Task<BitmapSource?> FetchAsync(string key, string id, string type, int width)
    {
        await _downloads.WaitAsync();
        try
        {
            var filename = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".img";
            var path = Path.Combine(_directory, filename);
            byte[]? bytes = null;
            // The file may have just been trimmed away: then it is downloaded again.
            if (File.Exists(path)) try { bytes = await File.ReadAllBytesAsync(path); } catch (IOException) { }
            var cached = bytes is not null;
            bytes ??= await _client.ImageAsync(id, type, width);
            // A full disk or a read-only folder loses the copy on disk, never the image: it is decoded from memory.
            if (!cached) await KeepOnDiskAsync(path, bytes);
            try
            {
                return await Task.Run(() =>
                {
                    if (cached) DiskCache.Touch(path);
                    // Never decoded wider than the file itself: a 400 px logo asked at 640 stays 400 px.
                    int original;
                    using (var header = new MemoryStream(bytes)) original = BitmapDecoder.Create(header, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = original > 0 ? Math.Min(width, original) : width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return (BitmapSource)bitmap;
                });
            }
            // A damaged file is removed so the next request downloads it again.
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException) { TryDelete(path); throw new IOException("Image illisible.", ex); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException or UnauthorizedAccessException) { return null; }
        finally { _downloads.Release(); }
    }
    /// <summary>Written beside, then moved: an interrupted write can never leave a truncated image behind. At best: false when it could not be kept.</summary>
    internal static async Task<bool> KeepOnDiskAsync(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, path, overwrite: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { TryDelete(temporary); return false; }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
