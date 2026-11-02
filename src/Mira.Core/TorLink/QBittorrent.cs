using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mira.Core.TorLink;

// qBittorrent for private trackers: their torrents only reach a client the tracker allows (WebTorrent, under TorLink, never
// is), keep their « private » flag (no DHT, no peer exchange) and must go on sharing for the account's ratio. Mira for
// Windows, on the PC of Jellyfin, runs the real qBittorrent under a profile of its own (its own settings, downloads and
// window, beside a qBittorrent the person may use too) and drives it through its Web API, on 127.0.0.1 only, with a
// password Mira draws and keeps.

/// <summary>
/// qBittorrent for Windows as Mira installs it when it is not there yet: its official x64 installer, at the address and
/// SHA-256 of winget's reviewed manifest (qBittorrent.qBittorrent), checked again against the file. It installs for the
/// whole PC (Windows asks for the administrator's consent) and adds its own rule to Windows' firewall.
/// </summary>
public sealed record QBittorrentPackage(string Version, Uri Url, string Sha256)
{
    public const long MaximumSize = 200L << 20;
    public static readonly QBittorrentPackage Current = new("5.3.0",
        new("https://sourceforge.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-5.3.0/qbittorrent_5.3.0_x64_setup.exe/download"),
        "AFF5B3BDC00D6E5773D6099BA30F9696DD3DFD1669AB0F64CE5C4E7168486436");
    public string FileName => $"qbittorrent_{Version}_x64_setup.exe";
}

/// <summary>What Mira writes in the qBittorrent.ini of its profile before each start; the rest is the person's or qBittorrent's.</summary>
public static class QBittorrentSettings
{
    /// <summary>The Web API's account: one name, Mira's password.</summary>
    public const string Username = "mira";
    /// <summary>The category of every download Mira hands it: the downloads page lists these only.</summary>
    public const string Category = "Mira";

    /// <summary>Its settings in &lt;profile&gt;/qBittorrent/config: QSettings' ini file, named so on Windows, .conf elsewhere.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "qBittorrent.ini" : "qBittorrent.conf";

    /// <summary>
    /// The password as qBittorrent keeps it (Utils::Password::PBKDF2): PBKDF2-HMAC-SHA512, 100 000 rounds, a salt of 16
    /// bytes, 64 bytes out, « salt:key » in base64.
    /// </summary>
    public static string PasswordHash(string password, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA512, 64);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(key);
    }

    /// <summary>
    /// The keys Mira sets, by section of the .ini (QSettings: « Preferences/WebUI/Port » is « WebUI\Port » under
    /// [Preferences]): the Web UI on 127.0.0.1 and its port, behind Mira's account, without UPnP; the legal notice
    /// accepted (qBittorrent asks once otherwise, before anything else); started hidden in the notification area, which
    /// keeps it (closing the window keeps it running, and quitting asks nothing); no check for its updates, which would
    /// reach qbittorrent.org (Mira says when one is out).
    /// </summary>
    public static IReadOnlyList<(string Section, string Key, string Value)> Managed(int webPort, string passwordHash) =>
    [
        ("LegalNotice", "Accepted", "true"),
        ("Preferences", @"WebUI\Enabled", "true"),
        ("Preferences", @"WebUI\Address", "127.0.0.1"),
        ("Preferences", @"WebUI\Port", webPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("Preferences", @"WebUI\Username", Username),
        ("Preferences", @"WebUI\Password_PBKDF2", $"\"@ByteArray({passwordHash})\""),
        ("Preferences", @"WebUI\LocalHostAuth", "true"),
        ("Preferences", @"WebUI\UseUPnP", "false"),
        ("Preferences", @"WebUI\HostHeaderValidation", "true"),
        ("Preferences", @"WebUI\CSRFProtection", "true"),
        ("Preferences", @"General\SystrayEnabled", "true"),
        ("Preferences", @"General\CloseToTray", "true"),
        ("Preferences", @"General\CloseToTrayNotified", "true"),
        ("Preferences", @"General\MinimizeToTray", "true"),
        ("Preferences", @"General\ExitConfirm", "false"),
        ("Preferences", @"Advanced\updateCheck", "false"),
        ("GUI", "StartUpWindowState", "Hidden"),
    ];

    /// <summary>
    /// <paramref name="existing"/> (null: none yet) with Mira's keys set: every other line kept as it was, a missing
    /// section added at the end.
    /// </summary>
    public static string Ini(string? existing, int webPort, string passwordHash)
    {
        var lines = (existing ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        foreach (var group in Managed(webPort, passwordHash).GroupBy(x => x.Section))
        {
            var header = $"[{group.Key}]";
            var start = lines.FindIndex(x => x.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add(header);
                lines.AddRange(group.Select(x => $"{x.Key}={x.Value}"));
                continue;
            }
            var end = lines.FindIndex(start + 1, x => x.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            foreach (var (_, key, value) in group)
            {
                var at = lines.FindIndex(start + 1, end - start - 1, x => x.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) { lines[at] = $"{key}={value}"; continue; }
                // After the section's last setting, before the blank lines that part it from the next.
                var last = end;
                while (last > start + 1 && lines[last - 1].Trim().Length == 0) last--;
                lines.Insert(last, $"{key}={value}");
                end++;
            }
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// What Mira sets through the Web API at each start (app/setPreferences): where downloads go, the port peers reach,
    /// and new downloads started at once with nothing asked.
    /// </summary>
    public static Dictionary<string, object> Preferences(string savePath, int listenPort) => new()
    {
        ["save_path"] = savePath,
        ["temp_path_enabled"] = false,
        ["listen_port"] = listenPort,
        ["random_port"] = false,
        ["create_subfolder_enabled"] = true,
        ["add_stopped_enabled"] = false,
        ["start_paused_enabled"] = false,
        ["auto_tmm_enabled"] = false,
    };
}

/// <summary>
/// qBittorrent's finished downloads in Mira's category, read once (torrents/info, then torrents/files of each), for
/// <see cref="TorLinkImporter"/>: placed in the library by hard link or copy, never taken from qBittorrent, which goes on
/// sharing them.
/// </summary>
public sealed class QBittorrentHistory(IReadOnlyList<TorLinkCompletion> completions, IReadOnlyDictionary<string, TorrentInfo> files) : IDownloadSource
{
    /// <summary>The source Mira gives them: TorLink's are its search sites.</summary>
    public const string Source = "qbittorrent";

    public List<TorLinkCompletion>? ReadHistory() => completions.ToList();
    public TorrentInfo? ReadTorrent(string id, out bool refused) { refused = false; return files.GetValueOrDefault(id); }

    /// <summary>The finished ones of <paramref name="torrents"/>, with their files from <paramref name="filesOf"/>.</summary>
    public static async Task<QBittorrentHistory> ReadAsync(IReadOnlyList<QBittorrentTorrent> torrents, Func<string, CancellationToken, Task<List<TorrentFileEntry>>> filesOf, ISet<string> known, CancellationToken ct)
    {
        var done = new List<TorLinkCompletion>(); var files = new Dictionary<string, TorrentInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var torrent in torrents.Where(QBittorrentDownloads.Finished))
        {
            var at = torrent.CompletedOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(torrent.CompletedOn) : DateTimeOffset.UtcNow;
            done.Add(new(torrent.Hash, torrent.Name, Source, torrent.Size, torrent.SavePath, at));
            // Files are asked only for a download not placed yet: the others are not looked at again.
            if (!known.Contains(torrent.Hash)) files[torrent.Hash] = new(torrent.Name, await filesOf(torrent.Hash, ct));
        }
        return new(done, files);
    }

    /// <summary>
    /// Where qBittorrent downloads for Mira: beside the first library folder (on its drive: a hard link instead of a
    /// copy), never inside one, where Jellyfin would list unfinished files; otherwise in the person's Downloads.
    /// </summary>
    public static string DownloadFolder(MediaLibraries libraries)
    {
        var name = L("Téléchargements qBittorrent", "qBittorrent downloads");
        try
        {
            var roots = new[] { libraries.Movies, libraries.Series, libraries.Anime }.OfType<string>().Where(x => x.Trim().Length > 0)
                .Select(x => Path.GetFullPath(x.Trim()).TrimEnd('\\', '/')).ToList();
            bool InsideLibrary(string folder) => roots.Any(root => folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(folder, root, StringComparison.OrdinalIgnoreCase));
            if (roots.Count > 0 && Path.GetDirectoryName(roots[0]) is { Length: > 0 } parent && Path.Combine(parent, name) is var beside && !InsideLibrary(beside)) return beside;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name);
    }
}

/// <summary>A download in qBittorrent, as its Web API lists it (torrents/info): the fields Mira reads.</summary>
public sealed record QBittorrentTorrent(string Hash, string Name, string State, double Progress, long Speed, int Peers, long Size, long Completed, long Eta,
    string SavePath, string? ContentPath, string Category, long CompletedOn, double Ratio);

/// <summary>qBittorrent's downloads in the words of Mira's downloads page (<see cref="RelayDownload"/>).</summary>
public static class QBittorrentDownloads
{
    /// <summary>qBittorrent's eta when it cannot tell (100 days).</summary>
    public const long Infinite = 8_640_000;

    /// <summary>
    /// downloading, queued, paused, failed or seeding (finished: <see cref="RemoteLines"/> says « rangement »). qBittorrent 5
    /// says « stopped » where 4 said « paused ».
    /// </summary>
    public static string Status(string state) => state switch
    {
        "error" or "missingFiles" => "failed",
        "pausedDL" or "stoppedDL" => "paused",
        "queuedDL" or "checkingDL" or "checkingResumeData" or "allocating" or "moving" => "queued",
        "downloading" or "metaDL" or "forcedMetaDL" or "stalledDL" or "forcedDL" => "downloading",
        "uploading" or "stalledUP" or "forcedUP" or "queuedUP" or "checkingUP" or "pausedUP" or "stoppedUP" => "seeding",
        _ => "queued",
    };

    public static bool Finished(QBittorrentTorrent torrent) => Status(torrent.State) == "seeding" && torrent.Progress >= 1;

    public static RelayDownload Download(QBittorrentTorrent torrent)
    {
        var status = Status(torrent.State);
        return new(torrent.Hash, RemoteDownloads.CleanName(torrent.Name) ?? torrent.Hash, status, (int)Math.Clamp(Math.Floor(torrent.Progress * 100), 0, 100),
            status == "downloading" ? Math.Max(0, torrent.Speed) : 0, Math.Max(0, torrent.Peers), Math.Max(0, torrent.Size), Math.Clamp(torrent.Completed, 0, Math.Max(0, torrent.Size)),
            status == "downloading" && torrent.Eta is > 0 and < Infinite ? Math.Min(torrent.Eta, RemoteDownloads.MaxEta) : null,
            status == "failed" ? torrent.State == "missingFiles"
                ? L("Ses fichiers ne sont plus là où qBittorrent les a mis.", "Its files are no longer where qBittorrent put them.")
                : L("qBittorrent signale une erreur : ouvre-le sur le PC serveur pour la voir.", "qBittorrent reports an error: open it on the server PC to see it.") : null);
    }

    /// <summary>torrents/info: the list, with what Mira reads of each; nothing for anything else.</summary>
    public static List<QBittorrentTorrent> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return [];
        var torrents = new List<QBittorrentTorrent>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "hash")?.ToLowerInvariant() is not { } hash || !RemoteDownloads.IsInfoHash(hash)) continue;
            torrents.Add(new(hash, Text(item, "name") ?? hash, Text(item, "state") ?? "", Real(item, "progress"), Whole(item, "dlspeed"),
                (int)Math.Min(int.MaxValue, Whole(item, "num_seeds") + Whole(item, "num_leechs")), Whole(item, "size"), Whole(item, "completed"), Whole(item, "eta"),
                Text(item, "save_path") ?? "", Text(item, "content_path"), Text(item, "category") ?? "", Whole(item, "completion_on"), Real(item, "ratio")));
        }
        return torrents;
    }

    internal static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static double Real(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var real) && double.IsFinite(real) ? real : 0;
    internal static long Whole(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (value.TryGetInt64(out var whole) ? whole : value.TryGetDouble(out var real) ? (long)Math.Clamp(real, long.MinValue, long.MaxValue) : 0) : 0;
}

/// <summary>Refused by qBittorrent: a wrong password, an address it bans for too many, or an answer Mira can't read.</summary>
public sealed class QBittorrentException(string message) : Exception(message);

/// <summary>
/// qBittorrent's Web API (v2), on 127.0.0.1 only, under Mira's account: its session cookie is asked again whenever
/// qBittorrent forgets it (403). Never through a proxy: the address is this PC's.
/// </summary>
public sealed class QBittorrentClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _password;
    private readonly SemaphoreSlim _login = new(1, 1);
    private bool _signedIn;

    public QBittorrentClient(int port, string password, HttpMessageHandler? handler = null)
    {
        _password = password;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, UseCookies = true, CookieContainer = new CookieContainer() })
        { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v2/"), Timeout = TimeSpan.FromSeconds(15) };
        // The Web UI refuses a request whose Referer or Origin is another site (CSRF): Mira names it.
        _http.DefaultRequestHeaders.Referrer = new Uri($"http://127.0.0.1:{port}/");
    }

    /// <summary>qBittorrent's version (« v5.1.2 »), signed in, or null while it doesn't answer; refused = <see cref="QBittorrentException"/>.</summary>
    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        try { return (await TextAsync(HttpMethod.Get, "app/version", null, ct))?.Trim(); }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public async Task SignInAsync(CancellationToken ct)
    {
        await _login.WaitAsync(ct);
        try
        {
            using var content = new FormUrlEncodedContent([new("username", QBittorrentSettings.Username), new("password", _password)]);
            using var response = await _http.PostAsync("auth/login", content, ct);
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new QBittorrentException(L("qBittorrent refuse Mira pour un moment (trop d’essais) : il réessaiera.", "qBittorrent is refusing Mira for a while (too many attempts): it’ll try again."));
            if (!response.IsSuccessStatusCode || body != "Ok.")
                throw new QBittorrentException(L("qBittorrent n’accepte pas le mot de passe de Mira.", "qBittorrent doesn’t accept Mira’s password."));
            _signedIn = true;
        }
        finally { _login.Release(); }
    }

    /// <summary>
    /// Hands qBittorrent a magnet link or a .torrent, into <paramref name="savePath"/> under Mira's category. False when
    /// qBittorrent refuses it (« Fails. »: already there, or not a torrent).
    /// </summary>
    public async Task<bool> AddAsync(string? magnet, byte[]? torrent, string savePath, CancellationToken ct)
    {
        if (magnet is null == torrent is null) throw new ArgumentException("A magnet link or a .torrent.");
        HttpContent Content()
        {
            var form = new MultipartFormDataContent();
            if (magnet is not null) form.Add(new StringContent(magnet), "urls");
            if (torrent is not null)
            {
                var file = new ByteArrayContent(torrent);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
                form.Add(file, "torrents", "mira.torrent");
            }
            form.Add(new StringContent(savePath), "savepath");
            form.Add(new StringContent(QBittorrentSettings.Category), "category");
            form.Add(new StringContent("false"), "paused");
            form.Add(new StringContent("false"), "stopped");
            form.Add(new StringContent("false"), "autoTMM");
            return form;
        }
        var body = await TextAsync(HttpMethod.Post, "torrents/add", Content, ct, accept: [HttpStatusCode.UnsupportedMediaType, HttpStatusCode.Conflict]);
        return body is not null && !body.Trim().StartsWith("Fails", StringComparison.Ordinal);
    }

    /// <summary>app/setPreferences with <see cref="QBittorrentSettings.Preferences"/>.</summary>
    public async Task SetPreferencesAsync(IReadOnlyDictionary<string, object> preferences, CancellationToken ct) =>
        await TextAsync(HttpMethod.Post, "app/setPreferences", () => new FormUrlEncodedContent([new("json", JsonSerializer.Serialize(preferences))]), ct);

    /// <summary>Its downloads in Mira's category.</summary>
    public async Task<List<QBittorrentTorrent>> TorrentsAsync(CancellationToken ct)
    {
        var text = await TextAsync(HttpMethod.Get, "torrents/info?category=" + Uri.EscapeDataString(QBittorrentSettings.Category), null, ct);
        try { using var document = JsonDocument.Parse(text ?? ""); return QBittorrentDownloads.Parse(document.RootElement); }
        catch (JsonException) { throw new QBittorrentException(L("qBittorrent a répondu sans liste.", "qBittorrent answered without a list.")); }
    }

    /// <summary>A download's files (torrents/files), relative to its save path, as qBittorrent names them.</summary>
    public async Task<List<TorrentFileEntry>> FilesAsync(string hash, CancellationToken ct)
    {
        var text = await TextAsync(HttpMethod.Get, "torrents/files?hash=" + Uri.EscapeDataString(hash), null, ct);
        try
        {
            using var document = JsonDocument.Parse(text ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && QBittorrentDownloads.Text(x, "name") is { Length: > 0 })
                .Select(x => new TorrentFileEntry(QBittorrentDownloads.Text(x, "name")!.Replace('/', Path.DirectorySeparatorChar), QBittorrentDownloads.Whole(x, "size"))).ToList();
        }
        catch (JsonException) { throw new QBittorrentException(L("qBittorrent a répondu sans liste.", "qBittorrent answered without a list.")); }
    }

    /// <summary>
    /// pause, resume, or delete (its files with it, as « Annuler » does): qBittorrent 5 names the first two stop and start,
    /// 4 pause and resume (the other's address answers 404).
    /// </summary>
    public async Task ControlAsync(string hash, string action, CancellationToken ct)
    {
        Func<HttpContent> Form(bool deleteFiles = false) => () => new FormUrlEncodedContent(deleteFiles
            ? [new("hashes", hash), new("deleteFiles", "true")] : [new KeyValuePair<string, string>("hashes", hash)]);
        switch (action)
        {
            case "delete": await TextAsync(HttpMethod.Post, "torrents/delete", Form(deleteFiles: true), ct); break;
            case "pause" or "resume":
                var (current, older) = action == "pause" ? ("torrents/stop", "torrents/pause") : ("torrents/start", "torrents/resume");
                if (await TextAsync(HttpMethod.Post, current, Form(), ct, accept: [HttpStatusCode.NotFound]) is null)
                    await TextAsync(HttpMethod.Post, older, Form(), ct);
                break;
            default: throw new ArgumentException($"Unknown action « {action} ».");
        }
    }

    /// <summary>
    /// A request under Mira's session, signed in first or again when qBittorrent forgot it (403). A status in
    /// <paramref name="accept"/> comes back as null instead of an error.
    /// </summary>
    private async Task<string?> TextAsync(HttpMethod method, string path, Func<HttpContent>? content, CancellationToken ct, HttpStatusCode[]? accept = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!_signedIn) await SignInAsync(ct);
            using var request = new HttpRequestMessage(method, path) { Content = content?.Invoke() };
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0) { _signedIn = false; continue; }
            if (accept?.Contains(response.StatusCode) == true) return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(L($"qBittorrent a répondu avec une erreur ({(int)response.StatusCode}).", $"qBittorrent answered with an error ({(int)response.StatusCode})."), null, response.StatusCode);
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    public void Dispose() { _http.Dispose(); _login.Dispose(); }
}
