using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.TorLink;
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
            const string existing = "[BitTorrent]\nSession\\Port=51413\n\n[Preferences]\nWebUI\\Port=8080\nGeneral\\Locale=fr\nwebui\\address=*\n\n[Meta]\nMigrationVersion=8\n";
            var merged = QBittorrentSettings.Ini(existing, 18411, "a:b");
            Assert(merged.Contains("Session\\Port=51413") && merged.Contains("General\\Locale=fr") && merged.Contains("MigrationVersion=8"), "The person's settings stay: " + merged);
            Assert(!merged.Contains("8080") && !merged.Contains("=*") && merged.Split("WebUI\\Address=").Length == 2, "Mira's replace theirs, whatever the case: " + merged);
            var preferences = merged[merged.IndexOf("[Preferences]", StringComparison.Ordinal)..merged.IndexOf("[Meta]", StringComparison.Ordinal)];
            Assert(preferences.Contains("WebUI\\Enabled=true") && preferences.TrimEnd().EndsWith("Advanced\\updateCheck=false", StringComparison.Ordinal) && preferences.EndsWith("\r\n\r\n", StringComparison.Ordinal),
                "New keys in their section, before the blank line: " + preferences);
            Assert(QBittorrentSettings.Ini(merged, 18411, "a:b") == merged, "Written again: the same");
            return Task.CompletedTask;
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

        if (Environment.GetEnvironmentVariable("MIRA_QBITTORRENT") is { Length: > 0 } program)
            await Test("qBittorrent : le vrai, dans le profil de Mira (réglages, mot de passe, .torrent privé partagé, magnet, pause, reprise, annulation)", () => RealAsync(program));
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
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add("--profile=" + Path.Combine(root, "profile"));
        using var process = Process.Start(start) ?? throw new Exception("qBittorrent did not start");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
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
            await client.ControlAsync(hash, "delete", ct);
            while (await Find() is not null) await Task.Delay(200, ct);
            Assert((await client.TorrentsAsync(ct)).Count == 1, "Only Mira's category");

            // Its own session forgotten (qBittorrent restarted, or the cookie expired): signed in again.
            await client.ControlAsync(file.InfoHash, "pause", ct);
            ini = File.ReadAllText(Path.Combine(config, QBittorrentSettings.FileName));
            Assert(ini.Contains("General\\Locale=fr"), "The person's setting kept: " + ini);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
