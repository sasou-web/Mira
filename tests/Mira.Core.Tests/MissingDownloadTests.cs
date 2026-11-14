using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Télécharger les manquants » of a series' page: which results to put forward (MissingDownloads, cases shared with the
/// iPhone in tests/fixtures/missing.json), the relay that keeps them until placed, the agent's place, and the progress shown.
/// </summary>
internal static class MissingDownloadTests
{
    private const string SeriesId = "0123456789abcdef0123456789abcdef";
    private static string Hash(int n) => n.ToString("x40");
    private static string Magnet(int n) => $"magnet:?xt=urn:btih:{Hash(n)}";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        var fixture = Fixture();
        foreach (var node in fixture["choose"]!.AsArray())
        {
            var c = node!;
            await Test("Télécharger les manquants : " + (string)c["name"]!, () =>
            {
                var s = c["series"]!;
                var series = new MissingSeries((string)s["name"]!, (string?)s["originalTitle"], (int?)s["year"], (int)s["before"]!, (string?)s["query"]);
                var a = c["ask"]!;
                var ask = new MissingAsk((int)a["season"]!, a["episodes"]!.AsArray().Select(x => (int)x!).ToList(), (bool)a["wholeSeason"]!);
                var hits = c["hits"]!.AsArray().Select((x, i) => ("s-" + (string)x![4]!, new RelayHit(Hash(i + 1), (string)x[0]!, (long)((double)x[1]! * (1L << 30)), (int)x[2]!, 0, (string)x[3]!, null, (string)x[4]!))).ToList();
                var picks = MissingDownloads.Choose(ask, series, hits);
                var packs = c["packs"]!.AsArray().Select(x => (string)x!).ToList();
                Assert(picks.Packs.Select(x => x.Entry.Hit.Name).SequenceEqual(packs), "Packs: " + string.Join(" | ", picks.Packs.Select(x => x.Entry.Hit.Name)));
                var episodes = c["episodes"]!.AsObject().ToDictionary(x => int.Parse(x.Key, System.Globalization.CultureInfo.InvariantCulture), x => (string)x.Value!);
                Assert(picks.Episodes.ToDictionary(x => x.Episode, x => x.Best.Entry.Hit.Name).OrderBy(x => x.Key).SequenceEqual(episodes.OrderBy(x => x.Key)),
                    "Episodes: " + string.Join(" | ", picks.Episodes.Select(x => $"{x.Episode}={x.Best.Entry.Hit.Name}")));
                Assert(picks.NotFound.SequenceEqual(c["notFound"]!.AsArray().Select(x => (int)x!)), "Not found: " + string.Join(", ", picks.NotFound));
                Assert(picks.Episodes.All(x => x.Others.All(o => o != x.Best) && x.Best.Search.StartsWith("s-", StringComparison.Ordinal)), "The others never the best; each with its search");
                return Task.CompletedTask;
            });
        }
        await Test("Télécharger les manquants : ce qui est cherché, les suites d'épisodes, les textes", () =>
        {
            foreach (var q in fixture["queries"]!.AsArray())
            {
                var s = q!["series"]!;
                var series = new MissingSeries((string)s["name"]!, null, (int?)s["year"], (int)s["before"]!, (string?)s["query"]);
                var queries = MissingDownloads.Queries(series, (int)q["season"]!);
                Assert(queries.SequenceEqual(q["queries"]!.AsArray().Select(x => (string)x!)), "Queries: " + string.Join(" | ", queries));
            }
            foreach (var r in fixture["runs"]!.AsArray())
            {
                var runs = MissingDownloads.Runs(r![0]!.AsArray().Select(x => (int)x!));
                Assert(runs.SequenceEqual(r[1]!.AsArray().Select(x => ((int)x![0]!, (int)x[1]!))), "Runs: " + string.Join(", ", runs));
            }
            Assert(MissingDownloads.GetEpisodes(6, 9) == "Télécharger É6–9" && MissingDownloads.GetSeason(2) == "Télécharger la saison 2" && MissingDownloads.NotFound([9, 11, 12]) == "Rien trouvé pour É9, É11–12.", "Texts");
            Assert(MissingDownloads.Categories(true).SequenceEqual(["anime", "tv"]) && MissingDownloads.Categories(false).SequenceEqual(["tv"]), "Categories");
            Assert(MissingDownloads.Inside(@"C:\Users\Alex\Videos\MEDIAS\ANIME\Frieren", @"C:\Users\Alex\Videos\MEDIAS\ANIME\") && !MissingDownloads.Inside("/media/animes", "/media/anime")
                && !MissingDownloads.Inside("/media/anime", "/media/anime"), "A Windows server's paths read anywhere");
            Assert(MissingDownloads.Before([new MediaItem { Id = "a", ParentIndexNumber = 1, IndexNumber = 12 }, new MediaItem { Id = "b", ParentIndexNumber = 2, IndexNumber = 9 }], 3) == 21, "The episodes before a season");
            return Task.CompletedTask;
        });

        await Test("Télécharger les manquants : le menu reste dans la fenêtre (dessous, dessus, plus étroit, plus court)", () =>
        {
            // ogsas's case (2026-11-14): « Get season 2 » at the right of a 1640 × 520 page, the menu 466 wide went past the window.
            var button = new MenuFit.Box(1393, 170, 165, 46);
            var size = MenuFit.Size(1640, 520, button, 440);
            Assert(size.Width == 440 && size.MaxHeight == 520 - 12 - 216 - 8, "Room below: " + size);
            var place = MenuFit.At(1640, 520, button, 440, 250);
            Assert(place == new MenuFit.Place(1558 - 440, 224, false), "Right-aligned with its button, below it: " + place);
            // Too tall for below, fits above: above the button.
            place = MenuFit.At(1640, 520, button, 440, 300);
            Assert(!place.Above && place.Y == 520 - 12 - 300, "Below has 284, above 150: the larger side, moved up to stay inside: " + place);
            var low = new MenuFit.Box(40, 600, 165, 46);
            place = MenuFit.At(1640, 700, low, 440, 300);
            Assert(place == new MenuFit.Place(40, 600 - 8 - 300, true), "Above a button near the bottom, left-aligned: " + place);
            // A narrow window: the menu narrower, inside its margins.
            size = MenuFit.Size(400, 700, new MenuFit.Box(300, 100, 80, 40), 440);
            Assert(size.Width == 400 - 24, "Narrower: " + size);
            place = MenuFit.At(400, 700, new MenuFit.Box(300, 100, 80, 40), size.Width, 200);
            Assert(place.X == 12 && place.Y == 148, "Inside the margins: " + place);
            // A short window: never shorter than Shortest where there is room for it, and placed inside.
            size = MenuFit.Size(1000, 200, new MenuFit.Box(10, 80, 100, 40), 440);
            Assert(size.MaxHeight == 160, "Shortest: " + size);
            place = MenuFit.At(1000, 200, new MenuFit.Box(10, 80, 100, 40), 440, 160);
            Assert(place.Y == 200 - 12 - 160 && place.X == 12, "Moved inside: " + place);
            return Task.CompletedTask;
        });

        await Test("Télécharger les manquants : le relais range la demande dans la série, la garde jusqu'au rangement, la refuse à un Mira trop ancien", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-13T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var relay = new TorLinkRelay(() => now);
            AgentReport Report(bool seasons = true, List<AgentSearchOutcome>? searches = null, List<AgentOutcome>? requests = null, List<RelayDownload>? downloads = null, List<RelayPlaced>? placed = null,
                List<string>? sorting = null) =>
                new("PC-SALON", true, null, downloads, placed, searches, requests, [], Seasons: seasons, Sorting: sorting);
            relay.Exchange("pc-1", Report(seasons: false));
            var (search, _) = relay.Search("Arcane S02", "tv");
            relay.Exchange("pc-1", Report(seasons: false));
            List<AgentHit> hits = [new(Hash(1), "Arcane.S02E06.1080p.WEB-DL-FLUX", 2L << 30, 120, 1, "1337x", Magnet(1), null), new(Hash(2), "Arcane.S02.1080p.WEB-DL-FLUX", 20L << 30, 300, 1, "1337x", Magnet(2), null)];
            relay.Exchange("pc-1", Report(seasons: false, searches: [new(search!, "done", hits, null)]));
            var one = new MissingPlace(SeriesId, 2, [6]);
            Assert(relay.Request(null, search, Hash(1), null, null, missing: one).Error == "Mira sur le PC serveur ne sait pas encore ranger un épisode manquant : mets-le à jour.", "An older Mira would place it by its name");
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Agent is { Seasons: true }, "The agent says it can");
            Assert(relay.Request(null, search, Hash(1), null, null, missing: new MissingPlace("not-an-id", 2, [6])).Error == "Demande inconnue." && relay.Request(null, search, Hash(1), null, null, missing: new MissingPlace(SeriesId, 0, [6])).Error == "Demande inconnue."
                && relay.Request(Magnet(1), null, null, null, null, missing: one).Error == "Demande inconnue.", "Only a result of a search, for a series and a season");
            var (episode, _) = relay.Request(null, search, Hash(1), null, null, missing: one);
            var (season, _) = relay.Request(null, search, Hash(2), null, null, missing: new MissingPlace("01234567-89ab-cdef-0123-456789ABCDEF", 2, []));
            Assert(relay.Status().Missing is [{ Episodes: [6], State: "waiting", Name: "Arcane.S02E06.1080p.WEB-DL-FLUX" }, { SeriesId: SeriesId, Episodes: [], Size: 20L << 30 }], "Listed with its series, the season's id cleaned");
            var work = relay.Exchange("pc-1", Report());
            Assert(work.Requests.Select(x => x.Place).SequenceEqual([new FollowPlace("", 2, 6, SeriesId), new FollowPlace("", 2, 0, SeriesId)]), "The agent gets the series and the episode, 0 for a whole season");
            relay.Exchange("pc-1", Report(requests: [new(episode!, "sent", null), new(season!, "failed", "TorLink n’a pas répondu.")]));
            relay.Exchange("pc-1", Report(downloads: [new(Hash(1), "Arcane", "downloading", 40, 1 << 20, 3, 2L << 30, 800L << 20, 600)]));
            var status = relay.Status();
            Assert(status.Requests.All(x => x.Id != episode) && status.Missing is [{ Id: var kept, State: "sent" }, { State: "failed", Error: "TorLink n’a pas répondu." }] && kept == episode, "Kept beyond the request");

            // What the tile shows: the season's pill counts both, the failed one only once none is under way.
            var progress = MissingDownloads.Progress(status, SeriesId, 2, [6]);
            Assert(progress is { Stage: MissingStage.Downloading, Percent: 40 } && progress.Line == "40 % · encore 10 min", $"Downloading: {progress}");
            Assert(MissingDownloads.Progress(status, SeriesId, 2, [7]) is { Stage: MissingStage.Failed, Line: "TorLink n’a pas répondu." }, "Only the season's pack covers E7: failed");
            Assert(MissingDownloads.Progress(status, SeriesId, 1, [6]) is null && MissingDownloads.Progress(status, "ffffffffffffffffffffffffffffffff", 2, [6]) is null, "Another season, another series");

            relay.Exchange("pc-1", Report(downloads: [new(Hash(1), "Arcane", "seeding", 100, 0, 3, 2L << 30, 2L << 30)]));
            Assert(MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6]) is { Stage: MissingStage.Adding, Title: "Rangement dans ta bibliothèque…" }, "Finished: being placed");
            // TorLink moves it to its history, Mira waits for its files, then places it: never the button again meanwhile.
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Missing!.Single(x => x.Id == episode) is { Finished: true, Listed: true } && MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6]) is { Stage: MissingStage.Adding }, "Gone from TorLink once finished: still being placed");
            now = now.AddMinutes(5);
            relay.Exchange("pc-1", Report(sorting: [Hash(1)]));
            Assert(MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6]) is { Stage: MissingStage.Adding }, "Mira places it");
            relay.Exchange("pc-1", Report(downloads: [new(Hash(1), "Arcane", "seeding", 100, 0, 3, 2L << 30, 2L << 30)], placed: [new(Hash(1), "Arcane · S02E06", "Rangé", null, true)]));
            var placedAt = now;
            var filed = MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6], now.AddMinutes(1));
            Assert(filed is { Stage: MissingStage.Placed, Title: "Rangement dans ta bibliothèque…", Rescan: false } && filed.Line == "Rangé. Jellyfin l’ajoute à la série…", $"Placed: Jellyfin adds it ({filed})");
            Assert(MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6], now.AddMinutes(4)) is { Stage: MissingStage.Placed, Rescan: true, Line: "Jellyfin ne l’a pas encore ajouté à la série." }, "Placed long ago and still missing: a scan offered");
            Assert(MissingDownloads.Pill(filed!) == "", "The pill can't tell whether Jellyfin has it already");
            now = now.AddMinutes(20);
            relay.Exchange("pc-1", Report(downloads: [new(Hash(1), "Arcane", "seeding", 100, 0, 3, 2L << 30, 2L << 30)]));
            Assert(relay.Status().Missing is [{ PlacedAt: var at }] && at == placedAt && MissingDownloads.Progress(relay.Status(), SeriesId, 2, [6]) is { Stage: MissingStage.Placed },
                "Placed, kept while Jellyfin may not list it yet, even once the agent no longer lists it");
            now = now.AddDays(1);
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Missing is [], "Forgotten a day after placed, and after failed");

            Assert(MissingDownloads.Arrived([new MediaItem { Id = "a" }], [new MediaItem { Id = "A" }, new MediaItem { Id = "b" }]) && !MissingDownloads.Arrived([new MediaItem { Id = "a" }, new MediaItem { Id = "b" }], [new MediaItem { Id = "a" }]),
                "A new episode in Jellyfin: the page shows it");
            Assert(MissingDownloads.LookAgain(TimeSpan.FromMinutes(1)) == TimeSpan.FromSeconds(10) && MissingDownloads.LookAgain(TimeSpan.FromMinutes(6)) == TimeSpan.FromMinutes(1), "Often at first, then once a minute");

            // Cancelled in TorLink after it listed it: forgotten, the button comes back.
            var (again, _) = relay.Search("Arcane S02", "tv");
            relay.Exchange("pc-1", Report());
            relay.Exchange("pc-1", Report(searches: [new(again!, "done", [new(Hash(3), "Arcane.S02E07.1080p.WEB-DL-FLUX", 2L << 30, 120, 1, "1337x", Magnet(3), null)], null)]));
            var (seven, _) = relay.Request(null, again, Hash(3), null, null, missing: new MissingPlace(SeriesId, 2, [7]));
            relay.Exchange("pc-1", Report());
            relay.Exchange("pc-1", Report(requests: [new(seven!, "sent", null)], downloads: [new(Hash(3), "Arcane", "downloading", 5, 0, 0)]));
            Assert(relay.Status().Missing is [{ State: "sent" }], "Under way");
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Missing is [{ Listed: true, Finished: false }], "Gone from TorLink's list before it finished: a minute's grace");
            now = now.AddMinutes(2);
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Missing is [], "Gone from TorLink's list: forgotten");
            return Task.CompletedTask;
        });

        await Test("Télécharger les manquants : un pack de saison rangé dans la série, chaque fichier sous son numéro", async () =>
        {
            var dir = Path.Combine(testRoot, "missing-downloads"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var downloads = Path.Combine(dir, "downloads"); var shows = Path.Combine(dir, "SERIES"); var series = Path.Combine(shows, "Arcane (2021)", "Season 01");
            foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, series }) Directory.CreateDirectory(folder);
            File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
            var importer = new TorLinkImporter(Path.Combine(dir, "profile"));
            var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
            const string Pack = "Arcane.League.of.Legends.S02.1080p.NF.WEB-DL.DDP5.1.H.264-FLUX";
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { new { id = Hash(5), name = Pack, source = "1337x", sizeBytes = 30, dir = downloads, completedAt = at } }));
            var folder2 = Path.Combine(downloads, Pack); Directory.CreateDirectory(folder2);
            foreach (var e in new[] { 1, 2, 3 }) File.WriteAllBytes(Path.Combine(folder2, $"Arcane.League.of.Legends.S02E0{e}.1080p.NF.WEB-DL.DDP5.1.H.264-FLUX.mkv"), new byte[10]);
            importer.Expect(Hash(5), new ImportTarget(shows, "Arcane (2021)", MediaKind.Series, 2, 0));
            await importer.ProcessAsync(state, new MediaLibraries(null, null, null), ImportMode.KeepSeeding, automatic: false);
            var placed = Directory.EnumerateFiles(shows, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(shows, x).Replace('\\', '/')).Order().ToList();
            Assert(importer.Find(Hash(5)) is { State: TorLinkImportState.Imported } && placed.SequenceEqual(["Arcane (2021)/Season 02/Arcane (2021) - S02E01.mkv", "Arcane (2021)/Season 02/Arcane (2021) - S02E02.mkv", "Arcane (2021)/Season 02/Arcane (2021) - S02E03.mkv"]),
                "Placed: " + string.Join(", ", placed));
        });
    }

    private static JsonNode Fixture()
    {
        var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "missing.json")))!;
    }
}
