using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mira.Core.TorLink;

namespace Mira.Core;

/// <summary>
/// Titles Jellyfin recognized badly, for an administrator (web, Windows, Mac; cases shared with Mira web's
/// identify.js in tests/fixtures/identify.json): « Titres à corriger » lists the films and series it matched to
/// nothing (no TMDb, IMDb or TVDb id), without a poster or a year, or twice; « Identifier… » on a title's page asks
/// Jellyfin for what its metadata sources propose (Jellyfin speaks to TMDb, as it already does; Mira never does) and
/// applies the one chosen.
/// </summary>
public static partial class TitleIdentity
{
    /// <summary>What is wrong with a title, in the order the list says it.</summary>
    public enum Problem { Unidentified, Duplicate, NoPoster, NoYear, NoOverview }

    /// <summary>A title to fix, what is wrong with it, and for a duplicate, the other one.</summary>
    public sealed record Entry(MediaItem Item, IReadOnlyList<Problem> Problems, MediaItem? Twin);

    /// <summary>A search of « Identifier… »: a name and a year, or the id of a link to TMDb or IMDb pasted instead.</summary>
    public sealed record Query(string Name, int? Year, IReadOnlyDictionary<string, string> ProviderIds);

    /// <summary>The fields Jellyfin is asked for to judge a title (<see cref="JellyfinClient.TitlesToCheckAsync"/>).</summary>
    public const string Fields = "ProviderIds,Overview,ProductionYear";

    /// <summary>Whether Jellyfin matched the title to anything: one id of a metadata source, whichever.</summary>
    public static bool Identified(MediaItem item) => item.ProviderIds?.Values.Any(x => !string.IsNullOrWhiteSpace(x)) == true;

    /// <summary>
    /// The films and series that need a look, those Jellyfin did not recognize first, then the duplicates, then the rest;
    /// by name within each. A film without a poster of its own has one all the same: Jellyfin takes a frame of the
    /// video, so only « Non reconnu » says it.
    /// </summary>
    public static List<Entry> Check(IEnumerable<MediaItem> titles)
    {
        var list = titles.Where(x => x.Type is "Movie" or "Series").ToList();
        var entries = new List<Entry>();
        foreach (var item in list)
        {
            var problems = new List<Problem>();
            var identified = Identified(item);
            if (!identified) problems.Add(Problem.Unidentified);
            var twin = list.FirstOrDefault(x => x.Id != item.Id && Same(item, x));
            if (twin is not null) problems.Add(Problem.Duplicate);
            if (!item.ImageTags.ContainsKey("Primary")) problems.Add(Problem.NoPoster);
            if (item.ProductionYear is null) problems.Add(Problem.NoYear);
            // Without an id, the summary missing is the same story.
            if (identified && string.IsNullOrWhiteSpace(item.Overview)) problems.Add(Problem.NoOverview);
            if (problems.Count > 0) entries.Add(new(item, problems, twin));
        }
        return [.. entries.OrderBy(x => x.Problems[0]).ThenBy(x => x.Item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Item.Id, StringComparer.Ordinal)];
    }

    /// <summary>Two titles of one kind that are the same: an id of a metadata source in common, or the same name and year.</summary>
    private static bool Same(MediaItem a, MediaItem b)
    {
        if (a.Type != b.Type) return false;
        if (a.ProviderIds is { } ids && b.ProviderIds is { } others
            && ids.Any(x => !string.IsNullOrWhiteSpace(x.Value) && others.Any(y => string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Value.Trim(), y.Value?.Trim(), StringComparison.OrdinalIgnoreCase))))
            return true;
        return NameKey(a.Name) is { Length: > 0 } key && key == NameKey(b.Name) && (a.ProductionYear ?? FolderYear(a.Name)) == (b.ProductionYear ?? FolderYear(b.Name));
    }

    // Jellyfin names a film without metadata after its folder: « Dune (2021) ».
    [GeneratedRegex(@"\s*\(((?:18|19|20)\d\d)\)\s*$")]
    private static partial Regex YearSuffix();
    private static string NameKey(string? name) => ReleaseRanking.TitleKey(YearSuffix().Replace(name ?? "", ""));
    private static int? FolderYear(string? name) => YearSuffix().Match(name ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    public static string Text(Problem problem, MediaItem? twin = null) => problem switch
    {
        Problem.Unidentified => L("Non reconnu", "Not recognized"),
        Problem.Duplicate => twin is null ? L("En double", "Duplicate") : L($"En double avec « {Named(twin)} »", $"Duplicate of “{Named(twin)}”"),
        Problem.NoPoster => L("Sans affiche", "No poster"),
        Problem.NoYear => L("Sans année", "No year"),
        _ => L("Sans résumé", "No summary"),
    };
    /// <summary>« Non reconnu · Sans affiche ».</summary>
    public static string Text(Entry entry) => string.Join(" · ", entry.Problems.Select(x => Text(x, entry.Twin)));
    /// <summary>« Dune (2021) », the year added unless the name already ends with it.</summary>
    public static string Named(MediaItem item) => item.ProductionYear is { } year && FolderYear(item.Name) is null ? $"{item.Name} ({year})" : item.Name;

    [GeneratedRegex(@"\b(tt\d{6,10})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbId();
    [GeneratedRegex(@"themoviedb\.org/(?:movie|tv)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbLink();

    /// <summary>
    /// What « Chercher » sends: a link to TMDb (« themoviedb.org/movie/438631-dune ») or an IMDb id (« tt1160419 », or
    /// its page) finds that title and nothing else; otherwise the name, with the year of the year box, else the one
    /// the name ends with (« Dune (2021) »).
    /// </summary>
    public static Query Search(string? text, string? year)
    {
        var typed = (text ?? "").Trim();
        if (TmdbLink().Match(typed) is { Success: true } tmdb) return new("", null, new Dictionary<string, string> { ["Tmdb"] = tmdb.Groups[1].Value });
        if (ImdbId().Match(typed) is { Success: true } imdb) return new("", null, new Dictionary<string, string> { ["Imdb"] = imdb.Groups[1].Value.ToLowerInvariant() });
        var chosen = int.TryParse((year ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 1880 and <= 2100 ? y : FolderYear(typed);
        return new(YearSuffix().Replace(typed, "").Trim(), chosen, new Dictionary<string, string>());
    }

    [GeneratedRegex(@"^[^\s]+[._][^\s]+$")]
    private static partial Regex Dotted();
    /// <summary>
    /// The search « Identifier… » opens with: the title's name without the year of its folder, and that year; a name
    /// left as its file had it (« Dune.Part.Two.2024.MULTi.1080p ») read as a release name is.
    /// </summary>
    public static (string Name, int? Year) Prefill(MediaItem item)
    {
        var name = item.Name.Trim();
        if (Dotted().IsMatch(name) && ReleaseRanking.Parse(name) is { Title.Length: > 0 } release) return (release.Title, release.Year ?? item.ProductionYear);
        return (YearSuffix().Replace(name, "").Trim(), item.ProductionYear ?? FolderYear(name));
    }

    /// <summary>The address of a proposal's poster, fetched by Mira's extension for an administrator (never by the device from TMDb).</summary>
    public static string ImagePath(string url) => "Mira/identify/image?url=" + Uri.EscapeDataString(url);

    /// <summary>« 2021 · TheMovieDb » under a proposal's name.</summary>
    public static string Line(RemoteMatch match) => string.Join(" · ", new[] { match.Year?.ToString(CultureInfo.InvariantCulture), match.Provider }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// What a metadata source of Jellyfin proposes for a title (its RemoteSearchResult): shown by name, year, poster and
/// summary, and sent back to Jellyfin as it came (<see cref="Raw"/>) when chosen.
/// </summary>
public sealed record RemoteMatch(string Name, int? Year, string? Overview, string? ImageUrl, string? Provider, JsonObject Raw)
{
    public static RemoteMatch From(JsonObject raw)
    {
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        var year = raw["ProductionYear"] is JsonValue y && y.TryGetValue<int>(out var number) ? number : (int?)null;
        if (year is null && Text(raw["PremiereDate"]) is { Length: >= 4 } date && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var fromDate)) year = fromDate;
        return new(Text(raw["Name"]) ?? "", year, Text(raw["Overview"]), Text(raw["ImageUrl"]), Text(raw["SearchProviderName"]), raw);
    }
}
