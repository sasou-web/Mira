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

        await Test("Télécharger sur le PC serveur : la meilleure release par titre et épisode, comme Mira web (tests/fixtures/releases.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "releases.json")));
            foreach (var row in fixture.RootElement.GetProperty("names").EnumerateArray())
            {
                var release = ReleaseRanking.Parse(row[0].GetString());
                int? year = row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetInt32();
                var seen = (release.Title, release.Year, ReleaseRanking.EpisodeLabel(release), ReleaseRanking.QualityLine(release));
                Assert(seen == (row[1].GetString()!, year, row[3].GetString()!, row[4].GetString()!), $"« {row[0]} » read as {seen}");
            }
            foreach (var row in fixture.RootElement.GetProperty("readable").EnumerateArray())
            {
                var shown = ReleaseRanking.Readable(row[0].GetString());
                Assert(shown == (row[1].GetString()!, row[2].GetString()), $"« {row[0]} » shown as {shown}");
            }
            foreach (var search in fixture.RootElement.GetProperty("ranked").EnumerateArray())
            {
                var hits = search.GetProperty("hits").EnumerateArray().Select(x => new RelayHit(x[0].GetString()!, x[1].GetString()!, x[2].GetInt64(), x[3].GetInt32(), 0, x[4].GetString()!, null, x.GetArrayLength() > 5 ? x[5].GetString() : null)).ToList();
                var found = ReleaseRanking.Group(hits, search.GetProperty("category").GetString()).Select(g => $"{g.Title} | {g.Label} | {g.Best.Hit.Id} | {string.Join(",", g.Others.Select(x => x.Hit.Id))}{(g.Category is { } kind ? " | " + kind : "")}");
                var expected = search.GetProperty("groups").EnumerateArray().Select(g => $"{g[0].GetString()} | {g[1].GetString()} | {g[2].GetString()} | {string.Join(",", g[3].EnumerateArray().Select(x => x.GetString()))}{(g.GetArrayLength() > 4 ? " | " + g[4].GetString() : "")}");
                Assert(found.SequenceEqual(expected), "Groups: " + string.Join(" ; ", found));
            }
            Assert(ReleaseRanking.Parse("[ToonsHub] JoJos Bizarre Adventure S06E04 1080p (JoJo no Kimyou na Bouken: Steel Ball Run - 2nd - 3rd STAGE, Dual-Audio)").Alternate == "JoJo no Kimyou na Bouken: Steel Ball Run"
                && ReleaseRanking.Parse("Film.2020.1080p.WEB-DL.MultiSub.x264").Languages.Count == 0 && ReleaseRanking.TitleKey("JoJo’s Bizarre Adventure: Éclat & Co") == "jojos bizarre adventure eclat and co", "Other name, subtitles, key");
            RelayHit Hit(string name, int seeders, string source = "1337x") => new("x", name, 3L << 30, seeders, 0, source, null);
            Assert(ReleaseRanking.Score(Hit("Film.2020.1080p.BluRay.x264-A", 5), ReleaseRanking.Parse("Film.2020.1080p.BluRay.x264-A")) > ReleaseRanking.Score(Hit("Film.2020.1080p.WEBRip.x264-B", 5000), ReleaseRanking.Parse("Film.2020.1080p.WEBRip.x264-B"))
                && ReleaseRanking.Warning(new(Hit("", 0, "subsplease"), ReleaseRanking.Parse("[SubsPlease] Show - 05 (1080p)"), 0, 0)) == "", "Seeders only break ties; SubsPlease has none");
            return Task.CompletedTask;
        });
        await Test("Télécharger sur le PC serveur : « Déjà dans ta bibliothèque », comme Mira web (tests/fixtures/owned.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "owned.json")));
            static int? Int(JsonElement x) => x.ValueKind == JsonValueKind.Number ? x.GetInt32() : null;
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var name = row.GetProperty("name").GetString()!;
                var group = ReleaseRanking.Group([new RelayHit("x", name, 2L << 30, 10, 0, "check", null)])[0];
                var items = row.GetProperty("items").EnumerateArray().Select(x => new MediaItem { Type = x[0].GetString()!, Name = x[1].GetString()!, OriginalTitle = x[2].GetString(), ProductionYear = Int(x[3]), Id = x[4].GetString()! }).ToList();
                var episodes = row.TryGetProperty("episodes", out var list) ? list.EnumerateArray().Select(x => new MediaItem { ParentIndexNumber = Int(x[0]), IndexNumber = Int(x[1]), IndexNumberEnd = x.GetArrayLength() > 2 ? Int(x[2]) : null }).ToList() : [];
                var line = OwnedTitles.Line(group, items, episodes);
                var expected = row.GetProperty("line");
                Assert(expected.ValueKind == JsonValueKind.Null ? line is null : line == new OwnedLine(expected[0].GetString()!, expected[1].GetString()!, expected[2].GetBoolean()), $"« {name} »: {line}");
            }
            var many = ReleaseRanking.Group(new[] { "Mira.Check.S01E02.1080p.WEB-DL", "Mira.Check.S01E03.1080p.WEB-DL", "Mira.Check.2026.1080p.WEB-DL" }.Concat(Enumerable.Range(0, 20).Select(i => $"Film {(char)('A' + i)} 2020 1080p BluRay"))
                .Select((n, i) => new RelayHit($"h{i}", n, 2L << 30, 9, 0, "x", null)));
            var lookups = OwnedTitles.Lookups(many);
            Assert(lookups.Count == OwnedTitles.MaxLookups && lookups[0] == ("show|mira check", "Mira Check", false) && lookups[1] == ("film|mira check", "Mira Check", true), "Lookups: " + string.Join(" ; ", lookups));
            return Task.CompletedTask;
        });
        await Test("Télécharger sur le PC serveur : l'écran des apps écrit le PC, un résultat, une demande et un téléchargement comme Mira web (tests/fixtures/torlink-lines.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "torlink-lines.json")));
            var root = fixture.RootElement; var now = root.GetProperty("now").GetDateTimeOffset();
            T Read<T>(JsonElement value) => value.Deserialize<T>(RemoteProtocol.Json)!;
            foreach (var pair in root.GetProperty("sizes").EnumerateArray()) Assert(RemoteLines.Size(pair[0].GetInt64()) == pair[1].GetString(), $"Size {pair[0]}: {RemoteLines.Size(pair[0].GetInt64())}");
            foreach (var pair in root.GetProperty("speeds").EnumerateArray()) Assert(RemoteLines.Speed(pair[0].GetInt64()) == pair[1].GetString(), $"Speed {pair[0]}: {RemoteLines.Speed(pair[0].GetInt64())}");
            foreach (var row in root.GetProperty("agents").EnumerateArray())
            {
                var line = RemoteLines.Agent(row.GetProperty("agent").ValueKind == JsonValueKind.Null ? null : Read<RelayAgent>(row.GetProperty("agent")), now);
                Assert(line == (row.GetProperty("text").GetString()!, row.GetProperty("ready").GetBoolean()), $"Agent {row.GetProperty("agent")}: {line}");
            }
            foreach (var pair in root.GetProperty("hits").EnumerateArray()) Assert(RemoteLines.Hit(Read<RelayHit>(pair[0])) == pair[1].GetString(), $"Hit {pair[0]}: {RemoteLines.Hit(Read<RelayHit>(pair[0]))}");
            foreach (var pair in root.GetProperty("requests").EnumerateArray()) Assert(RemoteLines.Request(Read<RelayRequest>(pair[0])) == pair[1].GetString(), $"Request {pair[0]}: {RemoteLines.Request(Read<RelayRequest>(pair[0]))}");
            foreach (var pair in root.GetProperty("downloads").EnumerateArray()) Assert(RemoteLines.Download(Read<RelayDownload>(pair[0])) == pair[1].GetString(), $"Download {pair[0]}: {RemoteLines.Download(Read<RelayDownload>(pair[0]))}");
            foreach (var pair in root.GetProperty("remaining").EnumerateArray()) Assert(RemoteLines.Remaining(pair[0].GetDouble()) == pair[1].GetString(), $"Remaining {pair[0]}: {RemoteLines.Remaining(pair[0].GetDouble())}");
            T? Maybe<T>(JsonElement value) where T : class => value.ValueKind == JsonValueKind.Null ? null : Read<T>(value);
            foreach (var pair in root.GetProperty("followNotes").EnumerateArray()) Assert(RemoteLines.FollowNote(Maybe<FollowNote>(pair[0])) == pair[1].GetString(), $"Note {pair[0]}: {RemoteLines.FollowNote(Maybe<FollowNote>(pair[0]))}");
            foreach (var pair in root.GetProperty("follows").EnumerateArray()) Assert(RemoteLines.Follow(Read<RelayFollow>(pair[0]), now) == pair[1].GetString(), $"Follow {pair[0]}: {RemoteLines.Follow(Read<RelayFollow>(pair[0]), now)}");
            foreach (var pair in root.GetProperty("picks").EnumerateArray()) Assert(RemoteLines.Pick(Read<RelayPick>(pair[0]), now) == pair[1].GetString(), $"Pick {pair[0]}: {RemoteLines.Pick(Read<RelayPick>(pair[0]), now)}");
            foreach (var pair in root.GetProperty("taken").EnumerateArray()) Assert(RemoteLines.Taken(Read<RelayTaken>(pair[0]), now) == pair[1].GetString(), $"Taken {pair[0]}: {RemoteLines.Taken(Read<RelayTaken>(pair[0]), now)}");
            var download = new RelayDownload(Hash, "Dune", "paused", 10, 0, 0, Eta: 60);
            // Why a download failed: in queue.json, never in TorLink's API; taken from there for the failed ones listed live.
            var queue = $$"""[{"id":"{{Hash}}","name":"Dune","status":"failed","progress":3,"error":"ENOSPC: no space left on device"},{"id":"{{new string('b', 40)}}","name":"Autre","status":"downloading","progress":5,"error":"old"}]""";
            var live = new List<RelayDownload> { new(Hash, "Dune", "failed", 3, 0, 0), new(new string('b', 40), "Autre", "downloading", 5, 10, 1) };
            var merged = TorLinkRemote.WithErrors(live, queue);
            Assert(merged[0].Error == "ENOSPC: no space left on device" && merged[1].Error is null && TorLinkRemote.ParseQueue(queue, true)[1].Error is null
                && ReferenceEquals(TorLinkRemote.WithErrors(merged, queue), merged) && TorLinkRemote.WithErrors(live, null)[0].Error is null, "Failure reason from queue.json");
            Assert(RemoteLines.TimeLeft(download) is null && RemoteLines.TimeLeft(download with { Status = "downloading", Eta = null, Speed = 10 }) is null, "No time left while paused, nor without a size");
            Assert(RemoteLines.Controllable(download with { Status = "queued" }) && RemoteLines.Paused(download) && !RemoteLines.Controllable(download with { Status = "completed" }), "Controllable");
            var status = new RelayStatus(null, [new("1", "Dune", 0, Hash, "sent", null, now), new("2", "Alien", 0, Other, "sent", null, now)], [], [download], []);
            Assert(RemoteLines.Pending(status).Select(x => x.Id).SequenceEqual(["2"]), "A request TorLink lists shows as its download");
            Assert(RemoteLines.Refused(HttpStatusCode.Forbidden)!.Contains("administrateurs", StringComparison.Ordinal) && RemoteLines.Refused(HttpStatusCode.InternalServerError) is null, "Refusals");
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
            relay.Exchange("pc-1", Report(downloads: [new(Hash, "Dune", "failed", 1, 0, 0, Error: "ENOSPC\u0007 " + new string('x', 400)), new(new string('b', 40), "Autre", "downloading", 1, 0, 0, Error: "stale")]));
            Assert(relay.Status().Downloads is [{ Error: { Length: <= 301 } error }, { Error: null }] && error.StartsWith("ENOSPC x", StringComparison.Ordinal), "Why it failed, cleaned; nothing for one under way");
            relay.Exchange("pc-1", Report(downloads: [new(Hash, "Dune", "downloading", 1, 0, 0)]));

            // A search: cleaned and checked, handed out three at a time, its results checked against their magnet links.
            Assert(relay.Search("   ", "movies").Error == "Écris ce que tu cherches." && relay.Search("dune", "games").Error == "Choisis Films, Séries ou Animes.", "Refused searches");
            var (search, _) = relay.Search("--dune  part two", "movies");
            relay.Search("a", "tv"); relay.Search("b", "anime");
            work = relay.Exchange("pc-1", Report());
            Assert(work.Searches.Count == 3 && work.Searches[0] is { Query: "dune part two", Category: "movies" } && relay.SearchState(search!)!.State == "running", "Three searches at a time");
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
            work = relay.Exchange("pc-2", Report(downloads: [new(Hash, "Dune\u0007", "downloading", 140, -5, 3, 1000, 5000, -1), new("bad", "x", "", 0, 0, 0)],
                placed: [new(Other, "Dune (2021)", "Rangé", "0123456789ABCDEF0123456789abcdef", true), new(Hash, "Autre", "Rangé", "../Users", true)]));
            Assert(work.Controls.Single() is { Target: Hash, Action: "pause" }, "The control goes to the agent");
            var status = relay.Status();
            Assert(status.Downloads.Single() is { Name: "Dune", Progress: 100, Speed: 0, Size: 1000, Downloaded: 1000, Eta: null } && status.Placed[0].ItemId == "0123456789ABCDEF0123456789abcdef" && status.Placed[1].ItemId is null, "Reports sanitized");
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

        await Test("Télécharger sur le PC serveur : une recherche dans les films, les séries et les animes à la fois", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-22T12:00:00Z");
            var relay = new TorLinkRelay(() => now);
            AgentReport Report(List<AgentSearchOutcome>? searches = null) => new("PC-SALON", true, null, null, null, searches, null, null);
            const string Episode = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            relay.Exchange("pc-1", Report());
            var (id, error) = relay.Search("one piece", RemoteDownloads.All);
            Assert(id is not null && error is null && relay.SearchState(id)! is { State: "waiting", Category: "all" }, "Asked for in every category");
            // An agent before 0.8.3 knows only the three categories: it gets one search of each, in one visit.
            var work = relay.Exchange("pc-1", Report());
            Assert(work.Searches.Select(x => x.Category).SequenceEqual(RemoteDownloads.Categories) && work.Searches.All(x => x.Query == "one piece" && x.Id != id), "One search of TorLink by category");
            AgentHit Hit(string hash, string name, int seeders) => new(hash, name, 1L << 30, seeders, 0, "x", Magnet(hash), null);
            var (movies, tv, anime) = (work.Searches[0].Id, work.Searches[1].Id, work.Searches[2].Id);
            relay.Exchange("pc-1", Report([new(movies, "done", [Hit(Hash, "One Piece Film Red 2022 1080p", 300), Hit(Other, "One Piece Stampede 2019 1080p", 90)], null), new(anime, "failed", null, "Nyaa ne répond pas.")]));
            Assert(relay.SearchState(id!)! is { State: "running", Results: [] }, "Not done while a category still runs");
            relay.Exchange("pc-1", Report([new(tv, "done", [Hit(Episode, "One.Piece.2023.S01E01.1080p", 500), Hit(Hash, "One Piece Film Red 2022 1080p", 300)], null)]));
            var state = relay.SearchState(id!)!;
            Assert(state.State == "done" && state.Error is null && string.Join(",", state.Results.Select(x => $"{x.Category}:{x.Id[0]}")) == "movies:c,tv:e,movies:0",
                "Mixed, the first of each category first; found twice, kept once; a category that failed leaves the others: " + string.Join(",", state.Results.Select(x => $"{x.Category}:{x.Id[0]}")));
            var (picked, _) = relay.Request(null, id!, Episode, null, null);
            Assert(relay.Status().Requests.Single(x => x.Id == picked) is { Name: "One.Piece.2023.S01E01.1080p", InfoHash: Episode }, "A result of one category, asked for by the search");

            var (lost, _) = relay.Search("rien", RemoteDownloads.All);
            work = relay.Exchange("pc-1", Report());
            relay.Exchange("pc-1", Report([.. work.Searches.Select(x => new AgentSearchOutcome(x.Id, "failed", null, "TorLink n’est pas prêt."))]));
            Assert(relay.SearchState(lost!)! is { State: "failed", Error: "TorLink n’est pas prêt.", Results: [] }, "Failed in every category");
            var (none, failed) = RemoteDownloads.Combine<string>([], x => x);
            Assert(none.Count == 0 && failed is null, "Nothing to combine");
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
                {"downloads":[{"id":"{{Hash.ToUpperInvariant()}}","name":"Dune","status":"downloading","progress":42,"speed":1048576.5,"peers":7,"totalBytes":3000000000,"downloadedBytes":1260000000,"eta":1662.4},{"id":"x","name":"?"}],
                 "seeds":[{"id":"{{Other}}","name":"Autre","status":"seeding"}]}
                """);
            Assert(TorLinkRemote.ParseDownloads(downloads.RootElement).Single() is { Id: Hash, Status: "downloading", Progress: 42, Speed: 1048576, Peers: 7, Size: 3000000000, Downloaded: 1260000000, Eta: 1662 }, "Downloads");
            Assert(TorLinkRemote.Working(downloads.RootElement), "Working while downloading");
            using var seeding = JsonDocument.Parse($$"""{"downloads":[{"id":"{{Hash}}","status":"paused"}],"seeds":[{"id":"{{Other}}","status":"seeding"}]}""");
            using var idle = JsonDocument.Parse($$"""{"downloads":[{"id":"{{Hash}}","status":"paused"},{"id":"{{Other}}","status":"failed"}],"seeds":[{"id":"{{Other}}","status":"missing"}]}""");
            Assert(TorLinkRemote.Working(seeding.RootElement) && !TorLinkRemote.Working(idle.RootElement), "Sharing keeps it running, paused or failed do not");
            var queue = $$"""[{"id":"{{Hash}}","name":"Dune","status":"downloading","progress":42,"speed":900,"peers":3,"totalBytes":1000,"downloadedBytes":420,"eta":9e9},{"id":"{{Other}}","name":"Autre","status":"paused","progress":7}]""";
            Assert(TorLinkRemote.ParseQueue(queue, running: false) is [{ Status: "stopped", Speed: 0, Peers: 0, Progress: 42, Size: 1000, Downloaded: 420, Eta: null }, { Status: "paused", Eta: null }], "Saved queue while TorLink is closed");
            Assert(TorLinkRemote.ParseQueue(queue, running: true)[0] is { Status: "downloading", Eta: RemoteDownloads.MaxEta } && TorLinkRemote.ParseQueue("{", true).Count == 0, "Saved queue while it runs");

            Assert(TorLinkRemote.Problem(false, false, false, false) == "TorLink n’est pas activé dans Mira sur le PC serveur." && TorLinkRemote.Problem(true, false, false, false)!.Contains("confirme", StringComparison.Ordinal)
                && TorLinkRemote.Problem(true, true, true, false)!.Contains("autre fenêtre", StringComparison.Ordinal) && TorLinkRemote.Problem(true, true, false, true)!.Contains("à l’écran", StringComparison.Ordinal)
                && TorLinkRemote.Problem(true, true, false, false) is null && TorLinkRemote.Problem(true, true, true, false, recent: false)!.Contains("trop ancien", StringComparison.Ordinal), "Problems");
            // Before 1.4 « torlnk search » printed TorLink's usage, whose last line read as its error (« tip: quote magnet links… »).
            Assert(TorLinkRemote.Supports("1.9.0") && TorLinkRemote.Supports("1.10.0") && TorLinkRemote.Supports(" 2.0.0-beta.1 ") && TorLinkRemote.Supports("1.9.1+build")
                && !TorLinkRemote.Supports("1.8.0") && !TorLinkRemote.Supports("1.3.1") && !TorLinkRemote.Supports("1.0.0") && !TorLinkRemote.Supports(null) && !TorLinkRemote.Supports("") && !TorLinkRemote.Supports("next"), "TorLink versions for Mira's screen");
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
