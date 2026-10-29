using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>A file's picture as Mira judges it: its resolution (2160, 1440, 1080, 720, 480) and its source (<see cref="ReleaseInfo.Source"/>); null when unknown.</summary>
public sealed record TitleQuality(int? Resolution, string? Source);

/// <summary>
/// « Chercher une meilleure version » of a film or an episode (an administrator): the results of TorLink that are this
/// very title, in a better picture than its file, as Mira web judges them (src/Mira.Web/js/upgrade.js; cases shared in
/// tests/fixtures/upgrade.json). Never a second version kept beside: the old file goes to the Recycle Bin of the PC of
/// Jellyfin once the new one is checked (<see cref="TitleSwap"/>).
/// </summary>
public static partial class TitleUpgrade
{
    /// <summary>Its length may differ by this share at most from the old one's: another cut, or another title, otherwise.</summary>
    public const double LengthTolerance = 0.02;

    // From the worst to the best; a version filmed in a theatre is below everything.
    private static readonly Dictionary<string, int> SourceTier = new()
    { ["cam"] = 0, ["dvd"] = 1, ["hdtv"] = 2, ["hdrip"] = 2, ["webrip"] = 3, ["webdl"] = 4, ["bluray"] = 5, ["remux"] = 6 };
    // A release that does not say where it comes from: often a re-encode of anything.
    private const int UnknownRelease = 2;

    // Jellyfin names a film without metadata after its folder: « Dune (2021) ».
    [GeneratedRegex(@"\s*\((?:19|20)\d\d\)\s*$")] private static partial Regex Year();
    [GeneratedRegex(@"[:;/\\|·–—]+")] private static partial Regex Punctuation();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    private static string NameKey(string? name) => ReleaseRanking.TitleKey(Year().Replace(name ?? "", ""));
    // Japanese, Chinese, Korean…: an original title in another script is no name to search releases by.
    private static bool Latin(string text) => text.All(c => !char.IsLetter(c) || c <= '\u024F' || c is >= '\u1E00' and <= '\u1EFF');

    /// <summary>
    /// The picture of a title's file: its resolution by the width or height of its first video stream (a film cropped to
    /// 3840 × 1600 is still 4K, as <see cref="MediaQuality"/> says it), its source by its file's name, else its folder's.
    /// </summary>
    public static TitleQuality Current(MediaItem item)
    {
        var source = item.MediaSources?.FirstOrDefault();
        var path = source?.Path ?? item.Path;
        return Current(source?.MediaStreams.FirstOrDefault(x => x.Type == "Video") is { } video ? (video.Width ?? 0, video.Height ?? 0) : (0, 0), path);
    }

    /// <summary>The picture of a file of <paramref name="size"/> (width, height; 0 when unknown) at <paramref name="path"/>.</summary>
    public static TitleQuality Current((int Width, int Height) size, string? path)
    {
        var (width, height) = size;
        int? resolution = width >= 3800 || height >= 2000 ? 2160 : width >= 2500 || height >= 1400 ? 1440 : width >= 1800 || height >= 1000 ? 1080
            : width >= 1200 || height >= 700 ? 720 : width > 0 || height > 0 ? 480 : null;
        string? source = null;
        if (path is { Length: > 0 })
        {
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            source = ReleaseRanking.Parse(parts[^1]).Source ?? (parts.Length > 1 ? ReleaseRanking.Parse(parts[^2]).Source : null);
        }
        return new(resolution, source);
    }

    private static int ResolutionTier(int? resolution) => resolution switch { >= 2160 => 4, >= 1080 => 3, >= 720 => 2, > 0 => 1, _ => 0 };

    /// <summary>
    /// Whether <paramref name="release"/> has a better picture than <paramref name="current"/>: a higher resolution from a
    /// source as good or one step below (a 1080p WEB-DL beats a 720p BluRay), never a 4K from a lesser source (a 1080p
    /// BluRay beats a 4K WEB-DL); the same resolution from a better source. Anything beats a version filmed in a theatre;
    /// a release that does not say its resolution, a whole season or a version filmed in a theatre is never offered. A
    /// file whose source is unknown counts as a WEB-DL (a DVD under 720p), the most common.
    /// </summary>
    public static bool Better(TitleQuality current, ReleaseInfo release)
    {
        if (release.Pack || release.Source == "cam" || release.Resolution is not { } resolution) return false;
        int r = ResolutionTier(resolution), c = ResolutionTier(current.Resolution);
        if (current.Source == "cam") return r >= 2;
        var rs = release.Source is { } s && SourceTier.TryGetValue(s, out var tier) ? tier : UnknownRelease;
        var cs = current.Source is { } k && SourceTier.TryGetValue(k, out var known) ? known : c <= 1 ? SourceTier["dvd"] : SourceTier["webdl"];
        if (r > c) return rs >= cs - (resolution >= 2160 ? 0 : 1);
        return r == c && rs > cs;
    }

    /// <summary>
    /// Whether <paramref name="release"/> is this very title: a film of the same name (or original name) and year (±1, a
    /// release without a year passes); an episode of the series under the same season and number (one numbered without a
    /// season only in its first season). Never a whole season or several films.
    /// </summary>
    public static bool Fits(MediaItem item, ReleaseInfo release)
    {
        if (release.Pack) return false;
        var keys = new HashSet<string> { ReleaseRanking.TitleKey(release.Title) };
        if (release.Alternate is { } alternate) keys.Add(ReleaseRanking.TitleKey(alternate));
        if (item.Type == "Movie")
        {
            if (release.Episode is not null) return false;
            var named = keys.Contains(NameKey(item.Name)) || (item.OriginalTitle is { Length: > 0 } original && keys.Contains(NameKey(original)));
            return named && !(release.Year is { } y && item.ProductionYear is { } p && Math.Abs(p - y) > 1);
        }
        if (item.Type != "Episode" || release.Episode is not { } episode || item.IndexNumber is not { } number || !keys.Contains(NameKey(item.SeriesName))) return false;
        if (release.Absolute ? item.ParentIndexNumber != 1 : release.Season != item.ParentIndexNumber) return false;
        return episode == number && (release.LastEpisode ?? episode) == (item.IndexNumberEnd ?? number);
    }

    /// <summary>
    /// Why this title's file cannot be replaced on the PC of Jellyfin, or null when it can: a film or an episode, one
    /// file (never several versions, nor a disc's folder), its length known, inside one of <paramref name="libraries"/>
    /// (Jellyfin's library folders, as this PC sees them).
    /// </summary>
    public static string? Problem(MediaItem item, IEnumerable<string> libraries)
    {
        if (item.Type is not ("Movie" or "Episode")) return L("Seuls un film ou un épisode se remplacent.", "Only a film or an episode can be replaced.");
        if (item.MediaSources is { Count: > 1 }) return L("Ce titre a plusieurs versions dans Jellyfin : Mira n’en remplace pas une seule.", "This title has several versions in Jellyfin: Mira doesn’t replace just one.");
        var path = item.MediaSources?.FirstOrDefault()?.Path ?? item.Path;
        if (path is not { Length: > 0 } || !ReleaseName.IsVideo(path)) return L("Ce titre n’est pas un seul fichier vidéo.", "This title isn’t a single video file.");
        if (item.RunTimeTicks is not > 0) return L("Jellyfin ne connaît pas encore la durée de ce titre.", "Jellyfin doesn’t know this title’s length yet.");
        string Folder(string folder) => folder.Replace('\\', '/').TrimEnd('/') + "/";
        var file = path.Replace('\\', '/');
        return libraries.Any(x => x is { Length: > 0 } && file.StartsWith(Folder(x), StringComparison.OrdinalIgnoreCase)) ? null
            : L("Ce fichier n’est dans aucune bibliothèque de Jellyfin sur ce PC.", "This file isn’t in any Jellyfin library on this PC.");
    }

    /// <summary>The title's file, as Jellyfin has it.</summary>
    public static string? FileOf(MediaItem item) => item.MediaSources?.FirstOrDefault()?.Path ?? item.Path;

    /// <summary>What TorLink searches: a film by its original name when written in Latin letters (releases mostly use it), and its year; an episode as « Show S01E02 ».</summary>
    public static string Query(MediaItem item)
    {
        string Clean(string text) => Spaces().Replace(Punctuation().Replace(Year().Replace(text, ""), " "), " ").Trim();
        if (item.Type == "Episode")
            return Clean($"{item.SeriesName} S{item.ParentIndexNumber ?? 1:00}E{item.IndexNumber ?? 1:00}");
        var name = item.OriginalTitle is { Length: > 0 } original && Latin(original) ? original : item.Name;
        return Clean(item.ProductionYear is { } year ? $"{Clean(name)} {year}" : name);
    }

    /// <summary>The results of a search that are this title in a better picture than its file, as TorLink found them.</summary>
    public static List<RelayHit> Candidates(MediaItem item, IEnumerable<RelayHit> hits)
    {
        var current = Current(item);
        return hits.Where(hit => ReleaseRanking.Parse(hit.Name) is var release && Fits(item, release) && Better(current, release)).ToList();
    }

    /// <summary>
    /// What Jellyfin's ffprobe said of a file (<c>-show_entries format=duration:stream=codec_type -of json</c>): its length,
    /// null when it found no picture in it, or no length.
    /// </summary>
    public static TimeSpan? ProbedLength(string? json)
    {
        try
        {
            using var document = JsonDocument.Parse(json is { Length: > 0 } ? json : "{}");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var picture = root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array
                && streams.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("codec_type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "video");
            return picture && root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object && format.TryGetProperty("duration", out var duration)
                && duration.ValueKind == JsonValueKind.String && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 1e7
                ? TimeSpan.FromSeconds(seconds) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The new file lasts as long as the old one, within <see cref="LengthTolerance"/>.</summary>
    public static bool SameLength(TimeSpan old, TimeSpan found) => old > TimeSpan.Zero && found > TimeSpan.Zero && Math.Abs((found - old).Ticks) <= old.Ticks * LengthTolerance;

    /// <summary>« 720p · WEBRip », « 1080p · source inconnue », « Filmé en salle »: the picture of a file, in the words of the page.</summary>
    public static string Line(TitleQuality quality)
    {
        var resolution = quality.Resolution switch { 2160 => "4K", 480 => "SD", { } r => $"{r}p", null => L("Définition inconnue", "Unknown resolution") };
        return string.Join(" · ", resolution, quality.Source is { } source ? ReleaseRanking.SourceLabel(source) : L("source inconnue", "unknown source"));
    }

    /// <summary>« Dune (2021) », « The Bear · S01E02 »: the title replaced, in a list or a sentence.</summary>
    public static string Title(MediaItem item) => item.Type == "Episode"
        ? $"{item.SeriesName} · S{item.ParentIndexNumber ?? 1:00}E{item.IndexNumber ?? 1:00}"
        : item.ProductionYear is { } year && !Year().IsMatch(item.Name) ? $"{item.Name} ({year})" : item.Name;
}
