using Mira.Core.TorLink;

namespace Mira.Core;

/// <summary>
/// Where a film or a series is on the PC of Jellyfin (« Afficher dans l'explorateur ») and what goes to the Recycle Bin
/// with it (« Mettre à la Corbeille »): always inside one of the library's folders, never one of them.
/// </summary>
public static class TitleFiles
{
    /// <summary>Jellyfin's folders of bonus videos: those beside a film are its own, not other films.</summary>
    private static readonly HashSet<string> ExtrasFolders = new(StringComparer.OrdinalIgnoreCase)
    { "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "samples", "shorts", "clips", "trailers", "other", "theme-music", "backdrops" };

    /// <summary>A film's file or a series' folder, as Jellyfin gave it; null for anything else or a path that is not complete.</summary>
    public static string? Location(MediaItem item) =>
        item.Type is "Movie" or "Series" && item.Path is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;

    /// <summary>
    /// What goes to the Recycle Bin: a series' folder; a film's own folder when no other film is in it (its bonus videos
    /// go with it), otherwise its file alone. Null when that is not strictly inside one of <paramref name="libraries"/>,
    /// or no longer on this computer.
    /// </summary>
    public static string? ToRemove(MediaItem item, IEnumerable<string> libraries)
    {
        if (Location(item) is not { } location) return null;
        var full = Trim(location);
        var roots = libraries.Where(x => x.Length > 0 && Path.IsPathFullyQualified(x)).Select(Trim).ToList();
        bool Inside(string path) => roots.Any(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (!Inside(full)) return null;
        // A series, or a film kept as a disc's folder (VIDEO_TS, BDMV): Jellyfin gives the folder itself.
        if (Directory.Exists(full)) return full;
        if (item.Type == "Series") return null;
        if (!File.Exists(full)) return null;
        var folder = Path.GetDirectoryName(full)!;
        return Inside(folder) && !OtherVideos(folder, full) ? folder : full;
    }

    /// <summary>Another video in <paramref name="folder"/> or below, bonus folders aside: another film, kept.</summary>
    private static bool OtherVideos(string folder, string film)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(ReleaseName.IsVideo).Where(x => !string.Equals(Trim(x), film, StringComparison.OrdinalIgnoreCase))
                .Any(x => !Path.GetRelativePath(folder, Path.GetDirectoryName(x)!).Split(Path.DirectorySeparatorChar).Any(ExtrasFolders.Contains));
        }
        // Unreadable: the file alone, never more than what is known.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The size of a file, or of a folder and everything in it; 0 when unreadable.</summary>
    public static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Sum(x => x.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
