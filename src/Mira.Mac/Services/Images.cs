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
    public Task<Bitmap?> BackdropAsync(MediaItem item, int width = 1920) => Get(Artwork.Backdrop(item), width);
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
        return Get(new ImageRef(id, "Logo", item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? ""), 800);
    }

    private Task<Bitmap?> Get(ImageRef image, int width)
    {
        var key = $"{image.ItemId}-{image.Type}-{image.Tag}-{width}";
        return _memory.Get(key, () => FetchAsync(key, image, width));
    }
    private async Task<Bitmap?> FetchAsync(string key, ImageRef image, int width)
    {
        if (string.IsNullOrEmpty(image.ItemId)) return null;
        var file = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".img");
        try
        {
            byte[] bytes;
            if (File.Exists(file)) { bytes = await File.ReadAllBytesAsync(file); DiskCache.Touch(file); }
            else
            {
                await _downloads.WaitAsync();
                try { bytes = await _client.ImageAsync(image.ItemId, image.Type, width); }
                finally { _downloads.Release(); }
                // At best: a full disk or a read-only folder loses the copy on disk, never the image, decoded from memory.
                var temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, file, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { try { File.Delete(temporary); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { } }
            }
            return await Task.Run(() => Decode(bytes, width));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException) { return null; }
    }
    /// <summary>Null for data Skia cannot read (an error page, a damaged file), whatever it throws for it.</summary>
    private static Bitmap? Decode(byte[] bytes, int width)
    {
        try { using var stream = new MemoryStream(bytes); return Bitmap.DecodeToWidth(stream, width); }
        catch (Exception) { return null; }
    }
}
