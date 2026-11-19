using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mira.Core.TorLink;

// qBittorrent for private trackers: their torrents only reach a client the tracker allows (WebTorrent, under TorLink, never
// is), keep their « private » flag (no DHT, no peer exchange) and must go on sharing for a while (« hit and run »). Mira for
// Windows, on the PC of Jellyfin, runs the real qBittorrent under a profile of its own (its own settings, downloads and
// window, beside a qBittorrent the person may use too) and drives it through its Web API, on 127.0.0.1 only, with a
// password Mira draws and keeps. Sharing (ogsas, 2026-11-15): a public download stops as soon as it is finished, a
// private one after 72 hours (QBittorrentSettings.PrivateSharing), never longer.

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
    /// reach qbittorrent.org (Mira says when one is out). The Web UI's keys twice: qBittorrent 5.2 and later read them
    /// under [WebUI] (« WebUI/Port »), and move those of [Preferences] there only once, at their first start; 5.1 and
    /// before read them under [Preferences] only.
    /// </summary>
    public static IReadOnlyList<(string Section, string Key, string Value)> Managed(int webPort, string passwordHash)
    {
        (string Key, string Value)[] webUI =
        [
            ("Enabled", "true"),
            ("Address", "127.0.0.1"),
            ("Port", webPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Username", Username),
            ("Password_PBKDF2", $"\"@ByteArray({passwordHash})\""),
            ("LocalHostAuth", "true"),
            ("UseUPnP", "false"),
            ("HostHeaderValidation", "true"),
            ("CSRFProtection", "true"),
        ];
        return
        [
            ("LegalNotice", "Accepted", "true"),
            .. webUI.Select(x => ("Preferences", @"WebUI\" + x.Key, x.Value)),
            .. Others,
            .. webUI.Select(x => ("WebUI", x.Key, x.Value)),
        ];
    }

    private static readonly (string Section, string Key, string Value)[] Others =
    [
        ("Preferences", @"General\SystrayEnabled", "true"),
        ("Preferences", @"General\CloseToTray", "true"),
        ("Preferences", @"General\CloseToTrayNotified", "true"),
        ("Preferences", @"General\MinimizeToTray", "true"),
        ("Preferences", @"General\ExitConfirm", "false"),
        ("Preferences", @"Advanced\updateCheck", "false"),
        ("GUI", "StartUpWindowState", "Hidden"),
        // The same as the Web API sets (Preferences), here so that a qBittorrent Mira starts reads them at once.
        ("BitTorrent", @"Session\DiskIOType", SimplePreadPwrite.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("BitTorrent", @"Session\PieceExtentAffinity", "true"),
        ("BitTorrent", @"Session\Preallocation", "false"),
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
    /// new downloads started at once with nothing asked, and sharing stopped (never removed) after
    /// <see cref="PrivateSharing"/>: what a private tracker asks; a public download Mira stops sooner
    /// (<see cref="QBittorrentDownloads.StopsSharing"/>).
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
        ["max_ratio_enabled"] = false,
        ["max_seeding_time_enabled"] = true,
        ["max_seeding_time"] = (int)PrivateSharing.TotalMinutes,
        ["max_inactive_seeding_time_enabled"] = false,
        // 0: stop (« pause » before qBittorrent 5); 1 and after remove the download.
        ["max_ratio_act"] = 0,
        // A big download onto a slow drive (a USB hard disk, the Dell's 6 TB): left to itself, libtorrent starts thousands
        // of pieces at once, from every peer, and writes them through memory-mapped files, which Windows flushes late and
        // all together. Received faster than the drive writes, pieces pile up unchecked (12 993 begun, 15 % checked, for
        // 72 % received, 2026-11-15), and each pause or restart then rereads them all before downloading on: hours at a
        // few KB/s. Piece extent affinity has peers finish the pieces next to each other first; « simple pread/pwrite »
        // (qBittorrent 5.1 and later, libtorrent 2) writes each block as it comes and keeps memory maps for reading
        // only, what qBittorrent advises on Windows. The disk I/O type is read at qBittorrent's next start.
        ["enable_piece_extent_affinity"] = true,
        ["disk_io_type"] = SimplePreadPwrite,
        // Never each file's room taken when it is added (tried 2026-11-18 against fragmented packs): qBittorrent runs without
        // the right to set where a file's valid data ends (SeManageVolumePrivilege), so NTFS writes zeros from there to every
        // piece written further on. On the Dell's USB disk (2026-11-19), a 65.8 GB pack had 55 GB of zeros written for 8.4 GB
        // received, the drive busy 100 % of the time, and Jellyfin's reads of the film being watched took 140 ms (up to
        // 0.5 s): playback stuck loading. Sparse files again; pieces stay grouped by the affinity above.
        ["preallocate_all"] = false,
        // No second read of a whole download once it is done: each piece is already checked as it comes (off by default, kept so).
        ["recheck_completed_torrents"] = false,
        // Watching first (<see cref="PlaybackFirst"/>): while something plays from this PC's Jellyfin, Mira turns on
        // qBittorrent's other speed limits, these, and turns them off a minute after; never by qBittorrent's own schedule.
        ["alt_dl_limit"] = PlaybackFirst.Download,
        ["alt_up_limit"] = PlaybackFirst.Upload,
        ["scheduler_enabled"] = false,
    };

    /// <summary>qBittorrent's DiskIOType « Simple pread/pwrite » (5.1 and later; before, an unknown value is its default).</summary>
    public const int SimplePreadPwrite = 3;

    /// <summary>
    /// How long a finished download goes on sharing at most: 72 hours, what private trackers ask most often before a
    /// download no longer counts as « hit and run ».
    /// </summary>
    public static readonly TimeSpan PrivateSharing = TimeSpan.FromHours(72);
}

/// <summary>
/// qBittorrent's finished downloads in Mira's category, read once (torrents/info, then torrents/files of each), for
/// <see cref="TorLinkImporter"/>: placed in the library by hard link or copy, never taken from qBittorrent (a private one
/// goes on sharing for a while).
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
    /// Where qBittorrent downloads for Mira: beside the first library folder (on its volume: a hard link instead of a
    /// copy), never inside one, where Jellyfin would list unfinished files; otherwise in the person's Downloads.
    /// </summary>
    public static string DownloadFolder(MediaLibraries libraries, Func<string, string?>? volumeOf = null)
    {
        volumeOf ??= Volumes.Of;
        var name = L("Téléchargements qBittorrent", "qBittorrent downloads");
        try
        {
            var roots = new[] { libraries.Movies, libraries.Series, libraries.Anime }.OfType<string>().Where(x => x.Trim().Length > 0)
                .Select(x => Path.GetFullPath(x.Trim()).TrimEnd('\\', '/')).ToList();
            bool InsideLibrary(string folder) => roots.Any(root => folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(folder, root, StringComparison.OrdinalIgnoreCase));
            if (roots.Count > 0)
            {
                // A library folder that is a mount point itself has its parent on another volume: the root of its own then.
                var volume = volumeOf(roots[0]);
                var choices = new[] { Path.GetDirectoryName(roots[0]) is { Length: > 0 } parent ? Path.Combine(parent, name) : null,
                    volume is { Length: > 0 } ? Path.Combine(Volumes.Folder(volume), name) : null };
                if (choices.OfType<string>().FirstOrDefault(x => !InsideLibrary(x) && Volumes.Same(volumeOf(x), volume)) is { } beside) return beside;
                if (choices[0] is { } first && !InsideLibrary(first)) return first;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", name);
    }

    /// <summary>
    /// The folder qBittorrent downloads to from now on, given the one kept (<paramref name="kept"/>, empty the first time):
    /// kept while it is on the library's volume, or when no better one is; otherwise <see cref="DownloadFolder"/> on that
    /// volume (a drive mounted in the library's folder, for one). Downloads qBittorrent already has keep their folder: it
    /// shares them from there.
    /// </summary>
    public static string FolderToUse(string kept, MediaLibraries libraries, Func<string, string?>? volumeOf = null)
    {
        volumeOf ??= Volumes.Of;
        var beside = DownloadFolder(libraries, volumeOf);
        if (kept.Trim().Length == 0) return beside;
        try
        {
            var volumes = new[] { libraries.Movies, libraries.Series, libraries.Anime }.OfType<string>().Where(x => x.Trim().Length > 0)
                .Select(x => volumeOf(Path.GetFullPath(x.Trim()))).Select(x => x is null ? null : Path.TrimEndingDirectorySeparator(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return volumes is [{ Length: > 0 } volume] && !Volumes.Same(volumeOf(Path.GetFullPath(kept)), volume) && Volumes.Same(volumeOf(beside), volume) ? beside : kept;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return kept; }
    }
}

/// <summary>A download in qBittorrent, as its Web API lists it (torrents/info): the fields Mira reads.</summary>
public sealed record QBittorrentTorrent(string Hash, string Name, string State, double Progress, long Speed, int Peers, long Size, long Completed, long Eta,
    string SavePath, string? ContentPath, string Category, long CompletedOn, double Ratio, bool? Private = null);

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

    /// <summary>
    /// A finished download Mira stops now: a public one still sharing. A private one, or one qBittorrent doesn't say
    /// (no metadata, qBittorrent 4), goes on until <see cref="QBittorrentSettings.PrivateSharing"/>.
    /// </summary>
    public static bool StopsSharing(QBittorrentTorrent torrent) =>
        Finished(torrent) && torrent.Private == false && torrent.State is not ("pausedUP" or "stoppedUP");

    /// <param name="unreachable">Its folder can't be reached (<see cref="Reachable"/> false): its drive unplugged, said so, not « files gone ».</param>
    public static RelayDownload Download(QBittorrentTorrent torrent, bool unreachable = false)
    {
        var status = Status(torrent.State);
        return new(torrent.Hash, RemoteDownloads.CleanName(torrent.Name) ?? torrent.Hash, status, (int)Math.Clamp(Math.Floor(torrent.Progress * 100), 0, 100),
            status == "downloading" ? Math.Max(0, torrent.Speed) : 0, Math.Max(0, torrent.Peers), Math.Max(0, torrent.Size), Math.Clamp(torrent.Completed, 0, Math.Max(0, torrent.Size)),
            status == "downloading" && torrent.Eta is > 0 and < Infinite ? Math.Min(torrent.Eta, RemoteDownloads.MaxEta) : null,
            status == "failed" ? unreachable ? Unreachable(torrent.SavePath)
                : torrent.State == "missingFiles"
                ? L("Ses fichiers ne sont plus là où qBittorrent les a mis.", "Its files are no longer where qBittorrent put them.")
                : L("qBittorrent signale une erreur : ouvre-le sur le PC serveur pour la voir.", "qBittorrent reports an error: open it on the server PC to see it.") : null);
    }

    /// <summary>
    /// A download that failed because its folder's drive is unplugged (a USB disk, a drive mounted in a folder): the
    /// person plugs it back in, Mira has qBittorrent check it again and start it (<see cref="AfterDisk"/>).
    /// </summary>
    public static string Unreachable(string folder) => L(
        $"« {folder} » est injoignable : son disque est sans doute débranché. Rebranche-le, le téléchargement reprend tout seul.",
        $"“{folder}” can’t be reached: its drive is probably unplugged. Plug it back in and the download picks up on its own.");

    /// <summary>A new download refused while the folder qBittorrent downloads to can't be reached.</summary>
    public static string FolderUnreachable(string folder) => L(
        $"qBittorrent télécharge dans « {folder} », injoignable : son disque est sans doute débranché. Rebranche-le puis réessaie.",
        $"qBittorrent downloads to “{folder}”, which can’t be reached: its drive is probably unplugged. Plug it back in, then try again.");

    /// <summary>
    /// The drive under <paramref name="folder"/> answers: the folder itself, or, once it is gone (moved, deleted), the
    /// nearest folder above it, read. False for a drive unplugged (no root left) or a drive mounted in a folder that is
    /// unplugged (the folder is there, reading it fails). <paramref name="make"/>: the folder made first when missing.
    /// </summary>
    public static bool Reachable(string folder, bool make = false)
    {
        try
        {
            var path = Path.GetFullPath(folder.Trim());
            if (make) Directory.CreateDirectory(path);
            while (!Directory.Exists(path)) { if (Path.GetDirectoryName(path) is not { } parent) return false; path = parent; }
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return false; }
    }

    /// <summary>
    /// What Mira does with a failed download, from what it saw before (<paramref name="waiting"/>: its drive was found
    /// unplugged): « wait » while the drive is still away, « recheck » once it is back (qBittorrent checks what it has,
    /// then goes on), nothing for a failure Mira didn't see start with an unplugged drive (files deleted, another error).
    /// </summary>
    public static string? AfterDisk(QBittorrentTorrent torrent, bool reachable, bool waiting) =>
        Status(torrent.State) != "failed" ? null : !reachable ? "wait" : waiting ? "recheck" : null;

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
                Text(item, "save_path") ?? "", Text(item, "content_path"), Text(item, "category") ?? "", Whole(item, "completion_on"), Real(item, "ratio"),
                Flag(item, "private") ?? Flag(item, "isPrivate")));
        }
        return torrents;
    }

    internal static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>A true or false; null when absent or null (qBittorrent 4, or a magnet without its metadata yet).</summary>
    internal static bool? Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;
    internal static double Real(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var real) && double.IsFinite(real) ? real : 0;
    internal static long Whole(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (value.TryGetInt64(out var whole) ? whole : value.TryGetDouble(out var real) ? (long)Math.Clamp(real, long.MinValue, long.MaxValue) : 0) : 0;
}

/// <summary>Refused by qBittorrent: a wrong password, an address it bans for too many, or an answer Mira can't read.</summary>
public sealed class QBittorrentException(string message, bool signIn = false) : Exception(message)
{
    /// <summary>Its sign-in was refused (password, or too many attempts): what a fresh start of Mira's own qBittorrent mends.</summary>
    public bool SignIn => signIn;
}

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
                throw new QBittorrentException(L("qBittorrent refuse Mira pour un moment (trop d’essais) : il réessaiera.", "qBittorrent is refusing Mira for a while (too many attempts): it’ll try again."), signIn: true);
            // qBittorrent 5.1 and before: 200 « Ok. », or « Fails. »; 5.2 and later: 204 without a body, or 401.
            if (!response.IsSuccessStatusCode || body is not ("Ok." or ""))
                throw new QBittorrentException(L("qBittorrent n’accepte pas le mot de passe de Mira.", "qBittorrent doesn’t accept Mira’s password."), signIn: true);
            _signedIn = true;
        }
        finally { _login.Release(); }
    }

    /// <summary>
    /// Hands qBittorrent a magnet link or a .torrent, into <paramref name="savePath"/> under Mira's category. False when
    /// qBittorrent refuses it (« Fails. »: already there, or not a torrent).
    /// </summary>
    /// <paramref name="look"/>: a pasted link to read (« Coller un lien magnet »), under <see cref="MagnetLinks.LookCategory"/>: qBittorrent
    /// stops it as soon as it has its metadata, before any of its files is downloaded (<see cref="ChooseAsync"/> starts it).
    public async Task<bool> AddAsync(string? magnet, byte[]? torrent, string savePath, CancellationToken ct, bool look = false)
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
            form.Add(new StringContent(look ? MagnetLinks.LookCategory : QBittorrentSettings.Category), "category");
            // qBittorrent 4.5 and later (Web API 2.8.15): stopped once its metadata comes.
            if (look) form.Add(new StringContent("MetadataReceived"), "stopCondition");
            form.Add(new StringContent("false"), "paused");
            form.Add(new StringContent("false"), "stopped");
            form.Add(new StringContent("false"), "autoTMM");
            return form;
        }
        // A category qBittorrent doesn't know yet: made first (an answer of 409 says it was there).
        await CategoryAsync(look ? MagnetLinks.LookCategory : QBittorrentSettings.Category, ct);
        var body = await TextAsync(HttpMethod.Post, "torrents/add", Content, ct, accept: [HttpStatusCode.UnsupportedMediaType, HttpStatusCode.Conflict]);
        return body is not null && !body.Trim().StartsWith("Fails", StringComparison.Ordinal);
    }

    private readonly HashSet<string> _categories = [];
    private async Task CategoryAsync(string name, CancellationToken ct)
    {
        if (_categories.Contains(name)) return;
        await TextAsync(HttpMethod.Post, "torrents/createCategory", () => new FormUrlEncodedContent([new("category", name), new("savePath", "")]), ct, accept: [HttpStatusCode.Conflict]);
        _categories.Add(name);
    }

    /// <summary>A download whatever its category (a link being read included); null when qBittorrent hasn't it.</summary>
    public async Task<QBittorrentTorrent?> TorrentAsync(string hash, CancellationToken ct)
    {
        var text = await TextAsync(HttpMethod.Get, "torrents/info?hashes=" + Uri.EscapeDataString(hash), null, ct);
        try { using var document = JsonDocument.Parse(text ?? ""); return QBittorrentDownloads.Parse(document.RootElement).FirstOrDefault(x => x.Hash == hash); }
        catch (JsonException) { throw new QBittorrentException(L("qBittorrent a répondu sans liste.", "qBittorrent answered without a list.")); }
    }

    /// <summary>
    /// Every file of a download with its index in qBittorrent (« Coller un lien magnet »), as its paths are inside the link;
    /// empty until qBittorrent has its metadata.
    /// </summary>
    public async Task<List<LinkFile>> ContentAsync(string hash, CancellationToken ct)
    {
        var text = await TextAsync(HttpMethod.Get, "torrents/files?hash=" + Uri.EscapeDataString(hash), null, ct, accept: [HttpStatusCode.NotFound]);
        try
        {
            using var document = JsonDocument.Parse(text ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var files = new List<LinkFile>(); var position = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                // The index qBittorrent gives (Web API 2.8.2 and later), otherwise its place in the list: the same.
                var index = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : position;
                position++;
                if (item.ValueKind == JsonValueKind.Object && QBittorrentDownloads.Text(item, "name") is { Length: > 0 } name) files.Add(new(index, name, QBittorrentDownloads.Whole(item, "size")));
            }
            return files;
        }
        catch (JsonException) { throw new QBittorrentException(L("qBittorrent a répondu sans liste.", "qBittorrent answered without a list.")); }
    }

    /// <summary>
    /// The files of a link being read (<see cref="AddAsync"/> with look); empty until its metadata comes. Read, it is stopped
    /// here too: qBittorrent's stop condition only waits for metadata it hasn't, so a link it had before would download on.
    /// </summary>
    public async Task<List<LinkFile>> ReadAsync(string hash, CancellationToken ct)
    {
        var files = await ContentAsync(hash, ct);
        if (files.Count > 0 && await TorrentAsync(hash, ct) is { Category: MagnetLinks.LookCategory } read && QBittorrentDownloads.Status(read.State) != "paused")
            await ControlAsync(hash, "pause", ct);
        return files;
    }

    /// <summary>
    /// A link read before, downloaded now: only <paramref name="files"/> (null: all of them; the others at priority 0, never
    /// downloaded), into Mira's category, then started.
    /// </summary>
    public async Task ChooseAsync(string hash, IReadOnlyCollection<int>? files, CancellationToken ct)
    {
        if (files is not null)
        {
            var skipped = (await ContentAsync(hash, ct)).Select(x => x.Index).Where(x => !files.Contains(x)).ToList();
            // A long list in parts: qBittorrent reads each « id » list whole.
            foreach (var part in skipped.Chunk(500))
                await TextAsync(HttpMethod.Post, "torrents/filePrio", () => new FormUrlEncodedContent([new("hash", hash), new("id", string.Join('|', part)), new("priority", "0")]), ct);
        }
        await CategoryAsync(QBittorrentSettings.Category, ct);
        await TextAsync(HttpMethod.Post, "torrents/setCategory", () => new FormUrlEncodedContent([new("hashes", hash), new("category", QBittorrentSettings.Category)]), ct);
        await ControlAsync(hash, "resume", ct);
    }

    /// <summary>The links read and not downloaded (<see cref="MagnetLinks.LookCategory"/>), with when they were added.</summary>
    public async Task<List<(string Hash, DateTimeOffset Added)>> LooksAsync(CancellationToken ct)
    {
        var text = await TextAsync(HttpMethod.Get, "torrents/info?category=" + Uri.EscapeDataString(MagnetLinks.LookCategory), null, ct);
        try
        {
            using var document = JsonDocument.Parse(text ?? "[]");
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && QBittorrentDownloads.Text(x, "hash") is { } h && RemoteDownloads.IsInfoHash(h.ToLowerInvariant()))
                .Select(x => (QBittorrentDownloads.Text(x, "hash")!.ToLowerInvariant(), DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, QBittorrentDownloads.Whole(x, "added_on"))))).ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>
    /// qBittorrent's other speed limits (<see cref="PlaybackFirst"/>) on or off; true when it changed. Read then toggled
    /// (transfer/speedLimitsMode, toggleSpeedLimitsMode): what 4.x and 5.x both answer.
    /// </summary>
    public async Task<bool> SlowAsync(bool slow, CancellationToken ct)
    {
        var mode = (await TextAsync(HttpMethod.Get, "transfer/speedLimitsMode", null, ct))?.Trim();
        if ((mode == "1") == slow) return false;
        await TextAsync(HttpMethod.Post, "transfer/toggleSpeedLimitsMode", null, ct);
        return true;
    }

    /// <summary>app/shutdown: qBittorrent saves its settings and list, then quits.</summary>
    public async Task QuitAsync(CancellationToken ct) => await TextAsync(HttpMethod.Post, "app/shutdown", null, ct);

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
            // A file left out when the link was chosen from (priority 0) is never downloaded: never placed either.
            return document.RootElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && QBittorrentDownloads.Text(x, "name") is { Length: > 0 } && !(x.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == 0))
                .Select(x => new TorrentFileEntry(QBittorrentDownloads.Text(x, "name")!.Replace('/', Path.DirectorySeparatorChar), QBittorrentDownloads.Whole(x, "size"))).ToList();
        }
        catch (JsonException) { throw new QBittorrentException(L("qBittorrent a répondu sans liste.", "qBittorrent answered without a list.")); }
    }

    /// <summary>
    /// pause, resume, delete (its files with it, as « Annuler » does) or recheck (its files read again, after a drive came
    /// back): qBittorrent 5 names the first two stop and start, 4 pause and resume (the other's address answers 404).
    /// </summary>
    public async Task ControlAsync(string hash, string action, CancellationToken ct)
    {
        Func<HttpContent> Form(bool deleteFiles = false) => () => new FormUrlEncodedContent(deleteFiles
            ? [new("hashes", hash), new("deleteFiles", "true")] : [new KeyValuePair<string, string>("hashes", hash)]);
        switch (action)
        {
            case "delete": await TextAsync(HttpMethod.Post, "torrents/delete", Form(deleteFiles: true), ct); break;
            case "recheck": await TextAsync(HttpMethod.Post, "torrents/recheck", Form(), ct); break;
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
