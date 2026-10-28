using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using Mira.Core;
using Mira.Core.Subtitles;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// What a source is asked for a title: never the file's name or path. The ids Jellyfin knows (IMDb, TMDb; those of the
/// series for an episode), the season and episode, the title's name as a last resort when no id is known, and the
/// file's fingerprint for the source that recognises it (<see cref="MovieHash"/>). Languages as Jellyfin says them
/// (ISO 639-2: « fre »); each source gets its own code (<see cref="SubtitleLanguages"/>).
/// </summary>
public sealed record SubtitleQuery(bool IsEpisode, string? Hash, long? FileSize, string? ImdbId, string? TmdbId, string? ParentImdbId, string? ParentTmdbId,
    int? Season, int? Episode, string? Title, int? Year, bool HashOnly = false)
{
    /// <summary>Whether a source can be asked anything precise: an id, or a title to look up by name.</summary>
    public bool Askable => ImdbId is not null || TmdbId is not null || ParentImdbId is not null || ParentTmdbId is not null || Title is { Length: > 0 } || Hash is not null;
}

/// <summary>A subtitle file fetched from a source: its bytes, the name the source gave it, and what it is for (the episode of a pack).</summary>
public sealed record FetchedSubtitle(byte[] Bytes, string FileName, string Format);

/// <summary>A source could not serve: its daily quota is spent (<see cref="Until"/> says when it comes back), or it refused the key or the account.</summary>
public sealed class SubtitleSourceException(string message, DateTimeOffset? until = null, bool refused = false) : Exception(message)
{
    public DateTimeOffset? Until { get; } = until;
    public bool Refused { get; } = refused;
}

/// <summary>Language codes between Jellyfin (ISO 639-2, « fre ») and the sources (ISO 639-1, « fr »; « pt-br » for Brazil).</summary>
public static class SubtitleLanguages
{
    private static readonly (string Three, string Two)[] Table =
    [
        ("fre", "fr"), ("eng", "en"), ("jpn", "ja"), ("spa", "es"), ("ger", "de"), ("ita", "it"), ("por", "pt"), ("dut", "nl"), ("rus", "ru"), ("pol", "pl"),
        ("swe", "sv"), ("dan", "da"), ("nor", "no"), ("fin", "fi"), ("tur", "tr"), ("ara", "ar"), ("chi", "zh"), ("kor", "ko"), ("cze", "cs"), ("hun", "hu"),
        ("gre", "el"), ("rum", "ro"), ("ukr", "uk"), ("heb", "he"), ("vie", "vi"), ("tha", "th"), ("ind", "id"), ("cat", "ca"), ("bul", "bg"), ("hrv", "hr"),
        ("srp", "sr"), ("slo", "sk"), ("slv", "sl"), ("est", "et"), ("lav", "lv"), ("lit", "lt"), ("may", "ms"), ("per", "fa"), ("hin", "hi"), ("ben", "bn"),
    ];
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    { ["fra"] = "fre", ["deu"] = "ger", ["nld"] = "dut", ["zho"] = "chi", ["ces"] = "cze", ["ell"] = "gre", ["ron"] = "rum", ["slk"] = "slo", ["msa"] = "may", ["fas"] = "per" };

    /// <summary>« fre » → « fr »; null for a language no source is asked for.</summary>
    public static string? Two(string? three)
    {
        if (three is null) return null;
        var code = three.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(code, out var main)) code = main;
        if (code.Length == 2 && Table.Any(x => x.Two == code)) return code;
        return Table.FirstOrDefault(x => x.Three == code).Two;
    }

    /// <summary>« fr », « pt-br », « PT » → « fre », « por »; the code itself when unknown (Jellyfin keeps it).</summary>
    public static string Three(string? two)
    {
        if (two is null) return "und";
        var code = two.Trim().ToLowerInvariant();
        var main = code.Split('-')[0];
        if (main.Length == 3) return Aliases.TryGetValue(main, out var alias) ? alias : main;
        return Table.FirstOrDefault(x => x.Two == main).Three ?? code;
    }
}

/// <summary>
/// OpenSubtitles' fingerprint of a video file: its size plus the 64-bit words of its first and last 64 KiB, summed, as 16
/// hexadecimal digits. Identifies the release the file comes from; says nothing of what is in it.
/// </summary>
public static class MovieHash
{
    private const int Chunk = 64 * 1024;

    /// <summary>The hash of a file; null for a file shorter than 64 KiB (the source would not know it).</summary>
    public static string? Compute(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < Chunk) return null;
        var hash = (ulong)stream.Length;
        var buffer = new byte[Chunk];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(buffer);
        hash = Add(hash, buffer);
        stream.Seek(-Chunk, SeekOrigin.End);
        stream.ReadExactly(buffer);
        hash = Add(hash, buffer);
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static string? Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Compute(stream);
    }

    private static ulong Add(ulong hash, byte[] buffer)
    {
        for (var i = 0; i < buffer.Length; i += 8) unchecked { hash += BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i, 8)); }
        return hash;
    }
}

/// <summary>
/// A subtitle archive (SubDL sends zips, sometimes a whole season): the file for the episode wanted, else the only
/// subtitle file, else the first. Nothing is written to disk.
/// </summary>
public static class SubtitleArchive
{
    private const long MaxEntry = 8 * 1024 * 1024;

    /// <summary>Whether these bytes are a zip archive.</summary>
    public static bool IsZip(ReadOnlySpan<byte> bytes) => bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] is 3 or 5 or 7 && bytes[3] is 4 or 6 or 8;

    /// <summary>The subtitle file of the archive that fits (season, episode); null when the archive has none.</summary>
    public static FetchedSubtitle? Pick(byte[] zip, int? season, int? episode)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entries = archive.Entries.Where(x => ReleaseName.IsSubtitle(x.Name) && x.Length > 0 && x.Length <= MaxEntry).ToList();
        if (entries.Count == 0) return null;
        ZipArchiveEntry? chosen = null;
        if (episode is { } wanted)
        {
            foreach (var entry in entries)
            {
                var info = ReleaseRanking.Parse(Path.GetFileNameWithoutExtension(entry.Name));
                if (info.Episode == wanted && (info.Season is null || season is null || info.Season == season) && !info.Pack) { chosen = entry; break; }
            }
            if (chosen is null && entries.Count > 1) return null;
        }
        chosen ??= entries[0];
        using var stream = chosen.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new FetchedSubtitle(memory.ToArray(), chosen.Name, Path.GetExtension(chosen.Name).TrimStart('.').ToLowerInvariant());
    }
}

/// <summary>Subtitle files already beside a video.</summary>
public static class SubtitleFiles
{
    /// <summary>A subtitle file beside the video whose name carries the language (any of its codes), after the video's own name.</summary>
    public static bool HasBeside(string videoPath, string two)
    {
        var folder = Path.GetDirectoryName(videoPath);
        if (folder is null || !Directory.Exists(folder)) return false;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var three = SubtitleLanguages.Three(two);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { two, three };
        foreach (var (alias, main) in new[] { ("fra", "fre"), ("deu", "ger"), ("nld", "dut"), ("zho", "chi"), ("ces", "cze"), ("ell", "gre"), ("ron", "rum") }) if (main == three) names.Add(alias);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, stem + ".*"))
            {
                if (!Mira.Core.ReleaseName.IsSubtitle(file)) continue;
                var tags = Path.GetFileNameWithoutExtension(file)[stem.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
                if (tags.Any(names.Contains)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}

/// <summary>
/// The id Mira gives Jellyfin for a result, and reads back to fetch it: the source, the language, then what the source
/// needs (OpenSubtitles: a file id; SubDL: the path of its zip). No underscore: Jellyfin splits on the first one.
/// </summary>
public static class SubtitleIds
{
    public const string OpenSubtitles = "os", SubDl = "sd";

    public static string Make(string source, string language, string key) => $"{source}:{language}:{Hex(key)}";

    public static (string Source, string Language, string Key)? Parse(string? id)
    {
        if (id is null) return null;
        var parts = id.Split(':', 3);
        if (parts.Length != 3 || parts[0] is not (OpenSubtitles or SubDl) || parts[1].Length == 0) return null;
        try { return (parts[0], parts[1], Encoding.UTF8.GetString(Convert.FromHexString(parts[2]))); }
        catch (FormatException) { return null; }
    }

    private static string Hex(string text) => Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();
}

/// <summary>What the two sources share: one request at a time per source, a pause between them, and the HTTP client.</summary>
public abstract class SubtitleSource(HttpClient http)
{
    protected static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    protected HttpClient Http { get; } = http;
    /// <summary>The name shown for the source.</summary>
    public abstract string Name { get; }
    /// <summary>Whether this source has what it needs to be asked (a key).</summary>
    public abstract bool Ready { get; }

    /// <summary>The candidates of this source for a title, in one language, scored by nobody yet.</summary>
    public abstract Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, string language, CancellationToken ct);
    /// <summary>The file of a candidate (its key, as <see cref="SubtitleIds"/> gave it back), for the episode wanted when the source sends a pack.</summary>
    public abstract Task<FetchedSubtitle> FetchAsync(string key, int? season, int? episode, CancellationToken ct);

    /// <summary>One request at a time, <see cref="Pause"/> apart: the sources throttle by address, and Jellyfin asks for several languages at once.</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + Pause - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            _last = DateTimeOffset.UtcNow;
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var after = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                response.Dispose();
                await Task.Delay(after > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : after, ct).ConfigureAwait(false);
                using var again = Clone(request);
                response = await Http.SendAsync(again, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                _last = DateTimeOffset.UtcNow;
            }
            return response;
        }
        finally { _gate.Release(); }
    }

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is StringContent content) clone.Content = new StringContent(content.ReadAsStringAsync().Result, Encoding.UTF8, "application/json");
        return clone;
    }

    protected static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > limit) throw new SubtitleSourceException("File too big");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > limit) throw new SubtitleSourceException("File too big");
        }
        return memory.ToArray();
    }
}
