using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using Mira.Core;

namespace Mira.Desktop.Services;

public sealed class ImageCache
{
    private readonly JellyfinClient _client;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(6);
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _memory = new();
    public ImageCache(JellyfinClient client, string directory)
    {
        _client = client;
        var profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Connection.Server + client.Connection.UserId)))[..16];
        _directory = Path.Combine(directory, "images", profile); Directory.CreateDirectory(_directory);
    }
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
        var key = $"{id}-{type}-{tag}-{width}";
        return _memory.GetOrAdd(key, _ => FetchAsync(key, id, type, width));
    }
    public Task<BitmapSource?> LogoAsync(MediaItem item)
    {
        var id = item.ImageTags.ContainsKey("Logo") ? item.Id : item.ParentLogoItemId ?? item.SeriesId;
        if (id is null) return Task.FromResult<BitmapSource?>(null);
        var tag = item.ImageTags.GetValueOrDefault("Logo") ?? item.ParentLogoImageTag ?? "";
        var key = $"{id}-Logo-{tag}-900";
        return _memory.GetOrAdd(key, _ => FetchAsync(key, id, "Logo", 900));
    }
    public Task<BitmapSource?> EpisodeAsync(MediaItem item)
    {
        var type = item.ImageTags.ContainsKey("Primary") ? "Primary" : "Thumb";
        var key = $"{item.Id}-{type}-{item.ImageTags.GetValueOrDefault(type)}-500";
        return _memory.GetOrAdd(key, _ => FetchAsync(key, item.Id, type, 500));
    }
    private async Task<BitmapSource?> FetchAsync(string key, string id, string type, int width)
    {
        await _downloads.WaitAsync();
        try
        {
            var filename = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".img";
            var path = Path.Combine(_directory, filename);
            var cached = File.Exists(path);
            var bytes = cached ? await File.ReadAllBytesAsync(path) : await _client.ImageAsync(id, type, width);
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
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return (BitmapSource)bitmap;
                });
            }
            // A damaged file is removed so the next request downloads it again.
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException) { TryDelete(path); throw new IOException("Image illisible.", ex); }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NotSupportedException or TaskCanceledException or UnauthorizedAccessException) { _memory.TryRemove(key, out _); return null; }
        finally { _downloads.Release(); }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
