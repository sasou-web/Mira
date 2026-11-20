using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A subtitle file of a season's pack: its name (the one inside the .zip for an entry of one) and its content.</summary>
public sealed record PackFile(string Name, byte[] Data);
/// <summary>A file of the pack and the episode it goes with (null: none found, to choose by hand), with what its name says.</summary>
public sealed record PackMatch(PackFile File, MediaItem? Episode, (string Format, string Language, bool Forced, bool HearingImpaired) Guess);

/// <summary>
/// « Sous-titres d'une saison » (ogsas, 2026-11-20): a .zip or several subtitle files chosen at once for a season, each
/// put on its episode from its name (« S10E03 », « E03 », « - 03 », a number counted from the start of the series),
/// its language read as for one file (<see cref="JellyfinSubtitles.Guess"/>), then kept on Jellyfin one by one.
/// </summary>
public static class SeasonSubtitles
{
    /// <summary>Most files a pack is read for: more is no season's subtitles.</summary>
    public const int MaxFiles = 400;
    private static readonly Regex SeasonEpisode = new(@"(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,4})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Crossed = new(@"(?<![a-z0-9])(\d{1,2})x(\d{2,4})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EpisodeTag = new(@"(?<![a-z0-9])(?:e|ep|episode|épisode)[ ._-]?(\d{1,4})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Dashed = new(@"\s-\s(\d{1,4})(?:v\d)?(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Bare = new(@"(?<![a-z0-9])(?<!\d\.)(\d{1,4})(?:v\d)?(?![0-9]|p\b|bit|x|\.\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Bracketed = new(@"\[[^\]]*\]|\([^)]*\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The season and episode a file's name gives: « S10E03 » or « 10x03 » (season and episode), else « E03 », « Ep 3 »,
    /// else « - 03 », else the first number standing alone outside brackets that is no year, resolution or codec (« Show - 03 [1080p] »).
    /// Null when it gives none.
    /// </summary>
    public static (int? Season, int Episode)? Number(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (SeasonEpisode.Match(name) is { Success: true } se) return (int.Parse(se.Groups[1].Value), int.Parse(se.Groups[2].Value));
        if (Crossed.Match(name) is { Success: true } x) return (int.Parse(x.Groups[1].Value), int.Parse(x.Groups[2].Value));
        if (EpisodeTag.Match(name) is { Success: true } e) return (null, int.Parse(e.Groups[1].Value));
        if (Dashed.Match(name) is { Success: true } d && int.Parse(d.Groups[1].Value) is > 0 and < 1900 and var dashed) return (null, dashed);
        foreach (Match m in Bare.Matches(Bracketed.Replace(name, " ")))
        {
            var n = int.Parse(m.Groups[1].Value);
            if (n is >= 1900 and <= 2099 || n is 264 or 265 or 480 or 576 or 720 or 1080 or 2160 || n == 0) continue;
            return (null, n);
        }
        return null;
    }

    /// <summary>
    /// Each file on its episode of <paramref name="season"/>: by its episode number, else (a number past the season's)
    /// counted from the start of the series, <paramref name="before"/> being the episodes of the seasons before it. A
    /// file that names another season, or no episode of this one, is left without one.
    /// </summary>
    public static IReadOnlyList<PackMatch> Match(IEnumerable<PackFile> files, IReadOnlyList<MediaItem> episodes, int season, int before, string? preferences) =>
        files.Select(file =>
        {
            var guess = JellyfinSubtitles.Guess(file.Name, preferences)!.Value;
            MediaItem? episode = null;
            if (Number(file.Name) is { } number && (number.Season is null || number.Season == season))
                episode = episodes.FirstOrDefault(x => x.IndexNumber == number.Episode)
                    ?? (before > 0 && number.Episode > before ? episodes.FirstOrDefault(x => x.IndexNumber == number.Episode - before) : null);
            return new PackMatch(file, episode, guess);
        }).OrderBy(x => x.Episode is null).ThenBy(x => x.Episode?.IndexNumber).ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The subtitle files among those chosen: each file of a format Jellyfin keeps, and each such entry of a .zip
    /// (folders inside it included, never macOS's « __MACOSX » copies); any too big, or past <see cref="MaxFiles"/>, left out.
    /// </summary>
    public static async Task<IReadOnlyList<PackFile>> ReadAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var found = new List<PackFile>();
        foreach (var path in paths)
        {
            if (found.Count >= MaxFiles) break;
            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries)
                {
                    if (found.Count >= MaxFiles) break;
                    if (entry.FullName.Contains("__MACOSX", StringComparison.Ordinal) || !IsSubtitle(entry.Name) || entry.Length > JellyfinSubtitles.MaxSize) continue;
                    using var stream = entry.Open(); using var copy = new MemoryStream();
                    await stream.CopyToAsync(copy, ct);
                    found.Add(new PackFile(entry.Name, copy.ToArray()));
                }
            }
            else if (IsSubtitle(path) && new FileInfo(path).Length <= JellyfinSubtitles.MaxSize) found.Add(new PackFile(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct)));
        }
        return found;
    }
    private static bool IsSubtitle(string name) => name.Length > 0 && JellyfinSubtitles.Formats.Contains(Path.GetExtension(name).TrimStart('.').ToLowerInvariant());
}
