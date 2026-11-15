using System.IO;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Coller un lien magnet »: what a link holds once read (MagnetLinks: episodes, films, what goes with each), what the window
/// proposes and chooses, where it goes, and the relay that has the PC of Jellyfin read it, then download only what was chosen.
/// </summary>
internal static class MagnetLinkTests
{
    private const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";
    private const string Magnet = "magnet:?xt=urn:btih:" + Hash + "&dn=Severance.S02.1080p.WEB-DL";

    private static List<LinkFile> Files(params (string Path, long Size)[] files) => files.Select((f, i) => new LinkFile(i, f.Path, f.Size)).ToList();
    private const long Gb = 1L << 30;

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        HashSet<(int, int)> None() => [];

        await Test("Coller un lien magnet : une saison lue épisode par épisode, ses sous-titres avec elle, l'échantillon et le reste de côté", () =>
        {
            var files = Files([.. Enumerable.Range(1, 10).Select(n => ($"Severance.S02.1080p.WEB-DL/Severance.S02E{n:00}.1080p.WEB-DL.mkv", (long)(1.4 * Gb))),
                ("Severance.S02.1080p.WEB-DL/Severance.S02E01.1080p.WEB-DL.fr.srt", 40_000), ("Severance.S02.1080p.WEB-DL/Sample/severance.sample.mkv", 20_000_000),
                ("Severance.S02.1080p.WEB-DL/RARBG.txt", 100)]);
            var contents = MagnetLinks.Read("Severance.S02.1080p.WEB-DL", files);
            Assert(contents.Episodic && contents.Episodes.Count == 10, "Episodes: " + string.Join(", ", contents.Episodes.Select(e => e.Key)));
            Assert(contents.Episodes.All(e => e.Season == 2) && contents.Episodes.Select(e => e.Number).SequenceEqual(Enumerable.Range(1, 10)), "Numbers");
            Assert(contents.Episodes[0].Files.SequenceEqual([0, 10]), "E01 with its subtitles: " + string.Join(",", contents.Episodes[0].Files));
            Assert(contents.Samples && contents.FileCount == 13, "Sample said");
            // Everything: null (nothing left out); the episodes only: the sample and the text file left out.
            var all = MagnetLinks.FilesOf(LinkOption.Everything, contents, None(), None());
            Assert(all is null, "Everything = every file");
            var lacking = MagnetLinks.FilesOf(LinkOption.Missing, contents, new HashSet<(int, int)> { (2, 1), (2, 2), (2, 3) }, None());
            Assert(lacking!.SequenceEqual([3, 4, 5, 6, 7, 8, 9]), "E04 to E10: " + string.Join(",", lacking!));
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : un film, ses sous-titres, l'échantillon jamais", () =>
        {
            var files = Files(("Millennium.Actress.2001.1080p.BluRay.x264/Millennium.Actress.2001.1080p.BluRay.x264.mkv", 8 * Gb),
                ("Millennium.Actress.2001.1080p.BluRay.x264/Millennium.Actress.2001.1080p.BluRay.x264.en.srt", 50_000),
                ("Millennium.Actress.2001.1080p.BluRay.x264/Millennium.Actress.2001.1080p.BluRay.x264.fr.srt", 50_000),
                ("Millennium.Actress.2001.1080p.BluRay.x264/Sample/sample.mkv", 30_000_000));
            var contents = MagnetLinks.Read("Millennium.Actress.2001.1080p.BluRay.x264", files);
            Assert(!contents.Episodic && contents.FilmCount == 1 && contents.FilmSubtitles == 2, $"Film: {contents.FilmCount}, {contents.FilmSubtitles}");
            Assert(contents.Films.SequenceEqual([0, 1, 2]) && contents.Samples, "Films: " + string.Join(",", contents.Films));
            Assert(MagnetLinks.FilesOf(LinkOption.Film, contents, None(), None())!.SequenceEqual([0, 1, 2]), "The sample stays out");
            Assert(MagnetLinks.Options(contents, None()) is { Options: [LinkOption.Film], Suggested: LinkOption.Film }, "One proposal");
            Assert(MagnetLinks.Title(LinkOption.Film, contents, None()) == "Le film et ses 2 fichiers de sous-titres", MagnetLinks.Title(LinkOption.Film, contents, None()));
            var nothing = MagnetLinks.Read("notes", Files(("notes/readme.txt", 10)));
            Assert(!nothing.Episodic && nothing.Films.Count == 0, "No video");
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : ce que la bibliothèque a déjà, la proposition de Mira, les textes", () =>
        {
            var contents = MagnetLinks.Read("Severance.S02.1080p.WEB-DL", Files([.. Enumerable.Range(1, 10).Select(n => ($"S/Severance.S02E{n:00}.mkv", Gb))]));
            HashSet<(int, int)> owned = [(2, 1), (2, 2), (2, 3), (1, 5)];
            var (options, suggested) = MagnetLinks.Options(contents, owned);
            Assert(options.SequenceEqual([LinkOption.Missing, LinkOption.Everything, LinkOption.Choose]) && suggested == LinkOption.Missing, "Missing put forward");
            Assert(MagnetLinks.Options(contents, None()) is { Options: [LinkOption.Everything, LinkOption.Choose], Suggested: LinkOption.Everything }, "Nothing owned: all of it");
            Assert(MagnetLinks.Title(LinkOption.Missing, contents, owned) == "Les 7 épisodes que tu n’as pas", MagnetLinks.Title(LinkOption.Missing, contents, owned));
            Assert(MagnetLinks.Detail(LinkOption.Missing, contents, owned, None()) == "E04–E10 · 7,0 Go", MagnetLinks.Detail(LinkOption.Missing, contents, owned, None()));
            Assert(MagnetLinks.Library(contents, owned, true) == "E01–E03 déjà dans ta bibliothèque", MagnetLinks.Library(contents, owned, true));
            Assert(MagnetLinks.Library(contents, None(), false) == "Pas encore dans ta bibliothèque" && MagnetLinks.Library(contents, None(), null) == "", "Not there, or not known");
            Assert(MagnetLinks.ChooseFrom(contents) == "E01 à E10", MagnetLinks.ChooseFrom(contents));
            Assert(MagnetLinks.Chosen(contents, None()) == "Aucun choisi pour l’instant", "None chosen");
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : Maj+clic prend une plage, jamais un épisode de la bibliothèque ; Tout · Rien d'un groupe", () =>
        {
            var contents = MagnetLinks.Read("Severance.S02", Files([.. Enumerable.Range(1, 10).Select(n => ($"S/Severance.S02E{n:00}.mkv", Gb))]));
            HashSet<(int, int)> owned = [(2, 3)];
            var chosen = new HashSet<(int, int)>();
            var e = contents.Episodes;
            MagnetLinks.Click(chosen, e, e[1], null, owned);
            MagnetLinks.Click(chosen, e, e[5], e[1], owned);
            Assert(chosen.Order().SequenceEqual([(2, 2), (2, 4), (2, 5), (2, 6)]), "Range E02–E06 without E03: " + string.Join(",", chosen.Order()));
            MagnetLinks.Click(chosen, e, e[2], null, owned);
            Assert(!chosen.Contains((2, 3)), "An episode of the library is never chosen");
            MagnetLinks.Click(chosen, e, e[4], e[5], owned);
            Assert(chosen.Order().SequenceEqual([(2, 2), (2, 4)]), "The range takes the clicked one's new state: " + string.Join(",", chosen.Order()));
            var group = MagnetLinks.Groups(e).Single();
            Assert(group.Label == "" && MagnetLinks.State(group, chosen, owned) == LinkGroupState.Some, "Some");
            MagnetLinks.Toggle(chosen, group, owned);
            Assert(MagnetLinks.State(group, chosen, owned) == LinkGroupState.All && chosen.Count == 9, "All but the library's");
            MagnetLinks.Toggle(chosen, group, owned);
            Assert(chosen.Count == 0 && MagnetLinks.State(group, chosen, owned) == LinkGroupState.None, "None");
            Assert(MagnetLinks.Chosen(contents, new HashSet<(int, int)> { (2, 1), (2, 2) }) == "2 choisis · 2,0 Go", MagnetLinks.Chosen(contents, new HashSet<(int, int)> { (2, 1), (2, 2) }));
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : beaucoup d'épisodes en tranches de 50, plusieurs saisons une par une", () =>
        {
            var anime = MagnetLinks.Read("Naruto.Shippuden.S01.Complete", Files([.. Enumerable.Range(1, 120).Select(n => ($"N/Naruto Shippuden - S01E{n:000}.mkv", 300_000_000L))]));
            var groups = MagnetLinks.Groups(anime.Episodes);
            Assert(groups.Select(g => g.Label).SequenceEqual(["E001–E050", "E051–E100", "E101–E150"]), "Slices: " + string.Join(", ", groups.Select(g => g.Label)));
            Assert(MagnetLinks.Numbered(groups) && MagnetLinks.Cell(anime.Episodes[100], true, MagnetLinks.Digits(120)) == "101", "Numbers alone in a slice");
            var seasons = MagnetLinks.Read("Show.S01-S03", Files([.. from s in Enumerable.Range(1, 3) from n in Enumerable.Range(1, 8) select ($"Show/Season {s}/Show.S{s:00}E{n:00}.mkv", Gb)]));
            var bySeason = MagnetLinks.Groups(seasons.Episodes);
            Assert(bySeason.Select(g => g.Label).SequenceEqual(["Saison 1", "Saison 2", "Saison 3"]) && !MagnetLinks.Numbered(bySeason), "Seasons: " + string.Join(", ", bySeason.Select(g => g.Label)));
            Assert(MagnetLinks.ChooseFrom(seasons) == "Saisons 1 à 3" && MagnetLinks.Cell(seasons.Episodes[0], false) == "E01", "Seasons' words");
            Assert(MagnetLinks.Span(seasons.Episodes.Where(x => x.Season >= 2).ToList(), seasons.Episodes) == "Saisons 2 à 3", "Whole seasons by their names");
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : où il va (la série déjà là, une nouvelle, un film), la place libre", () =>
        {
            var root = Directory.CreateTempSubdirectory("mira-link-").FullName;
            try
            {
                var libraries = new MediaLibraries(Directory.CreateDirectory(Path.Combine(root, "FILMS")).FullName, Directory.CreateDirectory(Path.Combine(root, "SERIES")).FullName, null);
                var episodes = Files([.. Enumerable.Range(1, 3).Select(n => ($"Severance.S02/Severance.S02E{n:00}.1080p.mkv", Gb))]);
                var place = MagnetLinks.Place("Severance.S02.1080p.WEB-DL", episodes, libraries);
                Assert(place is { Root: "SERIES", Folder: "Severance", Exists: false }, $"New: {place}");
                Assert(MagnetLinks.Destination(place, [2], true) == ("dans ", "SERIES › Severance"), MagnetLinks.Destination(place, [2], true).ToString());
                Directory.CreateDirectory(Path.Combine(root, "SERIES", "Severance"));
                place = MagnetLinks.Place("Severance.S02.1080p.WEB-DL", episodes, libraries);
                Assert(place is { Exists: true } && MagnetLinks.Destination(place, [2], true) == ("dans ", "Severance › Saison 02"), $"There: {place}");
                Assert(MagnetLinks.Destination(place, [1, 2, 3], true).Where == "Severance › Saisons 01 à 03", MagnetLinks.Destination(place, [1, 2, 3], true).Where);
                var film = MagnetLinks.Place("Millennium.Actress.2001.1080p.BluRay", Files(("Millennium.Actress.2001.1080p.BluRay.mkv", 8 * Gb)), libraries);
                Assert(film is { Root: "FILMS", Folder: "Millennium Actress (2001)" } && MagnetLinks.Destination(film, [], false).Where == "FILMS › Millennium Actress (2001)", $"Film: {film}");
                Assert(MagnetLinks.Place("x", episodes, new MediaLibraries(null, null, null)) is null && MagnetLinks.Destination(null, [], true).Where == "ta bibliothèque", "No library");
                Assert(MagnetLinks.Free(5_000_000_000_000, "MAISON-PC") == "4,5 To libres sur MAISON-PC" && MagnetLinks.Free(null, "MAISON-PC") == "", MagnetLinks.Free(5_000_000_000_000, "MAISON-PC"));
            }
            finally { Directory.Delete(root, recursive: true); }
            return Task.CompletedTask;
        });

        await Test("Coller un lien magnet : le relais fait lire le lien au PC serveur, puis ne télécharge que les fichiers choisis", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-15T21:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var relay = new TorLinkRelay(() => now);
            AgentReport Report(bool looks = true, bool qbittorrent = true, List<AgentLookOutcome>? looked = null, List<AgentOutcome>? requests = null) =>
                new("MAISON-PC", true, null, [], [], [], requests, [], QBittorrent: qbittorrent, Looks: looks, Looked: looked);
            Assert(relay.Look(Magnet).Error is not null, "Nobody to read it");
            relay.Exchange("pc-1", Report(looks: false));
            Assert(relay.Look(Magnet).Error is not null && relay.Status().Agent is { Looks: false }, "A Mira that doesn't read links");
            Assert(relay.Request(Magnet, null, null, null, null, files: [0, 2]).Error is not null, "Files refused to it");
            relay.Exchange("pc-1", Report(qbittorrent: false));
            Assert(relay.Status().Agent is { Looks: false }, "Reading needs its qBittorrent");
            relay.Exchange("pc-1", Report());
            Assert(relay.Status().Agent is { Looks: true }, "Reads links");
            Assert(relay.Look("magnet:?xt=urn:btih:nothing").Error is not null, "Not a magnet");
            var (id, error) = relay.Look(Magnet);
            Assert(id is not null && error is null && relay.Look(Magnet).Id == id, "The same reading for the same link");
            Assert(relay.LookState(id!) is { State: "waiting" }, "Waiting");
            var work = relay.Exchange("pc-1", Report());
            Assert(work.Looks is [{ Magnet: Magnet } look] && look.Id == id, "Handed to the PC");
            Assert(relay.LookState(id!) is { State: "running" } && relay.Exchange("pc-1", Report()).Looks is null, "Once");
            var files = Enumerable.Range(0, 3).Select(i => new LinkFile(i, $"Severance.S02/Severance.S02E{i + 1:00}.mkv", 1 << 30)).ToList();
            relay.Exchange("pc-1", Report(looked: [new(id!, "done", "Severance.S02.1080p.WEB-DL", files, new("SERIES", "Severance", true), null)]));
            var state = relay.LookState(id!);
            Assert(state is { State: "done", Name: "Severance.S02.1080p.WEB-DL", Place: { Folder: "Severance", Exists: true } } && state.Files!.Count == 3, $"Read: {state}");

            Assert(relay.Request(Magnet, null, null, null, null, files: [0, 0]).Error is not null && relay.Request(Magnet, null, null, null, null, files: []).Error is not null, "Files checked");
            var (request, refused) = relay.Request(Magnet, null, null, null, null, files: [2, 0]);
            Assert(request is not null && refused is null, "Sent: " + refused);
            work = relay.Exchange("pc-1", Report());
            Assert(work.Requests is [{ Pasted: true, Files: [0, 2] }], "The files chosen, in order");
            // Nobody shares it: said, never listed again.
            var (other, _) = relay.Look("magnet:?xt=urn:btih:0000000000000000000000000000000000000001");
            relay.Exchange("pc-1", Report());
            relay.Exchange("pc-1", Report(looked: [new(other!, "silent", null, null, null, null)]));
            Assert(relay.LookState(other!) is { State: "silent", Files: null }, "Silent");
            now += TimeSpan.FromMinutes(20);
            relay.Exchange("pc-1", Report());
            Assert(relay.LookState(id!) is null, "Forgotten after 15 minutes");
            return Task.CompletedTask;
        });
        await Task.CompletedTask;
    }
}
