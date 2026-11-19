using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// qBittorrent for private trackers: the .torrent Mira reads (info hash, private flag), the qBittorrent.ini it writes, the
/// states it shows, and, with MIRA_QBITTORRENT (a qbittorrent-nox: the Linux job of ci.yml), the real Web API under Mira's
/// profile, settings and password.
/// </summary>
internal static class QBittorrentTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("qBittorrent : un .torrent lu comme qBittorrent le lit (empreinte, privé, taille), le reste refusé", () =>
        {
            var (bytes, info) = Torrent("Sonic 2 (2022).mkv", Encoding.ASCII.GetBytes(new string('x', 40_000)), isPrivate: true);
            var file = Bencode.ReadFile(bytes) ?? throw new Exception("Not read");
            Assert(file.InfoHash == Convert.ToHexStringLower(SHA1.HashData(info)), $"Info hash {file.InfoHash}");
            Assert(file is { Private: true, Name: "Sonic 2 (2022).mkv", Size: 40_000 }, $"Read as {file}");
            Assert(Bencode.ReadFile(Torrent("Dune", [1, 2, 3], isPrivate: false).Bytes) is { Private: false }, "Public");
            Assert(Bencode.ReadFile(Encoding.ASCII.GetBytes("<html>login</html>")) is null && Bencode.ReadFile(bytes[..^1]) is null && Bencode.ReadFile([]) is null, "Not a .torrent");
            var other = Ben(new Dictionary<string, object> { ["info"] = new Dictionary<string, object> { ["name"] = "x", ["length"] = 1L, ["private"] = 0L, ["pieces"] = new byte[20], ["piece length"] = 16384L } });
            Assert(Bencode.ReadFile(other) is { Private: false }, "private = 0");
            return Task.CompletedTask;
        });

        await Test("qBittorrent : réglages de Mira écrits dans qBittorrent.ini sans toucher au reste, mot de passe au format de qBittorrent", () =>
        {
            var salt = Enumerable.Range(1, 16).Select(x => (byte)x).ToArray();
            var hash = QBittorrentSettings.PasswordHash("secret", salt);
            var parts = hash.Split(':');
            Assert(parts.Length == 2 && Convert.FromBase64String(parts[0]).SequenceEqual(salt), "Salt first");
            Assert(Convert.FromBase64String(parts[1]).SequenceEqual(Rfc2898DeriveBytes.Pbkdf2("secret"u8, salt, 100_000, HashAlgorithmName.SHA512, 64)), "PBKDF2-SHA512, 100 000 rounds");
            Assert(QBittorrentSettings.PasswordHash("secret") != QBittorrentSettings.PasswordHash("secret"), "A new salt each time");

            var fresh = QBittorrentSettings.Ini(null, 18411, "a:b");
            Assert(fresh.Contains("[Preferences]\r\n") && fresh.Contains("WebUI\\Address=127.0.0.1\r\n") && fresh.Contains("WebUI\\Port=18411\r\n")
                && fresh.Contains("WebUI\\Password_PBKDF2=\"@ByteArray(a:b)\"\r\n") && fresh.Contains("[LegalNotice]\r\nAccepted=true") && fresh.Contains("StartUpWindowState=Hidden"), fresh);
            var webUI = fresh[fresh.IndexOf("[WebUI]\r\n", StringComparison.Ordinal)..];
            Assert(webUI.Contains("\r\nAddress=127.0.0.1\r\n") && webUI.Contains("\r\nPort=18411\r\n") && webUI.Contains("\r\nUsername=mira\r\n")
                && webUI.Contains("\r\nPassword_PBKDF2=\"@ByteArray(a:b)\"\r\n") && webUI.Contains("\r\nLocalHostAuth=true\r\n"), "The Web UI's keys under [WebUI] too (qBittorrent 5.2 and later): " + fresh);
            const string existing = "[BitTorrent]\nSession\\Port=51413\n\n[Preferences]\nWebUI\\Port=8080\nGeneral\\Locale=fr\nwebui\\address=*\n\n[Meta]\nMigrationVersion=8\n";
            var merged = QBittorrentSettings.Ini(existing, 18411, "a:b");
            Assert(merged.Contains("Session\\Port=51413") && merged.Contains("General\\Locale=fr") && merged.Contains("MigrationVersion=8"), "The person's settings stay: " + merged);
            Assert(!merged.Contains("8080") && !merged.Contains("=*") && merged.Split("WebUI\\Address=").Length == 2, "Mira's replace theirs, whatever the case: " + merged);
            var preferences = merged[merged.IndexOf("[Preferences]", StringComparison.Ordinal)..merged.IndexOf("[Meta]", StringComparison.Ordinal)];
            Assert(preferences.Contains("WebUI\\Enabled=true") && preferences.TrimEnd().EndsWith("Advanced\\updateCheck=false", StringComparison.Ordinal) && preferences.EndsWith("\r\n\r\n", StringComparison.Ordinal),
                "New keys in their section, before the blank line: " + preferences);
            var bitTorrent = merged[..merged.IndexOf("[Preferences]", StringComparison.Ordinal)];
            Assert(bitTorrent.Contains("Session\\Port=51413\r\nSession\\DiskIOType=3\r\nSession\\PieceExtentAffinity=true\r\nSession\\Preallocation=true\r\n"), "Disk settings under [BitTorrent], read at its start: " + bitTorrent);
            Assert(QBittorrentSettings.Ini(merged, 18411, "a:b") == merged, "Written again: the same");
            return Task.CompletedTask;
        });

        await Test("qBittorrent : connexion acceptée comme 4.x et 5.1 la disent (200 « Ok. ») et comme 5.2 et après (204 sans texte), refusée sinon", async () =>
        {
            foreach (var (status, body, accepted) in new[] { (HttpStatusCode.OK, "Ok.", true), (HttpStatusCode.NoContent, "", true), (HttpStatusCode.OK, "Fails.", false), (HttpStatusCode.Unauthorized, "", false) })
            {
                using var client = new QBittorrentClient(18411, "secret", new Answer(status, body));
                try { await client.SignInAsync(CancellationToken.None); Assert(accepted, $"{(int)status} « {body} » accepted"); }
                catch (QBittorrentException ex) { Assert(!accepted && ex.SignIn, $"{(int)status} « {body} » refused: {ex.Message}"); }
            }
        });

        await Test("qBittorrent : ses états dits comme ceux de TorLink (en cours, en attente, en pause, échec, terminé)", () =>
        {
            foreach (var (state, status) in new[] { ("downloading", "downloading"), ("stalledDL", "downloading"), ("metaDL", "downloading"), ("queuedDL", "queued"), ("checkingDL", "queued"),
                ("pausedDL", "paused"), ("stoppedDL", "paused"), ("error", "failed"), ("missingFiles", "failed"), ("uploading", "seeding"), ("stalledUP", "seeding"), ("stoppedUP", "seeding"), ("pausedUP", "seeding") })
                Assert(QBittorrentDownloads.Status(state) == status, $"{state} → {QBittorrentDownloads.Status(state)}");
            using var document = JsonDocument.Parse("""
                [{"hash":"C12FE1C06BBA254A9DC9F519B335AA7C1367A88A","name":"Sonic 2 (2022)","state":"stalledDL","progress":0.4567,"dlspeed":0,"num_seeds":3,"num_leechs":25,"size":1000,"completed":456,"eta":8640000,"save_path":"D:\\Mira","category":"Mira","ratio":0.1},
                 {"hash":"nope","name":"x"},{"hash":"0123456789abcdef0123456789abcdef01234567","name":"Dune","state":"downloading","progress":0.1,"dlspeed":2048,"size":10,"completed":1,"eta":120},
                 {"hash":"89abcdef0123456789abcdef0123456789abcdef","name":"Arcane","state":"error","progress":0.2}]
                """);
            var torrents = QBittorrentDownloads.Parse(document.RootElement);
            Assert(torrents.Count == 3 && torrents[0].Hash == "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", "Hashes in lower case, the others left");
            var stalled = QBittorrentDownloads.Download(torrents[0]);
            Assert(stalled is { Status: "downloading", Progress: 45, Speed: 0, Peers: 28, Size: 1000, Downloaded: 456, Eta: null }, $"Waiting: {stalled}");
            Assert(QBittorrentDownloads.Download(torrents[1]) is { Speed: 2048, Eta: 120 }, "Downloading");
            Assert(QBittorrentDownloads.Download(torrents[2]) is { Status: "failed", Error.Length: > 0 }, "Failed, with a reason");
            Assert(!QBittorrentDownloads.Finished(torrents[0]), "Not finished");
            return Task.CompletedTask;
        });

        await Test("qBittorrent : disque débranché sous un téléchargement dit tel quel, revérifié et relancé à son retour, pas pour des fichiers supprimés", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "mira-qb-disk-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert(QBittorrentDownloads.Reachable(Path.Combine(root, "Mira"), make: true) && Directory.Exists(Path.Combine(root, "Mira")), "Its folder made and read");
                Assert(QBittorrentDownloads.Reachable(Path.Combine(root, "gone", "deeper")), "A folder deleted on a drive that answers: reachable (its files are gone, not its drive)");
                Assert(!QBittorrentDownloads.Reachable("\0bad"), "A path that can't be one: not reachable");
                var missing = new QBittorrentTorrent("0123456789abcdef0123456789abcdef01234567", "Dune", "missingFiles", 0.5, 0, 0, 10, 5, 0, Path.Combine(root, "Mira"), null, "Mira", 0, 0);
                var unplugged = QBittorrentDownloads.Download(missing, unreachable: true);
                Assert(unplugged is { Status: "failed" } && unplugged.Error!.Contains(missing.SavePath, StringComparison.Ordinal) && unplugged.Error.Contains("débranché", StringComparison.Ordinal), "Its drive named: " + unplugged.Error);
                var gone = QBittorrentDownloads.Download(missing).Error;
                Assert(gone?.Contains("plus là", StringComparison.Ordinal) == true, "Files gone from a drive that answers: said so: " + gone);
                Assert(QBittorrentDownloads.AfterDisk(missing, reachable: false, waiting: false) == "wait" && QBittorrentDownloads.AfterDisk(missing, reachable: false, waiting: true) == "wait", "Drive away: waited for");
                Assert(QBittorrentDownloads.AfterDisk(missing, reachable: true, waiting: true) == "recheck", "Drive back: checked again and started");
                Assert(QBittorrentDownloads.AfterDisk(missing, reachable: true, waiting: false) is null, "Files deleted, never seen with the drive away: left to the person");
                Assert(QBittorrentDownloads.AfterDisk(missing with { State = "downloading" }, reachable: false, waiting: true) is null, "Going on: nothing to do");
                Assert(QBittorrentDownloads.FolderUnreachable("E:\\Films").Contains("E:\\Films", StringComparison.Ordinal), "A new download refused, its folder named");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return Task.CompletedTask;
        });

        await Test("qBittorrent : partage arrêté dès la fin pour un torrent public, après 72 h pour un privé ou un inconnu, jamais supprimé", () =>
        {
            using var document = JsonDocument.Parse("""
                [{"hash":"0123456789abcdef0123456789abcdef01234567","name":"Haikyu","state":"stalledUP","progress":1,"private":false},
                 {"hash":"89abcdef0123456789abcdef0123456789abcdef","name":"Sonic 2","state":"uploading","progress":1,"private":true},
                 {"hash":"c12fe1c06bba254a9dc9f519b335aa7c1367a88a","name":"Dune","state":"stalledUP","progress":1},
                 {"hash":"fedcba9876543210fedcba9876543210fedcba98","name":"Arcane","state":"stoppedUP","progress":1,"isPrivate":false},
                 {"hash":"00112233445566778899aabbccddeeff00112233","name":"Klaus","state":"downloading","progress":0.5,"private":false},
                 {"hash":"ffeeddccbbaa99887766554433221100ffeeddcc","name":"Coco","state":"metaDL","progress":0,"private":null}]
                """);
            var torrents = QBittorrentDownloads.Parse(document.RootElement);
            Assert(torrents.Select(x => x.Private).SequenceEqual(new bool?[] { false, true, null, false, false, null }), "Private read from « private », else « isPrivate »: " + string.Join(", ", torrents.Select(x => x.Private)));
            Assert(torrents.Where(QBittorrentDownloads.StopsSharing).Select(x => x.Name).SequenceEqual(["Haikyu"]),
                "Only the finished public one still sharing: " + string.Join(", ", torrents.Where(QBittorrentDownloads.StopsSharing).Select(x => x.Name)));
            Assert(QBittorrentDownloads.Finished(torrents[3]), "Stopped, it is still finished: placed in the library");
            var preferences = QBittorrentSettings.Preferences("D:\\Mira", 51413);
            Assert(preferences["max_seeding_time_enabled"] is true && preferences["max_seeding_time"] is 4320 && preferences["max_ratio_enabled"] is false
                && preferences["max_inactive_seeding_time_enabled"] is false && preferences["max_ratio_act"] is 0, "72 hours, then stopped (0), never removed");
            Assert(preferences["enable_piece_extent_affinity"] is true && preferences["disk_io_type"] is 3 && preferences["preallocate_all"] is true && preferences["recheck_completed_torrents"] is false, "A slow drive kept up with: pieces finished side by side, written by pwrite (« Simple pread/pwrite »)");
            return Task.CompletedTask;
        });

        await Test("qBittorrent : un .torrent envoyé d’un autre appareil passe par le relais seulement vers un PC serveur qui a qBittorrent, avec les liens collés", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-02T02:00:00Z");
            var relay = new TorLinkRelay(() => now);
            var (bytes, _) = Torrent("Sonic 2 (2022).mkv", Encoding.ASCII.GetBytes(new string('x', 40_000)), isPrivate: true);
            var file = Bencode.ReadFile(bytes)!;
            var encoded = Convert.ToBase64String(bytes);
            Assert(relay.Request(null, null, null, null, null, torrent: encoded).Error!.Contains("qBittorrent"), "No agent yet: refused, with why");
            AgentReport Report(bool accepting, bool qbittorrent, List<AgentOutcome>? requests = null) =>
                new("PC-SALON", accepting, accepting ? null : "TorLink n’est pas activé dans Mira sur le PC serveur.", [], [], [], requests, [], QBittorrent: qbittorrent);
            relay.Exchange("pc-1", Report(accepting: true, qbittorrent: false));
            Assert(relay.Request(null, null, null, null, null, torrent: encoded).Error!.Contains("qBittorrent") && relay.Status().Agent is { QBittorrent: false }, "A Mira without qBittorrent: refused");
            relay.Exchange("pc-1", Report(accepting: false, qbittorrent: true));
            Assert(relay.Status().Agent is { QBittorrent: true, Accepting: false }, "Said to Mira web");
            Assert(relay.Request(null, null, null, null, null, torrent: "not base64!") is { Id: null, Error: "Ce n’est pas un fichier .torrent." }, "Not base64");
            Assert(relay.Request(null, null, null, null, null, torrent: Convert.ToBase64String("<html>"u8.ToArray())) is { Id: null }, "Not a .torrent");
            Assert(relay.Request(null, null, null, null, null, replace: "0123456789abcdef0123456789abcdef", torrent: encoded) is { Id: null }, "Never for a better version");
            var (sent, error) = relay.Request(null, null, null, null, null, torrent: encoded);
            Assert(sent is not null && error is null, "Taken: " + error);
            Assert(relay.Status().Requests.Single() is { Name: "Sonic 2 (2022).mkv", Size: 40_000 } request && request.InfoHash == file.InfoHash, "Named and sized from the file");
            var (pasted, _) = relay.Request($"magnet:?xt=urn:btih:{new string('c', 40)}&dn=Dune", null, null, null, null);
            // TorLink off, qBittorrent ready: the .torrent and the pasted link go, to qBittorrent.
            var work = relay.Exchange("pc-1", Report(accepting: false, qbittorrent: true));
            Assert(work.Requests.Count == 2 && work.Requests.Single(x => x.Id == sent).Torrent == encoded && work.Requests.All(x => x.Pasted), "Handed with the file: " + string.Join(", ", work.Requests));
            Assert(work.Requests.Single(x => x.Id == sent).Magnet.Contains(file.InfoHash), "Its hash's magnet beside it");
            relay.Exchange("pc-1", Report(accepting: false, qbittorrent: true, requests: [new(sent!, "sent", null), new(pasted!, "failed", "qBittorrent a refusé ce téléchargement.")]));
            Assert(relay.Status().Requests.Single(x => x.Id == pasted).Error == "qBittorrent a refusé ce téléchargement.", "Its refusal said");
            // Neither TorLink nor qBittorrent: nothing is handed.
            var (waiting, _) = relay.Request($"magnet:?xt=urn:btih:{new string('d', 40)}", null, null, null, null);
            Assert(relay.Exchange("pc-1", Report(accepting: false, qbittorrent: false)).Requests.Count == 0, "Nobody to take it");
            Assert(relay.Exchange("pc-1", Report(accepting: true, qbittorrent: false)).Requests.Single().Id == waiting, "TorLink takes a pasted link when there's no qBittorrent");
            return Task.CompletedTask;
        });

        await Test("qBittorrent : un téléchargement fini rangé dans la bibliothèque sans le retirer à qBittorrent (lien physique, sinon copie)", async () =>
        {
            var root = Directory.CreateTempSubdirectory("mira-qb-import-").FullName;
            try
            {
                var downloads = Directory.CreateDirectory(Path.Combine(root, "qBittorrent downloads")).FullName;
                var films = Directory.CreateDirectory(Path.Combine(root, "Films")).FullName;
                var source = Path.Combine(downloads, "Sonic.The.Hedgehog.2.2022.MULTi.1080p.mkv");
                File.WriteAllBytes(source, new byte[1000]);
                const string hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";
                var torrents = new List<QBittorrentTorrent>
                {
                    new(hash, "Sonic.The.Hedgehog.2.2022.MULTi.1080p.mkv", "stalledUP", 1, 0, 3, 1000, 1000, 0, downloads, source, "Mira", 1_793_600_000, 0.4),
                    new(new string('e', 40), "Unfinished", "downloading", 0.5, 10, 3, 1000, 500, 50, downloads, null, "Mira", 0, 0),
                };
                var asked = new List<string>();
                var history = await QBittorrentHistory.ReadAsync(torrents, (id, _) => { asked.Add(id); return Task.FromResult(new List<TorrentFileEntry> { new("Sonic.The.Hedgehog.2.2022.MULTi.1080p.mkv", 1000) }); }, new HashSet<string>(), CancellationToken.None);
                Assert(history.ReadHistory() is [{ Id: hash, Source: QBittorrentHistory.Source }] && asked.SequenceEqual([hash]), "Only the finished one, its files asked");
                var importer = new TorLinkImporter(Path.Combine(root, "profile"));
                await importer.ProcessAsync(history, new MediaLibraries(films, null, null), ImportMode.Share, automatic: false, requested: [hash]);
                var placed = Path.Combine(films, "Sonic The Hedgehog 2 (2022)", "Sonic The Hedgehog 2 (2022).mkv");
                Assert(importer.Find(hash) is { State: TorLinkImportState.Imported, MovedOut: false } && File.Exists(placed), "Placed: " + importer.Find(hash)?.Message);
                Assert(File.Exists(source), "Left to qBittorrent, which shares it");
                Assert(QBittorrentHistory.DownloadFolder(new(films, null, null)) == Path.Combine(root, L("Téléchargements qBittorrent", "qBittorrent downloads")), "Beside the library");
                Assert(QBittorrentHistory.DownloadFolder(new(null, null, null)).EndsWith(L("Téléchargements qBittorrent", "qBittorrent downloads"), StringComparison.Ordinal), "No library: the person's Downloads");
            }
            finally { Directory.Delete(root, recursive: true); }
        });

        if (Environment.GetEnvironmentVariable("MIRA_QBITTORRENT") is { Length: > 0 } program)
            await Test("qBittorrent : le vrai, dans le profil de Mira (réglages, mot de passe, .torrent privé partagé, magnet, pause, reprise, annulation)", () => RealAsync(program));
    }

    /// <summary>The same answer to every request.</summary>
    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    /// <summary>A one-file .torrent of <paramref name="content"/>, and its info dictionary's bytes.</summary>
    private static (byte[] Bytes, byte[] Info) Torrent(string name, byte[] content, bool isPrivate)
    {
        const int piece = 16384;
        var pieces = content.Chunk(piece).SelectMany(SHA1.HashData).ToArray();
        var info = new Dictionary<string, object> { ["name"] = name, ["length"] = (long)content.Length, ["piece length"] = (long)piece, ["pieces"] = pieces };
        if (isPrivate) info["private"] = 1L;
        return (Ben(new Dictionary<string, object> { ["announce"] = "http://127.0.0.1:9/announce?passkey=0123", ["info"] = info }), Ben(info));
    }

    /// <summary>A .torrent of several files in a folder, and its info dictionary's bytes.</summary>
    private static (byte[] Bytes, byte[] Info) Torrents(string folder, (string Name, int Size)[] files)
    {
        const int piece = 16384;
        var content = files.SelectMany(f => Enumerable.Repeat((byte)f.Name.Length, f.Size)).ToArray();
        var pieces = content.Chunk(piece).SelectMany(SHA1.HashData).ToArray();
        var list = files.Select(f => (object)new Dictionary<string, object> { ["length"] = (long)f.Size, ["path"] = new List<object> { f.Name } }).ToList();
        var info = new Dictionary<string, object> { ["name"] = folder, ["files"] = list, ["piece length"] = (long)piece, ["pieces"] = pieces };
        return (Ben(new Dictionary<string, object> { ["announce"] = "http://127.0.0.1:9/announce", ["info"] = info }), Ben(info));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RealAsync(string program)
    {
        var root = Directory.CreateTempSubdirectory("mira-qbittorrent-").FullName;
        var downloads = Directory.CreateDirectory(Path.Combine(root, "downloads")).FullName;
        var config = Directory.CreateDirectory(Path.Combine(root, "profile", "qBittorrent", "config")).FullName;
        int port = FreePort(), peers = FreePort();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // A setting of the person's, kept.
        File.WriteAllText(Path.Combine(config, QBittorrentSettings.FileName), QBittorrentSettings.Ini("[Preferences]\nGeneral\\Locale=fr\n", port, QBittorrentSettings.PasswordHash(password)));
        var output = new StringBuilder();
        Process Start()
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            start.ArgumentList.Add("--profile=" + Path.Combine(root, "profile"));
            var started = Process.Start(start) ?? throw new Exception("qBittorrent did not start");
            started.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            started.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            started.BeginOutputReadLine(); started.BeginErrorReadLine();
            return started;
        }
        var process = Start();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var ct = timeout.Token;
            using var client = new QBittorrentClient(port, password);
            string? version = null;
            while ((version = await client.VersionAsync(ct)) is null)
            {
                if (process.HasExited) throw new Exception("qBittorrent quit: " + output);
                await Task.Delay(200, ct);
            }
            Assert(version.StartsWith('v'), "Version " + version);

            using (var wrong = new QBittorrentClient(port, "not-the-password"))
            {
                try { await wrong.SignInAsync(ct); throw new Exception("A wrong password was accepted"); }
                catch (QBittorrentException) { }
            }

            await client.SetPreferencesAsync(QBittorrentSettings.Preferences(downloads, peers), ct);
            var ini = File.ReadAllText(Path.Combine(config, QBittorrentSettings.FileName));

            // A private .torrent whose file is there already: checked, then shared.
            var content = RandomNumberGenerator.GetBytes(100_000);
            var (bytes, _) = Torrent("Sonic 2 (2022).mkv", content, isPrivate: true);
            var file = Bencode.ReadFile(bytes)!;
            var target = Directory.CreateDirectory(Path.Combine(root, "library-side")).FullName;
            File.WriteAllBytes(Path.Combine(target, "Sonic 2 (2022).mkv"), content);
            Assert(await client.AddAsync(null, bytes, target, ct), "The .torrent refused");
            QBittorrentTorrent? torrent = null;
            while ((torrent = (await client.TorrentsAsync(ct)).FirstOrDefault(x => x.Hash == file.InfoHash)) is null || !QBittorrentDownloads.Finished(torrent)) await Task.Delay(200, ct);
            Assert(torrent.Category == QBittorrentSettings.Category && Path.GetFullPath(torrent.SavePath).TrimEnd('/', '\\') == target, $"Category and folder: {torrent}");
            // qBittorrent 5 says a torrent's private flag in its list; 4 doesn't, and Mira then leaves it to the 72 hours.
            Assert(torrent.Private == true || version.StartsWith("v4", StringComparison.Ordinal), $"Private flag read from qBittorrent {version}: {torrent.Private}");
            var files = await client.FilesAsync(file.InfoHash, ct);
            Assert(files.Count == 1 && files[0] == new TorrentFileEntry("Sonic 2 (2022).mkv", 100_000), "Files: " + string.Join(", ", files));

            // A magnet link: waits for its metadata (no peer here), then paused, resumed and cancelled.
            const string hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";
            Assert(await client.AddAsync($"magnet:?xt=urn:btih:{hash}&dn=Dune", null, downloads, ct), "The magnet refused");
            async Task<QBittorrentTorrent?> Find() => (await client.TorrentsAsync(ct)).FirstOrDefault(x => x.Hash == hash);
            while (await Find() is null) await Task.Delay(200, ct);
            await client.ControlAsync(hash, "pause", ct);
            while ((await Find())?.State is not ("pausedDL" or "stoppedDL")) await Task.Delay(200, ct);
            await client.ControlAsync(hash, "resume", ct);
            while (QBittorrentDownloads.Status((await Find())!.State) == "paused") await Task.Delay(200, ct);
            // What Mira asks once an unplugged drive is back: accepted by qBittorrent's API (4.x and 5.x).
            await client.ControlAsync(hash, "recheck", ct);
            await client.ControlAsync(hash, "delete", ct);
            while (await Find() is not null) await Task.Delay(200, ct);
            Assert((await client.TorrentsAsync(ct)).Count == 1, "Only Mira's category");

            // « Coller un lien magnet »: a link of three files read (stopped at its metadata, out of Mira's list), then two of them chosen.
            var (season, _) = Torrents("Show.S01", [("Show.S01E01.mkv", 40_000), ("Show.S01E02.mkv", 40_000), ("sample.mkv", 20_000)]);
            var seasonHash = Bencode.ReadFile(season)!.InfoHash;
            Assert(await client.AddAsync(null, season, downloads, ct, look: true), "The link to read refused");
            List<LinkFile> inside;
            while ((inside = await client.ReadAsync(seasonHash, ct)).Count == 0) await Task.Delay(200, ct);
            Assert(inside.Select(x => (x.Index, x.Path)).SequenceEqual([(0, "Show.S01/Show.S01E01.mkv"), (1, "Show.S01/Show.S01E02.mkv"), (2, "Show.S01/sample.mkv")]), "Content: " + string.Join(", ", inside));
            Assert((await client.TorrentAsync(seasonHash, ct))?.Category == MagnetLinks.LookCategory, "Under the category of links read");
            // Stopped once read (a .torrent has its metadata at once: qBittorrent's condition doesn't stop it): nothing downloaded before the choice.
            for (var i = 0; i < 50 && QBittorrentDownloads.Status((await client.TorrentAsync(seasonHash, ct))!.State) != "paused"; i++) await Task.Delay(100, ct);
            Assert(QBittorrentDownloads.Status((await client.TorrentAsync(seasonHash, ct))!.State) == "paused", "Stopped once read: " + (await client.TorrentAsync(seasonHash, ct))!.State);
            Assert(!(await client.TorrentsAsync(ct)).Any(x => x.Hash == seasonHash) && (await client.LooksAsync(ct)).Any(x => x.Hash == seasonHash), "Never among Mira's downloads while read");
            Assert(MagnetLinks.Read("Show.S01", inside) is { Episodic: true, Episodes.Count: 2 } read && MagnetLinks.FilesOf(LinkOption.Everything, read, new HashSet<(int, int)>(), new HashSet<(int, int)>()) is null, "Read by Mira");
            await client.ChooseAsync(seasonHash, [0, 1], ct);
            Assert((await client.TorrentsAsync(ct)).Any(x => x.Hash == seasonHash), "Among Mira's downloads once chosen");
            for (var i = 0; i < 50 && QBittorrentDownloads.Status((await client.TorrentAsync(seasonHash, ct))!.State) == "paused"; i++) await Task.Delay(100, ct);
            Assert(QBittorrentDownloads.Status((await client.TorrentAsync(seasonHash, ct))!.State) != "paused", "Started once chosen");
            var chosenFiles = await client.FilesAsync(seasonHash, ct);
            Assert(chosenFiles.Select(x => x.RelativePath).SequenceEqual([Path.Combine("Show.S01", "Show.S01E01.mkv"), Path.Combine("Show.S01", "Show.S01E02.mkv")]), "The sample left out: " + string.Join(", ", chosenFiles));
            await client.ControlAsync(seasonHash, "delete", ct);
            Assert(await client.TorrentAsync(seasonHash, ct) is null, "Gone");

            // Its own session forgotten (qBittorrent restarted, or the cookie expired): signed in again.
            await client.ControlAsync(file.InfoHash, "pause", ct);
            ini = File.ReadAllText(Path.Combine(config, QBittorrentSettings.FileName));
            Assert(ini.Contains("General\\Locale=fr"), "The person's setting kept: " + ini);

            // Quit, then started again under another password (a profile whose secret was drawn anew): qBittorrent 5.2
            // and later, which moved the Web UI's keys to [WebUI] at its first start, read Mira's there.
            await client.QuitAsync(ct);
            await process.WaitForExitAsync(ct);
            process.Dispose();
            var renewed = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var settings = Path.Combine(config, QBittorrentSettings.FileName);
            File.WriteAllText(settings, QBittorrentSettings.Ini(File.ReadAllText(settings), port, QBittorrentSettings.PasswordHash(renewed)));
            process = Start();
            using var again = new QBittorrentClient(port, renewed);
            while (await again.VersionAsync(ct) is null)
            {
                if (process.HasExited) throw new Exception("qBittorrent quit: " + output);
                await Task.Delay(200, ct);
            }
            Assert((await again.TorrentsAsync(ct)).Any(x => x.Hash == file.InfoHash), "Its downloads kept across the restart");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            process.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
