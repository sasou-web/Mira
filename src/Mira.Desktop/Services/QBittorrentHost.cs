using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
/// with DPAPI. It is never tied to Mira's lifetime: it goes on, in the notification area, once Mira closes (sharing a private download until its 72 hours).
/// </summary>
internal sealed class QBittorrentHost(string profile) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private QBittorrentClient? _client;
    private Secret? _secret;
    private DateTime _failedAt = DateTime.MinValue, _restartedAt = DateTime.MinValue;

    /// <summary>
    /// After a start that failed, how long the downloads list waits before trying again (it asks every few seconds): a
    /// qBittorrent that can't start is not started over and over. What the person does (a link, a .torrent) tries at once.
    /// </summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    /// <summary>Mira's own qBittorrent stopped and started again for a refused sign-in at most this often: never in a loop.</summary>
    private static readonly TimeSpan RestartEvery = TimeSpan.FromMinutes(5);

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
    /// <paramref name="now"/> false (the list read every few seconds): within <see cref="Retry"/> of a start that failed,
    /// that failure again, without trying.
    /// </summary>
    public async Task<string?> StartAsync(string savePath, CancellationToken ct, bool now = true)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return null;
            if (!now && Problem is not null && DateTime.UtcNow - _failedAt < Retry) return Problem;
            Problem = await StartCoreAsync(savePath, ct);
            if (Problem is not null) _failedAt = DateTime.UtcNow;
            return Problem;
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> StartCoreAsync(string savePath, CancellationToken ct, bool restarted = false)
    {
        if (Program() is not { } program) return L("qBittorrent n’est pas installé sur ce PC.", "qBittorrent isn’t installed on this PC.");
        var secret = _secret ??= LoadSecret();
        // Its ports taken by another program meanwhile (a game, a VPN, another qBittorrent): new ones drawn, before
        // asking anything there (another program answering on the Web UI's port would look like a refused password).
        if (Moved(secret) is { } moved)
        {
            secret = moved;
            if (_client is not null) { _client.Dispose(); _client = null; }
        }
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
                        if (DateTime.UtcNow > until)
                        {
                            // Its port taken while it started: drawn again, and started once more.
                            if (!restarted && Moved(secret) is not null) { client.Dispose(); StopOwn(); return await StartCoreAsync(savePath, ct, restarted: true); }
                            return L("qBittorrent a démarré mais ne répond pas à Mira. Redémarre le PC si ça dure.", "qBittorrent started but isn’t answering Mira. Restart the PC if this lasts.");
                        }
                        await Task.Delay(500, ct);
                    }
                }
            }
            // A download folder on a drive unplugged (a USB disk) doesn't keep qBittorrent from answering: its list is read,
            // its downloads say their drive is away, and a new one is refused with that reason (MainWindow.QBittorrentAsync).
            var preferences = QBittorrentSettings.Preferences(savePath, secret.PeerPort);
            if (!await Task.Run(() => QBittorrentDownloads.Reachable(savePath, make: true), ct)) preferences.Remove("save_path");
            await client.SetPreferencesAsync(preferences, ct);
            _client = client;
            return null;
        }
        catch (QBittorrentException ex) when (ex.SignIn && !restarted && DateTime.UtcNow - _restartedAt > RestartEvery && StopOwn() > 0)
        {
            // Mira's own qBittorrent refuses its password (one started with an older one, or banning 127.0.0.1 after
            // too many tries): stopped, then started again from the settings written now, once, and not again for a while.
            _restartedAt = DateTime.UtcNow;
            if (!ReferenceEquals(client, _client)) client.Dispose();
            _client = null;
            await Task.Delay(1500, ct);
            return await StartCoreAsync(savePath, ct, restarted: true);
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
        var made = new Secret(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), FreePort(), PeerPort());
        SaveSecret(made);
        return made;
    }

    private void SaveSecret(Secret secret)
    {
        var temporary = SecretFile + ".tmp";
        File.WriteAllBytes(temporary, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(secret), null, DataProtectionScope.CurrentUser));
        File.Move(temporary, SecretFile, overwrite: true);
    }

    /// <summary>
    /// The Web UI's port or the peers' port listened on by another program than Mira's own qBittorrent: new ones drawn and
    /// kept, Mira's qBittorrent stopped when it was running (its Web UI, unable to listen, never answers; it starts again
    /// on the new port). Null when both are free or its own, or when Windows can't tell.
    /// </summary>
    private Secret? Moved(Secret secret)
    {
        var (web, peers) = (ListenerOwner(secret.Port), ListenerOwner(secret.PeerPort));
        if (web is null && peers is null) return null;
        if (OwnIds() is not { } own) return null;
        var (webTaken, peersTaken) = (web is { } w && !own.Contains(w), peers is { } p && !own.Contains(p));
        if (!webTaken && !peersTaken) return null;
        var moved = secret with { Port = webTaken ? FreePort() : secret.Port, PeerPort = peersTaken ? PeerPort() : secret.PeerPort };
        try { SaveSecret(moved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
        _secret = moved;
        if (webTaken) StopOwn();
        return moved;
    }

    /// <summary>A port for peers, among those nobody listens on.</summary>
    private static int PeerPort()
    {
        for (var attempt = 0; ; attempt++)
            if (RandomNumberGenerator.GetInt32(20_000, 60_000) is var port && (attempt >= 20 || ListenerOwner(port) is null)) return port;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort, int family, int tableClass, uint reserved);

    /// <summary>
    /// The process listening on this TCP port (IPv4, any address), read from Windows' table of listeners
    /// (TCP_TABLE_OWNER_PID_LISTENER); null when nobody listens there, or the table can't be read.
    /// </summary>
    internal static int? ListenerOwner(int port)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var table = size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
            try
            {
                // 2 = AF_INET, 3 = TCP_TABLE_OWNER_PID_LISTENER; 122 = ERROR_INSUFFICIENT_BUFFER (size now says how much).
                var result = GetExtendedTcpTable(table, ref size, false, 2, 3, 0);
                if (result == 122) continue;
                if (result != 0 || table == IntPtr.Zero) return null;
                var count = Marshal.ReadInt32(table);
                // MIB_TCPROW_OWNER_PID: state, local address, local port (network order, low 16 bits), remote address and port, process: 6 × 4 bytes.
                for (var i = 0; i < count && 4 + (i + 1) * 24 <= size; i++)
                {
                    var local = Marshal.ReadInt32(table, 4 + i * 24 + 8);
                    if ((((local & 0xFF) << 8) | ((local >> 8) & 0xFF)) == port) return Marshal.ReadInt32(table, 4 + i * 24 + 20);
                }
                return null;
            }
            finally { if (table != IntPtr.Zero) Marshal.FreeHGlobal(table); }
        }
        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Ends the qBittorrent started under this profile (its command line names it), never another one the person uses:
    /// read through WMI's scripting objects, which need no administrator rights for this account's processes. How many.
    /// </summary>
    private int StopOwn()
    {
        var stopped = 0;
        foreach (var id in OwnIds() ?? [])
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill();
                process.WaitForExit(10_000);
                stopped++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        return stopped;
    }

    /// <summary>The processes of the qBittorrent started under this profile (none: empty); null when WMI can't say.</summary>
    private List<int>? OwnIds()
    {
        try
        {
            var ids = new List<int>();
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!)!;
            dynamic service = locator.ConnectServer(".", @"root\cimv2");
            foreach (dynamic found in service.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'qbittorrent.exe'"))
            {
                var line = (object?)found.Properties_.Item("CommandLine").Value as string;
                if (line is null || !line.Contains("--profile=" + ProfileDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                try { ids.Add(Convert.ToInt32((object)found.Properties_.Item("ProcessId").Value)); }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { }
            }
            return ids;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException) { return null; }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    /// <summary>It stopped answering (quit, or restarted): asked again, and started if need be, at the next use.</summary>
    public void Lost() => _client = null;

    public void Dispose() { _client?.Dispose(); _gate.Dispose(); }

    private sealed record Secret(string Password, int Port, int PeerPort);
}
