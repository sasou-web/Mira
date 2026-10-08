using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Updates;
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
// Read-only probe of a real server through a validation profile's protected session: the library folders TorLink
// imports would use, and the lookup behind « Voir ». Only GET requests; nothing is reported to or changed on the server.
if (Array.IndexOf(args, "--jellyfin-live") is var liveArg and >= 0)
{
    if (liveArg + 1 >= args.Length || new LocalProfile(Path.GetFullPath(args[liveArg + 1])).LoadConnection() is not { } connection) { Console.WriteLine("Profil sans session protégée valide."); return; }
    using var client = new JellyfinClient(connection);
    var folders = await client.VirtualFoldersAsync();
    Console.WriteLine(folders is null ? "Dossiers refusés : compte non administrateur." : $"{folders.Count} bibliothèque(s) : " + string.Join(" · ", folders.Select(x => $"{x.Name} [{x.CollectionType}] {x.Locations.Length} dossier(s)")));
    var libraries = MediaLibraries.FromJellyfin(folders);
    string Describe(string? path) => path is null ? "non trouvé" : Directory.Exists(path) ? "dossier présent sur ce PC" : "dossier absent de ce PC";
    Console.WriteLine($"Films : {Describe(libraries.Movies)} · Séries : {Describe(libraries.Series)} · Animes : {Describe(libraries.Anime)}");
    var newest = libraries.Movies is { } films && Directory.Exists(films)
        ? Directory.EnumerateFiles(films, "*", SearchOption.AllDirectories).Where(ReleaseName.IsVideo).OrderByDescending(File.GetCreationTimeUtc).FirstOrDefault() : null;
    Console.WriteLine(newest is null ? "Aucune vidéo de film à rechercher." : (await client.FindIndexedAsync([newest])).Count > 0
        ? "La vidéo de film la plus récente est retrouvée dans Jellyfin par son chemin exact." : "La vidéo de film la plus récente n’est pas parmi les 100 derniers ajouts indexés.");
    return;
}
// Loopback stand-in for GitHub's release list, for tools/update-check.ps1: serves the files of one folder as release v<version>.
//   Mira.Tests --update-server <folder> <port> <version>     (stops after 15 minutes, or when killed)
if (Array.IndexOf(args, "--update-server") is var serverArg and >= 0)
{
    var folder = Path.GetFullPath(args[serverArg + 1]); var port = int.Parse(args[serverArg + 2]); var version = args[serverArg + 3];
    var origin = $"http://127.0.0.1:{port}";
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port); listener.Start();
    Console.WriteLine($"Flux de mises à jour sur {origin}/releases pour {folder}");
    using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    while (!lifetime.IsCancellationRequested)
    {
        System.Net.Sockets.TcpClient connection;
        try { connection = await listener.AcceptTcpClientAsync(lifetime.Token); } catch (OperationCanceledException) { break; }
        _ = Task.Run(async () =>
        {
            using var client = connection; using var stream = client.GetStream();
            var header = new StringBuilder(); var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(buffer) == 1) header.Append((char)buffer[0]);
            var target = header.ToString().Split(' ').ElementAtOrDefault(1) ?? "/";
            byte[] body; var type = "application/octet-stream"; var status = "200 OK";
            if (target.StartsWith("/releases", StringComparison.Ordinal))
            {
                var assets = Directory.EnumerateFiles(folder).Select(f => new FileInfo(f))
                    .Select(f => new { name = f.Name, size = f.Length, browser_download_url = $"{origin}/files/{Uri.EscapeDataString(f.Name)}" }).ToArray();
                body = JsonSerializer.SerializeToUtf8Bytes(new[] { new { tag_name = "v" + version, draft = false, prerelease = true, html_url = $"{origin}/release", assets } });
                type = "application/json";
            }
            else if (target.StartsWith("/files/", StringComparison.Ordinal) && Uri.UnescapeDataString(target[7..]) is var name && !name.Contains('/') && !name.Contains('\\') && File.Exists(Path.Combine(folder, name)))
                body = await File.ReadAllBytesAsync(Path.Combine(folder, name));
            else { body = Encoding.UTF8.GetBytes("introuvable"); status = "404 Not Found"; type = "text/plain"; }
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(head); await stream.WriteAsync(body); } catch (IOException) { }
            Console.WriteLine($"{status[..3]} {target}");
        });
    }
    listener.Stop();
    return;
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
await Test("Adresse saisie : sans http://, avec port, ou copiée depuis la page web de Jellyfin", () =>
{
    void Expect(string input, params string[] expected)
    { var actual = ServerAddress.Candidates(input); Assert(actual.SequenceEqual(expected), $"{input} → {string.Join(" ; ", actual)}"); }
    Expect("192.168.1.20", "https://192.168.1.20/", "http://192.168.1.20:8096/", "http://192.168.1.20/");
    Expect(" nas:8096 ", "http://nas:8096/", "https://nas:8096/");
    Expect("jellyfin.example.test:8920", "https://jellyfin.example.test:8920/", "http://jellyfin.example.test:8920/");
    Expect("example.test/jellyfin", "https://example.test/jellyfin/", "http://example.test:8096/jellyfin/", "http://example.test/jellyfin/");
    Expect("http://192.168.1.20:8096/web/#/home.html", "http://192.168.1.20:8096/");
    Expect("https://example.test/jellyfin/web/index.html#!/details?id=1", "https://example.test/jellyfin/");
    Expect("localhost:8096/web", "http://localhost:8096/", "https://localhost:8096/");
    Expect("http://web:8096/", "http://web:8096/");
    Expect("[::1]:8096", "http://[::1]:8096/", "https://[::1]:8096/");
    foreach (var bad in new[] { "", "   ", "http://user:secret@localhost", "ftp://example.test" })
    { try { ServerAddress.Candidates(bad); throw new Exception("Accepted invalid address: " + bad); } catch (ArgumentException) { } }
    Assert(ServerAddress.ParseVersion("10.11.0-rc2") == new Version(10, 11, 0) && ServerAddress.ParseVersion("inconnue") is null, "Server version parsing");
    return Task.CompletedTask;
});
await Test("Recherche du serveur : première adresse Jellyfin dans l’ordre, autres serveurs et versions anciennes refusés", async () =>
{
    HttpResponseMessage Info(string version = "10.10.3", string product = "Jellyfin Server") => JsonResponse(new { ServerName = "Salon", Version = version, ProductName = product, Id = "server-1" });
    Handler Serve(Func<Uri, HttpResponseMessage> answer) => new(request => Task.FromResult(answer(request.RequestUri!)));
    var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
    // The HTTPS port is closed and port 80 is a router page: the server is on 8096.
    var found = await ServerAddress.DiscoverAsync("nas", Serve(uri =>
    {
        asked.Add(uri.AbsoluteUri);
        return uri.Scheme == "https" ? throw new HttpRequestException("refusé") : uri.Port == 8096 ? Info() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>routeur</html>") };
    }));
    Assert(found == new ServerInfo("http://nas:8096/", "Salon", "10.10.3"), "Wrong server: " + found);
    Assert(asked.All(x => x.EndsWith("/System/Info/Public")), "Something other than the public information was requested");
    // Both answer: HTTPS wins.
    Assert((await ServerAddress.DiscoverAsync("nas", Serve(_ => Info()))).Address == "https://nas/", "HTTPS not preferred");
    // HTTPS never answers (a firewall dropping it): port 8096 is used after a short grace, not the whole timeout.
    var clock = Stopwatch.StartNew();
    var silent = await ServerAddress.DiscoverAsync("nas", new Handler(async request =>
    {
        if (request.RequestUri!.Scheme == "https") await Task.Delay(Timeout.Infinite);
        return request.RequestUri.Port == 8096 ? Info() : new HttpResponseMessage(HttpStatusCode.NotFound);
    }));
    Assert(silent.Address == "http://nas:8096/" && clock.Elapsed < ServerAddress.ProbeTimeout - TimeSpan.FromSeconds(2), $"Waited {clock.Elapsed} for {silent.Address}");
    // A proxy redirecting to HTTPS: the final address is kept.
    var redirected = await ServerAddress.DiscoverAsync("http://example.test/jellyfin", Serve(uri =>
    { var response = Info(); response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/jellyfin/System/Info/Public"); return response; }));
    Assert(redirected.Address == "https://example.test/jellyfin/", "Redirected address not kept: " + redirected.Address);
    async Task Refused(string input, Handler handler, string expected)
    {
        try { await ServerAddress.DiscoverAsync(input, handler); throw new Exception("Accepted: " + input); }
        catch (ServerDiscoveryException ex) { Assert(ex.Message.Contains(expected), $"Unexpected message for {input}: {ex.Message}"); }
    }
    await Refused("http://nas:8096", Serve(_ => Info("4.8.10", "Emby Server")), "pas un serveur Jellyfin");
    await Refused("http://nas:8096", Serve(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "pas un serveur Jellyfin");
    await Refused("http://nas:8096", Serve(_ => Info("10.8.13")), "10.8.13");
    await Refused("https://nas", Serve(_ => throw new HttpRequestException("TLS", new System.Security.Authentication.AuthenticationException())), "certificat");
    await Refused("nas", Serve(_ => throw new HttpRequestException("refusé")), "Aucun serveur Jellyfin");
});
await Test("Écoute de Jellyfin : la session part dans l’en-tête, jamais dans l’adresse (Jellyfin 10.11 et suivants)", async () =>
{
    // Jellyfin 10.11+ answers 403 "Token is required" to /socket?api_key=… unless legacy authorization is on.
    using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
    string? url = null, authorization = null;
    var serving = Task.Run(async () =>
    {
        var context = await server.GetContextAsync();
        url = context.Request.RawUrl; authorization = context.Request.Headers["Authorization"];
        var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"MessageType\":\"LibraryChanged\",\"Data\":{}}"), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
        await Task.Delay(500);
    });
    using var client = new JellyfinClient(new Connection($"http://127.0.0.1:{port}/", "user-1", "Alice", "jeton-secret", "device-1"));
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var changed = 0;
    var listening = client.ListenAsync(() => { changed++; stop.Cancel(); }, stop.Token);
    try { await listening; } catch (OperationCanceledException) { }
    await serving.WaitAsync(TimeSpan.FromSeconds(5)); server.Stop();
    Assert(changed == 1, "The library change was not received");
    Assert(url == "/socket" && authorization is { } header && header.Contains("Token=\"jeton-secret\"") && header.Contains("Client=\"Mira\""), $"Socket request: {url} | {authorization}");
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
await Test("Hors de chez toi : adresse Tailscale de ce PC, et Jellyfin qui accepte ses appareils sans s’ouvrir à Internet", async () =>
{
    Assert(new[] { "100.64.0.1", "100.101.102.103", "100.127.255.254" }.All(LocalNetwork.IsTailnet) && !new[] { "100.63.255.255", "100.128.0.1", "192.168.1.20", "fd7a:115c:a1e0::1", "x" }.Any(LocalNetwork.IsTailnet), "Tailscale's range");
    Assert(LocalNetwork.TailnetIPv4([("100.70.0.9", "Ethernet Realtek PCIe"), ("192.168.1.20", "Tailscale Tailscale Tunnel"), ("100.101.102.103", "Tailscale Tailscale Tunnel")]) == "100.101.102.103", "Only an address of the Tailscale adapter counts");
    Assert(LocalNetwork.TailnetIPv4([("100.70.0.9", "Ethernet Realtek PCIe")]) is null, "A 100.64 address from the Internet provider is not Tailscale");
    Assert(LocalNetwork.ForOtherDevices("http://127.0.0.1:8096", "100.101.102.103") == "http://100.101.102.103:8096", "Address on Tailscale");
    Assert(LocalNetwork.WithTailnet([]).SequenceEqual(LocalNetwork.JellyfinDefaultSubnets.Append(LocalNetwork.TailnetRange)), "Jellyfin's defaults kept, Tailscale added");
    Assert(LocalNetwork.WithTailnet(["192.168.1.0/24"]).SequenceEqual(["192.168.1.0/24", LocalNetwork.TailnetRange]) && LocalNetwork.WithTailnet(["100.64.0.0/10"]).Count == 1, "A chosen list is kept; no duplicate");

    // Jellyfin's network settings, read then written back whole with only the local networks changed.
    var stored = """{"EnableRemoteAccess":false,"LocalNetworkSubnets":[],"KnownProxies":["10.0.0.1"],"EnableHttps":false,"InternalHttpPort":8096}""";
    var admin = true; string? posted = null;
    using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
    {
        if (!admin && request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (request.Method == HttpMethod.Post) { stored = posted = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stored, Encoding.UTF8, "application/json") };
    }));
    Assert(await client.TailnetAllowedAsync() == false, "Tailscale allowed before being asked");
    Assert(await client.AllowTailnetAsync() && await client.TailnetAllowedAsync() == true, "Tailscale not allowed after being asked");
    var sent = JsonDocument.Parse(posted!).RootElement;
    Assert(sent.GetProperty("LocalNetworkSubnets").EnumerateArray().Select(x => x.GetString()).SequenceEqual(LocalNetwork.WithTailnet([])) && !sent.GetProperty("EnableRemoteAccess").GetBoolean()
        && sent.GetProperty("KnownProxies")[0].GetString() == "10.0.0.1" && sent.GetProperty("InternalHttpPort").GetInt32() == 8096, "Other network settings changed: " + posted);
    stored = """{"EnableRemoteAccess":true,"LocalNetworkSubnets":[]}""";
    Assert(await client.TailnetAllowedAsync() == true, "Access from elsewhere already on lets Tailscale in");
    admin = false; stored = """{"EnableRemoteAccess":false,"LocalNetworkSubnets":[]}""";
    Assert(!await client.AllowTailnetAsync(), "A guest changed Jellyfin's network settings");
});
await Test("Continuer à regarder : un titre retiré disparaît jusqu’à sa prochaine lecture, et sa reprise est remise à zéro", async () =>
{
    var removed = DateTimeOffset.UtcNow.AddHours(-1);
    MediaItem Film(string id, DateTimeOffset? played) => new() { Id = id, Name = id, Type = "Movie", UserData = new() { PlaybackPositionTicks = 600_000_000, LastPlayedDate = played } };
    MediaItem Next(string id, string series) => new() { Id = id, Name = id, Type = "Episode", SeriesId = series, SeriesName = "Show", UserData = new() };
    var row = new List<MediaItem> { Film("dropped", removed.AddDays(-2)), Next("e4", "show"), Film("kept", removed.AddDays(-1)) };
    var hidden = new Dictionary<string, DateTimeOffset> { ["dropped"] = removed, ["show"] = removed };
    Assert(ContinueWatching.WithoutHidden(row, hidden, []).Select(x => x.Id).SequenceEqual(["kept"]), "A removed film, or the next episode of a removed series, stayed in the row");
    Assert(ContinueWatching.WithoutHidden(row, new Dictionary<string, DateTimeOffset>(), []).Count == 3, "Nothing removed, everything shown");
    var playedAgain = new List<MediaItem> { new() { Id = "e3", Type = "Episode", SeriesId = "show", UserData = new() { LastPlayedDate = removed.AddMinutes(5) } } };
    Assert(ContinueWatching.WithoutHidden(row, hidden, playedAgain).Select(x => x.Id).SequenceEqual(["e4", "kept"]), "A series played again since did not come back");
    Assert(ContinueWatching.WithoutHidden([Film("dropped", removed.AddMinutes(1))], hidden, []).Count == 1, "A film resumed elsewhere since did not come back");

    // Kept per account, beyond the 30 days after which cached pages are forgotten; « Annuler » puts it back.
    var store = Store("resume-hidden");
    store.HideFromResume("dropped", removed); store.HideFromResume("show", removed);
    store.Prune(DateTimeOffset.UtcNow.AddDays(31));
    Assert(store.HiddenFromResume() is { Count: 2 } kept && kept["show"] == removed, "The removed titles were forgotten with the cache");
    store.HideFromResume("show", null);
    Assert(store.HiddenFromResume().Keys.SequenceEqual(["dropped"]), "« Annuler » did not put the series back");

    // On Jellyfin: only the resume point changes (its other user data are kept, checked on 12.1).
    string? sent = null;
    using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
    {
        sent = request.Method + " " + request.RequestUri!.PathAndQuery + " " + await request.Content!.ReadAsStringAsync();
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    await client.SetPlaybackPositionAsync("film 1", -5);
    Assert(sent == "POST /UserItems/film%201/UserData?userId=user-1 {\"PlaybackPositionTicks\":0}", "Request: " + sent);
});
await Test("Fiche : un acteur ou un réalisateur mène à ses titres de la bibliothèque, avec les autres filtres", async () =>
{
    var item = new MediaItem { Id = "film", Name = "Aube Claire", Type = "Movie", People =
    [
        new() { Id = "p1", Name = " Jeanne Essai ", Type = "Actor" }, new() { Id = "p1b", Name = "jeanne essai", Type = "Actor" },
        new() { Name = "Sans Fiche", Type = "Actor" }, new() { Id = "d1", Name = "Réa Lisatrice", Type = "Director" }, new() { Id = "w1", Name = "Scé Nariste", Type = "Writer" }
    ] };
    Assert(item.CastPeople.Select(x => (x.Id, x.Name)).SequenceEqual([("p1", "Jeanne Essai"), ((string?)null, "Sans Fiche")]) && item.Cast.SequenceEqual(["Jeanne Essai", "Sans Fiche"]), "Cast: names trimmed, one per name, ids kept");
    Assert(item.DirectorPeople.Single() is { Id: "d1", Name: "Réa Lisatrice" }, "Director");
    var query = new CatalogQuery(Year: 2002, Sort: "title", PersonId: "p 1");
    Assert(query.Parameters.Contains("&personIds=p%201") && query.Parameters.Contains("&years=2002") && query.CacheKey != (query with { PersonId = null }).CacheKey, "Person filter: " + query.Parameters);
    Assert(!new CatalogQuery().Parameters.Contains("personIds"), "No person, no filter");
    Uri? requested = null;
    using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request => { requested = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = Array.Empty<object>(), TotalRecordCount = 0 })); }));
    await client.BrowseAsync(filters: new CatalogQuery(PersonId: "d1"));
    Assert(requested!.Query.Contains("personIds=d1") && requested.Query.Contains("includeItemTypes=Movie,Series"), "Request: " + requested);
    // Checked on Jellyfin 12.1 with actors and a director read from .nfo files: the people come with their id, and
    // personIds returns exactly their films, also with a year and a sort.
});
await Test("Fenêtre : taille normale sur un grand écran, réduite et jamais agrandie sur un petit", () =>
{
    ScreenFit.Fit? Open(double width, double height) => ScreenFit.Window(new(1480, 930), new(960, 600), new(width, height));
    Assert(Open(2560, 1392) is { Size: { Width: 1480, Height: 930 }, Minimum: { Width: 960, Height: 600 } }, "A large screen keeps the default size");
    Assert(Open(1920, 1032) is { Size: { Width: 1480, Height: 928 } }, "1080p at 100 %: 930 px do not fit in 90 % of 1032");
    Assert(Open(1536, 826) is { Size: { Width: 1382, Height: 743 } }, "1080p at 125 %: 90 % of the screen");
    Assert(Open(1280, 688) is { Size: { Width: 1152, Height: 619 } }, "1080p at 150 %: 90 % of the screen, above the minimum height");
    Assert(Open(900, 560) is { Size: { Width: 900, Height: 560 }, Minimum: { Width: 900, Height: 560 } }, "A screen smaller than the minimum lowers the minimum to the screen");
    Assert(Open(0, 0) is null, "An unknown screen changes nothing");
    // The size remembered from the last session: kept when it fits, never larger than the screen nor below the minimum.
    Assert(ScreenFit.Window(new(1700, 1000), new(960, 600), new(1920, 1032)) is { Size: { Width: 1700, Height: 928 } }, "A remembered size taller than the screen");
    Assert(ScreenFit.Window(new(500, 300), new(960, 600), new(1920, 1032)) is { Size: { Width: 960, Height: 600 } }, "A remembered size below the minimum");
    return Task.CompletedTask;
});
await Test("Actualiser : Jellyfin analyse les dossiers, avancement suivi jusqu’à la fin, refus d’un compte non administrateur", async () =>
{
    // The scan task as Jellyfin lists it: Idle with the end of the last scan, then Running with its progress.
    object ScanTask(string state, double? progress, string ended) => new[] { new { Key = "RefreshLibrary", State = state, CurrentProgressPercentage = progress, LastExecutionResult = new { EndTimeUtc = ended } } };
    async Task<(bool Scanned, List<double?> Seen, List<string> Calls)> Run(Queue<object> states, bool admin = true, int startLimitMs = 2000)
    {
        var seen = new List<double?>(); var calls = new List<string>(); object last = states.Peek();
        using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            if (!admin) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (request.Method == HttpMethod.Post) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            if (states.Count > 0) last = states.Dequeue();
            return Task.FromResult(JsonResponse(last));
        }));
        var scanned = await client.ScanAndWaitAsync(new SyncProgress<double?>(seen.Add), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(startLimitMs));
        return (scanned, seen, calls);
    }
    var followed = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z"), ScanTask("Running", 40, "2026-10-01T10:00:00Z"), ScanTask("Running", 80, "2026-10-01T10:00:00Z"), ScanTask("Idle", null, "2026-10-02T09:00:00Z")]));
    Assert(followed.Scanned && followed.Seen.SequenceEqual([null, .4, .8]) && followed.Calls.Count(x => x == "POST /Library/Refresh") == 1, "Followed scan: " + string.Join(", ", followed.Seen) + " · " + string.Join(", ", followed.Calls));
    var quick = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z"), ScanTask("Idle", null, "2026-10-02T09:00:00Z")]));
    Assert(quick.Scanned && quick.Seen.SequenceEqual([(double?)null]), "A scan over before the first look was not seen ending");
    var refused = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z")]), admin: false);
    Assert(!refused.Scanned && refused.Seen.Count == 0, "A non-administrator was told the folders were scanned");
    var stuck = await Run(new([ScanTask("Idle", null, "2026-10-01T10:00:00Z")]), startLimitMs: 60);
    Assert(stuck.Scanned && stuck.Calls.Count < 200, "A scan that never starts is waited for: " + stuck.Calls.Count + " requests");
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
await Test("Fiche : distribution, réalisation, titres similaires et tri par dernière lecture", async () =>
{
    var film = JsonSerializer.Deserialize<MediaItem>("""
        {"Id":"f","Name":"Film","People":[{"Name":"Ana","Role":"Mia","Type":"Actor"},{"Name":"Ben","Type":"Director"},{"Name":" ana ","Type":"Actor"},
        {"Name":"Chloé","Type":"Writer"},{"Name":"Dan","Type":"actor"},{"Name":"","Type":"Actor"}]}
        """, Json.Options)!;
    Assert(film.Cast.SequenceEqual(["Ana", "Dan"]) && film.Directors.SequenceEqual(["Ben"]), $"Credits: {string.Join(", ", film.Cast)} / {string.Join(", ", film.Directors)}");
    // Pages cached before people were read still load.
    Assert(JsonSerializer.Deserialize<ItemsResult>("""{"Items":[{"Id":"old","Name":"Ancien"}]}""", Json.Options)!.Items[0].Cast.Length == 0, "Old cached page");
    Assert(new CatalogQuery(Sort: "played").Parameters.Contains("sortBy=DatePlayed,SortName&sortOrder=Descending"), "Last played sort");
    Uri? asked = null;
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(request =>
    { asked = request.RequestUri; return Task.FromResult(JsonResponse(new { Items = new[] { new { Id = "g", Name = "Autre" } }, TotalRecordCount = 1 })); }));
    var similar = await client.SimilarAsync("film 1");
    Assert(similar.Items.Single().Id == "g" && asked!.AbsolutePath == "/jellyfin/Items/film%201/Similar" && asked.Query.Contains("userId=user-1") && asked.Query.Contains("limit=12"), "Similar request: " + asked);
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
await Test("Stockage local en panne : rien ne remonte au lecteur, l’erreur est affichée et la fermeture ne bloque pas", async () =>
{
    var folder = Path.Combine(testRoot, "broken-storage"); var store = new LibraryStore(folder, "panne");
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))));
    var sync = new SyncService(client, store);
    // Then the database becomes unopenable (a folder where the file was): every SQLite call fails from now on.
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var database = Directory.GetFiles(folder, "library-*.db").Single();
    foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
    Directory.CreateDirectory(database);
    await new SyncService(client, store).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    await sync.RecordAsync("start", Report()).WaitAsync(TimeSpan.FromSeconds(5));
    await sync.RecordAsync("progress", Report(40)).WaitAsync(TimeSpan.FromSeconds(5));
    await sync.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert(sync.Error == SyncService.StorageError, "Storage failure not reported: " + sync.Error);
    // The flush lock must be released even though counting failed, or closing Mira waits for it forever.
    await sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
});
await Test("Cache de bibliothèque illisible : mis de côté et recréé, le compte s’ouvre", () =>
{
    var folder = Path.Combine(testRoot, "damaged-store");
    var first = new LibraryStore(folder, "abîmé"); first.Save("home", new ItemsResult { TotalRecordCount = 1 });
    Assert(first.DamagedCopy is null, "An intact database was set aside");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var database = Directory.GetFiles(folder, "library-*.db").Single();
    foreach (var journal in new[] { database + "-wal", database + "-shm" }) if (File.Exists(journal)) File.Delete(journal);
    File.WriteAllText(database, "Pas une base SQLite : un fichier coupé par une panne de courant, qui ne commence pas par l’en-tête attendu.");
    var store = new LibraryStore(folder, "abîmé");
    store.Save("home", new ItemsResult { TotalRecordCount = 2 }); store.Enqueue("start", Report());
    Assert(store.Load<ItemsResult>("home") is { TotalRecordCount: 2 } && store.PendingCount == 1, "The recreated database does not work");
    Assert(store.DamagedCopy is { } copy && File.Exists(copy) && File.ReadAllText(copy).StartsWith("Pas une base"), "The damaged file was not kept aside");
    return Task.CompletedTask;
});
await Test("Moteur mpv : téléchargé, vérifié et extrait dans le profil ; altéré ou incomplet, jamais installé", async () =>
{
    // A small archive shaped like mpv's Windows builds: libmpv-2.dll and its headers.
    var archive = Convert.FromBase64String("N3q8ryccAAQSc70DwgAAAAAAAAAiAAAAAAAAABwWbsgBADQvKiBlbi10ZXRlICovCmZhdXggbW90ZXVyIG1wdiBwb3VyIGxlcyB0ZXN0cyBkZSBNaXJhCgAAAIEzB64P0YkKnKCQoHdexUAYjoQJNqTpTtdkwHH/AlkW0BAA/Bkz01eGX3Anuuait2gpZTXZf9jAC7J3/dXvw+tz35fkNTgXZ9k+YJs57syaM+RclR0pc/nBp46uaTmsVW1bYGi/WuClG7sn7CLBB8/0KodMIp0c0SrMXAIplkuFPaS65AAAABcGOQEJgIkABwsBAAEjAwEBBV0AEAAADIDWCgF5nvznAAA=");
    MpvPackage Package(string? libraryHash = null) => new("test-1", new("https://downloads.example.test/mpv/mpv-dev-test.7z"), archive.Length,
        "9ec0036370600d637114441debcdd6ba663a3b295264dff5240c28557c7f934d", libraryHash ?? "7306c02a18fb46f612ba938a15f9e7b8c0313c1f1e1eb67ead4565729a7403c2");
    var requests = 0; var served = archive; var status = HttpStatusCode.OK;
    var handler = new Handler(_ => { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(served) }); });
    var profile = Path.Combine(testRoot, "engine");
    var path = await MpvInstaller.InstallAsync(profile, Package(), handler);
    Assert(path == MpvInstaller.LibraryPath(profile) && File.ReadAllText(path) == "faux moteur mpv pour les tests de Mira\n" && MpvInstaller.IsInstalled(profile, Package()), "Engine not installed");
    var kept = Directory.GetFiles(MpvInstaller.Folder(profile)).Select(Path.GetFileName).Order().ToArray();
    Assert(kept.SequenceEqual(new[] { "libmpv-2.dll", "SOURCE.txt", "version.txt" }.Order()), "Unexpected files: " + string.Join(", ", kept));
    await MpvInstaller.InstallAsync(profile, Package(), handler);
    Assert(requests == 1 && !MpvInstaller.IsInstalled(profile, Package() with { Version = "test-2" }), "Installed engine downloaded again, or another version taken for it");
    async Task Refused(string folder, MpvPackage package, string expected)
    {
        var target = Path.Combine(testRoot, folder);
        try { await MpvInstaller.InstallAsync(target, package, handler); throw new Exception("Installed: " + folder); }
        catch (InstallException ex) { Assert(ex.Message.Contains(expected), $"{folder}: {ex.Message}"); }
        Assert(!MpvInstaller.IsInstalled(target, package) && !Directory.EnumerateFiles(MpvInstaller.Folder(target)).Any(), folder + " left files behind");
    }
    served = archive.ToArray(); served[^8] ^= 0xFF;
    await Refused("engine-altered", Package(), "ne correspond pas à la version vérifiée");
    served = [.. archive, 0, 0, 0];
    await Refused("engine-longer", Package(), "n’est pas celui attendu");
    served = archive;
    await Refused("engine-library", Package(new string('0', 64)), "extrait ne correspond pas");
    status = HttpStatusCode.NotFound;
    await Refused("engine-missing", Package(), "404");
    var current = MpvPackage.Current;
    Assert(current.Url.Scheme == "https" && current.ArchiveName.EndsWith(".7z") && current.ArchiveSha256.Length == 64 && current.LibrarySha256.Length == 64 && current.Size > 1_000_000, "Pinned engine");
});
await Test("TorLink : Node.js installé par Mira depuis le zip officiel vérifié, TorLink figé et installé par npm sans scripts", async () =>
{
    // A zip shaped like nodejs.org's: one top folder, node.exe, npm, corepack and scripts Mira leaves out.
    byte[] Zip(Action<System.IO.Compression.ZipArchive> fill)
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true)) fill(zip);
        return memory.ToArray();
    }
    void Entry(System.IO.Compression.ZipArchive zip, string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
    var good = Zip(zip =>
    {
        foreach (var (name, text) in new[] { ("node.exe", "faux node"), ("LICENSE", "MIT"), ("node_modules/npm/bin/npm-cli.js", "// npm"), ("node_modules/npm/package.json", "{}"),
            ("node_modules/corepack/package.json", "{}"), ("npm.cmd", "@echo off"), ("install_tools.bat", "@echo off") })
            Entry(zip, "node-v24.0.0-win-x64/" + name, text);
    });
    string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    NodePackage Package(byte[] bytes) => new("24.0.0", new("https://nodejs.example.test/dist/v24.0.0/node-v24.0.0-win-x64.zip"), bytes.Length, Sha(bytes));
    var served = good; var requests = 0;
    var handler = new Handler(_ => { Interlocked.Increment(ref requests); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) }); });
    var profile = Path.Combine(testRoot, "node-profile");
    var node = await NodeInstaller.InstallAsync(profile, Package(good), handler);
    Assert(node == NodeInstaller.Executable(profile) && File.ReadAllText(node) == "faux node" && File.Exists(NodeInstaller.NpmCli(profile)) && NodeInstaller.IsInstalled(profile, Package(good)), "Node.js not installed");
    var kept = Directory.EnumerateFiles(NodeInstaller.Folder(profile), "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(NodeInstaller.Folder(profile), x).Replace('\\', '/')).Order().ToArray();
    Assert(kept.SequenceEqual(new[] { "LICENSE", "node.exe", "node_modules/npm/bin/npm-cli.js", "node_modules/npm/package.json", "SOURCE.txt", "version.txt" }.Order()), "Unexpected files: " + string.Join(", ", kept));
    await NodeInstaller.InstallAsync(profile, Package(good), handler);
    Assert(requests == 1 && !Directory.EnumerateFileSystemEntries(profile).Any(x => x.EndsWith(".partial")), "Installed Node.js downloaded again, or a partial file left");
    async Task Refused(string folder, NodePackage package, byte[] bytes, string expected)
    {
        served = bytes; var target = Path.Combine(testRoot, folder);
        try { await NodeInstaller.InstallAsync(target, package, handler); throw new Exception("Installed: " + folder); }
        catch (InstallException ex) { Assert(ex.Message.Contains(expected), $"{folder}: {ex.Message}"); }
        Assert(!File.Exists(NodeInstaller.Executable(target)) && !Directory.Exists(NodeInstaller.Folder(target) + ".partial"), folder + " left Node.js behind");
    }
    var altered = good.ToArray(); altered[^30] ^= 0xFF;
    await Refused("node-altered", Package(good), altered, "ne correspond pas à la version vérifiée");
    var escape = Zip(zip => { Entry(zip, "node-v24.0.0-win-x64/node.exe", "x"); Entry(zip, "node-v24.0.0-win-x64/node_modules/npm/../../../../evil.txt", "x"); Entry(zip, "node-v24.0.0-win-x64/node_modules/npm/bin/npm-cli.js", "x"); });
    await Refused("node-escape", Package(escape), escape, "chemin inattendu");
    Assert(!File.Exists(Path.Combine(testRoot, "evil.txt")), "A file was written outside Node.js's folder");
    var empty = Zip(zip => Entry(zip, "autre/README.md", "x"));
    await Refused("node-empty", Package(empty), empty, "ne contient pas Node.js");
    Assert(NodeInstaller.MajorVersion("24.21.0") == 24 && NodeInstaller.MajorVersion("v22.15.0") == 22 && NodeInstaller.MajorVersion("") is null && NodeInstaller.MajorVersion(null) is null, "Node.js major version");
    var current = NodePackage.Current;
    Assert(current.Url.Host == "nodejs.org" && current.Url.Scheme == "https" && current.ArchiveName.EndsWith("-win-x64.zip") && current.Root == $"node-v{current.Version}-win-x64/"
        && current.Sha256.Length == 64 && current.Size > 20_000_000 && NodeInstaller.MajorVersion(current.Version) >= NodeInstaller.MinimumMajor, "Pinned Node.js");
    // TorLink: one exact version of the npm package, into Mira's own folder, without install scripts.
    var arguments = Mira.Core.TorLink.TorLinkPackage.Current.NpmArguments(@"C:\Mira\data\node\node_modules\npm\bin\npm-cli.js", @"C:\Mira\data");
    Assert(arguments[0].EndsWith("npm-cli.js") && arguments[1] == "install" && arguments[2] == $"torlnk@{Mira.Core.TorLink.TorLinkPackage.Current.Version}"
        && arguments.Contains("--ignore-scripts") && arguments[arguments.ToList().IndexOf("--prefix") + 1] == Path.Combine(@"C:\Mira\data", "torlink"), "npm arguments: " + string.Join(" ", arguments));
    Assert(System.Text.RegularExpressions.Regex.IsMatch(Mira.Core.TorLink.TorLinkPackage.Current.Version, @"^\d+\.\d+\.\d+$"), "TorLink version is not exact");
    var problems = new[]
    {
        Mira.Core.TorLink.TorLinkPackage.NpmProblem("npm error code ENOTFOUND\nnpm error syscall getaddrinfo\nnpm error A complete log of this run can be found in: C:\\x.log\n"),
        Mira.Core.TorLink.TorLinkPackage.NpmProblem("npm ERR! code E404\nnpm ERR! 404 Not Found - GET https://registry.npmjs.org/torlnk\n"),
        Mira.Core.TorLink.TorLinkPackage.NpmProblem("quelque chose\nune dernière ligne\n"),
        Mira.Core.TorLink.TorLinkPackage.NpmProblem(""),
    };
    Assert(problems[0] == "ENOTFOUND · syscall getaddrinfo" && problems[1] == "E404 · 404 Not Found - GET https://registry.npmjs.org/torlnk" && problems[2] == "une dernière ligne" && problems[3] is null,
        "npm problems: " + string.Join(" | ", problems));
});
await Test("Moteur mpv : celui installé par Mira est trouvé, un chemin choisi passe avant", () =>
{
    var saved = AppFiles.ProfileDirectory;
    try
    {
        AppFiles.ProfileDirectory = Path.Combine(testRoot, "engine-profile");
        var installed = MpvInstaller.LibraryPath(AppFiles.ProfileDirectory); Directory.CreateDirectory(Path.GetDirectoryName(installed)!); File.WriteAllText(installed, "x");
        var chosen = Path.Combine(testRoot, "engine-chosen.dll"); File.WriteAllText(chosen, "x");
        Assert(MpvEngine.FindLibrary() == installed && MpvEngine.FindLibrary(chosen) == chosen, "Engine lookup order");
    }
    finally { AppFiles.ProfileDirectory = saved; }
    return Task.CompletedTask;
});
await Test("Serveur Jellyfin : son assistant de premier démarrage rempli en français, reprise possible avec le même compte", async () =>
{
    // Answers like Jellyfin 12.1 on its first run: "loading" at first, then the wizard's routes, open until the wizard is completed.
    var loading = 2; var completed = false; string? name = null, password = null, culture = null; bool? remote = null; var failLibrary = false; var signed = true; var requests = 0;
    var libraries = new List<(string Name, string Type, string Path)>();
    // Jellyfin reads JSON names in any case, as ASP.NET does: Mira's bodies go out in camelCase.
    static JsonElement Field(JsonElement? json, params string[] path) => path.Aggregate(json!.Value, (x, name) => x.EnumerateObject().First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value);
    var handler = new Handler(async request =>
    {
        Interlocked.Increment(ref requests);
        signed &= request.Headers.TryGetValues("Authorization", out var values) && values.Single().Contains("Client=\"Mira\"");
        var path = request.RequestUri!.AbsolutePath; JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync()).RootElement;
        var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        if (path.StartsWith("/Startup/") && completed) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        switch (request.Method.Method + " " + path)
        {
            case "GET /System/Info/Public":
                return loading-- > 0 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Jellyfin Server is loading") }
                    : JsonResponse(new { Id = "server-1", Version = "12.1", StartupWizardCompleted = completed });
            case "GET /System/Ping": return new(HttpStatusCode.OK) { Content = new StringContent("\"Jellyfin Server\"") };
            case "POST /Startup/Configuration": culture = Field(body, "UICulture").GetString() + "/" + Field(body, "MetadataCountryCode").GetString(); return new(HttpStatusCode.NoContent);
            case "GET /Startup/User": return JsonResponse(new { Name = name ?? "jellyfin", Password = "" });
            case "POST /Startup/User":
                if (password is not null) return new(HttpStatusCode.Forbidden);
                if (Field(body, "Password").GetString() is not { Length: > 0 } chosen) return new(HttpStatusCode.BadRequest);
                name = Field(body, "Name").GetString(); password = chosen; return new(HttpStatusCode.NoContent);
            case "POST /Users/AuthenticateByName":
                return Field(body, "Username").GetString() == name && Field(body, "Pw").GetString() == password
                    ? JsonResponse(new { AccessToken = "token-1", User = new { Id = "user-1", Name = name } }) : new(HttpStatusCode.Unauthorized);
            case "GET /Library/VirtualFolders": return JsonResponse(libraries.Select(x => new { x.Name, CollectionType = x.Type, Locations = new[] { x.Path } }));
            case "POST /Library/VirtualFolders":
                if (failLibrary) return new(HttpStatusCode.InternalServerError);
                libraries.Add((query["name"], query["collectionType"], Field(Field(body, "LibraryOptions", "PathInfos")[0], "Path").GetString()!)); return new(HttpStatusCode.NoContent);
            case "POST /Startup/RemoteAccess": remote = Field(body, "EnableRemoteAccess").GetBoolean(); return new(HttpStatusCode.NoContent);
            case "POST /Startup/Complete": completed = true; return new(HttpStatusCode.NoContent);
            default: return new(HttpStatusCode.NotFound);
        }
    });
    var server = new Uri("http://127.0.0.1:8096/");
    Assert(await JellyfinSetup.StateAsync(server, handler) is null, "A loading server was taken as ready");
    var state = await JellyfinSetup.WaitAsync(server, TimeSpan.FromSeconds(5), handler, TimeSpan.FromMilliseconds(10));
    Assert(state == new ServerState("12.1", false), "Server state: " + state);
    // A comma in the path: Jellyfin's query form would split it, the body keeps it whole.
    var folders = MediaFolders.Under(Path.Combine(testRoot, "Vidéos, à moi") + " ");
    // The service may read the whole root: never a drive or the user's own folder.
    var drive = Path.GetPathRoot(testRoot)!;
    Assert(MediaFolders.Refusal(folders.Root) is null && MediaFolders.Refusal(drive) is { } whole && whole.Contains("tout le disque")
        && MediaFolders.Refusal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar) is not null
        && MediaFolders.Refusal("Vidéos") is not null && MediaFolders.Refusal(" ") is not null, "Media root refusals");
    folders.Create(); folders.AllowJellyfinService();
    Assert(new[] { folders.Movies, folders.Series, folders.Anime }.All(Directory.Exists) && Path.GetFileName(folders.Series) == "Séries" && !folders.Root.EndsWith(' '), "Media folders");
    await JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler);
    (string, string, string)[] expected = [("Films", "movies", folders.Movies), ("Séries", "tvshows", folders.Series), ("Animes", "tvshows", folders.Anime)];
    Assert(completed && name == "Alice" && password == "secret" && culture == "fr/FR" && remote == false && signed, $"Wizard: {completed} {name} {culture} {remote} {signed}");
    Assert(libraries.SequenceEqual(expected), "Libraries: " + string.Join(", ", libraries));
    // Cut off before its end: run again with the same account, the missing library is added once.
    completed = false; libraries.RemoveAt(2);
    await JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler);
    Assert(completed && libraries.SequenceEqual(expected), "Resumed setup: " + string.Join(", ", libraries));
    async Task Refused(Func<Task> setup, string message)
    {
        try { await setup(); throw new Exception("Accepted: " + message); }
        catch (InstallException ex) { Assert(ex.Message.Contains(message), ex.Message); }
    }
    completed = false;
    await Refused(() => JellyfinSetup.ConfigureAsync(server, "Alice", "autre", folders, handler), "déjà un compte administrateur");
    libraries.Clear(); failLibrary = true;
    await Refused(() => JellyfinSetup.ConfigureAsync(server, "Alice", "secret", folders, handler), "la bibliothèque Films (code 500)");
    var sent = requests;
    foreach (var (user, secret) in new[] { (" Alice", "secret"), ("a/b", "secret"), ("Alice", "") })
    {
        try { await JellyfinSetup.ConfigureAsync(server, user, secret, folders, handler); throw new Exception("Accepted: " + user); }
        catch (ArgumentException) { }
    }
    Assert(requests == sent, "An invalid account reached the server");
    Assert(new[] { "Alice", "Jean-Luc O'Neil", "Élodie", "a.b@c+d" }.All(JellyfinSetup.IsValidUserName) && !new[] { "", " ", "..", "Alice ", "a\\b", "a<b" }.Any(JellyfinSetup.IsValidUserName), "User name rule");
    // Another program on that port, or nothing at all: not a Jellyfin, and the wait ends with a clear message.
    var page = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>box</html>") }));
    var nothing = new Handler(_ => throw new HttpRequestException("Connection refused"));
    Assert(await JellyfinSetup.StateAsync(server, page) is null && await JellyfinSetup.StateAsync(server, nothing) is null, "Not a Jellyfin, yet a state");
    // Told apart, so that Jellyfin is never installed over one that is starting, nor beside another program on its port.
    HttpMessageHandler Answer(HttpStatusCode status, string body) => new Handler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
    var answers = new[]
    {
        (await JellyfinSetup.ProbeAsync(server, nothing)).Answer, (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.ServiceUnavailable, "Jellyfin Server is loading"))).Answer,
        (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.ServiceUnavailable, ""))).Answer, (await JellyfinSetup.ProbeAsync(server, page)).Answer,
        (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.NotFound, ""))).Answer, (await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.OK, "{\"Name\":\"box\"}"))).Answer,
    };
    Assert(answers.SequenceEqual([ServerAnswer.Nothing, ServerAnswer.Loading, ServerAnswer.Loading, ServerAnswer.Other, ServerAnswer.Other, ServerAnswer.Other]), "Probe: " + string.Join(", ", answers));
    // Jellyfin 12's startup page, seen on a real 12.1: this route in camelCase, "wizard not completed" even when it is, and 503 everywhere else.
    var startupPage = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/System/Info/Public"
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"localAddress\":\"http://127.0.0.1:8096\",\"version\":\"12.1.0\",\"productName\":\"Jellyfin Server\",\"id\":\"a\",\"startupWizardCompleted\":false}") }
        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Jellyfin Server is loading. Please try again shortly.") }));
    Assert(await JellyfinSetup.ProbeAsync(server, startupPage) is { Answer: ServerAnswer.Loading, State: null }, "Jellyfin's startup page taken for the server");
    Assert(await JellyfinSetup.ProbeAsync(server, Answer(HttpStatusCode.OK, "{\"Id\":\"a\",\"Version\":\"12.1.0\",\"StartupWizardCompleted\":true}")) is { Answer: ServerAnswer.Ready, State: { Version: "12.1.0", WizardCompleted: true } }, "Ready probe");
    using (var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
    {
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert(JellyfinSetup.PortInUse(port), "A listening port seen as free");
        listener.Stop(); Assert(!JellyfinSetup.PortInUse(port), "A closed port seen as taken");
    }
    await Refused(() => JellyfinSetup.WaitAsync(server, TimeSpan.FromMilliseconds(50), nothing, TimeSpan.FromMilliseconds(10)), "Jellyfin ne répond pas");
    var package = JellyfinServerPackage.Current;
    Assert(package.Url.Scheme == "https" && package.Url.Host == "repo.jellyfin.org" && package.FileName.EndsWith(".exe") && package.Sha256.Length == 64, "Pinned Jellyfin installer");
});
await Test("Journal de Jellyfin : la cause d’un démarrage raté est lue, pas les erreurs ordinaires ni les anciennes", () =>
{
    // The format of Jellyfin 12.1's log files, with the errors a running server logs and a start that fails.
    var folder = Path.Combine(testRoot, "jellyfin-log"); Directory.CreateDirectory(folder);
    File.WriteAllLines(Path.Combine(folder, "log_20261001.log"), [
        "[2026-10-01 09:00:00.000 +02:00] [FTL] [1] Main: Error while starting server",
        "System.InvalidOperationException: An old failure",
        "[2026-10-01 13:33:26.446 +02:00] [INF] [11] Main: Jellyfin version: \"12.1.0\"",
        "[2026-10-01 13:33:36.267 +02:00] [ERR] [10] Emby.Server.Implementations.Updates.InstallationManager: An error occurred while accessing the plugin manifest: \"https://repo.jellyfin.org/files/plugin/manifest.json\"",
        "System.Net.Http.HttpRequestException: No such host is known.",
        "[2026-10-01 13:33:40.100 +02:00] [FTL] [1] Main: Error while starting server",
        "System.IO.IOException: Failed to bind to address http://[::]:8096: address already in use.",
        " ---> Microsoft.AspNetCore.Connections.AddressInUseException: Only one usage of each socket address is normally permitted.",
        "   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.TransportManager.BindAsync()",
        "[2026-10-01 13:33:41.000 +02:00] [INF] [1] Main: Received a SIGTERM signal, shutting down"]);
    var cause = JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 10, 1, 13, 30, 0, TimeSpan.FromHours(2)));
    Assert(cause == "Error while starting server (Failed to bind to address http://[::]:8096: address already in use.)", "Cause: " + cause);
    Assert(JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 10, 1, 13, 35, 0, TimeSpan.FromHours(2))) is null, "An earlier failure reported as the latest");
    File.SetLastWriteTimeUtc(Path.Combine(folder, "log_20261001.log"), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    Assert(JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)) is null, "An old log file read as recent");
    Assert(JellyfinLog.LastFatal(Path.Combine(testRoot, "no-jellyfin-log"), DateTimeOffset.MinValue) is null, "A missing folder");
    return Task.CompletedTask;
});
await Test("Dossiers médias : le service Jellyfin peut les lire, fichiers liés ou déplacés compris", async () =>
{
    // Jellyfin's Windows service runs as Network Service: a hard link or a move keeps the download's own permissions.
    var folders = MediaFolders.Under(Path.Combine(testRoot, "service-access")); folders.Create(); folders.AllowJellyfinService();
    var downloads = Path.Combine(testRoot, "service-downloads"); Directory.CreateDirectory(downloads);
    var other = Path.Combine(testRoot, "service-other"); Directory.CreateDirectory(other);
    Assert(JellyfinServiceAccess.HasOwnRule(folders.Root) && !JellyfinServiceAccess.HasOwnRule(downloads), "Root not shared with the service");
    async Task<string> Import(string file, string library, ImportMode mode)
    {
        var source = Path.Combine(downloads, file); File.WriteAllBytes(source, new byte[64]);
        var result = await MediaImporter.ExecuteAsync(MediaPlanner.Plan(file, null, [new SourceFile(source, file, 64)], new MediaLibraries(library, null, null)), mode);
        Assert(result.Placed.Count == 1 && (mode == ImportMode.Move ? result.Moved : result.Linked), "Import: " + result.Method);
        return result.Placed[0].Destination;
    }
    var linked = await Import("Big.Buck.Bunny.2008.mkv", folders.Movies, ImportMode.KeepSeeding);
    var moved = await Import("Sintel.2010.mkv", folders.Movies, ImportMode.Move);
    var elsewhere = await Import("Tears.of.Steel.2012.mkv", other, ImportMode.KeepSeeding);
    Assert(JellyfinServiceAccess.HasOwnRule(linked) && JellyfinServiceAccess.HasOwnRule(moved), "A placed file stays unreadable by the service");
    Assert(!JellyfinServiceAccess.HasOwnRule(elsewhere), "A folder the service was not given received the right anyway");
});
await Test("Nouveautés : bienvenue sur un profil neuf, points forts après une mise à jour, notes de version courtes", () =>
{
    var all = WhatsNew.All;
    Assert(all.Count >= 2 && all.Select(x => x.Number).SequenceEqual(all.Select(x => x.Number).OrderDescending()), "Versions not newest first");
    foreach (var release in all)
        Assert(release.Summary.Length is > 10 and <= 100 && release.Items.Count is >= 2 and <= 5 && release.Items.All(x => x.Icon.Length > 0 && x.Title.Length is > 3 and <= 50 && x.Text.Length is > 10 and <= 200),
            "Highlights of " + release.Version + " are not short");
    // The version being built has its main points: the screen after the update and the release page need them.
    var project = Path.Combine("src", "Mira.Desktop", "Mira.Desktop.csproj");
    if (File.Exists(project) && System.Text.RegularExpressions.Regex.Match(File.ReadAllText(project), "<Version>([^<]+)</Version>") is { Success: true } version)
        Assert(all.Any(x => x.Version == version.Groups[1].Value), $"WhatsNew.json has no entry for {version.Groups[1].Value}");
    var entries = WhatsNew.Parse("""[{"version":"0.5.4","summary":"Quatre","items":[]},{"version":"0.5.6","summary":"Six","items":[]},{"version":"0.5.5","summary":"Cinq","items":[]}]""");
    Assert(entries.Select(x => x.Version).SequenceEqual(["0.5.6", "0.5.5", "0.5.4"]), "Parsed order");
    Assert(WhatsNew.Since(new Version(0, 5, 3), new Version(0, 5, 5), entries).Select(x => x.Version).SequenceEqual(["0.5.5", "0.5.4"]), "Versions skipped by an update");
    Assert(WhatsNew.Since(null, new Version(0, 5, 5), entries).Select(x => x.Version).SequenceEqual(["0.5.5"]), "Unknown previous version");
    var decisions = new[]
    {
        WhatsNew.Decide("", new Version(0, 5, 5), knownProfile: false, entries), WhatsNew.Decide("", new Version(0, 5, 5), knownProfile: true, entries),
        WhatsNew.Decide("0.5.4", new Version(0, 5, 5), true, entries), WhatsNew.Decide("0.5.5", new Version(0, 5, 5), true, entries),
        WhatsNew.Decide("0.5.6", new Version(0, 5, 5), true, entries), WhatsNew.Decide("0.5.6", new Version(0, 5, 7), true, entries),
        WhatsNew.Decide("pas une version", new Version(0, 5, 5), false, entries),
    };
    Assert(decisions.SequenceEqual([StartupScreen.Welcome, StartupScreen.WhatsNew, StartupScreen.WhatsNew, StartupScreen.None, StartupScreen.None, StartupScreen.None, StartupScreen.Welcome]),
        "Startup screens: " + string.Join(", ", decisions));
    var notes = WhatsNew.ReleaseNotes(all[0]);
    Assert(notes.StartsWith(all[0].Summary) && all[0].Items.All(x => notes.Contains($"**{x.Title}**")) && notes.Contains($"Mira-{all[0].Version}-win-x64-setup.exe")
        && notes.Contains($"/blob/v{all[0].Version}/CHANGELOG.md") && notes.Length < 1500, "Release notes:\n" + notes);
    return Task.CompletedTask;
});
await Test("Nouveautés : chaque icône citée est dessinée par Mira", () =>
{
    var missing = WhatsNew.All.SelectMany(x => x.Items).Select(x => x.Icon).Where(x => !Mira.Desktop.Views.Icon.Draws(x)).Distinct().ToArray();
    Assert(missing.Length == 0, "Icons not drawn: " + string.Join(", ", missing));
    return Task.CompletedTask;
});
await Test("Autres appareils : l’adresse du PC sur le réseau de la maison, pas celle d’un adaptateur virtuel", () =>
{
    LocalNetwork.Candidate C(string address, bool gateway = true, bool isVirtual = false) => new(address, gateway, isVirtual);
    Assert(LocalNetwork.PreferredIPv4([C("172.24.80.1", isVirtual: true), C("169.254.10.2"), C("127.0.0.1"), C("192.168.1.20")]) == "192.168.1.20", "Home network address");
    Assert(LocalNetwork.PreferredIPv4([C("10.0.0.5", gateway: false), C("192.168.0.12")]) == "192.168.0.12", "An adapter with the box's gateway comes first");
    Assert(LocalNetwork.PreferredIPv4([C("fe80::1"), C("169.254.3.3")]) is null, "No usable address");
    Assert(LocalNetwork.ForOtherDevices("http://127.0.0.1:8096/", "192.168.1.20") == "http://192.168.1.20:8096"
        && LocalNetwork.ForOtherDevices("http://localhost:8096", "192.168.1.20") == "http://192.168.1.20:8096"
        && LocalNetwork.ForOtherDevices("https://media.example.org/jellyfin/", "192.168.1.20") == "https://media.example.org/jellyfin"
        && LocalNetwork.ForOtherDevices("http://127.0.0.1:8096/", null) is null && LocalNetwork.ForOtherDevices("pas une adresse", "192.168.1.20") is null, "Address for other devices");
    return Task.CompletedTask;
});
await Test("Session protégée par Windows, mot de passe absent du stockage", () =>
{
    var profile = new LocalProfile(Path.Combine(testRoot, "protected")); var connection = new Connection("http://localhost/", "user", "Alice", "secret-session-token", profile.DeviceId);
    profile.SaveConnection(connection); Assert(profile.LoadConnection() == connection, "Session round trip failed");
    var disk = File.ReadAllBytes(Path.Combine(profile.DirectoryPath, "session.protected")); Assert(!Encoding.UTF8.GetString(disk).Contains("secret-session-token"), "Token stored as plain text"); return Task.CompletedTask;
});
await Test("Profil : un identifiant d’appareil vide est remplacé, puis gardé", () =>
{
    var folder = Path.Combine(testRoot, "empty-device"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "device-id"), "  \n");
    var id = new LocalProfile(folder).DeviceId;
    Assert(id.Length == 32 && new LocalProfile(folder).DeviceId == id && File.ReadAllText(Path.Combine(folder, "device-id")) == id, "Empty device id kept, or not persisted: '" + id + "'");
    return Task.CompletedTask;
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
await Test("Cache disque : au-delà du budget, les images les moins récemment vues partent", () =>
{
    var folder = Path.Combine(testRoot, "disk-cache"); Directory.CreateDirectory(Path.Combine(folder, "compte-2"));
    string Make(string name, int size, double daysAgo)
    { var path = Path.Combine(folder, name); File.WriteAllBytes(path, new byte[size]); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysAgo)); return path; }
    var oldest = Make("a.img", 400, 30); var old = Make(Path.Combine("compte-2", "b.img"), 400, 20); var recent = Make("c.img", 400, 2); var fresh = Make("d.img", 400, 0);
    var interrupted = Make("e.img.tmp", 100, 1); var writing = Make("f.img.tmp", 100, 0); var other = Make("notes.txt", 5000, 40);
    Assert(DiskCache.Trim(folder, 2000, 1000) == (1, 100) && !File.Exists(interrupted) && File.Exists(writing), "Trimmed under budget, or temporary files mishandled");
    var touched = File.GetLastWriteTimeUtc(fresh); DiskCache.Touch(fresh);
    Assert(File.GetLastWriteTimeUtc(fresh) == touched, "A file used today was written again");
    DiskCache.Touch(oldest);
    Assert(DiskCache.Trim(folder, 1500, 800) == (2, 800), "Wrong amount trimmed");
    Assert(File.Exists(oldest) && File.Exists(fresh) && !File.Exists(old) && !File.Exists(recent) && File.Exists(other), "Least recently used files not chosen, or another file touched");
    Assert(DiskCache.Trim(Path.Combine(testRoot, "absent"), 1, 0) == (0, 0), "Missing folder");
    return Task.CompletedTask;
});
await Test("Cache de bibliothèque : pages anciennes oubliées ; accueil, reprise, historique et envois en attente gardés", () =>
{
    var store = Store("prune");
    store.Save("home", new ItemsResult()); store.Save("Movie::|2024||recent", new ItemsResult { TotalRecordCount = 3 });
    store.RememberPlayback(new MediaItem { Id = "vu", RunTimeTicks = 1000, UserData = new() { LastPlayedDate = DateTimeOffset.UtcNow, PlaybackPositionTicks = 500 } });
    store.Enqueue("stop", Report(400) with { ItemId = "livré" }); store.Acknowledge(store.Peek()!.Id);
    store.Enqueue("progress", Report(300));
    Assert(Store("prune").Load<ItemsResult>("Movie::|2024||recent") is { TotalRecordCount: 3 }, "A recent page expired on opening");
    store.Prune(DateTimeOffset.UtcNow.AddMinutes(1));
    Assert(store.Load<ItemsResult>("Movie::|2024||recent") is null, "Old filtered page kept");
    Assert(store.Load<ItemsResult>("home") is not null && store.Load<List<MediaItem>>("resume") is { Count: 1 } && store.RecentPlayback().Count == 1, "Home, resume row or history pruned");
    var items = new[] { new MediaItem { Id = "item-1" }, new MediaItem { Id = "livré" } }; store.ApplyLocalProgress(items);
    Assert(items[0].UserData.PlaybackPositionTicks == 300 && items[1].UserData.PlaybackPositionTicks == 0 && store.PendingCount == 1, "Pending position lost, or delivered one kept");
    return Task.CompletedTask;
});
await Test("Continuer à regarder : un épisode montre sa saison ou lui-même, pas l’image d’une autre saison", async () =>
{
    // A series whose seasons look nothing alike: its backdrop shows one season, the episode belongs to another.
    MediaItem Episode(string? seasonThumb = null, string? backdropParent = "series", Dictionary<string, string>? own = null) => new()
    {
        Id = "episode", Type = "Episode", SeriesId = "series", SeasonId = "season-7", ImageTags = own ?? [],
        ParentBackdropItemId = backdropParent, ParentBackdropImageTags = backdropParent is null ? [] : ["backdrop-tag"], SeasonThumbImageTag = seasonThumb,
    };
    var choices = new[]
    {
        Artwork.Landscape(Episode(own: new() { ["Thumb"] = "own-thumb", ["Primary"] = "still" })),
        Artwork.Landscape(Episode(seasonThumb: "season-thumb", own: new() { ["Primary"] = "still" })),
        Artwork.Landscape(Episode(backdropParent: "season-7", own: new() { ["Primary"] = "still" })),
        Artwork.Landscape(Episode(own: new() { ["Primary"] = "still" })),
        Artwork.Landscape(Episode()),
        Artwork.Landscape(new MediaItem { Id = "movie", Type = "Movie", BackdropImageTags = ["movie-backdrop"], ImageTags = new() { ["Primary"] = "poster" } }),
    };
    Assert(choices.SequenceEqual([
        new ImageRef("episode", "Thumb", "own-thumb"), new ImageRef("season-7", "Thumb", "season-thumb"), new ImageRef("season-7", "Backdrop", "backdrop-tag"),
        new ImageRef("episode", "Primary", "still"), new ImageRef("series", "Backdrop", "backdrop-tag"), new ImageRef("movie", "Backdrop", "movie-backdrop")]),
        "Choices: " + string.Join(" | ", choices.Select(x => $"{x.ItemId}/{x.Type}/{x.Tag}")));
    // The home banner keeps the series' backdrop: it presents the series, with its logo.
    Assert(Artwork.Backdrop(Episode(own: new() { ["Primary"] = "still" })) == new ImageRef("series", "Backdrop", "backdrop-tag"), "Banner of an episode");
    Assert(Artwork.Seasons([Episode(), Episode(), new MediaItem { Type = "Movie", SeasonId = "x" }, new MediaItem { Type = "Episode" }]).SequenceEqual(["season-7"]), "Seasons asked for");
    // One request for every season; a season without a thumbnail answers null.
    string? url = null;
    using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "token", "device"), new Handler(request =>
    {
        url = request.RequestUri!.PathAndQuery;
        return Task.FromResult(JsonResponse(new { Items = new object[] { new { Id = "season-7", ImageTags = new { Thumb = "t7" } }, new { Id = "season-2", ImageTags = new { } } }, TotalRecordCount = 2 }));
    }));
    var thumbs = await client.SeasonThumbsAsync(["season-7", "season-2", "season-9"]);
    Assert(url is not null && url.StartsWith("/Items?userId=user-1&ids=season-7,season-2,season-9&") && url.Contains("enableImageTypes=Thumb"), "Request: " + url);
    Assert(thumbs.Count == 3 && thumbs["season-7"] == "t7" && thumbs["season-2"] is null && thumbs["season-9"] is null, "Thumbnails by season");
    url = null; Assert((await client.SeasonThumbsAsync([])).Count == 0 && url is null, "A request without any season");
});
await Test("Images : mémoire bornée, les moins récentes libérées puis relues sur disque sans nouveau téléchargement", async () =>
{
    // 500 × 500 artwork, decoded at the poster width: 1 000 000 bytes each.
    var pixels = new byte[500 * 500 * 4]; Array.Fill(pixels, (byte)180);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(500, 500, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 2000)));
    using var png = new MemoryStream(); encoder.Save(png); var bytes = png.ToArray();
    var downloads = 0;
    using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ =>
    { Interlocked.Increment(ref downloads); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }));
    const long each = 1_000_000;
    var cache = new ImageCache(client, Path.Combine(testRoot, "image-cache")) { MemoryLimit = each * 3, MemoryTarget = each * 2 };
    MediaItem Poster(int i) => new() { Id = "poster-" + i, ImageTags = new() { ["Primary"] = "tag" } };
    var requests = new List<Task<System.Windows.Media.Imaging.BitmapSource?>>();
    for (var i = 0; i < 5; i++) { requests.Add(cache.GetAsync(Poster(i))); Assert(await requests[i] is { PixelWidth: 500 }, "Image not decoded"); }
    await WaitUntil(() => cache.Settled == 5);
    Assert(cache.MemoryBytes == each * 3, "Memory not trimmed to its target: " + cache.MemoryBytes);
    Assert(ReferenceEquals(cache.GetAsync(Poster(4)), requests[4]), "The most recent image left memory");
    var again = cache.GetAsync(Poster(0));
    Assert(!ReferenceEquals(again, requests[0]), "The oldest image stayed in memory past the budget");
    Assert(await again is { PixelWidth: 500 } && downloads == 5, "An image evicted from memory was downloaded again instead of read from disk");
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
await Test("TorLink : titres, années et épisodes lus dans des noms de sortie réels", () =>
{
    void Movie(string name, string title, int? year) { var (t, y) = ReleaseName.Movie(name); Assert(t == title && y == year, $"Film mal lu : {name} → {t} ({y})"); }
    Movie("Helter Skelter (2012) [1080p] [BluRay] [5.1] [YTS.MX]", "Helter Skelter", 2012);
    Movie("L'argent.1983.Criterion.1080p.BluRay.x265.HEVC.FLAC-SARTRE", "L'argent", 1983);
    Movie("Torrenting.org   -   Beau.Travail.1999.iNTERNAL.BDRip.x264-MANiC", "Beau Travail", 1999);
    Movie("www.Torrenting.com - The Doom Generation 1995 UHD BluRay 1080p DDP 5 1 SDR x265-SM737", "The Doom Generation", 1995);
    Movie("2001.A.Space.Odyssey.1968.2160p.UHD.BluRay.x265-GROUP", "2001 A Space Odyssey", 1968);
    Movie("1917 (2019) [1080p] [WEBRip]", "1917", 2019);
    Movie("Call.Me.By.Your.Name.2017.1080p", "Call Me By Your Name", 2017);
    Movie("Kite.1998.DVDRip.x264", "Kite", 1998);
    var jojo = "JoJos.Bizarre.Adventure.S06E02.The.Sheriffs.Request.to.Mountain.Tim.1080p.NF.WEB-DL.DUAL.AAC2.0.H.264.MSubs-ToonsHub.mkv";
    Assert(ReleaseName.Show(jojo).Title == "JoJos Bizarre Adventure" && ReleaseName.Episode(jojo, false)?.Label == "S06E02", "SxxEyy episode misread");
    Assert(ReleaseName.Show("Doctor.Who.2005.S13E01.1080p") == ("Doctor Who", 2005), "Series year lost");
    Assert(ReleaseName.Episode("Show.Name.2019.S02E10E11.1080p.WEB.h264-GRP", false)?.Label == "S02E10-E11", "Double episode lost");
    var pack = "[Marin] My Dress-Up Darling - S01 [PROPER BD 1080p HEVC FLAC] [Dual-Audio]";
    Assert(ReleaseName.Show(pack) == ("My Dress-Up Darling", null) && ReleaseName.Season(pack) == 1, "Season pack misread");
    Assert(ReleaseName.Episode("[SubsPlease] Sousou no Frieren S2 - 05 (1080p) [ABCD1234].mkv", true) is { Season: 2, Episode: 5, Absolute: false }, "Fansub season and number misread");
    Assert(ReleaseName.Episode("[Group] Show - 07 [1080p].mkv", true) is { Season: 1, Episode: 7, Absolute: true }, "Absolute anime number misread");
    Assert(ReleaseName.Episode("Some.Film.2010.1080p.BluRay", false) is null && ReleaseName.Episode("[YTS] Film - 2014 [1080p]", true) is null, "A film or a year read as an episode");
    Assert(ReleaseName.Season("Show.S01-S03.Complete.1080p") is null, "A multi-season pack was given one season");
    return Task.CompletedTask;
});
await Test("TorLink : sous-titres, bonus, clés de dossiers et noms Windows", () =>
{
    Assert(ReleaseName.SubtitleTags(@"Film\Subs\2_English.srt") == ".en" && ReleaseName.SubtitleTags(@"Film\Film.2012.en.forced.srt") == ".en.forced" && ReleaseName.SubtitleTags(@"Film\French.srt") == ".fr"
        && ReleaseName.SubtitleTags(@"Film\Film.2012.srt") == "" && ReleaseName.SubtitleTags(@"Show\Show.S01E01.eng.SDH.srt") == ".en.sdh", "Subtitle language tags");
    Assert(ReleaseName.CreditlessLabel("[Group] Show - NCOP1 [1080p].mkv", false) == "NCOP01" && ReleaseName.CreditlessLabel("Show - Creditless Ending 2.mkv", false) == "NCED02"
        && ReleaseName.CreditlessLabel("Show - OP1.mkv", false) is null && ReleaseName.CreditlessLabel("Show - OP1.mkv", true) == "NCOP01", "Creditless openings and endings");
    Assert(ReleaseName.Key("JoJo's Bizarre Adventure (2012)") == ReleaseName.Key("JoJos Bizarre Adventure") && ReleaseName.Key("L'Argent (1983)") == ReleaseName.Key("L'argent")
        && ReleaseName.Key("Les Bronzés font du ski") == "les bronzes font du ski", "Folder matching keys");
    Assert(ReleaseName.SafeName("Mission: Impossible - Fallout (2018)") == "Mission - Impossible - Fallout (2018)" && ReleaseName.SafeName("CON") == "CON_"
        && ReleaseName.SafeName("What?/Why* (2020)") == "What Why (2020)" && ReleaseName.SafeName("Trailing dots...") == "Trailing dots", "Windows-safe names");
    Assert(ReleaseName.SeasonOfFolder("Season 17 - Thousand-Year Blood War") == 17 && ReleaseName.SeasonOfFolder("Specials") == 0 && ReleaseName.SeasonOfFolder("Extras") is null, "Season folders");
    Assert(ReleaseName.IsSample(@"Film\Sample\film-sample.mkv") && !ReleaseName.IsSample(@"Film\Film.2019.mkv"), "Sample detection");
    return Task.CompletedTask;
});
await Test("TorLink : films, épisodes, packs et bonus rangés dans les dossiers existants", () =>
{
    var root = Path.Combine(testRoot, "planner-library"); var films = Path.Combine(root, "FILMS"); var series = Path.Combine(root, "SERIES"); var anime = Path.Combine(root, "ANIME");
    foreach (var folder in new[] { Path.Combine(films, "L'Argent (1983)"), series, Path.Combine(anime, "Jujutsu Kaisen (2020)", "Season 01"), Path.Combine(anime, "Bleach (2004)", "Season 17 - Thousand-Year Blood War") }) Directory.CreateDirectory(folder);
    var libraries = new MediaLibraries(films, series, anime);
    SourceFile File(string relative, long length) => new(Path.Combine(@"C:\downloads", relative), relative, length);
    HashSet<string> Targets(ImportPlan plan) => plan.Operations.Select(x => x.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var a = "L'argent.1983.Criterion.1080p.BluRay.x265.HEVC.FLAC-SARTRE";
    var argent = MediaPlanner.Plan(a, "tpb-movies", [File($@"{a}\{a}.mkv", 6_900_000_000), File($@"{a}\Cannes Film Festival 1983.mkv", 121_000_000), File($@"{a}\Sample\sample.mkv", 50_000_000), File($@"{a}\L'argent.1983.French.srt", 90_000), File($@"{a}\info.txt", 1_898)], libraries);
    var argentFolder = Path.Combine(films, "L'Argent (1983)");
    Assert(argent.Kind == MediaKind.Movie && argent.Title == "L'Argent (1983)" && Targets(argent).SetEquals(new[] { Path.Combine(argentFolder, "L'Argent (1983).mkv"), Path.Combine(argentFolder, "Extras", "Cannes Film Festival 1983.mkv"), Path.Combine(argentFolder, "L'Argent (1983).fr.srt") }),
        "Film with extras, sample and notes: " + string.Join(" | ", Targets(argent)));
    Assert(argent.Notes.Any(x => x.Contains("échantillon")) && argent.Notes.Any(x => x.Contains("annexe")), "Left-out files are not reported");
    var jujutsu = MediaPlanner.Plan("[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234].mkv", "subsplease", [File("[SubsPlease] Jujutsu Kaisen - 05 (1080p) [ABCD1234].mkv", 1_400_000_000)], libraries);
    Assert(jujutsu.Kind == MediaKind.Anime && jujutsu.Operations.Single().Destination == Path.Combine(anime, "Jujutsu Kaisen (2020)", "Season 01", "Jujutsu Kaisen (2020) - S01E05.mkv"), "Existing anime folder not reused: " + string.Join(" | ", Targets(jujutsu)));
    var bleach = MediaPlanner.Plan("Bleach.S17E49.1080p.WEB.H264-GRP", null, [File(@"Bleach.S17E49.1080p.WEB.H264-GRP\Bleach.S17E49.1080p.WEB.H264-GRP.mkv", 1_000_000_000)], libraries);
    Assert(bleach.Kind == MediaKind.Anime && bleach.Operations.Single().Destination == Path.Combine(anime, "Bleach (2004)", "Season 17 - Thousand-Year Blood War", "Bleach (2004) - S17E49.mkv"), "Named season folder not reused: " + string.Join(" | ", Targets(bleach)));
    var jojoName = "JoJos.Bizarre.Adventure.S06E02.The.Sheriffs.Request.to.Mountain.Tim.1080p.NF.WEB-DL.DUAL.AAC2.0.H.264.MSubs-ToonsHub.mkv";
    var jojo = MediaPlanner.Plan(jojoName, "x1337-tv", [File(jojoName, 942_154_418)], libraries);
    Assert(jojo.Kind == MediaKind.Series && jojo.Operations.Single().Destination == Path.Combine(series, "JoJos Bizarre Adventure", "Season 06", "JoJos Bizarre Adventure - S06E02.mkv"), "TV episode: " + string.Join(" | ", Targets(jojo)));
    var p = "[Marin] My Dress-Up Darling - S01 [PROPER BD 1080p HEVC FLAC] [Dual-Audio]";
    var pack = MediaPlanner.Plan(p, null, [File($@"{p}\[Marin] My Dress-Up Darling - 01 [BD 1080p HEVC FLAC] [ABCDEF12].mkv", 1_064_000_000), File($@"{p}\[Marin] My Dress-Up Darling - 02 [BD 1080p HEVC FLAC] [ABCDEF13].mkv", 1_277_000_000),
        File($@"{p}\[Marin] My Dress-Up Darling - 01 [BD 1080p HEVC FLAC] [ABCDEF12].fr.ass", 40_000), File($@"{p}\Extras\[Marin] My Dress-Up Darling - NCOP [BD 1080p].mkv", 113_000_000)], libraries);
    var show = Path.Combine(anime, "My Dress-Up Darling");
    Assert(pack.Kind == MediaKind.Anime && pack.Title == "My Dress-Up Darling · 2 épisodes" && Targets(pack).SetEquals(new[] { Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E01.mkv"), Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E02.mkv"),
        Path.Combine(show, "Season 01", "My Dress-Up Darling - S01E01.fr.ass"), Path.Combine(show, "Extras", "My Dress-Up Darling - NCOP.mkv") }), "Anime season pack: " + string.Join(" | ", Targets(pack)));
    var lone = MediaPlanner.Plan("Sample.2019.1080p", null, [File(@"Sample.2019.1080p\Sample.2019.1080p.mkv", 2_000_000_000)], libraries);
    Assert(lone.Operations.Count == 1 && lone.Title == "Sample (2019)", "A lone feature titled Sample was dropped");
    Assert(MediaPlanner.Plan("Some.Game-FitGirl", "fitgirl", [File(@"Some.Game-FitGirl\setup.exe", 1000)], libraries).Ignored is not null, "A game would reach Jellyfin");
    Assert(MediaPlanner.Plan("Album.2020.FLAC", null, [File(@"Album.2020.FLAC\01.flac", 1000)], libraries).Ignored is not null, "A download without video would reach Jellyfin");
    Assert(MediaPlanner.Plan("Film.2020.1080p.mkv", null, [File("Film.2020.1080p.mkv", 1000)], new MediaLibraries(null, series, anime)).Problem is not null, "A missing film library must wait for a folder, not guess one");
    Assert(MediaPlanner.Plan("Tears.of.Steel.2012.mkv", null, [File("Tears.of.Steel.2012.mkv", 1000)], libraries, MediaKind.Anime).Kind == MediaKind.Anime, "A forced kind is ignored");
    return Task.CompletedTask;
});
await Test("TorLink : lien physique, déplacement entre disques sans copie restante, fichier existant jamais remplacé, rien hors de la bibliothèque", async () =>
{
    var dir = Path.Combine(testRoot, "importer"); var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS");
    Directory.CreateDirectory(downloads); Directory.CreateDirectory(films);
    var video = Path.Combine(downloads, "Big.Buck.Bunny.2008.1080p.mkv"); File.WriteAllBytes(video, new byte[4096]);
    var plan = MediaPlanner.Plan("Big.Buck.Bunny.2008.1080p.mkv", "yts", [new SourceFile(video, "Big.Buck.Bunny.2008.1080p.mkv", 4096)], new MediaLibraries(films, null, null));
    var target = Path.Combine(films, "Big Buck Bunny (2008)", "Big Buck Bunny (2008).mkv");
    var first = await MediaImporter.ExecuteAsync(plan, ImportMode.KeepSeeding);
    Assert(first.Placed.Count == 1 && first.Linked && !first.Copied && File.Exists(target) && File.Exists(video), "Hard link not created, or the download moved");
    using (var stream = new FileStream(video, FileMode.Append)) stream.WriteByte(1);
    Assert(new FileInfo(target).Length == 4097, "The library file is a copy instead of a hard link");
    var again = await MediaImporter.ExecuteAsync(plan, ImportMode.KeepSeeding);
    Assert(again.AlreadyThere.Count == 1 && again.Placed.Count == 0, "A second run duplicated the file");
    var other = Path.Combine(downloads, "other.mkv"); File.WriteAllBytes(other, new byte[10]);
    var conflict = await MediaImporter.ExecuteAsync(plan with { Operations = [plan.Operations[0] with { Source = other }] }, ImportMode.KeepSeeding);
    Assert(conflict.Conflicts.Count == 1 && new FileInfo(target).Length == 4097, "An existing library file was replaced");
    foreach (var outside in new[] { Path.Combine(dir, "outside.mkv"), Path.Combine(films, "..", "escaped.mkv"), Path.Combine(films, "tool", "tool.exe") })
    {
        var refused = await MediaImporter.ExecuteAsync(plan with { Operations = [plan.Operations[0] with { Destination = outside }] }, ImportMode.KeepSeeding);
        Assert(refused.Failures.Count == 1 && !File.Exists(Path.GetFullPath(outside)), "A file was written outside the library or with a program extension: " + outside);
    }
    // A \\?\ library path does not share the downloads' root: this exercises the way between two drives, where no
    // link is possible. Even when sharing is kept, the download moves: nothing stays twice on disk.
    var copies = Path.Combine(dir, "COPIES"); Directory.CreateDirectory(copies);
    var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    foreach (var mode in new[] { ImportMode.KeepSeeding, ImportMode.Move })
    {
        var name = mode == ImportMode.Move ? "Elephants.Dream.2006.mkv" : "Tears.of.Steel.2012.mkv";
        var source = Path.Combine(downloads, name); File.WriteAllBytes(source, bytes);
        var crossed = await MediaImporter.ExecuteAsync(MediaPlanner.Plan(name, null, [new SourceFile(source, name, 5000)], new MediaLibraries(@"\\?\" + copies, null, null)), mode);
        var arrived = crossed.Placed.Single().Destination;
        Assert(crossed is { Moved: true, OtherDrive: true, Copied: false, Linked: false, Placed.Count: 1 } && !File.Exists(source) && File.ReadAllBytes(arrived).SequenceEqual(bytes),
            $"{mode} between drives left a copy behind, or lost data: " + crossed.Method);
        Assert(crossed.Method == "déplacement depuis un autre disque", "Method shown after crossing drives: " + crossed.Method);
    }
    Assert(!Directory.EnumerateFiles(copies, "*" + MediaImporter.PartialSuffix, SearchOption.AllDirectories).Any(), "A partial file was left");
    var sintel = Path.Combine(downloads, "Sintel.2010.mkv"); File.WriteAllBytes(sintel, new byte[100]);
    var moved = await MediaImporter.ExecuteAsync(MediaPlanner.Plan("Sintel.2010.mkv", null, [new SourceFile(sintel, "Sintel.2010.mkv", 100)], new MediaLibraries(films, null, null)), ImportMode.Move);
    Assert(moved is { Moved: true, OtherDrive: false, Copied: false } && moved.Method == "déplacement" && !File.Exists(sintel) && File.Exists(Path.Combine(films, "Sintel (2010)", "Sintel (2010).mkv")), "Move mode: " + moved.Method);
    Directory.CreateDirectory(Path.Combine(films, "Empty Show", "Season 01"));
    MediaImporter.PruneEmptyFolders([Path.Combine(films, "Empty Show", "Season 01")], [films]);
    Assert(!Directory.Exists(Path.Combine(films, "Empty Show")) && Directory.Exists(films) && File.Exists(target), "Empty folders were kept, or more was removed");
    // Nested library folders: an anime root inside the series one is never removed, even once empty.
    var seriesRoot = Path.Combine(dir, "SERIES"); var nestedAnime = Path.Combine(seriesRoot, "Anime");
    Directory.CreateDirectory(Path.Combine(nestedAnime, "Show", "Season 01"));
    MediaImporter.PruneEmptyFolders([Path.Combine(nestedAnime, "Show", "Season 01")], [seriesRoot, nestedAnime]);
    Assert(Directory.Exists(nestedAnime) && !Directory.Exists(Path.Combine(nestedAnime, "Show")), "A nested library root was removed, or the emptied show was kept");
});
await Test("TorLink : historique sans lien magnet, activation, attente des fichiers, dossier manquant et journal rechargé", async () =>
{
    var dir = Path.Combine(testRoot, "torlink-ledger"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
    var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS");
    foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, films }) Directory.CreateDirectory(folder);
    Assert(state.ReadHistory() is { Count: 0 } && state.DownloadDirectory() == TorLinkState.DefaultDownloadDirectory, "Missing TorLink files");
    File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
    File.WriteAllText(state.QueueFile, "[{\"id\":\"x\",\"status\":\"downloading\"},{\"id\":\"y\",\"status\":\"paused\"}]");
    Assert(state.DownloadDirectory() == downloads && state.ActiveDownloads() == 1, "TorLink config or queue misread");
    var custom = TorLinkState.ForCurrentUser(Path.Combine(dir, "portable"));
    Assert(custom.DataDirectory == Path.Combine(dir, "portable", "data") && custom.ConfigDirectory == Path.Combine(dir, "portable", "config"), "TORLINK_STATE_DIR layout");
    var profile = Path.Combine(dir, "profile"); var importer = new TorLinkImporter(profile);
    string Id(char c) => new(c, 40);
    var before = importer.Baseline.AddMinutes(-5).ToUnixTimeMilliseconds(); var after = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
    object Item(string id, string name, long at) => new { id, name, source = "yts", sizeBytes = 10, magnet = "magnet:?xt=urn:btih:" + id, dir = downloads, completedAt = at };
    void History(params object[] items) => File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(items));
    File.WriteAllText(state.HistoryFile, "[{\"id\":");
    Assert(state.ReadHistory() is null, "A half-written history must be retried, not read as empty");
    History(Item(Id('a'), "Old.Film.2001.mkv", before), Item(Id('b'), "New.Film.2002.mkv", after), new { id = "../../evil", name = "x", dir = downloads }, Item(Id('b'), "Duplicate.mkv", after));
    Assert(state.ReadHistory() is { Count: 2 } history && history.All(x => TorLinkState.IsInfoHash(x.Id)) && history.Single(x => x.Id == Id('b')).Name == "New.Film.2002.mkv", "Invalid or duplicate history entries");
    File.WriteAllBytes(Path.Combine(downloads, "Old.Film.2001.mkv"), new byte[10]);
    var libraries = new MediaLibraries(films, null, null);
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(Id('a')) is null, "A download finished before activation was imported automatically");
    Assert(importer.Find(Id('b')) is { State: TorLinkImportState.Waiting, Attempts: 1 }, "Missing files are not awaited");
    File.WriteAllBytes(Path.Combine(downloads, "New.Film.2002.mkv"), new byte[10]);
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(Id('b')) is { State: TorLinkImportState.Imported, Kind: MediaKind.Movie } && File.Exists(Path.Combine(films, "New Film (2002)", "New Film (2002).mkv")), "The awaited download was not imported");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true, requested: [Id('a')]);
    Assert(importer.Find(Id('a'))?.State == TorLinkImportState.Imported, "An earlier download asked for was not imported");
    History(Item(Id('a'), "Old.Film.2001.mkv", before), Item(Id('b'), "New.Film.2002.mkv", after), Item(Id('c'), "Third.Film.2003.mkv", after));
    File.WriteAllBytes(Path.Combine(downloads, "Third.Film.2003.mkv"), new byte[10]);
    await importer.ProcessAsync(state, new MediaLibraries(null, null, null), ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(Id('c'))?.State == TorLinkImportState.Blocked, "A missing library folder must block the import");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(Id('c'))?.State == TorLinkImportState.Imported, "A blocked download is not retried once the folder is known");
    var reopened = new TorLinkImporter(profile);
    Assert(reopened.Entries.Count == 3 && reopened.Baseline == importer.Baseline && reopened.Find(Id('b'))!.Files.Count == 1, "The import log was not kept");
    Assert(!File.ReadAllText(Path.Combine(profile, "torlink-imports.json")).Contains("magnet:"), "Mira stored a magnet link");
});
await Test("TorLink : dossier de téléchargement sur le disque de la bibliothèque, choisi seulement si TorLink n’en a pas", () =>
{
    var dir = Path.Combine(testRoot, "torlink-drive"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
    var chosen = Path.Combine(dir, "Téléchargements TorLink");
    Assert(state.ChooseDownloadDirectory(chosen) && state.DownloadDirectory() == Path.GetFullPath(chosen) && Directory.Exists(chosen), "First download folder");
    Assert(!state.ChooseDownloadDirectory(Path.Combine(dir, "other")) && state.DownloadDirectory() == Path.GetFullPath(chosen), "An existing choice was replaced");
    if (OperatingSystem.IsWindows())
    {
        var downloads = @"C:\Users\Toi\Downloads\torlink";
        string? Beside(string? movies, string? series = null, string? anime = null) => TorLinkState.DownloadFolderBeside(new(movies, series, anime), downloads);
        Assert(Beside(@"C:\Users\Toi\Videos\Jellyfin\Films", @"C:\Users\Toi\Videos\Jellyfin\Séries") is null, "Same drive: nothing to change");
        Assert(Beside(@"D:\Media\Films", @"D:\Media\Séries", @"D:\Media\Animes") == @"D:\Media\Téléchargements TorLink", "Beside the library folders");
        Assert(Beside(@"D:\Films") == @"D:\Téléchargements TorLink", "Library folder at the top of a drive");
        Assert(Beside(@"D:\Séries\Films", @"D:\Séries") == @"D:\Téléchargements TorLink", "A folder inside another library folder");
        Assert(Beside(@"D:\") is null, "A library at the root of a drive leaves no room");
        Assert(Beside(@"D:\Films", @"E:\Séries") is null, "Library folders on two drives");
        Assert(Beside(null) is null, "No library folder");
    }
    return Task.CompletedTask;
});
await Test("TorLink : fichier déjà présent jamais déplacé, classement conservé, déplacement repris et journal illisible gardé", async () =>
{
    var dir = Path.Combine(testRoot, "torlink-recovery"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
    var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS"); var series = Path.Combine(dir, "SERIES"); var anime = Path.Combine(dir, "ANIME");
    foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, films, series, anime }) Directory.CreateDirectory(folder);
    var libraries = new MediaLibraries(films, series, anime);
    var importer = new TorLinkImporter(Path.Combine(dir, "profile"));
    var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
    var history = new List<object>();
    string Finished(char c, string name, string source) { var id = new string(c, 40); history.Add(new { id, name, source, sizeBytes = 10, dir = downloads, completedAt = at }); File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(history)); return id; }
    string Download(string relative, int size) { var path = Path.Combine(downloads, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, new byte[size]); return path; }

    // A same-size film that was already in the library: found there, never moved by « Classer ».
    Download("Dune.2021.1080p.mkv", 64);
    var existing = Path.Combine(films, "Dune (2021)", "Dune (2021).mkv"); Directory.CreateDirectory(Path.GetDirectoryName(existing)!); File.WriteAllBytes(existing, new byte[64]);
    var dune = Finished('a', "Dune.2021.1080p.mkv", "yts");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(dune) is { State: TorLinkImportState.Imported, Files: [{ Found: true }] }, "A file already in the library was taken for Mira's own");
    var (left, _) = await importer.ReclassifyAsync(dune, MediaKind.Series, libraries);
    Assert(File.Exists(existing) && left?.Kind == MediaKind.Movie && !Directory.EnumerateFileSystemEntries(series).Any(), "« Classer » moved a file Mira did not place");

    // A hard link of TorLink's own file is Mira's, even without its log line: it can move.
    var sintelSource = Download("Sintel.2010.mkv", 32);
    Assert((await MediaImporter.ExecuteAsync(MediaPlanner.Plan("Sintel.2010.mkv", "yts", [new SourceFile(sintelSource, "Sintel.2010.mkv", 32)], libraries), ImportMode.KeepSeeding)).Linked, "Hard link");
    var sintel = Finished('b', "Sintel.2010.mkv", "yts");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(sintel) is { State: TorLinkImportState.Imported, Files: [{ Found: false }] }, "A hard link of TorLink's file was not recognised");

    // « Réessayer » after « Classer comme anime » keeps the anime library: no second link among the series.
    Download("Show.S01E02.1080p.mkv", 16);
    var show = Finished('c', "Show.S01E02.1080p.mkv", "eztv");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
    Assert(importer.Find(show)?.Kind == MediaKind.Series, "Episode not placed among the series");
    await importer.ReclassifyAsync(show, MediaKind.Anime, libraries);
    Assert(importer.Find(show) is { Kind: MediaKind.Anime, ForcedKind: MediaKind.Anime, Files: [var moved] } && File.Exists(moved.Destination) && moved.Destination.StartsWith(anime), "Reclassification");
    await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true, requested: [show]);
    Assert(importer.Find(show) is { Kind: MediaKind.Anime, State: TorLinkImportState.Imported } && !Directory.EnumerateFiles(series, "*", SearchOption.AllDirectories).Any(), "A new attempt put the reclassified episode back among the series");

    // Move mode stopped by a conflicting subtitle: the moved video stays tracked and the subtitle follows on the next attempt.
    var packFiles = new[] { Download(@"Pack.S02E01\Pack.S02E01.mkv", 24), Download(@"Pack.S02E01\Pack.S02E01.en.srt", 5) };
    var packPlan = MediaPlanner.Plan("Pack.S02E01", "eztv", packFiles.Select(x => new SourceFile(x, Path.GetRelativePath(downloads, x), new FileInfo(x).Length)).ToList(), libraries);
    var subtitle = packPlan.Operations.Single(x => x.Role == "subtitle").Destination; var packVideo = packPlan.Operations.Single(x => x.Role == "video").Destination;
    Directory.CreateDirectory(Path.GetDirectoryName(subtitle)!); File.WriteAllBytes(subtitle, new byte[3]);
    var pack = Finished('d', "Pack.S02E01", "eztv");
    await importer.ProcessAsync(state, libraries, ImportMode.Move, automatic: true);
    Assert(importer.Find(pack) is { State: TorLinkImportState.Partial } && File.Exists(packVideo) && !File.Exists(packFiles[0]), "Move with a conflicting subtitle");
    File.Delete(subtitle);
    await importer.ProcessAsync(state, libraries, ImportMode.Move, automatic: true, requested: [pack]);
    Assert(importer.Find(pack) is { State: TorLinkImportState.Imported, Files.Count: 2 } && File.Exists(subtitle) && new FileInfo(subtitle).Length == 5 && File.Exists(packVideo) && !File.Exists(packFiles[1]),
        "The rest of a moved download could not be placed any more: " + importer.Find(pack)?.Message);
    // Emptied by the move, the download's folder leaves TorLink's; TorLink's own folder stays.
    Assert(!Directory.Exists(Path.Combine(downloads, "Pack.S02E01")) && Directory.Exists(downloads), "The emptied download folder stayed in TorLink's, or TorLink's folder went");
    Assert(importer.Find(pack) is { MovedOut: true, SeedPaused: false } && importer.MovedSeeds().Contains(pack) && !importer.MovedSeeds().Contains(dune), "A moved download is not set for its sharing to pause");

    // Before Mira starts TorLink again: the moved download stops being shared; anything else is left as it was.
    var other = new string('f', 40);
    File.WriteAllText(state.SeedsFile, JsonSerializer.Serialize(new object[] { pack.ToUpperInvariant(), new { id = other, status = "seeding" } }));
    var paused = state.PauseSeeds([pack]);
    var seeds = JsonDocument.Parse(File.ReadAllText(state.SeedsFile)).RootElement.EnumerateArray().ToList();
    Assert(paused.SequenceEqual([pack]) && seeds.Count == 2 && seeds[0].GetProperty("status").GetString() == "paused" && seeds[1].GetProperty("status").GetString() == "seeding" && seeds[1].GetProperty("id").GetString() == other,
        "seeds.json after pausing: " + File.ReadAllText(state.SeedsFile));
    importer.MarkSeedPaused(paused);
    Assert(!importer.MovedSeeds().Contains(pack) && state.PauseSeeds([pack]).SequenceEqual([pack]), "A paused download is paused again, or no longer found");
    File.WriteAllText(state.SeedsFile, "[{\"id\":");
    Assert(state.PauseSeeds([pack]).Count == 0 && File.ReadAllText(state.SeedsFile) == "[{\"id\":", "A half-written seeds.json was rewritten");
    Assert(new TorLinkState(Path.Combine(dir, "none"), Path.Combine(dir, "none")).PauseSeeds([pack]).Count == 0, "Pausing without seeds.json");

    // An unreadable log is kept aside instead of lost; incomplete entries are dropped without failing.
    var broken = Path.Combine(dir, "broken"); Directory.CreateDirectory(broken);
    File.WriteAllText(Path.Combine(broken, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":[{\"Id\":");
    Assert(new TorLinkImporter(broken).Entries.Count == 0 && Directory.EnumerateFiles(broken, "torlink-imports.json.bad-*").Any(), "An unreadable log was overwritten without a copy");
    var partial = Path.Combine(dir, "partial"); Directory.CreateDirectory(partial);
    File.WriteAllText(Path.Combine(partial, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":[null,{\"Id\":\"zz\"},{\"Id\":\"" + new string('e', 40) + "\",\"Files\":null,\"Title\":null}]}");
    Assert(new TorLinkImporter(partial) is { Entries: [{ Files.Count: 0, Title: "" }] } loaded && loaded.Baseline.Year == 2026, "A log with incomplete entries");
    File.WriteAllText(Path.Combine(partial, "torlink-imports.json"), "{\"Baseline\":\"2026-01-01T00:00:00+00:00\",\"Entries\":null}");
    Assert(new TorLinkImporter(partial).Entries.Count == 0, "A log without entries");
});
await Test("TorLink : fichiers exacts du .torrent, chemins normalisés et fichiers incomplets attendus", () =>
{
    var multi = Bencode.ReadTorrent(TorrentBytes("Film: Cut?", [(["a", "b?.mkv"], 1)]))!;
    Assert(multi.Name == "Film: Cut?" && multi.Files.Single().RelativePath == Path.Combine("Film: Cut?", "a", "b.mkv"), "Multi-file layout differs from WebTorrent");
    var single = Bencode.ReadTorrent(TorrentBytes("Film: Cut?.mkv", [([], 7)], single: true))!;
    Assert(single.Files.Single() == new TorrentFileEntry("Film Cut.mkv", 7), "Single-file name not sanitised like fs-chunk-store");
    Assert(Bencode.ReadTorrent(Encoding.ASCII.GetBytes("d4:infod4:name")) is null && Bencode.ReadTorrent(Encoding.ASCII.GetBytes("l99999999999:")) is null, "Corrupt metadata accepted");
    var dir = Path.Combine(testRoot, "torrent-files"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data")); var downloads = Path.Combine(dir, "downloads");
    var id = new string('d', 40); const string name = "Pack.2004";
    Directory.CreateDirectory(state.TorrentsDirectory); Directory.CreateDirectory(Path.Combine(downloads, name, "Subs"));
    File.WriteAllBytes(Path.Combine(state.TorrentsDirectory, id + ".torrent"), TorrentBytes(name, [(["Pack.2004.mkv"], 20), (["Subs", "English.srt"], 5), (["..", "..", "notes.txt"], 3)]));
    File.WriteAllBytes(Path.Combine(downloads, name, "Pack.2004.mkv"), new byte[20]); File.WriteAllBytes(Path.Combine(downloads, name, "Subs", "English.srt"), new byte[4]);
    var completion = new TorLinkCompletion(id, name, "yts", 25, downloads, DateTimeOffset.UtcNow);
    Assert(TorLinkImporter.DownloadedFiles(state, completion) is null, "An incomplete subtitle was accepted");
    File.WriteAllBytes(Path.Combine(downloads, name, "Subs", "English.srt"), new byte[5]);
    var files = TorLinkImporter.DownloadedFiles(state, completion);
    Assert(files is { Count: 2 } && files.Any(x => x.RelativePath == Path.Combine(name, "Subs", "English.srt")) && files.All(x => x.Path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)), "Torrent file list not used exactly");
    return Task.CompletedTask;
});
await Test("Jellyfin : dossiers des bibliothèques et signalement des médias, sans déconnexion en cas de refus", async () =>
{
    var requests = new List<(string Method, string Path, string Body)>(); var refuse = false;
    using var client = new JellyfinClient(new("http://localhost/jellyfin/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        lock (requests) requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
        if (refuse) return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (request.RequestUri.AbsolutePath.EndsWith("/Library/VirtualFolders")) return JsonResponse(new object[]
        {
            new { Name = "Movies", CollectionType = "movies", Locations = new[] { @"C:\Media\FILMS" } }, new { Name = "Anime", CollectionType = "tvshows", Locations = new[] { @"C:\Media\ANIME" } },
            new { Name = "Shows", CollectionType = "tvshows", Locations = new[] { @"C:\Media\SERIES" } }, new { Name = "Music", CollectionType = "music", Locations = new[] { @"C:\Media\MUSIC" } }
        });
        if (request.RequestUri.AbsolutePath.EndsWith("/Items")) return JsonResponse(new { Items = new object[]
        {
            new { Id = "movie-1", Type = "Movie", Path = @"C:\Media\FILMS\Big Buck Bunny (2008)\Big Buck Bunny (2008).mkv" },
            new { Id = "episode-1", Type = "Episode", SeriesId = "series-9", Path = @"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv" }
        }, TotalRecordCount = 2 });
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }));
    var folders = await client.VirtualFoldersAsync();
    Assert(MediaLibraries.FromJellyfin(folders) == new MediaLibraries(@"C:\Media\FILMS", @"C:\Media\SERIES", @"C:\Media\ANIME"), "Libraries not matched to films, series and anime");
    Assert(MediaLibraries.FromJellyfin(folders, anime: @"D:\Anime").Anime == @"D:\Anime" && MediaLibraries.FromJellyfin(null).IsEmpty, "Manual folder or missing server data");
    Assert(await client.ReportMediaChangedAsync([@"C:\Media\FILMS\Big Buck Bunny (2008)"]), "Media report failed");
    var report = requests.Last();
    Assert(report.Method == "POST" && report.Path == "/jellyfin/Library/Media/Updated" && report.Body.Contains("\"Updates\"") && report.Body.Contains("\"UpdateType\":\"Created\"") && report.Body.Contains("Big Buck Bunny (2008)"), "Media report request changed: " + report.Body);
    var before = requests.Count;
    var indexed = await client.FindIndexedAsync([@"c:\media\films\big buck bunny (2008)\big buck bunny (2008).mkv", @"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv", @"C:\Elsewhere.mkv"]);
    Assert(indexed.Count == 2 && indexed[@"C:\MEDIA\FILMS\Big Buck Bunny (2008)\Big Buck Bunny (2008).mkv"] == "movie-1" && indexed[@"C:\Media\SERIES\Show\Season 01\Show - S01E02.mkv"] == "series-9"
        && !indexed.ContainsKey(@"C:\Elsewhere.mkv") && requests.Count == before + 1, "Indexed file lookup, in a single request");
    Assert((await client.FindIndexedAsync([])).Count == 0 && requests.Count == before + 1, "An empty lookup reached the server");
    Assert(requests.Any(x => x.Path.Contains("fields=Path") && x.Path.Contains("includeItemTypes=Movie,Episode")), "Indexed lookup query changed");
    VirtualFolder Tv(string name, string folder) => new(name, "tvshows", [folder]);
    Assert(MediaLibraries.FromJellyfin([Tv("Dessins animés", @"D:\Cartoons"), Tv("Séries", @"D:\TV")]).Anime is null
        && MediaLibraries.FromJellyfin([Tv("Animation", @"D:\Animation"), Tv("Animaux", @"D:\Nature")]).Anime is null
        && MediaLibraries.FromJellyfin([Tv("Séries", @"D:\TV"), Tv("Japon", @"D:\Médias\Animés")]) is { Anime: @"D:\Médias\Animés", Series: @"D:\TV" }, "Anime library matched on a partial word");
    refuse = true;
    Assert(await client.VirtualFoldersAsync() is null && !await client.ReportMediaChangedAsync([@"C:\Media\FILMS\x"]), "A refused optional call must not end the session");
});
await Test("Pseudo-console : sortie UTF-8, taille et code de sortie d’un programme console", async () =>
{
    var output = new StringBuilder();
    var exitedEarly = new TaskCompletionSource<int>();
    using var console = Mira.Desktop.TorLink.PseudoConsole.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "echo mira-conpty-ok é & mode con"], testRoot, EnvironmentBlock(), 90, 20,
        text => { lock (output) output.Append(text); }, code => exitedEarly.TrySetResult(code));
    var code = await console.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(await exitedEarly.Task.WaitAsync(TimeSpan.FromSeconds(2)) == code, "The exit callback given at start was not called");
    // The exit can be reported before the last output is read: wait for the text itself.
    string Text() { lock (output) return Plain(output.ToString()); }
    try { await WaitUntil(() => Text() is var t && t.Contains("mira-conpty-ok é") && t.Contains("90") && t.Contains("20"), 6); } catch (TimeoutException) { }
    Assert(code == 0, "Console program exit code lost: " + code);
    Assert(Text() is var text && text.Contains("mira-conpty-ok é") && text.Contains("90") && text.Contains("20"), "Pseudo console output or size lost: " + Text());
});
await Test("Lecteur : « Épisode 3 / 12 », pistes nommées en français, vitesse et décalage", () =>
{
    MediaItem Episode(string id, int season, int number, string name = "") => new() { Id = id, Type = "Episode", SeriesName = "Sword Art Online", Name = name, ParentIndexNumber = season, IndexNumber = number };
    var single = Enumerable.Range(1, 25).Select(n => Episode($"e{n}", 1, n, n == 1 ? "Le monde des épées" : $"Episode {n}")).ToList();
    Assert(PlayerText.Subtitle(single[0], single) == "Épisode 1 / 25 · Le monde des épées", "Episode line: " + PlayerText.Subtitle(single[0], single));
    Assert(PlayerText.Subtitle(single[2], single) == "Épisode 3 / 25", "A generic name is repeated: " + PlayerText.Subtitle(single[2], single));
    Assert(PlayerText.Subtitle(single[0]) == "Épisode 1 · Le monde des épées", "Before the episode list: " + PlayerText.Subtitle(single[0]));
    var seasons = single.Take(12).Concat(Enumerable.Range(1, 10).Select(n => Episode($"s2e{n}", 2, n))).Append(Episode("sp1", 0, 1, "OVA")).ToList();
    Assert(PlayerText.Subtitle(seasons[0], seasons) == "Saison 1 · Épisode 1 / 12 · Le monde des épées" && PlayerText.Subtitle(seasons[13], seasons) == "Saison 2 · Épisode 2 / 10"
        && PlayerText.Subtitle(seasons[^1], seasons) == "Hors-série · Épisode 1 / 1 · OVA", "Seasons: " + PlayerText.Subtitle(seasons[13], seasons));
    Assert(PlayerText.Subtitle(Episode("e30", 1, 30), single.Take(10).ToList()) == "Épisode 30", "A partial list claims a total");
    Assert(PlayerText.Subtitle(new MediaItem { Name = "Your Name", Type = "Movie", ProductionYear = 2016, RunTimeTicks = TimeSpan.FromMinutes(106).Ticks }) == "2016  ·  1 h 46", "Film line changed");
    Assert(PlayerText.Track("2", null, "jpn", "aac", 2) == ("Japonais", "AAC · stéréo") && PlayerText.Track("3", "Signs & Songs", "fre", "ass", forced: true) == ("Signs & Songs", "Français · ASS · forcés")
        && PlayerText.Track("4", "", "und", "") == ("Piste 4", "") && PlayerText.Track("5", "japanese", "pt-BR", "eac3", 6) == ("Japanese", "Portugais · Dolby Digital Plus · 5.1"), "Track names");
    Assert(PlayerText.Speed(.75) == "0,75×" && PlayerText.Speed(1) == "1×" && PlayerText.Delay(.5) == "+0,5 s" && PlayerText.Delay(-1.5) == "−1,5 s" && PlayerText.Delay(0) == "0,0 s", "Speed or offset formatting");
    return Task.CompletedTask;
});
await Test("Mises à jour : manifeste signé accepté ; modifié, mal formé ou d’une autre clé, refusé", () =>
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var manifest = new UpdateManifest("Mira", new Version(0, 5, 3), [new("zip", "Mira-0.5.3-win-x64.zip", 10, new string('a', 64))]).ToJson();
    var signature = UpdateSignature.Sign(manifest, key); string[] trusted = [UpdateSignature.PublicKey(key)];
    Assert(UpdateSignature.Verify(manifest, signature, trusted), "Valid signature refused");
    var altered = manifest.ToArray(); altered[^3] ^= 1;
    Assert(!UpdateSignature.Verify(altered, signature, trusted), "Altered manifest accepted");
    Assert(!UpdateSignature.Verify(manifest, UpdateSignature.Sign(manifest, other), trusted) && !UpdateSignature.Verify(manifest, signature, ["garbage", ""]), "Another or an invalid key accepted");
    Assert(!UpdateSignature.Verify(manifest, signature[..63], trusted) && UpdateSignature.Decode("pas du base64") is null && UpdateSignature.Decode(UpdateSignature.Encode(signature)) is { Length: 64 }, "Signature encoding");
    Assert(UpdateKeys.Trusted.Count > 0 && UpdateKeys.Trusted.All(k => { using var e = ECDsa.Create(); e.ImportSubjectPublicKeyInfo(Convert.FromBase64String(k), out _); return e.KeySize == 256; }), "Built-in key missing or not P-256");
    var parsed = UpdateManifest.Parse(manifest);
    Assert(parsed.Version == new Version(0, 5, 3) && parsed.Files.Single() is { Kind: "zip", Name: "Mira-0.5.3-win-x64.zip", Size: 10 }, "Manifest round trip");
    string Json(string files, string product = "Mira", string version = "0.5.3") => $$"""{"product":"{{product}}","version":"{{version}}","files":[{{files}}]}""";
    var good = $$"""{"kind":"zip","name":"Mira-0.5.3-win-x64.zip","size":10,"sha256":"{{new string('a', 64)}}"}""";
    foreach (var bad in new[] { Json(good, product: "Autre"), Json(good, version: "0.5"), Json(""), Json(good.Replace("Mira-0.5.3-win-x64.zip", "..\\\\evil.exe")), Json(good.Replace("\"size\":10", "\"size\":0")),
        Json(good.Replace(new string('a', 64), "abc")), Json(good + "," + good), Json(good.Replace("\"zip\"", "\"script\"")), "{" })
    {
        var rejected = false; try { UpdateManifest.Parse(Encoding.UTF8.GetBytes(bad)); } catch (UpdateException) { rejected = true; }
        Assert(rejected, "Invalid manifest accepted: " + bad);
    }
    return Task.CompletedTask;
});
await Test("Mises à jour : la version signée la plus récente est proposée, jamais une antérieure ; téléchargement vérifié", async () =>
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var files = new Dictionary<string, byte[]>();
    byte[] zip = Encoding.UTF8.GetBytes("nouvelle archive"), setup = Encoding.UTF8.GetBytes("nouvel installateur");
    string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var manifest = new UpdateManifest("Mira", new Version(0, 5, 3), [new("zip", "Mira-0.5.3-win-x64.zip", zip.Length, Sha(zip)), new("installer", "Mira-0.5.3-win-x64-setup.exe", setup.Length, Sha(setup))]).ToJson();
    files["/0.5.3/mira-update.json"] = manifest; files["/0.5.3/mira-update.json.sig"] = Encoding.ASCII.GetBytes(UpdateSignature.Encode(UpdateSignature.Sign(manifest, key)));
    files["/0.5.3/Mira-0.5.3-win-x64.zip"] = zip; files["/0.5.3/Mira-0.5.3-win-x64-setup.exe"] = setup;
    object Asset(string version, string name) => new { name, size = files[$"/{version}/{name}"].Length, browser_download_url = $"https://github.com/sasou-web/Mira/releases/download/v{version}/{name}" };
    var releases = new object[]
    {
        new { tag_name = "v0.6.0", draft = true, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.6.0", assets = new[] { Asset("0.5.3", "mira-update.json") } },
        new { tag_name = "v0.5.4", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.4", assets = Array.Empty<object>() },
        new { tag_name = "nightly", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/nightly", assets = Array.Empty<object>() },
        new { tag_name = "v0.5.3", draft = false, prerelease = true, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.3",
            assets = new[] { Asset("0.5.3", "mira-update.json"), Asset("0.5.3", "mira-update.json.sig"), Asset("0.5.3", "Mira-0.5.3-win-x64.zip"), Asset("0.5.3", "Mira-0.5.3-win-x64-setup.exe") } },
        new { tag_name = "v0.5.1", draft = false, html_url = "https://github.com/sasou-web/Mira/releases/tag/v0.5.1", assets = Array.Empty<object>() }
    };
    var requested = new List<Uri>();
    using var handler = new Handler(request =>
    {
        var url = request.RequestUri!; requested.Add(url);
        if (url.Host == "api.github.com") return Task.FromResult(JsonResponse(releases));
        var path = url.AbsolutePath.Replace("/sasou-web/Mira/releases/download/v", "/");
        return Task.FromResult(files.TryGetValue(path, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound));
    });
    async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch (UpdateException) { return true; } }
    using var client = new UpdateClient(new Version(0, 5, 2), [UpdateSignature.PublicKey(key)], handler: handler);
    var offer = await client.CheckAsync("zip");
    Assert(offer is { Version: { Major: 0, Minor: 5, Build: 3 }, File.Name: "Mira-0.5.3-win-x64.zip" } && offer.Page?.AbsoluteUri == "https://github.com/sasou-web/Mira/releases/tag/v0.5.3", "Newest signed release not offered");
    Assert((await client.CheckAsync("installer"))?.File.Name == "Mira-0.5.3-win-x64-setup.exe" && await Fails(() => client.CheckAsync("portable")), "File for the installation kind");
    Assert(requested.All(u => u.Host is "api.github.com" or "github.com"), "Another host was contacted");
    using (var same = new UpdateClient(new Version(0, 5, 3), [UpdateSignature.PublicKey(key)], handler: handler)) Assert(await same.CheckAsync("zip") is null, "The running version is offered again");
    using (var newer = new UpdateClient(new Version(0, 6, 1), [UpdateSignature.PublicKey(key)], handler: handler)) Assert(await newer.CheckAsync("zip") is null, "An older version is offered");
    using (var stranger = new UpdateClient(new Version(0, 5, 2), [UpdateSignature.PublicKey(other)], handler: handler)) Assert(await Fails(() => stranger.CheckAsync("zip")), "A manifest signed by an unknown key is accepted");
    var folder = Path.Combine(testRoot, "update-download");
    var path = await client.DownloadAsync(offer!, folder);
    Assert(File.ReadAllBytes(path).SequenceEqual(zip) && !File.Exists(path + ".partial") && UpdateClient.Matches(path, offer!.File), "Download not kept or not verified");
    Assert(await client.DownloadAsync(offer!, folder) == path, "A verified download is fetched again");
    File.Delete(path); files["/0.5.3/Mira-0.5.3-win-x64.zip"] = Encoding.UTF8.GetBytes("nouvelle archivf");
    Assert(await Fails(() => client.DownloadAsync(offer!, folder)) && !Directory.EnumerateFiles(folder).Any(), "A download that does not match the signature was kept");
    Assert(await Fails(() => client.DownloadAsync(offer! with { Url = new Uri("https://example.com/Mira.zip") }, folder)) && requested.All(u => u.Host != "example.com"), "A download outside GitHub was attempted");
    var feedRefused = false; try { new UpdateClient(new Version(0, 5, 2), [], new Uri("https://example.com/releases")).Dispose(); } catch (ArgumentException) { feedRefused = true; }
    Assert(feedRefused, "A feed outside GitHub was accepted");
});
await Test("Mises à jour : chaque redirection vérifiée, reprise d’un téléchargement interrompu, réponse inattendue refusée", async () =>
{
    var payload = Encoding.UTF8.GetBytes(new string('m', 200_000));
    var file = new UpdateFile("zip", "Mira-0.5.3-win-x64.zip", payload.Length, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
    var requested = new List<Uri>(); var honourRange = true; var redirect = "https://release-assets.githubusercontent.com/github-production-release-asset/1/a";
    using var handler = new Handler(request =>
    {
        var url = request.RequestUri!; requested.Add(url);
        if (url.Host == "github.com") { var moved = new HttpResponseMessage(HttpStatusCode.Found); moved.Headers.Location = new Uri(redirect); return Task.FromResult(moved); }
        if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from && honourRange)
        {
            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[(int)from..]) };
            rest.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, payload.Length - 1, payload.Length);
            return Task.FromResult(rest);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    });
    async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch (UpdateException) { return true; } }
    using var client = new UpdateClient(new Version(0, 5, 2), [], handler: handler);
    var offer = new UpdateOffer(new Version(0, 5, 3), "v0.5.3", null, file, new Uri("https://github.com/sasou-web/Mira/releases/download/v0.5.3/Mira-0.5.3-win-x64.zip"));
    var folder = Path.Combine(testRoot, "update-resume"); Directory.CreateDirectory(folder);
    // Interrupted after 80 000 bytes: the rest is asked for with Range and the hash covers the whole file.
    File.WriteAllBytes(Path.Combine(folder, file.Name + ".partial"), payload[..80_000]);
    var path = await client.DownloadAsync(offer, folder);
    Assert(File.ReadAllBytes(path).SequenceEqual(payload) && requested.Any(u => u.Host == "release-assets.githubusercontent.com"), "Download not resumed through GitHub's file server");
    File.Delete(path); File.WriteAllBytes(Path.Combine(folder, file.Name + ".partial"), payload[..80_000]); honourRange = false;
    Assert(File.ReadAllBytes(await client.DownloadAsync(offer, folder)).SequenceEqual(payload), "A server ignoring Range was not downloaded again from the start");
    File.Delete(Path.Combine(folder, file.Name)); requested.Clear(); redirect = "https://evil.example/Mira.zip";
    Assert(await Fails(() => client.DownloadAsync(offer, folder)) && requested.All(u => u.Host != "evil.example"), "A redirect outside GitHub was followed");
    redirect = "http://release-assets.githubusercontent.com/x";
    Assert(await Fails(() => client.DownloadAsync(offer, folder)) && requested.All(u => u.Scheme == "https"), "A redirect to plain HTTP was followed");
    foreach (var bad in new[] { "[1, \"x\", null]", "[{\"tag_name\":\"v0.5.3\",\"assets\":[1,{\"name\":\"a\",\"size\":\"big\",\"browser_download_url\":\"https://github.com/a\"}]}]", "{\"message\":\"Not Found\"}" })
    {
        var parsed = false; try { var releases = ReleaseFeed.Parse(bad); parsed = releases.All(r => r.Assets.All(a => a.Size == -1)); } catch (UpdateException) { parsed = true; }
        Assert(parsed, "Unexpected JSON not handled: " + bad);
    }
    var manifestRefused = false; try { UpdateManifest.Parse(Encoding.UTF8.GetBytes("{\"product\":\"Mira\",\"version\":\"0.5.3\",\"files\":[1,2]}")); } catch (UpdateException) { manifestRefused = true; }
    Assert(manifestRefused, "A manifest with unexpected entries was not refused cleanly");
});
await Test("Mises à jour : copie en deux temps, dossier intact si la préparation échoue, résultat annoncé une fois", () =>
{
    var root = Path.Combine(testRoot, "update-two-phase"); var app = Path.Combine(root, "app"); var incoming = Path.Combine(root, "incoming");
    Directory.CreateDirectory(Path.Combine(app, "data", "updates")); Directory.CreateDirectory(incoming);
    File.WriteAllText(Path.Combine(app, "Mira.dll"), "ancienne"); File.WriteAllText(Path.Combine(app, "b.dll"), "ancienne b");
    File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "nouvelle"); File.WriteAllText(Path.Combine(incoming, "b.dll"), "nouvelle b");
    // The new file cannot even be prepared: nothing of the installed copy has moved.
    using (new FileStream(Path.Combine(app, "b.dll" + UpdateApplier.NewSuffix), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        var failed = false; try { UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-a"), attempts: 2); } catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(Path.Combine(app, "Mira.dll")) == "ancienne" && File.ReadAllText(Path.Combine(app, "b.dll")) == "ancienne b" && !File.Exists(Path.Combine(app, "Mira.dll" + UpdateApplier.NewSuffix)),
            "A failed preparation touched the installed copy");
    }
    UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-b"));
    Assert(File.ReadAllText(Path.Combine(app, "b.dll")) == "nouvelle b" && !Directory.EnumerateFiles(app, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories).Any(), "Two-phase copy incomplete");
    // Result of the last installation, reported once whatever the updater of this run.
    var pending = Path.Combine(app, "data", "updates", "pending.json");
    File.WriteAllText(pending, $$"""{"Version":"{{JellyfinClient.AppVersion}}","From":"0.0.1","Attempts":1,"Reported":false}""");
    Assert(Updater.TakeResult(Path.Combine(app, "data")) is { Installed: true } && !File.Exists(pending), "An installed version was not reported, or its record stayed");
    File.WriteAllText(pending, """{"Version":"99.0.0","From":"0.0.1","Attempts":1,"Reported":false}""");
    Assert(Updater.TakeResult(Path.Combine(app, "data")) is { Installed: false } && Updater.TakeResult(Path.Combine(app, "data")) is null && File.Exists(pending), "A failed installation was not reported exactly once");
    return Task.CompletedTask;
});
await Test("Mises à jour : type d’installation, dossier remplacé sans toucher aux données, retour arrière sur erreur", () =>
{
    var root = Path.Combine(testRoot, "update-apply"); var app = Path.Combine(root, "app"); var incoming = Path.Combine(root, "incoming");
    Directory.CreateDirectory(Path.Combine(app, "data")); Directory.CreateDirectory(Path.Combine(incoming, "sub")); Directory.CreateDirectory(Path.Combine(incoming, "data"));
    File.WriteAllText(Path.Combine(app, "Mira.dll"), "ancienne"); File.WriteAllText(Path.Combine(app, "seulement-avant.txt"), "gardé"); File.WriteAllText(Path.Combine(app, "data", "settings.json"), "mes réglages");
    File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "nouvelle"); File.WriteAllText(Path.Combine(incoming, "sub", "ajout.txt"), "ajouté"); File.WriteAllText(Path.Combine(incoming, "data", "settings.json"), "autres réglages");
    Assert(Updater.Detect(app, singleFile: true) == InstallKind.Portable && Updater.Detect(app, singleFile: false) == InstallKind.Folder
        && Updater.Detect(@"C:\src\Mira\src\Mira.Desktop\bin\Release\net8.0-windows10.0.19041.0\", false) == InstallKind.Development, "Installation kind");
    UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-1"));
    Assert(File.ReadAllText(Path.Combine(app, "Mira.dll")) == "nouvelle" && File.ReadAllText(Path.Combine(app, "sub", "ajout.txt")) == "ajouté" && File.ReadAllText(Path.Combine(app, "seulement-avant.txt")) == "gardé", "Folder not updated");
    Assert(File.ReadAllText(Path.Combine(app, "data", "settings.json")) == "mes réglages" && !Directory.Exists(Path.Combine(app, "data", "updates", "rollback-1")), "Data touched or rollback copy left behind");
    // A file that cannot be replaced: what was already replaced comes back, and nothing new stays.
    File.WriteAllText(Path.Combine(incoming, "Mira.dll"), "plus récente"); File.WriteAllText(Path.Combine(incoming, "verrou.bin"), "x"); File.WriteAllText(Path.Combine(app, "verrou.bin"), "ancien");
    using (new FileStream(Path.Combine(app, "verrou.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var failed = false; try { UpdateApplier.CopyFolder(incoming, app, Path.Combine(app, "data", "updates", "rollback-2"), attempts: 2); } catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(Path.Combine(app, "Mira.dll")) == "nouvelle" && File.ReadAllText(Path.Combine(app, "data", "settings.json")) == "mes réglages"
            && !Directory.EnumerateFiles(app, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories).Any(), "A failed update was not rolled back, or left new files behind");
    }
    File.WriteAllText(Path.Combine(app, "unins000.exe"), ""); File.WriteAllText(Path.Combine(app, "unins000.dat"), "");
    Assert(Updater.Detect(app, singleFile: false) == InstallKind.Installer, "Inno Setup installation not recognised");
    var exe = Path.Combine(root, "Mira-portable.exe"); var fresh = Path.Combine(root, "nouveau.exe"); File.WriteAllText(exe, "ancien exe"); File.WriteAllText(fresh, "nouvel exe");
    UpdateApplier.ReplaceFile(fresh, exe);
    Assert(File.ReadAllText(exe) == "nouvel exe" && !File.Exists(exe + ".previous") && !File.Exists(exe + ".update"), "Portable executable not replaced cleanly");
    return Task.CompletedTask;
});
if (args.Contains("--torlink-integration"))
{
    await Test("TorLink réel : écran d’accueil dans la pseudo-console, redimensionnement, arrêt propre et état isolé", async () =>
    {
        var installation = Mira.Desktop.TorLink.TorLinkInstallation.Locate(null) ?? throw new Exception("TorLink not found on this PC");
        Assert(installation.HasRuntime, "Node.js runtime not found for TorLink");
        var state = Path.Combine(testRoot, "torlink-state");
        var real = TorLinkState.ForCurrentUser(stateOverride: null);
        var realStamp = File.Exists(real.QueueFile) ? File.GetLastWriteTimeUtc(real.QueueFile) : DateTime.MinValue;
        var environment = EnvironmentBlock(); environment["TORLINK_STATE_DIR"] = state; environment["COLORTERM"] = "truecolor";
        var before = installation.RunningElsewhere(null);
        var output = new StringBuilder();
        using var console = Mira.Desktop.TorLink.PseudoConsole.Start(installation.Node, [installation.Entry], installation.Root, environment, 110, 32, text => { lock (output) output.Append(text); });
        await WaitUntil(() => { lock (output) return Plain(output.ToString()).Contains("torrent downloader"); }, 20);
        Assert(before is not null || installation.RunningElsewhere(null) == console.ProcessId, "A running TorLink is not detected from its command line");
        Assert(installation.RunningElsewhere(console.ProcessId) == before, "Mira's own TorLink is reported as another instance");
        console.Resize(130, 40); await Task.Delay(600);
        Assert(!console.HasExited, "TorLink stopped after a resize");
        console.Write("\u0003");
        var code = await console.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        Assert(code == 0, "TorLink did not quit cleanly on Ctrl+C: " + code);
        Assert(File.Exists(Path.Combine(state, "data", "queue.json")), "TorLink did not use the isolated state folder");
        Assert((File.Exists(real.QueueFile) ? File.GetLastWriteTimeUtc(real.QueueFile) : DateTime.MinValue) == realStamp, "The real TorLink queue was touched");
        await WaitUntil(() => installation.RunningElsewhere(null) == before, 5);
    });
}
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
static async Task WaitUntil(Func<bool> ready, int seconds = 4) { var timeout = DateTime.UtcNow.AddSeconds(seconds); while (!ready()) { if (DateTime.UtcNow > timeout) throw new TimeoutException(); await Task.Delay(20); } }
static Dictionary<string, string> EnvironmentBlock()
{
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        if (entry.Key is string key && key.Length > 0 && !key.StartsWith('=') && entry.Value is string value) values[key] = value;
    return values;
}
// Minimal bencode writer for synthetic .torrent metadata (dictionary keys sorted, as the format requires).
static byte[] Ben(object value)
{
    using var stream = new MemoryStream();
    void Write(object item)
    {
        switch (item)
        {
            case long number: stream.Write(Encoding.ASCII.GetBytes($"i{number}e")); break;
            case string text: var bytes = Encoding.UTF8.GetBytes(text); stream.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:")); stream.Write(bytes); break;
            case IEnumerable<KeyValuePair<string, object>> map: stream.WriteByte((byte)'d'); foreach (var (key, entry) in map.OrderBy(x => x.Key, StringComparer.Ordinal)) { Write(key); Write(entry); } stream.WriteByte((byte)'e'); break;
            case System.Collections.IEnumerable list: stream.WriteByte((byte)'l'); foreach (var entry in list) Write(entry!); stream.WriteByte((byte)'e'); break;
        }
    }
    Write(value);
    return stream.ToArray();
}
static byte[] TorrentBytes(string name, (string[] Path, long Length)[] files, bool single = false)
{
    var info = new Dictionary<string, object> { ["name"] = name, ["piece length"] = 16384L, ["pieces"] = "" };
    if (single) info["length"] = files[0].Length;
    else info["files"] = files.Select(f => (object)new Dictionary<string, object> { ["length"] = f.Length, ["path"] = f.Path.Cast<object>().ToList() }).ToList();
    return Ben(new Dictionary<string, object> { ["announce"] = "udp://tracker.invalid:1337", ["info"] = info });
}
// Terminal text without VT control sequences (cursor moves, colours, titles).
static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-_]", "");
/// <summary>Progress reported on the caller's thread, in order (Progress<T> would post it to the thread pool).</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request).WaitAsync(cancellationToken); }
