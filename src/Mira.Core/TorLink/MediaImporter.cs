using System.Runtime.InteropServices;

namespace Mira.Core;

/// <summary>KeepSeeding: a hard link on the same NTFS volume (no second copy, TorLink keeps sharing), otherwise a copy. Move: the download itself moves.</summary>
public enum ImportMode { KeepSeeding, Move }

public sealed record ImportFailure(ImportOperation Operation, string Reason);

/// <summary>Canceled: the pass stopped early (Mira closing); what is listed was done and stays done.</summary>
public sealed record ImportResult(IReadOnlyList<ImportOperation> Placed, IReadOnlyList<ImportOperation> AlreadyThere, IReadOnlyList<ImportFailure> Conflicts, IReadOnlyList<ImportFailure> Failures, bool Linked, bool Copied, bool Moved, bool Canceled = false)
{
    public string? Method => Moved ? "déplacement" : Copied ? "copie" : Linked ? "lien physique" : null;
}

/// <summary>Executes an import plan. Never replaces an existing file and never writes outside the plan's library folder.</summary>
public static class MediaImporter
{
    /// <summary>Copies are written under this suffix, then renamed: Jellyfin never sees half a video.</summary>
    public const string PartialSuffix = ".mira-part";

    public static async Task<ImportResult> ExecuteAsync(ImportPlan plan, ImportMode mode, CancellationToken ct = default)
    {
        var placed = new List<ImportOperation>(); var already = new List<ImportOperation>();
        var conflicts = new List<ImportFailure>(); var failures = new List<ImportFailure>();
        bool linked = false, copied = false, moved = false, canceled = false;
        if (plan.LibraryRoot is null) return new(placed, already, conflicts, failures, linked, copied, moved);
        var root = Path.GetFullPath(plan.LibraryRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        try
        {
            // Videos first: a subtitle never arrives before its film.
            foreach (var operation in plan.Operations.OrderBy(x => x.Role switch { "video" => 0, "extra" => 1, _ => 2 }))
            {
                ct.ThrowIfCancellationRequested();
                string destination;
                try { destination = Path.GetFullPath(operation.Destination); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { failures.Add(new(operation, "Nom de destination invalide.")); continue; }
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !(ReleaseName.IsVideo(destination) || ReleaseName.IsSubtitle(destination)))
                { failures.Add(new(operation, "Destination refusée.")); continue; }
                var source = new FileInfo(operation.Source);
                if (!source.Exists || source.Attributes.HasFlag(FileAttributes.ReparsePoint)) { failures.Add(new(operation, "Fichier source introuvable.")); continue; }
                var target = new FileInfo(destination);
                if (target.Exists)
                {
                    // Same size: an earlier run already placed it. Otherwise it is someone else's file, left untouched.
                    if (target.Length == source.Length) already.Add(operation);
                    else conflicts.Add(new(operation, "Un autre fichier porte déjà ce nom."));
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(target.DirectoryName!);
                    if (mode == ImportMode.Move && SameVolume(source.FullName, destination)) { File.Move(source.FullName, destination, false); moved = true; }
                    else if (mode == ImportMode.KeepSeeding && TryHardLink(source.FullName, destination)) linked = true;
                    else
                    {
                        await CopyAsync(source.FullName, destination, ct);
                        copied = true;
                        if (mode == ImportMode.Move)
                        {
                            // The copy is in place either way; a source still in use simply stays in TorLink's folder.
                            try { File.Delete(source.FullName); moved = true; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    placed.Add(operation);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add(new(operation, Reason(ex))); }
            }
        }
        // Closing Mira: the files already placed are reported, so that they stay tracked.
        catch (OperationCanceledException) { canceled = true; }
        return new(placed, already, conflicts, failures, linked, copied, moved, canceled);
    }

    /// <summary>
    /// Removes the folders a move left empty, walking up to the nearest library root. A root is never removed,
    /// even when one library folder sits inside another (an anime folder inside the series one).
    /// </summary>
    public static void PruneEmptyFolders(IEnumerable<string?> folders, IEnumerable<string?> roots)
    {
        var limits = roots.OfType<string>().Where(x => x.Trim().Length > 0).Select(x => Path.GetFullPath(x).TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Inside(string folder) => limits.Any(root => folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        bool IsRoot(string folder) => limits.Any(root => string.Equals(folder, root, StringComparison.OrdinalIgnoreCase));
        foreach (var start in folders.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Length))
        {
            var folder = Path.GetFullPath(start).TrimEnd('\\', '/');
            while (folder.Length > 0 && Inside(folder) && !IsRoot(folder))
            {
                try
                {
                    if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) break;
                    Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                folder = (Path.GetDirectoryName(folder) ?? "").TrimEnd('\\', '/');
            }
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        var temporary = destination + PartialSuffix;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                output.SetLength(input.Length);
                await input.CopyToAsync(output, 1 << 20, ct);
            }
            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));
            File.Move(temporary, destination, false);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>True when both paths name the same file on disk: the same path, or hard links of one file.</summary>
    public static bool IsSameFile(string a, string b)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
            if (!OperatingSystem.IsWindows() || !SameVolume(a, b)) return false;
            using var first = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var second = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(first, out var x) && GetFileInformationByHandle(second, out var y)
                && x.VolumeSerial == y.VolumeSerial && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool TryHardLink(string source, string destination) =>
        OperatingSystem.IsWindows() && SameVolume(source, destination) && CreateHardLink(Extended(destination), Extended(source), IntPtr.Zero);
    private static bool SameVolume(string a, string b) => string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
    // Win32 calls need the extended prefix beyond MAX_PATH; .NET adds it itself for its own file APIs.
    private static string Extended(string path) => path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    private static string Reason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Accès refusé au dossier de la bibliothèque.",
        IOException io when (io.HResult & 0xFFFF) is 112 or 39 => "Espace disque insuffisant.",
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "Fichier utilisé par une autre application.",
        _ => "Le fichier n’a pas pu être placé dans la bibliothèque."
    };

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    // BY_HANDLE_FILE_INFORMATION: FILETIME fields are 4-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct FileInformation
    {
        public uint Attributes; public long Created, Accessed, Written; public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);
}
