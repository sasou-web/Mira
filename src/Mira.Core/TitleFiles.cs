using System.Text.RegularExpressions;
using Mira.Core.TorLink;

namespace Mira.Core;

/// <summary>
/// Where a film, a series or an episode is on the PC of Jellyfin (« Afficher dans l'explorateur ») and what goes to the
/// Recycle Bin with it (« Mettre à la Corbeille »), a season too: always inside one of the library's folders, never one
/// of them, never a series' folder for less than the series. Cases shared with the tests: tests/fixtures/recycle.json.
/// </summary>
public static partial class TitleFiles
{
    /// <summary>Jellyfin's folders of bonus videos: those beside a film are its own, not other films.</summary>
    private static readonly HashSet<string> ExtrasFolders = new(StringComparer.OrdinalIgnoreCase)
    { "extras", "featurettes", "behind the scenes", "deleted scenes", "interviews", "scenes", "samples", "shorts", "clips", "trailers", "other", "theme-music", "backdrops" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".tbn" };
    /// <summary>What a subtitle's name may say besides its language (« Film.en.forced.srt », « Film.fr.sdh.srt »).</summary>
    private static readonly HashSet<string> SubtitleFlags = new(StringComparer.OrdinalIgnoreCase) { "forced", "sdh", "cc", "hi", "default", "full", "signs", "songs" };

    private static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>A film's file, a series' folder or an episode's file, as Jellyfin gave it; null for anything else or a path that is not complete.</summary>
    public static string? Location(MediaItem item) =>
        item.Type is "Movie" or "Series" or "Episode" && item.Path is { Length: > 0 } path && Path.IsPathFullyQualified(path) ? path : null;

    /// <summary>
    /// What goes to the Recycle Bin: a series' folder; a film's own folder when no other film is in it (its bonus videos
    /// go with it), otherwise its files alone, with the files of the same name beside them (subtitles, .nfo, images). A
    /// film in several files goes whole: its parts (« Film - cd1.mkv », « Film - cd2.mkv »: its AdditionalParts) and its
    /// versions (« Film - 1080p.mkv », « Film - 720p.mkv »: its MediaSources), Jellyfin giving the first as its Path,
    /// <paramref name="others"/> the rest. A file left behind is listed again by Jellyfin as the film a minute later
    /// (checked on 12.1, for both). An episode the same way, in its series' folder (<paramref name="seriesFolder"/>): its
    /// files and theirs; the last episode of a season folder takes that folder. Null when that is not strictly inside one
    /// of <paramref name="libraries"/>, or no longer on this computer.
    /// </summary>
    public static IReadOnlyList<string>? ToRemove(MediaItem item, IEnumerable<string> libraries, IEnumerable<string>? others = null, string? seriesFolder = null)
    {
        if (Location(item) is not { } location) return null;
        var full = Trim(location);
        var roots = Roots(libraries);
        if (!Inside(roots, full)) return null;
        var own = new List<string> { full };
        if (item.Type is "Movie" or "Episode")
        {
            foreach (var other in others ?? [])
            {
                // A version that is no file (an address of a .strm) is not on this computer.
                if (other.Length == 0 || !Path.IsPathFullyQualified(other)) continue;
                var path = Trim(other);
                // Never the title without all of its files: none of it outside the libraries.
                if (!Inside(roots, path)) return null;
                if ((File.Exists(path) || Directory.Exists(path)) && !own.Contains(path, StringComparer.OrdinalIgnoreCase)) own.Add(path);
            }
        }
        // A series, or a film kept as a disc's folder (VIDEO_TS, BDMV): Jellyfin gives the folder itself.
        if (Directory.Exists(full)) return item.Type == "Episode" ? null : own;
        if (item.Type == "Series") return null;
        if (!File.Exists(full)) return null;
        var folder = Path.GetDirectoryName(full)!;
        if (item.Type == "Movie") return Inside(roots, folder) && !OtherVideos(folder, own) ? [folder] : WithCompanions(own);
        // An episode: its season's folder when nothing else is left in it, never the series' folder.
        if (SeasonFolder(roots, folder, seriesFolder) && !OtherVideos(folder, own)) return [folder, .. SeasonImages(seriesFolder!, item.ParentIndexNumber ?? 1)];
        return WithCompanions(own);
    }

    /// <summary>
    /// What goes with a season: its folder (<paramref name="seasonFolder"/>, as Jellyfin gave it, else the one holding all
    /// its files) when every video in it is one of <paramref name="files"/> (bonus folders aside), otherwise each of
    /// its episodes' files and theirs; its pictures in the series' folder either way (« season10-poster.jpg »). Null when
    /// one of its files is outside the libraries, or none is on this computer.
    /// </summary>
    public static IReadOnlyList<string>? SeasonToRemove(int season, IEnumerable<string> files, IEnumerable<string> libraries, string seriesFolder, string? seasonFolder = null)
    {
        var roots = Roots(libraries);
        if (!Path.IsPathFullyQualified(seriesFolder)) return null;
        var series = Trim(seriesFolder);
        if (!Inside(roots, series)) return null;
        var own = new List<string>();
        foreach (var file in files)
        {
            if (file.Length == 0 || !Path.IsPathFullyQualified(file)) continue;
            var path = Trim(file);
            if (!Inside(roots, path)) return null;
            if ((File.Exists(path) || Directory.Exists(path)) && !own.Contains(path, StringComparer.OrdinalIgnoreCase)) own.Add(path);
        }
        if (own.Count == 0) return null;
        var folder = seasonFolder is { Length: > 0 } given && Path.IsPathFullyQualified(given) && Directory.Exists(given) ? Trim(given) : CommonFolder(own);
        if (folder is not null && SeasonFolder(roots, folder, series) && own.All(x => x.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) && !OtherVideos(folder, own))
            return [folder, .. SeasonImages(series, season)];
        return [.. WithCompanions(own), .. SeasonImages(series, season)];
    }

    /// <summary>
    /// The files of the same name beside a video (« Episode.en.srt », « Episode.nfo », « Episode-thumb.jpg »,
    /// « Film-poster.jpg », its « .trickplay » folder): they belong to it alone. Never another video.
    /// </summary>
    public static IReadOnlyList<string> Companions(string video)
    {
        var folder = Path.GetDirectoryName(video);
        if (folder is null || !Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(video);
        var found = new List<string>();
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
            // « Film.Returns.nfo » is « Film.Returns.mkv »'s, not « Film.mkv »'s: the longest video name it starts with owns it.
            var longer = entries.Where(x => File.Exists(x) && ReleaseName.IsVideo(x)).Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Where(x => x.Length > stem.Length && x.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.Length <= stem.Length || !name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                if (longer.Any(x => name.Length > x.Length && name.StartsWith(x, StringComparison.OrdinalIgnoreCase) && name[x.Length] is '.' or '-')) continue;
                var rest = name[stem.Length..];
                if (Directory.Exists(entry)) { if (rest.Equals(".trickplay", StringComparison.OrdinalIgnoreCase)) found.Add(Trim(entry)); continue; }
                if (ReleaseName.IsVideo(name)) continue;
                if (Companion().IsMatch(rest) && (ReleaseName.IsSubtitle(name) || ImageExtensions.Contains(Path.GetExtension(name)) || rest.Equals(".nfo", StringComparison.OrdinalIgnoreCase)))
                    found.Add(Trim(entry));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>What the confirmation lists: videos, subtitles (and their languages), the rest (.nfo, images), sizes.</summary>
    public sealed record Contents(int Videos, long VideoSize, int Subtitles, IReadOnlyList<string> Languages, int Others, IReadOnlyList<string> OtherKinds, long Size, int Folders);

    /// <summary>Every file that goes, a folder's own included, sorted out for the confirmation; unreadable ones left out.</summary>
    public static Contents Describe(IEnumerable<string> targets)
    {
        int videos = 0, subtitles = 0, others = 0, folders = 0; long videoSize = 0, size = 0;
        var languages = new List<string>(); var kinds = new List<string>();
        foreach (var target in targets)
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(target) ? [.. Directory.EnumerateFiles(target, "*", Everything)] : File.Exists(target) ? [target] : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }
            if (Directory.Exists(target)) folders++;
            foreach (var file in files)
            {
                long length;
                try { length = new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                size += length;
                var name = Path.GetFileName(file);
                if (ReleaseName.IsVideo(name)) { videos++; videoSize += length; }
                else if (ReleaseName.IsSubtitle(name)) { subtitles++; if (SubtitleLanguage(name) is { } language && !languages.Contains(language)) languages.Add(language); }
                else { others++; var kind = Kind(name); if (!kinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) kinds.Add(kind); }
            }
        }
        return new Contents(videos, videoSize, subtitles, languages, others, kinds, size, folders);
    }

    /// <summary>The language a subtitle's name gives (« Film.fr.forced.srt »: French), in Mira's language; null when none.</summary>
    public static string? SubtitleLanguage(string name)
    {
        var parts = Path.GetFileNameWithoutExtension(name).Split('.');
        for (var i = parts.Length - 1; i >= 1; i--)
        {
            if (SubtitleFlags.Contains(parts[i])) continue;
            return parts[i].Length is >= 2 and <= 7 ? PlayerText.Language(parts[i].ToLowerInvariant()) : null;
        }
        return null;
    }

    /// <summary>« .nfo », « -thumb.jpg », « .jpg »: what kind of file it is, for the confirmation.</summary>
    private static string Kind(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        var dash = stem.LastIndexOf('-');
        return ImageExtensions.Contains(extension) && dash >= 0 && ImageSuffix().IsMatch(stem[dash..]) ? stem[dash..].ToLowerInvariant() + extension : extension;
    }

    private static List<string> WithCompanions(List<string> own)
    {
        var all = new List<string>(own);
        foreach (var path in own.Where(File.Exists).ToList())
            foreach (var companion in Companions(path))
                if (!all.Contains(companion, StringComparer.OrdinalIgnoreCase)) all.Add(companion);
        return all;
    }

    /// <summary>A season's pictures in its series' folder (« season10-poster.jpg », « season-specials-poster.jpg »).</summary>
    private static List<string> SeasonImages(string seriesFolder, int season)
    {
        var series = Trim(seriesFolder);
        if (!Directory.Exists(series)) return [];
        try
        {
            return [.. Directory.EnumerateFiles(series, "season*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Where(x => SeasonImage().Match(Path.GetFileName(x)) is { Success: true } match
                    && (match.Groups[1].Value.Equals("-specials", StringComparison.OrdinalIgnoreCase) ? season == 0 : int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == season))
                .Select(Trim)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A folder of its own inside the series' folder: never the series' folder itself, never outside it.</summary>
    private static bool SeasonFolder(List<string> roots, string folder, string? seriesFolder) =>
        seriesFolder is { Length: > 0 } && Path.IsPathFullyQualified(seriesFolder) && Inside(roots, folder)
        && folder.StartsWith(Trim(seriesFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? CommonFolder(List<string> paths)
    {
        var folders = paths.Select(x => Path.GetDirectoryName(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders is [{ } one] ? one : null;
    }

    /// <summary>Another video in <paramref name="folder"/> or below, bonus folders aside: another title, kept.</summary>
    private static bool OtherVideos(string folder, IReadOnlyList<string> own)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", Everything)
                .Where(ReleaseName.IsVideo).Where(x => !own.Contains(Trim(x), StringComparer.OrdinalIgnoreCase))
                .Any(x => !Path.GetRelativePath(folder, Path.GetDirectoryName(x)!).Split(Path.DirectorySeparatorChar).Any(ExtrasFolders.Contains));
        }
        // Unreadable: the files alone, never more than what is known.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The size of a file, or of a folder and everything in it; 0 when unreadable.</summary>
    public static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", Everything).Sum(x => x.Length) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static List<string> Roots(IEnumerable<string> libraries) => [.. libraries.Where(x => x.Length > 0 && Path.IsPathFullyQualified(x)).Select(Trim)];
    private static bool Inside(List<string> roots, string path) => roots.Any(root => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // After a video's name: « .en.srt », « .fr.forced.ass », « .nfo », « .jpg », « -thumb.jpg », « -poster.png »; never
    // « 2.nfo » or « .2.srt » (another title whose name starts the same).
    [GeneratedRegex(@"^(?:(?:\.[A-Za-z][A-Za-z-]{0,19})*\.[A-Za-z0-9]{2,4}|-(?:thumb|poster|fanart|backdrop|landscape|banner|logo|clearlogo|clearart|disc|discart)\.[A-Za-z]{3,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex Companion();
    [GeneratedRegex(@"^-(?:thumb|poster|fanart|backdrop|landscape|banner|logo|clearlogo|clearart|disc|discart)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSuffix();
    [GeneratedRegex(@"^season(\d{1,3}|-specials)-[A-Za-z]+\.(?:jpg|jpeg|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonImage();
}
