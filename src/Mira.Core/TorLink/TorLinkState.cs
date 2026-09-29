using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A download that TorLink recorded as finished in its history.</summary>
public sealed record TorLinkCompletion(string Id, string Name, string? Source, long SizeBytes, string Directory, DateTimeOffset CompletedAt);

/// <summary>
/// Read-only view of TorLink's own state files, at the places TorLink itself uses
/// (env-paths "torlink": %APPDATA%\torlink\Config and %LOCALAPPDATA%\torlink\Data, or TORLINK_STATE_DIR).
/// Mira never writes these files: TorLink stays the only owner of its queue, history and seeds.
/// </summary>
public sealed partial class TorLinkState
{
    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public TorLinkState(string configDirectory, string dataDirectory) { ConfigDirectory = configDirectory; DataDirectory = dataDirectory; }
    public static TorLinkState ForCurrentUser(string? stateOverride = null)
    {
        var custom = stateOverride ?? Environment.GetEnvironmentVariable("TORLINK_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            var root = Path.GetFullPath(custom);
            return new(Path.Combine(root, "config"), Path.Combine(root, "data"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } a ? a : Path.Combine(home, "AppData", "Roaming");
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(home, "AppData", "Local");
        return new(Path.Combine(roaming, "torlink", "Config"), Path.Combine(local, "torlink", "Data"));
    }
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    public string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public string QueueFile => Path.Combine(DataDirectory, "queue.json");
    public string TorrentsDirectory => Path.Combine(DataDirectory, "torrents");
    public static string DefaultDownloadDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "torlink");
    [GeneratedRegex("^[0-9a-fA-F]{40}$")] private static partial Regex InfoHash();
    public static bool IsInfoHash(string value) => InfoHash().IsMatch(value);

    /// <summary>TorLink's download folder from its config.json, or TorLink's default.</summary>
    public string DownloadDirectory()
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(ConfigFile));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "downloadDir") is { Length: > 0 } dir) return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return DefaultDownloadDirectory;
    }

    /// <summary>Finished downloads, newest first. Null while the file cannot be read yet (TorLink replaces it atomically: try again).</summary>
    public List<TorLinkCompletion>? ReadHistory()
    {
        string raw;
        try { raw = ReadShared(HistoryFile); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var list = new List<TorLinkCompletion>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                // The magnet link is deliberately never read: Mira has no use for it.
                if (Text(item, "id") is not { } id || !IsInfoHash(id) || Text(item, "name") is not { Length: > 0 } name || Text(item, "dir") is not { Length: > 0 } dir) continue;
                var completed = item.TryGetProperty("completedAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253_402_300_799_999)) : DateTimeOffset.MinValue;
                var size = item.TryGetProperty("sizeBytes", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) ? Math.Max(0, bytes) : 0;
                list.Add(new(id.ToLowerInvariant(), name, Text(item, "source"), size, dir, completed));
            }
            return list.DistinctBy(x => x.Id).OrderByDescending(x => x.CompletedAt).ToList();
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Downloads still running in TorLink, according to the queue it persists on each change.</summary>
    public int ActiveDownloads()
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(QueueFile));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Count(x => x.ValueKind == JsonValueKind.Object && Text(x, "status") == "downloading") : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return 0; }
    }

    /// <summary>The file list TorLink saved for a torrent (its .torrent metadata), laid out as WebTorrent writes it.</summary>
    public TorrentInfo? ReadTorrent(string id)
    {
        if (!IsInfoHash(id)) return null;
        try
        {
            var file = new FileInfo(Path.Combine(TorrentsDirectory, id.ToLowerInvariant() + ".torrent"));
            if (!file.Exists || file.Length > 64L * 1024 * 1024) return null;
            using var stream = OpenShared(file.FullName);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Bencode.ReadTorrent(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // TorLink replaces its files by renaming a new copy over them: a reader that does not share delete access
    // would make that rename fail, and TorLink would lose the write. Mira's reads never get in its way.
    private static FileStream OpenShared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static string ReadShared(string path)
    {
        using var reader = new StreamReader(OpenShared(path), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
