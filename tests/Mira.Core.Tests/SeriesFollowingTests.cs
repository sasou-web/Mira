using System.IO;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Suivre une série »: which results are the next episodes of a series (SeriesFollowing), where they go once
/// downloaded (MediaPlanner, TorLinkImporter), and the series the relay keeps and hands to the agent (TorLinkRelay).
/// </summary>
internal static class SeriesFollowingTests
{
    private const string SeriesId = "0123456789abcdef0123456789abcdef";
    private const long Gib = 1L << 30;
    private static string Hash(int n) => n.ToString("x40");
    private static string Magnet(int n) => $"magnet:?xt=urn:btih:{Hash(n)}";
    private static (string, AgentHit) Hit(int n, string name, double gib = 1.4, int seeders = 50, string source = "nyaa", string category = "anime") =>
        (category, new AgentHit(Hash(n), name, (long)(gib * Gib), seeders, 1, source, Magnet(n), null));
    private static List<FollowEpisode> Episodes(int season, int from, int to) => Enumerable.Range(from, to - from + 1).Select(e => new FollowEpisode(season, e)).ToList();
    private static WorkFollow Work(List<string>? names = null, FollowEpisode? latest = null, List<FollowEpisode>? taken = null, List<string>? refused = null, string? query = null) =>
        new("f1", SeriesId, "Frieren", query, null, names, latest, taken, refused, null);

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        await Test("Suivre une série : l'épisode suivant, sous un nom de la série, dans la bonne saison, jamais un pack", () =>
        {
            // Frieren: 28 episodes, then a second season that fansubs number from the first episode of the series.
            var frieren = new FollowedSeries("Frieren: Beyond Journey's End", "葬送のフリーレン", 2023, [.. Episodes(1, 1, 28), .. Episodes(2, 1, 2)]);
            var hits = new List<(string, AgentHit)>
            {
                Hit(1, "[SubsPlease] Sousou no Frieren - 31 (1080p) [A1B2C3D4].mkv", seeders: 0, source: "subsplease"),
                Hit(2, "[Erai-raws] Sousou no Frieren - 31 [1080p][Multiple Subtitle].mkv"),
                Hit(3, "[SubsPlease] Sousou no Frieren - 32 (1080p) [A1B2C3D5].mkv", seeders: 0, source: "subsplease"),
                Hit(4, "[SubsPlease] Sousou no Frieren - 02 (1080p) [A1B2C3D6].mkv", seeders: 0, source: "subsplease"),
                Hit(5, "[Judas] Sousou no Frieren (Season 02) [1080p][HEVC x265 10bit][Multi-Subs] (Batch)", 12),
                Hit(6, "Sousou no Frieren S02E03 1080p WEB-DL MULTI x264-GRP", 1.5, category: "tv"),
                Hit(7, "[SubsPlease] Some Other Show - 05 (1080p) [A1B2C3D7].mkv"),
                Hit(8, "[SubsPlease] Sousou no Frieren - 33 (1080p) [A1B2C3D8].mkv", 30, source: "subsplease"),
                Hit(1, "[SubsPlease] Sousou no Frieren - 31 (1080p) [A1B2C3D4].mkv", seeders: 0, source: "subsplease", category: "tv"),
            };
            var check = SeriesFollowing.Choose(Work(["Sousou no Frieren"]), frieren, [], hits, new FollowRelease("SubsPlease", [], 1080));
            Assert(check.Picks.Select(x => (x.Season, x.Episode, x.Magnet)).SequenceEqual([(2, 3, Magnet(1)), (2, 4, Magnet(3))]), "Picks: " + string.Join(", ", check.Picks.Select(x => $"S{x.Season}E{x.Episode} {x.Name}")));
            Assert(check.Note == new FollowNote("found", "S02E03, S02E04") && check.Latest == new FollowEpisode(2, 2) && check.Names.SequenceEqual(["Sousou no Frieren"]), $"Note {check.Note}, latest {check.Latest}, names {string.Join(" / ", check.Names)}");
            Assert(check.Picks[0] is { Group: "SubsPlease", Resolution: 1080, Source: "subsplease" } && check.Picks[0].Languages!.Count == 0, "What the release was, for the next one");
            // Without the group taken before: the ranking, then those sharing it most.
            var any = SeriesFollowing.Choose(Work(["Sousou no Frieren"]), frieren, [], hits, null);
            Assert(any.Picks[0].Magnet == Magnet(6), "Best release without a previous one: " + any.Picks[0].Name);
            // A name Jellyfin does not know: nothing, until the name searched or a release gives it.
            Assert(SeriesFollowing.Choose(Work(), frieren, [], hits, null) is { Picks.Count: 0, Note.Kind: "unknown" }, "Unknown name");
            var learned = SeriesFollowing.Choose(Work(), frieren, [], [Hit(9, "[Judas] Frieren: Beyond Journey's End (Sousou no Frieren) - S02E03 [1080p][HEVC x265 10bit][Multi-Subs]")], null);
            Assert(learned.Picks.Count == 1 && learned.Names.SequenceEqual(["Sousou no Frieren"]), "The other name of a release is learned: " + string.Join(", ", learned.Names));
            Assert(SeriesFollowing.Choose(Work(), frieren, ["Sousou no Frieren"], hits, null).Picks.Count == 2 && SeriesFollowing.Choose(Work(query: "Sousou no Frieren"), frieren, [], hits, null).Picks.Count == 2, "Names known on the PC, or searched");

            // Both numbers possible: « - 9 » after S02E03 with five episodes in season 1 is S02E09 or S02E04; TheTVDB says which is out.
            var short1 = new FollowedSeries("Frieren: Beyond Journey's End", null, 2023, [.. Episodes(1, 1, 5), .. Episodes(2, 1, 3)]);
            var nine = new List<(string, AgentHit)> { Hit(1, "[SubsPlease] Sousou no Frieren - 09 (1080p) [A1B2C3D4].mkv", seeders: 0, source: "subsplease") };
            Assert(SeriesFollowing.Choose(Work(["Sousou no Frieren"]), short1, [], nine, null).Picks.Count == 0, "Two readings, nothing known: left alone");
            Assert(SeriesFollowing.Choose(Work(["Sousou no Frieren"]), short1, [], nine, null, new HashSet<FollowEpisode> { new(2, 4) }).Picks.Single() is { Season: 2, Episode: 4 }, "TheTVDB says S02E04 is out");

            // Taken and refused releases, episodes taken since, the latest of the library never going back.
            var after = SeriesFollowing.Choose(Work(["Sousou no Frieren"], taken: [new(2, 3)], refused: [Hash(3)]), frieren, [], hits, null);
            Assert(after.Picks.Count == 0 && after.Note == new FollowNote("held", "S02E05", "size"), $"Taken and refused: {after.Note}");
            var deleted = new FollowedSeries(frieren.Name, null, 2023, Episodes(2, 1, 2));
            Assert(SeriesFollowing.Choose(Work(["Sousou no Frieren"], latest: new(2, 4)), deleted, [], [Hit(10, "[SubsPlease] Sousou no Frieren - 03 (1080p) [AB].mkv")], null) is { Picks.Count: 0, Note: { Kind: "nothing", Episode: "S02E04" }, Latest: { Season: 2, Episode: 2 } },
                "An episode watched and deleted is not new");

            // In its season, or the first ones of the next; never a jump, an ambiguous number, a pack, a year far off.
            var show = new FollowedSeries("The Bear", null, 2022, Episodes(1, 1, 12));
            var bear = SeriesFollowing.Choose(Work(), show, [], [
                Hit(11, "The.Bear.S01E13.1080p.WEB.h264-ETHEL", category: "tv"), Hit(12, "The.Bear.S02E01.1080p.WEB.h264-ETHEL", category: "tv"),
                Hit(13, "The.Bear.S03E01.1080p.WEB.h264-ETHEL", category: "tv"), Hit(14, "The.Bear.S01E30.1080p.WEB.h264-ETHEL", category: "tv"),
                Hit(15, "The.Bear.S02.COMPLETE.1080p.WEB.h264-ETHEL", 9, category: "tv"), Hit(16, "The.Bear.S01E13-E14.1080p.WEB.h264-ETHEL", 3, category: "tv"),
                Hit(17, "The.Bear.1985.S02E02.1080p.WEB.h264-ETHEL", category: "tv"), Hit(18, "The.Bear.S02E02.1080p.CAM.h264-ETHEL", category: "tv")], null);
            Assert(bear.Picks.Select(x => (x.Season, x.Episode)).SequenceEqual([(1, 13), (2, 1)]), "The Bear: " + string.Join(", ", bear.Picks.Select(x => x.Name)));
            var packs = SeriesFollowing.Choose(Work(), show, [], [Hit(15, "The.Bear.S02.COMPLETE.1080p.WEB.h264-ETHEL", 9, category: "tv"), Hit(16, "The.Bear.S01E13-E14.1080p.WEB.h264-ETHEL", 3, category: "tv")], null);
            Assert(packs.Picks.Count == 0 && packs.Note == new FollowNote("held", "S01E13-E14", "pack"), $"Packs only: {packs.Note}");
            var split = new FollowedSeries("Show", null, null, [.. Episodes(1, 1, 5), .. Episodes(2, 1, 2)]);
            Assert(SeriesFollowing.Choose(Work(), split, [], [Hit(19, "[Group] Show - 08 (1080p).mkv")], null).Picks.Count == 0, "S02E08 or S02E03: not chosen");
            Assert(SeriesFollowing.Choose(Work(), split, [], [Hit(19, "[Group] Show - 16 (1080p).mkv")], null).Picks.Single() is { Season: 2, Episode: 11 }, "Only S02E11 fits");
            Assert(SeriesFollowing.Choose(Work(), show, [], Enumerable.Range(13, 9).Select(e => Hit(20 + e, $"The.Bear.S01E{e}.1080p.WEB.h264-ETHEL", category: "tv")).ToList(), null).Picks.Count == SeriesFollowing.MaxPicks, "A few at a time");

            // The same language as before, the sources, the size.
            var french = SeriesFollowing.Choose(Work(), show, [], [Hit(40, "The.Bear.S01E13.FRENCH.1080p.WEB.h264-FW", category: "tv")], new FollowRelease(null, ["VOSTFR"], null));
            Assert(french.Picks.Count == 0 && french.Note == new FollowNote("held", "S01E13", "language"), $"Language: {french.Note}");
            Assert(SeriesFollowing.Choose(Work(), show, [], [Hit(41, "The.Bear.S01E13.1080p.WEB.h264-ETHEL", seeders: 1, category: "tv")], null).Note == new FollowNote("held", "S01E13", "health"), "Too few sources");
            Assert(SeriesFollowing.Choose(Work(), show, [], [Hit(42, "The.Bear.S01E13.2160p.REMUX-X", 30, category: "tv")], null).Note == new FollowNote("held", "S01E13", "size"), "Over 25 GB");
            string[] vo = [], multi = ["MULTI"], vf = ["VFF"], st = ["VOSTFR"];
            Assert(SeriesFollowing.SameLanguage(vf, null) && SeriesFollowing.SameLanguage(multi, vo) && !SeriesFollowing.SameLanguage(vf, vo) && SeriesFollowing.SameLanguage(st, vo)
                && !SeriesFollowing.SameLanguage(vf, multi) && SeriesFollowing.SameLanguage(["VF"], vf) && !SeriesFollowing.SameLanguage(vo, st) && SeriesFollowing.SameLanguage(multi, st), "Languages");

            // What is searched, what the library has.
            Assert(SeriesFollowing.Queries(null, "Doctor Who (2005)", "Doctor Who").SequenceEqual(["Doctor Who"]) && SeriesFollowing.Queries(" -Frieren ", "x", "y").SequenceEqual(["Frieren"])
                && SeriesFollowing.Queries(null, "Frieren: Beyond Journey's End", "Sousou No Frieren").SequenceEqual(["Sousou No Frieren", "Frieren: Beyond Journey's End"]), "Queries");
            Assert(SeriesFollowing.Categories("anime").SequenceEqual(["anime", "tv"]) && SeriesFollowing.Categories("tv").SequenceEqual(["tv"]), "Categories");
            var owned = SeriesFollowing.Owned([new MediaItem { ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 }, new MediaItem { ParentIndexNumber = 0, IndexNumber = 1 }, new MediaItem { ParentIndexNumber = 1, IndexNumber = 3 }, new MediaItem { ParentIndexNumber = 1 }]);
            Assert(owned.SequenceEqual(Episodes(1, 1, 3)) && SeriesFollowing.Latest(owned) == new FollowEpisode(1, 3) && SeriesFollowing.Label(new(1, 3)) == "S01E03", "Owned episodes, double ones counted, never specials");
            return Task.CompletedTask;
        });

        await Test("Suivre une série : l'épisode rangé dans le dossier de sa série, sous son numéro, rangement automatique coupé", async () =>
        {
            var dir = Path.Combine(testRoot, "series-following"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var downloads = Path.Combine(dir, "downloads"); var anime = Path.Combine(dir, "ANIME"); var show = Path.Combine(anime, "Frieren", "Saison 2");
            foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, show }) Directory.CreateDirectory(folder);
            File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
            var importer = new TorLinkImporter(Path.Combine(dir, "profile"));
            var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
            object Item(string id, string name) => new { id, name, source = "nyaa", sizeBytes = 10, dir = downloads, completedAt = at };
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { Item(Hash(1), "[SubsPlease] Sousou no Frieren - 31 (1080p) [A1B2C3D4].mkv"), Item(Hash(2), "Some.Film.2020.mkv"), Item(Hash(3), "[Erai-raws] Sousou no Frieren - 32 [1080p]") }));
            File.WriteAllBytes(Path.Combine(downloads, "[SubsPlease] Sousou no Frieren - 31 (1080p) [A1B2C3D4].mkv"), new byte[10]);
            File.WriteAllBytes(Path.Combine(downloads, "Some.Film.2020.mkv"), new byte[10]);
            var pack = Path.Combine(downloads, "[Erai-raws] Sousou no Frieren - 32 [1080p]"); Directory.CreateDirectory(pack);
            File.WriteAllBytes(Path.Combine(pack, "[Erai-raws] Sousou no Frieren - 32 [1080p].mkv"), new byte[10]);
            File.WriteAllBytes(Path.Combine(pack, "[Erai-raws] Sousou no Frieren - 32 [1080p].fr.srt"), new byte[4]);
            File.WriteAllBytes(Path.Combine(pack, "NCOP.mkv"), new byte[2]);
            importer.Expect(Hash(1).ToUpperInvariant(), new ImportTarget(anime, "Frieren", MediaKind.Anime, 2, 3));
            importer.Expect(Hash(3), new ImportTarget(anime, "Frieren", MediaKind.Anime, 2, 4));
            await importer.ProcessAsync(state, new MediaLibraries(null, null, null), ImportMode.KeepSeeding, automatic: false);
            Assert(importer.Find(Hash(2)) is null, "Automatic placement off: a download not expected stays");
            Assert(importer.Find(Hash(1)) is { State: TorLinkImportState.Imported, Kind: MediaKind.Anime } && File.Exists(Path.Combine(show, "Frieren - S02E03.mkv")),
                "Expected episode placed under its number, in the season folder there: " + string.Join(", ", Directory.EnumerateFiles(anime, "*", SearchOption.AllDirectories)));
            Assert(File.Exists(Path.Combine(show, "Frieren - S02E04.mkv")) && File.Exists(Path.Combine(show, "Frieren - S02E04.fr.srt")) && Directory.EnumerateFiles(Path.Combine(anime, "Frieren", "Extras")).Count() == 1,
                "A folder: its episode, its subtitles, its extras: " + string.Join(", ", Directory.EnumerateFiles(anime, "*", SearchOption.AllDirectories)));
            Assert(new TorLinkImporter(Path.Combine(dir, "profile")).Expected(Hash(1)) == new ImportTarget(anime, "Frieren", MediaKind.Anime, 2, 3), "Expected downloads kept with the journal");
            var (names, title, prefer) = SeriesFollowing.FromJournal(importer.Entries, Path.Combine(anime, "Frieren"));
            Assert(names.SequenceEqual(["Sousou no Frieren"]) && title == "Sousou no Frieren" && prefer is { Group: "Erai-raws", Resolution: 1080 } && SeriesFollowing.FromJournal(importer.Entries, Path.Combine(anime, "Other")).Title is null,
                $"From the journal: {string.Join(", ", names)} / {title} / {prefer}");
            Assert(MediaLibraries.IsAnime(new VirtualFolder("Animés", "tvshows", [anime], null)) && !MediaLibraries.IsAnime(new VirtualFolder("Dessins animés", "tvshows", [Path.Combine(dir, "Cartoons")], null)), "Anime libraries");
        });

        await Test("Suivre une série : chercher le jour de la sortie, toutes les 2 h ce jour-là, sinon une fois par jour (FollowSchedule)", () =>
        {
            var at = DateTimeOffset.Parse("2026-10-29T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            FollowAiring Air(int episode, string day) => new(1, episode, DateTimeOffset.Parse(day + "T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
            var latest = new FollowEpisode(1, 4);
            Assert(FollowSchedule.Next(at, latest, null) == at + TimeSpan.FromHours(6) && FollowSchedule.Next(at, latest, []) == at + TimeSpan.FromHours(6), "No dates: every 6 h");
            Assert(FollowSchedule.Next(at, latest, [Air(5, "2026-10-29")]) == at + TimeSpan.FromHours(2), "Out today: every 2 h");
            Assert(FollowSchedule.Next(at, latest, [Air(5, "2026-10-30")]) == DateTimeOffset.Parse("2026-10-29T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Tomorrow in its country, maybe tonight here: from half a day before");
            Assert(FollowSchedule.Next(at, latest, [Air(5, "2026-10-31")]) == at + TimeSpan.FromDays(1) && FollowSchedule.Next(at + TimeSpan.FromDays(1), latest, [Air(5, "2026-10-31")]) == DateTimeOffset.Parse("2026-10-30T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Saturday: once a day, then from Friday noon UTC");
            Assert(FollowSchedule.Next(at, latest, [Air(5, "2026-11-12")]) == at + TimeSpan.FromDays(1), "Weeks away: once a day");
            Assert(FollowSchedule.Next(at, latest, [Air(5, "2026-10-20"), Air(6, "2026-11-12")]) == at + TimeSpan.FromHours(6), "Behind: every 6 h");
            Assert(FollowSchedule.Next(at, new(1, 5), [Air(5, "2026-10-29"), Air(6, "2026-11-05")]) == DateTimeOffset.Parse("2026-10-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Already here or taken: the next one, within a day");
            Assert(FollowSchedule.Coming(latest, [Air(5, "2026-10-20"), Air(6, "2026-10-28"), Air(7, "2026-11-04")], at) == Air(6, "2026-10-28"), "Said: the first whose window isn't over");
            Assert(FollowSchedule.Out([Air(5, "2026-10-29"), Air(6, "2026-10-30"), Air(7, "2026-11-04")], at).SetEquals([new FollowEpisode(1, 5)]) && FollowSchedule.Out([Air(6, "2026-10-30")], at + TimeSpan.FromHours(2)).Count == 1, "Out by now, from half a day ahead");
            var episodes = new List<MediaItem>
            {
                new() { Type = "Episode", LocationType = "Virtual", ParentIndexNumber = 1, IndexNumber = 6, PremiereDate = DateTimeOffset.Parse("2026-11-05T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
                new() { Type = "Episode", LocationType = "Virtual", ParentIndexNumber = 0, IndexNumber = 1, PremiereDate = at },
                new() { Type = "Episode", LocationType = "Virtual", ParentIndexNumber = 1, IndexNumber = 7 },
                new() { Type = "Episode", LocationType = "FileSystem", ParentIndexNumber = 1, IndexNumber = 4, PremiereDate = at },
                new() { Type = "Episode", LocationType = "Virtual", ParentIndexNumber = 1, IndexNumber = 5, PremiereDate = DateTimeOffset.Parse("2026-10-29T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture) },
            };
            Assert(EpisodeCalendar.Airing(episodes, at).SequenceEqual([Air(5, "2026-10-29"), Air(6, "2026-11-05")]), "Sent by the agent: virtual, numbered, dated, by date");
            return Task.CompletedTask;
        });
        await Test("Suivre une série : le relais garde les séries, les donne à l'agent et retient ses choix 10 min", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
            var file = Path.Combine(testRoot, "relay-follows", "Mira.follows.json");
            var relay = new TorLinkRelay(() => now, file);
            AgentReport Report(List<AgentFollowOutcome>? follows, List<AgentOutcome>? requests = null, List<RelayDownload>? downloads = null, bool accepting = true) =>
                new("PC-SALON", accepting, null, downloads, null, null, requests, null, follows);
            Assert(relay.Follow("nope", "Frieren", null) == (null, "Série inconnue.") && relay.Follow(SeriesId, " ", null).Error is not null, "Unknown series");
            var (id, _) = relay.Follow(SeriesId.ToUpperInvariant(), "Frieren", " Sousou no Frieren ");
            Assert(id is not null && relay.Follow(SeriesId, "Frieren: Beyond Journey's End", null).Id == id, "Followed once, its name changed");
            Assert(relay.Status().Follows!.Single() is { Name: "Frieren: Beyond Journey's End", Query: null, Next: null, Checking: false, Taken.Count: 0, Picks.Count: 0 }, "Followed, checked soon");

            // An agent of 0.8.4 checks nothing; one that cannot download now neither.
            Assert(relay.Exchange("device-1", Report(null)).Follows is null && relay.Exchange("device-1", Report([], accepting: false)).Follows is null, "Not for an older agent or a busy TorLink");
            var work = relay.Exchange("device-1", Report([]));
            Assert(work.Follows!.Single() is { SeriesId: SeriesId, Name: "Frieren: Beyond Journey's End", Folder: null, Prefer: null } && relay.Follows().Single().Checking, "Handed to the agent");
            Assert(relay.Exchange("device-1", Report([])).Follows!.Count == 0, "Not twice while it checks");
            Assert(relay.Status().Agent!.Follows, "The agent checks series");

            // Its choices wait for « Annuler »; the requests list never shows them.
            var folder = @"C:\Users\ogsas\Videos\MEDIAS\ANIME\Frieren";
            FollowPick Pick(int n, int episode) => new(2, episode, $"[SubsPlease] Sousou no Frieren - {28 + episode} (1080p)", Magnet(n), 2 * Gib, 0, "subsplease", "SubsPlease", [], 1080);
            relay.Exchange("device-1", Report([new(id!, "done", new FollowNote("found", "S02E03, S02E04"), folder, "anime", ["Sousou no Frieren"], new(2, 2), [Pick(1, 3), Pick(2, 4), Pick(3, 3)])]));
            var follow = relay.Follows().Single();
            Assert(follow is { Checking: false, Latest: "S02E02", Note.Kind: "found" } && follow.Next == now + FollowSchedule.Every && follow.Picks.Select(x => (x.Episode, x.State)).SequenceEqual([("S02E03", "waiting"), ("S02E04", "waiting")])
                && follow.Picks[0].SendAt == now + SeriesFollowing.Hold && relay.Status().Requests.Count == 0, "Held picks, one per episode");
            Assert(relay.Exchange("device-1", Report([])).Requests.Count == 0, "Not sent before 10 min");
            Assert(relay.Cancel(follow.Picks[1].Id) == true && relay.Follows().Single().Picks.Count == 1, "Cancelled");
            now += TimeSpan.FromMinutes(11);
            var sent = relay.Exchange("device-1", Report([])).Requests.Single();
            Assert(sent.Place == new FollowPlace(folder, 2, 3) && sent.Magnet == Magnet(1), "Sent after 10 min, with where it goes");
            Assert(relay.Cancel(sent.Id) == false, "Too late to cancel");
            relay.Exchange("device-1", Report([], [new(sent.Id, "sent", null)]));
            Assert(relay.Follows().Single() is { Latest: "S02E03", Picks.Count: 0, Taken: [{ Episode: "S02E03" }] }, "Taken");

            // Kept on disk, checked again after a restart, with what was taken and refused.
            var reopened = new TorLinkRelay(() => now, file);
            var again = reopened.Exchange("device-2", Report([])).Follows!.Single();
            Assert(again is { Folder: not null, Latest: { Season: 2, Episode: 2 }, Prefer: { Group: "SubsPlease", Resolution: 1080 } } && again.Taken!.SequenceEqual([new FollowEpisode(2, 3)]) && again.Refused!.SequenceEqual([Hash(2)]) && again.Names!.SequenceEqual(["Sousou no Frieren"]),
                $"After a restart: {JsonSerializer.Serialize(again)}");
            // Given up by TorLink: refused, looked for again.
            reopened.Exchange("device-2", Report([new(id!, "done", new FollowNote("nothing", "S02E03"), folder, "anime", null, new(2, 2), null)], downloads: [new(Hash(1), "x", "failed", 3, 0, 0)]));
            Assert(reopened.Follows().Single() is { Taken.Count: 0, Latest: "S02E02", Next: not null }, "A failed download is no longer taken");

            // Every 6 hours, half an hour after a failure, again when the agent vanished.
            now += TimeSpan.FromHours(1);
            Assert(reopened.Exchange("device-2", Report([])).Follows!.Count == 0, "Not before 6 hours");
            Assert(reopened.CheckNow(id!) && reopened.Exchange("device-2", Report([])).Follows!.Count == 1, "Chercher maintenant");
            reopened.Exchange("device-2", Report([new(id!, "failed", new FollowNote("failed", Detail: "TorLink n’est pas prêt."), null, null, null, null, null)]));
            Assert(reopened.Follows().Single().Note is { Kind: "failed", Detail: "TorLink n’est pas prêt." } && reopened.Exchange("device-2", Report([])).Follows!.Count == 0, "Failed: not at once");
            now += TorLinkRelay.Retry;
            Assert(reopened.Exchange("device-2", Report([])).Follows!.Count == 1, "Failed: again in 30 min");
            now += TorLinkRelay.FollowClaim;
            Assert(reopened.Exchange("device-3", Report([])).Follows!.Count == 1, "Never reported: handed again");
            // TheTVDB's dates (the agent's Airing): S02E03 out yesterday is behind, S02E04 comes in two days.
            reopened.CheckNow(id!); reopened.Exchange("device-3", Report([]));
            reopened.Exchange("device-3", Report([new(id!, "done", new FollowNote("nothing", "S02E02"), folder, "anime", null, new(2, 2), null,
                [new(2, 3, now.UtcDateTime.Date.AddDays(-1)), new(2, 4, now.UtcDateTime.Date.AddDays(2)), new(0, 1, now), new(2, 5, now.AddYears(30))])]));
            var dated = reopened.Follows().Single();
            Assert(dated is { Airs.Episode: "S02E03" } && dated.Next == now + TimeSpan.FromHours(2), $"In the window of S02E03: every 2 h ({dated.Next}, {dated.Airs})");
            Assert(!reopened.Unfollow("autre") && reopened.Unfollow(id!) && reopened.Follows().Count == 0 && new TorLinkRelay(() => now, file).Follows().Count == 0, "Unfollowed");
            for (var i = 0; i < TorLinkRelay.MaxFollows; i++) reopened.Follow(i.ToString("x32"), $"Série {i}", null);
            Assert(reopened.Follow(SeriesId, "Une de trop", null).Error is not null, "At most 40");
            File.WriteAllText(file, "{");
            Assert(new TorLinkRelay(() => now, file).Follows().Count == 0 && Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.bad-*").Any(), "Unreadable: put aside");
            return Task.CompletedTask;
        });
    }
}
