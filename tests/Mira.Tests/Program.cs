using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Playback;

var testRoot = Path.GetFullPath(Path.Combine(".artifacts", "tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
if (args.Contains("--prepare-ui"))
{
    var profile = new LocalProfile(Path.GetFullPath(".artifacts/mock-profile"));
    profile.SaveConnection(new("http://127.0.0.1:18096/", "mock-user", "Compte de test", "mira-test-token", profile.DeviceId));
    profile.SaveSettings(new() { Volume = 0, AutoNext = false });
    Console.WriteLine("Profil de test isolé créé."); return;
}
var passed = 0;
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS  " + name); }
PlaybackReport Report(long position = 0) => new() { ItemId = "item-1", MediaSourceId = "source-1", PlaySessionId = "session-1", PositionTicks = position };
LibraryStore Store(string name) => new(testRoot, name);

await Test("Reprise : la date Jellyfin est conservée et les titres sont classés par dernière lecture", () =>
{
    var recent = DateTimeOffset.UtcNow;
    MediaItem Episode(string id, string series, DateTimeOffset at, long position = 30) => new() { Id = id, Type = "Episode", SeriesId = series, RunTimeTicks = 1000, UserData = new() { PlaybackPositionTicks = position, LastPlayedDate = at } };
    var old = Episode("old", "old-series", recent.AddDays(-2));
    var latest = Episode("latest", "new-series", recent);
    var parsed = JsonSerializer.Deserialize<MediaItem>(JsonSerializer.Serialize(latest, Json.Options), Json.Options)!;
    Assert(parsed.UserData.LastPlayedDate == recent, "LastPlayedDate was discarded");
    var list = ContinueWatching.Order([old, latest], [], []);
    Assert(list[0].Id == "latest", "The most recent resume is not first");
    var next = Episode("next", "new-series", default, 0);
    var earlierSameSeries = Episode("earlier", "new-series", recent.AddDays(-1));
    list = ContinueWatching.Order([earlierSameSeries, latest, old], [next], []);
    Assert(list.Count == 2 && list[0].Id == "latest", "NextUp or an older episode displaced the actual resume");
    latest.UserData.Played = true; latest.UserData.PlaybackPositionTicks = 0;
    list = ContinueWatching.Order([old], [next], [latest]);
    Assert(list[0].Id == "next", "A just-finished series must keep its next episode at the front");
    return Task.CompletedTask;
});
await Test("Reprise : ordre persistant et réponse serveur retardée après une lecture locale", () =>
{
    var store = Store("recent-playback"); var now = DateTimeOffset.UtcNow;
    var current = new MediaItem { Id = "local", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = now, PlaybackPositionTicks = 220 } };
    var earlier = current with { UserData = current.UserData with { LastPlayedDate = now.AddMinutes(-1), PlaybackPositionTicks = 100 } };
    store.RememberPlayback(current); store.RememberPlayback(earlier);
    var reopened = Store("recent-playback"); var history = reopened.RecentPlayback();
    Assert(history.Single().UserData.PlaybackPositionTicks == 220, "An old write overwrote recent activity");
    Assert(reopened.Load<List<MediaItem>>("resume")!.Single().Id == "local", "Local resume was not cached across restarts");
    Assert(reopened.MergeResume([], history).Single().Id == "local", "Late server response dropped the most recent playback");
    var newerRemote = current with { UserData = current.UserData with { LastPlayedDate = now.AddSeconds(10), PlaybackPositionTicks = 500 } };
    Assert(reopened.MergeResume([newerRemote], history).Single().UserData.PlaybackPositionTicks == 500, "Local history replaced a newer remote playback");
    reopened.ForgetProgress("local"); Assert(reopened.RecentPlayback().Count == 0 && reopened.Load<List<MediaItem>>("resume")!.Count == 0, "Manual watched/unwatched reset left a stale local resume");
    return Task.CompletedTask;
});
await Test("Reprise : une ancienne lecture absente du serveur ne revient que si sa synchronisation attend", () =>
{
    var store = Store("recent-pending");
    var old = new MediaItem { Id = "item-1", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = DateTimeOffset.UtcNow.AddDays(-1), PlaybackPositionTicks = 200 } };
    store.RememberPlayback(old); Assert(store.MergeResume([], store.RecentPlayback()).Count == 0, "An expired local entry was resurrected");
    store.Enqueue("stop", Report(200)); Assert(store.MergeResume([], store.RecentPlayback()).Count == 1, "An offline pending resume disappeared");
    store.Acknowledge(store.Peek()!.Id); Assert(store.MergeResume([], store.RecentPlayback()).Count == 0, "Acknowledged old history overrode the server");
    return Task.CompletedTask;
});

await Test("URL de serveur : sous-chemin conservé, URL invalide refusée", () =>
{
    Assert(JellyfinClient.NormalizeServer(" https://example.test/jellyfin ") == "https://example.test/jellyfin/", "Base path lost");
    foreach (var bad in new[] { "file:///tmp/video", "http://user:secret@localhost", "http://localhost?api_key=x", "localhost:8096" })
    { try { JellyfinClient.NormalizeServer(bad); throw new Exception("Accepted invalid URI"); } catch (ArgumentException) { } }
    return Task.CompletedTask;
});
await Test("Authentification : corps JSON, jeton dans l’en-tête, recherche encodée", async () =>
{
    var requests = new List<(string Url, string Header, string Body)>();
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "", "", "", "device"), new Handler(async request =>
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "", body));
        return JsonResponse(request.RequestUri.AbsolutePath.EndsWith("AuthenticateByName") ? new { AccessToken = "test-secret", User = new { Id = "user-1", Name = "Alice" } } : new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
    }));
    await client.LoginAsync("Alice", "password-test"); await client.BrowseAsync(search: "été & lune");
    Assert(requests[0].Body.Contains("password-test"), "Missing authentication body");
    Assert(requests[1].Header.Contains("test-secret"), "Missing authorization header");
    Assert(requests[1].Url.Contains("/jellyfin/Items?"), "Lost reverse-proxy path");
    Assert(!requests.Any(r => r.Url.Contains("test-secret") || r.Url.Contains("password-test")), "Credential in request URI");
    Assert(requests[1].Url.Contains("%26"), "Search not escaped");
});
await Test("Filtres : recherche, genre, année et pagination envoyés ensemble à Jellyfin", async () =>
{
    Uri? requestUri = null;
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
    { requestUri = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 0 })); }));
    await client.BrowseAsync("Series", "library-1", "été", 60, true, filters: new CatalogQuery("Action & aventure", 2024, false, "title"));
    var query = requestUri!.Query;
    Assert(query.Contains("genres=Action%20%26%20aventure"), "Genre not encoded");
    foreach (var part in new[] { "years=2024", "isPlayed=false", "sortBy=SortName", "sortOrder=Ascending", "startIndex=60", "parentId=library-1", "isFavorite=true", "includeItemTypes=Series" }) Assert(query.Contains(part), "Missing query component: " + part);
    Assert(new CatalogQuery(Played: false).CacheKey != new CatalogQuery(Played: true).CacheKey, "Watched filters share a cache key");
    Assert(new CatalogQuery().Parameters.Contains("DateCreated"), "Default sort changed");
});
await Test("Facettes : genres récursifs et années de toute la bibliothèque", async () =>
{
    var paths = new List<string>();
    using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
    {
        paths.Add(request.RequestUri!.PathAndQuery);
        return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("Filters2")
            ? JsonResponse(new { Genres = new[] { new { Name = "Animation", Id = "genre-1" } } })
            : JsonResponse(new { Items = new[] { new { Name = "2004" }, new { Name = "2026" }, new { Name = "inconnue" } } }));
    }));
    var facets = await client.FiltersAsync();
    Assert(facets.Genres.SequenceEqual(new[] { "Animation" }), "Genre names were lost");
    Assert(facets.Years.SequenceEqual(new[] { 2004, 2026 }), "Years were not parsed correctly");
    Assert(paths.Count == 2 && paths.All(x => x.Contains("recursive=true") && x.Contains("userId=user-1")), "Facets are not recursive and user scoped");
});
await Test("Préférences : migration des anciens réglages et persistance de l’apparence", () =>
{
    var old = JsonSerializer.Deserialize<PlayerSettings>("{\"Volume\":45}", Json.Options)!;
    Assert(old.RememberPosition && old.ShowProgress && old.HeroAutoPlay && !old.ReduceMotion && old.PosterDensity == "Comfortable", "Old settings lose defaults");
    var profile = new LocalProfile(Path.Combine(testRoot, "appearance"));
    profile.SaveSettings(new PlayerSettings { ShowProgress = false, ReduceMotion = true, HeroAutoPlay = false, PosterDensity = "Compact", RememberPosition = false, AudioLanguage = "eng,en" });
    var restored = profile.LoadSettings();
    Assert(!restored.ShowProgress && restored.ReduceMotion && !restored.HeroAutoPlay && restored.PosterDensity == "Compact" && !restored.RememberPosition && restored.AudioLanguage == "eng,en", "Preferences were not persisted");
    return Task.CompletedTask;
});
await Test("File durable : démarrage et arrêt préservés, progression coalescée", () =>
{
    var store = Store("outbox"); store.Enqueue("start", Report());
    for (var i = 1; i <= 100; i++) store.Enqueue("progress", Report(i));
    store.Enqueue("stop", Report(101)); Assert(store.PendingCount == 3, "Unexpected number of queued reports");
    var reopened = Store("outbox"); var kinds = new List<string>(); var positions = new List<long>();
    while (reopened.Peek() is { } entry) { kinds.Add(entry.Kind); positions.Add(entry.Report.PositionTicks); reopened.Acknowledge(entry.Id); }
    Assert(kinds.SequenceEqual(new[] { "start", "progress", "stop" }), "Ordering lost");
    Assert(positions.SequenceEqual(new long[] { 0, 100, 101 }), "Latest position lost"); return Task.CompletedTask;
});
await Test("Une réponse ancienne ne supprime pas une progression plus récente", () =>
{
    var store = Store("inflight"); store.Enqueue("progress", Report(10)); var old = store.Peek()!;
    store.Enqueue("progress", Report(20)); store.Acknowledge(old.Id);
    Assert(store.Peek()?.Report.PositionTicks == 20, "In-flight acknowledgment deleted fresh progress"); return Task.CompletedTask;
});
await Test("Reprise locale et séparation des comptes", () =>
{
    var store = Store("profile-a"); store.Save("items", new[] { new MediaItem { Id = "item-1", Name = "Film" } }); store.Enqueue("progress", Report(300));
    var items = store.Load<MediaItem[]>("items")!; store.ApplyLocalProgress(items); Assert(items[0].UserData.PlaybackPositionTicks == 300, "Local resume not applied");
    Assert(Store("profile-b").Load<MediaItem[]>("items") is null, "Cross-profile cache leak"); return Task.CompletedTask;
});
await Test("Un serveur lent ne bloque pas la sauvegarde de progression", async () =>
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async _ => { await gate.Task; return new(HttpStatusCode.ServiceUnavailable); }));
    var store = Store("slow-server"); await using var sync = new SyncService(client, store);
    var watch = Stopwatch.StartNew(); await sync.RecordAsync("start", Report()); await sync.RecordAsync("progress", Report(55)); await sync.RecordAsync("stop", Report(55));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(1), "Saving progress waited for network"); Assert(store.PendingCount == 3, "Reports not durable before response");
    gate.SetResult(); await WaitUntil(() => sync.Error is not null); Assert(store.PendingCount == 3, "Failed report was deleted");
});
await Test("Coupure puis reconnexion : livraison ordonnée sans perte", async () =>
{
    var online = false; var received = new List<string>();
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(request =>
    { if (!online) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); received.Add(request.RequestUri!.AbsolutePath); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }));
    var store = Store("recovery"); await using var sync = new SyncService(client, store);
    await sync.RecordAsync("start", Report()); await sync.RecordAsync("progress", Report(88)); await sync.RecordAsync("stop", Report(90));
    Assert(store.PendingCount == 3, "Offline reports missing"); online = true; await sync.FlushAsync(); await WaitUntil(() => store.PendingCount == 0);
    Assert(received.SequenceEqual(new[] { "/Sessions/Playing", "/Sessions/Playing/Progress", "/Sessions/Playing/Stopped" }), "Protocol ordering incorrect");
});
await Test("Fin de lecture locale : au-delà de 90 %, titre vu et reprise à zéro", () =>
{
    var store = Store("finished"); var runtime = 1_000 * TimeSpan.TicksPerSecond;
    store.Enqueue("stop", Report(runtime) with { ItemId = "done" }); store.Enqueue("stop", Report(runtime / 2) with { ItemId = "half" });
    var items = new[] { new MediaItem { Id = "done", RunTimeTicks = runtime }, new MediaItem { Id = "half", RunTimeTicks = runtime } };
    store.ApplyLocalProgress(items);
    Assert(items[0].UserData.Played && items[0].UserData.PlaybackPositionTicks == 0 && items[0].Progress == 0, "A title stopped at the credits resumes at the credits");
    Assert(!items[1].UserData.Played && items[1].UserData.PlaybackPositionTicks == runtime / 2, "Partial progress was lost");
    return Task.CompletedTask;
});
await Test("Générique terminé avant 90 % : vu et reprise effacée, même après redémarrage hors ligne", () =>
{
    var store = Store("completed-outro"); var runtime = 1_000 * TimeSpan.TicksPerSecond;
    store.Enqueue("start", Report()); store.Enqueue("complete", Report(runtime * 83 / 100));
    var reopened = Store("completed-outro");
    var item = new MediaItem { Id = "item-1", RunTimeTicks = runtime };
    reopened.ApplyLocalProgress([item]);
    Assert(item.UserData.Played && item.Progress == 0, "Credits completion below threshold was lost");
    var kinds = new List<string>();
    while (reopened.Peek() is { } entry) { kinds.Add(entry.Kind); reopened.Acknowledge(entry.Id); }
    Assert(kinds.SequenceEqual(new[] { "start", "stop", "watched" }), "Completion must atomically persist stop then watched");
    reopened.ForgetProgress(item.Id);
    var reset = item with { UserData = new() }; reopened.ApplyLocalProgress([reset]);
    Assert(!reset.UserData.Played && reset.Progress == 0, "Manual unwatched retains a local completion override");
    return Task.CompletedTask;
});
await Test("Marquage vu : échec puis reprise sans renvoyer l’arrêt déjà livré", async () =>
{
    var online = false; var stops = 0; var marks = 0;
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "u", "Alice", "token", "device"), new Handler(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("Stopped")) { Interlocked.Increment(ref stops); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
        Assert(request.Method == HttpMethod.Post && request.RequestUri.PathAndQuery == "/jellyfin/UserPlayedItems/item-1?userId=u", "Incorrect watched API request");
        if (online) Interlocked.Increment(ref marks);
        return Task.FromResult(new HttpResponseMessage(online ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable));
    }));
    var store = Store("watched-retry"); await using var sync = new SyncService(client, store);
    await sync.RecordAsync("complete", Report(830)); await WaitUntil(() => sync.Error is not null);
    Assert(stops == 1 && store.PendingCount == 1 && store.Peek()?.Kind == "watched", "Successful stop must not be retried");
    var item = new MediaItem { Id = "item-1", RunTimeTicks = 1000 }; store.ApplyLocalProgress([item]);
    Assert(item.UserData.Played && item.Progress == 0, "Waiting for watched request lost the local mark");
    online = true; await sync.FlushAsync(); await WaitUntil(() => store.PendingCount == 0);
    Assert(stops == 1 && marks == 1, "Watched retry duplicated the stop or lost the mark");
});
await Test("Synchronisation : un rapport refusé définitivement ne bloque pas la file", async () =>
{
    var received = new List<string>();
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(request =>
    {
        received.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(received.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.NoContent));
    }));
    var store = Store("permanent-refusal"); await using var sync = new SyncService(client, store);
    await sync.RecordAsync("start", Report()); await sync.RecordAsync("stop", Report(9));
    await WaitUntil(() => store.PendingCount == 0);
    Assert(received.Contains("/Sessions/Playing/Stopped") && sync.Discarded == 1 && sync.PendingCount == 0, "A 404 report blocked later deliveries");
});
await Test("Synchronisation : l’arrêt final part avant la fermeture", async () =>
{
    var received = new List<string>();
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async request =>
    { await Task.Delay(120); lock (received) received.Add(request.RequestUri!.AbsolutePath); return new HttpResponseMessage(HttpStatusCode.NoContent); }));
    var store = Store("final-stop"); var sync = new SyncService(client, store);
    await sync.RecordAsync("start", Report()); await sync.RecordAsync("stop", Report(12));
    await sync.DisposeAsync();
    Assert(received.Contains("/Sessions/Playing/Stopped") && store.PendingCount == 0, "Closing cancelled the stop report");
});
await Test("Session protégée par Windows, mot de passe absent du stockage", () =>
{
    var profile = new LocalProfile(Path.Combine(testRoot, "protected")); var connection = new Connection("http://localhost/", "user", "Alice", "secret-session-token", profile.DeviceId);
    profile.SaveConnection(connection); Assert(profile.LoadConnection() == connection, "Session round trip failed");
    var disk = File.ReadAllBytes(Path.Combine(profile.DirectoryPath, "session.protected")); Assert(!Encoding.UTF8.GetString(disk).Contains("secret-session-token"), "Token stored as plain text"); return Task.CompletedTask;
});
await Test("Progression bornée et libellés d’épisodes", () =>
{
    var item = new MediaItem { Id = "x", Name = "Le départ", SeriesName = "Les étoiles", Type = "Episode", ParentIndexNumber = 2, IndexNumber = 3, RunTimeTicks = 100, UserData = new() { PlaybackPositionTicks = 200 } };
    Assert(item.Progress == 1, "Progress overflow"); Assert(item.Subtitle.Contains("S02 · E03"), "Episode label missing"); return Task.CompletedTask;
});
await Test("Défilement : même distance à 30, 60 et 144 images/s", () =>
{
    var positions = new List<double>();
    foreach (var fps in new[] { 30, 60, 144 })
    {
        var motion = new ScrollMotion(); motion.Add(432, 1000);
        for (var frame = 0; frame < fps; frame++) motion.Step(.2 / fps, 1000);
        positions.Add(motion.Position);
        Assert(motion.Position > 300 && motion.Position < 432, "Scroll must interpolate rather than jump");
    }
    Assert(positions.Max() - positions.Min() < .001, "Frame rate changes the easing speed"); return Task.CompletedTask;
});
await Test("Défilement : inversion immédiate et bornes après changement du contenu", () =>
{
    var motion = new ScrollMotion(); motion.Reset(300); motion.Add(600, 1000); motion.Step(.04, 1000);
    var before = motion.Position; motion.Add(-108, 1000);
    Assert(motion.Step(.016, 1000) < before, "Reversing the wheel continues in the old direction");
    motion.Step(.1, 120); Assert(motion.Position <= 120 && motion.Target <= 120, "Shrinking content overscrolls");
    motion.Add(-1000, 120); motion.Step(10, 120); Assert(motion.Position == 0 && !motion.Moving, "Top boundary never settles");
    motion.Add(1000, 120); motion.Step(10, 120); Assert(motion.Position == 120 && !motion.Moving, "Bottom boundary never settles");
    return Task.CompletedTask;
});
await Test("Carrousel : progression, pause, reprise et changement manuel", () =>
{
    var timeline = new CarouselTimeline(8);
    Assert(!timeline.Advance(4, false) && timeline.Progress == .5, "Halfway marker is incorrect");
    Assert(!timeline.Advance(40, true) && timeline.Progress == .5, "Paused carousel advances");
    Assert(timeline.Advance(4, false) && timeline.Progress == 1, "Slide does not end after resumed duration");
    timeline.Reset(); Assert(timeline.Progress == 0 && !timeline.Advance(7.9, false), "Manual selection inherits previous duration");
    return Task.CompletedTask;
});
await Test("Préchargement : une seule requête partagée, données invalidées à l’actualisation", async () =>
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(async _ =>
    { Interlocked.Increment(ref count); await gate.Task; return JsonResponse(new MediaItem { Id = "series-1", Name = "Une série" }); }));
    var cache = new MetadataCache(client); var hover = cache.ItemAsync("series-1"); var detail = cache.ItemAsync("series-1");
    Assert(ReferenceEquals(hover, detail) && count == 1, "Hover and detail issue duplicate requests");
    gate.SetResult(); await Task.WhenAll(hover, detail); await cache.ItemAsync("series-1"); Assert(count == 1, "Warm detail refetches metadata");
    cache.Clear(); await cache.ItemAsync("series-1"); Assert(count == 2, "Invalidation retained stale metadata");
});
await Test("Préchargement : une erreur temporaire ne bloque pas la prochaine ouverture", async () =>
{
    var count = 0;
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ =>
    { count++; return Task.FromResult(count == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(new MediaItem { Id = "x", Name = "Rétabli" })); }));
    var cache = new MetadataCache(client);
    try { await cache.ItemAsync("x"); throw new Exception("Expected temporary HTTP failure"); } catch (HttpRequestException) { }
    Assert((await cache.ItemAsync("x")).Name == "Rétabli" && count == 2, "Faulted prefetch remains cached");
});
await Test("Couleur du bandeau : teintes différentes pour des images rouges et bleues", () =>
{
    byte[] Pixels(byte b, byte g, byte r) => Enumerable.Range(0, 64).SelectMany(_ => new[] { b, g, r, (byte)255 }).ToArray();
    var red = ArtworkPalette.Extract(Pixels(30, 40, 180)); var blue = ArtworkPalette.Extract(Pixels(180, 60, 30));
    Assert(red.R > red.B && blue.B > blue.R && red != blue, "Artwork colors share a fixed accent");
    Assert(Math.Min(red.R, Math.Min(red.G, red.B)) >= 175 && Math.Min(blue.R, Math.Min(blue.G, blue.B)) >= 175, "Tint loses legibility on the dark header");
    return Task.CompletedTask;
});
await Test("Couleur du bandeau : transparence et images neutres gardent un accent blanc", () =>
{
    foreach (var pixels in new[] { Array.Empty<byte>(), new byte[] { 0, 0, 0, 255, 255, 0, 0, 0 }, new byte[] { 110, 110, 110, 255, 250, 250, 250, 255 } })
        Assert(ArtworkPalette.Extract(pixels) == new RgbColor(245, 245, 247), "Neutral artwork introduces an arbitrary accent");
    return Task.CompletedTask;
});
await Test("Segments Jellyfin : types demandés, passages inconnus ou trop courts ignorés", async () =>
{
    Uri? requestUri = null;
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
    {
        requestUri = request.RequestUri;
        return Task.FromResult(JsonResponse(new { Items = new object[] {
            new { Type = "Outro", StartTicks = 1_300 * TimeSpan.TicksPerSecond, EndTicks = 1_390 * TimeSpan.TicksPerSecond },
            new { Type = "Intro", StartTicks = 60 * TimeSpan.TicksPerSecond, EndTicks = 150 * TimeSpan.TicksPerSecond },
            new { Type = "Unknown", StartTicks = 0L, EndTicks = 30 * TimeSpan.TicksPerSecond },
            new { Type = "Recap", StartTicks = 0L, EndTicks = TimeSpan.TicksPerSecond } }, TotalRecordCount = 4 }));
    }));
    var segments = PlaybackMarkers.FromJellyfin(await client.SegmentsAsync("episode 1"));
    Assert(requestUri!.AbsolutePath == "/jellyfin/MediaSegments/episode%201", "Segment path or escaping changed");
    foreach (var type in new[] { "Intro", "Outro", "Recap", "Preview", "Commercial" }) Assert(requestUri.Query.Contains("includeSegmentTypes=" + type), "Missing segment type " + type);
    Assert(segments.SequenceEqual(new[] { new SkipSegment(SkipKind.Intro, 60, 150), new SkipSegment(SkipKind.Outro, 1300, 1390) }), "Segments not filtered and ordered");
});
await Test("Chapitres nommés : opening, ending, récap, aperçu et générique selon sa position", () =>
{
    var chapters = new List<ChapterMark> { new(0, "Récapitulatif"), new(45, "OP"), new(135, "Partie A"), new(700, "Chapitre 4"), new(1290, "Ending"), new(1380, "Next Episode Preview"), new(1410, null) };
    var passages = PlaybackMarkers.FromChapters(chapters, 1420);
    Assert(passages.SequenceEqual(new[] { new SkipSegment(SkipKind.Recap, 0, 45), new SkipSegment(SkipKind.Intro, 45, 135), new SkipSegment(SkipKind.Outro, 1290, 1380), new SkipSegment(SkipKind.Preview, 1380, 1410) }), "Named chapters misclassified: " + string.Join(", ", passages));
    Assert(PlaybackMarkers.Classify("Générique", 30, 1400) == SkipKind.Intro && PlaybackMarkers.Classify("Générique", 1300, 1400) == SkipKind.Outro, "Bare credits title ignores its position");
    Assert(PlaybackMarkers.Classify("Avant-générique") is null && PlaybackMarkers.Classify("Top Gun") is null && PlaybackMarkers.Classify("Opening Credits") == SkipKind.Intro, "Cold open or ordinary titles are skippable");
    Assert(PlaybackMarkers.FromChapters(new List<ChapterMark> { new(0, "Opening"), new(600, "Épisode") }, 1400).Count == 0, "A ten-minute chapter is not an opening");
    return Task.CompletedTask;
});
await Test("Passages : segments serveur prioritaires, passage actif et fin vers l’épisode suivant", () =>
{
    var server = new List<SkipSegment> { new(SkipKind.Intro, 60, 150) };
    var guessed = new List<SkipSegment> { new(SkipKind.Intro, 50, 140), new(SkipKind.Outro, 1290, 1380), new(SkipKind.Preview, 1380, 1418) };
    var merged = PlaybackMarkers.Merge(server, guessed);
    Assert(merged.SequenceEqual(new[] { server[0], guessed[1], guessed[2] }), "Chapter guesses override the server");
    Assert(PlaybackMarkers.Active(merged, 59.8) == server[0] && PlaybackMarkers.Active(merged, 149.5) is null && PlaybackMarkers.Active(merged, 700) is null, "Active passage boundaries changed");
    Assert(PlaybackMarkers.EndsPlayback(merged, guessed[1], 1420) && !PlaybackMarkers.EndsPlayback(merged, server[0], 1420), "Credits followed by a preview should lead to the next episode");
    Assert(PlaybackMarkers.Label(SkipKind.Intro, true, false) == "Passer l’opening" && PlaybackMarkers.Label(SkipKind.Outro, false, false) == "Passer le générique" && PlaybackMarkers.Label(SkipKind.Outro, true, true) == "Épisode suivant", "Skip labels changed");
    return Task.CompletedTask;
});
if (args.Contains("--mpv-integration"))
{
    await Test("libmpv réel : flux HTTP authentifié, reprise, pause, seek et synchronisation", async () =>
    {
        using var client = new JellyfinClient(new("http://127.0.0.1:18096/", "mock-user", "Compte de test", "mira-test-token", "mpv-integration"));
        var catalog = await client.BrowseAsync(); Assert(catalog.Items.Count == 1, "Mock catalog not loaded");
        var info = await client.PlaybackAsync("mock-film"); var source = info.MediaSources.Single();
        using var mpv = new MpvEngine(IntPtr.Zero, new() { Volume = 0 }, headless: true);
        var loaded = false; var ended = false; string? error = null;
        mpv.FileLoaded += () => loaded = true; mpv.Ended += _ => ended = true; mpv.Error += text => error = text;
        mpv.Load(client.StreamUri("mock-film", source.Id).AbsoluteUri, 2, client.AuthorizationHeader);
        var wait = Stopwatch.StartNew(); while (!loaded && error is null && wait.Elapsed < TimeSpan.FromSeconds(10)) { mpv.Poll(); await Task.Delay(20); }
        Assert(loaded, "Authenticated mpv stream did not load: " + error);
        await Task.Delay(250); mpv.Poll(); Assert(mpv.Number("time-pos") >= 1.9, "Resume position ignored");
        mpv.Set("pause", "yes"); await Task.Delay(80); mpv.Poll(); Assert(mpv.Flag("pause"), "Pause failed");
        var position = mpv.Number("time-pos"); await Task.Delay(150); mpv.Poll(); Assert(Math.Abs(mpv.Number("time-pos") - position) < 0.1, "Playback progressed while paused");
        mpv.Command("seek", "8", "absolute+exact"); await Task.Delay(150); mpv.Poll(); Assert(Math.Abs(mpv.Number("time-pos") - 8) < 0.3, "Seek failed");
        Assert(mpv.Tracks().Any(t => t.Type == "audio"), "Audio track not exposed");
        var report = new PlaybackReport { ItemId = "mock-film", MediaSourceId = source.Id, PlaySessionId = info.PlaySessionId, PositionTicks = 2 * TimeSpan.TicksPerSecond };
        var store = Store("native-sync"); await using var sync = new SyncService(client, store);
        await sync.RecordAsync("start", report); await sync.RecordAsync("progress", report with { PositionTicks = 8 * TimeSpan.TicksPerSecond, IsPaused = true });
        await sync.RecordAsync("stop", report with { PositionTicks = 8 * TimeSpan.TicksPerSecond });
        await WaitUntil(() => store.PendingCount == 0);
        mpv.Command("stop"); await Task.Delay(50); mpv.Poll(); Assert(ended, "Stop event not observed");
    });
}
Console.WriteLine($"\n{passed} tests réussis.");

static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
static async Task WaitUntil(Func<bool> ready) { var timeout = DateTime.UtcNow.AddSeconds(4); while (!ready()) { if (DateTime.UtcNow > timeout) throw new TimeoutException(); await Task.Delay(20); } }
sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request).WaitAsync(cancellationToken); }
