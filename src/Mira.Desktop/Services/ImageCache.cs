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
    /// <summary>Decoded bitmaps kept for reuse (a 2560 px backdrop weighs ~15 MB). Cards on screen keep their own.</summary>
    public const long MemoryBudget = 512L << 20, MemoryKeep = 384L << 20;
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly Dictionary<string, Entry> _memory = [];
    private readonly object _gate = new();
    private long _memoryBytes, _clock;
    private int _settled;
    private sealed class Entry(Task<BitmapSource?> image, long lastUse) { public readonly Task<BitmapSource?> Image = image; public long LastUse = lastUse, Bytes; }
    public ImageCache(JellyfinClient client, string directory)
    {
        _client = client;
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Connection.Server + client.Connection.UserId)))[..16];
        var root = Path.Combine(directory, "images");
        _directory = Path.Combine(root, profile); Directory.CreateDirectory(_directory);
        // Artwork replaced on the server, or of an account no longer used, leaves with the oldest files.
        _ = Task.Run(() => DiskCache.Trim(root, DiskBudget, DiskKeep));
    }
    /// <summary>Estimated size of the decoded bitmaps held for reuse.</summary>
    internal long MemoryBytes { get { lock (_gate) return _memoryBytes; } }
    /// <summary>Requests finished and accounted for.</summary>
    internal int Settled { get { lock (_gate) return _settled; } }
    internal long MemoryLimit { get; init; } = MemoryBudget;
    internal long MemoryTarget { get; init; } = MemoryKeep;
    public Task<BitmapSource?> GetAsync(MediaItem item, bool backdrop = false, int? targetWidth = null)
    {
        string id = item.Id, type = "Primary", tag = "";
        if (backdrop)
        {
            if (item.BackdropImageTags.Length > 0) { type = "Backdrop"; tag = item.BackdropImageTags[0]; }
            else if (item.ParentBackdropItemId is not null) { id = item.ParentBackdropItemId; type = "Backdrop"; tag = item.ParentBackdropImageTags.FirstOrDefault() ?? ""; }
            else if (item.ImageTags.TryGetValue("Thumb", out var thumb)) { type = "Thumb"; tag = thumb; }
        }
        else if (item.Type == "Episode" && item.SeriesId is not null) { id = item.SeriesId; tag = item.SeriesPrimaryImageTag ?? ""; }
        if (tag.Length == 0) tag = item.ImageTags.GetValueOrDefault(type) ?? "";
        var width = targetWidth ?? (backdrop ? 2560 : 500);
        return Cached($"{id}-{type}-{tag}-{width}", id, type, width);
    }
    public Task<BitmapSource?> LogoAsync(MediaItem item)
    {
        var id = item.ImageTags.ContainsKey("Logo") ? item.Id : item.ParentLogoItemId ?? item.SeriesId;
        if (id is null) return Task.FromResult<BitmapSource?>(null);
        var tag = item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? "";
        return Cached($"{id}-Logo-{tag}-900", id, "Logo", 900);
    }
    public Task<BitmapSource?> EpisodeAsync(MediaItem item)
    {
        var type = item.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        return Cached($"{item.Id}-{type}-{item.ImageTags.GetValueOrDefault(type)}-500", item.Id, type, 500);
    }
    /// <summary>One request per image, shared by every caller; the least recently used leave past the budget.</summary>
    private Task<BitmapSource?> Cached(string key, string id, string type, int width)
    {
        Entry entry;
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var hit)) { hit.LastUse = ++_clock; return hit.Image; }
            // FetchAsync never takes this lock: starting it here is safe, and no caller can see a half-made entry.
            entry = new Entry(FetchAsync(key, id, type, width), ++_clock); _memory[key] = entry;
        }
        _ = entry.Image.ContinueWith(done => Settle(key, entry, done.IsCompletedSuccessfully ? done.Result : null), TaskScheduler.Default);
        return entry.Image;
    }
    private void Settle(string key, Entry entry, BitmapSource? bitmap)
    {
        lock (_gate)
        {
            _settled++;
            if (!_memory.TryGetValue(key, out var current) || current != entry) return;
            // A failed image is asked again next time instead of staying missing.
            if (bitmap is null) { _memory.Remove(key); return; }
            entry.Bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8);
            _memoryBytes += entry.Bytes;
            if (_memoryBytes <= MemoryLimit) return;
            foreach (var (oldKey, old) in _memory.Where(x => x.Value.Bytes > 0).OrderBy(x => x.Value.LastUse).ToList())
            {
                if (_memoryBytes <= MemoryTarget) break;
                _memory.Remove(oldKey); _memoryBytes -= old.Bytes;
            }
        }
    }
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
            if (!cached)
            {
                // Written beside, then moved: an interrupted write can never leave a truncated image behind.
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, path, overwrite: true);
            }
            try
            {
                return await Task.Run(() =>
                {
                    if (cached) DiskCache.Touch(path);
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return (BitmapSource)bitmap;
                });
            }
            // A damaged file is removed so the next request downloads it again.
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException) { TryDelete(path); throw new IOException("Image illisible.", ex); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException or UnauthorizedAccessException) { return null; }
        finally { _downloads.Release(); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
