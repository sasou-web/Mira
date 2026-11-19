using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// Artwork from Jellyfin: one request per image, kept on disk (1 GB at most, the least recently used leave first) and
/// in memory while it is reused. Decoded at the width it is shown, never larger.
/// </summary>
public sealed class Images
{
    public const long DiskBudget = 1L << 30, DiskKeep = 768L << 20;
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    /// <summary>A title page's backdrop and logo have their own lane, never behind the posters and stills asked with them (as on Windows).</summary>
    private readonly SemaphoreSlim _pageDownloads = new(3);
    /// <summary>Cards hovered warm their page one at a time, in their own line: never ahead of an open page's backdrop and logo (as on Windows).</summary>
    private readonly SemaphoreSlim _warming = new(1);
    /// <summary>Raised by each hover and each page opened: a warm still waiting for its turn and no longer the latest is dropped.</summary>
    private int _warmGeneration;
    /// <summary>A title page opens: the warms still waiting are dropped (the one under way ends).</summary>
    public void DropWarming() => Interlocked.Increment(ref _warmGeneration);
    /// <summary>Backdrops already put on disk by <see cref="WarmPageAsync"/>, asked once per run.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _warmed = new();
    /// <summary>128 MB of decoded bitmaps kept for reuse, as on Windows; bitmaps on screen keep their own reference.</summary>
    private readonly ImageMemory<Bitmap> _memory = new(ImageMemory<Bitmap>.DefaultLimit, ImageMemory<Bitmap>.DefaultTarget, bitmap => (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);

    public Images(JellyfinClient client, string profileDirectory)
    {
        _client = client;
        _directory = AccountData.ImageFolder(profileDirectory, client.Connection); Directory.CreateDirectory(_directory);
        var root = Path.GetDirectoryName(_directory)!;
        _ = Task.Run(() => DiskCache.Trim(root, DiskBudget, DiskKeep));
    }

    /// <summary>The poster of a film or series; an episode shows its series' poster.</summary>
    public Task<Bitmap?> PosterAsync(MediaItem item, int width = 360) => item.Type == "Episode" && item.SeriesId is not null
        ? Get(new ImageRef(item.SeriesId, "Primary", item.SeriesPrimaryImageTag ?? ""), width)
        : Get(new ImageRef(item.Id, "Primary", item.ImageTags.GetValueOrDefault("Primary") ?? ""), width);
    /// <summary>A banner's or a title page's backdrop; <paramref name="ownPage"/>: an episode opened on purpose shows everything (see Artwork.Backdrop).</summary>
    public Task<Bitmap?> BackdropAsync(MediaItem item, int width = 1920, bool ownPage = false) => Get(Artwork.Backdrop(item, ownPage), width, first: ownPage);
    /// <summary>
    /// A card hovered a moment: its page's backdrop downloaded to the disk (not decoded, ~15 MB in memory, the posters would
    /// leave for it) and its logo decoded, so that the page opens on them instead of waiting for Jellyfin (as on Windows).
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
            var logo = item.ImageTags.ContainsKey("Logo") ? LogoAsync(item) : Task.FromResult<Bitmap?>(null);
            if (image.ItemId.Length > 0)
            {
                var key = Key(image, PageBackdropWidth);
                if (_warmed.TryAdd(key, 0)) await Task.Run(() => KeepAsync(key, image, PageBackdropWidth));
            }
            await logo;
        }
        finally { _warming.Release(); }
    }
    /// <summary>The width a title page asks its backdrop at (DetailPage).</summary>
    public const int PageBackdropWidth = 1920;
    /// <summary>A title page's backdrop, small (640 px): shown while the whole one downloads, as on Windows.</summary>
    public Task<Bitmap?> PageBackdropPreviewAsync(MediaItem item) => Get(Artwork.Backdrop(item, ownPage: true), 640, first: true);
    /// <summary>Whether the whole backdrop of a title page is on the disk already (seen before, or warmed on hover): then no preview.</summary>
    public Task<bool> PageBackdropKeptAsync(MediaItem item)
    {
        var image = Artwork.Backdrop(item, ownPage: true);
        if (image.ItemId.Length == 0) return Task.FromResult(true);
        var file = PathOf(Key(image, PageBackdropWidth));
        return Task.Run(() => File.Exists(file));
    }
    private async Task KeepAsync(string key, ImageRef image, int width)
    {
        var file = PathOf(key);
        if (File.Exists(file)) return;
        // In the hover's line (WarmPageAsync), not in the open page's.
        try { Keep(file, await _client.ImageAsync(image.ItemId, image.Type, width)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException) { _warmed.TryRemove(key, out _); }
    }
    /// <summary>A wide card: for an episode, art of its season or of the episode itself before the series' (see Artwork.Landscape).</summary>
    public Task<Bitmap?> LandscapeAsync(MediaItem item, int width = 560) => Get(Artwork.Landscape(item), width);
    public Task<Bitmap?> EpisodeAsync(MediaItem item, int width = 400)
    {
        var type = item.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        return Get(new ImageRef(item.Id, type, item.ImageTags.GetValueOrDefault(type) ?? ""), width);
    }
    public Task<Bitmap?> LogoAsync(MediaItem item)
    {
        var id = item.ImageTags.ContainsKey("Logo") ? item.Id : item.ParentLogoItemId ?? item.SeriesId;
        if (id is null || !item.ImageTags.ContainsKey("Logo") && item.ParentLogoImageTag is null) return Task.FromResult<Bitmap?>(null);
        return Get(new ImageRef(id, "Logo", item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? ""), 800, first: true);
    }

    private static string Key(ImageRef image, int width) => $"{image.ItemId}-{image.Type}-{image.Tag}-{width}";
    private string PathOf(string key) => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".img");
    private Task<Bitmap?> Get(ImageRef image, int width, bool first = false)
    {
        var key = Key(image, width);
        // All of it away from the interface's thread, the disk's copy included (its name, its reading): a page that
        // comes in asks for dozens at once.
        return _memory.Get(key, () => Task.Run(() => FetchAsync(key, image, width, first)));
    }
    private async Task<Bitmap?> FetchAsync(string key, ImageRef image, int width, bool first)
    {
        if (string.IsNullOrEmpty(image.ItemId)) return null;
        var file = PathOf(key);
        var lane = first ? _pageDownloads : _downloads;
        try
        {
            byte[] bytes;
            if (File.Exists(file)) { bytes = await File.ReadAllBytesAsync(file); DiskCache.Touch(file); }
            else
            {
                await lane.WaitAsync();
                try { bytes = await _client.ImageAsync(image.ItemId, image.Type, width); }
                finally { lane.Release(); }
                Keep(file, bytes);
            }
            return Decode(bytes, width);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException) { return null; }
    }
    /// <summary>At best: a full disk or a read-only folder loses the copy on disk, never the image, decoded from memory.</summary>
    private static void Keep(string file, byte[] bytes)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, file, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { try { File.Delete(temporary); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { } }
    }
    /// <summary>Null for data Skia cannot read (an error page, a damaged file), whatever it throws for it.</summary>
    private static Bitmap? Decode(byte[] bytes, int width)
    {
        try { using var stream = new MemoryStream(bytes); return Bitmap.DecodeToWidth(stream, width); }
        catch (Exception) { return null; }
    }
}
