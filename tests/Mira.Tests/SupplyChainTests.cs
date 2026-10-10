using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;

/// <summary>
/// What Mira downloads and installs beside itself: bounded .torrent reading, the unpacked update checked byte for byte, a
/// folder update cut short and undone, TorLink's lock, and newer Node.js and libmpv prepared beside the ones in use. This
/// file stays free of WPF and of the window's code, so that the same checks also run outside Windows; the rest is in
/// SupplyChainTests.Desktop.cs.
/// </summary>
internal static partial class SupplyChainTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, string root)
    {
        await test("TorLink : .torrent borné (taille, nombre de valeurs), empreintes des pièces sautées sans copie", () =>
        {
            var pieces = new string('p', 2_000_000);
            var info = Bencode.ReadTorrent(Torrent("Film.2008", [(["Film.2008.mkv"], 9)], pieces), out var refused);
            Assert(info is { Name: "Film.2008", Files: [{ Length: 9 }] } && !refused, "A torrent with 2 MB of piece hashes was not read");
            Assert(Bencode.ReadTorrent(new byte[Bencode.MaxFileSize + 1], out refused) is null && refused, "A file over the size limit was parsed");
            // 600 000 empty lists in the info dictionary: small on disk, too many values to build.
            var crowded = Encoding.ASCII.GetBytes("d4:infod4:name1:a1:x" + "l" + string.Concat(Enumerable.Repeat("le", 600_000)) + "eee");
            Assert(crowded.Length < Bencode.MaxFileSize && Bencode.ReadTorrent(crowded, out refused) is null && refused, "A file over the value limit was parsed");
            Assert(Bencode.ReadTorrent(Encoding.ASCII.GetBytes("d4:infod4:name1:a6:lengthi" + new string('9', 40) + "eee"), out refused) is null && !refused, "An oversized integer was accepted or called too large");
            Assert(Bencode.ReadTorrent(Encoding.ASCII.GetBytes("d4:infod4:name1:a6:pieces99:abcee"), out refused) is null && !refused, "Pieces running past the end were accepted");
            // TorLink's own .torrent too large: not read, the download's files found by its name, and said plainly.
            var dir = Path.Combine(root, "torrent-limits"); var state = new TorLinkState(Path.Combine(dir, "config"), Path.Combine(dir, "data")); var downloads = Path.Combine(dir, "downloads");
            var id = new string('f', 40); Directory.CreateDirectory(state.TorrentsDirectory); Directory.CreateDirectory(downloads);
            using (var big = File.Create(Path.Combine(state.TorrentsDirectory, id + ".torrent"))) big.SetLength(Bencode.MaxFileSize + 1);
            File.WriteAllBytes(Path.Combine(downloads, "Film.2008.mkv"), new byte[9]);
            Assert(state.ReadTorrent(id, out refused) is null && refused, "TorLink's oversized .torrent was read");
            var files = TorLinkImporter.DownloadedFiles(state, new(id, "Film.2008.mkv", "yts", 9, downloads, DateTimeOffset.UtcNow), null, out var torrentRefused);
            Assert(torrentRefused && files is [{ RelativePath: "Film.2008.mkv" }], "The download was not found by its name after its .torrent was refused");
            Assert(TorLinkImporter.TorrentRefusedHint.Contains(".torrent") && !TorLinkImporter.TorrentRefusedHint.Contains("Bencode"), "The refusal is not said plainly");
            return Task.CompletedTask;
        });

        await test("Mises à jour : archive décompressée identique octet par octet à l’archive vérifiée, sans fichier en trop", () =>
        {
            var folder = Path.Combine(root, "update-tree"); Directory.CreateDirectory(folder);
            var zip = Path.Combine(folder, "Mira.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var (name, text) in new[] { ("Mira/Mira.exe", "exe"), ("Mira/Mira.dll", "bibliothèque"), ("Mira/sub/a.json", "{}") })
                    using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(text);
            var tree = Path.Combine(folder, "app");
            ZipFile.ExtractToDirectory(zip, tree); File.WriteAllText(Path.Combine(tree, "ready.txt"), "empreinte");
            Assert(Updater.TreeMatches(zip, tree), "An intact tree was refused");
            var dll = Path.Combine(tree, "Mira", "Mira.dll"); var original = File.ReadAllBytes(dll);
            var changed = original.ToArray(); changed[0] ^= 1; File.WriteAllBytes(dll, changed);
            Assert(!Updater.TreeMatches(zip, tree), "A file changed with the same size was accepted");
            File.WriteAllBytes(dll, original);
            File.WriteAllText(Path.Combine(tree, "Mira", "version.dll"), "ajouté");
            Assert(!Updater.TreeMatches(zip, tree), "A file added to the tree was accepted");
            File.Delete(Path.Combine(tree, "Mira", "version.dll")); File.Delete(Path.Combine(tree, "Mira", "sub", "a.json"));
            Assert(!Updater.TreeMatches(zip, tree), "A missing file was accepted");
            return Task.CompletedTask;
        });

        await test("Mises à jour : échange de dossier coupé net (courant), rétabli au démarrage suivant", () =>
        {
            var folder = Path.Combine(root, "update-cut"); var app = Path.Combine(folder, "app"); var updates = Path.Combine(app, "data", "updates");
            var rollback = Path.Combine(updates, "rollback-20261010120000");
            Directory.CreateDirectory(Path.Combine(app, "sub")); Directory.CreateDirectory(Path.Combine(rollback, "sub"));
            void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
            // Where the swap stood when the power went: a.dll swapped, b.dll moved out but not replaced, c.dll not reached,
            // d.dll added, e.dll still waiting beside its destination.
            Write(Path.Combine(app, "a.dll"), "nouvelle a"); Write(Path.Combine(rollback, "a.dll"), "ancienne a");
            Write(Path.Combine(rollback, "sub", "b.dll"), "ancienne b"); Write(Path.Combine(app, "sub", "b.dll" + UpdateApplier.NewSuffix), "nouvelle b");
            Write(Path.Combine(app, "c.dll"), "ancienne c"); Write(Path.Combine(app, "c.dll" + UpdateApplier.NewSuffix), "nouvelle c");
            Write(Path.Combine(app, "d.dll"), "ajoutée d"); Write(Path.Combine(app, "e.dll" + UpdateApplier.NewSuffix), "ajoutée e");
            Write(Path.Combine(app, "data", "settings.json"), "mes réglages");
            var journal = new UpdateApplier.SwapJournal(app, ["a.dll", Path.Combine("sub", "b.dll"), "c.dll", Path.Combine("..", "..", "outside.dll")], ["d.dll", "e.dll"]);
            Write(Path.Combine(rollback, UpdateApplier.InProgressMarker), JsonSerializer.Serialize(journal));
            Write(Path.Combine(folder, "outside.dll"), "jamais touché");
            Assert(UpdateApplier.Interrupted(updates, app + Path.DirectorySeparatorChar) is [{ Journal.Replaced.Count: 4 }], "The journal of the interrupted swap was not found");
            Assert(UpdateApplier.Interrupted(updates, folder).Count == 0, "A journal was taken for another folder");
            Assert(UpdateApplier.Recover(updates, app), "Nothing was restored");
            string Read(params string[] parts) => File.ReadAllText(Path.Combine([app, .. parts]));
            Assert(Read("a.dll") == "ancienne a" && Read("sub", "b.dll") == "ancienne b" && Read("c.dll") == "ancienne c" && !File.Exists(Path.Combine(app, "d.dll")),
                "The previous version did not come back whole");
            Assert(!Directory.EnumerateFiles(app, "*" + UpdateApplier.NewSuffix, SearchOption.AllDirectories).Any() && Read("data", "settings.json") == "mes réglages"
                && File.ReadAllText(Path.Combine(folder, "outside.dll")) == "jamais touché", "New files stayed, data was touched, or a path outside the folder was followed");
            Assert(!File.Exists(Path.Combine(rollback, UpdateApplier.InProgressMarker)) && UpdateApplier.Interrupted(updates, app).Count == 0 && !UpdateApplier.Recover(updates, app),
                "The journal outlived a complete restore");
            // A finished swap leaves no journal; one that failed and came back in full neither.
            var incoming = Path.Combine(folder, "incoming"); Write(Path.Combine(incoming, "a.dll"), "plus récente a"); Write(Path.Combine(incoming, "f.dll"), "ajoutée f");
            UpdateApplier.CopyFolder(incoming, app, Path.Combine(updates, "rollback-ok"));
            Assert(Read("a.dll") == "plus récente a" && Read("f.dll") == "ajoutée f" && !Directory.Exists(Path.Combine(updates, "rollback-ok")), "A complete swap left its rollback or journal");
            if (OperatingSystem.IsWindows())
            {
                // Windows only: an open file cannot be renamed there. Held during the restore, it keeps the journal and the
                // rollback copy for the next start, which finishes the restore.
                Write(Path.Combine(app, "g.dll"), "nouvelle g"); Write(Path.Combine(updates, "rollback-held", "g.dll"), "ancienne g");
                Write(Path.Combine(updates, "rollback-held", UpdateApplier.InProgressMarker), JsonSerializer.Serialize(new UpdateApplier.SwapJournal(app, ["g.dll"], [])));
                using (new FileStream(Path.Combine(app, "g.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                    UpdateApplier.Recover(updates, app, attempts: 2);
                Assert(UpdateApplier.Interrupted(updates, app).Count == 1 && File.Exists(Path.Combine(updates, "rollback-held", UpdateApplier.IncompleteMarker)), "A restore that could not finish lost its journal");
                Assert(UpdateApplier.Recover(updates, app) && Read("g.dll") == "ancienne g" && UpdateApplier.Interrupted(updates, app).Count == 0, "The next start did not finish the restore");
                var held = Path.Combine(app, "h.dll"); Write(held, "ancienne h"); Write(Path.Combine(incoming, "h.dll"), "nouvelle h");
                using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var failed = false; try { UpdateApplier.CopyFolder(incoming, app, Path.Combine(updates, "rollback-fail"), attempts: 2); } catch (IOException) { failed = true; }
                    Assert(failed && UpdateApplier.Interrupted(updates, app).Count == 0, "A swap that failed and came back in full kept its journal");
                }
            }
            // The next start keeps a rollback copy whose swap is still to undo, and removes the others.
            var profile = Path.Combine(folder, "profile"); var staging = Path.Combine(profile, "updates");
            Write(Path.Combine(staging, "rollback-needed", UpdateApplier.InProgressMarker), "{}"); Write(Path.Combine(staging, "rollback-done", "a.dll"), "x");
            using var updater = Updater.Create(["--data", profile, "--update-feed", "http://127.0.0.1:9/"], profile, validation: true) ?? throw new Exception("No updater for a loopback feed");
            updater.CleanStaging();
            Assert(Directory.Exists(Path.Combine(staging, "rollback-needed")) && !Directory.Exists(Path.Combine(staging, "rollback-done")), "A rollback copy still needed was removed, or a finished one kept");
            return Task.CompletedTask;
        });

        await test("TorLink : dépendances figées par le package-lock.json relu, installées par npm ci sans scripts", () =>
        {
            using var lockFile = JsonDocument.Parse(TorLinkPackage.Lock); using var manifest = JsonDocument.Parse(TorLinkPackage.Manifest);
            var version = TorLinkPackage.Current.Version;
            var packages = lockFile.RootElement.GetProperty("packages");
            Assert(lockFile.RootElement.GetProperty("lockfileVersion").GetInt32() == 3 && manifest.RootElement.GetProperty("dependencies").GetProperty(TorLinkPackage.Name).GetString() == version
                && packages.GetProperty("").GetProperty("dependencies").GetProperty(TorLinkPackage.Name).GetString() == version
                && packages.GetProperty("node_modules/" + TorLinkPackage.Name).GetProperty("version").GetString() == version, "The lock does not pin TorLink " + version);
            var count = 0;
            foreach (var entry in packages.EnumerateObject().Where(x => x.Name.Length > 0))
            {
                count++;
                Assert(entry.Value.TryGetProperty("integrity", out var integrity) && integrity.GetString()!.StartsWith("sha512-", StringComparison.Ordinal)
                    && entry.Value.TryGetProperty("resolved", out var resolved) && resolved.GetString()!.StartsWith("https://registry.npmjs.org/", StringComparison.Ordinal)
                    && !entry.Value.TryGetProperty("link", out _), entry.Name + " is not pinned to an archive of registry.npmjs.org by its sha512");
            }
            Assert(count > 100, "The lock lists only " + count + " packages");
            var arguments = TorLinkPackage.NpmArguments(@"C:\Mira\data\node\node_modules\npm\bin\npm-cli.js", @"C:\Mira\data\torlink-next.partial");
            Assert(arguments[1] == "ci" && arguments.Contains("--ignore-scripts") && arguments.Contains("--omit=dev") && arguments[arguments.ToList().IndexOf("--prefix") + 1] == @"C:\Mira\data\torlink-next.partial",
                "npm arguments: " + string.Join(" ", arguments));
            // A TorLink installed by an older Mira (npm install, no lock) keeps working but is not current; one from this lock is.
            var folder = Path.Combine(root, "torlink-lock"); var entry2 = Path.Combine(TorLinkPackage.PackageIn(folder), "dist", "cli.cjs");
            Directory.CreateDirectory(Path.GetDirectoryName(entry2)!); File.WriteAllText(entry2, "");
            Assert(!TorLinkPackage.Current.IsInstalledIn(folder), "A TorLink without the lock's stamp counts as current");
            File.WriteAllText(Path.Combine(folder, TorLinkPackage.VersionFile), TorLinkPackage.Current.Stamp);
            Assert(TorLinkPackage.Current.IsInstalledIn(folder) && TorLinkPackage.Current.Stamp == $"{version} {Convert.ToHexString(SHA256.HashData(TorLinkPackage.Lock)).ToLowerInvariant()}", "The lock's stamp");
            return Task.CompletedTask;
        });

        await test("Node.js : version plus récente préparée à côté, mise en place seulement quand rien ne l’utilise", async () =>
        {
            NodePackage Package(string version, byte[] zip) => new(version, new($"https://nodejs.example.test/dist/v{version}/node-v{version}-win-x64.zip"), zip.Length, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
            byte[] Zip(string version) => NodeZip(version);
            var older = Zip("24.0.0"); var newer = Zip("24.1.0"); var served = older; var requests = 0;
            using var handler = new Handler(_ => { Interlocked.Increment(ref requests); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) }; });
            var profile = Path.Combine(root, "node-refresh");
            await NodeInstaller.InstallAsync(profile, Package("24.0.0", older), handler);
            served = newer;
            var next = await NodeInstaller.PrepareAsync(profile, Package("24.1.0", newer), handler);
            Assert(next == NodeInstaller.NextFolder(profile) && NodeInstaller.IsInstalled(profile, Package("24.0.0", older)) && File.ReadAllText(NodeInstaller.Executable(profile)) == "node 24.0.0",
                "Preparing the newer Node.js touched the one in use");
            Assert(await NodeInstaller.PrepareAsync(profile, Package("24.1.0", newer), handler) == next && requests == 2, "A prepared Node.js was downloaded again");
            if (OperatingSystem.IsWindows())
            {
                // Windows only: a folder holding an open file cannot be renamed, as when TorLink runs from it.
                using (new FileStream(NodeInstaller.Executable(profile), FileMode.Open, FileAccess.Read, FileShare.Read))
                    Assert(!NodeInstaller.ActivatePrepared(profile, Package("24.1.0", newer)) && File.ReadAllText(NodeInstaller.Executable(profile)) == "node 24.0.0" && Directory.Exists(next),
                        "Node.js was replaced while in use, or its prepared version lost");
            }
            Assert(NodeInstaller.ActivatePrepared(profile, Package("24.1.0", newer)) && File.ReadAllText(NodeInstaller.Executable(profile)) == "node 24.1.0" && !Directory.Exists(next)
                && !Directory.Exists(NodeInstaller.Folder(profile) + ".old"), "The prepared Node.js did not take its place cleanly");
            // A swap cut between its two renames: the previous folder comes back before anything else.
            Directory.Move(NodeInstaller.Folder(profile), NodeInstaller.Folder(profile) + ".old");
            Assert(PreparedFolder.Settle(NodeInstaller.Folder(profile)) && NodeInstaller.IsInstalled(profile, Package("24.1.0", newer)), "A cut swap was not settled");
        });

        await test("Moteur mpv : build plus récente préparée à côté, mise en place au démarrage suivant, jamais une plus ancienne", async () =>
        {
            var archive = Convert.FromBase64String(MpvArchive);
            MpvPackage Package(string version) => new(version, new("https://downloads.example.test/mpv/mpv-dev-test.7z"), archive.Length, MpvArchiveSha256, MpvLibrarySha256);
            var requests = 0;
            using var handler = new Handler(_ => { Interlocked.Increment(ref requests); return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }; });
            var profile = Path.Combine(root, "engine-refresh");
            await MpvInstaller.InstallAsync(profile, Package("20260101-git-ancien"), handler);
            Assert(!MpvInstaller.IsOutdated(profile, Package("20260101-git-ancien")) && MpvInstaller.IsOutdated(profile, Package("20260927-git-recent"))
                && !MpvInstaller.IsOutdated(profile, Package("20251231-git-avant")), "Older and newer builds told apart wrongly");
            Assert(await MpvInstaller.PrepareUpdateAsync(profile, Package("20260927-git-recent"), handler) && MpvInstaller.IsPrepared(profile, Package("20260927-git-recent"))
                && File.ReadAllText(Path.Combine(MpvInstaller.Folder(profile), MpvInstaller.VersionFile)) == "20260101-git-ancien", "Preparing the engine touched the one in use");
            Assert(await MpvInstaller.PrepareUpdateAsync(profile, Package("20260927-git-recent"), handler) && requests == 2, "A prepared engine was downloaded again");
            Assert(MpvInstaller.ActivatePrepared(profile, Package("20260927-git-recent")) && MpvInstaller.IsInstalled(profile, Package("20260927-git-recent"))
                && !Directory.Exists(MpvInstaller.NextFolder(profile)) && File.ReadAllText(MpvInstaller.LibraryPath(profile)).StartsWith("faux moteur", StringComparison.Ordinal), "The prepared engine did not take its place");
            Assert(!MpvInstaller.ActivatePrepared(profile, Package("20260927-git-recent")) && !await MpvInstaller.PrepareUpdateAsync(profile, Package("20260927-git-recent"), handler) && requests == 2,
                "A current engine was prepared again");
            // Without its version file, Mira's engine counts as older; no engine of Mira, nothing to update.
            File.Delete(Path.Combine(MpvInstaller.Folder(profile), MpvInstaller.VersionFile));
            Assert(MpvInstaller.IsOutdated(profile, Package("20260927-git-recent")) && !MpvInstaller.IsOutdated(Path.Combine(root, "engine-none"), Package("20260927-git-recent")), "Engine without version");
        });

        await DesktopAsync(test, root);
    }

    private static partial Task DesktopAsync(Func<string, Func<Task>, Task> test, string root);

    // The 7z archive of the engine test in Program.cs: libmpv-2.dll ("faux moteur mpv pour les tests de Mira") and a header.
    private const string MpvArchive = "N3q8ryccAAQSc70DwgAAAAAAAAAiAAAAAAAAABwWbsgBADQvKiBlbi10ZXRlICovCmZhdXggbW90ZXVyIG1wdiBwb3VyIGxlcyB0ZXN0cyBkZSBNaXJhCgAAAIEzB64P0YkKnKCQoHdexUAYjoQJNqTpTtdkwHH/AlkW0BAA/Bkz01eGX3Anuuait2gpZTXZf9jAC7J3/dXvw+tz35fkNTgXZ9k+YJs57syaM+RclR0pc/nBp46uaTmsVW1bYGi/WuClG7sn7CLBB8/0KodMIp0c0SrMXAIplkuFPaS65AAAABcGOQEJgIkABwsBAAEjAwEBBV0AEAAADIDWCgF5nvznAAA=";
    private const string MpvArchiveSha256 = "9ec0036370600d637114441debcdd6ba663a3b295264dff5240c28557c7f934d", MpvLibrarySha256 = "7306c02a18fb46f612ba938a15f9e7b8c0313c1f1e1eb67ead4565729a7403c2";

    /// <summary>A zip shaped like nodejs.org's: one top folder, node.exe (its text names the version), npm and the licence.</summary>
    private static byte[] NodeZip(string version)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in new[] { ("node.exe", "node " + version), ("LICENSE", "MIT"), ("node_modules/npm/bin/npm-cli.js", "// npm"), ("node_modules/npm/package.json", "{}") })
                using (var writer = new StreamWriter(zip.CreateEntry($"node-v{version}-win-x64/{name}").Open())) writer.Write(text);
        return memory.ToArray();
    }

    /// <summary>Synthetic .torrent metadata, its keys sorted as bencode requires.</summary>
    private static byte[] Torrent(string name, (string[] Path, long Length)[] files, string pieces)
    {
        var text = new StringBuilder("d4:infod");
        void Str(string value) => text.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        Str("files"); text.Append('l');
        foreach (var (path, length) in files) { text.Append("d6:lengthi").Append(length).Append("e4:pathl"); foreach (var part in path) Str(part); text.Append("ee"); }
        text.Append('e'); Str("name"); Str(name); Str("piece length"); text.Append("i16384e"); Str("pieces"); Str(pieces);
        return Encoding.UTF8.GetBytes(text.Append("ee").ToString());
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
