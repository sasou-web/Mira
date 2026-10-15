using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Core;

public sealed class JellyfinClient : IDisposable
{
    private readonly HttpClient _http;
    public Connection Connection { get; private set; }
    public JellyfinClient(Connection connection, HttpMessageHandler? handler = null)
    {
        var server = NormalizeServer(connection.Server);
        Connection = connection with { Server = server };
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(server);
        _http.Timeout = TimeSpan.FromSeconds(12);
        SetAuthorization();
    }
    /// <summary>Version of the running application, read once from the entry assembly.</summary>
    public static string AppVersion { get; } = (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0");
    /// <summary>The kind of device Jellyfin's dashboard shows for this app: "Windows", or "Mac" for Mira on macOS.</summary>
    public static string DeviceName { get; set; } = "Windows";
    public static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UserInputException("Saisis une adresse http:// ou https://, sans identifiants dans l’adresse.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    private void SetAuthorization()
    {
        _http.DefaultRequestHeaders.Remove("Authorization");
        var token = Connection.Token.Replace("\"", "").Replace("\r", "").Replace("\n", "");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization",
            $"MediaBrowser Client=\"Mira\", Device=\"{DeviceName}\", DeviceId=\"{Connection.DeviceId}\", Version=\"{AppVersion}\"" +
            (token.Length > 0 ? $", Token=\"{token}\"" : ""));
    }
    public async Task<Connection> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("Users/AuthenticateByName", new { Username = username, Pw = password }, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Nom d’utilisateur ou mot de passe incorrect.");
        await CheckAsync(response);
        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>(Json.Options, ct)
            ?? throw new IOException("Réponse de connexion vide.");
        Connection = Connection with { Token = result.AccessToken, UserId = result.User.Id, UserName = result.User.Name };
        SetAuthorization();
        return Connection;
    }
    // Quick Connect, Jellyfin's sign-in by a code: a device that is not signed in (Mira web on a phone) shows six
    // digits, and a device signed in approves them for its account, so no password is typed on the first one.
    /// <summary>Whether Jellyfin lets devices in by a code: false when its dashboard turned it off, or on any other answer.</summary>
    public async Task<bool> QuickConnectEnabledAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("QuickConnect/Enabled", ct);
        return response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(ct)).Trim() == "true";
    }
    /// <summary>
    /// Approves another device's code for this account. Asks first whether Quick Connect is on: turned off, Jellyfin
    /// answers 401, which Mira would take for an ended session.
    /// </summary>
    public async Task<CodeApproval> ApproveCodeAsync(string code, CancellationToken ct = default)
    {
        if (!await QuickConnectEnabledAsync(ct)) return CodeApproval.Disabled;
        using var response = await _http.PostAsync($"QuickConnect/Authorize?code={Uri.EscapeDataString(code)}&userId={Uri.EscapeDataString(Connection.UserId)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return CodeApproval.Unknown;
        await CheckAsync(response);
        return CodeApproval.Approved;
    }
    /// <summary>The six digits of a code as typed (« 458 128 », « 458-128 »); null when it is not six digits.</summary>
    public static string? QuickConnectCode(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 6 && (text ?? "").All(c => char.IsAsciiDigit(c) || c is ' ' or '-' or '\u00a0' or '\u202f') ? digits : null;
    }
    /// <summary>The longest « Déconnecter » waits for Jellyfin: a server that is off must not hold the sign-out.</summary>
    public static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Ends this session on Jellyfin too (Sessions/Logout), which then forgets its token: signed out of Mira, it can no
    /// longer be used. True when Jellyfin confirmed or already no longer knew it; false when it could not be told (the
    /// session then stays in its list of devices). Never throws: signing out goes on either way.
    /// </summary>
    public async Task<bool> LogoutAsync(CancellationToken ct = default)
    {
        if (Connection.Token.Length == 0) return false;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LogoutTimeout);
        try
        {
            using var response = await _http.PostAsync("Sessions/Logout", null, limit.Token);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return false; }
    }
    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        await CheckAsync(response);
        return await response.Content.ReadFromJsonAsync<T>(Json.Options, ct) ?? throw new IOException("Réponse Jellyfin vide.");
    }
    public Task<ItemsResult> ResumeAsync(CancellationToken ct = default) => GetAsync<ItemsResult>(
        $"UserItems/Resume?userId={Connection.UserId}&limit=16&mediaTypes=Video&fields=Overview,Genres&enableImageTypes=Primary,Backdrop,Thumb", ct);
    public Task<ItemsResult> NextUpAsync(CancellationToken ct = default) => GetAsync<ItemsResult>(
        $"Shows/NextUp?userId={Connection.UserId}&limit=16&fields=Overview,Genres", ct);
    public const int PageSize = 60;
    public Task<ItemsResult> BrowseAsync(string types = "Movie,Series", string? parentId = null, string? search = null, int start = 0, bool favorites = false, CancellationToken ct = default, CatalogQuery? filters = null, int limit = PageSize) =>
        GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&recursive=true&includeItemTypes={types}&limit={Math.Clamp(limit, 1, 600)}&startIndex={start}" +
            "&fields=Overview,Genres,ChildCount,RecursiveItemCount&enableImages=true" + (filters ?? new CatalogQuery()).Parameters +
            (parentId is null ? "" : $"&parentId={Uri.EscapeDataString(parentId)}") +
            (string.IsNullOrWhiteSpace(search) ? "" : $"&searchTerm={Uri.EscapeDataString(search)}") +
            (favorites ? "&isFavorite=true" : ""), ct);
    /// <summary>
    /// « Ton bilan »: the films and episodes played to the end since <paramref name="from"/> (by their last play), newest
    /// first, a few pages at most, and the series of those episodes (their genres, whether all is watched).
    /// </summary>
    public async Task<(List<MediaItem> Played, List<MediaItem> Series)> PlayedSinceAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        const int page = 300, most = 3000;
        var played = new List<MediaItem>();
        while (played.Count < most)
        {
            var result = await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&recursive=true&includeItemTypes=Movie,Episode&isPlayed=true&sortBy=DatePlayed&sortOrder=Descending" +
                $"&fields=Genres&enableImages=false&enableUserData=true&enableTotalRecordCount=false&startIndex={played.Count}&limit={page}", ct);
            played.AddRange(result.Items);
            if (result.Items.Count < page || result.Items[^1].UserData.LastPlayedDate is not { } last || last < from) break;
        }
        played.RemoveAll(x => x.UserData.LastPlayedDate is not { } at || at < from);
        var ids = played.Where(x => x.Type == "Episode").Select(x => x.SeriesId).OfType<string>().Distinct().ToList();
        var series = new List<MediaItem>();
        foreach (var chunk in ids.Chunk(100))
            series.AddRange((await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&ids={string.Join(',', chunk.Select(Uri.EscapeDataString))}&fields=Genres&enableImages=false&enableUserData=true", ct)).Items);
        return (played, series);
    }
    public async Task<CatalogFilters> FiltersAsync(CancellationToken ct = default)
    {
        // The legacy Filters endpoint only examines immediate children of the user root.
        var filters = GetAsync<JsonElement>($"Items/Filters2?userId={Connection.UserId}&includeItemTypes=Movie,Series&recursive=true", ct);
        var years = GetAsync<ItemsResult>($"Years?userId={Connection.UserId}&includeItemTypes=Movie,Series&recursive=true&enableImages=false", ct);
        await Task.WhenAll(filters, years);
        var genres = filters.Result.TryGetProperty("Genres", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(x => x.GetProperty("Name").GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray() : [];
        return new CatalogFilters { Genres = genres, Years = years.Result.Items.Select(x => int.TryParse(x.Name, out var year) ? year : 0).Where(x => x > 0).Distinct().ToArray() };
    }
    public async Task<List<LibrarySection>> LibrariesAsync(CancellationToken ct = default)
    {
        var result = await GetAsync<JsonElement>($"UserViews?userId={Connection.UserId}", ct);
        return result.GetProperty("Items").EnumerateArray().Select(x => new LibrarySection(x.GetProperty("Id").GetString()!,
            x.GetProperty("Name").GetString()!, x.TryGetProperty("CollectionType", out var c) ? c.GetString() : null)).ToList();
    }
    /// <summary>
    /// Every episode of a series, in order: long anime run past a thousand. Without the episodes Jellyfin lists as missing
    /// (its "afficher les épisodes manquants" option), which have no file to play.
    /// </summary>
    public Task<ItemsResult> EpisodesAsync(string seriesId, CancellationToken ct = default) =>
        GetAsync<ItemsResult>($"Shows/{Uri.EscapeDataString(seriesId)}/Episodes?userId={Connection.UserId}&fields=Overview&isMissing=false", ct);
    public Task<MediaItem> ItemAsync(string id, CancellationToken ct = default) =>
        GetAsync<MediaItem>($"Items/{Uri.EscapeDataString(id)}?userId={Connection.UserId}", ct);
    /// <summary>Titles Jellyfin finds close to this one (shared genres, people, studios).</summary>
    public Task<ItemsResult> SimilarAsync(string id, int limit = 12, CancellationToken ct = default) =>
        GetAsync<ItemsResult>($"Items/{Uri.EscapeDataString(id)}/Similar?userId={Connection.UserId}&limit={limit}&fields=Overview,Genres,ChildCount,RecursiveItemCount", ct);
    /// <summary>A film's or a series' bonus videos: making-of, deleted scenes, textless credits of its Extras folders (Jellyfin 10.9+).</summary>
    public Task<List<MediaItem>> ExtrasAsync(string id, CancellationToken ct = default) =>
        GetAsync<List<MediaItem>>($"Items/{Uri.EscapeDataString(id)}/SpecialFeatures?userId={Connection.UserId}", ct);
    /// <summary>
    /// The saga a film belongs to (its first Jellyfin collection), with its other titles in the order they came out.
    /// Null when it belongs to none, or when Jellyfin cannot say which collections hold a title (404: older than 12.1, where it was checked).
    /// </summary>
    public async Task<Saga?> SagaAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"Items/{Uri.EscapeDataString(id)}/Collections?userId={Connection.UserId}&limit=1", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response);
        var found = await response.Content.ReadFromJsonAsync<ItemsResult>(Json.Options, ct);
        if (found?.Items.FirstOrDefault(x => x.Type == "BoxSet") is not { } box) return null;
        var titles = await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&parentId={Uri.EscapeDataString(box.Id)}&sortBy=ProductionYear,PremiereDate,SortName&fields=Overview,Genres,ChildCount", ct);
        var others = titles.Items.Where(x => x.Id != id).ToList();
        return others.Count == 0 ? null : new Saga(box.Name, others);
    }
    public Task<PlaybackInfo> PlaybackAsync(string id, CancellationToken ct = default) =>
        GetAsync<PlaybackInfo>($"Items/{Uri.EscapeDataString(id)}/PlaybackInfo?userId={Connection.UserId}", ct);
    /// <summary>Typed intro/credits markers (Jellyfin 10.10+, filled by the server or a plugin such as Intro Skipper).</summary>
    public async Task<List<MediaSegment>> SegmentsAsync(string itemId, CancellationToken ct = default) =>
        (await GetAsync<MediaSegmentsResult>($"MediaSegments/{Uri.EscapeDataString(itemId)}" +
            "?includeSegmentTypes=Intro&includeSegmentTypes=Outro&includeSegmentTypes=Recap&includeSegmentTypes=Preview&includeSegmentTypes=Commercial", ct)).Items;
    /// <summary>
    /// What the player draws on its timeline: the chapters known to Jellyfin and its trickplay pictures, both returned by
    /// the single-item endpoint without extra fields.
    /// </summary>
    public async Task<TitleTimeline> TimelineAsync(string itemId, string? sourceId = null, CancellationToken ct = default)
    {
        var item = await GetAsync<TimelineHolder>($"Items/{Uri.EscapeDataString(itemId)}?userId={Connection.UserId}", ct);
        return new(item.Chapters ?? [], TrickplaySet.Pick(itemId, item.Trickplay, sourceId));
    }
    private sealed record TimelineHolder { public List<ChapterInfo>? Chapters { get; init; } public Dictionary<string, Dictionary<string, TrickplayInfo>>? Trickplay { get; init; } }
    /// <summary>One tile image of a title's trickplay pictures.</summary>
    public async Task<byte[]> TrickplayTileAsync(TrickplaySet set, int tile, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(set.TilePath(tile), ct);
        await CheckAsync(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    public Uri StreamUri(string itemId, string sourceId) => new(_http.BaseAddress!,
        $"Videos/{Uri.EscapeDataString(itemId)}/stream?static=true&mediaSourceId={Uri.EscapeDataString(sourceId)}");
    public string AuthorizationHeader => _http.DefaultRequestHeaders.GetValues("Authorization").Single();
    /// <summary>
    /// Jellyfin's file of a title as it is (« Garder sur ce PC »), from <paramref name="from"/> bytes on when a previous
    /// try stopped there: 206 then, or 200 with the whole file when Jellyfin ignores the range. The caller reads and
    /// disposes the answer; HttpClient's time limit ends once its headers are in.
    /// </summary>
    public async Task<HttpResponseMessage> DownloadAsync(string itemId, long from, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"Items/{Uri.EscapeDataString(itemId)}/Download");
        if (from > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, null);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        // 403 with a valid session: this account's policy forbids downloads (a session gone answers 401).
        if (response.StatusCode == HttpStatusCode.Forbidden) { response.Dispose(); throw new OfflineRefusedException(); }
        if (from > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return response;
        try { await CheckAsync(response); } catch { response.Dispose(); throw; }
        return response;
    }
    /// <summary>An external subtitle file of a title, in <paramref name="extension"/> (« srt », « ass »), converted by Jellyfin if needed.</summary>
    public async Task<byte[]> SubtitleFileAsync(string itemId, string sourceId, int index, string extension, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(sourceId)}/Subtitles/{index}/0/Stream.{extension}", ct);
        await CheckAsync(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    public async Task ReportAsync(string kind, PlaybackReport report, CancellationToken ct = default)
    {
        if (kind == "watched") { await SetPlayedAsync(report.ItemId, true, ct); return; }
        var path = kind switch { "start" => "Sessions/Playing", "progress" => "Sessions/Playing/Progress", "stop" => "Sessions/Playing/Stopped", _ => throw new ArgumentException("Invalid report kind") };
        using var response = await _http.PostAsJsonAsync(path, report, Json.Options, ct);
        await CheckAsync(response);
    }
    /// <summary>
    /// Where a title resumes, the rest of its user data kept (checked on Jellyfin 12.1): 0 takes it out of
    /// « Reprendre » on every device.
    /// </summary>
    public async Task SetPlaybackPositionAsync(string id, long ticks, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"UserItems/{Uri.EscapeDataString(id)}/UserData?userId={Connection.UserId}", new { PlaybackPositionTicks = Math.Max(0, ticks) }, Json.Options, ct);
        await CheckAsync(response);
    }
    /// <summary>Marks an item (or every episode of a series) as watched or not watched.</summary>
    public async Task SetPlayedAsync(string id, bool played, CancellationToken ct = default)
    {
        var path = $"UserPlayedItems/{Uri.EscapeDataString(id)}?userId={Connection.UserId}";
        using var response = played ? await _http.PostAsync(path, null, ct) : await _http.DeleteAsync(path, ct);
        await CheckAsync(response);
    }
    /// <summary>
    /// Library folders of the server, with their paths. Reading them needs administrator rights: null when the
    /// account may not, without ending the session (folders are then chosen in Réglages → TorLink).
    /// </summary>
    public async Task<List<VirtualFolder>?> VirtualFoldersAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Library/VirtualFolders", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        return await response.Content.ReadFromJsonAsync<List<VirtualFolder>>(Json.Options, ct) ?? [];
    }
    /// <summary>
    /// Reports new or removed media paths, the same signal Jellyfin's own folder monitor produces
    /// (the server rescans them after its monitor delay). Optional: false when refused or unreachable.
    /// </summary>
    public async Task<bool> ReportMediaChangedAsync(IEnumerable<string> created, IEnumerable<string>? deleted = null, CancellationToken ct = default)
    {
        var updates = created.Select(x => new MediaUpdate(x, "Created")).Concat((deleted ?? []).Select(x => new MediaUpdate(x, "Deleted"))).ToArray();
        if (updates.Length == 0) return true;
        try
        {
            // Json.Options keeps Jellyfin's own property casing ("Updates", "Path", "UpdateType").
            using var response = await _http.PostAsJsonAsync("Library/Media/Updated", new MediaUpdates(updates), Json.Options, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) { return false; }
    }
    /// <summary>
    /// Asks Jellyfin to look through every library folder for new, changed or removed files, like « Analyser toutes
    /// les médiathèques » in its dashboard. False when this account may not: only an administrator can.
    /// </summary>
    public async Task<bool> ScanLibrariesAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("Library/Refresh", null, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        await CheckAsync(response);
        return true;
    }
    /// <summary>Jellyfin's library scan task: running or not, its progress (0 to 100), when it last ended. Null when unreadable (not an administrator).</summary>
    public async Task<LibraryScan?> LibraryScanAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("ScheduledTasks?isHidden=false", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        var tasks = await response.Content.ReadFromJsonAsync<List<ScheduledTask>>(Json.Options, ct) ?? [];
        return tasks.FirstOrDefault(x => x?.Key == "RefreshLibrary") is { } task
            ? new(task.State is "Running" or "Cancelling", task.CurrentProgressPercentage, task.LastExecutionResult?.EndTimeUtc) : null;
    }
    /// <summary>
    /// Scans the libraries and follows the scan to its end, reporting its progress from 0 to 1 (null before it
    /// starts). False when this account may not scan. A scan that does not start within <paramref name="startLimit"/>
    /// (queued behind another task) or whose progress cannot be read is not waited for.
    /// </summary>
    public async Task<bool> ScanAndWaitAsync(IProgress<double?>? progress, TimeSpan poll, TimeSpan startLimit, CancellationToken ct = default)
    {
        var before = await LibraryScanAsync(ct);
        if (!await ScanLibrariesAsync(ct)) return false;
        progress?.Report(null);
        var started = DateTime.UtcNow; var seen = false;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(30))
        {
            await Task.Delay(poll, ct);
            if (await LibraryScanAsync(ct) is not { } scan) return true;
            if (scan.Running) { seen = true; progress?.Report(scan.Progress is { } p ? Math.Clamp(p / 100, 0, 1) : null); continue; }
            // Ended: seen running, or finished between two looks (a small library takes less than a second).
            if (seen || scan.LastEnded != before?.LastEnded || DateTime.UtcNow - started > startLimit) return true;
        }
        return true;
    }
    /// <summary>
    /// Whether Jellyfin gathers the films of a saga into a collection by itself at each library scan (« Ajouter
    /// automatiquement à une collection » of every film library, Jellyfin 10.9 and later, off by default): what the
    /// saga row of a film page needs. Null when Mira can neither tell nor change it: not an administrator, no film
    /// library, or a Jellyfin without the option.
    /// </summary>
    public async Task<bool?> SagasAsync(CancellationToken ct = default) =>
        await LibraryOptionsAsync(["movies"], ct) is { Count: > 0 } films && films.All(x => AutoCollections(x.Options) is not null)
            ? films.All(x => AutoCollections(x.Options) == true) : null;
    /// <summary>
    /// Turns that option on in every film library, sending back their other options as Jellyfin gave them. Jellyfin
    /// makes the sagas at its next scan. False when refused (only an administrator may).
    /// </summary>
    public async Task<bool> EnableSagasAsync(CancellationToken ct = default)
    {
        if (await LibraryOptionsAsync(["movies"], ct) is not { } films) return false;
        foreach (var (id, options) in films)
        {
            if (AutoCollections(options) != false) continue;
            var changed = (JsonObject)options!.DeepClone();
            changed["AutomaticallyAddToCollection"] = true;
            using var response = await _http.PostAsJsonAsync("Library/VirtualFolders/LibraryOptions", new JsonObject { ["Id"] = id, ["LibraryOptions"] = changed }, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
            await CheckAsync(response);
        }
        return true;
    }
    /// <summary>How many sagas (Jellyfin collections) this account sees.</summary>
    public async Task<int> SagaCountAsync(CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&includeItemTypes=BoxSet&recursive=true&limit=0", ct)).TotalRecordCount;
    private static bool? AutoCollections(JsonObject? options) =>
        options?["AutomaticallyAddToCollection"] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
    /// <summary>
    /// Whether Jellyfin makes the trickplay pictures (the timeline's previews) of every film and series library: their
    /// option « EnableTrickplayImageExtraction », off by default. Null as for <see cref="SagasAsync"/>.
    /// </summary>
    public async Task<bool?> TrickplayEnabledAsync(CancellationToken ct = default) =>
        await LibraryOptionsAsync(["movies", "tvshows"], ct) is { Count: > 0 } libraries && libraries.All(x => Trickplay(x.Options) is not null)
            ? libraries.All(x => Trickplay(x.Options) == true) : null;
    /// <summary>
    /// Turns that option on in every film and series library, their other options kept, then starts Jellyfin's task that
    /// makes the pictures (« Generate Trickplay Images », run every night afterwards). False when refused.
    /// </summary>
    public async Task<bool> EnableTrickplayAsync(CancellationToken ct = default)
    {
        if (await LibraryOptionsAsync(["movies", "tvshows"], ct) is not { } libraries) return false;
        foreach (var (id, options) in libraries)
        {
            if (Trickplay(options) != false) continue;
            var changed = (JsonObject)options!.DeepClone();
            changed["EnableTrickplayImageExtraction"] = true;
            using var response = await _http.PostAsJsonAsync("Library/VirtualFolders/LibraryOptions", new JsonObject { ["Id"] = id, ["LibraryOptions"] = changed }, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
            await CheckAsync(response);
        }
        if (await ScheduledTaskAsync("RefreshTrickplayImages", ct) is { Id: { } task, Running: false })
        {
            using var started = await _http.PostAsync($"ScheduledTasks/Running/{Uri.EscapeDataString(task)}", null, ct);
            await CheckAsync(started);
        }
        return true;
    }
    /// <summary>How far Jellyfin's trickplay task is (0 to 1, null before it says), or null when it is not running.</summary>
    public async Task<double?> TrickplayProgressAsync(CancellationToken ct = default) =>
        await ScheduledTaskAsync("RefreshTrickplayImages", ct) is { Running: true } task ? task.Progress ?? 0 : null;
    /// <summary>One of Jellyfin's scheduled tasks by its key: its id, whether it runs, how far, and when it last ended; null when refused or missing.</summary>
    private async Task<(string? Id, bool Running, double? Progress, string? Ended)?> ScheduledTaskAsync(string key, CancellationToken ct)
    {
        using var response = await _http.GetAsync("ScheduledTasks?isHidden=false", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is not JsonArray tasks) return null;
        var task = tasks.OfType<JsonObject>().FirstOrDefault(x => x["Key"] is JsonValue found && found.TryGetValue<string>(out var name) && name == key);
        if (task is null) return null;
        var running = task["State"] is JsonValue state && state.TryGetValue<string>(out var value) && value == "Running";
        var progress = task["CurrentProgressPercentage"] is JsonValue percent && percent.TryGetValue<double>(out var share) ? Math.Clamp(share / 100, 0, 1) : (double?)null;
        var ended = task["LastExecutionResult"] is JsonObject last && last["EndTimeUtc"] is JsonValue end && end.TryGetValue<string>(out var at) ? at : null;
        return (task["Id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null, running, progress, ended);
    }
    private static bool? Trickplay(JsonObject? options) =>
        options?["EnableTrickplayImageExtraction"] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
    /// <summary>The libraries of these kinds with their options, as Jellyfin's dashboard reads them; null when refused (not an administrator).</summary>
    private async Task<List<(string Id, JsonObject? Options)>?> LibraryOptionsAsync(string[] kinds, CancellationToken ct)
    {
        using var response = await _http.GetAsync("Library/VirtualFolders", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        var folders = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
        return folders.OfType<JsonObject>().Where(x => x["CollectionType"] is JsonValue type && type.TryGetValue<string>(out var kind) && kinds.Contains(kind) && x["ItemId"] is JsonValue)
            .Select(x => ((string)x["ItemId"]!, x["LibraryOptions"] as JsonObject)).ToList();
    }
    /// <summary>
    /// The kind of hardware Jellyfin converts with (HardwareAccelerationType: none, nvenc, qsv, amf…). Null when
    /// unreadable: not an administrator, or a Jellyfin without the setting.
    /// </summary>
    public async Task<string?> HardwareAccelerationAsync(CancellationToken ct = default) =>
        await EncodingSettingsAsync(ct) is { } encoding && encoding["HardwareAccelerationType"] is JsonValue value && value.TryGetValue<string>(out var kind) ? kind : null;
    /// <summary>
    /// Has Jellyfin convert with that kind of hardware, its encoder included, every other setting kept as it was
    /// (which codecs it decodes there among them: H.264 and VC-1 by default). False when refused.
    /// </summary>
    public async Task<bool> SetHardwareAccelerationAsync(string kind, CancellationToken ct = default)
    {
        if (await EncodingSettingsAsync(ct) is not { } encoding) return false;
        encoding["HardwareAccelerationType"] = kind;
        if (kind != GpuEncoding.None) encoding["EnableHardwareEncoding"] = true;
        using var content = new StringContent(encoding.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("System/Configuration/encoding", content, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        await CheckAsync(response);
        return true;
    }
    /// <summary>
    /// Whether Jellyfin converts a title with its settings as they are: the start of a film or an episode, made again
    /// in H.264 360p (never copied), read until <paramref name="enough"/> bytes, then stopped. A hardware it cannot open
    /// answers 500 at once (checked on 12.1 with nvenc and no card); one that gives up midway ends the stream short.
    /// Null when there is nothing to convert. An H.264 file is taken first: the card then decodes it too.
    /// </summary>
    public async Task<bool?> ConversionWorksAsync(TimeSpan limit, int enough = 256 * 1024, CancellationToken ct = default)
    {
        var result = await GetAsync<JsonElement>($"Items?userId={Connection.UserId}&recursive=true&includeItemTypes=Movie,Episode&mediaTypes=Video&sortBy=DateCreated&sortOrder=Descending&limit=20&fields=MediaStreams&enableImages=false&enableUserData=false", ct);
        if (!result.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var titles = items.EnumerateArray().Where(x => x.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String).ToList();
        if (titles.Count == 0) return null;
        static bool H264(JsonElement item) => item.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array
            && streams.EnumerateArray().Any(s => s.TryGetProperty("Type", out var type) && type.ValueEquals("Video") && s.TryGetProperty("Codec", out var codec) && codec.ValueEquals("h264"));
        var title = titles.FirstOrDefault(H264) is { ValueKind: JsonValueKind.Object } h264 ? h264 : titles[0];
        var session = Guid.NewGuid().ToString("N");
        var stop = $"Videos/ActiveEncodings?deviceId={Uri.EscapeDataString(Connection.DeviceId)}&playSessionId={session}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        var answered = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"Videos/{title.GetProperty("Id").GetString()}/stream.ts?static=false&videoCodec=h264&audioCodec=aac&maxHeight=360&videoBitRate=1000000&audioBitRate=128000&maxAudioChannels=2&allowVideoStreamCopy=false&allowAudioStreamCopy=false&deviceId={Uri.EscapeDataString(Connection.DeviceId)}&playSessionId={session}");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // FFmpeg failed to start. Anything else (starting, refused, session ended) is not the hardware's doing.
            if (response.StatusCode == HttpStatusCode.InternalServerError) return false;
            await CheckAsync(response);
            answered = true;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[81920]; var read = 0;
            while (read < enough)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) return false;
                read += count;
            }
            return true;
        }
        // Too slow to give its first bytes: not worth keeping. HttpClient's own limit lands here as well.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        // The stream cut once begun: FFmpeg stopped. Jellyfin out of reach before answering is not the card's doing.
        catch (Exception ex) when (answered && ex is HttpRequestException or IOException) { return false; }
        finally
        {
            try { using var stopped = await _http.DeleteAsync(stop, CancellationToken.None); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        }
    }
    /// <summary>
    /// Who plays what on Jellyfin right now (<see cref="Viewers"/>): every session for an administrator, this account's
    /// own otherwise.
    /// </summary>
    public async Task<List<Viewer>> ViewersAsync(CancellationToken ct = default) =>
        Viewers.Parse(await GetAsync<JsonElement>("Sessions?activeWithinSeconds=960", ct), Connection.DeviceId);
    /// <summary>
    /// Asks a session to stop (Jellyfin's Playstate Stop): only one whose WebSocket takes commands (CanStop) does,
    /// Mira web among them while it plays. False when refused.
    /// </summary>
    public async Task<bool> StopViewerAsync(string sessionId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"Sessions/{Uri.EscapeDataString(sessionId)}/Playing/Stop", null, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return false;
        await CheckAsync(response);
        return true;
    }
    private async Task<JsonObject?> EncodingSettingsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("System/Configuration/encoding", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        await CheckAsync(response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
    }
    /// <summary>
    /// Whether this account administers Jellyfin (its policy): only an administrator installs Mira web or lets
    /// Tailscale in. Null when Jellyfin does not say.
    /// </summary>
    public async Task<bool?> IsAdministratorAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Users/Me", ct);
        if (!response.IsSuccessStatusCode) return null;
        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return user.RootElement.ValueKind == JsonValueKind.Object && user.RootElement.TryGetProperty("Policy", out var policy) && policy.ValueKind == JsonValueKind.Object
            && policy.TryGetProperty("IsAdministrator", out var admin) && admin.ValueKind is JsonValueKind.True or JsonValueKind.False ? admin.GetBoolean() : null;
    }
    /// <summary>
    /// Whether Jellyfin lets in devices on Tailscale: its access from elsewhere is on, or Tailscale's range counts as
    /// local. Null when its network settings cannot be read (not an administrator).
    /// </summary>
    public async Task<bool?> TailnetAllowedAsync(CancellationToken ct = default)
    {
        if (await NetworkSettingsAsync(ct) is not { } network) return null;
        return network["EnableRemoteAccess"] is JsonValue remote && remote.TryGetValue<bool>(out var on) && on || Subnets(network).Contains(LocalNetwork.TailnetRange, StringComparer.OrdinalIgnoreCase);
    }
    /// <summary>
    /// Counts Tailscale's range as local in Jellyfin's network settings, every other setting kept as it was: devices
    /// on Tailscale connect, the Internet stays refused. Applied at once, without restarting Jellyfin (checked on 12.1).
    /// False when this account may not change them.
    /// </summary>
    public async Task<bool> AllowTailnetAsync(CancellationToken ct = default)
    {
        if (await NetworkSettingsAsync(ct) is not { } network) return false;
        network["LocalNetworkSubnets"] = new JsonArray(LocalNetwork.WithTailnet(Subnets(network)).Select(x => (JsonNode?)x).ToArray());
        using var content = new StringContent(network.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("System/Configuration/network", content, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
        await CheckAsync(response);
        return true;
    }
    /// <summary>
    /// Whether Jellyfin is up and ready: while it starts, it answers System/Info/Public and 503 to the rest; System/Ping
    /// only once ready. False when it does not answer (restarting, stopped).
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("System/Ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }
    /// <summary>Whether this server serves Mira web at /Mira (its plugin installed and loaded).</summary>
    public async Task<bool> WebAppAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("Mira/manifest.webmanifest", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }
    /// <summary>
    /// Installs Mira web on Jellyfin as its dashboard would, from <paramref name="repository"/>: the repository of one
    /// release, which Mira checked against its signed manifest (<see cref="MiraWebRelease"/>). It replaces any other
    /// repository of Mira's (the others are kept), the plugin is installed from it, then Jellyfin restarted to load it,
    /// and waited for until /Mira answers (checked on 12.1, which restarts within its own process). Needs an administrator account.
    /// </summary>
    public async Task<WebAppInstall> InstallWebAppAsync(string repository, TimeSpan? restartLimit = null, CancellationToken ct = default)
    {
        if (await InstallFromAsync(repository, ct) is { } refused) return refused;
        await RestartAsync(ct);
        // Jellyfin stops answering while it restarts, then serves the plugin's pages.
        var limit = DateTime.UtcNow + (restartLimit ?? TimeSpan.FromSeconds(90));
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        while (DateTime.UtcNow < limit)
        {
            if (await WebAppAvailableAsync(ct)) return WebAppInstall.Ready;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return WebAppInstall.Restarting;
    }
    /// <summary>
    /// At startup, after Mira updated itself: Jellyfin's repository of Mira web moves forward to <paramref name="version"/>,
    /// the repository <paramref name="verified"/> gives once it has checked that release. Nothing is installed or
    /// restarted here: Jellyfin's own update task then installs the new version as it does any update, so a plugin
    /// the administrator uninstalled, disabled or keeps from updating stays so. Never backwards: nothing changes while
    /// Mira's repository already offers this version or a newer one, or while Jellyfin has this version or a newer one
    /// (another PC's Mira, more recent, is in charge). Only where Mira's repository is registered and enabled (by the
    /// guide, or by hand), and for an administrator. True once Jellyfin is where it should be; false when Mira has
    /// nothing to do on this server for now (no such repository, or not allowed), to look again at the next start.
    /// </summary>
    public async Task<bool> KeepWebAppCurrentAsync(Version version, Func<Task<string>> verified, CancellationToken ct = default)
    {
        if (await RepositoriesAsync(ct) is not { } repositories) return false;
        var mira = repositories.Where(RepositoryEnabled).Select(RepositoryUrl).Where(MiraWeb.IsRepository).ToList();
        if (mira.Count == 0) return false;
        if (mira.Any(url => MiraWeb.PinnedVersion(url) is { } pinned && pinned >= version)) return true;
        if (await WebAppVersionAsync(ct) is { } installed && installed >= version) return true;
        return await PinAsync(repositories, await verified(), ct) is null;
    }
    /// <summary>Jellyfin restarted, as its dashboard does (administrator): it stops answering for a few seconds.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        using var restart = await _http.PostAsync("System/Restart", null, ct);
        await CheckAsync(restart);
    }
    /// <summary>Mira web as Jellyfin serves it and as it waits for a restart (<see cref="MiraWeb.Installed"/>); null when this account may not read the plugins.</summary>
    public async Task<MiraWeb.Versions?> WebAppVersionsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("Plugins", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        return MiraWeb.Installed(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? []);
    }
    /// <summary>
    /// Jellyfin's « Update Plugins » task (PluginUpdates, which it runs at its start and every 24 hours) run now, as its
    /// dashboard does, then waited for until it ends (a few seconds on 12.1). It updates every plugin under its own
    /// rules: one kept from updating stays as it is. False when refused, missing, or not over within <paramref name="limit"/>.
    /// </summary>
    public async Task<bool> UpdatePluginsNowAsync(TimeSpan? limit = null, CancellationToken ct = default)
    {
        if (await ScheduledTaskAsync("PluginUpdates", ct) is not { Id: { } id } before) return false;
        if (!before.Running)
        {
            using var started = await _http.PostAsync($"ScheduledTasks/Running/{Uri.EscapeDataString(id)}", null, ct);
            if (started.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
            await CheckAsync(started);
        }
        // Over once it no longer runs and its last run is another one than before.
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromMinutes(3));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (await ScheduledTaskAsync("PluginUpdates", ct) is { Running: false, Ended: { } ended } && ended != before.Ended) return true;
        }
        return false;
    }
    /// <summary>The newest version of Mira's plugin Jellyfin lists (one waiting for a restart included); null without one.</summary>
    private async Task<Version?> WebAppVersionAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("Plugins", ct);
        await CheckAsync(response);
        var plugins = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
        Version? newest = null;
        foreach (var plugin in plugins.OfType<JsonObject>())
        {
            if (plugin["Id"] is not JsonValue id || !id.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var guid) || guid != MiraWeb.PluginId) continue;
            // Jellyfin writes four numbers (0.7.5.0), Mira three.
            if (plugin["Version"] is JsonValue listed && listed.TryGetValue<string>(out var number) && Version.TryParse(number, out var found)
                && new Version(found.Major, found.Minor, Math.Max(found.Build, 0)) is var three && (newest is null || three > newest)) newest = three;
        }
        return newest;
    }
    /// <summary>
    /// <paramref name="repository"/> made Mira's only repository in Jellyfin, then the plugin installed from it. Null once
    /// Jellyfin has it; otherwise why not.
    /// </summary>
    private async Task<WebAppInstall?> InstallFromAsync(string repository, CancellationToken ct)
    {
        if (await RepositoriesAsync(ct) is not { } repositories) return WebAppInstall.NotAllowed;
        if (await PinAsync(repositories, repository, ct) is { } refused) return refused;
        var install = $"Packages/Installed/{Uri.EscapeDataString(MiraWeb.PluginName)}?assemblyGuid={MiraWeb.PluginId}&repositoryUrl={Uri.EscapeDataString(repository)}";
        using var installed = await _http.PostAsync(install, null, ct);
        if (installed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return WebAppInstall.NotAllowed;
        if (installed.StatusCode == HttpStatusCode.NotFound) return WebAppInstall.Unavailable;
        await CheckAsync(installed);
        return null;
    }
    /// <summary>
    /// <paramref name="repository"/> made Mira's only repository in <paramref name="repositories"/> (Jellyfin's list), the
    /// others kept, and saved if that changed anything. Null once saved; otherwise why not.
    /// </summary>
    private async Task<WebAppInstall?> PinAsync(JsonArray repositories, string repository, CancellationToken ct)
    {
        // Jellyfin merges the versions every repository offers for a plugin and installs the newest: another repository
        // of Mira's, the latest release's above all, would bring back a version nobody checked.
        var mira = repositories.Where(x => MiraWeb.IsRepository(RepositoryUrl(x))).ToList();
        if (mira is [var only] && RepositoryUrl(only) == repository && RepositoryEnabled(only)) return null;
        foreach (var entry in mira) repositories.Remove(entry);
        repositories.Add(new JsonObject { ["Name"] = MiraWeb.RepositoryName, ["Url"] = repository, ["Enabled"] = true });
        using var content = new StringContent(repositories.ToJsonString(), Encoding.UTF8, "application/json");
        using var saved = await _http.PostAsync("Repositories", content, ct);
        if (saved.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return WebAppInstall.NotAllowed;
        await CheckAsync(saved);
        return null;
    }
    /// <summary>Jellyfin's plugin repositories, as it lists them; null when this account may not read them.</summary>
    private async Task<JsonArray?> RepositoriesAsync(CancellationToken ct)
    {
        using var listed = await _http.GetAsync("Repositories", ct);
        if (listed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(listed);
        return JsonNode.Parse(await listed.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
    }
    private static string? RepositoryUrl(JsonNode? entry) => entry is JsonObject repository && repository["Url"] is JsonValue url && url.TryGetValue<string>(out var text) ? text : null;
    // Jellyfin counts a repository without "Enabled" as enabled.
    private static bool RepositoryEnabled(JsonNode? entry) => entry is JsonObject repository && (repository["Enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on);
    private async Task<JsonObject?> NetworkSettingsAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("System/Configuration/network", ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        await CheckAsync(response);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
    }
    private static List<string> Subnets(JsonObject network) =>
        network["LocalNetworkSubnets"] is JsonArray list ? list.Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().ToList() : [];
    private sealed record ScheduledTask(string? Key, string? State, double? CurrentProgressPercentage, ScheduledTaskResult? LastExecutionResult);
    private sealed record ScheduledTaskResult(DateTimeOffset? EndTimeUtc);
    private sealed record MediaUpdate(string Path, string UpdateType);
    private sealed record MediaUpdates(MediaUpdate[] Updates);
    /// <summary>
    /// The titles owning these files once Jellyfin has indexed them, among its 100 latest films and episodes, in one
    /// request: file path → the film, or the series of an episode. Paths must match exactly the ones the server sees.
    /// </summary>
    public async Task<Dictionary<string, string>> FindIndexedAsync(IReadOnlyCollection<string> files, CancellationToken ct = default)
    {
        var wanted = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return found;
        var result = await GetAsync<JsonElement>($"Items?userId={Connection.UserId}&recursive=true&includeItemTypes=Movie,Episode&sortBy=DateCreated&sortOrder=Descending&limit=100&fields=Path&enableImages=false&enableUserData=false", ct);
        if (!result.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array) return found;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("Path", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() is not { } value || !wanted.Contains(value) || found.ContainsKey(value)) continue;
            var owner = item.TryGetProperty("SeriesId", out var series) && series.ValueKind == JsonValueKind.String && series.GetString() is { Length: > 0 } seriesId ? seriesId
                : item.TryGetProperty("Id", out var own) && own.ValueKind == JsonValueKind.String ? own.GetString() : null;
            if (owner is not null) found[value] = owner;
        }
        return found;
    }
    /// <summary>
    /// The 50 episodes Jellyfin added last, not watched yet, and their series with what this account watched of them
    /// (RecursiveItemCount gives the share watched): what <see cref="NewEpisodes"/> picks from.
    /// </summary>
    public async Task<(List<MediaItem> Episodes, Dictionary<string, MediaItem> Series)> RecentEpisodesAsync(CancellationToken ct = default)
    {
        var episodes = (await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&recursive=true&includeItemTypes=Episode&isMissing=false&isPlayed=false" +
            "&sortBy=DateCreated&sortOrder=Descending&limit=50&fields=DateCreated&enableImageTypes=Primary&imageTypeLimit=1", ct)).Items;
        var ids = episodes.Select(x => x.SeriesId).OfType<string>().Distinct().ToList();
        if (ids.Count == 0) return (episodes, []);
        var series = await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&ids={string.Join(',', ids.Select(Uri.EscapeDataString))}&fields=RecursiveItemCount&enableImages=false", ct);
        return (episodes, series.Items.Where(x => x.Type == "Series").GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First()));
    }
    /// <summary>The episode Jellyfin would play next for one series (resumable first, then the following one).</summary>
    public async Task<MediaItem?> NextEpisodeAsync(string seriesId, CancellationToken ct = default) =>
        (await GetAsync<ItemsResult>($"Shows/NextUp?userId={Connection.UserId}&seriesId={Uri.EscapeDataString(seriesId)}&limit=1&enableResumable=true&fields=Overview", ct)).Items.FirstOrDefault();
    /// <summary>The thumbnail tag of each of these seasons, null when it has none: one request for all of them.</summary>
    public async Task<Dictionary<string, string?>> SeasonThumbsAsync(IReadOnlyCollection<string> seasonIds, CancellationToken ct = default)
    {
        var found = new Dictionary<string, string?>();
        if (seasonIds.Count == 0) return found;
        var result = await GetAsync<ItemsResult>($"Items?userId={Connection.UserId}&ids={string.Join(',', seasonIds.Select(Uri.EscapeDataString))}" +
            "&enableImageTypes=Thumb&imageTypeLimit=1&enableUserData=false&fields=", ct);
        foreach (var id in seasonIds) found[id] = null;
        foreach (var season in result.Items) if (found.ContainsKey(season.Id)) found[season.Id] = season.ImageTags.GetValueOrDefault("Thumb");
        return found;
    }
    public async Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
    {
        var path = $"UserFavoriteItems/{Uri.EscapeDataString(id)}?userId={Connection.UserId}";
        using var response = favorite ? await _http.PostAsync(path, null, ct) : await _http.DeleteAsync(path, ct);
        await CheckAsync(response);
    }
    public async Task<byte[]> ImageAsync(string id, string type, int width, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"Items/{Uri.EscapeDataString(id)}/Images/{type}?maxWidth={width}&quality=85", ct);
        await CheckAsync(response);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    // « Regarder ensemble », Jellyfin's SyncPlay (Together): Unauthorized when Jellyfin does not let this account use it.
    public async Task<(long Sent, long Received, long ServerIn, long ServerOut)?> UtcTimeAsync(CancellationToken ct = default)
    {
        var sent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var answer = await GetAsync<JsonElement>("GetUtcTime", ct);
        var received = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Together.ServerTime(answer.TryGetProperty("RequestReceptionTime", out var a) ? a.GetString() : null) is { } serverIn
            && Together.ServerTime(answer.TryGetProperty("ResponseTransmissionTime", out var b) ? b.GetString() : null) is { } serverOut ? (sent, received, serverIn, serverOut) : null;
    }
    public async Task<List<Together.Group>> SyncGroupsAsync(CancellationToken ct = default) =>
        (await GetAsync<JsonElement>("SyncPlay/List", ct)) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(Together.ParseGroup).OfType<Together.Group>().ToList() : [];
    public async Task<Together.Group?> SyncGroupAsync(string groupId, CancellationToken ct = default) =>
        Together.ParseGroup(await GetAsync<JsonElement>("SyncPlay/" + Uri.EscapeDataString(groupId), ct));
    public Task SyncNewAsync(string name, CancellationToken ct = default) => SyncSendAsync("New", new JsonObject { ["GroupName"] = name }, ct);
    public Task SyncJoinAsync(string groupId, CancellationToken ct = default) => SyncSendAsync("Join", new JsonObject { ["GroupId"] = groupId }, ct);
    public Task SyncLeaveAsync(CancellationToken ct = default) => SyncSendAsync("Leave", null, ct);
    public Task SyncQueueAsync(string itemId, long startTicks, CancellationToken ct = default) =>
        SyncSendAsync("SetNewQueue", new JsonObject { ["PlayingQueue"] = new JsonArray(itemId), ["PlayingItemPosition"] = 0, ["StartPositionTicks"] = Math.Max(0, startTicks) }, ct);
    /// <summary>Pause, Unpause, Seek ({ PositionTicks }), Buffering and Ready ({ When, PositionTicks, IsPlaying, PlaylistItemId }), Ping.</summary>
    public async Task SyncSendAsync(string what, JsonObject? body, CancellationToken ct = default)
    {
        using var content = new StringContent(body?.ToJsonString() ?? "{}", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("SyncPlay/" + what, content, ct);
        await CheckAsync(response);
    }
    /// <param name="listening">Told true once the WebSocket is open, false when it closes: while it is closed, library
    /// changes go unheard and the page needs refreshing more often.</param>
    /// <param name="syncPlay">« Regarder ensemble »: Jellyfin's SyncPlayGroupUpdate and SyncPlayCommand, with their Data.</param>
    public async Task ListenAsync(Action changed, CancellationToken ct, Action<bool>? listening = null, Action<string, JsonElement>? syncPlay = null)
    {
        var delay = 2;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                // The session goes in the Authorization header, as for every other request. Jellyfin 10.11 and later
                // refuse an "api_key" in the address unless legacy authorization is turned on ("Token is required").
                socket.Options.SetRequestHeader("Authorization", AuthorizationHeader);
                var b = new UriBuilder(new Uri(_http.BaseAddress!, "socket")) { Scheme = _http.BaseAddress!.Scheme == "https" ? "wss" : "ws" };
                await socket.ConnectAsync(b.Uri, ct);
                using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeat = KeepAliveAsync(socket, heartbeatStop.Token);
                var buffer = new byte[8192];
                try
                {
                    listening?.Invoke(true);
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult part;
                        do
                        {
                            part = await socket.ReceiveAsync(buffer, ct);
                            if (part.MessageType == WebSocketMessageType.Close) break;
                            message.Write(buffer, 0, part.Count);
                            if (message.Length > 1_000_000) throw new IOException("Message trop volumineux.");
                        } while (!part.EndOfMessage);
                        if (part.MessageType == WebSocketMessageType.Close) break;
                        // Only a socket that actually talks resets the back-off: one closed right after opening keeps waiting longer.
                        delay = 2;
                        using var doc = JsonDocument.Parse(message.ToArray());
                        // Any other shape is ignored: an exception here would end the listening for the whole session.
                        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("MessageType", out var type) || type.ValueKind != JsonValueKind.String) continue;
                        if (type.GetString() is "LibraryChanged" or "UserDataChanged") changed();
                        else if (type.GetString() is "SyncPlayGroupUpdate" or "SyncPlayCommand" && syncPlay is not null && doc.RootElement.TryGetProperty("Data", out var data))
                            syncPlay(type.GetString()!, data.Clone());
                    }
                }
                finally
                {
                    listening?.Invoke(false);
                    heartbeatStop.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) { } catch (WebSocketException) { }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException) { }
            await Task.Delay(TimeSpan.FromSeconds(delay), ct);
            delay = Math.Min(30, delay * 2);
        }
    }
    private static async Task KeepAliveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var message = Encoding.UTF8.GetBytes("{\"MessageType\":\"KeepAlive\"}");
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
            await Task.Delay(TimeSpan.FromSeconds(25), ct);
        }
    }
    private static Task CheckAsync(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Connexion refusée ou session expirée. Reconnecte ton compte Jellyfin.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(StatusMessage(response.StatusCode), null, response.StatusCode);
        return Task.CompletedTask;
    }
    /// <summary>What an error answer of Jellyfin means, in words: it is starting, it failed, or it refused.</summary>
    public static string StatusMessage(HttpStatusCode status) => (int)status switch
    {
        // Jellyfin answers 503 to everything but its public information until it has started.
        503 => "Jellyfin démarre : réessaie dans un instant.",
        404 => "Jellyfin ne trouve pas ce qui est demandé : le titre a peut-être été retiré de la bibliothèque.",
        >= 500 => $"Jellyfin a rencontré une erreur (code {(int)status}). Réessaie dans un instant ; si ça continue, son journal en dit plus (Tableau de bord → Journaux).",
        _ => $"Jellyfin a refusé la demande (code {(int)status})."
    };
    public void Dispose() => _http.Dispose();
}
/// <summary>What Jellyfin said of a code approved for another device (Quick Connect).</summary>
public enum CodeApproval { Approved, Unknown, Disabled }
