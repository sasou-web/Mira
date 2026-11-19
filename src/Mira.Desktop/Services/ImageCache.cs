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
    /// <summary>An episode's still in a series' grid (EpisodeTiles), whatever its tile's width up to it: one size kept.</summary>
    public const double EpisodeTileWidth = 420;
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    /// <summary>
    /// A title page's backdrop and logo have their own lane: they never wait behind the posters and stills a page asks for
    /// at the same time (they came last on the Dell, 2026-11-18).
    /// </summary>
    private readonly SemaphoreSlim _pageDownloads = new(3);
    /// <summary>
    /// Cards hovered warm their page one at a time, in their own line: never ahead of an open page's backdrop and logo
    /// (audit, 2026-11-19: twenty posters hovered, the page then waited behind them, its name shown before its logo).
    /// </summary>
    private readonly SemaphoreSlim _warming = new(1);
    /// <summary>Raised by each hover and each page opened: a warm still waiting for its turn and no longer the latest is dropped.</summary>
    private int _warmGeneration;
    /// <summary>A title page opens: the warms still waiting are dropped (the one under way ends).</summary>
    public void DropWarming() => Interlocked.Increment(ref _warmGeneration);
    /// <summary>Backdrops already put on disk by <see cref="WarmPageAsync"/>, asked once per run.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warmed = new();
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
    /// <summary>A title page's backdrop: an episode opened on purpose shows everything, its own still too (see Artwork.Backdrop).</summary>
    public Task<BitmapSource?> PageBackdropAsync(MediaItem item) => Load(Artwork.Backdrop(item, ownPage: true), BackdropPixels, first: true);
    /// <summary>The width of a title page's first, quick backdrop, while the whole one comes (a tenth of its bytes).</summary>
    public const int BackdropPreviewPixels = 640;
    /// <summary>A title page's backdrop, small: shown while the whole one downloads (2026-11-18, the Dell: 0.4 to 1 s for it).</summary>
    public Task<BitmapSource?> PageBackdropPreviewAsync(MediaItem item) => Load(Artwork.Backdrop(item, ownPage: true), BackdropPreviewPixels, first: true);
    /// <summary>Whether the whole backdrop of a title page is on the disk already (seen before, or warmed on hover): then no preview.</summary>
    public Task<bool> PageBackdropKeptAsync(MediaItem item)
    {
        var image = Artwork.Backdrop(item, ownPage: true);
        if (image.ItemId.Length == 0) return Task.FromResult(true);
        var path = PathOf($"{image.ItemId}-{image.Type}-{image.Tag}-{BackdropPixels}");
        return Task.Run(() => File.Exists(path));
    }
    /// <summary>
    /// A card hovered a moment: its page's backdrop downloaded to the disk (not decoded: ~15 MB each in memory, the posters
    /// would leave for it) and its logo decoded, so that the page opens on them instead of waiting for Jellyfin.
    /// </summary>
    public async Task WarmPageAsync(MediaItem item)
    {
        var mine = Interlocked.Increment(ref _warmGeneration);
        await _warming.WaitAsync();
        try
        {
            // Another card hovered since, or a page opened: its turn is given up.
            if (mine != Volatile.Read(ref _warmGeneration)) return;
            var image = Artwork.Backdrop(item, ownPage: true);
            var width = BackdropPixels;
            var logo = item.ImageTags.ContainsKey("Logo") ? LogoAsync(item) : Task.FromResult<BitmapSource?>(null);
            if (image.ItemId.Length > 0)
            {
                var key = $"{image.ItemId}-{image.Type}-{image.Tag}-{width}";
                if (_warmed.TryAdd(key, 0)) await Task.Run(() => KeepAsync(key, image, width));
            }
            await logo;
        }
        finally { _warming.Release(); }
    }
    /// <summary>Downloaded to the disk only, in the hover's line (<see cref="_warming"/>), not in the open page's.</summary>
    private async Task KeepAsync(string key, ImageRef image, int width)
    {
        var path = PathOf(key);
        if (File.Exists(path)) return;
        try { await KeepOnDiskAsync(path, await _client.ImageAsync(image.ItemId, image.Type, width)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException) { _warmed.TryRemove(key, out _); }
    }
    /// <summary>A wide card: for an episode, art of its season or of the episode itself before the series' (see Artwork.Landscape).</summary>
    public Task<BitmapSource?> LandscapeAsync(MediaItem item, int width) => Load(Artwork.Landscape(item), width);
    // No picture (Artwork.None): nothing asked, Jellyfin would answer with the item's own picture whatever the tag.
    private Task<BitmapSource?> Load(ImageRef image, int width, bool first = false) => image.ItemId.Length == 0 ? Task.FromResult<BitmapSource?>(null)
        : Cached($"{image.ItemId}-{image.Type}-{image.Tag}-{width}", image.ItemId, image.Type, width, first);
    public Task<BitmapSource?> LogoAsync(MediaItem item)
    {
        var id = item.ImageTags.ContainsKey("Logo") ? item.Id : item.ParentLogoItemId ?? item.SeriesId;
        if (id is null) return Task.FromResult<BitmapSource?>(null);
        var tag = item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? "";
        var width = Pixels(LogoWidth);
        return Cached($"{id}-Logo-{tag}-{width}", id, "Logo", width, first: true);
    }
    /// <param name="shown">The width it is shown at: <see cref="EpisodeWidth"/> in a list, <see cref="EpisodeTileWidth"/> in a series' grid.</param>
    public Task<BitmapSource?> EpisodeAsync(MediaItem item, double shown = EpisodeWidth)
    {
        var type = item.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        var width = Pixels(shown);
        return Cached($"{item.Id}-{type}-{item.ImageTags.GetValueOrDefault(type)}-{width}", item.Id, type, width);
    }
    /// <summary>
    /// One request per image, shared by every caller; the least recently used leave past the budget. Fetched away from
    /// the window's thread from the start: its file checks, opened and read, each held that thread a little while a page
    /// opened or scrolled (more with an antivirus that reads each file opened).
    /// </summary>
    private Task<BitmapSource?> Cached(string key, string id, string type, int width, bool first = false) => Memory.Get(key, () => Task.Run(() => FetchAsync(key, id, type, width, first)));
    private string PathOf(string key) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".img");
    private static long Size(BitmapSource bitmap) => (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
    private async Task<BitmapSource?> FetchAsync(string key, string id, string type, int width, bool first)
    {
        var lane = first ? _pageDownloads : _downloads;
        await lane.WaitAsync();
        try
        {
            var path = PathOf(key);
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
                    return (BitmapSource)PictureBytes.Decode(bytes, original > 0 ? Math.Min(width, original) : width);
                });
            }
            // A damaged file is removed so the next request downloads it again.
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException) { TryDelete(path); throw new IOException("Image illisible.", ex); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException or UnauthorizedAccessException) { return null; }
        finally { lane.Release(); }
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
