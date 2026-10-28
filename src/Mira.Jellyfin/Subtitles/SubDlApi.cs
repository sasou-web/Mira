using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.Subtitles;
using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// SubDL (subdl.com, heir of Subscene), through its API (api.subdl.com/api/v1). It knows no hash: Mira compares the
/// release names it gives. What leaves the server: the person's own key on a search (it identifies their SubDL account), the
/// server's public address, the title's ids, season and episode, the languages; the title's name only when no id is
/// known. Never the file's name. Files come as zip archives, sometimes a whole season (<see cref="SubtitleArchive"/>).
/// </summary>
public sealed class SubDlApi : SubtitleSource
{
    public const string DefaultBase = "https://api.subdl.com/api/v1/", DefaultFiles = "https://dl.subdl.com";
    private const long MaxFile = 16 * 1024 * 1024;
    private readonly string _apiKey, _userAgent;
    private readonly Uri _base, _files;

    public SubDlApi(HttpClient http, string apiKey, string userAgent, string? baseUrl = null, string? filesUrl = null) : base(http)
    {
        _apiKey = apiKey.Trim(); _userAgent = userAgent;
        _base = new Uri(baseUrl is { Length: > 0 } ? baseUrl : DefaultBase);
        _files = new Uri(filesUrl is { Length: > 0 } ? filesUrl : DefaultFiles);
    }

    public override string Name => "SubDL";
    public override bool Ready => _apiKey.Length > 0;

    /// <summary>The query as sent, without the key.</summary>
    public static string SearchQuery(SubtitleQuery query, string language)
    {
        var parts = new List<string> { "languages=" + language.ToUpperInvariant(), "type=" + (query.IsEpisode ? "tv" : "movie"), "releases=1", "subs_per_page=30" };
        var imdb = query.IsEpisode ? query.ParentImdbId ?? query.ImdbId : query.ImdbId;
        var tmdb = query.IsEpisode ? query.ParentTmdbId ?? query.TmdbId : query.TmdbId;
        if (imdb is { } i) parts.Add("imdb_id=" + (i.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? i : "tt" + i));
        else if (tmdb is { } t) parts.Add("tmdb_id=" + t);
        else if (query.Title is { Length: > 0 } title)
        {
            parts.Add("film_name=" + Uri.EscapeDataString(title));
            if (query.Year is { } year) parts.Add("year=" + year.ToString(CultureInfo.InvariantCulture));
        }
        if (query.IsEpisode)
        {
            if (query.Season is { } s) parts.Add("season_number=" + s.ToString(CultureInfo.InvariantCulture));
            if (query.Episode is { } e) parts.Add("episode_number=" + e.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join("&", parts);
    }

    public override async Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, string language, CancellationToken ct)
    {
        if (!query.Askable || query.Hash is not null && query.ImdbId is null && query.TmdbId is null && query.ParentImdbId is null && query.ParentTmdbId is null && query.Title is null) return [];
        // The key goes in the query, as SubDL's API reads it (it reads no header).
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "subtitles?" + SearchQuery(query, language) + "&api_key=" + Uri.EscapeDataString(_apiKey)));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? body;
        try { body = JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { body = null; }
        var error = body is not null && Text(body, "error") is { Length: > 0 } said ? said : null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || error?.Contains("key", StringComparison.OrdinalIgnoreCase) == true)
            throw new SubtitleSourceException("SubDL refused the key" + (error is null ? "" : ": " + error), refused: true);
        if (!response.IsSuccessStatusCode) throw new SubtitleSourceException($"SubDL: {(int)response.StatusCode}" + (error is null ? "" : " " + error));
        if (body?["subtitles"] is not JsonArray subtitles) return [];
        return subtitles.OfType<JsonObject>().Select(x => Candidate(x, language, query)).OfType<SubtitleCandidate>().ToList();
    }

    /// <summary>A result as a candidate: its release name, its episode (a pack is kept when it holds the episode wanted); null without a file.</summary>
    public static SubtitleCandidate? Candidate(JsonObject entry, string language, SubtitleQuery query)
    {
        if (Text(entry, "url") is not { Length: > 0 } url) return null;
        // Old archives from Subscene come as .rar (Mira reads zip only), and SubDL often no longer has them.
        if (url.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)) return null;
        var release = Text(entry, "release_name") ?? Text(entry, "name") ?? "";
        var season = Int(entry, "season"); var episode = Int(entry, "episode");
        var pack = Flag(entry, "full_season");
        if (query.IsEpisode && !pack)
        {
            if (episode is { } e && query.Episode is { } wanted && e != wanted) return null;
            if (season is { } s && query.Season is { } ws && s != ws) return null;
        }
        else if (query.IsEpisode && season is { } s && query.Season is { } ws && s != ws) return null;
        var fps = Number(entry, "fps") ?? Number(entry, "framerate");
        var lang = Text(entry, "language") is { Length: > 0 } code ? SubtitleLanguages.Three(code) : SubtitleLanguages.Three(language);
        return new SubtitleCandidate("SubDL", url, lang, release, fps is > 0 ? fps : null, false, Flag(entry, "hi"), false, false, false, 0, null, "srt",
            Text(entry, "name") is { Length: > 0 } name && name != release ? [name] : null);
    }

    public override async Task<FetchedSubtitle> FetchAsync(string key, int? season, int? episode, CancellationToken ct)
    {
        var address = key.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(key) : new Uri(_files, key);
        if (address.Host != _files.Host) throw new SubtitleSourceException("SubDL: unexpected file host");
        // Without the key: a file asked with one counts against a paid download quota that a free key does not have;
        // without, SubDL allows 300 a day for the address.
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new SubtitleSourceException("SubDL: download quota spent", DateTimeOffset.UtcNow.AddHours(24));
        if (!response.IsSuccessStatusCode) throw new SubtitleSourceException($"SubDL: {(int)response.StatusCode} on {address.AbsolutePath}");
        var bytes = await ReadLimitedAsync(response, MaxFile, ct).ConfigureAwait(false);
        if (SubtitleArchive.IsZip(bytes))
            return SubtitleArchive.Pick(bytes, season, episode) ?? throw new SubtitleSourceException("SubDL: no subtitle file for this episode in the archive");
        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Path.GetFileName(address.AbsolutePath);
        return new FetchedSubtitle(bytes, name, ReleaseName.IsSubtitle(name) ? Path.GetExtension(name).TrimStart('.').ToLowerInvariant() : "srt");
    }

    private static string? Text(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Flag(JsonObject node, string name) => node[name] is JsonValue v && (v.TryGetValue<bool>(out var b) && b || v.TryGetValue<int>(out var n) && n != 0);
    private static int? Int(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
    private static double? Number(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
