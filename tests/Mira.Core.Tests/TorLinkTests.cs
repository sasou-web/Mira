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
            Assert(torlink.Summary(true) == "Rangement automatique. Dossier Séries à choisir dans Réglages → TorLink." && torlink.Summary(false).StartsWith("Rangement automatique désactivé", StringComparison.Ordinal), "Summary line");
            Assert(torlink.PlacementDue(), "A new history is not due");
            await torlink.PlaceAsync(() => (true, ImportMode.KeepSeeding), async () => { if (rounds++ == 0) await torlink.PlaceAsync(() => (true, ImportMode.KeepSeeding), () => Task.CompletedTask, CancellationToken.None); }, CancellationToken.None);
            Assert(rounds == 2, "A round asked for during another one runs after it, not at the same time: " + rounds);
            var entry = torlink.Importer.Find(id);
            Assert(entry is { State: TorLinkImportState.Imported } && File.Exists(Path.Combine(films, "Placed Film (2004)", "Placed Film (2004).mkv")) && !torlink.PlacementDue(), "The download was not placed");
            Assert(torlink.FirstOutcome(entry!) && !torlink.FirstOutcome(entry!) && !torlink.FirstOutcome(entry! with { State = TorLinkImportState.Waiting }), "One message per outcome");
            Assert(TorLinkPlacement.Outcome(entry!) == "« Placed Film (2004) » rejoint Films. Jellyfin l’ajoute à la bibliothèque." && TorLinkPlacement.DetailOf(entry!).StartsWith("Films · rangé", StringComparison.Ordinal), "Outcome: " + TorLinkPlacement.Outcome(entry!));
            Assert(TorLinkPlacement.Outcome(entry! with { JellyfinId = "j" }) == "« Placed Film (2004) » est disponible dans ta bibliothèque." && TorLinkPlacement.Outcome(entry! with { State = TorLinkImportState.Ignored }) is null, "Available or ignored");
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
