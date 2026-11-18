using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Core;

/// <summary>
/// « L'étagère » (ogsas, maquette A, 2026-11-16, /mnt/project-files/collections-maquettes/): a Jellyfin collection grouped
/// in the library, one card for a franchise (Monogatari: 14 shows and 3 movies) in Movies, Shows and Your library, in
/// place of its titles; a click opens its page, its titles numbered in their watch order. Home and the search always show
/// the titles themselves. Nothing is read but Jellyfin: its collections (sagas from TMDb, those made by hand), their
/// titles, and this account's choices (<see cref="Preference"/>).
/// </summary>
public static class CollectionShelf
{
    /// <summary>
    /// Where each collection's choices are kept: a custom preference of Mira's display preferences of the account (beside
    /// « mira.choices »), so they follow the account to its other devices. Jellyfin keeps no order of its own for a
    /// collection's titles (Items?parentId= sorts them, whatever DisplayOrder says: checked on 12.1).
    /// </summary>
    public const string Preference = "mira.collections";
    /// <summary>Titles a collection needs in a page for its card to take their place: one title shows as itself.</summary>
    public const int Least = 2;

    /// <summary>What this account chose for one collection: grouped or not (null: <see cref="Automatic"/>), and its watch order.</summary>
    public sealed record Choice(bool? Grouped = null, IReadOnlyList<string>? Order = null)
    {
        public bool Empty => Grouped is null && (Order is null || Order.Count == 0);
    }

    /// <summary>
    /// A collection Jellyfin made by itself (a saga of TMDb: its ids) is grouped unless this account said no; one made by
    /// hand (« Soirées ») stays a list, its titles in the grids, unless it said yes.
    /// </summary>
    public static bool Automatic(MediaItem box) => box.ProviderIds?.Values.Any(x => !string.IsNullOrWhiteSpace(x)) == true;
    public static bool Grouped(MediaItem box, Choice? choice) => choice?.Grouped ?? Automatic(box);

    /// <summary>The choices kept on Jellyfin (the preference's text); anything unreadable counts as nothing.</summary>
    public static Dictionary<string, Choice> Read(JsonNode? node)
    {
        var read = new Dictionary<string, Choice>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            try { node = JsonNode.Parse(text.GetValue<string>()); } catch (JsonException) { return read; }
        if (node is not JsonObject all) return read;
        foreach (var (id, value) in all)
        {
            if (value is not JsonObject entry || id.Length == 0) continue;
            bool? grouped = entry["grouped"] is JsonValue g && g.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? g.GetValue<bool>() : null;
            var order = entry["order"] is JsonArray list ? list.OfType<JsonValue>().Where(x => x.GetValueKind() == JsonValueKind.String).Select(x => x.GetValue<string>()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null;
            var choice = new Choice(grouped, order is { Count: > 0 } ? order : null);
            if (!choice.Empty) read[id] = choice;
        }
        return read;
    }
    /// <summary>The preference's text for these choices, an empty one left out.</summary>
    public static string Write(IReadOnlyDictionary<string, Choice> choices)
    {
        var all = new JsonObject();
        foreach (var (id, choice) in choices.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (choice.Empty) continue;
            var entry = new JsonObject();
            if (choice.Grouped is { } grouped) entry["grouped"] = grouped;
            if (choice.Order is { Count: > 0 } order) entry["order"] = new JsonArray(order.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            all[id] = entry;
        }
        return all.ToJsonString();
    }

    /// <summary>A collection grouped in the library: its own item (name, pictures) and its titles, films and series, each once.</summary>
    public sealed record Shelf(MediaItem Box, IReadOnlyList<MediaItem> Titles);

    /// <summary>
    /// A collection's titles once each, those of a collection inside it (TMDb's « Kizumonogatari Collection » put in
    /// « Monogatari ») taken in its place: never a pile in a pile. <paramref name="children"/>: each collection's own
    /// children as Jellyfin lists them.
    /// </summary>
    public static List<MediaItem> Flatten(string boxId, Func<string, IReadOnlyList<MediaItem>?> children)
    {
        var titles = new List<MediaItem>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var boxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string id, int depth)
        {
            if (!boxes.Add(id) || depth > 3 || children(id) is not { } list) return;
            foreach (var item in list)
                if (item.Type == "BoxSet") Walk(item.Id, depth + 1);
                else if (TitleCollections.Holds(item) && seen.Add(item.Id)) titles.Add(item);
        }
        Walk(boxId, 0);
        return titles;
    }

    /// <summary>The order the titles came out: year, then day, then name (as Jellyfin sorts a saga).</summary>
    public static List<MediaItem> ByRelease(IEnumerable<MediaItem> titles) =>
        titles.OrderBy(x => x.ProductionYear ?? int.MaxValue).ThenBy(x => x.PremiereDate ?? DateTimeOffset.MaxValue).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    /// <summary>
    /// The watch order: the order chosen (<see cref="Choice.Order"/>) for the titles it names, then those it does not
    /// (added since) in the order they came out. Without one, the order they came out.
    /// </summary>
    public static List<MediaItem> InOrder(IEnumerable<MediaItem> titles, IReadOnlyList<string>? order)
    {
        var released = ByRelease(titles);
        if (order is not { Count: > 0 }) return released;
        var place = order.Select((id, i) => (id, i)).GroupBy(x => x.id, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First().i, StringComparer.OrdinalIgnoreCase);
        return released.Select((item, i) => (item, i)).OrderBy(x => place.TryGetValue(x.item.Id, out var at) ? at : order.Count + x.i).Select(x => x.item).ToList();
    }
    /// <summary>One title moved up or down the list (« Change the order… »): the ids in their new order.</summary>
    public static List<string> Move(IReadOnlyList<MediaItem> titles, int index, int step)
    {
        var ids = titles.Select(x => x.Id).ToList();
        var to = index + step;
        if (index < 0 || index >= ids.Count || to < 0 || to >= ids.Count) return ids;
        (ids[index], ids[to]) = (ids[to], ids[index]);
        return ids;
    }

    /// <summary>Which collection each title is grouped under: a title in two takes the one with more titles (Monogatari over its film saga).</summary>
    public sealed class Index
    {
        private readonly Dictionary<string, Shelf> _byTitle = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<Shelf> Shelves { get; }
        public Index(IEnumerable<Shelf> shelves)
        {
            Shelves = shelves.Where(x => x.Titles.Count >= Least).OrderByDescending(x => x.Titles.Count).ThenBy(x => x.Box.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var shelf in Shelves) foreach (var title in shelf.Titles) _byTitle.TryAdd(title.Id, shelf);
        }
        public static readonly Index None = new([]);
        public Shelf? Of(string titleId) => _byTitle.GetValueOrDefault(titleId);
        /// <summary>The titles of <paramref name="shelf"/> a page of <paramref name="types"/> holds (films on Movies…) that are grouped under it.</summary>
        public int CountIn(Shelf shelf, string types) => shelf.Titles.Count(x => Holds(types, x) && ReferenceEquals(Of(x.Id), shelf));
    }
    private static bool Holds(string types, MediaItem item) => types.Split(',').Contains(item.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A page of the grid as the library shows it: the first title of a grouped collection met (in the page's order:
    /// the newest for « Recently added ») becomes the collection's card, its other titles leave the grid; only when the
    /// collection has <see cref="Least"/> titles of this page's kind (a saga of one film and twelve series shows its film on
    /// Movies). <paramref name="placed"/>: the collections already given a card by an earlier page of the same list.
    /// A filter keeps the card when one of its titles matches it: Jellyfin only sends those.
    /// </summary>
    public static List<MediaItem> Collapse(IEnumerable<MediaItem> page, Index index, string types, ISet<string> placed)
    {
        var shown = new List<MediaItem>();
        foreach (var item in page)
        {
            if (index.Of(item.Id) is not { } shelf || index.CountIn(shelf, types) < Least) { shown.Add(item); continue; }
            if (placed.Add(shelf.Box.Id)) shown.Add(Card(shelf));
        }
        return shown;
    }
    /// <summary>The card of a grouped collection: its own item, with its titles (its count, progress and cover).</summary>
    public static MediaItem Card(Shelf shelf)
    {
        var watched = shelf.Titles.Count(Watched);
        return shelf.Box with
        {
            Type = "BoxSet",
            ChildCount = shelf.Titles.Count,
            Shelf = shelf.Titles,
            UserData = shelf.Box.UserData with { Played = watched == shelf.Titles.Count, PlayedPercentage = Share(shelf.Titles) * 100 },
        };
    }

    /// <summary>A film or series watched to its end.</summary>
    public static bool Watched(MediaItem title) => title.UserData.Played;
    /// <summary>How far a title is, from 0 to 1: a film by its position, a series by its episodes watched.</summary>
    public static double Progress(MediaItem title) =>
        Watched(title) ? 1 : title.Type == "Series" ? Math.Clamp((title.UserData.PlayedPercentage ?? 0) / 100, 0, 1) : title.Progress;
    /// <summary>How far the whole collection is, each title one notch of the bar.</summary>
    public static double Share(IReadOnlyCollection<MediaItem> titles) => titles.Count == 0 ? 0 : titles.Sum(Progress) / titles.Count;
    /// <summary>
    /// « UP NEXT »: the first title of the order not watched to its end, where « Resume » takes the collection up. None
    /// when all are watched (« Watch again » then starts from the first).
    /// </summary>
    public static int? UpNext(IReadOnlyList<MediaItem> ordered)
    {
        for (var i = 0; i < ordered.Count; i++) if (!Watched(ordered[i])) return i;
        return null;
    }

    /// <summary>« Collection · 17 titles » under the card.</summary>
    public static string CardLine(int titles) => L("Collection", "Collection") + "   ·   " + Count(titles, "titre", "titres", "title", "titles");
    /// <summary>« 2009 – 2019 · 14 shows · 3 movies · 100 episodes ».</summary>
    public static string Facts(IReadOnlyCollection<MediaItem> titles)
    {
        var years = titles.Select(x => x.ProductionYear).OfType<int>().ToList();
        var shows = titles.Count(x => x.Type == "Series"); var movies = titles.Count(x => x.Type == "Movie");
        var episodes = titles.Where(x => x.Type == "Series").Sum(x => x.RecursiveItemCount ?? 0);
        return string.Join("  ·  ", new[]
        {
            years.Count == 0 ? null : years.Min() == years.Max() ? years.Min().ToString(Culture) : $"{years.Min().ToString(Culture)} – {years.Max().ToString(Culture)}",
            shows > 0 ? Count(shows, "série", "séries", "show", "shows") : null,
            movies > 0 ? Count(movies, "film", "films", "movie", "movies") : null,
            episodes > 0 ? Count(episodes, "épisode", "épisodes", "episode", "episodes") : null,
        }.Where(x => x is not null));
    }
    /// <summary>« 01 », the number of a title in the order.</summary>
    public static string Number(int index) => (index + 1).ToString("00", Culture);
    /// <summary>« 2016 · 1 h 04 », « 2009 · 15 episodes » under a numbered poster.</summary>
    public static string TitleLine(MediaItem title) => string.Join("  ·  ", new[]
    {
        title.ProductionYear?.ToString(Culture),
        title.Type == "Series" ? title.RecursiveItemCount is > 0 and var n ? Count(n, "épisode", "épisodes", "episode", "episodes") : L("Série", "Show") : title.DurationLabel,
    }.Where(x => !string.IsNullOrEmpty(x)));
    /// <summary>« Titles · 17 ».</summary>
    public static string TitlesHeading(int count) => L("Titres", "Titles") + "  ·  " + count.ToString(Culture);
    /// <summary>« 25 titles · 1 collection » at the top of a grid holding cards of collections.</summary>
    public static string GridCount(int titles, int collections) => Catalog.CountText(titles) + (collections > 0 ? "  ·  " + Count(collections, "collection", "collections", "collection", "collections") : "");
    /// <summary>« Grouped 17 titles as “Monogatari”. », said with « Undo » when a collection takes its titles' place.</summary>
    public static string GroupedNotice(int titles, string name) => L($"{titles} titres regroupés dans « {name} ».", $"Grouped {titles} titles as “{name}”.");
    /// <summary>The first time sagas are grouped on their own: how many, said once with « Undo ».</summary>
    public static string AutomaticNotice(int titles, int collections) => collections == 1
        ? L($"{titles} titres regroupés dans une collection.", $"Grouped {titles} titles into a collection.")
        : L($"{titles} titres regroupés dans {collections} collections.", $"Grouped {titles} titles into {collections} collections.");
}
