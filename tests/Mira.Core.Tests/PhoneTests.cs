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

/// <summary>Mira on the phone: Mira web, the Jellyfin plugin's files and playlists, phone setup. Free of WPF: also run outside Windows.</summary>
internal static class PhoneTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        await Test("Mira web : installé sur Jellyfin depuis le dépôt de Mira, les autres dépôts gardés, puis Jellyfin redémarré", async () =>
        {
            var repositories = $$"""[{"Name":"Jellyfin Stable","Url":"https://repo.jellyfin.org/files/plugin/manifest.json","Enabled":true},{"Name":"Mira","Url":"{{MiraWeb.LatestRepository}}","Enabled":true}]""";
            var calls = new List<string>(); var repository = MiraWeb.RepositoryFor(new Version(0, 7, 5));
            bool installed = false, restarted = false, admin = true, available = true;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath.TrimStart('/');
                calls.Add($"{request.Method} {path}");
                if (!admin && path != "Mira/manifest.webmanifest") return new HttpResponseMessage(HttpStatusCode.Forbidden);
                switch (path)
                {
                    case "Repositories" when request.Method == HttpMethod.Get:
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(repositories, Encoding.UTF8, "application/json") };
                    case "Repositories":
                        repositories = await request.Content!.ReadAsStringAsync();
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "Packages/Installed/Mira":
                        if (!available) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        Assert(request.RequestUri.Query.Contains("assemblyGuid=4ea89259-3350-45b0-8045-f1ab627214dd") && request.RequestUri.Query.Contains("repositoryUrl=https%3A%2F%2Fgithub.com"), "Install request: " + request.RequestUri.Query);
                        installed = true;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "System/Restart":
                        restarted = true;
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "Mira/manifest.webmanifest":
                        return new HttpResponseMessage(installed && restarted ? HttpStatusCode.OK : HttpStatusCode.NotFound);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            Assert(!await client.WebAppAvailableAsync(), "Mira web available before its installation");
            Assert(await client.InstallWebAppAsync(repository, TimeSpan.FromSeconds(10)) == WebAppInstall.Ready, "Installation not ready");
            // The latest release's repository (Mira up to 0.7.4) gives way to the checked one.
            var list = JsonDocument.Parse(repositories).RootElement;
            Assert(list.GetArrayLength() == 2 && list[0].GetProperty("Url").GetString()!.Contains("repo.jellyfin.org") && list[1].GetProperty("Url").GetString() == repository, "Repositories: " + repositories);
            Assert(await client.WebAppAvailableAsync(), "Mira web not available after its installation");
            calls.Clear();
            Assert(await client.InstallWebAppAsync(repository, TimeSpan.FromSeconds(10)) == WebAppInstall.Ready && !calls.Contains("POST Repositories"), "Mira's repository added twice");
            available = false;
            Assert(await client.InstallWebAppAsync(repository, TimeSpan.FromSeconds(1)) == WebAppInstall.Unavailable, "No version for this Jellyfin: not reported");
            admin = false;
            Assert(await client.InstallWebAppAsync(repository) == WebAppInstall.NotAllowed, "A guest installed a plugin");
            Assert(MiraWeb.SharePage("http://192.168.1.20:8096/") == "http://192.168.1.20:8096/Mira/#/partager", "Share page address");
        });
        await Test("Journal de Jellyfin : la cause d’un démarrage raté est lue, pas les erreurs ordinaires ni les anciennes", () =>
        {
            // The format of Jellyfin 12.1's log files, with the errors a running server logs and a start that fails.
            var folder = Path.Combine(testRoot, "jellyfin-log"); Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "log_20261001.log"), [
                "[2026-10-01 09:00:00.000 +02:00] [FTL] [1] Main: Error while starting server",
                "System.InvalidOperationException: An old failure",
                "[2026-10-01 13:33:26.446 +02:00] [INF] [11] Main: Jellyfin version: \"12.1.0\"",
                "[2026-10-01 13:33:36.267 +02:00] [ERR] [10] Emby.Server.Implementations.Updates.InstallationManager: An error occurred while accessing the plugin manifest: \"https://repo.jellyfin.org/files/plugin/manifest.json\"",
                "System.Net.Http.HttpRequestException: No such host is known.",
                "[2026-10-01 13:33:40.100 +02:00] [FTL] [1] Main: Error while starting server",
                "System.IO.IOException: Failed to bind to address http://[::]:8096: address already in use.",
                " ---> Microsoft.AspNetCore.Connections.AddressInUseException: Only one usage of each socket address is normally permitted.",
                "   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.TransportManager.BindAsync()",
                "[2026-10-01 13:33:41.000 +02:00] [INF] [1] Main: Received a SIGTERM signal, shutting down"]);
            var cause = JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 10, 1, 13, 30, 0, TimeSpan.FromHours(2)));
            Assert(cause == "Error while starting server (Failed to bind to address http://[::]:8096: address already in use.)", "Cause: " + cause);
            Assert(JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 10, 1, 13, 35, 0, TimeSpan.FromHours(2))) is null, "An earlier failure reported as the latest");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "log_20261001.log"), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert(JellyfinLog.LastFatal(folder, new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero)) is null, "An old log file read as recent");
            Assert(JellyfinLog.LastFatal(Path.Combine(testRoot, "no-jellyfin-log"), DateTimeOffset.MinValue) is null, "A missing folder");
            return Task.CompletedTask;
        });
        await Test("Mira web : sous-titres d’une conversion à l’heure dans le lecteur d’Apple (listes HLS de Jellyfin réécrites)", () =>
        {
            // Jellyfin's master playlist: its subtitles go through Mira, everything else stays Jellyfin's.
            var master = "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"French\",DEFAULT=NO,FORCED=NO,AUTOSELECT=YES,"
                + "URI=\"0123456789abcdef0123456789abcdef/Subtitles/3/subtitles.m3u8?SegmentLength=30&ApiKey=k\",LANGUAGE=\"fra\"\n"
                + "#EXT-X-STREAM-INF:BANDWIDTH=1,SUBTITLES=\"subs\"\nmain.m3u8?DeviceId=d&ApiKey=k\n";
            var rewritten = Mira.Jellyfin.HlsPlaylists.Master(master, "/jf/videos/item/", "/jf/Mira/hls/item/");
            Assert(rewritten.Contains("URI=\"/jf/Mira/hls/item/0123456789abcdef0123456789abcdef/Subtitles/3/subtitles.m3u8?SegmentLength=30&ApiKey=k\""), "Subtitles not through Mira: " + rewritten);
            Assert(rewritten.Contains("\n/jf/videos/item/main.m3u8?DeviceId=d&ApiKey=k\n"), "Video not Jellyfin's: " + rewritten);
            Assert(Mira.Jellyfin.HlsPlaylists.Master("#EXTM3U\r\n/x/main.m3u8\r\n", "/v/", "/m/") == "#EXTM3U\n/x/main.m3u8\n", "Absolute address changed");
            // Apple's menu shows Mira's French names, given by Jellyfin index; quotes and control characters cannot get in.
            var names = Mira.Jellyfin.HlsPlaylists.Names("3:Français|x:Bad|5:\"Anglais\"\n|7:");
            Assert(names.Count == 2 && names[3] == "Français" && names[5] == "Anglais", "Names: " + string.Join(", ", names));
            Assert(Mira.Jellyfin.HlsPlaylists.Master(master, "/jf/videos/item/", "/jf/Mira/hls/item/", names).Contains("NAME=\"Français\",DEFAULT=NO"), "Name not given");
            Assert(Mira.Jellyfin.HlsPlaylists.Master(master, "/v/", "/m/", Mira.Jellyfin.HlsPlaylists.Names("3:Prix $1 $0")).Contains("NAME=\"Prix $1 $0\""), "Name read as a pattern");
            // A subtitle of no known language: « und » for HLS, where Jellyfin writes « Unknown ».
            var unknown = Mira.Jellyfin.HlsPlaylists.Master(master.Replace("LANGUAGE=\"fra\"", "LANGUAGE=\"Unknown\""), "/v/", "/m/");
            Assert(unknown.Contains("LANGUAGE=\"und\"") && !unknown.Contains("Unknown"), "Unknown language kept: " + unknown);
            // Its subtitle playlist: WebVTT straight from Jellyfin, without the MPEG-TS time map (10 s late on fMP4).
            var subtitles = "#EXTM3U\n#EXTINF:30,\nstream.vtt?CopyTimestamps=true&AddVttTimeMap=true&StartPositionTicks=0&EndPositionTicks=300000000&ApiKey=k\n#EXT-X-ENDLIST\n";
            var segments = Mira.Jellyfin.HlsPlaylists.Subtitles(subtitles, "/jf/videos/item/source/Subtitles/3/");
            Assert(segments.Contains("\n/jf/videos/item/source/Subtitles/3/stream.vtt?CopyTimestamps=true&AddVttTimeMap=false&StartPositionTicks=0&"), "Segments: " + segments);
            Assert(!segments.Contains("AddVttTimeMap=true", StringComparison.OrdinalIgnoreCase), "Time map kept: " + segments);
            // A subtitle stream Jellyfin refuses: an empty but valid playlist, so that the video still plays.
            Assert(Mira.Jellyfin.HlsPlaylists.EmptySubtitles.StartsWith("#EXTM3U\n", StringComparison.Ordinal) && Mira.Jellyfin.HlsPlaylists.EmptySubtitles.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal)
                && !Mira.Jellyfin.HlsPlaylists.EmptySubtitles.Contains("#EXTINF", StringComparison.Ordinal), "Empty subtitles playlist");
            return Task.CompletedTask;
        });
        await Test("Mira web : seul le style de la page est permis, par son empreinte (politique de sécurité)", () =>
        {
            // The page's inline style, hashed as browsers hash it: its text in UTF-8 (the value openssl gives for it).
            var policy = Mira.Jellyfin.ContentPolicy.For("<head>\n  <style>html, body { background: #070708; color: #f4f4f5; margin: 0; }</style>\n</head>");
            Assert(policy.Contains("style-src 'self' 'sha256-jX62fjJWRNA/aBALPNu3Ignhb7AYylcMCFHqua1lFRk=';", StringComparison.Ordinal), "Policy: " + policy);
            Assert(!policy.Contains("unsafe-inline", StringComparison.Ordinal) && policy.StartsWith("default-src 'self'; script-src 'self';", StringComparison.Ordinal)
                && policy.EndsWith("frame-ancestors 'self'", StringComparison.Ordinal), "Policy: " + policy);
            // A file with CR LF: the HTML parser leaves LF, which the browser hashes.
            Assert(Mira.Jellyfin.ContentPolicy.StyleHashes("<style>\r\na{}\r\n</style>").SequenceEqual(Mira.Jellyfin.ContentPolicy.StyleHashes("<style>\na{}\n</style>")), "Line endings change the hash");
            // Each style element, with attributes or in capitals, once; none at all: only the app's own stylesheets.
            Assert(Mira.Jellyfin.ContentPolicy.StyleHashes("<STYLE media=\"all\">a{}</STYLE><style>b{}</style><style>a{}</style >").Count == 2, "Style elements");
            Assert(Mira.Jellyfin.ContentPolicy.For("<p>Mira</p>").Contains("; style-src 'self'; img-src", StringComparison.Ordinal), "Policy without inline style");
            return Task.CompletedTask;
        });
        await Test("Mira web : chaque fichier de l’app est servi par l’extension, un type inconnu est signalé", () =>
        {
            // The repository's src/Mira.Web, which the plugin builds in whole (Mira.Jellyfin.csproj): every file must be
            // served, or the phone gets a 404 for it (a .mjs once broke the QR code page).
            static DirectoryInfo? Repository(string start)
            {
                var folder = new DirectoryInfo(start);
                while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Mira.sln"))) folder = folder.Parent;
                return folder;
            }
            var repository = Repository(AppContext.BaseDirectory) ?? Repository(Environment.CurrentDirectory);
            Assert(repository is not null, "Repository not found from " + AppContext.BaseDirectory);
            var folder = Path.Combine(repository!.FullName, "src", "Mira.Web");
            var web = Mira.Jellyfin.WebFiles.FromFolder(folder);
            Assert(web.Unserved.Count == 0, "Not served: " + string.Join(", ", web.Unserved));
            var paths = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')).ToList();
            Assert(paths.Count > 20 && paths.Contains("js/app.js") && paths.Contains("index.html"), "Files: " + string.Join(", ", paths));
            foreach (var path in paths) Assert(web.TryGet(path, out var file) && file.ContentType.Length > 0, "Not served: " + path);
            // A file of a kind the plugin does not know is kept aside and named, never served by mistake.
            var odd = Path.Combine(testRoot, "web-odd");
            Directory.CreateDirectory(Path.Combine(odd, "js"));
            File.WriteAllText(Path.Combine(odd, "index.html"), "<p>Mira</p>");
            File.WriteAllText(Path.Combine(odd, "js", "app.js.map"), "{}");
            var oddWeb = Mira.Jellyfin.WebFiles.FromFolder(odd);
            Assert(oddWeb.Unserved.SequenceEqual(["js/app.js.map"]) && !oddWeb.TryGet("js/app.js.map", out _) && oddWeb.TryGet("index.html", out _), "Unserved: " + string.Join(", ", oddWeb.Unserved));
            return Task.CompletedTask;
        });
        await Test("Mira sur ton téléphone : étapes cochées, « Tout préparer » en une autorisation, QR code qui marche partout avec Tailscale", () =>
        {
            const string server = "http://127.0.0.1:8096", home = "http://192.168.1.20:8096", tailnet = "http://100.101.102.103:8096";
            JellyfinStartup.State service = new(true, true, false, true), noService = new(false, false, false, false);
            JellyfinFirewall.Reach reachOpen = new(JellyfinFirewall.Verdict.Open, JellyfinFirewall.Private), reachBlocked = new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Public);
            static string Marks(IReadOnlyList<PhoneSetup.Step> steps) => string.Join(" ", steps.Select(x => $"{x.Key}:{x.Mark}"));
            // A new PC, as the friend's: Jellyfin without its service, no firewall rule, Mira web missing, Tailscale connected
            // but refused by Jellyfin. One click does all three, with one consent of Windows.
            var fresh = new PhoneSetup.Facts(server, true, Web: false, Home: home, Admin: true, Tailnet: tailnet, TailnetAllowed: false, Firewall: reachBlocked, Boot: noService);
            var steps = PhoneSetup.Steps(fresh);
            Assert(Marks(steps) == "web:ToDo firewall:ToDo tailscale:ToDo boot:Note", "A new PC: " + Marks(steps));
            Assert(PhoneSetup.CanPrepare(steps) && PhoneSetup.NeedsWindows(steps), "« Tout préparer » and Windows' consent");
            Assert(PhoneSetup.Everywhere(fresh) is null && PhoneSetup.Address(fresh) == home + "/Mira" && PhoneSetup.SharePage(fresh) == home + "/Mira/#/partager",
                "Before it is ready, the QR code is the home network's: " + PhoneSetup.SharePage(fresh));
            // Once prepared: the QR code is this PC on Tailscale, which works at home too, with the home address to switch to.
            var ready = fresh with { Web = true, TailnetAllowed = true, Firewall = reachOpen };
            steps = PhoneSetup.Steps(ready);
            Assert(Marks(steps) == "web:Done firewall:Done tailscale:Done boot:Note" && !PhoneSetup.CanPrepare(steps), "Ready: " + Marks(steps));
            Assert(PhoneSetup.Everywhere(ready) == tailnet && PhoneSetup.Address(ready) == tailnet + "/Mira"
                && PhoneSetup.SharePage(ready) == home + "/Mira/#/partager?partout=http%3A%2F%2F100.101.102.103%3A8096", "Everywhere: " + PhoneSetup.SharePage(ready));
            // Without Tailscale: the rest is ready, Tailscale is the person's to install (a link), and the QR code stays at home.
            var noTailscale = ready with { Tailnet = null, TailnetAllowed = false };
            steps = PhoneSetup.Steps(noTailscale);
            Assert(Marks(steps) == "web:Done firewall:Done tailscale:Yours boot:Note" && steps[2].Action == "Installer Tailscale" && !PhoneSetup.CanPrepare(steps)
                && PhoneSetup.Address(noTailscale) == home + "/Mira", "Without Tailscale: " + Marks(steps));
            // The box counted as public: Mira's rule never applies there. The same Wi-Fi joins a café's, so the network is made
            // private only when the person says it is home's (« Passer en privé », its own consent, named by its name);
            // « Tout préparer » still writes Mira's rule, for Tailscale. When Windows will not, the person is told where.
            var box = new JellyfinFirewall.PublicNetwork("Wi-Fi", "Livebox-1234");
            var counted = fresh with { Firewall = reachBlocked with { Public = box } };
            PhoneSetup.Step Wall(PhoneSetup.Facts facts) => PhoneSetup.Steps(facts).Single(x => x.Key == "firewall");
            steps = PhoneSetup.Steps(counted);
            Assert(Wall(counted) is { Mark: PhoneSetup.Mark.ToDo, Action: "Passer en privé" } toPrivate && toPrivate.Text.Contains("« Livebox-1234 » (Wi-Fi)") && toPrivate.Text.Contains("ta maison")
                && PhoneSetup.NeedsWindows(steps), "The box counted as public: " + Marks(steps));
            Assert(Wall(counted with { Firewall = reachBlocked with { Public = new("Ethernet") } }).Text.Contains("« Ethernet » comme"), "A network without a name");
            // Away from home (a café's Wi-Fi, counted as public), Mira's rule ready: nothing for « Tout préparer », the QR code
            // is Tailscale's, and the network stays public unless the person says otherwise. An older version's rule is narrowed.
            var away = ready with { Firewall = new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Public, Tailnet: JellyfinFirewall.Verdict.Open, Public: box), MiraRule = JellyfinFirewall.RuleState.Ready };
            steps = PhoneSetup.Steps(away);
            Assert(Wall(away) is { Mark: PhoneSetup.Mark.Yours, Action: "Passer en privé" } && !PhoneSetup.CanPrepare(steps) && PhoneSetup.Everywhere(away) == tailnet, "Away from home: " + Marks(steps));
            Assert(Wall(away with { Firewall = new(JellyfinFirewall.Verdict.Open, JellyfinFirewall.Public, Tailnet: JellyfinFirewall.Verdict.Open, Public: box), MiraRule = JellyfinFirewall.RuleState.Stale })
                is { Mark: PhoneSetup.Mark.ToDo, Action: "Passer en privé" }, "Away from home, an older version's rule");
            var kept = away with { NetworkKept = true };
            steps = PhoneSetup.Steps(kept);
            Assert(Wall(kept) is { Mark: PhoneSetup.Mark.Yours, Action: null } keptPublic && keptPublic.Text.Contains("Paramètres → Réseau et Internet → Wi-Fi") && !PhoneSetup.NeedsWindows(steps)
                && PhoneSetup.Is(PhoneSetup.Steps(kept with { Firewall = reachOpen }), "firewall", PhoneSetup.Mark.Done), "Windows keeps it public: " + Marks(steps));
            // A profile Windows does not say, assumed public, alone in the way: the person's to check, never asked again.
            var unsure = ready with { Firewall = reachBlocked with { Public = new("Wi-Fi", Known: false) }, MiraRule = JellyfinFirewall.RuleState.Ready };
            steps = PhoneSetup.Steps(unsure);
            Assert(Wall(unsure) is { Mark: PhoneSetup.Mark.Yours, Action: null } check && check.Text.Contains("ne dit pas") && !PhoneSetup.CanPrepare(steps), "A profile Windows does not say: " + Marks(steps));
            // Blocking everything on public networks: the box's made private (by the person), rather than « block all » unticked for every café.
            Assert(Wall(counted with { Firewall = new(JellyfinFirewall.Verdict.Closed, JellyfinFirewall.Public, Public: box) }) is { Mark: PhoneSetup.Mark.ToDo, Action: "Passer en privé" },
                "Blocking everything on the box counted as public");
            // The firewall blocking everything is the person's to undo, in Windows' settings, on the network it blocks.
            steps = PhoneSetup.Steps(ready with { Firewall = new(JellyfinFirewall.Verdict.Closed, JellyfinFirewall.Public) });
            Assert(steps.Single(x => x.Key == "firewall") is { Mark: PhoneSetup.Mark.Yours } closed && closed.Text.Contains("Réseau public") && !PhoneSetup.CanPrepare(steps)
                && PhoneSetup.Everywhere(ready with { Firewall = new(JellyfinFirewall.Verdict.Closed, JellyfinFirewall.Private, Tailnet: JellyfinFirewall.Verdict.Closed) }) is null,
                "Everything blocked: " + Marks(steps));
            // The Wi-Fi blocking everything, Tailscale open: the QR code gives the address that works, Tailscale's.
            var wifiClosed = ready with { Firewall = new(JellyfinFirewall.Verdict.Closed, JellyfinFirewall.Public, Tailnet: JellyfinFirewall.Verdict.Open) };
            Assert(PhoneSetup.Everywhere(wifiClosed) == tailnet && PhoneSetup.Address(wifiClosed) == tailnet + "/Mira", "Tailscale open, the Wi-Fi closed: " + PhoneSetup.Address(wifiClosed));
            // What « Tout préparer » cannot change is the person's, never asked again: a block rule naming no program, or a
            // Jellyfin whose program Mira does not find (started by another account, in Docker) without Mira's rule.
            steps = PhoneSetup.Steps(fresh with { Firewall = reachBlocked with { Kept = "Bloquer 8096" } });
            Assert(steps.Single(x => x.Key == "firewall") is { Mark: PhoneSetup.Mark.Yours } blocking && blocking.Text.Contains("« Bloquer 8096 »") && !PhoneSetup.NeedsWindows(steps), "A rule of someone's own: " + Marks(steps));
            steps = PhoneSetup.Steps(fresh with { Firewall = null, Program = false });
            Assert(PhoneSetup.Is(steps, "firewall", PhoneSetup.Mark.Yours) && !PhoneSetup.NeedsWindows(steps)
                && PhoneSetup.Is(PhoneSetup.Steps(fresh with { Firewall = null, Program = false, MiraRule = JellyfinFirewall.RuleState.Ready }), "firewall", PhoneSetup.Mark.Done)
                && PhoneSetup.Is(PhoneSetup.Steps(fresh with { Firewall = null, Program = false, MiraRule = JellyfinFirewall.RuleState.Stale }), "firewall", PhoneSetup.Mark.Yours), "Jellyfin's program not found: " + Marks(steps));
            // Jellyfin restarting (after Mira web's installation) or stopped: nothing to install, no administrator asked for.
            var silent = fresh with { Web = null, Admin = null, TailnetAllowed = null, Answering = false };
            steps = PhoneSetup.Steps(silent);
            Assert(Marks(steps) == "web:Note firewall:ToDo tailscale:Note boot:Note" && steps[0].Title.Contains("ne répond pas"), "Jellyfin silent: " + Marks(steps));
            Assert(PhoneSetup.Is(PhoneSetup.Steps(ready with { TailnetAllowed = null }), "tailscale", PhoneSetup.Mark.Note), "The administrator, Tailscale not read yet");
            // Unreadable firewall: Mira's rule decides, when it is as Mira writes it. The service: set up with the same consent when it lacks something.
            Assert(PhoneSetup.Is(PhoneSetup.Steps(ready with { Firewall = null, MiraRule = JellyfinFirewall.RuleState.Ready }), "firewall", PhoneSetup.Mark.Done)
                && PhoneSetup.Is(PhoneSetup.Steps(ready with { Firewall = null }), "firewall", PhoneSetup.Mark.ToDo)
                && PhoneSetup.Is(PhoneSetup.Steps(ready with { Firewall = null, MiraRule = JellyfinFirewall.RuleState.Stale }), "firewall", PhoneSetup.Mark.ToDo), "Firewall not readable");
            // Phones getting through by other rules (Windows' prompt), Mira's own left on another port: replaced, with the same consent.
            var staleRule = ready with { MiraRule = JellyfinFirewall.RuleState.Stale };
            steps = PhoneSetup.Steps(staleRule);
            Assert(steps.Single(x => x.Key == "firewall") is { Mark: PhoneSetup.Mark.ToDo, Title: "Mettre à jour la règle du pare-feu" } update && update.Text.Contains("version précédente")
                && PhoneSetup.NeedsWindows(steps)
                && PhoneSetup.Everywhere(staleRule) == tailnet, "Mira's rule, stale: " + Marks(steps));
            steps = PhoneSetup.Steps(ready with { Boot = service with { StartsWithWindows = false } });
            Assert(PhoneSetup.Is(steps, "boot", PhoneSetup.Mark.ToDo) && PhoneSetup.NeedsWindows(steps), "The service, to set up");
            Assert(PhoneSetup.Is(PhoneSetup.Steps(staleRule with { Boot = service }), "boot", PhoneSetup.Mark.Done), "The service's own part is ready");
            steps = PhoneSetup.Steps(ready with { TailnetAllowed = false });
            Assert(PhoneSetup.CanPrepare(steps) && !PhoneSetup.NeedsWindows(steps), "Tailscale in Jellyfin needs no consent of Windows");
            // Another account than Jellyfin's administrator: Mira web and Tailscale are left to it.
            foreach (var allowed in new bool?[] { false, null })
            {
                steps = PhoneSetup.Steps(fresh with { Admin = false, TailnetAllowed = allowed, Firewall = reachOpen });
                Assert(Marks(steps) == "web:Yours firewall:Done tailscale:Yours boot:Note" && !PhoneSetup.CanPrepare(steps), "Not an administrator: " + Marks(steps));
            }
            // Jellyfin on another computer: Mira web only, the rest is set up over there.
            var remote = new PhoneSetup.Facts("http://192.168.1.30:8096", false, Web: true, Home: "http://192.168.1.30:8096");
            steps = PhoneSetup.Steps(remote);
            Assert(Marks(steps) == "web:Done elsewhere:Note" && PhoneSetup.Address(remote) == "http://192.168.1.30:8096/Mira" && PhoneSetup.Everywhere(remote) is null, "Another computer: " + Marks(steps));
            // This PC on no network: said first among the steps.
            Assert(PhoneSetup.Steps(fresh with { Home = null })[1] is { Key: "network", Mark: PhoneSetup.Mark.Yours }, "No network");
            // A Jellyfin here reached at this PC's own address is this PC's.
            Assert(LocalNetwork.IsThisPc("127.0.0.1") && LocalNetwork.IsThisPc("localhost") && LocalNetwork.IsThisPc("[::1]") && !LocalNetwork.IsThisPc("192.0.2.1")
                && (LocalNetwork.ThisPc() is not { } own || LocalNetwork.IsThisPc(own)), "This PC's own addresses");
            return Task.CompletedTask;
        });
        await Test("Autres appareils : l’adresse du PC sur le réseau de la maison, pas celle d’un adaptateur virtuel", () =>
        {
            LocalNetwork.Candidate C(string address, bool gateway = true, bool isVirtual = false) => new(address, gateway, isVirtual);
            Assert(LocalNetwork.PreferredIPv4([C("172.24.80.1", isVirtual: true), C("169.254.10.2"), C("127.0.0.1"), C("192.168.1.20")]) == "192.168.1.20", "Home network address");
            Assert(LocalNetwork.PreferredIPv4([C("10.0.0.5", gateway: false), C("192.168.0.12")]) == "192.168.0.12", "An adapter with the box's gateway comes first");
            Assert(LocalNetwork.PreferredIPv4([C("fe80::1"), C("169.254.3.3")]) is null, "No usable address");
            Assert(LocalNetwork.ForOtherDevices("http://127.0.0.1:8096/", "192.168.1.20") == "http://192.168.1.20:8096"
                && LocalNetwork.ForOtherDevices("http://localhost:8096", "192.168.1.20") == "http://192.168.1.20:8096"
                && LocalNetwork.ForOtherDevices("https://media.example.org/jellyfin/", "192.168.1.20") == "https://media.example.org/jellyfin"
                && LocalNetwork.ForOtherDevices("http://127.0.0.1:8096/", null) is null && LocalNetwork.ForOtherDevices("pas une adresse", "192.168.1.20") is null, "Address for other devices");
            return Task.CompletedTask;
        });
        await Test("Mira web : vérifié sur la release signée de cette version de Mira avant que Jellyfin ne s’en serve", async () =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var version = new Version(0, 7, 5); var plugin = Encoding.UTF8.GetBytes("extension Mira 0.7.5");
            var files = new Dictionary<string, byte[]>(); var requested = new List<Uri>();
            void Publish(ECDsa signer, bool withPlugin = true, string? source = null)
            {
                files.Clear();
                UpdateFile Describe(string kind, string name, byte[] bytes) => new(kind, name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                var manifest = new UpdateManifest("Mira", version, [Describe("zip", "Mira-0.7.5-win-x64.zip", "zip"u8.ToArray())])
                { Others = withPlugin ? [Describe("mac", "Mira-0.7.5-mac-arm64.dmg", "dmg"u8.ToArray()), Describe("jellyfin", "mira-jellyfin-0.7.5.zip", plugin)] : [] };
                files["mira-update.json"] = manifest.ToJson();
                files["mira-update.json.sig"] = Encoding.ASCII.GetBytes(UpdateSignature.Encode(UpdateSignature.Sign(files["mira-update.json"], signer)));
                files["mira-jellyfin-0.7.5.zip"] = plugin;
                files["jellyfin-manifest.json"] = Encoding.UTF8.GetBytes(JellyfinRepository("0.7.5.0", source ?? "https://github.com/sasou-web/Mira/releases/download/v0.7.5/mira-jellyfin-0.7.5.zip", Convert.ToHexString(MD5.HashData(plugin)).ToLowerInvariant()));
            }
            // GitHub's API says whether the published release is immutable ("immutable": true, null: field absent).
            bool? immutable = true; Func<Uri, HttpResponseMessage?>? fault = null;
            using var handler = new Handler(request =>
            {
                var url = request.RequestUri!; requested.Add(url);
                if (fault?.Invoke(url) is { } faulted) return Task.FromResult(faulted);
                if (url.Host == "api.github.com" && url.AbsolutePath == "/repos/sasou-web/Mira/releases/tags/v0.7.5")
                    return Task.FromResult(files.Count == 0 ? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(immutable is { } on ? $$"""{"tag_name":"v0.7.5","draft":false,"immutable":{{(on ? "true" : "false")}}}""" : """{"tag_name":"v0.7.5"}""") });
                const string prefix = "/sasou-web/Mira/releases/download/v0.7.5/";
                return Task.FromResult(url.Host == "github.com" && url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) && files.TryGetValue(url.AbsolutePath[prefix.Length..], out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            using var github = new UpdateClient(version, [UpdateSignature.PublicKey(key)], handler: handler);
            async Task<string> Refusal() { try { await MiraWebRelease.VerifyAsync(github, version); return ""; } catch (UpdateException ex) { return ex.Message; } }
            Publish(key);
            Assert(await MiraWebRelease.VerifyAsync(github, version) == "https://github.com/sasou-web/Mira/releases/download/v0.7.5/jellyfin-manifest.json" && MiraWeb.RepositoryFor(version) == "https://github.com/sasou-web/Mira/releases/download/v0.7.5/jellyfin-manifest.json", "Signed release refused");
            Assert(requested[0] == new Uri("https://api.github.com/repos/sasou-web/Mira/releases/tags/v0.7.5")
                && requested.Skip(1).All(u => u.Scheme == "https" && u.Host == "github.com" && u.AbsolutePath.StartsWith("/sasou-web/Mira/releases/download/v0.7.5/", StringComparison.Ordinal)), "Another address was asked");
            // A release GitHub does not keep immutable: its files could be replaced after this check, and Jellyfin would take
            // the new ones. Refused, before anything else is downloaded.
            foreach (var state in new bool?[] { false, null })
            {
                immutable = state; requested.Clear();
                Assert((await Refusal()).Contains("peut encore être modifiée") && requested.Count == 1, "A mutable release accepted: " + state);
            }
            immutable = true;
            // GitHub's answers, told apart: unreachable (this PC's Internet), limiting requests (its own message), and files
            // larger than signed or sent elsewhere than GitHub (an altered release).
            foreach (var (what, failure, expected) in new (string, Func<Uri, HttpResponseMessage?>, string)[]
            {
                ("unreachable", _ => throw new HttpRequestException("no route"), "accès à Internet"),
                ("rate limit on the API", u => u.Host == "api.github.com" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null, "limite les vérifications"),
                ("rate limit on a file", u => u.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : null, "limite les vérifications"),
                ("larger plugin", u => u.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. plugin, .. plugin]) } : null, "ne correspond pas"),
                ("plugin sent elsewhere", u => u.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/mira.zip") } } : null, "ne correspond pas"),
                ("huge manifest", u => u.AbsolutePath.EndsWith("mira-update.json", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[70_000]) } : null, "ne correspond pas"),
            })
            {
                fault = failure; var message = await Refusal(); fault = null;
                Assert(message.Contains(expected), $"{what}: « {message} »");
            }
            Assert(await MiraWebRelease.VerifyAsync(github, version) == MiraWeb.RepositoryFor(version), "Signed release refused after the failures");
            // The plugin replaced on GitHub after signing, at the same size and with Jellyfin's MD5 updated to match: only the
            // signed SHA-256 tells, and it is refused.
            var forged = Encoding.UTF8.GetBytes("extension Miro 0.7.5");
            files["mira-jellyfin-0.7.5.zip"] = forged;
            files["jellyfin-manifest.json"] = Encoding.UTF8.GetBytes(JellyfinRepository("0.7.5.0", "https://github.com/sasou-web/Mira/releases/download/v0.7.5/mira-jellyfin-0.7.5.zip", Convert.ToHexString(MD5.HashData(forged)).ToLowerInvariant()));
            Assert((await Refusal()).Contains("ne correspond pas"), "A plugin altered after signing accepted");
            // Jellyfin's manifest sending it elsewhere (the latest release, which nobody checked): refused.
            Publish(key, source: "https://github.com/sasou-web/Mira/releases/latest/download/mira-jellyfin-0.7.5.zip");
            Assert((await Refusal()).Contains("ne correspond pas"), "A Jellyfin manifest pointing elsewhere accepted");
            Publish(key); files["jellyfin-manifest.json"] = Encoding.UTF8.GetBytes(JellyfinRepository("0.7.5.0", "https://github.com/sasou-web/Mira/releases/download/v0.7.5/mira-jellyfin-0.7.5.zip", new string('0', 32)));
            Assert((await Refusal()).Contains("ne correspond pas"), "A Jellyfin manifest with another MD5 accepted");
            // Signed by a key Mira does not know, or not signed at all.
            Publish(other);
            Assert((await Refusal()).Contains("pas signé"), "A release signed by another key accepted");
            Publish(key); files.Remove("mira-update.json.sig");
            Assert((await Refusal()).Contains("pas de Mira web signé"), "A release without its signature accepted");
            // A release without the plugin in its signed manifest (up to 0.7.4), or no release at all (a development copy).
            Publish(key, withPlugin: false);
            Assert((await Refusal()).Contains("pas de Mira web signé"), "A release signing Windows alone accepted for Mira web");
            files.Clear();
            Assert((await Refusal()).Contains("copie de développement"), "A missing release not said plainly");
            // Which repositories in Jellyfin are Mira's.
            foreach (var url in new[] { MiraWeb.LatestRepository, MiraWeb.RepositoryFor(new Version(0, 7, 4)), "https://github.com/Sasou-Web/mira/releases/latest/download/jellyfin-manifest.json" })
                Assert(MiraWeb.IsRepository(url), "Mira's repository not recognised: " + url);
            foreach (var url in new[] { "https://repo.jellyfin.org/files/plugin/manifest.json", "https://github.com/someone/Mira/releases/latest/download/jellyfin-manifest.json", "https://github.com.evil.example/sasou-web/Mira/releases/x", "file:///sasou-web/Mira/releases/", "", null })
                Assert(!MiraWeb.IsRepository(url), "Another repository taken for Mira's: " + url);
        });
        await Test("Mira web : au démarrage, le dépôt de Mira avance jusqu’à cette version vérifiée, jamais en arrière, sans rien installer", async () =>
        {
            var version = new Version(0, 7, 5); var pinned = MiraWeb.RepositoryFor(version);
            const string stable = """{"Name":"Jellyfin Stable","Url":"https://repo.jellyfin.org/files/plugin/manifest.json","Enabled":true}""";
            string Repositories(params string[] mira) => "[" + string.Join(",", [stable, .. mira, """{"Name":"Autre","Url":"https://example.org/manifest.json"}"""]) + "]";
            string Mira(string url, bool enabled = true) => $$"""{"Name":"Mira","Url":"{{url}}","Enabled":{{(enabled ? "true" : "false")}}}""";
            // Jellyfin's plugin list: Mira's id as Jellyfin writes it (no dashes), and its four-number version.
            string Plugins(params (string Version, string Status)[] mira) => "[" + string.Join(",", mira.Select(p => $$"""{"Name":"Mira","Version":"{{p.Version}}","Id":"{{MiraWeb.PluginId:N}}","Status":"{{p.Status}}"}""")
                .Append("""{"Name":"Autre","Version":"9.9.9.9","Id":"0f0c4f4b8e3a4d6f9f7a1b2c3d4e5f60","Status":"Active"}""")) + "]";
            string repositories = "", plugins = ""; var admin = true; var calls = new List<string>(); var verified = 0;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                var path = request.RequestUri!.AbsolutePath.TrimStart('/');
                calls.Add($"{request.Method} {path}");
                if (!admin) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (path == "Repositories" && request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(repositories, Encoding.UTF8, "application/json") };
                if (path == "Repositories" && request.Method == HttpMethod.Post) { repositories = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
                if (path == "Plugins" && request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(plugins, Encoding.UTF8, "application/json") };
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            Task<string> Verified() { verified++; return Task.FromResult(pinned); }
            List<string?> Urls() => JsonDocument.Parse(repositories).RootElement.EnumerateArray().Select(x => x.GetProperty("Url").GetString()).ToList();
            async Task<bool> Start(string registered, string installed)
            {
                repositories = registered; plugins = installed; calls.Clear(); verified = 0;
                var settled = await client.KeepWebAppCurrentAsync(version, Verified);
                // Whatever happens at startup: nothing installed, nothing restarted, Jellyfin's update task does the rest.
                Assert(calls.All(c => c is "GET Repositories" or "GET Plugins" or "POST Repositories"), "Startup did more than move the repository: " + string.Join(", ", calls));
                return settled;
            }
            // Mira 0.7.4 registered the latest release's repository; Mira web is still 0.7.4: the repository moves to 0.7.5's,
            // checked first, every other repository of Mira's removed, the others kept.
            Assert(await Start(Repositories(Mira(MiraWeb.LatestRepository), Mira(MiraWeb.RepositoryFor(new Version(0, 7, 4)), enabled: false)), Plugins(("0.7.4.0", "Active"))) && verified == 1, "Not moved forward");
            Assert(Urls().SequenceEqual(["https://repo.jellyfin.org/files/plugin/manifest.json", "https://example.org/manifest.json", pinned]), "Repositories: " + repositories);
            // Already there: nothing checked or saved again.
            Assert(await Start(Repositories(Mira(pinned)), Plugins(("0.7.4.0", "Active"))) && verified == 0 && !calls.Contains("POST Repositories"), "Saved again when already pinned");
            // Another PC's Mira, more recent, pinned 0.7.6: never back to 0.7.5.
            Assert(await Start(Repositories(Mira(MiraWeb.RepositoryFor(new Version(0, 7, 6)))), Plugins(("0.7.4.0", "Active"))) && verified == 0 && !calls.Contains("POST Repositories"), "Moved back from a newer pinned version");
            // Jellyfin already has this version or a newer one (from the latest release, or waiting for its restart).
            foreach (var installed in new[] { Plugins(("0.7.5.0", "Active")), Plugins(("0.7.6.0", "Active")), Plugins(("0.7.4.0", "Superceded"), ("0.7.6.0", "Restart")) })
                Assert(await Start(Repositories(Mira(MiraWeb.LatestRepository)), installed) && verified == 0 && !calls.Contains("POST Repositories"), "Repository changed though Jellyfin is up to date: " + installed);
            // The administrator's choices stand: a disabled or uninstalled plugin only sees its repository move (Jellyfin's task
            // then installs nothing); a disabled repository of Mira's, or none, is left alone.
            Assert(await Start(Repositories(Mira(MiraWeb.LatestRepository)), Plugins(("0.7.4.0", "Disabled"))) && Urls().Last() == pinned, "Disabled plugin: repository not moved");
            Assert(await Start(Repositories(Mira(MiraWeb.LatestRepository)), Plugins()) && Urls().Last() == pinned, "Uninstalled plugin: repository not moved");
            foreach (var registered in new[] { Repositories(Mira(MiraWeb.LatestRepository, enabled: false)), Repositories() })
                Assert(!await Start(registered, Plugins(("0.7.4.0", "Active"))) && verified == 0 && !calls.Contains("POST Repositories") && !calls.Contains("GET Plugins"), "Changed a server without Mira's repository: " + registered);
            // A failed check changes nothing; a guest account can change nothing.
            repositories = Repositories(Mira(MiraWeb.LatestRepository)); plugins = Plugins(("0.7.4.0", "Active")); var before = repositories;
            var thrown = false; try { await client.KeepWebAppCurrentAsync(version, () => throw new UpdateException("non signé")); } catch (UpdateException) { thrown = true; }
            Assert(thrown && repositories == before, "Repository moved without a checked release");
            admin = false;
            Assert(!await client.KeepWebAppCurrentAsync(version, Verified) && repositories == before, "A guest changed Jellyfin's repositories");
            // The version a repository offers alone.
            Assert(MiraWeb.PinnedVersion(pinned) == version && MiraWeb.PinnedVersion(MiraWeb.LatestRepository) is null && MiraWeb.PinnedVersion("https://example.org/releases/download/v9.9.9/jellyfin-manifest.json") is null
                && MiraWeb.PinnedVersion("https://github.com/sasou-web/Mira/releases/download/v0.7.6/mira-jellyfin-0.7.6.zip") is null, "Pinned versions read wrong");
        });
    }
}
