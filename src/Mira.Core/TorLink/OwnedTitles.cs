using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>What the library already has of a title found: <paramref name="Owned"/> this very film, episode or season; else the series only.</summary>
public sealed record OwnedLine(string Text, string ItemId, bool Owned);

/// <summary>
/// « Télécharger sur le PC serveur »: whether a title found is already in the library, so that it is not downloaded twice,
/// as Mira web says it (src/Mira.Web/js/owned.js; cases shared in tests/fixtures/owned.json). Jellyfin is asked twice at
/// most per title (<see cref="Searches"/>), never anything else.
/// </summary>
public static partial class OwnedTitles
{
    /// <summary>Titles looked for at most, the first of a search.</summary>
    public const int MaxLookups = 10;

    // Jellyfin names a film without metadata after its folder: « Dune (2021) ».
    [GeneratedRegex(@"\s*\((?:19|20)\d\d\)\s*$")] private static partial Regex Year();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Words();
    private static string NameKey(string? name) => ReleaseRanking.TitleKey(Year().Replace(name ?? "", ""));
    private static bool FilmLike(ReleaseGroup group) => group.Release.Episode is null && group.Release.Half is null && !group.Release.Pack;

    /// <summary>A film and an episode of the same name are looked for apart: one among the films, the other among the series.</summary>
    public static string LookupKey(ReleaseGroup group) => (FilmLike(group) ? "film|" : "show|") + group.Show;

    /// <summary>The titles of <see cref="ReleaseRanking.Group"/> to look for in Jellyfin, once each and in their order.</summary>
    public static List<(string Key, string Title, bool Film)> Lookups(IEnumerable<ReleaseGroup> groups)
    {
        var found = new List<(string Key, string Title, bool Film)>();
        foreach (var group in groups)
        {
            var key = LookupKey(group);
            if (found.Count < MaxLookups && !found.Any(x => x.Key == key)) found.Add((key, Year().Replace(group.Title, ""), FilmLike(group)));
        }
        return found;
    }

    /// <summary>
    /// What Jellyfin is asked for a title found, in turn (<see cref="FindAsync"/>): the title, then its longest word, a final
    /// « s » left out. Jellyfin's search reads punctuation as a space and finds the words as written (12.1): a release's
    /// name, which drops apostrophes and writes « and » for « &amp; » (« Oceans.Eight », « Schindlers.List »,
    /// « Fast.and.Furious », « Greys.Anatomy »), never finds « Ocean's Eight », « Schindler's List », « Fast &amp; Furious »,
    /// « Grey's Anatomy »; a word of it does (« Ocean », « Schindler », « Furiou », « Anatomy »), and <see cref="Line"/>
    /// then keeps only the same title among what it found.
    /// </summary>
    public static IEnumerable<string> Searches(string title)
    {
        yield return title;
        var word = Words().Matches(title).Select(m => m.Value.Length > 3 && m.Value[^1] is 's' or 'S' ? m.Value[..^1] : m.Value)
            .Where(w => w.Length >= 3 && !w.Equals("and", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Length).FirstOrDefault();
        if (word is not null && !string.Equals(word, title.Trim(), StringComparison.OrdinalIgnoreCase)) yield return word;
    }

    /// <summary>
    /// The films and series Jellyfin has for a lookup of <see cref="Lookups"/>: those <paramref name="search"/> finds for its
    /// title, else (none of them named as one of <paramref name="groups"/>) those it finds for its longest word.
    /// </summary>
    public static async Task<List<MediaItem>> FindAsync(string title, IReadOnlyCollection<ReleaseGroup> groups, Func<string, Task<List<MediaItem>>> search)
    {
        List<MediaItem> items = [];
        foreach (var text in Searches(title))
        {
            items = await search(text).ConfigureAwait(false);
            if (groups.Any(group => Line(group, items) is not null)) break;
        }
        return items;
    }

    /// <summary>
    /// What the library has of a group, or null. <paramref name="items"/>: the films and series Jellyfin found for its title;
    /// <paramref name="episodes"/>: those of the series found, without missing ones.
    /// </summary>
    public static OwnedLine? Line(ReleaseGroup group, IEnumerable<MediaItem> items, IEnumerable<MediaItem>? episodes = null)
    {
        var keys = new HashSet<string> { group.Show, NameKey(group.Title) };
        if (group.Release.Alternate is { } alternate) keys.Add(ReleaseRanking.TitleKey(alternate));
        bool Named(MediaItem x) => keys.Contains(NameKey(x.Name)) || (x.OriginalTitle is { Length: > 0 } original && keys.Contains(NameKey(original)));
        var release = group.Release;
        if (FilmLike(group))
        {
            var film = items.FirstOrDefault(x => x.Type == "Movie" && Named(x) && !(release.Year is { } y && x.ProductionYear is { } p && Math.Abs(p - y) > 1));
            return film is null ? null : new(L("Déjà dans ta bibliothèque", "Already in your library"), film.Id, true);
        }
        var series = items.FirstOrDefault(x => x.Type == "Series" && Named(x));
        if (series is null) return null;
        var own = (episodes ?? []).Where(x => x.SeriesId is null || x.SeriesId == series.Id).ToList();
        if (release.Pack)
        {
            // A season (or a range of it), or every season: what of it is already there.
            var n = own.Count(x => (release.Season is null || x.ParentIndexNumber == release.Season)
                && (release.Episode is not { } first || release.LastEpisode is not { } last || (x.IndexNumber >= first && x.IndexNumber <= last)));
            if (n == 0) return new(release.Season is null ? L("Série dans ta bibliothèque", "Show in your library") : L("Série dans ta bibliothèque, pas cette saison", "Show in your library, not this season"), series.Id, false);
            var count = Count(n, "épisode", "épisodes", "episode", "episodes");
            return new(release.Season is { } season ? L($"Tu as déjà {count} de la saison {season}", $"You already have {count} of season {season}")
                : L($"Tu as déjà {count} de cette série", $"You already have {count} of this show"), series.Id, true);
        }
        // Numbered without a season (an anime): Jellyfin numbers it by season, the number says nothing of it there.
        if (release.Absolute || release.Episode is not { } from) return new(L("Série dans ta bibliothèque", "Show in your library"), series.Id, false);
        bool Has(int number) => own.Any(x => (release.Season is null || x.ParentIndexNumber == release.Season)
            && (x.IndexNumber == number || (x.IndexNumberEnd is { } end && x.IndexNumber <= number && number <= end)));
        return Enumerable.Range(from, Math.Max(1, (release.LastEpisode ?? from) - from + 1)).All(Has)
            ? new(L($"Tu as déjà {ReleaseRanking.EpisodeLabel(release)}", $"You already have {ReleaseRanking.EpisodeLabel(release)}"), series.Id, true)
            : new(L("Série dans ta bibliothèque, pas cet épisode", "Show in your library, not this episode"), series.Id, false);
    }
}
