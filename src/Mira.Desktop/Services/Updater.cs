using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Updates;

namespace Mira.Desktop.Services;

internal enum InstallKind { Installer, Portable, Folder, Development }
internal enum UpdateState { Disabled, Idle, Checking, Downloading, Ready, UpToDate, Failed }
/// <summary>How the last installation ended, read at startup: <paramref name="Installed"/> false when it failed.</summary>
internal sealed record UpdateResult(Version Version, bool Installed);

/// <summary>
/// Automatic updates of this copy of Mira. At startup, then every six hours, it asks GitHub for a newer signed release,
/// downloads the file matching the installation (installer, portable exe or zip folder) into the profile's "updates"
/// folder, checks it, and installs it when Mira closes, or at once with a restart. The data folder is never touched.
/// An installation that fails twice is no longer started automatically at close; "Redémarrer" still tries it.
/// </summary>
internal sealed class Updater : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6), RetryAfterFailure = TimeSpan.FromHours(1);
    public const int AutomaticAttempts = 2;
    private readonly UpdateClient _client;
    private readonly string _staging, _profile, _log, _pendingFile;
    private readonly bool _customProfile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeSpan _firstDelay;
    private Task? _check;
    private DateTime _verifiedAt = DateTime.MinValue;

    public InstallKind Kind { get; }
    public Version Current { get; }
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public UpdateOffer? Offer { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }
    /// <summary>Installer or new executable to start; for a folder, the new Mira.exe unpacked from the zip.</summary>
    public string? ReadyPath { get; private set; }
    public string AppPath { get; }
    /// <summary>The ready version already failed <see cref="AutomaticAttempts"/> times: it waits for "Redémarrer".</summary>
    public bool AutomaticInstallSuspended => Offer is { } offer && ReadPending(_pendingFile) is { } pending && pending.Version == offer.Version.ToString(3) && pending.Attempts >= AutomaticAttempts;
    public event Action? Changed;

    private Updater(Version current, InstallKind kind, string profile, bool customProfile, UpdateClient client, TimeSpan firstDelay)
    {
        Current = current; Kind = kind; _profile = profile; _customProfile = customProfile; _client = client; _firstDelay = firstDelay;
        _staging = Path.Combine(profile, "updates"); _log = Path.Combine(_staging, "update.log"); _pendingFile = Path.Combine(_staging, "pending.json");
        AppPath = kind == InstallKind.Portable ? Environment.ProcessPath! : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (kind == InstallKind.Development) State = UpdateState.Disabled;
    }

    /// <summary>
    /// How the installation started at the last close ended, reported once, with or without an updater for this run.
    /// Reached: nothing is pending any more. Not reached: the count of attempts stays, so that a version failing again
    /// stops being started at every close.
    /// </summary>
    public static UpdateResult? TakeResult(string profile)
    {
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? new Version(0, 0, 0);
        var staging = Path.Combine(profile, "updates"); var file = Path.Combine(staging, "pending.json"); var log = Path.Combine(staging, "update.log");
        if (ReadPending(file) is not { } pending || ReleaseFeed.ParseVersion(pending.Version) is not { } target) return null;
        if (current >= target) { TryDelete(() => File.Delete(file)); Log(log, $"version {target.ToString(3)} en place"); return new UpdateResult(target, true); }
        if (pending.Reported) return null;
        WritePending(file, pending with { Reported = true }); Log(log, $"la mise à jour vers {target.ToString(3)} ne s’est pas installée");
        return new UpdateResult(target, false);
    }

    /// <summary>The updater of this run; null for validation runs, unless they name a loopback test feed
    /// (--update-feed, with the --update-key that signed it) together with an isolated --data profile.</summary>
    public static Updater? Create(string[] args, string profile, bool validation)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var current = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? new Version(0, 0, 0);
        var kind = Detect(AppContext.BaseDirectory, !File.Exists(Path.Combine(AppContext.BaseDirectory, "Mira.dll")));
        var customProfile = Arg("--data") is not null;
        if (Arg("--update-feed") is { } feedText)
        {
            if (!customProfile || !Uri.TryCreate(feedText, UriKind.Absolute, out var feed) || !feed.IsLoopback) return null;
            // A validation run tests the real installation paths, even from a build folder.
            if (kind == InstallKind.Development) kind = InstallKind.Folder;
            IReadOnlyList<string> keys = Arg("--update-key") is { } key ? [key] : UpdateKeys.Trusted;
            return new Updater(current, kind, profile, customProfile, new UpdateClient(current, keys, feed), TimeSpan.FromSeconds(2));
        }
        if (validation) return null;
        return new Updater(current, kind, profile, customProfile, new UpdateClient(current, UpdateKeys.Trusted), TimeSpan.FromSeconds(20));
    }

    /// <summary>Installer: Inno Setup's uninstaller sits beside Mira.exe. Portable: one self-contained executable
    /// (no Mira.dll beside it). A dotnet build output is never updated in place.</summary>
    public static InstallKind Detect(string baseDirectory, bool singleFile)
    {
        if (singleFile) return InstallKind.Portable;
        if (Directory.Exists(baseDirectory) && Directory.EnumerateFiles(baseDirectory, "unins*.exe").Any() && Directory.EnumerateFiles(baseDirectory, "unins*.dat").Any()) return InstallKind.Installer;
        var parts = baseDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) && parts[i + 1] is "Debug" or "Release") return InstallKind.Development;
        return InstallKind.Folder;
    }
    public static string ManifestKind(InstallKind kind) => kind switch { InstallKind.Installer => "installer", InstallKind.Portable => "portable", _ => "zip" };

    /// <summary>Checks at startup and then regularly, while <paramref name="enabled"/> says so. Stops with <see cref="Dispose"/>;
    /// no failure ends the loop.</summary>
    public async Task RunAsync(Func<bool> enabled)
    {
        if (State == UpdateState.Disabled) return;
        var token = _lifetime.Token;
        try
        {
            await Task.Delay(_firstDelay, token);
            await Task.Run(() => CleanStaging(), token);
            while (!token.IsCancellationRequested)
            {
                try { if (enabled() && State != UpdateState.Ready) await CheckAsync(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log("vérification interrompue : " + ex.GetType().Name); }
                await Task.Delay(State == UpdateState.Failed ? RetryAfterFailure : Interval, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One check, then the download of a newer version; a second call while one runs joins it.</summary>
    public Task CheckAsync() => _check is { IsCompleted: false } running ? running : _check = CheckCoreAsync();
    private async Task CheckCoreAsync()
    {
        if (State == UpdateState.Disabled) return;
        var token = _lifetime.Token;
        Set(UpdateState.Checking);
        try
        {
            var offer = await _client.CheckAsync(ManifestKind(Kind), token);
            LastCheck = DateTimeOffset.Now;
            if (offer is null) { Offer = null; ReadyPath = null; Log($"à jour ({Current.ToString(3)})"); Set(UpdateState.UpToDate); return; }
            Offer = offer; Progress = 0; Set(UpdateState.Downloading);
            var directory = Path.Combine(_staging, offer.Version.ToString(3));
            // Only the offered version is kept: an older download waiting for its installation is replaced.
            await Task.Run(() => RemoveOtherVersions(offer.Version), token);
            // Reported for each 64 KB received: the page redraws per whole percent only.
            var progress = new Progress<double>(value => { var shown = Math.Floor(value * 100) != Math.Floor(Progress * 100); Progress = value; if (shown) Changed?.Invoke(); });
            var file = await _client.DownloadAsync(offer, directory, progress, token);
            ReadyPath = Kind == InstallKind.Folder ? await Task.Run(() => Unpack(file, offer, directory), token) : file;
            if (!IsVersion(ReadyPath, offer.Version)) throw new UpdateException("Le fichier téléchargé n’est pas la version annoncée.");
            Log($"prête : {offer.Version.ToString(3)} ({offer.File.Name})");
            Set(UpdateState.Ready);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Whatever went wrong, the page shows it and the next check comes: an update must never stop Mira.
            LastCheck = DateTimeOffset.Now; ReadyPath = null;
            Error = ex is UpdateException ? ex.Message : "La mise à jour n’a pas pu être préparée sur ce PC.";
            Log("échec : " + (ex is UpdateException ? ex.Message : ex.GetType().Name));
            Set(UpdateState.Failed);
        }
    }

    /// <summary>The zip holds a "Mira" folder: it is unpacked once beside the download, then reused while it still matches.</summary>
    private static string Unpack(string zip, UpdateOffer offer, string directory)
    {
        var root = Path.Combine(directory, "app"); var exe = Path.Combine(root, "Mira", "Mira.exe"); var marker = Path.Combine(root, "ready.txt");
        if (File.Exists(exe) && File.Exists(marker) && File.ReadAllText(marker).Trim() == offer.File.Sha256 && TreeMatches(zip, root)) return exe;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        // ExtractToDirectory refuses entries that would land outside the destination.
        ZipFile.ExtractToDirectory(zip, root);
        if (!File.Exists(exe) || Directory.Exists(Path.Combine(root, "Mira", "data"))) throw new UpdateException("L’archive de mise à jour n’a pas la forme attendue.");
        File.WriteAllText(marker, offer.File.Sha256);
        return exe;
    }
    /// <summary>Every file of the verified zip is present in the unpacked tree with its size (a file removed by an
    /// antivirus or a disk cleaner, or cut short, is noticed before the tree is copied over Mira).</summary>
    private static bool TreeMatches(string zip, string root)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != entry.Length) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>Mira.exe and the installer both carry "Mira" and the release version in their version resource
    /// (Inno Setup pads the product name with spaces).</summary>
    public static bool IsVersion(string path, Version version) =>
        FileVersionInfo.GetVersionInfo(path) is var info && info.ProductName?.Trim() == "Mira" && info.FileMajorPart == version.Major && info.FileMinorPart == version.Minor && info.FileBuildPart == version.Build;

    /// <summary>The prepared update is still there and unchanged: the exact download for the installer and the portable
    /// executable, the zip and every unpacked file for a folder. It sat in the profile since the download.</summary>
    private bool IsIntact()
    {
        if (ReadyPath is not { } path || Offer is not { } offer || !File.Exists(path)) return false;
        if (Kind != InstallKind.Folder) return UpdateClient.Matches(path, offer.File);
        var zip = Path.Combine(_staging, offer.Version.ToString(3), offer.File.Name);
        return UpdateClient.Matches(zip, offer.File) && TreeMatches(zip, Path.Combine(_staging, offer.Version.ToString(3), "app")) && IsVersion(path, offer.Version);
    }
    /// <summary>Off the interface thread: hashing the download takes a moment. False sends the update back to be prepared again.</summary>
    public async Task<bool> VerifyAsync()
    {
        if (State != UpdateState.Ready) return false;
        if (await Task.Run(IsIntact)) { _verifiedAt = DateTime.UtcNow; return true; }
        Discarded();
        return false;
    }
    private void Discarded() { Log("fichier modifié depuis le téléchargement : ignoré"); ReadyPath = null; Set(UpdateState.Idle); }

    /// <summary>
    /// Starts the prepared update: Inno Setup for an installed Mira, else the new executable, which waits for this process to
    /// end before replacing the old files. With <paramref name="relaunch"/>, Mira reopens once it is done, whatever the outcome.
    /// Without it (automatic, at close), a version that already failed twice is left for an explicit restart.
    /// </summary>
    public bool Apply(bool relaunch)
    {
        if (State != UpdateState.Ready || Offer is not { } offer || ReadyPath is not { } path) return false;
        var pending = ReadPending(_pendingFile) is { } previous && previous.Version == offer.Version.ToString(3) ? previous : new PendingUpdate(offer.Version.ToString(3), Current.ToString(3), 0, false);
        if (!relaunch && pending.Attempts >= AutomaticAttempts) { Log($"installation automatique de {pending.Version} suspendue après {pending.Attempts} échecs"); return false; }
        // Checked again unless "Redémarrer" has just done it: the window is already hidden, the moment costs nothing visible.
        if (DateTime.UtcNow - _verifiedAt > TimeSpan.FromMinutes(1) && !IsIntact()) { Discarded(); return false; }
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (Kind == InstallKind.Installer)
        {
            foreach (var argument in new[] { relaunch ? "/SILENT" : "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/SP-", "/LOG=" + Path.Combine(_staging, "setup.log") })
                start.ArgumentList.Add(argument);
            // The installer reopens Mira when it ends, installed or not (installer/Mira.iss, DeinitializeSetup).
            if (relaunch) { start.ArgumentList.Add("/RELAUNCH=1"); start.ArgumentList.Add("/FROM=" + Current.ToString(3)); start.ArgumentList.Add("/MIRA=" + Path.Combine(AppPath, "Mira.exe")); }
        }
        else
        {
            foreach (var argument in new[] { "--apply-update", Kind == InstallKind.Portable ? "portable" : "folder", "--target", AppPath, "--wait", Environment.ProcessId.ToString(), "--from", Current.ToString(3), "--version", offer.Version.ToString(3) })
                start.ArgumentList.Add(argument);
            if (relaunch) start.ArgumentList.Add("--relaunch");
            if (_customProfile)
            {
                start.ArgumentList.Add("--data"); start.ArgumentList.Add(_profile);
                if (Environment.GetCommandLineArgs().Contains("--offscreen")) start.ArgumentList.Add("--offscreen");
            }
        }
        // Counted before the start: the next Mira, whatever its version, reads how it went.
        WritePending(_pendingFile, pending with { Attempts = pending.Attempts + 1, Reported = false });
        try { using var process = Process.Start(start); Log($"installation de {offer.Version.ToString(3)} lancée" + (relaunch ? " avec redémarrage" : " à la fermeture")); return process is not null; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Log("lancement impossible : " + ex.GetType().Name); return false; }
    }

    /// <summary>Removes downloads of versions already installed and unfinished downloads of other versions. A rollback copy
    /// is removed only when the folder update it belongs to came back in full (no INCOMPLETE.txt). New files left beside
    /// the application by an interrupted folder update (never loaded: they end in .mira-new) are removed too.</summary>
    public void CleanStaging()
    {
        if (Kind == InstallKind.Folder && Directory.Exists(AppPath))
            foreach (var stray in Directory.EnumerateFiles(AppPath, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories))
                if (stray.EndsWith(UpdateApplier.NewSuffix, StringComparison.OrdinalIgnoreCase) && !stray.StartsWith(Path.Combine(AppPath, "data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    TryDelete(() => File.Delete(stray));
        if (!Directory.Exists(_staging)) return;
        foreach (var directory in Directory.EnumerateDirectories(_staging))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("rollback-", StringComparison.Ordinal))
            {
                if (File.Exists(Path.Combine(directory, UpdateApplier.IncompleteMarker))) Log($"copie de retour arrière gardée : {directory}");
                else TryDelete(() => Directory.Delete(directory, recursive: true));
            }
            else if (ReleaseFeed.ParseVersion(name) is { } version && version <= Current) TryDelete(() => Directory.Delete(directory, recursive: true));
        }
    }
    private void RemoveOtherVersions(Version keep)
    {
        if (!Directory.Exists(_staging)) return;
        foreach (var directory in Directory.EnumerateDirectories(_staging))
            if (ReleaseFeed.ParseVersion(Path.GetFileName(directory)) is { } version && version != keep) TryDelete(() => Directory.Delete(directory, recursive: true));
    }

    /// <summary>data/updates/pending.json: the version whose installation was started, from which version, how many times,
    /// and whether its failure was already reported.</summary>
    private sealed record PendingUpdate(string Version, string From, int Attempts, bool Reported);
    private static PendingUpdate? ReadPending(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static void WritePending(string file, PendingUpdate pending)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(pending)); File.Move(file + ".tmp", file, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void TryDelete(Action delete) { try { delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void Set(UpdateState state) { State = state; if (state is not UpdateState.Failed) Error = null; Changed?.Invoke(); }
    private void Log(string line) => Log(_log, line);
    private static void Log(string log, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024) File.Delete(log);
            File.AppendAllText(log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() { _lifetime.Cancel(); _client.Dispose(); }
}
