using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// Jellyfin Server for Windows as Mira installs it: the official installer, at the address and SHA-256 of winget's
/// reviewed manifest (Jellyfin.Server). It installs Jellyfin for the whole PC, as a service under Network Service.
/// </summary>
public sealed record JellyfinServerPackage(string Version, Uri Url, string Sha256)
{
    public const long MaximumSize = 600L << 20;
    public static readonly JellyfinServerPackage Current = new("12.1",
        new("https://repo.jellyfin.org/files/server/windows/stable/v12.1/amd64/jellyfin_12.1_windows-x64.exe"),
        "28E11B817B3410C860733591FBB88B9248B8D0B31CF2793B65CE0588CFFE3DA3");
    public string FileName => Path.GetFileName(Url.AbsolutePath);
}

/// <summary>Films, Séries and Animes under one root: the names Jellyfin's libraries, Mira and TorLink's placement share.</summary>
public sealed record MediaFolders(string Root, string Movies, string Series, string Anime)
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Jellyfin");
    public static MediaFolders Under(string root)
    {
        var full = Path.GetFullPath(root.Trim());
        return new(full, Path.Combine(full, "Films"), Path.Combine(full, "Séries"), Path.Combine(full, "Animes"));
    }
    /// <summary>
    /// Why <paramref name="root"/> cannot hold the media folders, or null. Jellyfin's service is given read access to
    /// the whole root: never a drive, the user's own folder or a system folder.
    /// </summary>
    public static string? Refusal(string root)
    {
        root = root.Trim();
        if (!Path.IsPathFullyQualified(root)) return $"Indique un dossier complet, par exemple {DefaultRoot}.";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "").Equals(full, StringComparison.OrdinalIgnoreCase))
            return $"Choisis un dossier plutôt que tout le disque, par exemple {Path.Combine(Path.GetPathRoot(full)!, "Vidéos")}.";
        var reserved = new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
            .Select(Environment.GetFolderPath).Where(x => x.Length > 0).Select(Path.TrimEndingDirectorySeparator);
        return reserved.Any(x => x.Equals(full, StringComparison.OrdinalIgnoreCase)) ? $"Choisis un dossier rien que pour tes vidéos, par exemple {DefaultRoot}." : null;
    }
    public void Create() { foreach (var folder in new[] { Movies, Series, Anime }) Directory.CreateDirectory(folder); }
    /// <summary>
    /// Jellyfin's service runs as Network Service, which may not read a user's own folders: without this, its libraries
    /// would stay empty. The person who created the root owns it and may grant this without administrator rights.
    /// </summary>
    public void AllowJellyfinService() => JellyfinServiceAccess.Grant(Root);
}

/// <summary>Read access for the account Jellyfin's Windows service runs as (Network Service).</summary>
public static class JellyfinServiceAccess
{
    /// <summary>Lets the service read a folder and everything created in it.</summary>
    public static void Grant(string folder) { if (OperatingSystem.IsWindows()) GrantFolder(folder); }
    /// <summary>
    /// A hard link, or a move on the same disk, keeps the permissions of the downloaded file instead of taking the
    /// library folder's. When that folder lets the service read its files, the placed file gets the same right; folders
    /// that do not grant it are left as they are. Best effort: a refusal leaves the file as it was.
    /// </summary>
    public static void ShareWithFolder(string file)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { ShareFile(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
    }
    [SupportedOSPlatform("windows")]
    private static void GrantFolder(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Service, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
    [SupportedOSPlatform("windows")]
    private static void ShareFile(string file)
    {
        var service = Service;
        var granted = new DirectoryInfo(Path.GetDirectoryName(file)!).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(x => x.IdentityReference == service && x.AccessControlType == AccessControlType.Allow && x.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit) && x.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        if (!granted) return;
        var info = new FileInfo(file);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
    /// <summary>True when <paramref name="path"/> carries a rule of its own (not inherited) letting the service read it.</summary>
    public static bool HasOwnRule(string path) => OperatingSystem.IsWindows() && OwnRule(path);
    [SupportedOSPlatform("windows")]
    private static bool OwnRule(string path)
    {
        FileSystemSecurity security = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        return security.GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
            .Any(x => x.IdentityReference == Service && x.AccessControlType == AccessControlType.Allow && x.FileSystemRights.HasFlag(FileSystemRights.ReadData));
    }
    [SupportedOSPlatform("windows")] private static SecurityIdentifier Service => new(WellKnownSidType.NetworkServiceSid, null);
}

/// <summary>What a Jellyfin server says about itself before anyone signs in.</summary>
public sealed record ServerState(string Version, bool WizardCompleted);
/// <summary>What answers at a Jellyfin address: nothing, Jellyfin while it loads, Jellyfin ready, or another program.</summary>
public enum ServerAnswer { Nothing, Loading, Ready, Other }
public sealed record ServerProbe(ServerAnswer Answer, ServerState? State = null);

/// <summary>Brings a newly installed Jellyfin to a ready library: its first-run wizard, done through the same routes as its own page.</summary>
public static partial class JellyfinSetup
{
    public static readonly Uri LocalServer = new("http://127.0.0.1:8096/");
    /// <summary>Jellyfin's own rule for user names (letters, digits, spaces, - _ ' . @ +; no space at either end).</summary>
    public static bool IsValidUserName(string name) => !string.IsNullOrWhiteSpace(name) && UserName().IsMatch(name) && name is not ("." or "..");
    /// <summary>The server's state; null while nothing answers on that address, while Jellyfin is still loading, or when another program answers.</summary>
    public static async Task<ServerState?> StateAsync(Uri server, HttpMessageHandler? handler = null, CancellationToken ct = default) =>
        (await ProbeAsync(server, handler, ct).ConfigureAwait(false)).State;
    /// <summary>What answers at <paramref name="server"/>: told apart so that Jellyfin is never installed over one that is starting, or beside another program on its port.</summary>
    public static async Task<ServerProbe> ProbeAsync(Uri server, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = Client(server, handler, TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("System/Info/Public", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await ReadAtMostAsync(response.Content, 64 << 10, ct).ConfigureAwait(false);
            // While it starts, Jellyfin 12 answers 503: "Jellyfin Server is loading", or nothing but Retry-After.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return new(ServerAnswer.Loading);
            if (!response.IsSuccessStatusCode) return new(ServerAnswer.Other);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Property(root, "Id") is not { ValueKind: JsonValueKind.String }) return new(ServerAnswer.Other);
            var state = new ServerState(Property(root, "Version") is { ValueKind: JsonValueKind.String } version ? version.GetString()! : "",
                Property(root, "StartupWizardCompleted") is { ValueKind: JsonValueKind.True });
            // Its startup page answers this route too, in camelCase and always "wizard not completed": only the
            // server itself answers System/Ping, so that tells a loading Jellyfin from a ready one.
            using var ping = await http.GetAsync("System/Ping", ct).ConfigureAwait(false);
            return ping.IsSuccessStatusCode ? new(ServerAnswer.Ready, state) : new(ServerAnswer.Loading);
        }
        catch (JsonException) { return new(ServerAnswer.Other); }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return new(ServerAnswer.Nothing); }
    }
    private static JsonElement? Property(JsonElement json, string name) =>
        json.EnumerateObject().Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(x => (JsonElement?)x.Value).FirstOrDefault();
    /// <summary>True when a program on this PC listens on <paramref name="port"/>, on any of its addresses.</summary>
    public static bool PortInUse(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port); }
        catch (NetworkInformationException) { return false; }
    }
    private static async Task<string> ReadAtMostAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[limit]; var total = 0;
        while (total < limit && await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false) is var read and > 0) total += read;
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
    /// <summary>Waits for the server to answer, polling every <paramref name="interval"/> (two seconds by default).</summary>
    public static async Task<ServerState> WaitAsync(Uri server, TimeSpan timeout, HttpMessageHandler? handler = null, TimeSpan? interval = null, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await StateAsync(server, handler, ct).ConfigureAwait(false) is { } state) return state;
            if (DateTime.UtcNow >= deadline) throw new InstallException("Jellyfin ne répond pas. Redémarre le PC, ou lance « Jellyfin Server » depuis le menu Démarrer, puis réessaie.");
            await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }
    /// <summary>
    /// Completes the first-run wizard: French interface and metadata, the administrator account, the Films, Séries and
    /// Animes libraries, access from the local network only, then the end of the wizard. Steps already done are kept,
    /// so an attempt cut off halfway can simply be run again with the same account.
    /// </summary>
    public static async Task ConfigureAsync(Uri server, string user, string password, MediaFolders folders, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (!IsValidUserName(user)) throw new ArgumentException("Le nom d’utilisateur peut contenir des lettres, des chiffres, des espaces et - _ ' . @ +, sans espace au début ni à la fin.");
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Choisis un mot de passe pour ton compte Jellyfin.");
        using var http = Client(server, handler, TimeSpan.FromSeconds(30));
        await Send(http, HttpMethod.Post, "Startup/Configuration", new { ServerName = Environment.MachineName, UICulture = "fr", MetadataCountryCode = "FR", PreferredMetadataLanguage = "fr" }, "la langue", ct).ConfigureAwait(false);
        // Reading the first user creates it.
        await Send(http, HttpMethod.Get, "Startup/User", null, "le compte", ct).ConfigureAwait(false);
        using (var account = await http.PostAsJsonAsync("Startup/User", new { Name = user, Password = password }, ct).ConfigureAwait(false))
        {
            // Refused once a password is set: only an earlier run with this same account may go on.
            if (account.StatusCode == HttpStatusCode.Forbidden && !await SignsInAsync(server, user, password, handler, ct).ConfigureAwait(false))
                throw new InstallException("Ce Jellyfin a déjà un compte administrateur, avec un autre nom ou mot de passe. Termine sa configuration sur http://127.0.0.1:8096, puis connecte-toi.");
            if (!account.IsSuccessStatusCode && account.StatusCode != HttpStatusCode.Forbidden) throw Refused("le compte", account.StatusCode);
        }
        var existing = await GetLibraryNamesAsync(http, ct).ConfigureAwait(false);
        foreach (var (name, type, path) in new[] { ("Films", "movies", folders.Movies), ("Séries", "tvshows", folders.Series), ("Animes", "tvshows", folders.Anime) })
        {
            if (existing.Contains(name)) continue;
            // The path goes in the body: the query form splits paths on commas.
            await Send(http, HttpMethod.Post, $"Library/VirtualFolders?name={Uri.EscapeDataString(name)}&collectionType={type}&refreshLibrary=false",
                new { LibraryOptions = new { PathInfos = new[] { new { Path = path } }, PreferredMetadataLanguage = "fr", MetadataCountryCode = "FR", EnableRealtimeMonitor = true } },
                "la bibliothèque " + name, ct).ConfigureAwait(false);
        }
        // Devices on the local network still connect; access from the Internet stays off until chosen in Jellyfin.
        await Send(http, HttpMethod.Post, "Startup/RemoteAccess", new { EnableRemoteAccess = false }, "l’accès réseau", ct).ConfigureAwait(false);
        await Send(http, HttpMethod.Post, "Startup/Complete", null, "la fin de l’assistant", ct).ConfigureAwait(false);
    }
    private static async Task<bool> SignsInAsync(Uri server, string user, string password, HttpMessageHandler? handler, CancellationToken ct)
    {
        using var client = new JellyfinClient(new(server.AbsoluteUri, "", user, "", "mira-setup"), handler);
        try { await client.LoginAsync(user, password, ct).ConfigureAwait(false); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task<HashSet<string>> GetLibraryNamesAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync("Library/VirtualFolders", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Refused("les bibliothèques", response.StatusCode);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct).ConfigureAwait(false);
        return json.RootElement.ValueKind != JsonValueKind.Array ? new(StringComparer.OrdinalIgnoreCase)
            : json.RootElement.EnumerateArray().Select(x => x.TryGetProperty("Name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : "")
                .Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    private static async Task Send(HttpClient http, HttpMethod method, string path, object? body, string step, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw Refused(step, response.StatusCode);
    }
    private static InstallException Refused(string step, HttpStatusCode status) => new($"Jellyfin a refusé de configurer {step} (code {(int)status}).");
    private static HttpClient Client(Uri server, HttpMessageHandler? handler, TimeSpan timeout)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.BaseAddress = server; http.Timeout = timeout;
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Client=\"Mira\", Device=\"Windows\", DeviceId=\"mira-setup\", Version=\"{JellyfinClient.AppVersion}\"");
        return http;
    }
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")] private static partial Regex UserName();
}

/// <summary>Jellyfin's own log, read when it does not start: the cause is there, not in its installer's message.</summary>
public static partial class JellyfinLog
{
    /// <summary>
    /// The last fatal error Jellyfin logged at or after <paramref name="since"/> in <paramref name="folder"/> (its
    /// <c>log_*.log</c> files), with the message of the exception that follows it; null when there is none or the log
    /// cannot be read.
    /// </summary>
    public static string? LastFatal(string folder, DateTimeOffset since)
    {
        try
        {
            var file = new DirectoryInfo(folder).EnumerateFiles("log_*.log").MaxBy(x => x.LastWriteTimeUtc);
            if (file is null || file.LastWriteTimeUtc < since.UtcDateTime) return null;
            var lines = Tail(file.FullName, 256 << 10);
            string? found = null; var detail = false;
            foreach (var line in lines)
            {
                if (Entry().Match(line) is { Success: true } entry)
                {
                    detail = false;
                    if (entry.Groups["level"].Value != "FTL" || !DateTimeOffset.TryParse(entry.Groups["time"].Value, System.Globalization.CultureInfo.InvariantCulture, default, out var time) || time < since) continue;
                    found = entry.Groups["message"].Value.Trim(); detail = true;
                }
                else if (detail && Exception().Match(line) is { Success: true } exception)
                {
                    found += " (" + exception.Groups["message"].Value.Trim() + ")"; detail = false;
                }
            }
            return found is null ? null : found.Length <= 300 ? found : found[..299] + "…";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
    /// <summary>The last <paramref name="bytes"/> of a file Jellyfin may still be writing, as whole lines.</summary>
    private static string[] Tail(string path, int bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - bytes); stream.Position = start;
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r'));
        return (start > 0 ? lines.Skip(1) : lines).ToArray();
    }
    // [2026-10-01 13:33:26.446 +00:00] [FTL] [1] Main: Error while starting server
    [GeneratedRegex(@"^\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+ [+-]\d{2}:\d{2})\] \[(?<level>[A-Z]{3})\] \[\d+\] [^:]+: (?<message>.*)$")] private static partial Regex Entry();
    // System.IO.IOException: Failed to bind to address http://[::]:8096: address already in use.
    [GeneratedRegex(@"^\s*[\w.`]+Exception(?: \([^)]*\))?: (?<message>.+)$")] private static partial Regex Exception();
}
