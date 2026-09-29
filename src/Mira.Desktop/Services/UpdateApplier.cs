using System.Diagnostics;
using System.IO;

namespace Mira.Desktop.Services;

/// <summary>
/// Second half of an update of a portable or folder copy, run by the downloaded version:
/// <c>Mira.exe --apply-update portable|folder --target &lt;exe or folder&gt; --wait &lt;pid&gt; --from &lt;version&gt; --version &lt;version&gt; [--relaunch] [--data &lt;profile&gt;]</c>.
/// It holds the update lock of the copy (Mira waits for it at startup), waits for the running Mira to close, then puts the
/// new files in place, the data folder left untouched. A folder is first copied beside its files, then swapped by renames:
/// if one rename fails, the previous files come back.
/// </summary>
internal static class UpdateApplier
{
    public const string NewSuffix = ".mira-new", IncompleteMarker = "INCOMPLETE.txt";

    public static int Run(string[] args)
    {
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var kind = Arg("--apply-update"); var target = Arg("--target");
        if (kind is not ("portable" or "folder") || target is null || !Path.IsPathFullyQualified(target)) return 2;
        target = Path.GetFullPath(target);
        var folder = kind == "folder" ? target : Path.GetDirectoryName(target)!;
        var profile = Arg("--data") is { } data && Path.IsPathFullyQualified(data) ? Path.GetFullPath(data) : Path.Combine(folder, "data");
        var updates = Path.Combine(profile, "updates"); var log = Path.Combine(updates, "update.log");
        var from = Arg("--from") ?? "?";
        var exit = 0; var validated = false;
        using (var busy = new Mutex(false, UpdateLock.Name(profile)))
        {
            var owned = false;
            try
            {
                try { owned = busy.WaitOne(TimeSpan.FromMinutes(2)); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new InvalidOperationException("Une autre mise à jour de cette copie est en cours.");
                // Only a Mira installation may be replaced: never an arbitrary file or folder named on the command line.
                var existing = kind == "folder" ? Path.Combine(target, "Mira.exe") : target;
                if (!File.Exists(existing) || !IsMira(existing)) throw new InvalidOperationException("La cible n’est pas une copie de Mira.");
                validated = true;
                if (int.TryParse(Arg("--wait"), out var pid) && pid != Environment.ProcessId) WaitForExit(pid);
                var source = Environment.ProcessPath ?? throw new InvalidOperationException("Exécutable introuvable.");
                if (kind == "portable") ReplaceFile(source, target);
                else CopyFolder(AppContext.BaseDirectory, target, Path.Combine(updates, "rollback-" + DateTime.Now.ToString("yyyyMMddHHmmss")));
                Log(log, $"mise à jour {Arg("--version") ?? ""} installée depuis {from}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                exit = 1; Log(log, "installation impossible : " + ex.Message);
            }
            // Released before the relaunch: the new Mira waits for this lock.
            finally { if (owned) busy.ReleaseMutex(); }
        }
        if (validated && args.Contains("--relaunch"))
        {
            // Relaunched after a failure too: the previous version, restored, reopens and says so.
            var exe = kind == "folder" ? Path.Combine(target, "Mira.exe") : target;
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = folder };
            start.ArgumentList.Add("--updated-from"); start.ArgumentList.Add(from);
            if (Arg("--data") is { } custom)
            {
                start.ArgumentList.Add("--data"); start.ArgumentList.Add(custom);
                if (args.Contains("--offscreen")) start.ArgumentList.Add("--offscreen");
            }
            try { using var _ = Process.Start(start); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { Log(log, "redémarrage impossible : " + ex.Message); }
        }
        return exit;
    }

    /// <summary>Mira.exe and the installer both carry "Mira" in their version resource (Inno Setup pads it with spaces).</summary>
    public static bool IsMira(string path) => FileVersionInfo.GetVersionInfo(path).ProductName?.Trim() == "Mira";

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // A recycled process id belongs to some other program: nothing to wait for.
            if (!process.ProcessName.StartsWith("Mira", StringComparison.OrdinalIgnoreCase)) return;
            if (!process.WaitForExit(120_000)) throw new InvalidOperationException("Mira ne s’est pas fermé.");
        }
        // Gone already, or while being looked at.
        catch (ArgumentException) { }
        catch (InvalidOperationException) when (Process.GetProcesses().All(p => p.Id != pid)) { }
    }

    /// <summary>Copies <paramref name="source"/> over <paramref name="target"/> in one replacement, keeping the old file until
    /// the new one is in place. The new file is Mira's own verified download: it does not keep the old one's browser mark.</summary>
    public static void ReplaceFile(string source, string target)
    {
        var incoming = target + ".update"; var previous = target + ".previous";
        Retry(() => File.Copy(source, incoming, overwrite: true));
        try { Retry(() => File.Replace(incoming, target, previous, ignoreMetadataErrors: true)); }
        finally { TryDelete(incoming); }
        TryDelete(previous);
        TryDelete(target + ":Zone.Identifier");
    }

    /// <summary>
    /// Updates <paramref name="target"/> with every file of <paramref name="source"/> except its "data" folder. The new files
    /// are first copied beside their destinations (.mira-new), which leaves the installed copy untouched; then each old file
    /// moves to <paramref name="rollback"/> and the new one takes its name. On any failure the old files come back; one that
    /// cannot is listed in the rollback folder, which is then kept.
    /// </summary>
    public static void CopyFolder(string source, string target, string rollback, int attempts = 40)
    {
        void Retry(Action action) => UpdateApplier.Retry(action, attempts);
        source = Path.GetFullPath(source); target = Path.GetFullPath(target);
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .Where(relative => !IsData(relative) && !relative.EndsWith(NewSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var staged = new List<string>();
        try
        {
            foreach (var relative in files)
            {
                var incoming = Path.Combine(target, relative) + NewSuffix;
                Directory.CreateDirectory(Path.GetDirectoryName(incoming)!);
                Retry(() => File.Copy(Path.Combine(source, relative), incoming, overwrite: true)); staged.Add(incoming);
            }
        }
        catch { foreach (var path in staged) TryDelete(path); throw; }
        var replaced = new List<string>(); var added = new List<string>();
        try
        {
            foreach (var relative in files)
            {
                var destination = Path.Combine(target, relative);
                if (File.Exists(destination))
                {
                    var kept = Path.Combine(rollback, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    Retry(() => File.Move(destination, kept, overwrite: true)); replaced.Add(relative);
                }
                else added.Add(relative);
                Retry(() => File.Move(destination + NewSuffix, destination));
            }
        }
        catch
        {
            var lost = new List<string>();
            foreach (var relative in added) if (File.Exists(Path.Combine(target, relative))) try { Retry(() => File.Delete(Path.Combine(target, relative))); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var relative in replaced)
                try { Retry(() => File.Move(Path.Combine(rollback, relative), Path.Combine(target, relative), overwrite: true)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(relative); }
            foreach (var path in staged) TryDelete(path);
            if (lost.Count > 0) try { File.WriteAllLines(Path.Combine(rollback, IncompleteMarker), lost); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        try { if (Directory.Exists(rollback)) Directory.Delete(rollback, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static bool IsData(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("data", StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>Antivirus scans and the closing process can hold a file for a moment: up to ten seconds by default.</summary>
    private static void Retry(Action action, int attempts = 40)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts) { Thread.Sleep(250); }
        }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { } }
    private static void Log(string path, string line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
