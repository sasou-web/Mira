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
    private const long MemoryBudget = 384L << 20;
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly Dictionary<string, (Task<Bitmap?> Image, long LastUse, long Bytes)> _memory = [];
    private readonly object _gate = new();
    private long _clock, _bytes;

    public Images(JellyfinClient client, string profileDirectory)
    {
        _client = client;
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Connection.Server + client.Connection.UserId)))[..16];
        var root = Path.Combine(profileDirectory, "images");
        _directory = Path.Combine(root, account); Directory.CreateDirectory(_directory);
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
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var hit)) { _memory[key] = hit with { LastUse = ++_clock }; return hit.Image; }
            var task = FetchAsync(key, image, width);
            _memory[key] = (task, ++_clock, 0);
            _ = task.ContinueWith(done => Settle(key, task, done.IsCompletedSuccessfully ? done.Result : null), TaskScheduler.Default);
            return task;
        }
    }
    private void Settle(string key, Task<Bitmap?> task, Bitmap? bitmap)
    {
        lock (_gate)
        {
            if (!_memory.TryGetValue(key, out var entry) || entry.Image != task) return;
            // A failed image is asked again next time instead of staying missing.
            if (bitmap is null) { _memory.Remove(key); return; }
            var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
            _memory[key] = entry with { Bytes = bytes }; _bytes += bytes;
            if (_bytes <= MemoryBudget) return;
            // Bitmaps on screen keep their own reference: forgetting them here only stops reuse.
            foreach (var old in _memory.OrderBy(x => x.Value.LastUse).ToList())
            {
                if (_bytes <= MemoryBudget * 3 / 4) break;
                _memory.Remove(old.Key); _bytes -= old.Value.Bytes;
            }
        }
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
                var temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, file, true);
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
