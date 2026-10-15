using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Télécharger sur le PC serveur »: what may be sent (RemoteDownloads, cases shared with Mira web), the extension's
/// relay (TorLinkRelay, on a clock of its own) and Mira for Windows' side (TorLinkRemote, TorLinkServeClient).
/// </summary>
internal static class RemoteDownloadTests
{
    private const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", Other = "0123456789abcdef0123456789abcdef01234567";
    private static string Magnet(string hash, string name = "Dune") => $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Télécharger sur le PC serveur : liens magnet, recherches et noms vérifiés comme Mira web (tests/fixtures/torlink-remote.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "torlink-remote.json")));
            static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString();
            foreach (var pair in fixture.RootElement.GetProperty("magnets").EnumerateArray())
                Assert(RemoteDownloads.InfoHash(pair[0].GetString()) == Text(pair[1]), $"Info hash of « {pair[0]} »: {RemoteDownloads.InfoHash(pair[0].GetString())}");
            foreach (var pair in fixture.RootElement.GetProperty("queries").EnumerateArray())
                Assert(RemoteDownloads.CleanQuery(pair[0].GetString()) == Text(pair[1]), $"Search « {pair[0]} »: « {RemoteDownloads.CleanQuery(pair[0].GetString())} »");
            foreach (var pair in fixture.RootElement.GetProperty("names").EnumerateArray())
                Assert(RemoteDownloads.DisplayName(pair[0].GetString()) == Text(pair[1]), $"Name of « {pair[0]} »: « {RemoteDownloads.DisplayName(pair[0].GetString())} »");
            Assert(RemoteDownloads.InfoHash($"magnet:?xt=urn:btih:{Hash}&tr={new string('x', 8192)}") is null && RemoteDownloads.InfoHash(null) is null, "Too long, or nothing");
            Assert(RemoteDownloads.CleanName(new string('x', 301)) == new string('x', 300) + "…" && RemoteDownloads.CleanName(" \u0007 ") is null, "Names");
            Assert(RemoteDownloads.Categories.SequenceEqual(["movies", "tv", "anime"]) && !RemoteDownloads.IsCategory("games") && !RemoteDownloads.IsAction("stop-seed"), "Categories and actions");
            Assert(TorLinkRemote.SearchArguments("cli.cjs", "dune", "movies").SequenceEqual(["cli.cjs", "search", "--category", "movies", "dune"]), "The search goes last, in one argument");
            Assert(TorLinkRemote.ServeArguments("cli.cjs", 4321).SequenceEqual(["cli.cjs", "serve", "--host", "127.0.0.1", "--port", "4321"]), "TorLink's API on this PC only");
            return Task.CompletedTask;
        });

        await Test("Télécharger sur le PC serveur : le relais de l'extension, de la recherche au téléchargement", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
            var relay = new TorLinkRelay(() => now);
            AgentReport Report(bool accepting = true, List<AgentSearchOutcome>? searches = null, List<AgentOutcome>? requests = null, List<AgentOutcome>? controls = null,
                List<RelayDownload>? downloads = null, List<RelayPlaced>? placed = null) => new("PC-SALON", accepting, accepting ? null : "TorLink est à l’écran sur le PC serveur.", downloads, placed, searches, requests, controls);

            // No Mira on the PC: a search has nobody to run it; a magnet link waits for it.
            Assert(relay.Status().Agent is null, "An agent before any visit");
            Assert(relay.Search("dune", "movies") == (null, "Mira n’est pas ouvert sur le PC serveur : c’est lui qui cherche, avec TorLink."), "Search without an agent");
            var (early, _) = relay.Request(Magnet(Hash), null, null, null, null);
            Assert(early is not null && relay.Status().Requests.Single() is { State: "waiting", Name: "Dune", InfoHash: Hash }, "A pasted link waits");

            var work = relay.Exchange("pc-1", Report());
            Assert(work.Requests.Single() is { Magnet: var m, Name: "Dune" } && m == Magnet(Hash) && work.Next == TorLinkRelay.Hurry && work.Busy is null, "The waiting link goes to the agent");
            Assert(relay.Status() is { Agent: { Name: "PC-SALON", Online: true, Accepting: true }, Requests: [{ State: "claimed" }] }, "Claimed");
            Assert(relay.Cancel(early!) == false, "Cancelled once the agent has it");
            relay.Exchange("pc-1", Report(requests: [new(early!, "sent", null)]));
            var (again, _) = relay.Request(Magnet(Hash), null, null, null, null);
            Assert(relay.Status().Requests.First(x => x.Id == early).State == "sent" && again != early && relay.Cancel(again!) == true, "Sent; asked again, a new request");
            relay.Exchange("pc-1", Report(downloads: [new(Hash, "Dune", "downloading", 1, 0, 0)]));
            Assert(relay.Status() is { Requests: [], Downloads: [{ Id: Hash }] }, "Sent, then listed by TorLink: no longer a request");

            // A search: cleaned and checked, handed out two at a time, its results checked against their magnet links.
            Assert(relay.Search("   ", "movies").Error == "Écris ce que tu cherches." && relay.Search("dune", "games").Error == "Choisis Films, Séries ou Animes.", "Refused searches");
            var (search, _) = relay.Search("--dune  part two", "movies");
            relay.Search("a", "tv"); relay.Search("b", "anime");
            work = relay.Exchange("pc-1", Report());
            Assert(work.Searches.Count == 2 && work.Searches[0] is { Query: "dune part two", Category: "movies" } && relay.SearchState(search!)!.State == "running", "Two searches at a time");
            List<AgentHit> hits = [new(Hash, "Dune Part Two 2024 1080p", 2L << 30, 120, 5, "yts", Magnet(Hash), null), new(Other, "Faux", 1, 1, 1, "x", Magnet(Hash), null)];
            relay.Exchange("pc-2", Report(searches: [new(search!, "done", hits, null)]));
            Assert(relay.SearchState(search!)!.State == "running", "Another device reports a search it was not given");
            now = now.AddSeconds(50);
            relay.Exchange("pc-1", Report(searches: [new(search!, "done", hits, null)]));
            var state = relay.SearchState(search!)!;
            Assert(state is { State: "done", Results: [{ Id: Hash, Name: "Dune Part Two 2024 1080p", Seeders: 120 }] }, "A result whose magnet link is another's is left out");
            Assert(typeof(RelayHit).GetProperty("Magnet") is null, "Mira web never gets the magnet links of a search");
            var (picked, _) = relay.Request(null, search, Hash, "ignored", 0);
            Assert(relay.Status().Requests.First(x => x.Id == picked) is { Name: "Dune Part Two 2024 1080p", Size: 2L << 30, State: "waiting" }, "A result asked for keeps its own name and size");
            Assert(relay.Request(null, search, Other, null, null).Error == "Ce résultat n’est plus là : cherche de nouveau.", "An unknown result");
            Assert(relay.Request(null, null, null, null, null).Error!.StartsWith("Ce n’est pas un lien magnet", StringComparison.Ordinal), "Nothing to download");
            Assert(relay.Request(Magnet(Hash), null, null, null, null).Id == picked, "The same download asked twice is one request");

            // While TorLink is in use on the PC: searches still go, downloads wait.
            relay.Search("c", "movies");
            work = relay.Exchange("pc-1", Report(accepting: false));
            Assert(work.Searches.Count == 1 && work.Requests.Count == 0 && relay.Status().Agent!.Problem == "TorLink est à l’écran sur le PC serveur.", "Not accepting");
            work = relay.Exchange("pc-1", Report());
            Assert(work.Requests.Single().Id == picked, "Accepting again");
            // Mira closed with the request taken: handed out again two minutes later.
            now = now.Add(TorLinkRelay.Claim);
            Assert(relay.Exchange("pc-1", Report()).Requests.Single().Id == picked, "A claim not reported is handed out again");
            relay.Exchange("pc-1", Report(requests: [new(picked!, "failed", "TorLink refuse.")]));
            Assert(relay.Status().Requests.First(x => x.Id == picked) is { State: "failed", Error: "TorLink refuse." } && relay.Cancel(picked!) == true && relay.Cancel(picked!) is null, "Failed, then removed");

            // Another Mira already has the role; the first one gone, the second takes it.
            Assert(relay.Exchange("pc-2", Report()) is { Busy: "PC-SALON", Next: TorLinkRelay.Busy, Searches: [] }, "A second agent while the first is online");
            now = now.Add(TorLinkRelay.Online);
            Assert(relay.Exchange("pc-2", Report()).Busy is null && relay.Status().Agent!.Online, "The second agent once the first is gone");

            // Controls: on a download TorLink has, by the agent; what it reports, sanitized.
            Assert(relay.Control("not-a-hash", "delete").Error == "Demande inconnue." && relay.Control(Hash, "stop-seed").Error == "Demande inconnue.", "Unknown controls");
            var (order, _) = relay.Control(Hash, "pause");
            Assert(relay.Control(Hash, "pause").Id == order && relay.Status().Controls.Single() is { Action: "pause", State: "waiting" }, "One control per download and action");
            work = relay.Exchange("pc-2", Report(downloads: [new(Hash, "Dune\u0007", "downloading", 140, -5, 3), new("bad", "x", "", 0, 0, 0)],
                placed: [new(Other, "Dune (2021)", "Rangé", "0123456789ABCDEF0123456789abcdef", true), new(Hash, "Autre", "Rangé", "../Users", true)]));
            Assert(work.Controls.Single() is { Target: Hash, Action: "pause" }, "The control goes to the agent");
            var status = relay.Status();
            Assert(status.Downloads.Single() is { Name: "Dune", Progress: 100, Speed: 0 } && status.Placed[0].ItemId == "0123456789ABCDEF0123456789abcdef" && status.Placed[1].ItemId is null, "Reports sanitized");
            relay.Exchange("pc-2", Report(controls: [new(order!, "done", null)]));
            Assert(relay.Status().Controls.Count == 0, "A control done is no longer listed");

            // Back every 2 s while someone looks at the screen (or something waits), every 10 s otherwise.
            now = now.Add(TorLinkRelay.Watched);
            Assert(relay.Exchange("pc-2", Report()).Next == TorLinkRelay.Idle, "Idle");
            relay.Status();
            Assert(relay.Exchange("pc-2", Report()).Next == TorLinkRelay.Hurry, "Watched");

            // Thirty requests wait at most; old outcomes go after a quarter of an hour.
            var fresh = new TorLinkRelay(() => now);
            for (var i = 0; i < TorLinkRelay.MaxWaiting; i++) Assert(fresh.Request(Magnet(i.ToString("x40")), null, null, null, null).Id is not null, "Request " + i);
            Assert(fresh.Request(Magnet(Other), null, null, null, null).Error == "Trop de demandes attendent déjà le PC serveur.", "More than thirty");
            return Task.CompletedTask;
        });

        await Test("Télécharger sur le PC serveur : résultats de « torlnk search », liste de TorLink et ce que dit Mira", () =>
        {
            // TorLink 1.9.0 without Internet: every source fails.
            var offline = """{"query":"big buck bunny","category":"movies","count":0,"sources":{"yts":{"ok":false,"count":0,"error":"fetch failed","code":"no response"},"tpb-movies":{"ok":false,"count":0,"error":"fetch failed","code":"no response"}},"results":[]}""";
            Assert(TorLinkRemote.ParseSearch(offline) is ([], "Aucune source de TorLink n’a répondu au PC serveur : vérifie sa connexion à Internet."), "No source answered");
            var nothing = """{"sources":{"yts":{"ok":true,"count":0}},"results":[]}""";
            var (none, error) = TorLinkRemote.ParseSearch(nothing);
            Assert(none.Count == 0 && error is null, "No result");
            Assert(TorLinkRemote.ParseSearch("TorLink 1.9.0\nusage…").Error == "TorLink n’a pas répondu comme prévu." && TorLinkRemote.ParseSearch("[]").Error == "TorLink n’a pas répondu comme prévu.", "Not JSON");
            var output = $$"""
                {"sources":{"yts":{"ok":true} },"results":[
                  {"infoHash":"{{Hash}}","name":"Dune\tPart Two","sizeBytes":2147483648,"seeders":120,"leechers":4.0,"source":"yts","magnet":"{{Magnet(Hash)}}","added":1700000000},
                  {"infoHash":"{{Hash}}","name":"Doublon","magnet":"{{Magnet(Hash)}}"},
                  {"infoHash":"{{Other}}","name":"Faux","magnet":"{{Magnet(Hash)}}"},
                  {"name":"Sans lien"},
                  {"name":"Base32","magnet":"magnet:?xt=urn:btih:YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK&dn=x","seeders":-3}]}
                """;
            var (found, refused) = TorLinkRemote.ParseSearch(output);
            Assert(refused is null && found.Count == 1 && found[0] is { Id: Hash, Name: "Dune Part Two", Size: 2147483648, Seeders: 120, Leechers: 4, Source: "yts", Added: 1700000000 }, "Results: " + string.Join(" | ", found));

            using var downloads = JsonDocument.Parse($$"""
                {"downloads":[{"id":"{{Hash.ToUpperInvariant()}}","name":"Dune","status":"downloading","progress":42,"speed":1048576.5,"peers":7},{"id":"x","name":"?"}],
                 "seeds":[{"id":"{{Other}}","name":"Autre","status":"seeding"}]}
                """);
            Assert(TorLinkRemote.ParseDownloads(downloads.RootElement).Single() is { Id: Hash, Status: "downloading", Progress: 42, Speed: 1048576, Peers: 7 }, "Downloads");
            Assert(TorLinkRemote.Working(downloads.RootElement), "Working while downloading");
            using var seeding = JsonDocument.Parse($$"""{"downloads":[{"id":"{{Hash}}","status":"paused"}],"seeds":[{"id":"{{Other}}","status":"seeding"}]}""");
            using var idle = JsonDocument.Parse($$"""{"downloads":[{"id":"{{Hash}}","status":"paused"},{"id":"{{Other}}","status":"failed"}],"seeds":[{"id":"{{Other}}","status":"missing"}]}""");
            Assert(TorLinkRemote.Working(seeding.RootElement) && !TorLinkRemote.Working(idle.RootElement), "Sharing keeps it running, paused or failed do not");
            var queue = $$"""[{"id":"{{Hash}}","name":"Dune","status":"downloading","progress":42,"speed":900,"peers":3},{"id":"{{Other}}","name":"Autre","status":"paused","progress":7}]""";
            Assert(TorLinkRemote.ParseQueue(queue, running: false) is [{ Status: "stopped", Speed: 0, Peers: 0, Progress: 42 }, { Status: "paused" }], "Saved queue while TorLink is closed");
            Assert(TorLinkRemote.ParseQueue(queue, running: true)[0].Status == "downloading" && TorLinkRemote.ParseQueue("{", true).Count == 0, "Saved queue while it runs");

            Assert(TorLinkRemote.Problem(false, false, false, false) == "TorLink n’est pas activé dans Mira sur le PC serveur." && TorLinkRemote.Problem(true, false, false, false)!.Contains("confirme", StringComparison.Ordinal)
                && TorLinkRemote.Problem(true, true, true, false)!.Contains("autre fenêtre", StringComparison.Ordinal) && TorLinkRemote.Problem(true, true, false, true)!.Contains("à l’écran", StringComparison.Ordinal)
                && TorLinkRemote.Problem(true, true, false, false) is null, "Problems");
            var placed = TorLinkRemote.Placed([new TorLinkImportEntry { Id = Hash, Name = "Dune.2021.1080p", State = TorLinkImportState.Imported, Kind = MediaKind.Movie, Title = "Dune (2021)", JellyfinId = "01234567-89ab-cdef-0123-456789abcdef" },
                new TorLinkImportEntry { Id = Other, Name = "x", State = TorLinkImportState.Waiting }]);
            Assert(placed.Single() is { Id: Hash, Ok: true, ItemId: "0123456789abcdef0123456789abcdef" }, "Placed: " + string.Join(" | ", placed));
            return Task.CompletedTask;
        });

        await Test("Télécharger sur le PC serveur : l'API de TorLink, sur 127.0.0.1 et derrière son jeton", async () =>
        {
            var seen = new List<string>();
            var handler = new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {request.Headers.Authorization} {body}");
                var (status, answer) = request.RequestUri.AbsolutePath switch
                {
                    "/health" => (HttpStatusCode.OK, """{"ok":true}"""),
                    "/add" when body.Contains(Other) => (HttpStatusCode.BadRequest, """{"error":"invalid magnet or info hash"}"""),
                    "/add" => (HttpStatusCode.OK, """{"ok":true,"outcome":"duplicate"}"""),
                    "/control" when body.Contains(Other) => (HttpStatusCode.NotFound, """{"error":"not found"}"""),
                    "/control" => (HttpStatusCode.OK, """{"ok":true}"""),
                    "/downloads" => (HttpStatusCode.OK, $$"""{"downloads":[{"id":"{{Hash}}","status":"queued"}],"seeds":[]}"""),
                    _ => (HttpStatusCode.Unauthorized, "")
                };
                return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
            });
            using var client = new TorLinkServeClient(4321, "jeton", handler);
            Assert(await client.HealthyAsync(CancellationToken.None), "Healthy");
            Assert(await client.AddAsync(Magnet(Hash), CancellationToken.None) == ("duplicate", null), "Added");
            Assert(await client.AddAsync(Magnet(Other), CancellationToken.None) == (null, "invalid magnet or info hash"), "Refused with TorLink's reason");
            Assert(await client.ControlAsync(Hash, "pause", CancellationToken.None) && !await client.ControlAsync(Other, "delete", CancellationToken.None), "Controls");
            Assert(TorLinkRemote.Working(await client.DownloadsAsync(CancellationToken.None)), "Downloads");
            Assert(seen.All(x => x.Contains(" Bearer jeton", StringComparison.Ordinal)) && JsonDocument.Parse(seen[1][seen[1].IndexOf('{')..]).RootElement.GetProperty("magnet").GetString() == Magnet(Hash)
                && seen[3] == $$"""POST /control Bearer jeton {"id":"{{Hash}}","action":"pause"}""", "Requests: " + string.Join(" | ", seen));
            using var fallen = new TorLinkServeClient(1, "jeton", new Handler(_ => throw new HttpRequestException("refused")));
            Assert(!await fallen.HealthyAsync(CancellationToken.None), "Not answering");
        });
    }
}
