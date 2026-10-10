using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// Second half of an update of a portable or folder copy, run by the downloaded version:
/// <c>Mira.exe --apply-update portable|folder --target &lt;exe or folder&gt; --wait &lt;pid&gt; --from &lt;version&gt; --version &lt;version&gt; [--relaunch] [--data &lt;profile&gt;]</c>.
/// It holds the update lock of the copy (Mira waits for it at startup), waits for the running Mira to close, then puts the
/// new files in place, the data folder left untouched. A folder is first copied beside its files, then swapped by renames:
/// if one rename fails, the previous files come back, and a swap cut short (power loss) is undone at the next start.
/// </summary>
internal static class UpdateApplier
{
    public const string NewSuffix = ".mira-new", OldSuffix = ".mira-old", IncompleteMarker = "INCOMPLETE.txt", InProgressMarker = "IN-PROGRESS.json";

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
                else
                {
                    // A swap of this folder cut short earlier is undone first: its rollback copy is the only one of those files.
                    if (Recover(updates, target)) Log(log, "mise à jour interrompue annulée avant la nouvelle");
                    if (Interrupted(updates, target).Count > 0) throw new InvalidOperationException("Une mise à jour interrompue n’a pas pu être annulée entièrement : sa copie de retour arrière reste dans data\\updates.");
                    CopyFolder(AppContext.BaseDirectory, target, Path.Combine(updates, "rollback-" + DateTime.Now.ToString("yyyyMMddHHmmss")));
                }
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
    /// are first copied beside their destinations (.mira-new) and written to the disk, which leaves the installed copy
    /// untouched; then each old file gets a second name in <paramref name="rollback"/> (a hard link, else it moves there)
    /// and the new one takes its name. On any failure the old files come back; one that cannot is listed in the rollback
    /// folder, which is then kept. The journal written before the first rename (<see cref="InProgressMarker"/>) lets the
    /// next start undo a swap that power loss cut short (<see cref="RecoverAtStartup"/>).
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
                // On the disk before any rename: after a power cut, a renamed file must not be one the cache never wrote.
                Retry(() => { File.Copy(Path.Combine(source, relative), incoming, overwrite: true); using var written = new FileStream(incoming, FileMode.Open, FileAccess.ReadWrite, FileShare.None); written.Flush(flushToDisk: true); });
                staged.Add(incoming);
            }
        }
        catch { foreach (var path in staged) TryDelete(path); throw; }
        var journal = Path.Combine(rollback, InProgressMarker);
        var replaced = new List<string>(); var added = new List<string>(); string? linked = null;
        try
        {
            WriteJournal(journal, new(target, files.Where(relative => File.Exists(Path.Combine(target, relative))).ToList(), files.Where(relative => !File.Exists(Path.Combine(target, relative))).ToList()));
            foreach (var relative in files)
            {
                var destination = Path.Combine(target, relative);
                if (File.Exists(destination))
                {
                    var kept = Path.Combine(rollback, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    // A second name keeps the previous file while the new one replaces it in a single rename: the destination
                    // is never missing, even cut short. On another drive than the profile, the previous file moves instead.
                    if (MediaImporter.TryHardLink(destination, kept)) linked = relative;
                    else Retry(() => File.Move(destination, kept, overwrite: true));
                    replaced.Add(relative);
                }
                else added.Add(relative);
                Retry(() => File.Move(destination + NewSuffix, destination, overwrite: true));
                linked = null;
            }
        }
        catch
        {
            var lost = new List<string>();
            // Given its second name but never replaced (a file held open can still get one): the previous file is in place.
            if (linked is not null) { replaced.Remove(linked); TryDelete(Path.Combine(rollback, linked)); }
            foreach (var relative in added) if (File.Exists(Path.Combine(target, relative))) try { Retry(() => File.Delete(Path.Combine(target, relative))); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var relative in replaced)
                try { Retry(() => PutBack(Path.Combine(rollback, relative), Path.Combine(target, relative))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(relative); }
            foreach (var path in staged) TryDelete(path);
            if (lost.Count > 0) try { File.WriteAllLines(Path.Combine(rollback, IncompleteMarker), lost); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // Everything came back: nothing is left to undo. Otherwise the journal stays for the next start.
            else TryDelete(journal);
            throw;
        }
        // Complete: the journal goes first (nothing to undo any more), then the previous files.
        TryDelete(journal);
        try { if (Directory.Exists(rollback)) Directory.Delete(rollback, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What a swap is about to change: the files it replaces and the ones it adds, relative to <see cref="Target"/>.</summary>
    internal sealed record SwapJournal(string Target, List<string> Replaced, List<string> Added);
    /// <summary>Written beside, flushed to the disk, then renamed: a journal is either whole or absent.</summary>
    private static void WriteJournal(string path, SwapJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal); stream.Flush(flushToDisk: true); }
        File.Move(path + ".tmp", path, overwrite: true);
    }
    /// <summary>The swaps of <paramref name="target"/> that never finished, with their rollback folder.</summary>
    internal static List<(string Rollback, SwapJournal Journal)> Interrupted(string updates, string target)
    {
        var found = new List<(string, SwapJournal)>();
        try
        {
            if (!Directory.Exists(updates)) return found;
            var folder = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var rollback in Directory.EnumerateDirectories(updates, "rollback-*").Order(StringComparer.Ordinal))
            {
                var path = Path.Combine(rollback, InProgressMarker);
                if (!File.Exists(path)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<SwapJournal>(File.ReadAllText(path)) is { Target: { Length: > 0 } journalTarget, Replaced: not null, Added: not null } journal
                        && string.Equals(Path.GetFullPath(journalTarget).TrimEnd(Path.DirectorySeparatorChar), folder, StringComparison.OrdinalIgnoreCase))
                        found.Add((rollback, journal));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }
    /// <summary>
    /// Undoes the swaps of <paramref name="target"/> cut short (power loss, a helper ended midway) from their journals: the
    /// previous files go back in place and the added ones leave, so that the copy is again the version that started the
    /// update, which offers it again. True when a file of <paramref name="target"/> changed. A file that cannot be restored
    /// yet keeps the journal, and so its rollback copy, for the next start or the next update.
    /// </summary>
    internal static bool Recover(string updates, string target, int attempts = 8)
    {
        var changed = false;
        foreach (var (rollback, journal) in Interrupted(updates, target))
        {
            var lost = new List<string>();
            foreach (var relative in journal.Added)
            {
                if (Within(target, relative) is not { } destination) continue;
                TryDelete(destination + NewSuffix);
                if (!File.Exists(destination)) continue;
                try { Retry(() => SetAside(destination), attempts); changed = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(relative); }
            }
            foreach (var relative in journal.Replaced)
            {
                if (Within(target, relative) is not { } destination || Within(rollback, relative) is not { } kept) continue;
                TryDelete(destination + NewSuffix);
                // Not reached by the swap: the previous file is still the one in place.
                if (!File.Exists(kept)) continue;
                try { Retry(() => changed |= PutBack(kept, destination), attempts); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lost.Add(relative); }
            }
            try
            {
                // All back: the rollback copy is no longer needed (CleanStaging removes it).
                if (lost.Count == 0) { File.Delete(Path.Combine(rollback, InProgressMarker)); File.Delete(Path.Combine(rollback, IncompleteMarker)); }
                else File.WriteAllLines(Path.Combine(rollback, IncompleteMarker), lost);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return changed;
    }
    /// <summary>
    /// At startup, before the window: a swap of this folder cut short (power loss) left Mira's files half replaced. They are
    /// restored under the update lock (two Mira starting at once restore nothing twice), then Mira starts again on them;
    /// true when this process must end. Nothing to restore, or no new process: false, and the start goes on.
    /// </summary>
    public static bool RecoverAtStartup(string profile, string[] args)
    {
        var updates = Path.Combine(profile, "updates");
        if (Interrupted(updates, AppContext.BaseDirectory).Count == 0) return false;
        using (var busy = new Mutex(false, UpdateLock.Name(profile)))
        {
            var owned = false;
            try
            {
                try { owned = busy.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
                if (!owned || !Recover(updates, AppContext.BaseDirectory)) return false;
                Log(Path.Combine(updates, "update.log"), "mise à jour interrompue : la version précédente est remise en place");
            }
            finally { if (owned) busy.ReleaseMutex(); }
        }
        // This process loaded some of the files it just replaced: a new one starts on the restored version once it ends.
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--after") i++;
            else start.ArgumentList.Add(args[i]);
        start.ArgumentList.Add("--after"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        try { using var _ = Process.Start(start); return true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { return false; }
    }
    /// <summary>
    /// Puts a previous file back; false when it already was (only its second name remained). A file this very process
    /// loaded (Mira restoring its own files at startup) cannot be replaced, only renamed: it is set aside first.
    /// </summary>
    private static bool PutBack(string kept, string destination)
    {
        if (File.Exists(destination) && MediaImporter.IsSameFile(kept, destination)) { File.Delete(kept); return false; }
        try { File.Move(kept, destination, overwrite: true); }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(destination)) { SetAside(destination); File.Move(kept, destination); }
        return true;
    }
    /// <summary>Renamed, then removed when nothing loads it any more (else by the next start, see Updater.CleanStaging).</summary>
    private static void SetAside(string path)
    {
        var aside = $"{path}.{Random.Shared.Next():x}{OldSuffix}";
        File.Move(path, aside);
        TryDelete(aside);
    }
    /// <summary>A path of the journal, kept inside <paramref name="root"/>.</summary>
    private static string? Within(string root, string relative)
    {
        try
        {
            var top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(top, relative));
            return !Path.IsPathRooted(relative) && full.StartsWith(top, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
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
