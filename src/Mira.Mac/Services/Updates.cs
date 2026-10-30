using System.Diagnostics;
using Mira.Core;
using Mira.Core.Updates;
using Mira.Mac.Views;
using static Mira.Core.Lang;

namespace Mira.Mac.Services;

/// <summary>
/// Automatic updates of Mira for Mac. At launch, then every six hours (unless « Rechercher les nouvelles versions » is
/// off), Mira asks GitHub for a newer release whose manifest carries the maintainer's signature, as on Windows, downloads
/// its disk image into the profile's "updates" folder and keeps it only if its size and SHA-256 match that manifest. The
/// new Mira.app is copied out of the image beside the running one (<see cref="MacInstall.Staging"/>), checked (bundle
/// identifier, version, its ad hoc signature intact), and swapped in by a small script once Mira has quit: at the next
/// quit, or at once with « Redémarrer », which opens it again. Downloaded by Mira rather than a browser, the image is not
/// quarantined, so Gatekeeper does not ask again. Where Mira cannot replace itself (opened from the disk image, from a
/// translocated copy, or in a folder this account cannot change), or after a swap that failed, the verified image is
/// offered instead, to install by hand. Nothing else than GitHub is contacted, as before.
/// </summary>
public static class Updates
{
    public const string Releases = "https://github.com/sasou-web/Mira/releases/latest";
    public static readonly Uri Project = new("https://github.com/sasou-web/Mira");
    private const string FailedName = "mac-failed";
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    public static Version Current => typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    private static bool _busy, _relaunch;
    /// <summary>The version whose Mira.app waits beside this one, swapped in when Mira quits.</summary>
    private static Version? _ready;
    private static string? _image;

    private static string Folder(MainWindow window) => Path.Combine(window.Profile.DirectoryPath, "updates");

    /// <summary>At launch: what the last quit installed, then the first check and the next ones every six hours.</summary>
    public static async Task StartAsync(MainWindow window)
    {
        Report(window);
        while (!window.IsClosing)
        {
            await CheckAsync(window);
            await Task.Delay(Every);
        }
    }

    /// <summary>How the swap started at the last quit ended, said once; the leftovers of a swap are cleared.</summary>
    private static void Report(MainWindow window)
    {
        var folder = Folder(window);
        var result = MacInstall.ReadResult(folder);
        if (SystemLanguage.Bundle() is { } bundle)
            foreach (var leftover in new[] { MacInstall.Staging(bundle), MacInstall.Previous(bundle) }) Delete(leftover);
        if (result is null) return;
        if (result.Installed && result.Version == Current)
        {
            Delete(Path.Combine(folder, FailedName));
            foreach (var image in Images(folder)) Delete(image);
            window.Notice(L($"Mira est passée à la version {Current}.", $"Mira is now version {Current}."), tone: NoticeTone.Done);
        }
        else if (!result.Installed)
        {
            // Not tried again at each quit: this version now waits to be installed by hand.
            try { File.WriteAllText(Path.Combine(folder, FailedName), result.Version.ToString(3)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var image = Images(folder).FirstOrDefault(i => Path.GetFileName(i) == UpdateManifest.OtherName("mac", result.Version));
            var text = L($"Mira {result.Version.ToString(3)} n’a pas pu remplacer cette version.", $"Mira {result.Version.ToString(3)} couldn’t replace this version.");
            if (image is not null) window.Notice(text + L(" Ouvre l’image disque et glisse Mira dans Applications.", " Open the disk image and drag Mira to Applications."), L("Ouvrir l’image disque", "Open the disk image"), () => OpenFile(image), sticky: true, tone: NoticeTone.Error);
            else window.Notice(text, L("Télécharger", "Download"), () => Open(new Uri(Releases)), sticky: true, tone: NoticeTone.Error);
        }
    }

    /// <param name="asked">From « Rechercher une mise à jour »: GitHub is asked even with the automatic search off.</param>
    public static async Task CheckAsync(MainWindow window, bool asked = false)
    {
        // Off: nothing leaves for GitHub unless asked.
        if (!asked && !window.Settings.AutoUpdate) return;
        if (_ready is { } waiting) { if (asked) Ready(window, waiting); return; }
        if (_busy) { if (asked) window.Notice(L("Mira cherche déjà une mise à jour.", "Mira is already looking for an update.")); return; }
        _busy = true;
        try
        {
            using var client = new UpdateClient(Current, UpdateKeys.Trusted);
            if (await client.CheckAsync("mac") is not { } offer)
            {
                if (asked) window.Notice(L($"Mira {Current} est à jour.", $"Mira {Current} is up to date."));
                return;
            }
            var version = offer.Version.ToString(3);
            // A build run outside Mira.app (dotnet run): there is nothing to replace, the release page says what is new.
            if (SystemLanguage.Bundle() is not { } bundle)
            {
                window.Notice(L($"Mira {version} est disponible.", $"Mira {version} is available."), L("Télécharger", "Download"), () => Open(ReleaseFeed.ReleasePage(offer.Page) ?? new Uri(Releases)));
                return;
            }
            if (asked) window.Notice(L($"Téléchargement de Mira {version}…", $"Downloading Mira {version}…"), sticky: true);
            var folder = Folder(window);
            var image = await client.DownloadAsync(offer, folder);
            _image = image;
            // An older image, from a version skipped since.
            foreach (var other in Images(folder).Where(i => i != image)) Delete(other);
            var problem = MacInstall.Problem(bundle);
            if (problem != MacInstallProblem.None || Failed(folder) == version)
            {
                var why = problem != MacInstallProblem.None ? MacInstall.Text(problem) : L("Elle n’a pas pu s’installer seule la dernière fois : ouvre l’image disque et glisse Mira dans Applications.", "It couldn’t install by itself last time: open the disk image and drag Mira to Applications.");
                window.Notice(L($"Mira {version} est téléchargée et vérifiée. ", $"Mira {version} is downloaded and checked. ") + why, L("Ouvrir l’image disque", "Open the disk image"), () => OpenFile(image), sticky: true);
                return;
            }
            await Task.Run(() => Stage(folder, bundle, image, offer.Version));
            _ready = offer.Version;
            Ready(window, offer.Version);
        }
        catch (UpdateException ex)
        {
            // A network or a GitHub limit is said only when asked; a release that fails its checks is always said.
            if (asked || ex is { Network: false, Status: null })
            {
                if (_image is { } image && File.Exists(image)) window.Notice(ex.Message, L("Ouvrir l’image disque", "Open the disk image"), () => OpenFile(image), sticky: true, tone: NoticeTone.Error);
                else window.Notice(ex.Message, tone: NoticeTone.Error);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Append(window.Profile.DirectoryPath, ex);
            if (asked) window.Notice(L("Impossible de vérifier les mises à jour pour le moment.", "Can’t check for updates right now."), tone: NoticeTone.Error);
        }
        finally { _busy = false; }
    }

    private static void Ready(MainWindow window, Version version) =>
        window.Notice(L($"Mira {version.ToString(3)} est prête : elle s’installera quand tu quitteras Mira.", $"Mira {version.ToString(3)} is ready: it’ll install when you quit Mira."),
            L("Redémarrer", "Restart"), () => { _relaunch = true; window.Close(); }, sticky: true, tone: NoticeTone.Done);

    /// <summary>
    /// Copies Mira.app out of the verified disk image beside the running bundle and checks the copy: the same bundle
    /// identifier, the version the signed manifest announced, and its signature still sealing every file. Any failure
    /// deletes the copy and throws an <see cref="UpdateException"/>.
    /// </summary>
    private static void Stage(string folder, string bundle, string image, Version version)
    {
        var staging = MacInstall.Staging(bundle);
        var mount = Path.Combine(folder, "mount");
        Detach(mount);
        Delete(staging);
        Directory.CreateDirectory(mount);
        try
        {
            if (Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-noautoopen", "-readonly", "-mountpoint", mount, image).Code != 0)
                throw Refused(L("macOS n’a pas pu ouvrir l’image disque.", "macOS couldn’t open the disk image."));
            if (Directory.GetDirectories(mount, "*.app") is not [var app])
                throw Refused(L("L’image disque ne contient pas Mira.", "The disk image doesn’t contain Mira."));
            if (Run("/usr/bin/ditto", app, staging).Code != 0)
                throw Refused(L("Mira n’a pas pu copier la nouvelle version à côté d’elle.", "Mira couldn’t copy the new version beside itself."));
        }
        catch { Delete(staging); throw; }
        finally { Detach(mount); }
        try
        {
            if (Plist(staging, "CFBundleIdentifier") is not { Length: > 0 } identifier || identifier != Plist(bundle, "CFBundleIdentifier"))
                throw Refused(L("La nouvelle version n’est pas Mira.", "The new version isn’t Mira."));
            if (Plist(staging, "CFBundleShortVersionString") != version.ToString(3))
                throw Refused(L("La nouvelle version n’annonce pas le bon numéro.", "The new version doesn’t announce the right number."));
            if (Run("/usr/bin/codesign", "--verify", "--deep", "--strict", staging).Code != 0)
                throw Refused(L("La signature de la nouvelle version ne couvre plus tous ses fichiers.", "The new version’s signature no longer covers all its files."));
            // Mira checked the image against the signed manifest itself; a quarantine flag left on it would only make
            // macOS ask again at the next launch.
            Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", staging);
        }
        catch { Delete(staging); throw; }
    }
    private static UpdateException Refused(string why) => new(L("La mise à jour n’a pas pu se préparer : ", "The update couldn’t be prepared: ") + why);

    /// <summary>« Relancer » elsewhere (a new language): with a new version waiting, the update script opens Mira once it
    /// is in place, rather than a second script opening the bundle while it is being swapped. False when none waits.</summary>
    public static bool RelaunchAfterInstall()
    {
        if (_ready is null) return false;
        _relaunch = true;
        return true;
    }

    /// <summary>At the end of the quit: the script that swaps the bundles once this process has ended, then reopens
    /// Mira after « Redémarrer ». Nothing when no new version waits.</summary>
    public static void InstallAtQuit(MainWindow window)
    {
        if (_ready is not { } version || SystemLanguage.Bundle() is not { } bundle || !Directory.Exists(MacInstall.Staging(bundle))) return;
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in MacInstall.Arguments(Environment.ProcessId, bundle, Path.Combine(Folder(window), MacInstall.ResultName), version, _relaunch ? "/usr/bin/open" : null))
            start.ArgumentList.Add(argument);
        using var shell = Process.Start(start);
    }

    /// <summary>
    /// The check of mac.yml, run from a copy of Mira.app: the disk image just built staged beside it as an update to this
    /// same version (mounted, copied, checked), then the swap script started for this process, which ends at once. The
    /// workflow then reads the result file in <paramref name="folder"/>.
    /// </summary>
    public static int Check(string image, string folder)
    {
        if (SystemLanguage.Bundle() is not { } bundle) { Console.Error.WriteLine("Not run from Mira.app"); return 2; }
        if (MacInstall.Problem(bundle) is not MacInstallProblem.None and var problem) { Console.Error.WriteLine($"Can't replace {bundle}: {problem}"); return 1; }
        try { Stage(folder, bundle, image, Current); }
        catch (UpdateException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        Console.WriteLine($"Staged {MacInstall.Staging(bundle)}");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in MacInstall.Arguments(Environment.ProcessId, bundle, Path.Combine(folder, MacInstall.ResultName), Current, null)) start.ArgumentList.Add(argument);
        using var shell = Process.Start(start);
        return shell is null ? 1 : 0;
    }

    private static string? Failed(string folder)
    {
        try { return File.ReadAllText(Path.Combine(folder, FailedName)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static IEnumerable<string> Images(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "Mira-*-mac-arm64.dmg").ToList() : [];
    private static string? Plist(string bundle, string key)
    {
        var (code, output) = Run("/usr/bin/plutil", "-extract", key, "raw", "-o", "-", Path.Combine(bundle, "Contents", "Info.plist"));
        return code == 0 ? output.Trim() : null;
    }
    private static void Detach(string mount)
    {
        if (!Directory.Exists(mount)) return;
        // A volume left mounted by a Mira that stopped halfway; an empty folder otherwise.
        if (Directory.EnumerateFileSystemEntries(mount).Any()) Run("/usr/bin/hdiutil", "detach", mount, "-force");
        try { Directory.Delete(mount); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>A system tool by its full path, its arguments passed one by one; two minutes at most.</summary>
    private static (int Code, string Output) Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return (-1, "");
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { try { process.Kill(); } catch (InvalidOperationException) { } return (-1, ""); }
            return (process.ExitCode, output.Result);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }

    /// <summary>Opens a verified disk image in the Finder.</summary>
    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Opens a web page in the default browser: an HTTPS address only, or HTTP with <paramref name="plainHttp"/> (a
    /// Jellyfin at home or on Tailscale), given to macOS' open as a single argument.
    /// </summary>
    public static void Open(Uri page, bool plainHttp = false)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps && !(plainHttp && page.Scheme == Uri.UriSchemeHttp)) return;
        try
        {
            // By its full path, so that no other « open » found on the PATH runs; Linux is where Mira's checks run.
            var start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(page.AbsoluteUri);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
