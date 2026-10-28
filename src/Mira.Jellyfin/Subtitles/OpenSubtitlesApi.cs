using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// OpenSubtitles.com, through its REST API (api.opensubtitles.com/api/v1), the only source that recognises the video
/// file itself (its moviehash). What leaves the server for a search: Mira's API key (the app's, public), the version
/// of Mira in the User-Agent, the server's public address (the service counts its quotas by it), the title's ids,
/// season and episode, the languages, the hash; the title's name only when no id is known. Never the file's name.
/// With an account (optional, for the quota): its name and password once a day, then a token. Downloads are counted
/// (5 a day by address without an account, more with one): a spent quota is said with when it comes back.
/// </summary>
public sealed class OpenSubtitlesApi : SubtitleSource
{
    /// <summary>
    /// Mira's own key for the API: the « API consumer » Mira of OpenSubtitles.com (account 963sasou, anonymous downloads
    /// allowed). Public by design, as the key of Jellyfin's own Open Subtitles plugin is: it names the app, not a person;
    /// the quota counts by address, or by account when one is set. A key of the person's own in the settings wins.
    /// </summary>
    public const string MiraKey = "UsQ0ndNkM9QTNmmkkKNN1Jw81nhX6HOq";
    public const string DefaultBase = "https://api.opensubtitles.com/api/v1/";
    private const long MaxFile = 4 * 1024 * 1024;
    private static readonly TimeSpan TokenLife = TimeSpan.FromHours(23);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _apiKey, _userAgent;
    private readonly string? _username, _password;
    private readonly Uri _base;
    private string? _token;
    private DateTimeOffset _tokenUntil;

    /// <summary>What the last download said of the quota: how many remain and when it resets.</summary>
    public (int Remaining, DateTimeOffset? Reset)? Quota { get; private set; }

    public OpenSubtitlesApi(HttpClient http, string apiKey, string userAgent, string? username = null, string? password = null, string? baseUrl = null) : base(http)
    {
        _apiKey = apiKey; _userAgent = userAgent;
        _username = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        _password = string.IsNullOrEmpty(password) ? null : password;
        _base = new Uri(baseUrl is { Length: > 0 } ? baseUrl : DefaultBase);
    }

    public override string Name => "OpenSubtitles";
    public override bool Ready => _apiKey.Length > 0;
    public bool HasAccount => _username is not null && _password is not null;

    /// <summary>The query as sent: for the tests, and for the journal (nothing of the file's name in it).</summary>
    public static string SearchQuery(SubtitleQuery query, string language)
    {
        var parts = new List<string> { "languages=" + Uri.EscapeDataString(language) };
        if (query.Hash is { } hash) { parts.Add("moviehash=" + hash); if (query.HashOnly) parts.Add("moviehash_match=only"); }
        parts.Add("type=" + (query.IsEpisode ? "episode" : "movie"));
        if (query.IsEpisode)
        {
            if (query.ParentImdbId is { } pi) parts.Add("parent_imdb_id=" + Digits(pi));
            if (query.ParentTmdbId is { } pt) parts.Add("parent_tmdb_id=" + Digits(pt));
            if (query.ImdbId is { } ei && query.ParentImdbId is null && query.ParentTmdbId is null) parts.Add("imdb_id=" + Digits(ei));
            if (query.Season is { } s) parts.Add("season_number=" + s.ToString(CultureInfo.InvariantCulture));
            if (query.Episode is { } e) parts.Add("episode_number=" + e.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            if (query.ImdbId is { } i) parts.Add("imdb_id=" + Digits(i));
            if (query.TmdbId is { } t) parts.Add("tmdb_id=" + Digits(t));
        }
        var hasId = parts.Any(x => x.Contains("imdb_id=", StringComparison.Ordinal) || x.Contains("tmdb_id=", StringComparison.Ordinal));
        if (!hasId && query.Hash is null && query.Title is { Length: > 0 } title)
        {
            parts.Add("query=" + Uri.EscapeDataString(title));
            if (!query.IsEpisode && query.Year is { } year) parts.Add("year=" + year.ToString(CultureInfo.InvariantCulture));
        }
        parts.Add("order_by=download_count");
        return string.Join("&", parts);
    }

    private static string Digits(string id) => new(id.Where(char.IsAsciiDigit).ToArray());

    public override async Task<List<SubtitleCandidate>> SearchAsync(SubtitleQuery query, string language, CancellationToken ct)
    {
        if (!query.Askable) return [];
        var found = new List<SubtitleCandidate>();
        for (var page = 1; page <= 3; page++)
        {
            using var request = Request(HttpMethod.Get, "subtitles?" + SearchQuery(query, language) + (page > 1 ? "&page=" + page : ""));
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            var body = await Body(response, ct).ConfigureAwait(false);
            if (body?["data"] is not JsonArray data) break;
            foreach (var entry in data.OfType<JsonObject>())
                if (Candidate(entry, language) is { } candidate) found.Add(candidate);
            if (body["total_pages"] is not JsonValue pages || !pages.TryGetValue<int>(out var total) || page >= total || data.Count == 0) break;
        }
        return found;
    }

    /// <summary>A result of the API as a candidate; null without a file to download.</summary>
    public static SubtitleCandidate? Candidate(JsonObject entry, string language)
    {
        if (entry["attributes"] is not JsonObject a) return null;
        if (a["files"] is not JsonArray files || files.OfType<JsonObject>().FirstOrDefault() is not { } first || first["file_id"] is not JsonValue idValue) return null;
        var fileId = idValue.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture) : idValue.TryGetValue<string>(out var s) ? s : null;
        if (fileId is null) return null;
        var names = files.OfType<JsonObject>().Select(f => Text(f, "file_name")).OfType<string>().Where(x => x.Length > 0).ToList();
        return new SubtitleCandidate("OpenSubtitles", fileId, SubtitleLanguages.Three(Text(a, "language") ?? language), Text(a, "release") ?? names.FirstOrDefault() ?? "",
            Number(a, "fps") is > 0 and var fps ? fps : null, Flag(a, "moviehash_match"), Flag(a, "hearing_impaired"), Flag(a, "foreign_parts_only"),
            Flag(a, "machine_translated") || Flag(a, "ai_translated"), Flag(a, "from_trusted"), (int)Math.Min(int.MaxValue, Number(a, "download_count") ?? 0),
            Number(a, "ratings"), "srt", names);
    }

    public override async Task<FetchedSubtitle> FetchAsync(string key, int? season, int? episode, CancellationToken ct)
    {
        await LoginAsync(ct).ConfigureAwait(false);
        var link = await LinkAsync(key, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, link);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new SubtitleSourceException($"OpenSubtitles: {(int)response.StatusCode} on the file");
        var bytes = await ReadLimitedAsync(response, MaxFile, ct).ConfigureAwait(false);
        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? key + ".srt";
        return new FetchedSubtitle(bytes, name, Path.GetExtension(name).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext ? ext : "srt");
    }

    /// <summary>The temporary link of a file (POST download): counts one download of the quota; 406 = quota spent.</summary>
    private async Task<Uri> LinkAsync(string fileId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = Request(HttpMethod.Post, "download", new JsonObject { ["file_id"] = long.Parse(fileId, CultureInfo.InvariantCulture) });
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && HasAccount) { _token = null; await LoginAsync(ct).ConfigureAwait(false); continue; }
            var body = await Body(response, ct).ConfigureAwait(false);
            var remaining = body?["remaining"] is JsonValue r && r.TryGetValue<int>(out var left) ? left : (int?)null;
            var reset = Text(body, "reset_time_utc") is { } when && DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
            if (remaining is not null) Quota = (remaining.Value, reset);
            if (response.StatusCode == HttpStatusCode.NotAcceptable || remaining == 0 && !response.IsSuccessStatusCode)
                throw new SubtitleSourceException(Text(body, "message") ?? "OpenSubtitles: download quota spent", reset ?? DateTimeOffset.UtcNow.AddHours(24));
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new SubtitleSourceException(Text(body, "message") ?? "OpenSubtitles refused the key", refused: true);
            if (!response.IsSuccessStatusCode || Text(body, "link") is not { Length: > 0 } link) throw new SubtitleSourceException($"OpenSubtitles: {(int)response.StatusCode} on download");
            return new Uri(link);
        }
        throw new SubtitleSourceException("OpenSubtitles: login failed", refused: true);
    }

    /// <summary>Logs in once a day when an account is set: the token raises the daily quota. A refused account is said, and not tried again this run.</summary>
    public async Task LoginAsync(CancellationToken ct)
    {
        if (!HasAccount || (_token is not null && DateTimeOffset.UtcNow < _tokenUntil)) return;
        using var request = Request(HttpMethod.Post, "login", new JsonObject { ["username"] = _username, ["password"] = _password });
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await Body(response, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new SubtitleSourceException(Text(body, "message") ?? "OpenSubtitles refused the account", refused: true);
        if (!response.IsSuccessStatusCode || Text(body, "token") is not { Length: > 0 } token) throw new SubtitleSourceException($"OpenSubtitles: {(int)response.StatusCode} on login");
        _token = token; _tokenUntil = DateTimeOffset.UtcNow + TokenLife;
        if (body?["user"] is JsonObject user && user["allowed_downloads"] is JsonValue allowed && allowed.TryGetValue<int>(out var downloads)) Quota ??= (downloads, null);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, JsonObject? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.TryAddWithoutValidation("Api-Key", _apiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<JsonObject?> Body(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length == 0) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonObject? node, string name) => node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool Flag(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static double? Number(JsonObject node, string name) => node[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
