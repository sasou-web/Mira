using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A download that TorLink recorded as finished in its history.</summary>
public sealed record TorLinkCompletion(string Id, string Name, string? Source, long SizeBytes, string Directory, DateTimeOffset CompletedAt);

/// <summary>
/// View of TorLink's own state files, at the places TorLink itself uses (env-paths "torlink":
/// %APPDATA%\torlink\Config and %LOCALAPPDATA%\torlink\Data, or TORLINK_STATE_DIR). TorLink stays the owner of its
/// queue and history. Mira writes only two things, and only while TorLink is closed: a download folder when TorLink
/// has none yet (<see cref="ChooseDownloadDirectory"/>), and a pause on the sharing of downloads it moved (<see cref="PauseSeeds"/>).
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
    public string SeedsFile => Path.Combine(DataDirectory, "seeds.json");
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

    /// <summary>
    /// A download moved into the library is no longer in TorLink's folder: started again, TorLink would look for it on
    /// the network for a few seconds and write pieces back before giving up. Its sharing is set to "paused" in
    /// seeds.json, as TorLink's Seeding tab does with p; the person can resume it there. Only while TorLink is closed:
    /// it keeps this file in memory and writes it again. Returns the downloads found in the file, paused now or before.
    /// </summary>
    public IReadOnlyList<string> PauseSeeds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return [];
        JsonArray seeds;
        try { if (JsonNode.Parse(ReadShared(SeedsFile)) is not JsonArray parsed) return []; seeds = parsed; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>(); var changed = false;
        for (var i = 0; i < seeds.Count; i++)
        {
            // TorLink writes {"id", "status"}; older versions wrote the bare id of a download being shared.
            var (id, status) = seeds[i] switch
            {
                JsonValue value when value.TryGetValue<string>(out var bare) => (bare, "seeding"),
                JsonObject item when item["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) =>
                    (text, item["status"] is JsonValue statusValue && statusValue.TryGetValue<string>(out var s) ? s : null),
                _ => (null, null)
            };
            if (id is null || !wanted.Contains(id)) continue;
            found.Add(id.ToLowerInvariant());
            if (status != "seeding") continue;
            seeds[i] = new JsonObject { ["id"] = id, ["status"] = "paused" };
            changed = true;
        }
        if (changed && !Replace(SeedsFile, seeds.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))) return [];
        return found;
    }

    /// <summary>
    /// Sets TorLink's download folder when it has none yet (no config.json: TorLink has never been set up), so that
    /// downloads land on the drive of the library and move there by a rename. An existing choice is never changed.
    /// </summary>
    public bool ChooseDownloadDirectory(string folder)
    {
        if (File.Exists(ConfigFile)) return false;
        try
        {
            Directory.CreateDirectory(folder); Directory.CreateDirectory(ConfigDirectory);
            var temporary = ConfigFile + ".mira.tmp";
            File.WriteAllText(temporary, new JsonObject { ["downloadDir"] = Path.GetFullPath(folder), ["trackers"] = new JsonArray() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            try { File.Move(temporary, ConfigFile, false); return true; }
            catch (IOException) { File.Delete(temporary); return false; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// A download folder on the library's drive when TorLink's is on another one, beside the library folders (never
    /// inside one, where Jellyfin would list unfinished files): "Téléchargements TorLink". Null when the drive is the
    /// same already, or when the library folders are spread over several drives.
    /// </summary>
    public static string? DownloadFolderBeside(MediaLibraries libraries, string downloads)
    {
        try
        {
            var full = new[] { libraries.Movies, libraries.Series, libraries.Anime }.OfType<string>().Where(x => x.Trim().Length > 0)
                .Select(x => Path.GetFullPath(x.Trim())).ToList();
            var drives = full.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (drives is not [{ Length: > 0 } drive] || string.Equals(drive, Path.GetPathRoot(Path.GetFullPath(downloads)), StringComparison.OrdinalIgnoreCase)) return null;
            var roots = full.Select(x => x.TrimEnd('\\', '/')).ToList();
            const string name = "Téléchargements TorLink";
            bool InsideLibrary(string folder) => roots.Any(root => folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(folder, root, StringComparison.OrdinalIgnoreCase));
            // Beside the first library folder, otherwise at the root of its drive; a library at the root of a drive leaves no room.
            return new[] { Path.GetDirectoryName(roots[0]) is { Length: > 0 } parent ? Path.Combine(parent, name) : null, Path.Combine(drive, name) }
                .OfType<string>().FirstOrDefault(x => !InsideLibrary(x));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
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

    /// <summary>Written beside, then renamed over the file, as TorLink writes its own.</summary>
    private static bool Replace(string path, string content)
    {
        var temporary = path + ".mira.tmp";
        try { File.WriteAllText(temporary, content); File.Move(temporary, path, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
