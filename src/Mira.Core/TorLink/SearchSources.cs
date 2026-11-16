using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>
/// The sites Mira asks beside TorLink's, on the PC of Jellyfin (« Plus de sources de recherche » of TorLink's settings, on
/// by default; Privacy, SECURITY.md). Seen from a French box on 2026-11-14: TorLink reached only The Pirate Bay, YTS, Nyaa
/// and SubsPlease (EZTV, 1337x and the others refused by its DNS), and Nyaa's feed gave it the 75 newest uploads only.
/// Knaben gathers The Pirate Bay, Nyaa, YTS, 1337x and TokyoTosho, the most shared first; AnimeTosho, anime (Nyaa,
/// TokyoTosho, AniDex); Torrents-CSV, an open index of what public trackers share; AniList, the names an anime is released
/// under. Each gets the search and nothing else.
/// </summary>
public static partial class SearchSources
{
    public const string Knaben = "knaben", AnimeTosho = "animetosho", TorrentsCsv = "torrents-csv";
    /// <summary>Knaben's page; asked twice for the broadest form when the first is full.</summary>
    public const int KnabenPage = 100;

    /// <summary>The sites of a category: Knaben always, AnimeTosho for anime, Torrents-CSV for films and shows.</summary>
    public static IReadOnlyList<string> For(string category) => category == "anime" ? [Knaben, AnimeTosho] : [Knaben, TorrentsCsv];

    /// <summary>
    /// What a search is asked as: typed, then its broadest form when it differs (<see cref="Broad"/>). Both: the
    /// typed one finds releases named with the apostrophe or the accent (Knaben: « Marvel's Agents of S.H.I.E.L.D. »
    /// 90, « Marvels… » 205, not all the same).
    /// </summary>
    public static IReadOnlyList<string> Forms(string query) => Broad(query) is { } broad && broad != query ? [query, broad] : [query];

    /// <summary>
    /// A search for a whole season (« Arcane S02 ») as many packs are named, in words: « Arcane Season 2 » (« Arcane Season 2
    /// (2024) [1080p] »); null for any other search. Asked of Knaben, whose titles must hold every word.
    /// </summary>
    public static string? SeasonWords(string? query) =>
        query is not null && SeasonAtEnd().Match(query) is { Success: true } match && RemoteDownloads.CleanQuery(query[..match.Index]) is { } title
            ? string.Create(CultureInfo.InvariantCulture, $"{title} Season {int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)}") : null;

    /// <summary>
    /// The title a search names and what follows it (« Frieren S02 »: « Frieren » and « S02 »), a year left out (releases of
    /// an anime seldom say it); null when it names no title (« S02 », « 1080p », a single letter).
    /// </summary>
    public static (string Title, string After)? TitleAndRest(string query)
    {
        var title = ReleaseRanking.Parse(query).Title?.Trim();
        if (string.IsNullOrEmpty(title) || ReleaseRanking.TitleKey(title).Length < 2 || Marker().IsMatch(title)) return null;
        var at = query.IndexOf(title, StringComparison.OrdinalIgnoreCase);
        var rest = at >= 0 ? YearWord().Replace(query[(at + title.Length)..], " ") : "";
        return (title, RemoteDownloads.CleanQuery(rest) ?? "");
    }

    /// <summary>
    /// The form every site finds the most for, measured on 2026-11-14: without accents on Latin letters (The Pirate Bay:
    /// « Shōgun » 52 results, « Shogun » 100 and the whole season; Knaben 18 and 694), without apostrophes (« JoJo's »
    /// none on The Pirate Bay, « JoJos » 100), hyphens, colons and the like as spaces (Knaben: « Spider-Man: Across the
    /// Spider-Verse » 36, « Spider Man Across the Spider Verse » 84). Dots stay (« S.H.I.E.L.D. »: as many as without), so
    /// do other scripts (« ジョジョ » keeps its marks). An elided article or pronoun is a word of its own, as releases write
    /// it (« L'Attaque des Titans », « L.Attaque.des.Titans »: « L Attaque des Titans », never « LAttaque »). Null when
    /// nothing is left.
    /// </summary>
    public static string? Broad(string? query)
    {
        if (query is null) return null;
        var text = new StringBuilder(query.Length);
        var latin = false;
        foreach (var c in Elision().Replace(query, " ").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) { if (!latin) text.Append(c); continue; }
            latin = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 'À' and <= 'ɏ') or (>= 'Ḁ' and <= 'ỿ');
            if (Apostrophes.Contains(c)) continue;
            text.Append(Gaps.Contains(c) ? ' ' : c);
        }
        return RemoteDownloads.CleanQuery(text.ToString().Normalize(NormalizationForm.FormC));
    }
    private const string Apostrophes = "'’‘ʼ`´", Gaps = ":;,!?&/\\|+_()[]{}<>\"“”«»‐‑‒–—―-~*#";

    /// <summary>
    /// Whether a name holds every word of the search (accents, apostrophes and punctuation aside, « and » and « the »
    /// not needed; a word may begin one of the name's, « S01 » in « S01E05 »): for the sites that answer with any of
    /// the words (Torrents-CSV, AnimeTosho).
    /// </summary>
    public static bool Holds(string name, string query)
    {
        var have = Words(name);
        return Words(query).Where(x => x is not ("and" or "the" or "et")).All(word => have.Any(x => x.StartsWith(word, StringComparison.Ordinal)));
    }
    private static List<string> Words(string text) =>
        [.. NotWord().Split((Broad(text) ?? "").ToLowerInvariant().Replace(".", " ", StringComparison.Ordinal)).Where(x => x.Length > 0)];

    /// <summary>
    /// A name that is no film nor episode, found by an index of everything (Torrents-CSV): a game, a program, music. A video
    /// release says at least one of its year, season, episode, resolution, source or codec; a game repack never.
    /// </summary>
    public static bool LooksLikeVideo(string name)
    {
        if (Games().IsMatch(name)) return false;
        var release = ReleaseRanking.Parse(name);
        return release.Year is not null || release.Season is not null || release.Episode is not null || release.Half is not null || release.Pack
            || release.Resolution is not null || release.Source is not null || release.Codec is not null;
    }

    // —— Knaben (api.knaben.org/v1: POST, JSON) ——

    /// <summary>
    /// Knaben's request: titles holding every word (« 100% »), the most shared first, a page of <see cref="KnabenPage"/>
    /// from <paramref name="from"/>, only the video categories of <paramref name="category"/> (anime: its subtitled, dubbed,
    /// dual-audio, raw and other-language releases, without its music and books), nothing unsafe nor adult.
    /// </summary>
    public static HttpRequestMessage KnabenRequest(string query, string category, int from = 0)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["search_type"] = "100%", ["search_field"] = "title", ["query"] = query, ["order_by"] = "seeders", ["order_direction"] = "desc",
            ["from"] = from, ["size"] = KnabenPage, ["hide_unsafe"] = true, ["hide_xxx"] = true, ["categories"] = KnabenCategories(category),
        });
        return new HttpRequestMessage(HttpMethod.Post, "https://api.knaben.org/v1") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
    private static int[] KnabenCategories(string category) => category switch
    {
        "movies" => [3000000],
        "anime" => [6001000, 6002000, 6003000, 6004000, 6008000],
        _ => [2000000],
    };

    /// <summary>
    /// Knaben's answer ({hits: [{hash, magnetUrl, title, description, bytes, seeders, peers, date, categoryId, tracker}]}).
    /// A title of The Pirate Bay cut with « ... » is taken whole from its description (« Uploader: x&lt;br&gt;name »);
    /// TokyoTosho's hash is in base32. In films, Nyaa's anime (which Knaben files in films too) are left to the anime search.
    /// </summary>
    public static List<AgentHit> ParseKnaben(string json, string category)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            throw new JsonException("No hits");
        var found = new List<AgentHit>();
        foreach (var hit in hits.EnumerateArray())
        {
            if (hit.ValueKind != JsonValueKind.Object || !Video(Categories(hit), category)) continue;
            var name = KnabenTitle(hit);
            if (name is null || (RemoteDownloads.InfoHash(Html(Text(hit, "magnetUrl"))) ?? HashOf(Text(hit, "hash"))) is not { } id) continue;
            found.Add(new(id, name, Number(hit, "bytes"), Count(hit, "seeders", "liveSeeders"), Count(hit, "peers", "livePeers"), Knaben,
                Magnet(id, name, Html(Text(hit, "magnetUrl"))), Date(Text(hit, "date"))));
        }
        return found;
    }

    private static List<long> Categories(JsonElement hit) =>
        hit.TryGetProperty("categoryId", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out _)).Select(x => x.GetInt64())] : [];

    /// <summary>TV (2…), films (3…) or anime (6…), never music videos, books nor music of anime; films without anime.</summary>
    private static bool Video(List<long> ids, string category) =>
        ids.All(x => x / 1000000 is 2 or 3 or 6 && x is not (6005000 or 6006000 or 6006001 or 6007000))
        && (category != "movies" || !ids.Any(x => x / 1000000 == 6));

    private static string? KnabenTitle(JsonElement hit)
    {
        var title = Html(Text(hit, "title"))?.Trim() ?? "";
        if ((title.EndsWith("...", StringComparison.Ordinal) || title.EndsWith('…')) && Html(Text(hit, "description")) is { } description)
        {
            var cut = title.TrimEnd('.', '…', ' ');
            var at = description.IndexOf("<br>", StringComparison.OrdinalIgnoreCase);
            var whole = at >= 0 ? description[(at + 4)..] : description;
            if (whole.IndexOf('<') is var end and >= 0) whole = whole[..end];
            whole = whole.Trim();
            if (cut.Length > 0 && whole.Length >= cut.Length && whole.StartsWith(cut, StringComparison.OrdinalIgnoreCase)) title = whole;
        }
        return RemoteDownloads.CleanName(title);
    }

    // —— AnimeTosho (feed.animetosho.net/json: GET) ——

    /// <summary>AnimeTosho's feed for a search: 75 releases a page, the newest first.</summary>
    public static HttpRequestMessage AnimeToshoRequest(string query, int page = 1) =>
        new(HttpMethod.Get, $"https://feed.animetosho.net/json?q={Uri.EscapeDataString(query)}" + (page > 1 ? $"&page={page}" : ""));

    /// <summary>AnimeTosho's answer ([{title, info_hash, magnet_uri, total_size, seeders, leechers, timestamp}]), the most shared first.</summary>
    public static List<AgentHit> ParseAnimeTosho(string json, string query)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("No releases");
        var found = new List<AgentHit>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || RemoteDownloads.CleanName(Text(item, "title") ?? Text(item, "torrent_name")) is not { } name || !Holds(name, query)) continue;
            if ((RemoteDownloads.InfoHash(Text(item, "magnet_uri")) ?? HashOf(Text(item, "info_hash"))) is not { } id) continue;
            found.Add(new(id, name, Number(item, "total_size"), Count(item, "seeders"), Count(item, "leechers"), AnimeTosho, Magnet(id, name, Text(item, "magnet_uri")),
                Number(item, "timestamp") is > 0 and var at ? at : null));
        }
        return [.. found.OrderByDescending(x => x.Seeders)];
    }

    // —— Torrents-CSV (torrents-csv.com/service/search: GET) ——

    /// <summary>Torrents-CSV's search: 25 torrents, the most shared first.</summary>
    public static HttpRequestMessage TorrentsCsvRequest(string query) => new(HttpMethod.Get, $"https://torrents-csv.com/service/search?q={Uri.EscapeDataString(query)}");

    /// <summary>Torrents-CSV's answer ({torrents: [{infohash, name, size_bytes, seeders, leechers, created_unix}]}): videos holding the search only.</summary>
    public static List<AgentHit> ParseTorrentsCsv(string json, string query)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("torrents", out var torrents) || torrents.ValueKind != JsonValueKind.Array)
            throw new JsonException("No torrents");
        var found = new List<AgentHit>();
        foreach (var item in torrents.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || RemoteDownloads.CleanName(Text(item, "name")) is not { } name || !Holds(name, query) || !LooksLikeVideo(name)) continue;
            if (HashOf(Text(item, "infohash")) is not { } id) continue;
            found.Add(new(id, name, Number(item, "size_bytes"), Count(item, "seeders"), Count(item, "leechers"), TorrentsCsv, Magnet(id, name, null),
                Number(item, "created_unix") is > 0 and var at ? at : null));
        }
        return found;
    }

    // —— AniList (graphql.anilist.co: POST, GraphQL) ——

    /// <summary>
    /// AniList's request for the animes a title may name, the closest first, never an adult one. Only the names: Romaji
    /// (« Sousou no Frieren »), English (« Frieren: Beyond Journey's End ») and the others it knows, to find the right one.
    /// </summary>
    public static HttpRequestMessage AniListRequest(string title)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["query"] = "query ($search: String) { Page(perPage: 8) { media(search: $search, type: ANIME, isAdult: false, sort: SEARCH_MATCH) { title { romaji english } synonyms } } }",
            ["variables"] = new Dictionary<string, string> { ["search"] = title },
        });
        return new HttpRequestMessage(HttpMethod.Post, "https://graphql.anilist.co") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// The other names of the anime <paramref name="title"/> names, from AniList's answer: the first one that is it (one of its
    /// names is the title, or begins with all its words: « Frieren », « Frieren: Beyond Journey's End »), its Romaji and English
    /// names that are not the title already; none when no anime is it. Releases are named after either (SubsPlease, Erai-raws:
    /// Romaji; ASW and the scene: English).
    /// </summary>
    public static List<string> ParseAniList(string json, string title)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("Page", out var page) || page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            throw new JsonException("No media");
        var key = ReleaseRanking.TitleKey(title);
        var words = Words(title);
        foreach (var item in media.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var titles = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            string?[] main = titles.ValueKind == JsonValueKind.Object ? [Text(titles, "romaji"), Text(titles, "english")] : [];
            var others = item.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array
                ? synonyms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()) : [];
            if (!main.Concat(others).OfType<string>().Any(name => ReleaseRanking.TitleKey(name) == key || words.Count > 0 && Words(name) is var named && named.Count >= words.Count && named.Take(words.Count).SequenceEqual(words)))
                continue;
            return [.. main.Select(RemoteDownloads.CleanName).OfType<string>().Where(x => ReleaseRanking.TitleKey(x) is { Length: > 0 } other && other != key).DistinctBy(ReleaseRanking.TitleKey)];
        }
        return [];
    }

    // —— Together ——

    /// <summary>
    /// The lists of one search gathered, TorLink's first: their best taken in turn (so that each site keeps its most
    /// shared ones), one result per torrent, at most <paramref name="most"/>, the most shared first. A torrent found
    /// by several keeps the most sharers any of them counted, its whole name (not one cut with « … »), and the site of
    /// the first list that had it.
    /// </summary>
    public static List<AgentHit> Merge(IReadOnlyList<IReadOnlyList<AgentHit>> lists, int most)
    {
        var kept = new Dictionary<string, AgentHit>(); var order = new List<string>();
        for (var rank = 0; lists.Any(x => rank < x.Count); rank++)
            foreach (var list in lists)
            {
                if (rank >= list.Count) continue;
                var hit = list[rank];
                if (!kept.TryGetValue(hit.Id, out var known))
                {
                    if (order.Count >= most) continue;
                    kept[hit.Id] = hit; order.Add(hit.Id); continue;
                }
                kept[hit.Id] = known with
                {
                    Name = Cut(known.Name) && !Cut(hit.Name) || known.Name.Length < hit.Name.Length && hit.Name.StartsWith(known.Name.TrimEnd('…', '.', ' '), StringComparison.OrdinalIgnoreCase) ? hit.Name : known.Name,
                    Size = known.Size > 0 ? known.Size : hit.Size,
                    Seeders = Math.Max(known.Seeders, hit.Seeders),
                    Leechers = Math.Max(known.Leechers, hit.Leechers),
                    Added = known.Added ?? hit.Added,
                };
            }
        return [.. order.Select(x => kept[x]).Select((hit, index) => (hit, index)).OrderByDescending(x => x.hit.Seeders).ThenBy(x => x.index).Select(x => x.hit)];
    }
    private static bool Cut(string name) => name.EndsWith('…') || name.EndsWith("...", StringComparison.Ordinal);

    /// <summary>
    /// A magnet link for a result: the site's own when it holds the same torrent and is not too long, otherwise its hash,
    /// its name and the public trackers TorLink adds to its own (the DHT finds the rest).
    /// </summary>
    public static string Magnet(string id, string name, string? given)
    {
        if (given is not null && RemoteDownloads.InfoHash(given) == id && given.Trim().Length <= RemoteDownloads.MaxMagnet) return given.Trim();
        return $"magnet:?xt=urn:btih:{id}&dn={Uri.EscapeDataString(name)}" + string.Concat(Trackers.Select(x => "&tr=" + Uri.EscapeDataString(x)));
    }
    private static readonly string[] Trackers =
    [
        "udp://tracker.opentrackr.org:1337/announce", "udp://open.demonii.com:1337/announce", "udp://tracker.openbittorrent.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce", "udp://exodus.desync.com:6969/announce", "udp://open.stealth.si:80/announce", "udp://tracker.dler.org:6969/announce",
    ];

    /// <summary>A 40-digit hexadecimal or 32-letter base32 info hash in lower-case hexadecimal; null otherwise.</summary>
    private static string? HashOf(string? hash) =>
        hash?.Trim() is { Length: 40 or 32 } text && (text.Length == 40 ? text.All(Uri.IsHexDigit) : text.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '2' and <= '7')))
            ? RemoteDownloads.InfoHash("magnet:?xt=urn:btih:" + text) : null;

    private static string? Html(string? text) => text is null ? null : WebUtility.HtmlDecode(text);
    private static string? Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (value.TryGetInt64(out var whole) ? Math.Max(0, whole) : value.TryGetDouble(out var real) && real > 0 ? (long)Math.Min(real, long.MaxValue) : 0) : 0;
    private static int Count(JsonElement item, string name, string? otherwise = null) =>
        (int)Math.Min(int.MaxValue, item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? Number(item, name) : otherwise is null ? 0 : Number(item, otherwise));
    private static long? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at.Year > 1990 ? at.ToUnixTimeSeconds() : null;

    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NotWord();
    [GeneratedRegex(@"\s[Ss](\d{1,2})\s*$")] private static partial Regex SeasonAtEnd();
    [GeneratedRegex(@"^(?:s\d{1,2}(?:e\d{1,4})?|e\d{1,4}|\d{3,4}[pi]|[48]k)$", RegexOptions.IgnoreCase)] private static partial Regex Marker();
    [GeneratedRegex(@"[(\[]\s*(?:19|20)\d\d\s*[)\]]|(?<![\p{L}\p{N}])(?:19|20)\d\d(?![\p{L}\p{N}])")] private static partial Regex YearWord();
    /// <summary>The apostrophe of « l' », « d' », « j' », « m' », « n' », « s' », « t' », « c' », « qu' » (« jusqu' ») before a word.</summary>
    [GeneratedRegex(@"(?<=(?<![\p{L}\p{M}'’ʼ‘`´])[cdjlmnst]|qu)['’ʼ‘`´](?=\p{L})", RegexOptions.IgnoreCase)] private static partial Regex Elision();
    [GeneratedRegex(@"\b(?:fitgirl|dodi|elamigos|codex|plaza|skidrow|empress|tenoke|razor1911)\b|\[gog\]", RegexOptions.IgnoreCase)] private static partial Regex Games();
}

/// <summary>
/// Mira's sites asked over HTTPS from the PC of Jellyfin, each answer read by <see cref="SearchSources"/>. A site that does not
/// answer in time, or not as expected, is said in its error; the others go on.
/// </summary>
public sealed class SearchSourceClient : IDisposable
{
    private readonly HttpClient _http;
    public SearchSourceClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mira", JellyfinClient.AppVersion));
    }

    /// <summary>
    /// One site's results for a search in a category: Knaben twice for the broadest form when its first page is full,
    /// the others once. An error in words when the site did not answer, or not as expected.
    /// </summary>
    public async Task<(List<AgentHit> Hits, string? Error)> SearchAsync(string source, string query, string category, bool broadest, CancellationToken ct)
    {
        try
        {
            switch (source)
            {
                case SearchSources.Knaben:
                {
                    var first = SearchSources.ParseKnaben(await AskAsync(SearchSources.KnabenRequest(query, category), ct), category);
                    if (!broadest || first.Count < SearchSources.KnabenPage - 10) return (first, null);
                    try { return ([.. first, .. SearchSources.ParseKnaben(await AskAsync(SearchSources.KnabenRequest(query, category, SearchSources.KnabenPage), ct), category)], null); }
                    catch (Exception ex) when (Failed(ex, ct)) { return (first, null); }
                }
                case SearchSources.AnimeTosho: return (SearchSources.ParseAnimeTosho(await AskAsync(SearchSources.AnimeToshoRequest(query), ct), query), null);
                case SearchSources.TorrentsCsv: return (SearchSources.ParseTorrentsCsv(await AskAsync(SearchSources.TorrentsCsvRequest(query), ct), query), null);
                default: return ([], $"{source} ?");
            }
        }
        catch (Exception ex) when (Failed(ex, ct)) { return ([], $"{source}: {(ex is JsonException ? L("réponse illisible", "unreadable answer") : ex is OperationCanceledException ? L("pas de réponse à temps", "no answer in time") : ex.Message)}"); }
    }

    /// <summary>
    /// The other names of an anime (<see cref="SearchSources.ParseAniList"/>), kept a few hours: a search and the one after it
    /// (« Frieren S02 » then « Frieren ») ask AniList once. None when AniList does not answer in a few seconds.
    /// </summary>
    public async Task<List<string>> AnimeNamesAsync(string title, CancellationToken ct)
    {
        var key = ReleaseRanking.TitleKey(title);
        if (_names.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < NamesKept) return known.Names;
        using var soon = CancellationTokenSource.CreateLinkedTokenSource(ct);
        soon.CancelAfter(NamesWait);
        try
        {
            var names = SearchSources.ParseAniList(await AskAsync(SearchSources.AniListRequest(title), soon.Token), title);
            if (_names.Count >= 200) _names.Clear();
            _names[key] = (DateTime.UtcNow, names);
            return names;
        }
        catch (Exception ex) when (Failed(ex, ct)) { return []; }
    }
    private readonly ConcurrentDictionary<string, (DateTime At, List<string> Names)> _names = new();
    private static readonly TimeSpan NamesKept = TimeSpan.FromHours(6), NamesWait = TimeSpan.FromSeconds(8);

    private static bool Failed(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or IOException or DecoderFallbackException || ex is OperationCanceledException && !ct.IsCancellationRequested;

    private async Task<string> AskAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            // A page of results is some hundred kilobytes; beyond a few megabytes it is not one.
            if (response.Content.Headers.ContentLength > MaxAnswer) throw new HttpRequestException(L("réponse trop longue", "answer too long"));
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[16 * 1024]; var text = new StringBuilder(); int read;
            while ((read = await reader.ReadAsync(buffer, ct)) > 0)
            {
                if (text.Length + read > MaxAnswer) throw new HttpRequestException(L("réponse trop longue", "answer too long"));
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }
    }
    private const int MaxAnswer = 4 * 1024 * 1024;

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// One search on the PC of Jellyfin, in one category: TorLink's (« torlnk search », given) and, unless turned off,
/// Mira's sites (<see cref="SearchSources"/>), each in every form of the search (<see cref="SearchSources.Forms"/>), all at
/// once, gathered by <see cref="SearchSources.Merge"/>. An anime is also searched under the names AniList gives it, by
/// TorLink and the sites alike: <c>Names</c>, for the devices to know its releases. An error only when nothing answered.
/// </summary>
public static class TorrentSearch
{
    /// <summary>What one category of a search may return, as the relay keeps it.</summary>
    public const int MaxHits = 150;

    public static async Task<(List<AgentHit> Hits, string? Error, List<string> Names)> RunAsync(Func<string, CancellationToken, Task<(List<AgentHit> Hits, string? Error)>> torlink,
        SearchSourceClient? sources, string query, string category, CancellationToken ct)
    {
        var forms = SearchSources.Forms(query);
        var broadest = forms[^1];
        var asked = forms.Select(form => torlink(form, ct)).ToList();
        var ours = new List<Task<(List<AgentHit> Hits, string? Error)>>();
        if (sources is not null)
            foreach (var site in SearchSources.For(category))
                // Torrents-CSV matches words alone: its broadest form is enough. Knaben, a season's packs in words too.
                foreach (var form in site == SearchSources.TorrentsCsv ? [broadest] : site == SearchSources.Knaben && SearchSources.SeasonWords(broadest) is { } words ? [.. forms, words] : forms)
                    ours.Add(sources.SearchAsync(site, form, category, form == broadest, ct));
        var names = new List<string>();
        if (sources is not null && category == "anime" && SearchSources.TitleAndRest(query) is { } split)
            foreach (var name in await sources.AnimeNamesAsync(split.Title, ct))
            {
                if (SearchSources.Broad($"{name} {split.After}") is not { } form || forms.Contains(form, StringComparer.OrdinalIgnoreCase)) continue;
                names.Add(name);
                asked.Add(torlink(form, ct));
                foreach (var site in SearchSources.For(category)) ours.Add(sources.SearchAsync(site, form, category, false, ct));
            }
        var theirs = await Task.WhenAll(asked);
        var mine = await Task.WhenAll(ours);
        var answered = theirs.Concat(mine).Where(x => x.Error is null).ToList();
        if (answered.Count == 0)
            return ([], sources is null ? theirs[0].Error : L("Aucun site de recherche n’a répondu au PC serveur : vérifie sa connexion à Internet.", "No search site answered the server PC: check its Internet connection."), names);
        return (SearchSources.Merge([.. answered.Select(x => (IReadOnlyList<AgentHit>)x.Hits)], MaxHits), null, names);
    }
}
