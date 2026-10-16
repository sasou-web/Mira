using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mira.Core;

/// <summary>
/// A film or an episode kept on this computer (« Garder sur ce PC »), to watch without Jellyfin: Jellyfin's own file,
/// its external subtitles and what Mira knew of the title. Described by the <see cref="OfflineCopies.Manifest"/> of its
/// folder, so that the folder alone is enough.
/// </summary>
public sealed record OfflineCopy
{
    /// <summary>The account it was kept for (<see cref="AccountData.LibraryProfile"/>): another account does not see it.</summary>
    public string Account { get; init; } = "";
    public MediaItem Item { get; init; } = new();
    /// <summary>The file as Jellyfin described it (its tracks), for the player and the page.</summary>
    public MediaSource? Source { get; init; }
    /// <summary>The video's name in the folder (« video.mkv »).</summary>
    public string File { get; init; } = "";
    /// <summary>Its size in bytes once complete; 0 until Jellyfin said.</summary>
    public long Size { get; init; }
    public bool Complete { get; init; }
    public List<OfflineSubtitle> Subtitles { get; init; } = [];
    public DateTimeOffset Added { get; init; }
    /// <summary>Where it is: read from the disk, never written.</summary>
    [JsonIgnore] public string Folder { get; init; } = "";
    [JsonIgnore] public string VideoPath => Path.Combine(Folder, File);
    [JsonIgnore] public string PartPath => VideoPath + ".part";
    /// <summary>What is on the disk now, the part of a download included.</summary>
    [JsonIgnore] public long BytesOnDisk => Folder.Length == 0 ? 0 : (Length(VideoPath) ?? Length(PartPath) ?? 0) + Subtitles.Sum(x => Length(Path.Combine(Folder, x.File)) ?? 0);
    private static long? Length(string path) { try { var file = new FileInfo(path); return file.Exists ? file.Length : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; } }
}
/// <summary>An external subtitle file kept beside the video (embedded ones are in the video).</summary>
public sealed record OfflineSubtitle(string File, int Index, string? Language, string? Title);

/// <summary>Jellyfin refused the download: this account may not download (its policy on Jellyfin).</summary>
public sealed class OfflineRefusedException() : IOException(L("Jellyfin ne permet pas à ce compte de télécharger ses vidéos. Dans Jellyfin, Tableau de bord → Utilisateurs → ce compte, autorise le téléchargement des médias, puis réessaie.", "Jellyfin doesn’t let this account download its videos. In Jellyfin, Dashboard → Users → this account, allow media downloads, then try again."));

/// <summary>
/// The films and episodes kept on this computer: where they are, how they are fetched from Jellyfin (resumed where a
/// previous try stopped) and what plays next without a connection. Free of WPF, tested on any system.
/// </summary>
public static class OfflineCopies
{
    public const string Manifest = "mira-copy.json";
    private static readonly JsonSerializerOptions Options = new(Json.Options) { WriteIndented = true };

    /// <summary>The folder of an account's copies under <paramref name="root"/>, named like its artwork's (no server name on the disk).</summary>
    public static string AccountFolder(string root, Connection connection) =>
        Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JellyfinClient.NormalizeServer(connection.Server) + connection.UserId)))[..16]);
    /// <summary>A new copy of <paramref name="item"/>, not fetched yet: its manifest is written at once, so that a closed Mira resumes it.</summary>
    public static OfflineCopy Plan(string root, Connection connection, MediaItem item, MediaSource source, DateTimeOffset now)
    {
        var folder = Path.Combine(AccountFolder(root, connection), SafeName(item.Id));
        var copy = new OfflineCopy { Account = AccountData.LibraryProfile(connection), Item = item with { MediaSources = null }, Source = source, File = "video." + Extension(source), Size = source.Size ?? 0, Added = now, Folder = folder };
        Directory.CreateDirectory(folder);
        Save(copy);
        return copy;
    }
    /// <summary>Every copy of this account under these folders, newest first; a folder that does not read is left out.</summary>
    public static List<OfflineCopy> List(IEnumerable<string> roots, Connection connection)
    {
        var account = AccountData.LibraryProfile(connection); var copies = new List<OfflineCopy>();
        foreach (var root in roots.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = AccountFolder(root, connection);
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var title in Directory.EnumerateDirectories(folder))
                    if (Read(title) is { } copy && copy.Account == account) copies.Add(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return copies.GroupBy(x => x.Item.Id).Select(x => x.OrderByDescending(c => c.Complete).First()).OrderByDescending(x => x.Added).ToList();
    }
    public static OfflineCopy? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, Manifest);
            if (!System.IO.File.Exists(path)) return null;
            var copy = JsonSerializer.Deserialize<OfflineCopy>(System.IO.File.ReadAllText(path), Options);
            // A name from the disk stays in its folder.
            return copy is null || copy.Item.Id.Length == 0 || SafeName(copy.File) != copy.File || copy.Subtitles.Any(x => SafeName(x.File) != x.File) ? null : copy with { Folder = folder };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    /// <summary>Written whole or not at all: a manifest cut by a crash would lose the copy.</summary>
    public static void Save(OfflineCopy copy)
    {
        var path = Path.Combine(copy.Folder, Manifest); var temporary = path + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(copy, Options));
        System.IO.File.Move(temporary, path, overwrite: true);
    }
    /// <summary>Removes a copy and its folder. False when a file is still in use (playing).</summary>
    public static bool Delete(OfflineCopy copy)
    {
        try { if (Directory.Exists(copy.Folder)) Directory.Delete(copy.Folder, recursive: true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>Every copy of an account, as its other data on sign-out (<see cref="AccountData"/>). False when one stayed.</summary>
    public static bool Erase(IEnumerable<string> roots, Connection connection)
    {
        var erased = true;
        foreach (var root in roots.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = AccountFolder(root, connection);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { erased = false; }
        }
        return erased;
    }

    /// <summary>
    /// Fetches the video (resumed from its part when Jellyfin allows it), then the external subtitles, and marks the copy
    /// complete. <paramref name="progress"/> gets the bytes done and the total.
    /// </summary>
    public static async Task<OfflineCopy> FetchAsync(JellyfinClient client, OfflineCopy copy, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (copy.Complete && System.IO.File.Exists(copy.VideoPath)) return copy;
        Directory.CreateDirectory(copy.Folder);
        var done = System.IO.File.Exists(copy.PartPath) ? new FileInfo(copy.PartPath).Length : 0;
        var response = await client.DownloadAsync(copy.Item.Id, done, ct);
        try
        {
            // Jellyfin sent the whole file again (no range), or the part is no longer of this file: from the start.
            if (done > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                done = 0;
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { response.Dispose(); response = await client.DownloadAsync(copy.Item.Id, 0, ct); }
            }
            var total = response.Content.Headers.ContentRange?.Length ?? (response.Content.Headers.ContentLength is { } length ? length + done : copy.Size);
            if (total > 0 && total != copy.Size) { copy = copy with { Size = total }; Save(copy); }
            if (FreeSpace(copy.Folder) is { } free && total - done > free - Reserve)
                throw new IOException(L($"Pas assez de place sur le disque pour garder « {copy.Item.DisplayTitle} » : il faut {SizeText(total - done)}, il reste {SizeText(Math.Max(0, free))}. Libère de la place ou choisis un autre dossier dans Réglages → Lecture.", $"Not enough disk space to keep “{copy.Item.DisplayTitle}”: it needs {SizeText(total - done)}, {SizeText(Math.Max(0, free))} is left. Free up some space or choose another folder in Settings → Playback."));
            progress?.Report((done, total));
            await using (var output = new FileStream(copy.PartPath, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[1 << 17]; var reported = DateTime.UtcNow;
                while (true)
                {
                    // A connection that stops sending without closing would wait forever: a minute without a byte ends it.
                    using var silence = CancellationTokenSource.CreateLinkedTokenSource(ct); silence.CancelAfter(TimeSpan.FromMinutes(1));
                    int read;
                    try { read = await input.ReadAsync(buffer, silence.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new HttpRequestException(L("Jellyfin a cessé d’envoyer le fichier.", "Jellyfin stopped sending the file.")); }
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct); done += read;
                    if (DateTime.UtcNow - reported > TimeSpan.FromMilliseconds(250)) { reported = DateTime.UtcNow; progress?.Report((done, total)); }
                }
            }
            if (total > 0 && done < total) throw new HttpRequestException(L("Le fichier a été coupé avant la fin. Mira le reprendra là où il s’est arrêté.", "The file was cut off before the end. Mira will pick it up where it stopped."));
        }
        finally { response.Dispose(); }
        System.IO.File.Move(copy.PartPath, copy.VideoPath, overwrite: true);
        progress?.Report((done, done));
        var subtitles = new List<OfflineSubtitle>();
        foreach (var stream in copy.Source?.MediaStreams.Where(x => x.Type == "Subtitle" && x.IsExternal) ?? [])
        {
            var name = $"subtitle-{stream.Index}.{SubtitleExtension(stream.Codec)}";
            try
            {
                var bytes = await client.SubtitleFileAsync(copy.Item.Id, copy.Source!.Id, stream.Index, SubtitleExtension(stream.Codec), ct);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(copy.Folder, name), bytes, ct);
                subtitles.Add(new(name, stream.Index, stream.Language, stream.DisplayTitle));
            }
            // A subtitle Jellyfin cannot give leaves the film watchable: it is only missing from the list.
            catch (HttpRequestException) { }
        }
        copy = copy with { Complete = true, Subtitles = subtitles };
        Save(copy);
        return copy;
    }
    /// <summary>Kept free beyond the file, so that a copy never fills the disk.</summary>
    public const long Reserve = 1L << 30;
    public static long? FreeSpace(string folder)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The episode after <paramref name="episode"/> among the copies, in the order of the series: what plays next when
    /// Jellyfin is out of reach.
    /// </summary>
    public static OfflineCopy? NextEpisode(IEnumerable<OfflineCopy> copies, MediaItem episode) =>
        episode.Type != "Episode" || episode.SeriesId is not { } series ? null : copies
            .Where(x => x.Complete && x.Item.Type == "Episode" && x.Item.SeriesId == series && Order(x.Item).CompareTo(Order(episode)) > 0)
            .OrderBy(x => Order(x.Item)).FirstOrDefault();
    private static (int, int) Order(MediaItem item) => (item.ParentIndexNumber ?? 0, item.IndexNumber ?? 0);
    /// <summary>The copies as the « Sur ce PC » page lists them: a series' episodes together, in order, films by date kept.</summary>
    public static List<OfflineCopy> Ordered(IEnumerable<OfflineCopy> copies)
    {
        var list = copies.ToList();
        var newest = list.GroupBy(x => x.Item.SeriesId ?? x.Item.Id).ToDictionary(x => x.Key, x => x.Max(c => c.Added));
        return list.OrderByDescending(x => newest[x.Item.SeriesId ?? x.Item.Id]).ThenBy(x => x.Item.SeriesId ?? x.Item.Id).ThenBy(x => Order(x.Item)).ToList();
    }

    /// <summary>« 850 Mo », « 4,2 Go ».</summary>
    public static string SizeText(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.0", Culture) + L(" Go", " GB")
        : Math.Max(1, (int)Math.Round(bytes / (double)(1L << 20))) + L(" Mo", " MB");
    /// <summary>The video's extension: that of Jellyfin's file, else its container (« mkv », « mp4 »).</summary>
    public static string Extension(MediaSource source)
    {
        var fromPath = source.Path is { } path ? Path.GetExtension(path.Replace('\\', '/')).TrimStart('.') : "";
        foreach (var candidate in new[] { fromPath }.Concat((source.Container ?? "").Split(',')))
        {
            var extension = candidate.Trim().ToLowerInvariant();
            if (extension.Length is > 0 and <= 5 && extension.All(char.IsAsciiLetterOrDigit)) return extension;
        }
        return "mkv";
    }
    /// <summary>A subtitle kept in its own format when mpv reads it, else converted to SubRip by Jellyfin.</summary>
    public static string SubtitleExtension(string? codec) => codec?.ToLowerInvariant() switch
    {
        "ass" => "ass",
        "ssa" => "ssa",
        "webvtt" or "vtt" => "vtt",
        _ => "srt"
    };
    private static string SafeName(string name) => new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray()) is { Length: > 0 } safe && !safe.StartsWith('.') ? safe : "_";
}
