using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using static TestSupport;

/// <summary>TorLink: release names, files of a .torrent, history and library folders. Free of WPF: also run outside Windows.</summary>
internal static class TorLinkTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
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
                Assert(!File.Exists(NodeInstaller.Executable(target)) && !Directory.Exists(NodeInstaller.NextFolder(target) + ".partial") && !Directory.Exists(NodeInstaller.NextFolder(target)), folder + " left Node.js behind");
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
            // TorLink: the tree of the lock shipped in Mira, into the folder being prepared, without install scripts.
            var arguments = Mira.Core.TorLink.TorLinkPackage.NpmArguments(@"C:\Mira\data\node\node_modules\npm\bin\npm-cli.js", @"C:\Mira\data\torlink-next.partial");
            Assert(arguments[0].EndsWith("npm-cli.js") && arguments[1] == "ci" && arguments.Contains("--omit=dev")
                && arguments.Contains("--ignore-scripts") && arguments[arguments.ToList().IndexOf("--prefix") + 1] == @"C:\Mira\data\torlink-next.partial", "npm arguments: " + string.Join(" ", arguments));
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
        await Test("TorLink : le batch d'un anime (« (01-28) ») rejoint sa série au lieu d'en faire une seconde", () =>
        {
            // The episodes of a batch in parentheses stayed in the title: « Sousou no Frieren 01-28 », a second series in
            // Jellyfin 12.1 beside « Sousou no Frieren » (two folders, two series).
            foreach (var (name, title) in new[]
            {
                ("[SubsPlease] Sousou no Frieren (01-28) (1080p) [Batch]", "Sousou no Frieren"), ("[DB] Cowboy Bebop (01-26) [Dual Audio 10bit 1080p][HEVC-x265]", "Cowboy Bebop"),
                ("[SubsPlease] One Piece (1071-1085) (1080p) [Batch]", "One Piece"), ("[Anime Time] Naruto Shippuden (001-500) [Dual Audio][1080p]", "Naruto Shippuden"),
                ("[Group] Show (01 ~ 12) [1080p]", "Show"), ("9-1-1.S07E05.1080p.WEB.h264-ETHEL", "9-1-1"), ("[Erai-raws] Shingeki no Kyojin - The Final Season - 01 ~ 16 [1080p]", "Shingeki no Kyojin - The Final Season"),
            })
                Assert(ReleaseName.Show(name) == (title, null), $"{name} → {ReleaseName.Show(name)}");
            // A range of years is not a batch: unchanged.
            Assert(ReleaseName.Readable("The Matrix Trilogy (1999-2003) 1080p", out _) == "The Matrix Trilogy 1999-2003 1080p", "Years");
            var dir = Path.Combine(testRoot, "torlink-batch"); var anime = Path.Combine(dir, "ANIME");
            Directory.CreateDirectory(Path.Combine(anime, "Sousou no Frieren", "Season 01"));
            const string Batch = "[SubsPlease] Sousou no Frieren (01-28) (1080p) [Batch]";
            SourceFile Episode(int number) => new(Path.Combine(dir, "downloads", $"{Batch}/[SubsPlease] Sousou no Frieren - {number:00} (1080p) [ABCD12{number:00}].mkv"), $"{Batch}/[SubsPlease] Sousou no Frieren - {number:00} (1080p) [ABCD12{number:00}].mkv", 1_400_000_000);
            var plan = MediaPlanner.Plan(Batch, "subsplease", [Episode(1), Episode(2)], new MediaLibraries(null, null, anime));
            Assert(plan.Kind == MediaKind.Anime && plan.Title == "Sousou no Frieren · 2 épisodes" && string.Join(" | ", plan.Operations.Select(x => Path.GetRelativePath(anime, x.Destination).Replace('\\', '/')))
                == "Sousou no Frieren/Season 01/Sousou no Frieren - S01E01.mkv | Sousou no Frieren/Season 01/Sousou no Frieren - S01E02.mkv", "Batch: " + string.Join(" | ", plan.Operations.Select(x => x.Destination)));
            return Task.CompletedTask;
        });
        await Test("TorLink : une duologie « Part 1 », « Part 2 » rangée en deux films, les parties d'un même film (cd1, cd2) ensemble", () =>
        {
            // Two films numbered as parts in their own title (the Deathly Hallows, Mockingjay, Breaking Dawn) became one film of
            // two parts named after the torrent: Jellyfin 12.1 lists that folder as one title (PartCount 2), both films one
            // after the other under the length of the first.
            var dir = Path.Combine(testRoot, "torlink-parts"); var films = Path.Combine(dir, "FILMS");
            var libraries = new MediaLibraries(films, null, null);
            SourceFile File(string path, long size) => new(Path.Combine(dir, "downloads", path), path, size);
            string Targets(ImportPlan plan) => string.Join(" | ", plan.Operations.Select(x => Path.GetRelativePath(films, x.Destination).Replace('\\', '/')));
            const string Hallows = "Harry.Potter.and.the.Deathly.Hallows.Part.1.and.2.2010-2011.1080p.BluRay.x264";
            var duology = MediaPlanner.Plan(Hallows, "x1337", [File($"{Hallows}/Harry.Potter.and.the.Deathly.Hallows.Part.1.2010.1080p.BluRay.x264.mkv", 9_000_000_000),
                File($"{Hallows}/Harry.Potter.and.the.Deathly.Hallows.Part.2.2011.1080p.BluRay.x264.mkv", 8_500_000_000), File($"{Hallows}/Harry.Potter.and.the.Deathly.Hallows.Part.2.2011.1080p.BluRay.x264.en.srt", 90_000)], libraries);
            Assert(duology.Title == "2 films" && Targets(duology) == "Harry Potter and the Deathly Hallows Part 1 (2010)/Harry Potter and the Deathly Hallows Part 1 (2010).mkv"
                + " | Harry Potter and the Deathly Hallows Part 2 (2011)/Harry Potter and the Deathly Hallows Part 2 (2011).mkv"
                + " | Harry Potter and the Deathly Hallows Part 2 (2011)/Harry Potter and the Deathly Hallows Part 2 (2011).en.srt", "Deathly Hallows: " + Targets(duology));
            var dawn = MediaPlanner.Plan("Twilight.Breaking.Dawn.Duology.1080p", "tpb", [File("Twilight.Breaking.Dawn.Part.1.2011.1080p.mkv", 2_000_000_000), File("Twilight.Breaking.Dawn.Part.2.2012.1080p.mkv", 2_100_000_000)], libraries);
            Assert(Targets(dawn) == "Twilight Breaking Dawn Part 2 (2012)/Twilight Breaking Dawn Part 2 (2012).mkv | Twilight Breaking Dawn Part 1 (2011)/Twilight Breaking Dawn Part 1 (2011).mkv", "Breaking Dawn: " + Targets(dawn));
            // The parts of one film stay one film, as Jellyfin stacks them: « CD1 » after the year, uneven parts, no year at all.
            const string Rip = "Some.Movie.2010.DVDRip.XviD-GRP";
            var cds = MediaPlanner.Plan(Rip, "tpb", [File($"{Rip}/{Rip}.CD1.avi", 700_000_000), File($"{Rip}/{Rip}.CD2.avi", 300_000_000)], libraries);
            Assert(Targets(cds) == "Some Movie (2010)/Some Movie (2010) - part1.avi | Some Movie (2010)/Some Movie (2010) - part2.avi", "CD1, CD2: " + Targets(cds));
            var bare = MediaPlanner.Plan("Old.Film.DVDRip", null, [File("Old.Film.DVDRip/Old.Film.DVDRip.part2.avi", 690_000_000), File("Old.Film.DVDRip/Old.Film.DVDRip.part1.avi", 700_000_000)], libraries);
            Assert(Targets(bare) == "Old Film/Old Film - part1.avi | Old Film/Old Film - part2.avi", "Without a year: " + Targets(bare));
            var spaced = MediaPlanner.Plan("Film (2019) [1080p]", null, [File("Film (2019) [1080p]/Film (2019) - cd1.mkv", 4_000_000_000), File("Film (2019) [1080p]/Film (2019) - cd2.mkv", 3_000_000_000)], libraries);
            Assert(Targets(spaced) == "Film (2019)/Film (2019) - part1.mkv | Film (2019)/Film (2019) - part2.mkv", "cd1, cd2: " + Targets(spaced));
            return Task.CompletedTask;
        });
        await Test("TorLink : une collection de films (« Complete Collection », « Intégrale ») rangée en films, une saison aux fichiers « 01 - Titre » en épisodes", () =>
        {
            // Both were placed as a series whose every video went to its Extras folder: on Jellyfin 12.1, a series without
            // seasons or episodes, the films missing from Films and the episodes from the series, only bonus videos of it.
            var dir = Path.Combine(testRoot, "torlink-packs"); var films = Path.Combine(dir, "FILMS"); var series = Path.Combine(dir, "SERIES");
            var libraries = new MediaLibraries(films, series, null);
            SourceFile File(string path, long size) => new(Path.Combine(dir, "downloads", path), path, size);
            string Targets(ImportPlan plan) => string.Join(" | ", plan.Operations.Select(x => Path.GetRelativePath(plan.LibraryRoot!, x.Destination).Replace('\\', '/')));
            const string Potter = "Harry.Potter.Complete.Collection.1080p.BluRay.x264";
            var potter = MediaPlanner.Plan(Potter, "x1337", [File($"{Potter}/Harry.Potter.and.the.Philosophers.Stone.2001.1080p.BluRay.x264.mkv", 9_000_000_000),
                File($"{Potter}/Harry.Potter.and.the.Chamber.of.Secrets.2002.1080p.BluRay.x264.mkv", 9_500_000_000), File($"{Potter}/Harry.Potter.and.the.Chamber.of.Secrets.2002.1080p.BluRay.x264.en.srt", 90_000)], libraries);
            Assert(potter.Kind == MediaKind.Movie && potter.Title == "2 films" && Targets(potter) == "Harry Potter and the Chamber of Secrets (2002)/Harry Potter and the Chamber of Secrets (2002).mkv"
                + " | Harry Potter and the Philosophers Stone (2001)/Harry Potter and the Philosophers Stone (2001).mkv"
                + " | Harry Potter and the Chamber of Secrets (2002)/Harry Potter and the Chamber of Secrets (2002).en.srt", "Complete Collection: " + Targets(potter));
            const string Indiana = "Indiana Jones Integrale 1981-2008 MULTi 1080p";
            var indiana = MediaPlanner.Plan(Indiana, "x1337", [File($"{Indiana}/Les Aventuriers de l'arche perdue (1981).mkv", 8_000_000_000), File($"{Indiana}/Indiana Jones et le Temple maudit (1984).mkv", 8_200_000_000)], libraries);
            Assert(indiana.Kind == MediaKind.Movie && Targets(indiana) == "Indiana Jones et le Temple maudit (1984)/Indiana Jones et le Temple maudit (1984).mkv | Les Aventuriers de l'arche perdue (1981)/Les Aventuriers de l'arche perdue (1981).mkv", "Intégrale: " + Targets(indiana));
            var saga = MediaPlanner.Plan("Star.Wars.Complete.Saga.1080p", "x1337", [File("Star.Wars.Complete.Saga.1080p/Star.Wars.Episode.IV.A.New.Hope.1977.1080p.mkv", 8_000_000_000),
                File("Star.Wars.Complete.Saga.1080p/Star.Wars.Episode.V.The.Empire.Strikes.Back.1980.1080p.mkv", 8_200_000_000)], libraries);
            Assert(saga.Kind == MediaKind.Movie && saga.Title == "2 films", "Complete Saga: " + Targets(saga));
            // A pack of every episode stays a show, its episodes numbered « Show - 01 ».
            var note = MediaPlanner.Plan("Death.Note.Complete.Series.1080p", "x1337-tv", [File("Death.Note.Complete.Series.1080p/Death Note - 01.mkv", 700_000_000), File("Death.Note.Complete.Series.1080p/Death Note - 02.mkv", 700_000_000)], libraries);
            Assert(note.Kind == MediaKind.Series && Targets(note) == "Death Note/Season 01/Death Note - S01E01.mkv | Death Note/Season 01/Death Note - S01E02.mkv", "Complete Series: " + Targets(note));

            // A season's files named by their number first, with or without a season folder.
            string Season(string torrent, params string[] names) => Targets(MediaPlanner.Plan(torrent, "x1337-tv", names.Select(n => File($"{torrent}/{n}", n.EndsWith(".srt") ? 50_000 : 700_000_000)).ToList(), libraries));
            Assert(Season("The.Office.US.S03.1080p.WEB-DL", "01 - Gay Witch Hunt.mkv", "02 - The Convention.mkv", "01 - Gay Witch Hunt.en.srt")
                == "The Office US/Season 03/The Office US - S03E01.mkv | The Office US/Season 03/The Office US - S03E02.mkv | The Office US/Season 03/The Office US - S03E01.en.srt", "« 01 - Title »");
            Assert(Season("Les.Simpson.Saison.1.FRENCH", "01. Noel mortel.avi", "E02 - Bart le genie.avi") == "Les Simpson/Season 01/Les Simpson - S01E01.avi | Les Simpson/Season 01/Les Simpson - S01E02.avi", "« 01. Title », « E02 - Title »");
            Assert(Season("The.Office.US.Complete", "Season 3/01 - Gay Witch Hunt.mkv", "Season 4/01 - Fun Run.mkv", "The.Office.US.S05.1080p/The Office - 01.mkv")
                == "The Office US/Season 03/The Office US - S03E01.mkv | The Office US/Season 04/The Office US - S04E01.mkv | The Office US/Season 05/The Office US - S05E01.mkv", "Season folders: " + Season("The.Office.US.Complete", "Season 3/01 - Gay Witch Hunt.mkv", "Season 4/01 - Fun Run.mkv", "The.Office.US.S05.1080p/The Office - 01.mkv"));
            Assert(Season("30.Rock.S01.1080p", "30 Rock 01 Pilot.mkv", "30 Rock 02 The Aftermath.mkv") == "30 Rock/Season 01/30 Rock - S01E01.mkv | 30 Rock/Season 01/30 Rock - S01E02.mkv", "« Show 01 Title »");
            // The digits of the show's title are no number: unnumbered files still go to Extras.
            Assert(Season("30.Rock.S01.1080p", "30 Rock - Pilot.mkv", "30 Rock - The Aftermath.mkv") == "30 Rock/Extras/30 Rock - Pilot.mkv | 30 Rock/Extras/30 Rock - The Aftermath.mkv", "« 30 Rock - Title »");
            // A season's subtitles in a folder per episode, as RARBG put them: the number of « 2_English » is the stream's, not
            // the episode's (they were never placed before).
            const string Web = "Show.Name.S01.1080p.WEB.H264-GRP";
            Assert(Season(Web, "Show.Name.S01E01.1080p.WEB.H264-GRP.mkv", "Show.Name.S01E02.1080p.WEB.H264-GRP.mkv", "Subs/Show.Name.S01E01.1080p.WEB.H264-GRP/2_English.srt",
                    "Subs/Show.Name.S01E01.1080p.WEB.H264-GRP/3_French.srt", "Subs/Show.Name.S01E02.1080p.WEB.H264-GRP/2_English.srt")
                == "Show Name/Season 01/Show Name - S01E01.mkv | Show Name/Season 01/Show Name - S01E02.mkv | Show Name/Season 01/Show Name - S01E01.en.srt"
                + " | Show Name/Season 01/Show Name - S01E01.fr.srt | Show Name/Season 01/Show Name - S01E02.en.srt", "Subtitles in a folder per episode");
            return Task.CompletedTask;
        });
        await Test("TorLink : un épisode « .5 » d'anime (récap, spécial) rangé dans les Extras de sa série, jamais en film ni à la place d'un épisode", () =>
        {
            // « Show - 07.5 » went to Films as a film « Show - 07.5 »; « Show S4 - 12.5 » too; « Show S3 - Episode 12.5 » and
            // « Show - Ep 12.5 » took the place of episode 12 (the real one, coming later, was never imported: the importer
            // never overwrites). Jellyfin 12.1 lists the videos of a series' Extras folder with the series.
            var dir = Path.Combine(testRoot, "torlink-halves");
            var libraries = new MediaLibraries(Path.Combine(dir, "FILMS"), Path.Combine(dir, "SERIES"), Path.Combine(dir, "ANIMES"));
            SourceFile File(string path, long size) => new(Path.Combine(dir, "downloads", path), path, size);
            string Targets(ImportPlan plan) => string.Join(" | ", plan.Operations.Select(x => Path.GetRelativePath(plan.LibraryRoot!, x.Destination).Replace('\\', '/')));
            ImportPlan One(string name, string source = "nyaa") => MediaPlanner.Plan(name, source, [File(name, 1_400_000_000)], libraries);
            var recap = One("[SubsPlease] Solo Leveling - 07.5 (1080p) [9F8A2A07].mkv");
            Assert(recap.Kind == MediaKind.Anime && recap.Title == "Solo Leveling · 07.5" && Targets(recap) == "Solo Leveling/Extras/Solo Leveling - 07.5.mkv"
                && recap.Notes.SequenceEqual(["1 épisode « .5 » (récap, spécial) placé dans Extras"]), $"« - 07.5 »: {recap.Kind} {recap.Title} {Targets(recap)} {string.Join(", ", recap.Notes)}");
            foreach (var (name, expected) in new[] {
                ("[Erai-raws] Osomatsu-san S4 - 12.5 [1080p][Multiple Subtitle].mkv", "Osomatsu-san/Extras/Osomatsu-san - S04 12.5.mkv"),
                ("[ANi] Dr. Stone Season 2 - 12.5 [1080P][Baha][WEB-DL][AAC AVC][CHT].mp4", "Dr. Stone/Extras/Dr. Stone - S02 12.5.mp4"),
                ("[Doki] Dog Days S3 - Episode 12.5 OAD (1280x720 Hi10P AAC) [ABCDEF12].mkv", "Dog Days/Extras/Dog Days - S03 12.5.mkv"),
                ("[Group] Show Name - Ep 12.5 [1080p].mkv", "Show Name/Extras/Show Name - 12.5.mkv"),
                ("[Group] Show Name - 12.5v2 [1080p].mkv", "Show Name/Extras/Show Name - 12.5.mkv") })
                Assert(Targets(One(name)) == expected, $"« {name} »: {Targets(One(name))}");
            // A batch: the « .5 » next to the episodes, each with its subtitles; in a season pack, the pack's season.
            const string Batch = "[Group] Show Name (01-13) [Batch]";
            var batch = MediaPlanner.Plan(Batch, "nyaa", [File($"{Batch}/[Group] Show Name - 12 [1080p].mkv", 700_000_000), File($"{Batch}/[Group] Show Name - 12.5 [1080p].mkv", 700_000_000),
                File($"{Batch}/[Group] Show Name - 12 [1080p].ass", 50_000), File($"{Batch}/[Group] Show Name - 12.5 [1080p].ass", 50_000)], libraries);
            Assert(Targets(batch) == "Show Name/Season 01/Show Name - S01E12.mkv | Show Name/Extras/Show Name - 12.5.mkv | Show Name/Season 01/Show Name - S01E12.ass | Show Name/Extras/Show Name - 12.5.ass", "Batch: " + Targets(batch));
            const string Pack = "Show Name S02 1080p WEB-DL";
            var pack = MediaPlanner.Plan(Pack, "x1337-tv", [File($"{Pack}/Show Name - 12.mkv", 700_000_000), File($"{Pack}/Show Name - 12.5.mkv", 700_000_000)], libraries);
            Assert(Targets(pack) == "Show Name/Season 02/Show Name - S02E12.mkv | Show Name/Extras/Show Name - S02 12.5.mkv", "Season pack: " + Targets(pack));
            const string Folder = "[SubsPlease] Solo Leveling - 07.5 (1080p) [9F8A2A07]";
            var withSubtitle = MediaPlanner.Plan(Folder, "nyaa", [File($"{Folder}/{Folder}.mkv", 1_400_000_000), File($"{Folder}/{Folder}.en.srt", 50_000)], libraries);
            Assert(Targets(withSubtitle) == "Solo Leveling/Extras/Solo Leveling - 07.5.mkv | Solo Leveling/Extras/Solo Leveling - 07.5.en.srt", "Subtitle: " + Targets(withSubtitle));
            // A « 2.5 » in a title is no episode.
            Assert(Targets(One("2.5 Dimensional Seduction S01E24 VOSTFR 1080p WEB x264 AAC -Tsundere-Raws (ADN) (2.5-jigen no Ririsa).mkv")) == "2.5 Dimensional Seduction/Season 01/2.5 Dimensional Seduction - S01E24.mkv"
                && One("[Group] Kingdom Hearts 2.5 HD ReMIX [1080p].mkv").Kind == MediaKind.Movie && One("[Group] Evangelion 2.22 You Can (Not) Advance (2009) [1080p].mkv").Kind == MediaKind.Movie, "Titles with « 2.5 »");
            return Task.CompletedTask;
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
        await Test("TorLink : rangement automatique, dossiers choisis puis trouvés, tour redemandé et messages", async () =>
        {
            var dir = Path.Combine(testRoot, "torlink-placement"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var downloads = Path.Combine(dir, "downloads"); var films = Path.Combine(dir, "FILMS");
            foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, films }) Directory.CreateDirectory(folder);
            File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
            using var torlink = new TorLinkPlacement(state, new TorLinkImporter(Path.Combine(dir, "profile")));
            var id = new string('d', 40); var at = torlink.Importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { new { id, name = "Placed.Film.2004.mkv", source = "yts", sizeBytes = 10, dir = downloads, completedAt = at } }));
            File.WriteAllBytes(Path.Combine(downloads, "Placed.Film.2004.mkv"), new byte[10]);
            var rounds = 0;
            await torlink.PlaceAsync(() => (true, ImportMode.KeepSeeding), () => { rounds++; return Task.CompletedTask; }, CancellationToken.None);
            Assert(rounds == 0 && torlink.Importer.Entries.Count == 0, "Placed before Jellyfin's folders were looked for");
            await torlink.DetectLibrariesAsync(null, CancellationToken.None);
            Assert(torlink.Ready && torlink.Choose("", " ", "") == false && torlink.Summary(true).StartsWith("Choisis", StringComparison.Ordinal), "No folder found nor chosen");
            Assert(torlink.Choose(" " + films + " ", "", "") && torlink.Libraries == new MediaLibraries(films, null, null) && !torlink.Choose(films, "", ""), "Chosen folder, trimmed, changes once");
            Assert(torlink.Summary(true) == "Rangement automatique. Dossier Séries à choisir dans Réglages → Téléchargements." && torlink.Summary(false).StartsWith("Rangement automatique désactivé", StringComparison.Ordinal), "Summary line");
            Assert(torlink.PlacementDue(), "A new history is not due");
            await torlink.PlaceAsync(() => (true, ImportMode.KeepSeeding), async () => { if (rounds++ == 0) await torlink.PlaceAsync(() => (true, ImportMode.KeepSeeding), () => Task.CompletedTask, CancellationToken.None); }, CancellationToken.None);
            Assert(rounds == 2, "A round asked for during another one runs after it, not at the same time: " + rounds);
            var entry = torlink.Importer.Find(id);
            Assert(entry is { State: TorLinkImportState.Imported } && File.Exists(Path.Combine(films, "Placed Film (2004)", "Placed Film (2004).mkv")) && !torlink.PlacementDue(), "The download was not placed");
            Assert(torlink.FirstOutcome(entry!) && !torlink.FirstOutcome(entry!) && !torlink.FirstOutcome(entry! with { State = TorLinkImportState.Waiting }), "One message per outcome");
            Assert(TorLinkPlacement.Outcome(entry!) == "« Placed Film (2004) » rejoint Films. Jellyfin l’ajoute à la bibliothèque." && TorLinkPlacement.DetailOf(entry!).StartsWith("Films · rangé", StringComparison.Ordinal), "Outcome: " + TorLinkPlacement.Outcome(entry!));
            Assert(TorLinkPlacement.Outcome(entry! with { JellyfinId = "j" }) == "« Placed Film (2004) » est disponible dans ta bibliothèque." && TorLinkPlacement.Outcome(entry! with { State = TorLinkImportState.Ignored }) is null, "Available or ignored");
        });
        await Test("TorLink : un épisode au nom japonais rejoint la série rangée sous son nom anglais", async () =>
        {
            Assert(ReleaseName.AlternateTitles("[ToonsHub] JoJos Bizarre Adventure S06E04 1080p NF WEB-DL DUAL AAC2.0 H.264 (JoJo no Kimyou na Bouken: Steel Ball Run - 2nd - 3rd STAGE, Dual-Audio, Multi-Subs)") is ["JoJo no Kimyou na Bouken: Steel Ball Run"]
                && ReleaseName.AlternateTitles("Dune (2021) [1080p] [BluRay]").Count == 0 && ReleaseName.AlternateTitles("[SubsPlease] Frieren - 05 (1080p) [ABCD1234].mkv").Count == 0, "Other names of a release");
            var dir = Path.Combine(testRoot, "torlink-aliases"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var downloads = Path.Combine(dir, "downloads"); var anime = Path.Combine(dir, "ANIME"); var show = Path.Combine(anime, "JoJo's Bizarre Adventure (2012)");
            foreach (var folder in new[] { state.ConfigDirectory, state.DataDirectory, downloads, Path.Combine(show, "Season 05") }) Directory.CreateDirectory(folder);
            File.WriteAllText(state.ConfigFile, JsonSerializer.Serialize(new { downloadDir = downloads }));
            var importer = new TorLinkImporter(Path.Combine(dir, "profile"));
            var at = importer.Baseline.AddSeconds(5).ToUnixTimeMilliseconds();
            // Real names, without the colon Windows refuses in a file name.
            const string English = "[ToonsHub] JoJos Bizarre Adventure S06E04 1080p NF WEB-DL DUAL AAC2.0 H.264 (JoJo no Kimyou na Bouken Steel Ball Run - 2nd - 3rd STAGE, Dual-Audio, Multi-Subs).mkv";
            const string Romaji = "[Erai-raws] JoJo no Kimyou na Bouken Steel Ball Run - 05 [1080p NF WEB-DL AVC AAC][MultiSub][9867D87D].mkv";
            object Item(char id, string name, long completed) => new { id = new string(id, 40), name, source = "nyaa", sizeBytes = 10, dir = downloads, completedAt = completed };
            var libraries = new MediaLibraries(null, null, anime);
            File.WriteAllBytes(Path.Combine(downloads, English), new byte[10]);
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { Item('e', English, at) }));
            await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
            Assert(File.Exists(Path.Combine(show, "Season 06", "JoJo's Bizarre Adventure (2012) - S06E04.mkv")), "The English release did not join the show: " + string.Join(", ", importer.Entries.SelectMany(x => x.Files).Select(x => x.Destination)));
            File.WriteAllBytes(Path.Combine(downloads, Romaji), new byte[10]);
            File.WriteAllText(state.HistoryFile, JsonSerializer.Serialize(new[] { Item('e', English, at), Item('f', Romaji, at + 1000) }));
            await importer.ProcessAsync(state, libraries, ImportMode.KeepSeeding, automatic: true);
            Assert(File.Exists(Path.Combine(show, "Season 06", "JoJo's Bizarre Adventure (2012) - S06E05.mkv")) && Directory.GetDirectories(anime).Length == 1,
                "The romaji release made its own show: " + string.Join(", ", Directory.GetFileSystemEntries(anime, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(anime, x))));

            // The same name placed in two folders: the one with the most episodes under it wins, whatever came last.
            var stray = Path.Combine(anime, "JoJo no Kimyou na Bouken Steel Ball Run", "Season 01"); Directory.CreateDirectory(stray);
            TorLinkImportEntry Placed(char id, string name, string destination, int minutes) => new()
            {
                Id = new string(id, 40), Name = name, Kind = MediaKind.Anime, LibraryRoot = anime, CompletedAt = DateTimeOffset.UnixEpoch.AddMinutes(minutes),
                Files = [new TorLinkImportedFile(name, destination, "video")]
            };
            var known = MediaPlanner.KnownShows([
                Placed('1', English.Replace("S06E04", "S06E03"), Path.Combine(show, "Season 06", "a.mkv"), 1), Placed('2', English, Path.Combine(show, "Season 06", "b.mkv"), 2),
                Placed('3', Romaji, Path.Combine(stray, "c.mkv"), 3)]);
            Assert(known.Single(x => x.Key == ReleaseName.Key("JoJo no Kimyou na Bouken Steel Ball Run")) is { Folder: "JoJo's Bizarre Adventure (2012)", Season: 6 }, "The stray folder won: " + string.Join(" | ", known));
        });
        await Test("TorLink : une plateforme entre parenthèses (« (WKN) ») n’est pas un autre nom de série", () =>
        {
            // Real names of nyaa (Tsundere-Raws). Their batches gave Wakanim in parentheses: taken for another name of the
            // show, the second batch of the platform joined the first one's folder, L'Attaque des Titans S4 placed as
            // « Kemono Jihen - S04E01 ».
            var dir = Path.Combine(testRoot, "torlink-platform"); var anime = Path.Combine(dir, "ANIME"); var libraries = new MediaLibraries(null, null, anime);
            Directory.CreateDirectory(anime);
            var journal = new List<TorLinkImportEntry>();
            ImportPlan Batch(string torrent, string show, int count)
            {
                var files = Enumerable.Range(1, count).Select(i => $"{torrent}/[Tsundere-Raws] {show} - {i:00} VOSTFR [WEBRIP 1080p].mp4").Select(x => new SourceFile(Path.Combine(dir, "downloads", x), x, 700_000_000)).ToList();
                var plan = MediaPlanner.Plan(torrent, "nyaa", files, libraries, null, MediaPlanner.KnownShows(journal));
                foreach (var folder in plan.Operations.Select(x => Path.GetDirectoryName(x.Destination)!).Distinct()) Directory.CreateDirectory(folder);
                journal.Add(new() { Id = new string((char)('a' + journal.Count), 40), Name = torrent, Kind = plan.Kind, LibraryRoot = plan.LibraryRoot, Files = plan.Operations.Select(x => new TorLinkImportedFile(x.RelativePath, x.Destination, x.Role)).ToList() });
                return plan;
            }
            string Placed(ImportPlan plan) => Path.GetRelativePath(anime, plan.Operations[0].Destination).Replace('\\', '/');
            Assert(Placed(Batch("[Tsundere-Raws] Kemono Jihen - BATCH VOSTFR (WKN) [1080p]", "Kemono Jihen", 12)) == "Kemono Jihen/Season 01/Kemono Jihen - S01E01.mp4", "First batch");
            var titans = Batch("[Tsundere-Raws] Shingeki no Kyojin S4 - BATCH VOSTFR (WKN) [1080p]", "Shingeki no Kyojin S4", 16);
            Assert(Placed(titans) == "Shingeki no Kyojin/Season 04/Shingeki no Kyojin - S04E01.mp4", "The second batch of the platform joined the first one's show: " + Placed(titans));

            // The other name after the tags: « (VOSTFR, …) », « (READNFO, VOSTFR, …) », never the tag itself.
            foreach (var (name, other) in new[]
            {
                ("Battle Spirits Re ZEKKAI NO KU S01E06 SUBFRENCH 1080p CR WEB-DL AAC2.0 x264-Tsundere-Raws (VOSTFR, Battle Spirits Re Zekkai no Kuu)", "Battle Spirits Re Zekkai no Kuu"),
                ("A Returners Magic Should Be Special S02E06 REPACK SUBFRENCH 1080p CR WEB-DL AAC2.0 x264-Tsundere-Raws (READNFO, VOSTFR, Kikansha no Mahou wa Tokubetsu desu 2nd Season, Gwihwanja ui Mabeop eun Teukbyeol haeya Hamnida)", "Kikansha no Mahou wa Tokubetsu desu"),
                ("Magic Knight Rayearth (2026) S01E03 MULTi AD 1080p CR WEB-DL AAC2.0 x264-Tsundere-Raws (VF, FRENCH, SUBFRENCH, VOSTFR, Mahou Kishi Rayearth)", "Mahou Kishi Rayearth"),
                ("Reincarnated as a Sword S02E07 SUBFRENCH 1080p ADN WEB-DL AAC2.0 x264-Tsundere-Raws (VOSTFR, Multi Subs, Tensei shitara Ken deshita II, TenKen 2, Reincarnated as a Sword Season 2)", "Tensei shitara Ken deshita II"),
                ("[ToonsHub] JoJos Bizarre Adventure S06E05 1080p NF WEB-DL DUAL AAC2.0 H.264 (JoJo no Kimyou na Bouken: Steel Ball Run, Dual-Audio, Multi-Subs)", "JoJo no Kimyou na Bouken: Steel Ball Run"),
            })
                Assert(ReleaseName.AlternateTitles(name) is [var title] && title == other && ReleaseRanking.Parse(name).Alternate == other, $"{name} → {string.Join(" ; ", ReleaseName.AlternateTitles(name))} / {ReleaseRanking.Parse(name).Alternate}");
            foreach (var name in new[] { "[Tsundere-Raws] Kemono Jihen - BATCH VOSTFR (WKN) [1080p]", "[Group] Show (ADN) [1080p]", "[Group] Show (VOSTFR) [1080p]", "[Group] Show (MULTi) [1080p]", "[Group] Show (Season 2) [1080p]", "[Group] Show (Part 2) [1080p]", "[Group] Show (Dual Audio, 1080p, English Sub)" })
                Assert(ReleaseName.AlternateTitles(name).Count == 0 && ReleaseRanking.Parse(name).Alternate is null, $"{name} → {string.Join(" ; ", ReleaseName.AlternateTitles(name))} / {ReleaseRanking.Parse(name).Alternate}");
            return Task.CompletedTask;
        });
        await Test("TorLink : un mot de tag dans un titre (« Charlottes Web », « The Bad Batch », « A Complete Unknown ») reste dans le titre", () =>
        {
            // « Web » and « Opus » ended a film's title, « Batch » and « Complete » a show's: « Charlottes (2006) », every episode
            // of « Star Wars The Bad Batch » in a folder « Star Wars The Bad », « A Complete Unknown » placed as a series « A ».
            foreach (var (name, title, year) in new (string, string, int?)[]
            {
                ("Charlottes.Web.2006.1080p.BluRay.x264-HANDJOB", "Charlottes Web", 2006), ("Mr.Hollands.Opus.1995.1080p.BluRay.x264-SiNNERS", "Mr Hollands Opus", 1995),
                ("Unfriended.Dark.Web.2018.1080p.BluRay.x264-DRONES", "Unfriended Dark Web", 2018), ("Old.Film.WEB.x264-GRP", "Old Film", null), ("Old.Film.Opus.x264-GRP", "Old Film", null),
            })
                Assert(ReleaseName.Movie(name) == (title, year), $"{name} → {ReleaseName.Movie(name)}");
            const string Episode = "Star.Wars.The.Bad.Batch.S03E15.1080p.WEB.h264-ETHEL";
            Assert(ReleaseName.Show(Episode) == ("Star Wars The Bad Batch", null) && !ReleaseName.IsCompletePack(Episode), "The Bad Batch: " + ReleaseName.Show(Episode));
            Assert(ReleaseName.Show("Star Wars The Bad Batch Complete Series 1080p") == ("Star Wars The Bad Batch", null), "The Bad Batch, every episode");
            // Every episode, still: « Complete » or « Batch » before nothing, a tag, a language, « Series » or years.
            foreach (var name in new[] { "The.Office.US.Complete.Series.1080p.BluRay.x264", "Breaking.Bad.Complete.1080p.BluRay.x264", "Kaamelott.Integrale.FRENCH.1080p.BluRay.x264", "Kaamelott.Integrale.2005-2009.1080p",
                "[Tsundere-Raws] Kemono Jihen - BATCH VOSTFR (WKN) [1080p]", "Show.Complete-GRP", "Show Complete" })
                Assert(ReleaseName.IsCompletePack(name), "Not every episode: " + name);
            Assert(ReleaseName.Show("Kaamelott.Integrale.2005-2009.1080p") == ("Kaamelott", null) && !ReleaseName.IsCompletePack("A.Complete.Unknown.2024.1080p.BluRay.x264-GRP"), "Kaamelott, A Complete Unknown");
            // Every film too: « Complete » or « Intégrale » before « Movie », « Films », « Trilogy », « 8-Film », « 1-4 » or « Livres ».
            foreach (var (name, title) in new[]
            {
                ("Harry.Potter.Complete.Movie.Collection.1080p.BluRay.x264-GRP", "Harry Potter"), ("Harry Potter Complete 8-Film Collection (2001-2011) 1080p BluRay", "Harry Potter"),
                ("The.Godfather.Complete.Trilogy.1080p.BluRay.x264-GRP", "The Godfather"), ("Toy Story Complete Movies 1-4 1080p", "Toy Story"), ("Scream Complete 1-5 1080p", "Scream"),
                ("Mad Max Complete Anthology 1080p", "Mad Max"), ("Kaamelott.Integrale.Livres.I.a.VI.FRENCH.1080p.BluRay.x264-GRP", "Kaamelott"),
                ("[AnimeRG] Broken Blade Complete Movie Series [DUAL-AUDIO] [1080p] [HEVC] [x265] [pseudo]", "Broken Blade"),
            })
                Assert(ReleaseName.IsCompletePack(name) && ReleaseName.Show(name).Title == title && ReleaseRanking.Parse(name) is { Pack: true } read && read.Title == title,
                    $"{name} → {ReleaseName.Show(name)}, {ReleaseRanking.Parse(name).Title}, pack {ReleaseRanking.Parse(name).Pack}");
            var dir = Path.Combine(testRoot, "torlink-title-words"); var films = Path.Combine(dir, "FILMS"); var series = Path.Combine(dir, "SERIES");
            var libraries = new MediaLibraries(films, series, null);
            SourceFile File(string path, long size) => new(Path.Combine(dir, "downloads", path), path, size);
            const string Unknown = "A.Complete.Unknown.2024.1080p.BluRay.x264-GRP";
            var film = MediaPlanner.Plan(Unknown, "x1337", [File($"{Unknown}/{Unknown}.mkv", 10_000_000_000), File($"{Unknown}/A.Complete.Unknown.2024.Featurette.mkv", 600_000_000)], libraries);
            Assert(film.Kind == MediaKind.Movie && film.Operations[0].Destination == Path.Combine(films, "A Complete Unknown (2024)", "A Complete Unknown (2024).mkv"),
                $"A Complete Unknown: {film.Kind} " + string.Join(" | ", film.Operations.Select(x => x.Destination)));
            var batch = MediaPlanner.Plan(Episode, "x1337-tv", [File($"{Episode}.mkv", 2_000_000_000)], libraries);
            Assert(batch.Operations.Single().Destination == Path.Combine(series, "Star Wars The Bad Batch", "Season 03", "Star Wars The Bad Batch - S03E15.mkv"), "The Bad Batch: " + batch.Operations.Single().Destination);
            // A pack of films numbered as episodes keeps its title without « Complete Movie Series » (AnimeRG on nyaa).
            const string Blades = "[AnimeRG] Broken Blade Complete Movie Series [DUAL-AUDIO] [1080p] [HEVC] [x265] [pseudo]";
            var blades = MediaPlanner.Plan(Blades, "x1337-tv", [File($"{Blades}/[AnimeRG] Broken Blade - 01 [1080p] [Dual-Audio] [HEVC] [x265] [pseudo].mkv", 600_000_000),
                File($"{Blades}/[AnimeRG] Broken Blade - 02 [1080p] [Dual-Audio] [HEVC] [x265] [pseudo].mkv", 600_000_000)], libraries);
            Assert(blades.Operations.Select(x => x.Destination).SequenceEqual([Path.Combine(series, "Broken Blade", "Season 01", "Broken Blade - S01E01.mkv"), Path.Combine(series, "Broken Blade", "Season 01", "Broken Blade - S01E02.mkv")]),
                "Broken Blade: " + string.Join(" | ", blades.Operations.Select(x => x.Destination)));
            return Task.CompletedTask;
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
        await Test("TorLink et qBittorrent : disque monté dans le dossier des bibliothèques (point de montage), téléchargements déplacés dessus", () =>
        {
            var dir = Path.Combine(testRoot, "mount-point"); var media = Path.Combine(dir, "MEDIAS");
            var films = Path.Combine(media, "FILMS"); var series = Path.Combine(media, "SERIES"); var anime = Path.Combine(media, "ANIME");
            // C: holds everything but MEDIAS, a drive of its own mounted there (Path.GetPathRoot sees one drive for both).
            string? VolumeOf(string path) => Path.GetFullPath(path) is var full && (full == media || full.StartsWith(media + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                ? media + Path.DirectorySeparatorChar : dir + Path.DirectorySeparatorChar;
            var libraries = new MediaLibraries(films, series, anime);
            var oldDownloads = Path.Combine(dir, "Downloads", "torlink"); var beside = Path.Combine(media, "Téléchargements TorLink");
            Assert(TorLinkState.DownloadFolderBeside(libraries, oldDownloads, VolumeOf) == beside, "Mount point taken for the same drive");
            Assert(TorLinkState.DownloadFolderBeside(libraries, beside, VolumeOf) is null, "Already on the library's volume");
            Assert(TorLinkState.DownloadFolderBeside(new(media, null, null), oldDownloads, VolumeOf) is null, "A library at the root of the mounted drive leaves no room");
            Assert(TorLinkState.DownloadFolderBeside(new(films, Path.Combine(dir, "Séries"), null), oldDownloads, VolumeOf) is null, "Library folders on two volumes");

            var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data"));
            var mine = Path.Combine(dir, "Videos", "Téléchargements TorLink");
            Directory.CreateDirectory(state.ConfigDirectory); Directory.CreateDirectory(state.DataDirectory);
            File.WriteAllText(state.ConfigFile, new JsonObject { ["downloadDir"] = mine, ["trackers"] = new JsonArray("udp://tracker") }.ToJsonString());
            File.WriteAllText(state.QueueFile, """[{"id":"aa","status":"completed"},{"id":"bb","status":"paused"}]""");
            File.WriteAllText(state.SeedsFile, """[{"id":"aa","status":"paused"}]""");
            Assert(!state.MoveDownloadDirectory(beside) && state.DownloadDirectory() == mine, "Moved with a paused download in the queue");
            File.WriteAllText(state.QueueFile, """[{"id":"aa","status":"completed"}]""");
            File.WriteAllText(state.SeedsFile, """[{"id":"aa","status":"seeding"}]""");
            Assert(!state.MoveDownloadDirectory(beside) && state.DownloadDirectory() == mine, "Moved while TorLink shares from the old folder");
            File.WriteAllText(state.SeedsFile, """[{"id":"aa","status":"paused"}]""");
            Assert(state.MoveDownloadDirectory(beside) && state.DownloadDirectory() == Path.GetFullPath(beside) && Directory.Exists(beside), "Not moved once idle");
            Assert(JsonNode.Parse(File.ReadAllText(state.ConfigFile))!["trackers"]![0]!.GetValue<string>() == "udp://tracker", "The rest of TorLink's config was lost");
            Assert(TorLinkState.DownloadFolderBeside(libraries, state.DownloadDirectory(), VolumeOf) is null, "Moved again");
            File.WriteAllText(state.ConfigFile, new JsonObject { ["downloadDir"] = Path.Combine(dir, "Mes torrents") }.ToJsonString());
            Assert(!state.MoveDownloadDirectory(beside), "A folder the person chose was moved");
            Assert(TorLinkState.MiraMayMove(TorLinkState.DefaultDownloadDirectory) && TorLinkState.MiraMayMove(Path.Combine(dir, "TorLink downloads")), "TorLink's default or Mira's folder");

            var qb = Path.Combine(media, "Téléchargements qBittorrent");
            Assert(Mira.Core.TorLink.QBittorrentHistory.DownloadFolder(libraries, VolumeOf) == qb, "qBittorrent: beside the library");
            Assert(Mira.Core.TorLink.QBittorrentHistory.FolderToUse("", libraries, VolumeOf) == qb, "qBittorrent: first choice");
            Assert(Mira.Core.TorLink.QBittorrentHistory.FolderToUse(Path.Combine(dir, "Videos", "Téléchargements qBittorrent"), libraries, VolumeOf) == qb, "qBittorrent: kept on the system drive");
            Assert(Mira.Core.TorLink.QBittorrentHistory.FolderToUse(Path.Combine(media, "Autre"), libraries, VolumeOf) == Path.Combine(media, "Autre"), "qBittorrent: a folder on the library's volume was changed");
            // FILMS itself the mount point: its parent is on C:, the folder goes at the root of FILMS's volume, which is the library: nothing better.
            string? FilmsMounted(string path) => Path.GetFullPath(path) is var full && (full == films || full.StartsWith(films + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                ? films + Path.DirectorySeparatorChar : dir + Path.DirectorySeparatorChar;
            Assert(Mira.Core.TorLink.QBittorrentHistory.FolderToUse(Path.Combine(dir, "x"), new(films, null, null), FilmsMounted) == Path.Combine(dir, "x"), "qBittorrent: moved into the library");
            return Task.CompletedTask;
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
    }
}
