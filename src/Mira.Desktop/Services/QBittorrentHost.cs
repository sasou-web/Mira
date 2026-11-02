using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Mira.Core;
using Mira.Core.TorLink;

namespace Mira.Desktop.Services;

/// <summary>
/// The qBittorrent Mira drives on the PC of Jellyfin, for private trackers: the one installed on this PC (its official
/// installer, Mira's or the person's), started under a profile of its own in Mira's data (its settings, list and window
/// apart from a qBittorrent the person uses), with its Web UI on 127.0.0.1 behind a password Mira draws once and keeps
/// with DPAPI. It is never tied to Mira's lifetime: it goes on sharing, in the notification area, once Mira closes.
/// </summary>
internal sealed class QBittorrentHost(string profile) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QBittorrentClient? _client;
    private Secret? _secret;

    /// <summary>Its profile: qBittorrent keeps it in &lt;here&gt;/qBittorrent/config and data.</summary>
    public string ProfileDirectory => Path.Combine(profile, "qbittorrent");
    private string SecretFile => Path.Combine(profile, "qbittorrent.protected");
    private string SettingsFile => Path.Combine(ProfileDirectory, "qBittorrent", "config", QBittorrentSettings.FileName);

    /// <summary>The Web API, once qBittorrent answers under Mira's account.</summary>
    public QBittorrentClient? Client => _client;
    /// <summary>Why the last start failed, in the person's words; null after one that worked.</summary>
    public string? Problem { get; private set; }

    /// <summary>qbittorrent.exe as its installer left it (HKLM\Software\qBittorrent, InstallLocation), or in Program Files; null when it isn't installed.</summary>
    public static string? Program()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"Software\qBittorrent");
                if (key?.GetValue("InstallLocation") is string folder && folder.Length > 0 && Path.Combine(folder, "qbittorrent.exe") is var exe && File.Exists(exe)) return exe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qBittorrent", "qbittorrent.exe");
        return File.Exists(standard) ? standard : null;
    }

    /// <summary>Its version as the file says it (« 5.3.0 »), for the page.</summary>
    public static string? Version(string program)
    {
        try { return FileVersionInfo.GetVersionInfo(program).ProductVersion?.Trim(); }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return null; }
    }

    /// <summary>
    /// qBittorrent answering under Mira's account: the one of this profile already running, or started now, its
    /// downloads going into <paramref name="savePath"/>. Null once it answers, otherwise why not (<see cref="Problem"/>).
    /// </summary>
    public async Task<string?> StartAsync(string savePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { Problem = await StartCoreAsync(savePath, ct); return Problem; }
        finally { _gate.Release(); }
    }

    private async Task<string?> StartCoreAsync(string savePath, CancellationToken ct)
    {
        if (Program() is not { } program) return L("qBittorrent n’est pas installé sur ce PC.", "qBittorrent isn’t installed on this PC.");
        var secret = _secret ??= LoadSecret();
        var client = _client ?? new QBittorrentClient(secret.Port, secret.Password);
        try
        {
            // Already running for this profile (Mira was closed meanwhile): it is asked first, its settings left as they are.
            if (await client.VersionAsync(ct) is null)
            {
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
                    var existing = File.Exists(SettingsFile) ? await File.ReadAllTextAsync(SettingsFile, ct) : null;
                    var ini = QBittorrentSettings.Ini(existing, secret.Port, QBittorrentSettings.PasswordHash(secret.Password));
                    await File.WriteAllTextAsync(SettingsFile, ini, ct);
                    var start = new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(program)! };
                    start.ArgumentList.Add("--profile=" + ProfileDirectory);
                    start.ArgumentList.Add("--no-splash");
                    try { using var process = Process.Start(start); }
                    catch (Win32Exception) { return L("qBittorrent n’a pas pu démarrer.", "qBittorrent couldn’t start."); }
                    var until = DateTime.UtcNow + TimeSpan.FromSeconds(45);
                    while (await client.VersionAsync(ct) is null)
                    {
                        if (DateTime.UtcNow > until) return L("qBittorrent a démarré mais ne répond pas à Mira : un autre programme utilise peut-être son port.", "qBittorrent started but isn’t answering Mira: another program may be using its port.");
                        await Task.Delay(500, ct);
                    }
                }
            }
            Directory.CreateDirectory(savePath);
            await client.SetPreferencesAsync(QBittorrentSettings.Preferences(savePath, secret.PeerPort), ct);
            _client = client;
            return null;
        }
        catch (QBittorrentException ex) { if (!ReferenceEquals(client, _client)) client.Dispose(); return ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (!ReferenceEquals(client, _client)) client.Dispose();
            return L("qBittorrent ne répond pas à Mira.", "qBittorrent isn’t answering Mira.");
        }
    }

    /// <summary>
    /// The official installer downloaded (<see cref="QBittorrentPackage.Current"/>), checked, held open, then run silently:
    /// Windows asks for the administrator's consent. Null once qBittorrent is installed, otherwise why not.
    /// </summary>
    public async Task<string?> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var package = QBittorrentPackage.Current;
        var folder = Path.Combine(profile, "downloads"); Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, package.FileName); var partial = setup + ".partial";
        try
        {
            await VerifiedDownload.DownloadAsync(package.Url, partial, package.Sha256, null, QBittorrentPackage.MaximumSize, "qBittorrent", progress: progress, ct: ct);
            File.Move(partial, setup, overwrite: true);
            Process? process;
            // Held open, sharing reads only, from its check to its start: nothing of this account can swap the file Windows runs as administrator.
            await using (var held = new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                if (!VerifiedDownload.Matches(await SHA256.HashDataAsync(held, ct), package.Sha256))
                    return L("Le fichier téléchargé ne correspond pas à la version vérifiée par Mira : il n’est pas installé.", "The downloaded file doesn’t match the version Mira checked: it isn’t installed.");
                try { process = Process.Start(new ProcessStartInfo(setup, "/S") { UseShellExecute = true, Verb = "runas" }); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return L("Installation annulée : Windows n’a pas reçu l’autorisation d’installer qBittorrent.", "Installation canceled: Windows didn’t get permission to install qBittorrent."); }
                catch (Win32Exception) { return L("L’installateur de qBittorrent n’a pas pu démarrer.", "qBittorrent’s installer couldn’t start."); }
            }
            if (process is null) return L("L’installateur de qBittorrent n’a pas pu démarrer.", "qBittorrent’s installer couldn’t start.");
            using (process)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(10));
                try { await process.WaitForExitAsync(limit.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return L("L’installation de qBittorrent n’a pas fini à temps.", "qBittorrent’s installation didn’t finish in time."); }
            }
            return Program() is null ? L("L’installateur de qBittorrent s’est arrêté sans l’installer.", "qBittorrent’s installer stopped without installing it.") : null;
        }
        catch (InstallException ex) { return ex.Message; }
        finally { TryDelete(partial); TryDelete(setup); }
    }

    /// <summary>The Web UI's password and ports, drawn once: kept with DPAPI for this Windows account.</summary>
    private Secret LoadSecret()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Secret>(ProtectedData.Unprotect(File.ReadAllBytes(SecretFile), null, DataProtectionScope.CurrentUser));
            if (saved is { Password.Length: >= 16, Port: > 1024 and < 65536, PeerPort: > 1024 and < 65536 }) return saved;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or CryptographicException or JsonException) { }
        var made = new Secret(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), FreePort(), RandomNumberGenerator.GetInt32(20_000, 60_000));
        var temporary = SecretFile + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(made), null, DataProtectionScope.CurrentUser));
        File.Move(temporary, SecretFile, overwrite: true);
        return made;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>It stopped answering (quit, or restarted): asked again, and started if need be, at the next use.</summary>
    public void Lost() => _client = null;

    public void Dispose() { _client?.Dispose(); _gate.Dispose(); }

    private sealed record Secret(string Password, int Port, int PeerPort);
}
