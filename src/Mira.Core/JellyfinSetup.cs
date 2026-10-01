using System.Net;
using System.Net.Http.Json;
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

/// <summary>Brings a newly installed Jellyfin to a ready library: its first-run wizard, done through the same routes as its own page.</summary>
public static partial class JellyfinSetup
{
    public static readonly Uri LocalServer = new("http://127.0.0.1:8096/");
    /// <summary>Jellyfin's own rule for user names (letters, digits, spaces, - _ ' . @ +; no space at either end).</summary>
    public static bool IsValidUserName(string name) => !string.IsNullOrWhiteSpace(name) && UserName().IsMatch(name) && name is not ("." or "..");
    /// <summary>The server's state; null while nothing answers on that address, or while Jellyfin is still loading.</summary>
    public static async Task<ServerState?> StateAsync(Uri server, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = Client(server, handler, TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync("System/Info/Public", ct).ConfigureAwait(false);
            // While it starts, Jellyfin answers 503 "Jellyfin Server is loading" in plain text.
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), default, ct).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String) return null;
            return new ServerState(root.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString()! : "",
                root.TryGetProperty("StartupWizardCompleted", out var done) && done.ValueKind == JsonValueKind.True);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return null; }
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
