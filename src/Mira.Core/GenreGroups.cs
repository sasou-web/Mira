using System.Globalization;
using System.Text;
using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>One genre as shown: its label in Mira's language, every name Jellyfin gives it (« Comédie|Comedy », the value of
/// <see cref="CatalogQuery.Genre"/>: Jellyfin reads <c>genres=</c> split on « | »), and how many titles of the page have it.</summary>
public sealed record GenreGroup(string Label, string Value, int Count);

/// <summary>
/// Jellyfin keeps a genre under each name its metadata gave it: a library fed in French and in English shows « Comédie »
/// beside « Comedy ». The names of one genre become one chip, most frequent first.
/// </summary>
public static class GenreGroups
{
    // French and English names of the genres TMDb, TVDb and AniDB give, by a key without case, accents, spaces or « & ».
    private static readonly (string French, string English)[] Pairs =
    [
        ("Action", "Action"), ("Aventure", "Adventure"), ("Action & Aventure", "Action & Adventure"), ("Animation", "Animation"),
        ("Comédie", "Comedy"), ("Crime", "Crime"), ("Documentaire", "Documentary"), ("Drame", "Drama"), ("Familial", "Family"),
        ("Fantastique", "Fantasy"), ("Histoire", "History"), ("Horreur", "Horror"), ("Musique", "Music"), ("Mystère", "Mystery"),
        ("Romance", "Romance"), ("Science-Fiction", "Science Fiction"), ("Science-Fiction & Fantastique", "Sci-Fi & Fantasy"),
        ("Téléfilm", "TV Movie"), ("Thriller", "Thriller"), ("Guerre", "War"), ("Guerre & Politique", "War & Politics"),
        ("Western", "Western"), ("Enfants", "Kids"), ("Feuilleton", "Soap"), ("Téléréalité", "Reality"), ("Talk-show", "Talk"),
        ("Actualités", "News"), ("Biographie", "Biography"), ("Sport", "Sport"), ("Comédie musicale", "Musical"),
    ];
    private static readonly Dictionary<string, int> PairOf = Pairs.SelectMany((x, i) => new[] { (Key(x.French), i), (Key(x.English), i) })
        .GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.First().i);
    /// <summary>The same genre whatever its spelling: « Sci-Fi », « science fiction » and « Science-Fiction » meet.</summary>
    public static string Key(string genre)
    {
        var text = new StringBuilder();
        var words = (" " + genre.ToLowerInvariant().Replace("&", " ") + " ").Replace(" and ", " ").Replace(" et ", " ");
        foreach (var c in words.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) text.Append(char.ToLowerInvariant(c));
        var key = text.ToString();
        return key switch { "scifi" => "sciencefiction", _ => key };
    }
    private static string Identity(string genre) => PairOf.TryGetValue(Key(genre), out var pair) ? "#" + pair : Key(genre);
    /// <summary>The genres of these titles (each title's list), grouped, most frequent first, then by label.</summary>
    public static IReadOnlyList<GenreGroup> FromTitles(IEnumerable<IReadOnlyList<string>> titles)
    {
        var names = new Dictionary<string, HashSet<string>>(); var counts = new Dictionary<string, int>();
        foreach (var genres in titles)
            foreach (var id in genres.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => { var id = Identity(x); (names.TryGetValue(id, out var set) ? set : names[id] = new(StringComparer.Ordinal)).Add(x.Trim()); return id; }).Distinct())
                counts[id] = counts.GetValueOrDefault(id) + 1;
        return names.Select(x => Group(x.Key, x.Value, counts.GetValueOrDefault(x.Key)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    /// <summary>A list of genre names (Jellyfin's filters), grouped, by label.</summary>
    public static IReadOnlyList<GenreGroup> FromNames(IEnumerable<string> genres) =>
        FromTitles(genres.Select(x => (IReadOnlyList<string>)[x])).OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    /// <summary>The group of one genre among those known (a genre clicked on a title page): all its names, or itself alone.</summary>
    public static GenreGroup Of(string genre, IEnumerable<string> known)
    {
        var id = Identity(genre);
        return Group(id, known.Append(genre).Where(x => Identity(x) == id).Select(x => x.Trim()).ToHashSet(StringComparer.Ordinal), 0);
    }
    private static GenreGroup Group(string id, IEnumerable<string> names, int count)
    {
        var all = names.Order(StringComparer.Ordinal).ToList();
        var label = id.StartsWith('#') && int.TryParse(id[1..], out var pair) ? L(Pairs[pair].French, Pairs[pair].English) : all[0];
        return new(label, string.Join('|', all), count);
    }
    /// <summary>A title has the genre of this value (a single name, or the names of a group joined by « | »).</summary>
    public static bool Has(IReadOnlyList<string> genres, string value) => value.Split('|').Any(genres.Contains);
}
