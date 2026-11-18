using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>
/// A series' bonus videos on its page, « La pastille » (ogsas, 2026-11-18, mock-up B of /mnt/project-files/extras-serie/):
/// a pill « Extras » after its last season, also for a single season, shows them in place of the episodes, sorted by kind
/// (openings, endings, then Jellyfin's kinds, then the rest), one row each with « Show all », the episodes' tiles. A film
/// keeps its row of extras under its facts. Windows and Mac lay it out from these rules; the iPhone has its own copy
/// (Library/Extras.swift), both held to tests/fixtures/series-extras.json.
/// </summary>
public static partial class SeriesExtras
{
    /// <summary>The pill after the seasons and the heading over the bonus videos.</summary>
    public static string Name => L("Bonus", "Extras");

    public sealed record Group(string Key, string Name, IReadOnlyList<MediaItem> Items);

    /// <summary>What a bonus video's name says: a textless opening or ending, its number, the title it belongs to, which copy.</summary>
    public sealed record Reading(string? Kind, int? Number, string? From, int? Copy);

    // Jellyfin's kinds of bonus after the openings and endings, in this order; the others (« Unknown » of an Extras folder) last.
    private static readonly string[] Kinds = ["Featurette", "BehindTheScenes", "Interview", "DeletedScene", "Scene", "Short", "Clip", "Sample", "Trailer"];

    /// <summary>
    /// The bonus videos by kind: « Openings », « Endings » (read from their names, NCOP/NCED as Mira names them), then
    /// Jellyfin's own kinds, then « Autres »; empty groups left out. Openings and endings by title, number and copy;
    /// the others as Jellyfin gives them.
    /// </summary>
    public static IReadOnlyList<Group> Groups(IReadOnlyList<MediaItem> extras, string? show)
    {
        var read = extras.Select((x, i) => (Item: x, Read: Read(x, show), At: i)).ToList();
        var groups = new List<Group>();
        foreach (var (kind, name) in new[] { ("opening", L("Openings", "Openings")), ("ending", L("Endings", "Endings")) })
        {
            var items = read.Where(x => x.Read.Kind == kind).OrderBy(x => x.Read.From ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Read.Number ?? 0).ThenBy(x => x.Read.Copy ?? 1).ThenBy(x => x.At).Select(x => x.Item).ToList();
            if (items.Count > 0) groups.Add(new Group(kind, name, items));
        }
        var rest = read.Where(x => x.Read.Kind is null).ToList();
        for (var i = 0; i < Kinds.Length; i++)
        {
            var kind = Kinds[i];
            // Clip and Sample are one kind on the page (« Extraits »).
            if (kind == "Sample") continue;
            var items = rest.Where(x => x.Item.ExtraType == kind || kind == "Clip" && x.Item.ExtraType == "Sample").Select(x => x.Item).ToList();
            if (items.Count > 0) groups.Add(new Group(kind, Plural(kind), items));
        }
        var others = rest.Where(x => !Kinds.Contains(x.Item.ExtraType)).Select(x => x.Item).ToList();
        if (others.Count > 0) groups.Add(new Group("other", L("Autres", "Other"), others));
        return groups;
    }

    private static string Plural(string kind) => kind switch
    {
        "Featurette" => L("Making-of", "Featurettes"),
        "BehindTheScenes" => L("Coulisses", "Behind the scenes"),
        "Interview" => L("Interviews", "Interviews"),
        "DeletedScene" => L("Scènes coupées", "Deleted scenes"),
        "Scene" => L("Scènes", "Scenes"),
        "Short" => L("Courts métrages", "Shorts"),
        "Clip" => L("Extraits", "Clips"),
        _ => L("Bandes-annonces", "Trailers")
    };

    /// <summary>
    /// « Bakemonogatari - NCOP01 » (Mira's name, PR #620): an opening, 1, of Bakemonogatari; « Monogatari Series -
    /// NCOP03.2 »: opening 3 of the series itself, its second copy; « [Group] Show - Creditless Ending 2 [BD] »: ending 2.
    /// Anything else is no opening nor ending.
    /// </summary>
    public static Reading Read(string? name, string? show)
    {
        var text = Leading().Replace(name ?? "", "").Trim();
        text = Bracketed().Replace(text, " ").Trim();
        var m = Creditless().Match(text);
        // A bare « OP » or « ED » with neither NC, a number nor « Creditless » is a word of a title (« Ed »), not a clip.
        if (!m.Success || m.Groups["p"].Length == 0 && m.Groups["n"].Length == 0 && m.Groups["k"].Length <= 2) return new Reading(null, null, null, null);
        var kind = m.Groups["k"].Value.ToUpperInvariant() is "OP" or "OPENING" ? "opening" : "ending";
        int? number = m.Groups["n"].Length > 0 ? int.Parse(m.Groups["n"].Value, Culture) : null;
        int? copy = m.Groups["c"].Length > 0 ? int.Parse(m.Groups["c"].Value, Culture) : null;
        var from = text[..m.Index].Trim().TrimEnd('-', '_', '–', ' ').Trim();
        return new Reading(kind, number, from.Length == 0 || Same(from, show) ? null : from, copy);
    }

    /// <summary>A bonus of one of Jellyfin's own kinds (a featurette, a trailer) is never read as an opening or an ending.</summary>
    public static Reading Read(MediaItem extra, string? show) => Kinds.Contains(extra.ExtraType) ? new Reading(null, null, null, null) : Read(extra.Name, show);

    private static bool Same(string a, string? b) => b is not null && Key(a) == Key(b);
    private static string Key(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A tile's title: « Opening 3 », « Ending », « Opening 3 · 2 » for a second copy; else Jellyfin's name.</summary>
    public static string Title(MediaItem extra, string? show)
    {
        var read = Read(extra, show);
        if (read.Kind is null) return extra.Name;
        var title = (read.Kind == "opening" ? "Opening" : "Ending") + (read.Number is { } n ? " " + n.ToString(Culture) : "");
        return read.Copy is { } copy ? $"{title} · {copy.ToString(Culture)}" : title;
    }

    /// <summary>Under a tile's title: « Bakemonogatari · 1 min 30 », the title an opening belongs to (none for the series' own), its length.</summary>
    public static string Meta(MediaItem extra, string? show)
    {
        var read = Read(extra, show);
        var first = read.Kind is not null ? read.From : null;
        return string.Join(" · ", new[] { first, Length(extra) }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>A short clip's length to the second (« 1 min 30 »), as openings are; past ten minutes, the usual « 24 min ».</summary>
    public static string Length(MediaItem extra)
    {
        if (extra.RunTimeTicks is not > 0) return "";
        var span = TimeSpan.FromTicks(extra.RunTimeTicks.Value);
        if (span.TotalMinutes >= 10) return extra.DurationLabel;
        var seconds = (int)Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero);
        return seconds < 60 ? $"{seconds} s" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60:00}";
    }

    /// <summary>Over the bonus: « Bonus · 48 · 1 h 12 », their count and their whole length.</summary>
    public static string Heading(IReadOnlyList<MediaItem> extras)
    {
        var ticks = extras.Sum(x => x.RunTimeTicks is > 0 ? x.RunTimeTicks.Value : 0);
        return string.Join("  ·  ", new[] { Name, extras.Count.ToString(Culture), ticks > 0 ? MediaItem.FormatDuration(TimeSpan.FromTicks(ticks)) : null }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>How many tiles a group shows: one row, all of them once opened.</summary>
    public static int Shown(int count, int columns, bool open) => open ? count : Math.Min(count, columns);

    /// <summary>« Tout afficher » under a group longer than a row, « Afficher moins » once opened.</summary>
    public static string Toggle(bool open) => open ? L("Afficher moins", "Show less") : L("Tout afficher", "Show all");

    /// <summary>The pill shows only for a series with bonus videos; with them, the pills show for a single season too.</summary>
    public static bool Pills(IReadOnlyList<EpisodePage> pages, int extras) => extras > 0 ? pages.Count >= 1 : EpisodeTiles.Pills(pages);

    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)+")]
    private static partial Regex Leading();
    [GeneratedRegex(@"\s*[\[(][^\])]*[\])]\s*")]
    private static partial Regex Bracketed();
    [GeneratedRegex(@"(?<![a-z0-9])(?<p>NC\s*|(?:Creditless|Clean|Textless|Non[\s-]?credit)\s+)?(?<k>Opening|Ending|OP|ED)\s*(?<n>\d{1,2})?(?:\.(?<c>\d{1,2}))?(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Creditless();
}
