using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Mira.Core.TorLink;

/// <summary>
/// « Télécharger sur le PC serveur », Mira for Windows' side (MainWindow.TorLinkRemote runs it): a search is TorLink's
/// own « torlnk search » (JSON on its output), a download goes to TorLink without a window, « torlnk serve » (its
/// HTTP API on 127.0.0.1 only, behind a token, <see cref="TorLinkServeClient"/>), which keeps the same queue as the
/// TorLink of the page. Decided here, so that the tests check every case.
/// </summary>
public static class TorLinkRemote
{
    /// <summary>A search that has not answered by then is stopped: TorLink's sources answer in a few seconds.</summary>
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);
    /// <summary>What a search may print; beyond it, it is stopped and refused.</summary>
    public const int MaxSearchOutput = 4 * 1024 * 1024;
    public const int MaxHits = 60;
    /// <summary>TorLink without a window stops once nothing has downloaded nor been shared for this long.</summary>
    public static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(3);
    /// <summary>How long TorLink without a window gets to answer after its start.</summary>
    public static readonly TimeSpan ServeStart = TimeSpan.FromSeconds(30);

    /// <summary>
    /// « torlnk search » for one of <see cref="RemoteDownloads.Categories"/>, the query last and in one argument: TorLink
    /// reads an argument starting with « -- » as an option, which <see cref="RemoteDownloads.CleanQuery"/> never leaves.
    /// </summary>
    public static IReadOnlyList<string> SearchArguments(string entry, string query, string category) => [entry, "search", "--category", category, query];
    /// <summary>« torlnk serve » on the loopback address only; its token goes in TORLINK_API_TOKEN, not on the command line.</summary>
    public static IReadOnlyList<string> ServeArguments(string entry, int port) => [entry, "serve", "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture)];
    public const string TokenVariable = "TORLINK_API_TOKEN";

    /// <summary>
    /// The results of « torlnk search » ({query, category, count, sources, results}), best shared first as TorLink sorts
    /// them, or why there are none. A result without a valid magnet link for its own info hash is left out.
    /// </summary>
    public static (List<AgentHit> Hits, string? Error) ParseSearch(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output.Trim());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], Unreadable);
            var hits = new List<AgentHit>();
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var item in results.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || Text(item, "magnet") is not { } magnet || RemoteDownloads.InfoHash(magnet) is not { } hash) continue;
                    if (Text(item, "infoHash") is { } id && !string.Equals(id, hash, StringComparison.OrdinalIgnoreCase)) continue;
                    hits.Add(new(hash, RemoteDownloads.CleanName(Text(item, "name")) ?? hash, Number(item, "sizeBytes"), (int)Math.Min(int.MaxValue, Number(item, "seeders")),
                        (int)Math.Min(int.MaxValue, Number(item, "leechers")), RemoteDownloads.CleanName(Text(item, "source")) ?? "", magnet, item.TryGetProperty("added", out var added) && added.ValueKind == JsonValueKind.Number && added.TryGetInt64(out var at) ? at : null));
                }
            hits = hits.DistinctBy(x => x.Id).Take(MaxHits).ToList();
            if (hits.Count > 0) return (hits, null);
            // No result: said differently when no source answered at all (no network, or every site down).
            var sources = root.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Object ? s.EnumerateObject().Select(x => x.Value).ToList() : [];
            var answered = sources.Any(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);
            return ([], sources.Count > 0 && !answered ? "Aucune source de TorLink n’a répondu au PC serveur : vérifie sa connexion à Internet." : null);
        }
        catch (JsonException) { return ([], Unreadable); }
    }
    private const string Unreadable = "TorLink n’a pas répondu comme prévu.";

    /// <summary>TorLink's downloads from its API (GET /downloads: {downloads, seeds}); the finished ones are the placed titles.</summary>
    public static List<RelayDownload> ParseDownloads(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array
            ? downloads.EnumerateArray().Select(Download).OfType<RelayDownload>().DistinctBy(x => x.Id).Take(60).ToList() : [];

    /// <summary>The same from queue.json, which TorLink writes at each change: no speed nor peers while it is closed.</summary>
    public static List<RelayDownload> ParseQueue(string json, bool running)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(Download).OfType<RelayDownload>()
                    .Select(x => running ? x : x with { Speed = 0, Peers = 0, Eta = null, Status = x.Status is "downloading" or "queued" ? "stopped" : x.Status }).DistinctBy(x => x.Id).Take(60).ToList()
                : [];
        }
        catch (JsonException) { return []; }
    }

    private static RelayDownload? Download(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && Text(item, "id") is { } id && RemoteDownloads.IsInfoHash(id.ToLowerInvariant())
            ? new(id.ToLowerInvariant(), RemoteDownloads.CleanName(Text(item, "name")) ?? id, Text(item, "status") ?? "", (int)Math.Clamp(Number(item, "progress"), 0, 100),
                Number(item, "speed"), (int)Math.Min(int.MaxValue, Number(item, "peers")), Number(item, "totalBytes"), Number(item, "downloadedBytes"),
                Number(item, "eta") is > 0 and var eta && Text(item, "status") == "downloading" ? Math.Min(eta, RemoteDownloads.MaxEta) : null)
            : null;

    /// <summary>Something still downloads, waits its turn, or is shared: TorLink without a window keeps running.</summary>
    public static bool Working(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        bool Any(string list, params string[] statuses) => root.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array
            && items.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && Text(x, "status") is { } status && statuses.Contains(status));
        return Any("downloads", "downloading", "queued") || Any("seeds", "seeding");
    }

    /// <summary>The latest titles Mira placed, for Mira web: in Mira's words, with their title in Jellyfin once it has them.</summary>
    public static List<RelayPlaced> Placed(IEnumerable<TorLinkImportEntry> entries) => entries
        .Where(x => x.State is not (TorLinkImportState.Ignored or TorLinkImportState.Waiting) && RemoteDownloads.IsInfoHash(x.Id)).Take(20)
        .Select(x => new RelayPlaced(x.Id, TorLinkPlacement.TitleOf(x), TorLinkPlacement.StatusOf(x), x.JellyfinId is { } item ? item.Replace("-", "") : null,
            x.State is TorLinkImportState.Imported or TorLinkImportState.Partial))
        .ToList();

    /// <summary>
    /// What the agent says when it cannot take downloads now (Mira web shows it); null when it can.
    /// <paramref name="onScreen"/>: TorLink's page is in front on this PC, where someone may be using it.
    /// </summary>
    public static string? Problem(bool installed, bool confirmed, bool elsewhere, bool onScreen) =>
        !installed ? "TorLink n’est pas activé dans Mira sur le PC serveur."
        : !confirmed ? "Sur le PC serveur, confirme d’abord dans Mira que ce TorLink est le tien."
        : elsewhere ? "TorLink est ouvert dans une autre fenêtre du PC serveur : tes demandes partent quand il est fermé."
        : onScreen ? "TorLink est à l’écran sur le PC serveur : tes demandes partent dès qu’on quitte sa page."
        : null;

    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (value.TryGetInt64(out var whole) ? Math.Max(0, whole) : value.TryGetDouble(out var real) && real > 0 ? (long)Math.Min(real, long.MaxValue) : 0) : 0;
}

/// <summary>
/// TorLink's HTTP API (« torlnk serve »), on 127.0.0.1 only and behind its token: add a magnet link, act on a download,
/// read the downloads. Never through a proxy: the address is this PC's.
/// </summary>
public sealed class TorLinkServeClient : IDisposable
{
    private readonly HttpClient _http;
    public TorLinkServeClient(int port, string token, HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>True once TorLink answers (GET /health, the only address without the token).</summary>
    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try { using var response = await _http.GetAsync("health", ct); return response.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>« added », or « duplicate » when TorLink has it already; null with TorLink's reason when it refuses.</summary>
    public async Task<(string? Outcome, string? Error)> AddAsync(string magnet, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("add", new { magnet }, ct);
        var body = await ReadAsync(response, ct);
        if (response.IsSuccessStatusCode) return (body is { } ok && ok.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String ? outcome.GetString() : "added", null);
        return (null, body is { } refused && refused.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : $"TorLink a répondu avec une erreur ({(int)response.StatusCode}).");
    }

    /// <summary>pause, resume, delete (its files with it: TorLink's own « delete »), stop-seed…: false when TorLink does not have that download.</summary>
    public async Task<bool> ControlAsync(string id, string action, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("control", new { id, action }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"TorLink a répondu avec une erreur ({(int)response.StatusCode}).");
        return true;
    }

    /// <summary>GET /downloads: {downloads, seeds}.</summary>
    public async Task<JsonElement> DownloadsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("downloads", ct);
        response.EnsureSuccessStatusCode();
        return await ReadAsync(response, ct) ?? throw new HttpRequestException("TorLink a répondu sans liste.");
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
