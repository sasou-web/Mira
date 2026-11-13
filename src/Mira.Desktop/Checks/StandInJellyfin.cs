using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Mira.Core;

namespace Mira.Checks;

/// <summary>
/// A Jellyfin answered inside Mira for --smooth-check (Windows and Mac: the Mac's links this file, each draws its
/// pictures with its own encoder): three libraries of a few hundred titles, Continuer à regarder, sagas, series with
/// their seasons, every picture drawn as a JPEG of the width asked (as Jellyfin resizes them), each answer after a home
/// network's delay. Nothing goes on the network; its WebSocket is never answered (the address does not exist).
/// </summary>
internal sealed partial class StandInJellyfin : HttpMessageHandler
{
    public const string Server = "http://stand-in.invalid/", UserId = "stand-in-user", Token = "stand-in-token", UserName = "Mesure";
    /// <summary>A Jellyfin on the same network: a list comes after this long, a picture after the other.</summary>
    public TimeSpan ListDelay { get; init; } = TimeSpan.FromMilliseconds(30);
    public TimeSpan ImageDelay { get; init; } = TimeSpan.FromMilliseconds(12);
    /// <summary>What it did not know (« GET path » and how many times): the report lists them.</summary>
    public ConcurrentDictionary<string, int> Unknown { get; } = new();
    public int Requests => _requests;
    public int Pictures => _pictures;
    private int _requests, _pictures;
    private readonly List<MediaItem> _movies = [], _series = [], _boxes = [], _people = [];
    private readonly List<StudioInfo> _studios = [];
    private readonly Dictionary<string, List<MediaItem>> _episodes = [];
    private readonly Dictionary<string, MediaItem> _byId = [];
    private readonly Dictionary<string, List<string>> _inBox = [];
    private readonly Dictionary<string, string> _libraryOf = [];
    private readonly ConcurrentDictionary<string, Task<byte[]>> _images = new();
    private static readonly string[] Blurs = ["LEHV6nWB2yk8pyo0adR*.7kCMdnj", "LGF5]+Yk^6#M@-5c,1J5@[or[Q6.", "L6PZfSi_.AyE_3t7t7R**0o#DgR4", "LKN]Rv%2Tw=w]~RBVZRi};RPxuwH"];

    public StandInJellyfin(int movies = 240, int series = 36)
    {
        var random = new Random(2611);
        var now = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
        string[] first = ["Silent", "Last", "Golden", "Hidden", "Broken", "Northern", "Blue", "Burning", "Distant", "Paper", "Iron", "Midnight", "Velvet", "Crimson", "Little", "Endless", "Electric", "Quiet", "Wild", "Glass", "Hollow", "Bright", "Secret", "Winter"];
        string[] second = ["Harbor", "Garden", "Signal", "River", "Kingdom", "Station", "Orchard", "Lantern", "Horizon", "Machine", "Tide", "Archive", "Summer", "Engine", "Ballad", "Forest", "Empire", "Island", "Mirror", "Comet", "Canyon", "Atlas", "Circus", "Frontier"];
        string[] genres = ["Action", "Aventure", "Animation", "Comédie", "Drame", "Fantastique", "Science-Fiction", "Thriller", "Romance", "Documentaire", "Crime", "Mystère"];
        string[] given = ["Lina", "Paul", "Inès", "Marc", "Yuki", "Sofia", "Omar", "Clara", "Hugo", "Maya", "Noé", "Elsa", "Léon", "Aya", "Tom"];
        string[] family = ["Marchand", "Okada", "Ferreira", "Lindqvist", "Benali", "Moreau", "Kowalski", "Tanaka"];
        string[] lines = ["Une ville s’éteint quand la dernière lanterne tombe.", "Deux inconnus traversent un pays qui n’existe plus sur aucune carte.", "Un été, une maison au bord de l’eau, et un secret que personne n’a gardé.",
            "Le dernier train part à minuit, et personne ne sait où il va.", "Une enquête qui commence par une lettre jamais envoyée.", "Sur une île oubliée, une forêt ne s’éveille qu’à la tombée du jour.",
            "Une équipe de bras cassés tente le casse du siècle.", "Quand la marée se retire, la ville engloutie remonte à la surface."];
        string Overview(int seed) => string.Join(' ', Enumerable.Range(0, 2 + seed % 2).Select(k => lines[(seed * 3 + k * 5) % lines.Length]));
        Dictionary<string, Dictionary<string, string>> Hashes(string id, params string[] types) =>
            types.ToDictionary(type => type, type => new Dictionary<string, string> { [type[..1].ToLowerInvariant() + id] = Blurs[Math.Abs(id.GetHashCode(StringComparison.Ordinal) + type.Length) % Blurs.Length] });
        for (var i = 0; i < 90; i++)
            _people.Add(new MediaItem { Id = $"person-{i}", Name = $"{given[i % given.Length]} {family[i / given.Length % family.Length]}", Type = "Person", ImageTags = i % 3 == 0 ? [] : new() { ["Primary"] = $"person-{i}" } });
        string[] studios = ["Lune Rouge", "Atelier Nord", "Studio Tetra", "Phare Films", "Kumo", "Vent d’Ouest", "Blue Hour", "Orbite", "Sakura Works", "Cinéma Bleu", "Ligne Claire", "Halcyon", "Mistral", "Comète"];
        for (var i = 0; i < studios.Length; i++) _studios.Add(new StudioInfo { Id = $"studio-{i}", Name = studios[i] });
        List<PersonInfo> Cast(int seed) =>
        [
            .. Enumerable.Range(0, 7).Select(k => _people[(seed * 7 + k * 13) % _people.Count]).Select((person, k) => new PersonInfo
            { Id = person.Id, Name = person.Name, Type = k == 6 ? "Director" : "Actor", Role = k == 6 ? null : $"{given[(seed + k) % given.Length]}", PrimaryImageTag = person.ImageTags.GetValueOrDefault("Primary") })
        ];
        for (var i = 0; i < movies; i++)
        {
            var id = $"movie-{i:000}";
            var name = $"The {first[i % 24]} {second[(i / 24 + i * 5) % 24]}";
            var runtime = TimeSpan.FromMinutes(84 + (i * 37) % 90).Ticks;
            var progress = i is 3 or 9 or 17 or 28 or 41 or 66 ? .15 + (i % 7) / 10d : 0;
            var movie = new MediaItem
            {
                Id = id, Name = name, Type = "Movie", ProductionYear = 1978 + (i * 17) % 48, RunTimeTicks = runtime, CommunityRating = (float)Math.Round(5.6 + (i * 29 % 33) / 10d, 1),
                OfficialRating = (i % 4) switch { 0 => "Tous publics", 1 => "12", 2 => "16", _ => "PG-13" }, Overview = Overview(i),
                Genres = [genres[i % genres.Length], genres[(i * 5 + 3) % genres.Length]], DateCreated = now.AddHours(-i * 7),
                PremiereDate = new DateTimeOffset(1978 + (i * 17) % 48, 1 + i % 12, 1 + i % 27, 0, 0, 0, TimeSpan.Zero),
                ImageTags = i % 2 == 0 ? new() { ["Primary"] = "p" + id, ["Logo"] = "l" + id } : new() { ["Primary"] = "p" + id },
                BackdropImageTags = ["b" + id], ImageBlurHashes = Hashes(id, "Primary", "Backdrop"), People = Cast(i),
                Studios = [_studios[i % _studios.Count], _studios[(i * 3 + 1) % _studios.Count]], Taglines = i % 3 == 0 ? ["Personne ne revient du même voyage."] : [],
                ProviderIds = new() { ["Tmdb"] = (10_000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                UserData = new UserData
                {
                    Played = i % 5 == 1, IsFavorite = i % 13 == 0, PlaybackPositionTicks = (long)(runtime * progress),
                    PlayedPercentage = progress > 0 ? progress * 100 : null, LastPlayedDate = progress > 0 || i % 5 == 1 ? now.AddHours(-i) : null
                }
            };
            _movies.Add(movie); _byId[id] = movie; _libraryOf[id] = "lib-movies";
        }
        for (var b = 0; b < 8; b++)
        {
            var id = $"box-{b}";
            var titles = Enumerable.Range(0, 3 + b % 3).Select(k => _movies[(b * 29 + k * 7) % _movies.Count].Id).Distinct().ToList();
            var box = new MediaItem { Id = id, Name = $"Saga {second[(b * 7) % 24]}", Type = "BoxSet", ChildCount = titles.Count, Overview = Overview(b + 3), ImageTags = new() { ["Primary"] = "p" + id }, BackdropImageTags = ["b" + id], ImageBlurHashes = Hashes(id, "Primary"), DateCreated = now.AddDays(-b) };
            _boxes.Add(box); _byId[id] = box; _inBox[id] = titles;
        }
        for (var s = 0; s < series; s++)
        {
            var id = $"series-{s:00}";
            var anime = s >= series - 10;
            var seasons = anime ? 1 + s % 2 : 1 + s % 4;
            var watched = s % 3 == 0 ? 0 : s % 3 == 1 ? 5 : 1000;
            var list = new List<MediaItem>();
            for (var season = 1; season <= seasons; season++)
            {
                var count = anime ? 12 + (s * 5 + season) % 13 : 8 + (s + season * 3) % 9;
                for (var e = 1; e <= count; e++)
                {
                    var episodeId = $"{id}-s{season}e{e:00}";
                    var number = list.Count;
                    var runtime = TimeSpan.FromMinutes(anime ? 24 : 42 + (s + e) % 16).Ticks;
                    var episode = new MediaItem
                    {
                        Id = episodeId, Name = $"{first[(s + e) % 24]} {second[(s * 3 + e * 7) % 24]}", Type = "Episode", SeriesId = id, SeriesName = $"{second[(s * 5) % 24]} {(anime ? "no Uta" : "Chronicles")}",
                        SeasonId = $"{id}-season{season}", ParentIndexNumber = season, IndexNumber = e, RunTimeTicks = runtime, Overview = Overview(s * 31 + e),
                        ImageTags = new() { ["Primary"] = "p" + episodeId }, ImageBlurHashes = Hashes(episodeId, "Primary"), ParentBackdropItemId = id, ParentBackdropImageTags = ["b" + id],
                        SeriesPrimaryImageTag = "p" + id, DateCreated = now.AddHours(-s * 11 - number), PremiereDate = now.AddDays(-400 + number * 7),
                        UserData = new UserData { Played = number < watched, LastPlayedDate = number < watched ? now.AddHours(-s * 3 - number) : null, PlaybackPositionTicks = number == watched && s % 4 == 1 ? runtime / 3 : 0 }
                    };
                    list.Add(episode); _byId[episodeId] = episode;
                }
            }
            var played = list.Count(x => x.UserData.Played);
            var show = new MediaItem
            {
                Id = id, Name = list[0].SeriesName!, Type = "Series", ProductionYear = 2004 + s % 22, ChildCount = seasons, CommunityRating = (float)Math.Round(6.8 + (s % 20) / 10d, 1), OfficialRating = "12",
                Overview = Overview(s + 11), Genres = anime ? ["Animation", genres[s % genres.Length]] : [genres[(s * 7) % genres.Length], genres[(s + 4) % genres.Length]],
                DateCreated = now.AddHours(-s * 11), ImageTags = new() { ["Primary"] = "p" + id, ["Logo"] = "l" + id }, BackdropImageTags = ["b" + id], ImageBlurHashes = Hashes(id, "Primary", "Backdrop"),
                People = Cast(s + 300), Studios = [_studios[(s * 5) % _studios.Count]],
                UserData = new UserData { Played = played == list.Count, IsFavorite = s % 7 == 0, PlayedPercentage = played == 0 ? null : 100d * played / list.Count, UnplayedItemCount = list.Count - played }
            };
            _series.Add(show); _byId[id] = show; _episodes[id] = list; _libraryOf[id] = anime ? "lib-anime" : "lib-series";
        }
        foreach (var person in _people) _byId[person.Id] = person;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var parts = path.Split('/');
        // Pictures are drawn away from the interface's thread, as Jellyfin sends them from another machine.
        if (parts is ["Items", var pictured, "Images", var type, ..])
        {
            Interlocked.Increment(ref _pictures);
            await Task.Delay(ImageDelay, cancellationToken).ConfigureAwait(false);
            var width = int.TryParse(query["maxWidth"] ?? query["fillWidth"], out var asked) ? Math.Clamp(asked, 16, 3840) : 400;
            var bytes = await _images.GetOrAdd($"{pictured}|{type}|{width}", _ => Task.Run(() => Picture(pictured, type, width))).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(type == "Logo" ? "image/png" : "image/jpeg") } } };
        }
        if (request.Method != HttpMethod.Get)
        {
            if (parts is ["UserFavoriteItems" or "UserPlayedItems", ..]) return Json(new UserData());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        await Task.Delay(ListDelay, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Answer(path, parts, query), cancellationToken).ConfigureAwait(false);
    }

    private HttpResponseMessage Answer(string path, string[] parts, System.Collections.Specialized.NameValueCollection query)
    {
        switch (parts)
        {
            case ["System", "Info", "Public"] or ["System", "Info"]: return Json(new { Id = "stand-in-server", ServerName = "Jellyfin de mesure", Version = "12.1.0", ProductName = "Jellyfin Server", OperatingSystem = "Linux" });
            case ["System", "Ping"]: return Json("Jellyfin Server");
            case ["Users", "Me"] or ["Users", UserId]: return Json(new { Id = UserId, Name = UserName, Policy = new { IsAdministrator = false, EnableCollectionManagement = false, EnableSubtitleManagement = false, EnableContentDownloading = false } });
            case ["UserViews"]:
                return Items([new MediaItem { Id = "lib-movies", Name = "Films", Type = "CollectionFolder" }, new MediaItem { Id = "lib-series", Name = "Séries", Type = "CollectionFolder" }, new MediaItem { Id = "lib-anime", Name = "Animes", Type = "CollectionFolder" }],
                    views: ["movies", "tvshows", "tvshows"]);
            case ["UserItems", "Resume"]:
                return Items([.. _movies.Where(x => x.UserData.PlaybackPositionTicks > 0), .. _episodes.Values.SelectMany(x => x).Where(x => x.UserData.PlaybackPositionTicks > 0)]);
            case ["Shows", "NextUp"]:
            {
                var next = _episodes.Where(x => query["seriesId"] is not { } one || x.Key == one).Select(x => x.Value.FirstOrDefault(e => !e.UserData.Played))
                    .OfType<MediaItem>().Where(x => query["seriesId"] is not null || _episodes[x.SeriesId!].Any(e => e.UserData.Played));
                return Items(next.Take(int.TryParse(query["limit"], out var most) ? most : 16));
            }
            case ["Shows", var seriesId, "Episodes"]:
                return Items(_episodes.GetValueOrDefault(seriesId) is { } all ? query["seasonId"] is { } season ? all.Where(x => x.SeasonId == season) : all : []);
            case ["Shows", "Upcoming"] or ["MediaSegments", _]: return Items([]);
            case ["Items", "Filters2"]: return Json(new { Genres = _movies.Concat(_series).SelectMany(x => x.Genres).Distinct().Order().Select(x => new { Name = x }) });
            case ["Years"]: return Items(_movies.Concat(_series).Select(x => x.ProductionYear).OfType<int>().Distinct().OrderDescending().Select(x => new MediaItem { Id = $"year-{x}", Name = x.ToString(System.Globalization.CultureInfo.InvariantCulture), Type = "Year" }));
            case ["Persons"]: return Items(_people.Where(x => Matches(x, query["searchTerm"])).Take(12));
            case ["Studios"]: return Items(_studios.Where(x => query["searchTerm"] is not { } term || x.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).Take(12).Select(x => new MediaItem { Id = x.Id!, Name = x.Name, Type = "Studio" }));
            case ["SyncPlay", "List"]: return Json(Array.Empty<object>());
            case ["DisplayPreferences", var name]: return Json(new { Id = name, Client = "mira", CustomPrefs = new Dictionary<string, string>() });
            case ["QuickConnect", "Enabled"]: return Json(false);
            // Asked of every account, answered to administrators only; no Mira extension on this one.
            case ["System", "Logs"] or ["Library", "VirtualFolders"]: return new HttpResponseMessage(HttpStatusCode.Forbidden);
            case ["Mira", ..]: return new HttpResponseMessage(HttpStatusCode.NotFound);
            case ["Playback", "BitrateTest"]: return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Math.Clamp(int.TryParse(query["size"], out var size) ? size : 0, 0, 10_000_000)]) };
            case ["Items", var id, "Similar"]:
            {
                if (!_byId.TryGetValue(id, out var title)) return Items([]);
                var pool = title.Type == "Series" ? _series : _movies;
                return Items(pool.Where(x => x.Id != id && x.Genres.Intersect(title.Genres).Any()).Take(int.TryParse(query["limit"], out var most) ? most : 12));
            }
            case ["Items", _, "SpecialFeatures"]: return Json(Array.Empty<MediaItem>());
            case ["Items", var id, "Collections"]: return Items(_inBox.Where(x => x.Value.Contains(id)).Select(x => _byId[x.Key]));
            case ["Items", var id] when _byId.TryGetValue(id, out var item): return Json(Detailed(item));
            case ["Items"]: return Browse(query);
        }
        Unknown.AddOrUpdate("GET " + string.Join('/', parts.Select(x => _byId.ContainsKey(x) ? "{id}" : x)), 1, (_, count) => count + 1);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>Items?…, as Jellyfin filters, sorts and pages it for Mira's lists.</summary>
    private HttpResponseMessage Browse(System.Collections.Specialized.NameValueCollection query)
    {
        if (query["ids"] is { } ids)
            return Items(ids.Split(',').Select(id => _byId.GetValueOrDefault(id) ?? (id.Contains("-season", StringComparison.Ordinal)
                ? new MediaItem { Id = id, Name = "Saison " + id[(id.LastIndexOf("season", StringComparison.Ordinal) + 6)..], Type = "Season", ImageTags = id.EndsWith('1') ? new() { ["Thumb"] = "t" + id } : [] } : null)).OfType<MediaItem>());
        if (query["locationTypes"] == "Virtual") return Items([]);
        var types = (query["includeItemTypes"] ?? "Movie,Series").Split(',', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MediaItem> items = types.SelectMany(type => type switch
        {
            "Movie" => _movies,
            "Series" => _series,
            "BoxSet" => _boxes,
            "Episode" => _episodes.Values.SelectMany(x => x),
            _ => []
        });
        if (query["parentId"] is { } parent)
            items = _inBox.TryGetValue(parent, out var inside) ? inside.Select(x => _byId[x])
                : parent.Contains("-season", StringComparison.Ordinal) ? _episodes.Values.SelectMany(x => x).Where(x => x.SeasonId == parent)
                : items.Where(x => _libraryOf.GetValueOrDefault(x.Id) == parent);
        if (query["searchTerm"] is { } term) items = items.Where(x => Matches(x, term));
        if (query["isFavorite"] == "true") items = items.Where(x => x.UserData.IsFavorite);
        if (query["isPlayed"] is { } played) items = items.Where(x => x.UserData.Played == (played == "true"));
        if (query["genres"] is { } genre) items = items.Where(x => genre.Split('|').Any(g => x.Genres.Contains(g)));
        if (query["years"] is { } years) items = items.Where(x => years.Split(',').Contains(x.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (query["personIds"] is { } person) items = items.Where(x => x.People.Any(p => p.Id == person));
        if (query["studioIds"] is { } studio) items = items.Where(x => x.Studios.Any(s => s.Id == studio));
        var descending = query["sortOrder"] == "Descending";
        items = (query["sortBy"]?.Split(',')[0]) switch
        {
            "SortName" => descending ? items.OrderByDescending(x => x.Name, StringComparer.Ordinal) : items.OrderBy(x => x.Name, StringComparer.Ordinal),
            "ProductionYear" or "PremiereDate" => descending ? items.OrderByDescending(x => x.ProductionYear).ThenBy(x => x.Name, StringComparer.Ordinal) : items.OrderBy(x => x.ProductionYear).ThenBy(x => x.Name, StringComparer.Ordinal),
            "CommunityRating" => items.OrderByDescending(x => x.CommunityRating).ThenBy(x => x.Name, StringComparer.Ordinal),
            "DatePlayed" => items.OrderByDescending(x => x.UserData.LastPlayedDate),
            _ => items.OrderByDescending(x => x.DateCreated).ThenBy(x => x.Name, StringComparer.Ordinal)
        };
        var all = items.ToList();
        var start = int.TryParse(query["startIndex"], out var from) ? from : 0;
        var limit = int.TryParse(query["limit"], out var most) ? most : all.Count;
        return Items(all.Skip(start).Take(limit), all.Count);
    }
    private static bool Matches(MediaItem item, string? term) => term is null || item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    /// <summary>A title as Items/{id} gives it: its file too (a film, an episode), as Mira's page reads its picture and sound from it.</summary>
    private static MediaItem Detailed(MediaItem item) => item.Type is not ("Movie" or "Episode") ? item : item with
    {
        MediaSources =
        [
            new MediaSource
            {
                Id = "source-" + item.Id, Container = "mkv", Protocol = "File", Size = 4_800_000_000, Bitrate = 9_000_000,
                MediaStreams =
                [
                    new MediaStream { Index = 0, Type = "Video", Codec = "hevc", Width = 1920, Height = 1080, DisplayTitle = "1080p HEVC SDR" },
                    new MediaStream { Index = 1, Type = "Audio", Codec = "eac3", Language = "fre", DisplayTitle = "Français - E-AC3 5.1" },
                    new MediaStream { Index = 2, Type = "Audio", Codec = "aac", Language = "eng", DisplayTitle = "English - AAC - Stereo" },
                    new MediaStream { Index = 3, Type = "Subtitle", Codec = "subrip", Language = "fre", DisplayTitle = "Français - SUBRIP" }
                ]
            }
        ]
    };
    private static HttpResponseMessage Items(IEnumerable<MediaItem> items, int? total = null, string[]? views = null)
    {
        var list = items.ToList();
        // Libraries say what they hold (CollectionType), which MediaItem does not carry.
        object body = views is null ? new ItemsResult { Items = list, TotalRecordCount = total ?? list.Count }
            : new { Items = list.Select((x, i) => new { x.Id, x.Name, x.Type, CollectionType = views[i] }), TotalRecordCount = list.Count };
        return Json(body);
    }
    /// <summary>Written as UTF-8 at once, as Jellyfin sends it: no text in between for Mira's collections to count.</summary>
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value)) { Headers = { ContentType = new("application/json") { CharSet = "utf-8" } } } };
    /// <summary>
    /// A picture's bytes as the check keeps them for its whole run, outside the generations the collector moves (pinned
    /// object heap): Jellyfin keeps its pictures on its own disk, in its own process. Among Mira's own young objects,
    /// each new one was more for Mira's next collection to keep and move, which a real server never costs it.
    /// </summary>
    private static byte[] Kept(ReadOnlySpan<byte> bytes)
    {
        var kept = GC.AllocateUninitializedArray<byte>(bytes.Length, pinned: true);
        bytes.CopyTo(kept); return kept;
    }

    /// <summary>
    /// A picture as a real one weighs: a tone per title, light and shapes across it, a grain that JPEG must keep (a flat
    /// picture decodes far faster than a poster). Posters stand, backdrops and episodes lie, logos are see-through PNGs.
    /// </summary>
    private static byte[] Picture(string id, string type, int width)
    {
        var portrait = type == "Primary" && !id.Contains("-s", StringComparison.Ordinal) || id.StartsWith("person-", StringComparison.Ordinal);
        var height = type == "Logo" ? width / 3 : portrait ? (int)Math.Round(width * 1.5) : (int)Math.Round(width * 9 / 16d);
        height = Math.Max(8, height);
        var seed = (uint)id.GetHashCode(StringComparison.Ordinal) ^ (uint)type.Length * 2654435761u;
        // Drawn in a borrowed buffer: a poster's pixels are a large object, which would make the collector run for the
        // check's own drawing rather than for Mira's.
        var hue = seed % 360; var stride = width * 4; var pixels = ArrayPool<byte>.Shared.Rent(stride * height);
        static (double R, double G, double B) Hsv(double h, double s, double v)
        {
            var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
            var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
            return ((r + m) * 255, (g + m) * 255, (b + m) * 255);
        }
        var top = Hsv(hue, .55, .62); var bottom = Hsv((hue + 40) % 360, .7, .16); var accent = Hsv((hue + 180) % 360, .45, .9);
        double cx = width * (.3 + seed % 40 / 100d), cy = height * (.35 + seed % 30 / 100d), radius = Math.Min(width, height) * .28;
        var state = seed | 1;
        for (var y = 0; y < height; y++)
        {
            var t = y / (double)height;
            for (var x = 0; x < width; x++)
            {
                state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                var grain = (int)(state % 17) - 8;
                var d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / radius;
                var glow = d < 1 ? (1 - d) * (1 - d) * .8 : 0;
                var stripe = (x + y * 2) / Math.Max(4, width / 18) % 2 == 0 ? .06 : 0;
                double Mix(double a, double b, double c) => Math.Clamp(a + (b - a) * t + (c - a - (b - a) * t) * glow + 255 * stripe * (1 - t) + grain, 0, 255);
                var at = y * stride + x * 4;
                if (type == "Logo")
                {
                    // Letters' bands on a clear background.
                    var ink = y > height / 4 && y < height * 3 / 4 && x / Math.Max(4, width / 9) % 3 != 2;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = (byte)(ink ? 240 : 0); pixels[at + 3] = (byte)(ink ? 255 : 0);
                    continue;
                }
                pixels[at] = (byte)Mix(top.B, bottom.B, accent.B); pixels[at + 1] = (byte)Mix(top.G, bottom.G, accent.G); pixels[at + 2] = (byte)Mix(top.R, bottom.R, accent.R); pixels[at + 3] = 255;
            }
        }
        try { return Encode(pixels, width, height, type == "Logo"); }
        finally { ArrayPool<byte>.Shared.Return(pixels); }
    }
    /// <summary>
    /// Blue, green, red and alpha per pixel, top row first, as a JPEG at 85 (a see-through PNG for a logo,
    /// <paramref name="png"/>): by WPF on Windows, by Skia on the Mac. <paramref name="pixels"/> may be longer than the picture.
    /// </summary>
    private static partial byte[] Encode(byte[] pixels, int width, int height, bool png);
}
