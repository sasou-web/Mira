namespace Mira.Core;

/// <summary>
/// Keeps a folder of cached files within a size budget. The last write time stands for the last use: files are
/// <see cref="Touch"/>ed when read again, and the ones unused for the longest go first.
/// </summary>
public static class DiskCache
{
    /// <summary>
    /// Once the files matching <paramref name="pattern"/> (in every subfolder) exceed <paramref name="maxBytes"/>,
    /// deletes the least recently used until at most <paramref name="keepBytes"/> remain. Temporary files left by
    /// an interrupted download are removed after an hour. A file in use is skipped, never waited for.
    /// </summary>
    public static (int Removed, long Bytes) Trim(string directory, long maxBytes, long keepBytes, string pattern = "*.img")
    {
        if (!Directory.Exists(directory)) return (0, 0);
        var removed = 0; long freed = 0;
        foreach (var stale in Files(directory, "*.tmp").Where(x => x.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1)))
        {
            var length = stale.Length;
            if (TryDelete(stale)) { removed++; freed += length; }
        }
        var files = Files(directory, pattern).ToList();
        var total = files.Sum(x => x.Length);
        if (total <= maxBytes) return (removed, freed);
        foreach (var file in files.OrderBy(x => x.LastWriteTimeUtc))
        {
            if (total <= keepBytes) break;
            var length = file.Length;
            if (!TryDelete(file)) continue;
            total -= length; freed += length; removed++;
        }
        return (removed, freed);
    }
    /// <summary>Marks a cached file as used now; at most once a day, so reading stays a read.</summary>
    public static void Touch(string path)
    {
        try { if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static IEnumerable<FileInfo> Files(string directory, string pattern)
    {
        try { return new DirectoryInfo(directory).EnumerateFiles(pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    private static bool TryDelete(FileInfo file)
    {
        try { file.Delete(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
