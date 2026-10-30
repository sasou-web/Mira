using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Mira.Mac.Checks;

/// <summary>
/// A stand-in Jellyfin for the self-check on a Mac without a server (GitHub's build Macs): a small library of films and
/// series whose videos are local files, artwork drawn on the fly, and every playback report recorded.
/// </summary>
public sealed class MockJellyfin : HttpMessageHandler
{
    public const string Server = "http://127.0.0.1:8096/", UserId = "check-user";
    private readonly string _media, _short;
    private readonly List<object> _movies, _series;
    private readonly Dictionary<string, List<object>> _episodes = [];
    private readonly Dictionary<string, byte[]> _images = [];
    /// <summary>Every request, « GET Items/… ».</summary>
    public List<string> Calls { get; } = [];
    public List<(string Kind, JsonElement Body)> Reports { get; } = [];
    /// <summary>What « Téléchargements » sent to the PC of Jellyfin: the names of its requests.</summary>
    public List<string> Requests { get; } = [];

    public MockJellyfin(string media, string shortMedia)
    {
        _media = media; _short = shortMedia;
        object Person(string id, string name, string type) => new { Id = id, Name = name, Type = type, Role = "" };
        object Movie(string id, string name, int year, double progress, string overview) => new
        {
            Id = id, Name = name, Type = "Movie", ProductionYear = year, RunTimeTicks = 30L * TimeSpan.TicksPerSecond, CommunityRating = 7.8,
            Overview = overview, Genres = new[] { "Aventure", "Animation" }, ImageTags = new Dictionary<string, string> { ["Primary"] = "p" + id },
            BackdropImageTags = new[] { "b" + id }, OfficialRating = "Tous publics",
            People = new[] { Person("actor-1", "Jeanne Essai", "Actor"), Person("actor-2", "Paul Témoin", "Actor"), Person("director-1", "Réa Lisatrice", "Director") },
            UserData = new { PlaybackPositionTicks = (long)(progress * 30 * TimeSpan.TicksPerSecond), Played = false, IsFavorite = id == "movie-2", LastPlayedDate = progress > 0 ? DateTimeOffset.UtcNow.AddHours(-2) : (DateTimeOffset?)null }
        };
        _movies =
        [
            Movie("movie-1", "Les heures bleues", 2025, 0.4, "Sur une île oubliée, deux voyageurs suivent les lumières d’une forêt qui ne s’éveille qu’à la tombée du jour."),
            Movie("movie-2", "Brume basse", 2024, 0, "Un phare, une tempête, et le gardien qui n’avait jamais quitté son rocher."),
            Movie("movie-3", "Ciel ouvert", 2023, 0, "Trois amis traversent le pays en planeur, d’un aérodrome à l’autre.")
        ];
        object Series(string id, string name, int seasons) => new
        {
            Id = id, Name = name, Type = "Series", ProductionYear = 2022, ChildCount = seasons, Overview = $"{name}, la série de démonstration du contrôle de Mira pour Mac.",
            Genres = new[] { "Comédie" }, ImageTags = new Dictionary<string, string> { ["Primary"] = "p" + id }, BackdropImageTags = new[] { "b" + id },
            People = new[] { Person("actor-1", "Jeanne Essai", "Actor") }, UserData = new { PlaybackPositionTicks = 0L, Played = false, IsFavorite = false }
        };
        _series = [Series("series-1", "Petite série", 1), Series("series-2", "Longue série", 1)];
        _episodes["series-1"] = Enumerable.Range(1, 3).Select(i => Episode("series-1", "Petite série", i, 6)).ToList();
        _episodes["series-2"] = Enumerable.Range(1, 130).Select(i => Episode("series-2", "Longue série", i, 6)).ToList();
    }
    private static object Episode(string series, string seriesName, int number, int seconds) => new
    {
        Id = $"{series}-e{number}", Name = $"Épisode {number}", Type = "Episode", SeriesId = series, SeriesName = seriesName, SeasonId = series + "-s1",
        ParentIndexNumber = 1, IndexNumber = number, RunTimeTicks = seconds * TimeSpan.TicksPerSecond, Overview = $"L’épisode {number}.",
        ImageTags = new Dictionary<string, string> { ["Primary"] = $"p{series}{number}" }, ParentBackdropItemId = series, ParentBackdropImageTags = new[] { "b" + series },
        UserData = new { PlaybackPositionTicks = 0L, Played = false, IsFavorite = false }
    };

    private IEnumerable<object> All => _movies.Concat(_series);
    private object? Find(string id) => All.Concat(_episodes.Values.SelectMany(x => x)).FirstOrDefault(x => Id(x) == id);
    private static string Id(object item) => (string)item.GetType().GetProperty("Id")!.GetValue(item)!;
    private static string Name(object item) => (string)item.GetType().GetProperty("Name")!.GetValue(item)!;
    private static string Type(object item) => (string)item.GetType().GetProperty("Type")!.GetValue(item)!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        lock (Calls) Calls.Add($"{request.Method} {path}");
        if (request.Method == HttpMethod.Post && path.StartsWith("Sessions/Playing"))
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            lock (Reports) Reports.Add((path switch { "Sessions/Playing" => "start", "Sessions/Playing/Progress" => "progress", _ => "stop" }, body));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        // Reached over a network (TailnetBench: through Mira's Tailscale node): the sign-in, and the file as it is streamed.
        if (path == "System/Info/Public") return Json(new { Id = "check-server", ServerName = "Jellyfin du contrôle", Version = "10.10.7", ProductName = "Jellyfin Server" });
        if (path == "System/Ping") return Json("Jellyfin Server");
        if (request.Method == HttpMethod.Post && path == "Users/AuthenticateByName") return Json(new { AccessToken = "check-token", ServerId = "check-server", User = new { Id = UserId, Name = "Contrôle" } });
        if (path == "Playback/BitrateTest") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[Math.Clamp(int.TryParse(query["size"], out var size) ? size : 0, 0, 10_000_000)]) };
        if (path.StartsWith("Videos/") && path.EndsWith("/stream")) return await FileAsync(request, path.Split('/')[1].StartsWith("series") ? _short : _media, cancellationToken);
        // An administrator, whose PC of Jellyfin runs Mira for Windows: « Téléchargements » through the relay of the extension.
        if (path == "Users/Me") return Json(new { Id = UserId, Name = "Contrôle", Policy = new { IsAdministrator = true } });
        if (path.StartsWith("Mira/torlink")) return await RelayAsync(request, path, cancellationToken);
        if (path.StartsWith("UserPlayedItems/") || path.StartsWith("UserFavoriteItems/")) return Json(new { });
        if (request.Method == HttpMethod.Post && path == "Sessions/Logout") return new HttpResponseMessage(HttpStatusCode.NoContent);
        // « En ce moment sur Jellyfin »: a friend's iPhone, converted, that takes « Arrêter ».
        if (path == "Sessions") return Json(new object[]
        {
            new
            {
                Id = "session-phone", UserName = "Marc", DeviceName = "iPhone", Client = "Mira", DeviceId = "phone", RemoteEndPoint = "100.101.102.103", SupportsRemoteControl = true,
                NowPlayingItem = new { Type = "Movie", Name = "Brume basse", RunTimeTicks = 6_000_000_000L },
                PlayState = new { PositionTicks = 1_200_000_000L, IsPaused = false, PlayMethod = "Transcode" },
                TranscodingInfo = new { IsVideoDirect = false, IsAudioDirect = true, VideoCodec = "h264", AudioCodec = "aac", Bitrate = 4_000_000, TranscodeReasons = new[] { "ContainerBitrateExceedsLimit" }, HardwareAccelerationType = "videotoolbox" }
            }
        });
        if (path.Contains("/Images/")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(await ImageAsync(path)) };
        if (path == "UserItems/Resume") return Items(_movies.Where(x => Id(x) == "movie-1"));
        if (path == "Shows/NextUp") return Items(query["seriesId"] is { } series ? _episodes.GetValueOrDefault(series)?.Take(1) ?? [] : _episodes["series-1"].Take(1));
        if (path == "Items/Filters2") return Json(new { Genres = new[] { new { Name = "Aventure" }, new { Name = "Animation" }, new { Name = "Comédie" } } });
        if (path == "UserViews") return Items(new object[] { new { Id = "view-movies", Name = "Films", CollectionType = "movies" }, new { Id = "view-series", Name = "Séries", CollectionType = "tvshows" } });
        if (path == "Years") return Items(new object[] { new { Id = "y1", Name = "2025" }, new { Id = "y2", Name = "2024" } });
        if (path.StartsWith("Shows/") && path.EndsWith("/Episodes")) return Items(_episodes.GetValueOrDefault(path.Split('/')[1]) ?? []);
        if (path.EndsWith("/Similar")) return Items(_movies.Where(x => !path.Contains(Id(x))));
        if (path.EndsWith("/PlaybackInfo"))
        {
            var id = path.Split('/')[1];
            var file = id.StartsWith("series") ? _short : _media;
            return Json(new { PlaySessionId = "session-" + id, MediaSources = new[] { new { Id = "source-" + id, Protocol = "File", Path = file, MediaStreams = Array.Empty<object>() } } });
        }
        if (path.StartsWith("MediaSegments/")) return Json(new { Items = Array.Empty<object>() });
        // « Chercher des sous-titres… »: an extension, and what it finds.
        if (path == "Libraries/AvailableOptions") return Json(new { SubtitleFetchers = new[] { new { Name = "Open Subtitles" } } });
        if (path.StartsWith("Items/") && path.Contains("/RemoteSearch/Subtitles/"))
            return Json(new object[] { new { Id = "sub-1", Name = "Les.heures.bleues.2025.1080p.WEB", ProviderName = "Open Subtitles", Format = "srt", ThreeLetterISOLanguageName = path.Split('/')[^1], DownloadCount = 1200, IsHashMatch = true }, new { Id = "sub-2", Name = "Les heures bleues (2025)", ProviderName = "Open Subtitles", Format = "srt", ThreeLetterISOLanguageName = path.Split('/')[^1], DownloadCount = 40 } });
        if (path == "Persons")
        {
            var people = new[] { ("actor-1", "Jeanne Essai"), ("actor-2", "Paul Témoin"), ("director-1", "Réa Lisatrice") };
            return Items(people.Where(x => x.Item2.Contains(query["searchTerm"] ?? "", StringComparison.CurrentCultureIgnoreCase))
                .Select(x => (object)new { Id = x.Item1, Name = x.Item2, Type = "Person", ImageTags = new Dictionary<string, string> { ["Primary"] = "p" + x.Item1 } }));
        }
        if (path.StartsWith("Items/") && path.Split('/').Length == 2) return Find(path.Split('/')[1]) is { } item ? Json(item) : new HttpResponseMessage(HttpStatusCode.NotFound);
        if (path == "Items")
        {
            IEnumerable<object> items = All;
            if (query["ids"] is { } ids) return Items(ids.Split(',').Select(x => (object)new { Id = x, Name = "Saison 1", Type = "Season", ImageTags = new Dictionary<string, string>() }));
            if (query["includeItemTypes"] is { } types) items = items.Where(x => types.Split(',').Contains(Type(x)));
            if (query["searchTerm"] is { } search) items = items.Where(x => Name(x).Contains(search, StringComparison.CurrentCultureIgnoreCase));
            if (query["isFavorite"] == "true") items = items.Where(x => Id(x) == "movie-2");
            if (query["personIds"] is { } person) items = items.Where(x => person.StartsWith("actor") || Type(x) == "Movie");
            return Items(items);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
    private async Task<HttpResponseMessage> RelayAsync(HttpRequestMessage request, string path, CancellationToken ct)
    {
        const long GiB = 1L << 30, MiB = 1L << 20;
        if (request.Method == HttpMethod.Post && path == "Mira/torlink/search") return Json(new { id = "search-1" });
        if (path == "Mira/torlink/search/search-1")
        {
            object Hit(string id, string name, double gib, int seeders, string source) => new { id, name, size = (long)(gib * GiB), seeders, leechers = seeders / 10, source, added = (long?)null };
            return Json(new
            {
                id = "search-1", query = "Brume basse", category = "movies", state = "done", error = (string?)null,
                results = new[]
                {
                    Hit("h1", "Brume.Basse.2024.720p.WEBRip.x264-YTS", 1.1, 900, "YTS"),
                    Hit("h2", "Brume.Basse.2024.MULTI.1080p.BluRay.x264-NOTAG", 8.9, 140, "1337x"),
                    Hit("h3", "Brume.Basse.2024.FRENCH.1080p.WEB-DL.H264-GRP", 3.1, 320, "1337x"),
                    Hit("h4", "Brume.Basse.2024.2160p.WEB-DL.HDR.HEVC-GRP", 15.2, 80, "1337x")
                }
            });
        }
        if (request.Method == HttpMethod.Post && path == "Mira/torlink/requests")
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
            lock (Requests) Requests.Add(body.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "magnet");
            return Json(new { id = "request-" + Requests.Count });
        }
        if (request.Method != HttpMethod.Get || path != "Mira/torlink") return new HttpResponseMessage(HttpStatusCode.NotFound);
        List<string> sent; lock (Requests) sent = [.. Requests];
        return Json(new
        {
            agent = new { name = "SALON-PC", online = true, seen = DateTimeOffset.UtcNow, accepting = true, problem = (string?)null },
            requests = sent.Select((name, i) => new { id = $"request-{i + 1}", name, size = 3 * GiB, infoHash = "", state = "waiting", error = (string?)null, created = DateTimeOffset.UtcNow }),
            controls = Array.Empty<object>(),
            downloads = new[] { new { id = "download-1", name = "Ciel.Ouvert.2023.MULTI.1080p.WEB-DL.x264-GRP", status = "downloading", progress = 41, speed = 2 * MiB, peers = 12, size = 29 * GiB / 10, downloaded = 12 * GiB / 10, eta = 750L } },
            placed = new[] { new { id = "placed-1", title = "Les heures bleues", status = "rangé dans Films", itemId = "movie-1", ok = true } }
        });
    }
    /// <summary>A video as Jellyfin streams the file as it is (Videos/{id}/stream?static=true): whole, or the range mpv asks for.</summary>
    private static async Task<HttpResponseMessage> FileAsync(HttpRequestMessage request, string file, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        HttpResponseMessage response;
        if (request.Headers.Range?.Ranges.FirstOrDefault() is not { } range) response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        else
        {
            // « bytes=a-b », « bytes=a- », or the last n bytes (« bytes=-n »).
            long from = range.From ?? Math.Max(0, bytes.Length - (range.To ?? 0)), to = range.From is not null && range.To is { } end ? Math.Min(end, bytes.Length - 1) : bytes.Length - 1;
            if (from >= bytes.Length || from > to) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response = new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes, (int)from, (int)(to - from + 1)) };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, bytes.Length);
        }
        response.Content.Headers.ContentType = new("video/x-matroska");
        response.Headers.AcceptRanges.Add("bytes");
        return response;
    }
    private static HttpResponseMessage Items(IEnumerable<object> items) { var list = items.ToList(); return Json(new { Items = list, TotalRecordCount = list.Count }); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    /// <summary>Artwork drawn by Avalonia: a tone per title and its name, in the requested shape.</summary>
    private async Task<byte[]> ImageAsync(string path)
    {
        lock (_images) if (_images.TryGetValue(path, out var hit)) return hit;
        var id = path.Split('/')[1]; var type = path.Split('/')[3];
        var name = Find(id) is { } item ? Name(item) : id;
        var bytes = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var (width, height) = type == "Primary" && !id.Contains("-e") ? (400, 600) : (960, 540);
            var hue = Math.Abs(id.GetHashCode() % 360);
            var tone = new Avalonia.Media.HsvColor(1, hue, 0.45, 0.45).ToRgb();
            var art = new Border
            {
                Width = width, Height = height,
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(tone, 0), new GradientStop(Color.Parse("#101014"), 1) } },
                Child = new TextBlock { Text = name, FontSize = width / 11.0, FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24) }
            };
            art.Measure(new Size(width, height)); art.Arrange(new Rect(0, 0, width, height));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(art);
            using var stream = new MemoryStream(); bitmap.Save(stream);
            return stream.ToArray();
        });
        lock (_images) _images[path] = bytes;
        return bytes;
    }
}
