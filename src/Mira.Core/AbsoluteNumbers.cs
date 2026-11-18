using System.Globalization;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// Episodes numbered from the series' start in their season's folder (« Season 02/Show - S02E26 », an anime pack
/// numbered 1 to 89 across four seasons), where TheTVDB numbers each season from 1: Jellyfin then keeps a virtual
/// episode for each of the season's numbers (S02E01–12) beside the held ones, which find no metadata either. A held
/// season reads as numbered from the start when every one of its episodes is past the season's last number at
/// TheTVDB and past the episodes of the seasons before, and lands inside the season once those are taken off
/// (ogsas, 2026-11-18, Shingeki no Kyojin: S02E26–37, S03E38–59, S04E60–89). Its virtual episodes held this way are
/// not missing (<see cref="Unheld"/>), and the files Mira placed get the season's numbers (<see cref="Renames"/>).
/// The iPhone's AbsoluteNumbers, cases in tests/fixtures/absolute.json.
/// </summary>
public static partial class AbsoluteNumbers
{
    /// <summary>The number in its season of each held episode numbered from the series' start: episode id → (season, number).</summary>
    public static Dictionary<string, (int Season, int Episode)> Read(IEnumerable<MediaItem> held, IEnumerable<MediaItem> virtuals)
    {
        var own = held.Where(x => x.LocationType != "Virtual" && x.ParentIndexNumber is >= 1 && x.IndexNumber is >= 1).ToList();
        var known = virtuals.Where(x => x.ParentIndexNumber is >= 1 && x.IndexNumber is >= 1).ToList();
        var found = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        if (known.Count == 0) return found;
        var offset = 0;
        foreach (var season in own.Concat(known).Select(x => x.ParentIndexNumber!.Value).Distinct().Order())
        {
            var mine = own.Where(x => x.ParentIndexNumber == season).ToList();
            var last = known.Where(x => x.ParentIndexNumber == season).Select(x => x.IndexNumber!.Value).DefaultIfEmpty(0).Max();
            bool Inside(MediaItem x) => x.IndexNumber > last && x.IndexNumber > offset && (x.IndexNumberEnd ?? x.IndexNumber) - offset <= last;
            if (offset > 0 && last > 0 && mine.Count > 0 && mine.All(Inside))
            {
                foreach (var x in mine) found[x.Id] = (season, x.IndexNumber!.Value - offset);
                offset += last;
            }
            else offset += Math.Max(last, mine.Select(x => x.IndexNumberEnd ?? x.IndexNumber!.Value).DefaultIfEmpty(0).Max());
        }
        return found;
    }

    /// <summary>The held episodes, those numbered from the start under their season's number (« S02E26 » as S02E01).</summary>
    public static List<MediaItem> Seasonal(IEnumerable<MediaItem> held, IEnumerable<MediaItem> virtuals)
    {
        var own = held.ToList();
        var read = Read(own, virtuals);
        return read.Count == 0 ? own : own.Select(x => read.TryGetValue(x.Id, out var at)
            ? x with { IndexNumber = at.Episode, IndexNumberEnd = x.IndexNumberEnd - x.IndexNumber + at.Episode } : x).ToList();
    }

    /// <summary>The virtual episodes the library doesn't hold under another number: those still missing or to come.</summary>
    public static List<MediaItem> Unheld(IEnumerable<MediaItem> virtuals, IEnumerable<MediaItem> held)
    {
        var known = virtuals.ToList();
        var own = held.ToList();
        var read = Read(own, known);
        if (read.Count == 0) return known;
        var taken = new HashSet<(int, int)>();
        foreach (var x in own)
            if (read.TryGetValue(x.Id, out var at))
                for (var n = at.Episode; n <= at.Episode + ((x.IndexNumberEnd ?? x.IndexNumber!.Value) - x.IndexNumber!.Value); n++) taken.Add((at.Season, n));
        return known.Where(x => !(x.ParentIndexNumber is { } s && x.IndexNumber is { } n && taken.Contains((s, n)))).ToList();
    }

    /// <summary>
    /// The files to rename so Jellyfin finds each episode under its season's number: of <paramref name="placed"/> (the
    /// files Mira put in the library, video and subtitles), those of a held episode numbered from the start, named as
    /// Mira names them (« Show - S02E26.mkv », « Show - S02E26.fr.srt ») → the same with the season's number
    /// (« Show - S02E01 »). Empty when nothing is to rename, or when a new name is taken (by a file of the library or
    /// another of the list).
    /// </summary>
    public static List<(string From, string To)> Renames(IEnumerable<string> placed, IEnumerable<MediaItem> held, IEnumerable<MediaItem> virtuals, Func<string, bool> exists)
    {
        var files = placed.ToList();
        var own = held.ToList();
        var read = Read(own, virtuals);
        var renames = new List<(string From, string To)>();
        foreach (var episode in own)
        {
            if (episode.Path is not { Length: > 0 } path || !read.TryGetValue(episode.Id, out var at)) continue;
            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            // Either separator: a Windows server's paths read the same anywhere.
            var start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
            var dot = path.LastIndexOf('.');
            var name = dot > start ? path[start..dot] : path[start..];
            var matches = Code().Matches(name);
            if (matches.Count != 1 || matches[0] is not { } code || int.Parse(code.Groups[1].Value, CultureInfo.InvariantCulture) != at.Season || int.Parse(code.Groups[2].Value, CultureInfo.InvariantCulture) != episode.IndexNumber) continue;
            var span = code.Groups[3].Success ? int.Parse(code.Groups[3].Value, CultureInfo.InvariantCulture) - int.Parse(code.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            var fresh = $"S{at.Season:00}E{at.Episode:00}" + (span > 0 ? $"-E{at.Episode + span:00}" : "");
            var oldStem = path[..(start + name.Length)];
            var newStem = path[..start] + name[..code.Index] + fresh + name[(code.Index + code.Length)..];
            // The episode's file and those beside it with its name: « .fr.srt », « .en.forced.ass ».
            foreach (var file in files.Where(f => f.StartsWith(oldStem + ".", StringComparison.OrdinalIgnoreCase)))
                renames.Add((file, newStem + file[oldStem.Length..]));
        }
        var sources = renames.Select(x => x.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = renames.Select(x => x.To).ToList();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count || targets.Any(x => !sources.Contains(x) && exists(x))) return [];
        // A rename onto a file the list moves itself: never in the order they come (S03E38 → S03E01 takes no held name).
        if (targets.Any(sources.Contains)) return [];
        return renames;
    }

    // « S02E26 », « S02E26-E27 », « S02E26-27 », a word of its own.
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,2})E(\d{1,4})(?:-E?(\d{1,4}))?(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Code();
}
