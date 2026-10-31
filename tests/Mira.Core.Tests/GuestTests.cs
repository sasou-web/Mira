using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Inviter par lien »: what a guest's page may ask of Jellyfin through the extension's gateway (GuestRoutes), what a
/// request from Tailscale Funnel may reach at all, the links' address tried as from the Internet (TunnelCheck), and the
/// links themselves (GuestInvites, on a clock of their own): one device each, taken back, gone after a week unused.
/// </summary>
internal static class GuestTests
{
    private const string Item = "90892981f28a0ccb86a6e55a3a0039c6", Dashed = "90892981-f28a-0ccb-86a6-e55a3a0039c6", Source = "0123456789abcdef0123456789abcdef";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Inviter par lien : la passerelle ne laisse passer que la séance", () =>
        {
            static GuestRoutes.Access For(string method, string path, string? seriesId = null, string? size = null) =>
                GuestRoutes.For(method, path, name => name == "seriesId" ? seriesId : name == "size" ? size : null);
            var open = new (string Method, string Path, GuestRoutes.Access Access)[]
            {
                ("GET", "System/Info/Public", GuestRoutes.Access.Public), ("GET", "System/Ping", GuestRoutes.Access.Public),
                ("GET", $"Items/{Item}", GuestRoutes.Access.Guest), ("GET", $"Items/{Dashed}/Images/Primary", GuestRoutes.Access.Guest),
                ("GET", $"Items/{Item}/Images/Backdrop/0", GuestRoutes.Access.Guest), ("POST", $"Items/{Item}/PlaybackInfo", GuestRoutes.Access.Guest),
                ("GET", $"Videos/{Item}/stream.mkv", GuestRoutes.Access.Guest), ("HEAD", $"videos/{Item}/stream", GuestRoutes.Access.Guest),
                ("GET", $"videos/{Dashed}/master.m3u8", GuestRoutes.Access.Guest), ("GET", $"videos/{Item}/main.m3u8", GuestRoutes.Access.Guest),
                ("GET", $"videos/{Item}/hls1/main/-1.mp4", GuestRoutes.Access.Guest), ("GET", $"videos/{Item}/hls1/main/12.ts", GuestRoutes.Access.Guest),
                ("GET", $"Videos/{Item}/{Source}/Subtitles/3/0/Stream.vtt", GuestRoutes.Access.Guest), ("GET", $"Videos/{Item}/{Source}/Subtitles/3/subtitles.m3u8", GuestRoutes.Access.Guest),
                ("GET", $"Videos/{Item}/Trickplay/320/4.jpg", GuestRoutes.Access.Guest), ("DELETE", "Videos/ActiveEncodings", GuestRoutes.Access.Guest),
                ("GET", $"Mira/hls/{Item}/master.m3u8", GuestRoutes.Access.Guest), ("GET", $"Mira/hls/{Item}/{Source}/Subtitles/2/subtitles.m3u8", GuestRoutes.Access.Guest),
                ("GET", $"Shows/{Item}/Episodes", GuestRoutes.Access.Guest), ("GET", $"MediaSegments/{Item}", GuestRoutes.Access.Guest),
                ("POST", "Sessions/Playing", GuestRoutes.Access.Guest), ("POST", "Sessions/Playing/Progress", GuestRoutes.Access.Guest),
                ("POST", "Sessions/Playing/Stopped", GuestRoutes.Access.Guest), ("POST", "Sessions/Capabilities", GuestRoutes.Access.Guest),
                ("GET", "GetUtcTime", GuestRoutes.Access.Guest), ("GET", "SyncPlay/List", GuestRoutes.Access.Groups),
                ("GET", $"SyncPlay/{Dashed}", GuestRoutes.Access.Group), ("POST", "SyncPlay/Join", GuestRoutes.Access.Join),
                ("POST", "SyncPlay/Ready", GuestRoutes.Access.Guest), ("POST", "SyncPlay/SetIgnoreWait", GuestRoutes.Access.Guest),
            };
            foreach (var (method, path, access) in open)
                Assert(For(method, path) == access, $"{method} {path}: {For(method, path)}");
            Assert(For("GET", "Shows/NextUp", seriesId: Item) == GuestRoutes.Access.Guest && For("GET", "Shows/NextUp") == GuestRoutes.Access.None, "Next up: of one series only");
            Assert(For("GET", "Playback/BitrateTest", size: "4000000") == GuestRoutes.Access.Guest && For("GET", "Playback/BitrateTest", size: "900000000") == GuestRoutes.Access.None
                && For("GET", "Playback/BitrateTest") == GuestRoutes.Access.None, "Connection test: 10 MB at most");
            // The library, the accounts, the server's settings, the queue, downloads: never.
            foreach (var (method, path) in new[]
            {
                ("GET", "Items"), ("GET", "Users"), ("GET", "Users/Public"), ("POST", "Users/AuthenticateByName"), ("GET", "Library/VirtualFolders"),
                ("GET", $"Items/{Item}/Similar"), ("GET", $"Items/{Item}/Download"), ("GET", $"Items/{Item}/File"), ("DELETE", $"Items/{Item}"),
                ("GET", "UserViews"), ("GET", "Sessions"), ("POST", "SyncPlay/New"), ("POST", "SyncPlay/SetNewQueue"), ("POST", "SyncPlay/Queue"),
                ("GET", "System/Info"), ("GET", "System/Configuration"), ("GET", "Mira/torlink"), ("GET", "Mira/invites"), ("POST", "Mira/guest/open"),
                ("GET", $"Items/{Item}/../../Users"), ("GET", $"Items/{Item}%2f..%2fUsers"), ("GET", "Items/not-an-id"), ("GET", $"Videos/{Item}/stream.mkv/x"),
                ("PUT", $"Items/{Item}"), ("POST", $"Items/{Item}"), ("GET", ""), ("GET", "/Users"), ("GET", $"Items//{Item}"),
            })
                Assert(For(method, path) == GuestRoutes.Access.None, $"{method} {path} let through");
            Assert(GuestRoutes.IsId(Item) && GuestRoutes.IsId(Dashed) && !GuestRoutes.IsId("x") && !GuestRoutes.IsId(null), "Ids");
            return Task.CompletedTask;
        });

        await Test("Inviter par lien : par Funnel, seulement la page et la passerelle", () =>
        {
            static GuestRoutes.Tunnel Through(string method, string path, string baseUrl = "", bool upgrade = false) => GuestRoutes.ThroughTunnel(method, path, baseUrl, upgrade);
            foreach (var (method, path) in new[] { ("GET", "/Mira"), ("GET", "/Mira/"), ("HEAD", "/Mira/"), ("GET", "/Mira/manifest.webmanifest"), ("GET", "/mira/v/abc/js/app.js") })
                Assert(Through(method, path) == GuestRoutes.Tunnel.Page, $"{method} {path}: {Through(method, path)}");
            foreach (var (method, path) in new[] { ("POST", "/Mira/guest/open"), ("GET", $"/Mira/guest/Items/{Item}"), ("DELETE", "/Mira/guest/Videos/ActiveEncodings"), ("GET", "/MIRA/GUEST/System/Info/Public") })
                Assert(Through(method, path) == GuestRoutes.Tunnel.Gateway, $"{method} {path}: {Through(method, path)}");
            Assert(Through("GET", "/Mira/guest/socket", upgrade: true) == GuestRoutes.Tunnel.Socket, "WebSocket at the gateway: for a guest's session only");
            Assert(Through("GET", "/jellyfin/Mira/guest/Users/Me", "/jellyfin") == GuestRoutes.Tunnel.Gateway && Through("GET", "/jellyfin/Mira/", "/jellyfin/") == GuestRoutes.Tunnel.Page
                && Through("GET", "/Mira/", "/jellyfin") == GuestRoutes.Tunnel.None, "Under a base URL");
            // Jellyfin itself, the extension's other parts, a WebSocket anywhere else, the page written to: never.
            foreach (var (method, path, upgrade) in new[]
            {
                ("GET", "/System/Info/Public", false), ("GET", "/web/", false), ("GET", "/Users/Public", false), ("POST", "/Users/AuthenticateByName", false), ("GET", "/", false), ("GET", "", false),
                ("GET", "/Mira/invites", false), ("PUT", "/Mira/invites/address", false), ("GET", "/Mira/torlink", false), ("GET", $"/Mira/hls/{Item}/master.m3u8", false), ("GET", "/Mira/subtitles", false),
                ("GET", "/Mira/identify/image", false), ("POST", "/Mira/", false), ("GET", "/Mira/guest", false), ("GET", "/Miraguest/x", false), ("GET", "/Mira%2Fguest/x", false), ("GET", "/Mira/guest%2F..%2FSystem", false),
                ("GET", "/socket", true), ("GET", "/Mira/socket", true), ("GET", "/Mira/", true), ("GET", "/Mira/v/x/socket", true),
            })
                Assert(Through(method, path, upgrade: upgrade) == GuestRoutes.Tunnel.None, $"{method} {path}{(upgrade ? " (WebSocket)" : "")} let through");
            Assert(GuestRoutes.FunnelHeader == "Tailscale-Funnel-Request", "Tailscale's own header (ipn/ipnlocal/serve.go)");
            return Task.CompletedTask;
        });

        await Test("Inviter par lien : l’adresse des liens essayée comme depuis Internet", async () =>
        {
            const string Me = "36b069f8-2dc3-4050-a793-0eb75a7373ea";
            static string Card(string id) => $"{{\"ServerName\":\"pc\",\"Version\":\"12.1.0\",\"Id\":\"{id}\"}}";
            // A stand-in for the address: each request line kept as it came, a card where `answer` gives one.
            static async Task<(TunnelCheck.Result Result, List<string> Targets)> TryAsync(Func<string, string?> answer, bool listening = true)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (!listening) listener.Stop();
                var targets = new List<string>();
                using var stop = new CancellationTokenSource();
                var serving = !listening ? Task.CompletedTask : Task.Run(async () =>
                {
                    while (true)
                    {
                        TcpClient connection;
                        try { connection = await listener.AcceptTcpClientAsync(stop.Token); } catch (OperationCanceledException) { return; }
                        _ = Task.Run(async () =>
                        {
                            using var held = connection; var stream = connection.GetStream(); var reader = new StreamReader(stream, Encoding.ASCII);
                            var line = await reader.ReadLineAsync() ?? ""; while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                            var target = line.Split(' ') is [_, var t, ..] ? t : ""; lock (targets) targets.Add(target);
                            var body = answer(target);
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(body is null ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                : $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"));
                        });
                    }
                });
                using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
                var result = await TunnelCheck.RunAsync(client, $"http://127.0.0.1:{port}/", Me, DateTimeOffset.UnixEpoch, CancellationToken.None);
                stop.Cancel(); await serving; listener.Stop();
                lock (targets) return (result, [.. targets]);
            }
            // What Jellyfin's server makes of a path: « %2e » read as « . », then « . » and « .. » gone.
            static string Normalized(string target) => new Uri("http://x" + target.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase)).AbsolutePath;

            // Funnel opened for /Mira only: Mira at the gateway, nothing else; each path sent as written.
            var (closed, sent) = await TryAsync(target => target == TunnelCheck.Inside ? Card(Me.Replace("-", "", StringComparison.Ordinal)) : null);
            Assert(closed.State == TunnelCheck.State.Closed && closed.Path is null, $"Only Mira: {closed}");
            Assert(TunnelCheck.Detours.All(sent.Contains) && sent.Contains(TunnelCheck.Inside) && sent.Contains("/Mira/%2e%2e/System/Info/Public") && sent.Contains("/Mira/../System/Info/Public"),
                "Sent as written: " + string.Join(" ", sent));
            // Funnel opened for all of Jellyfin, or a tunnel that hands « /Mira/%2e%2e/… » on as it is to Jellyfin.
            var (whole, _) = await TryAsync(target => Normalized(target) is "/System/Info/Public" or "/Mira/guest/System/Info/Public" ? Card(Me) : null);
            Assert(whole.State == TunnelCheck.State.Open && whole.Path == "/System/Info/Public", $"All of Jellyfin: {whole}");
            var (dotted, _) = await TryAsync(target => target == TunnelCheck.Inside || target == "/Mira/%2e%2e/System/Info/Public" ? Card(Me) : null);
            Assert(dotted.State == TunnelCheck.State.Open && dotted.Path == "/Mira/%2e%2e/System/Info/Public", $"A path out of /Mira: {dotted}");
            // Another server there, or none.
            var (other, _) = await TryAsync(target => Normalized(target).EndsWith("/System/Info/Public", StringComparison.Ordinal) ? Card("0123456789abcdef0123456789abcdef") : null);
            Assert(other.State == TunnelCheck.State.Unreachable, $"Another Jellyfin: {other}");
            var (none, _) = await TryAsync(_ => null, listening: false);
            Assert(none.State == TunnelCheck.State.Unreachable, $"Nothing there: {none}");

            Assert(TunnelCheck.ServerId(Card(Me)) == Me && TunnelCheck.ServerId("<html>") is null && TunnelCheck.ServerId("[1]") is null && TunnelCheck.ServerId("{\"Id\":3}") is null, "Card read");
            var at = DateTimeOffset.Parse("2026-10-29T10:00:00Z");
            TunnelCheck.Result Was(TunnelCheck.State state) => new("https://pc.ts.net", state, at);
            Assert(TunnelCheck.Stale(null, at, TimeSpan.FromHours(6), TimeSpan.FromMinutes(1)) && !TunnelCheck.Stale(Was(TunnelCheck.State.Closed), at.AddHours(5), TimeSpan.FromHours(6), TimeSpan.FromMinutes(1))
                && TunnelCheck.Stale(Was(TunnelCheck.State.Unreachable), at.AddMinutes(2), TimeSpan.FromHours(6), TimeSpan.FromMinutes(1))
                && TunnelCheck.Stale(Was(TunnelCheck.State.Open), at, TimeSpan.FromMinutes(10), TimeSpan.Zero), "Tried again when due");
            TunnelCheck.Keep(Was(TunnelCheck.State.Closed));
            Assert(TunnelCheck.Last("https://pc.ts.net") is { State: TunnelCheck.State.Closed } && TunnelCheck.Last("https://autre.ts.net") is null && TunnelCheck.Last(null) is null, "Kept for its address");
            var open = new TunnelCheck.Result("https://pc.ts.net", TunnelCheck.State.Open, at, "/Mira/%2e%2e/System/Info/Public");
            Assert(TunnelCheck.Text(open, true).Contains("https://pc.ts.net/Mira/%2e%2e/System/Info/Public", StringComparison.Ordinal) && TunnelCheck.Text(open, false).StartsWith("The tunnel lets more than Mira through", StringComparison.Ordinal)
                && TunnelCheck.Text(Was(TunnelCheck.State.Closed), true).StartsWith("Adresse vérifiée", StringComparison.Ordinal) && TunnelCheck.Text(Was(TunnelCheck.State.Unreachable), false).StartsWith("Mira doesn’t answer", StringComparison.Ordinal), "Texts");
        });

        await Test("Inviter par lien : les playlists passent par la passerelle", () =>
        {
            var master = "#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"/jf/Mira/hls/a/b/Subtitles/3/subtitles.m3u8?ApiKey=t\"\n#EXT-X-STREAM-INF:BANDWIDTH=1\n/jf/videos/a/main.m3u8?ApiKey=t\n";
            var through = GuestRoutes.Playlist(master, "/jf");
            Assert(through.Contains("URI=\"/jf/Mira/guest/Mira/hls/a/b/Subtitles/3/subtitles.m3u8?ApiKey=t\"", StringComparison.Ordinal)
                && through.Contains("\n/jf/Mira/guest/videos/a/main.m3u8?ApiKey=t\n", StringComparison.Ordinal), "Absolute addresses through the gateway: " + through);
            Assert(GuestRoutes.Playlist(through, "/jf") == through, "Already through: unchanged");
            var relative = "#EXTM3U\r\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?x=1\"\r\nhls1/main/0.mp4?x=1\r\nhttps://elsewhere.example/x.mp4\r\n//elsewhere/x\r\n";
            Assert(GuestRoutes.Playlist(relative, "") == relative.Replace("\r\n", "\n", StringComparison.Ordinal), "Relative and other addresses: as they are");
            Assert(GuestRoutes.Playlist("#EXTM3U\n/videos/a/main.m3u8\n", "") == "#EXTM3U\n/Mira/guest/videos/a/main.m3u8\n", "Without a base URL");
            return Task.CompletedTask;
        });

        await Test("Inviter par lien : seulement les groupes de l'hôte, un compte d'invité sans rien d'autre", () =>
        {
            static JsonNode Group(string name, params string[] people) => new JsonObject { ["GroupId"] = "g", ["GroupName"] = name, ["Participants"] = new JsonArray([.. people.Select(x => (JsonNode)x)]) };
            Assert(GuestRoutes.HostsGroup(Group("Groupe de ogsas"), "ogsas") && GuestRoutes.HostsGroup(Group("ogsas’s group"), "ogsas") && GuestRoutes.HostsGroup(Group("ogsas's group"), "ogsas"), "Named after the host, in either language");
            Assert(GuestRoutes.HostsGroup(Group("Soirée", "marc", "ogsas"), "ogsas") && !GuestRoutes.HostsGroup(Group("Groupe de marc", "marc"), "ogsas")
                && !GuestRoutes.HostsGroup(Group("Groupe de ogsas2"), "ogsas") && !GuestRoutes.HostsGroup(null, "ogsas") && !GuestRoutes.HostsGroup(Group("Groupe de "), ""), "Another's group");
            var policy = new JsonObject
            {
                ["IsAdministrator"] = true, ["EnableAllFolders"] = false, ["EnableContentDownloading"] = true, ["EnableContentDeletion"] = true,
                ["EnableRemoteControlOfOtherUsers"] = true, ["SyncPlayAccess"] = "CreateAndJoinGroups", ["MaxActiveSessions"] = 0, ["AuthenticationProviderId"] = "Default",
                ["EnableContentDeletionFromFolders"] = new JsonArray("a"),
            };
            var guest = GuestRoutes.Policy(policy);
            Assert(guest["IsAdministrator"]!.GetValue<bool>() == false && guest["IsHidden"]!.GetValue<bool>() && guest["EnableAllFolders"]!.GetValue<bool>()
                && guest["EnableAllDevices"]!.GetValue<bool>() && !guest["EnableContentDownloading"]!.GetValue<bool>() && !guest["EnableContentDeletion"]!.GetValue<bool>()
                && !guest["EnableRemoteControlOfOtherUsers"]!.GetValue<bool>() && !guest["EnableUserPreferenceAccess"]!.GetValue<bool>() && !guest["EnableSyncTranscoding"]!.GetValue<bool>()
                && guest["SyncPlayAccess"]!.GetValue<string>() == "JoinGroups" && guest["MaxActiveSessions"]!.GetValue<int>() == 1
                && guest["AuthenticationProviderId"]!.GetValue<string>() == "Default" && ((JsonArray)guest["EnableContentDeletionFromFolders"]!).Count == 0, "Guest policy: " + guest.ToJsonString());
            Assert(policy["IsAdministrator"]!.GetValue<bool>(), "Jellyfin's policy left as it was");
            Assert(GuestRoutes.GuestName(" Léa ", "Invité", ["admin"]) == "Léa" && GuestRoutes.GuestName("", "Invité", ["invité"]) == "Invité 2"
                && GuestRoutes.GuestName("Léa", "Invité", ["léa", "Léa 2"]) == "Léa 3" && GuestRoutes.GuestName("Léa ’O", "Invité", []) == "Léa 'O"
                && GuestRoutes.GuestName("a/b", "Invité", []) is null && GuestRoutes.GuestName(new string('a', 41), "Invité", []) is null, "Names");
            var code = GuestRoutes.NewCode();
            Assert(GuestRoutes.IsCode(code) && code != GuestRoutes.NewCode() && !GuestRoutes.IsCode(code + "a") && !GuestRoutes.IsCode("aaaaaaaaaaaaaaaaaaaaa/") && !GuestRoutes.IsCode(null), "Codes");
            Assert(GuestRoutes.TokenHash("abc") == GuestRoutes.TokenHash("abc") && GuestRoutes.TokenHash("abc") != GuestRoutes.TokenHash("abd") && !GuestRoutes.TokenHash("abc").Contains("abc", StringComparison.Ordinal), "Tokens kept by their hash");
            return Task.CompletedTask;
        });

        await Test("Inviter par lien : un appareil par lien, retiré à la main ou après une semaine sans servir", () =>
        {
            var now = DateTimeOffset.Parse("2026-10-23T20:00:00Z");
            var file = Path.Combine(run.Root, "guests", "Mira.invites.json");
            var invites = new GuestInvites(file, () => now);
            Assert(invites.Address is null && invites.SetAddress("https://PC.tail1.ts.net/Mira/") && invites.Address == "https://pc.tail1.ts.net", "Address");
            foreach (var bad in new[] { "http://pc.tail1.ts.net", "https://100.64.0.1", "https://pc.ts.net/autre", "https://u:p@pc.ts.net", "https://pc.ts.net/?x=1", "pc.ts.net" })
                Assert(!invites.SetAddress(bad) && invites.Address == "https://pc.tail1.ts.net", "Address refused: " + bad);
            Assert(GuestInvites.Link(invites.Address, "abc") == "https://pc.tail1.ts.net/Mira/#/invite/abc" && GuestInvites.Link(null, "abc") is null, "Link");

            var lea = invites.Add("Léa", Item, "secret", "host-id", "ogsas");
            Assert(invites.Open(lea.Code, "device-a", "1.2.3.4") is (GuestInvites.Opening.Opened, { Name: "Léa" }), "Opened");
            Assert(invites.ByToken("token-a") is null, "No session before Jellyfin gave one");
            Assert(invites.Opened(lea.Id, "device-a", "iPhone", "token-a") && invites.ByToken("token-a") is { Id: var found, Device: "device-a" } && found == lea.Id, "The device's session");
            Assert(invites.Open(lea.Code, "device-b", "1.2.3.4").Result == GuestInvites.Opening.OtherDevice && !invites.Opened(lea.Id, "device-b", "Chrome", "token-b"), "Another device refused");
            Assert(invites.Open(lea.Code, "device-a", "1.2.3.4").Result == GuestInvites.Opening.Opened && invites.Opened(lea.Id, "device-a", "iPhone", "token-a2")
                && invites.ByToken("token-a") is null && invites.ByToken("token-a2") is not null, "The same device again: its new session only");
            Assert(invites.Open("AAAAAAAAAAAAAAAAAAAAAA", "device-a", "1.2.3.4").Result == GuestInvites.Opening.Unknown && invites.Open("x", "device-a", "1.2.3.4").Result == GuestInvites.Opening.Unknown, "Unknown code");
            Assert(!File.ReadAllText(file).Contains("token-a2", StringComparison.Ordinal), "Session kept by its hash only");

            // Kept through a restart of Jellyfin.
            var again = new GuestInvites(file, () => now);
            Assert(again.Address == "https://pc.tail1.ts.net" && again.ByToken("token-a2") is { Name: "Léa" } && again.All().Single().DeviceName == "iPhone", "Read back");

            // In use, its end moves; unused for a week, it stops and its account is to delete.
            now = now.Add(GuestInvites.Unused - TimeSpan.FromMinutes(1));
            Assert(invites.ByToken("token-a2") is not null && invites.Sweep().Count == 0, "Used just in time");
            now = now.Add(GuestInvites.Unused - TimeSpan.FromMinutes(1));
            Assert(invites.Sweep().Count == 0 && invites.All().Count == 1, "A week after its last use, not after it was made");
            now = now.AddMinutes(2);
            Assert(invites.ByToken("token-a2") is null && invites.Open(lea.Code, "device-a", "1.2.3.4").Result == GuestInvites.Opening.Unknown, "Gone unused: refused");
            Assert(invites.Sweep().SequenceEqual([Item]) && invites.All().Count == 0, "Its account to delete");
            Assert(invites.Sweep().SequenceEqual([Item]), "Still to delete until Jellyfin did");
            invites.Deleted(Item);
            Assert(invites.Sweep().Count == 0, "Deleted");

            // Taken back by the host: at once.
            var marc = invites.Add("Marc", Dashed, "secret", "host-id", "ogsas");
            invites.Opened(marc.Id, "device-m", "Android", "token-m");
            Assert(invites.Remove(marc.Id) is { Name: "Marc" } && invites.ByToken("token-m") is null && invites.Remove(marc.Id) is null && invites.Sweep().SequenceEqual([Dashed]), "Taken back");

            // Too many tries: from one address, then from all.
            var tries = new GuestInvites(null, () => now);
            for (var i = 0; i < GuestInvites.TriesPerAddress; i++) tries.Open("x", "d", "5.6.7.8");
            Assert(tries.Open("x", "d", "5.6.7.8").Result == GuestInvites.Opening.TooMany && tries.Open("x", "d", "9.9.9.9").Result == GuestInvites.Opening.Unknown, "Ten a minute from one address");
            now = now.AddMinutes(1);
            Assert(tries.Open("x", "d", "5.6.7.8").Result == GuestInvites.Opening.Unknown, "A minute later");
            for (var i = 0; i < GuestInvites.TriesInAll; i++) tries.Open("x", "d", $"10.0.0.{i}");
            Assert(tries.Open("x", "d", "10.1.1.1").Result == GuestInvites.Opening.TooMany, "Sixty a minute in all");

            // An unreadable file is put aside, the links start again.
            File.WriteAllText(file, "{ not json");
            var fresh = new GuestInvites(file, () => now);
            Assert(fresh.All().Count == 0 && Directory.GetFiles(Path.GetDirectoryName(file)!, "Mira.invites.json.bad-*").Length == 1, "Unreadable file put aside");
            return Task.CompletedTask;
        });

        await Test("Ta party : un lien par hôte, un invité par appareil, refait = les anciens partent", () =>
        {
            var now = new DateTimeOffset(2026, 10, 31, 1, 0, 0, TimeSpan.Zero);
            var file = Path.Combine(run.Root, "party", "Mira.invites.json");
            var invites = new GuestInvites(file, () => now);
            var party = invites.PartyOf("host-id", "ogsas");
            Assert(GuestRoutes.IsCode(party.Code) && invites.PartyOf("host-id", "ogsas").Code == party.Code, "The same link each time");
            Assert(invites.PartyOf("other", "Rayro").Code != party.Code, "One per host");
            Assert(new GuestInvites(file, () => now).Party(party.Code) is { HostName: "ogsas" }, "Kept through a restart");
            Assert(invites.Party("x") is null && invites.Party(GuestRoutes.NewCode()) is null, "Any other code: none");

            Assert(invites.OpenParty(party.Code, "device-a", "1.2.3.4") is { Result: GuestInvites.Opening.Join, Invite: null, Party.HostId: "host-id" }, "A new device: a guest to make");
            var lea = invites.Add("Léa", Item, "secret", "host-id", "ogsas", party.Code, "device-a");
            Assert(invites.OpenParty(party.Code, "device-a", "1.2.3.4") is { Result: GuestInvites.Opening.Opened, Invite.Id: var id } && id == lea.Id, "The same device: its guest again");
            Assert(invites.OpenParty(party.Code, "device-b", "1.2.3.4").Result == GuestInvites.Opening.Join, "Another device: one more guest");
            Assert(invites.Opened(lea.Id, "device-b", "Chrome", "t") == false && invites.Opened(lea.Id, "device-a", "iPhone", "token-a"), "A guest stays its device's");
            Assert(invites.Guests(party.Code).Count == 1 && invites.Guests(invites.PartyOf("other", "Rayro").Code).Count == 0, "Guests of each party");
            Assert(invites.OpenParty(GuestRoutes.NewCode(), "device-a", "1.2.3.4").Result == GuestInvites.Opening.Unknown, "Unknown party");

            for (var i = 1; i < GuestInvites.MaxPartyGuests; i++) invites.Add($"G{i}", Guid.NewGuid().ToString("N"), "s", "host-id", "ogsas", party.Code, $"device-{i}");
            Assert(invites.OpenParty(party.Code, "device-z", "4.4.4.4").Result == GuestInvites.Opening.Full, "Full");

            // Made again: the old link stops, its guests' accounts go.
            var again = invites.NewParty("host-id", "ogsas");
            Assert(again.Code != party.Code && invites.Party(party.Code) is null && invites.ByToken("token-a") is null && invites.Guests(again.Code).Count == 0, "Old link and guests gone");
            Assert(invites.Sweep().Count == GuestInvites.MaxPartyGuests && invites.Sweep().Contains(Item), "Their accounts to delete");
            Assert(invites.PartyOf("host-id", "ogsas-renamed") is { HostName: "ogsas-renamed" } p && p.Code == again.Code, "A host renamed keeps the link");
            return Task.CompletedTask;
        });

        await Test("Ta party : la page, sa carte et le lien des apps", () =>
        {
            Assert(GuestRoutes.ThroughTunnel("GET", "/Mira/party/" + GuestRoutes.NewCode(), "", false) == GuestRoutes.Tunnel.Page
                && GuestRoutes.ThroughTunnel("GET", "/Mira/party/" + GuestRoutes.NewCode() + "/image", "", false) == GuestRoutes.Tunnel.Page, "The page and its image through Funnel");
            foreach (var path in new[] { "/Mira/party", "/Mira/party/", "/Mira/party/new", "/Mira/party/" + GuestRoutes.NewCode() + "/x", "/Mira/party/../invites" })
                Assert(GuestRoutes.ThroughTunnel("GET", path, "", false) == GuestRoutes.Tunnel.None, $"{path} let through");
            Assert(GuestRoutes.ThroughTunnel("POST", "/Mira/party/new", "", false) == GuestRoutes.Tunnel.None, "Never the host's own calls");

            var sessions = JsonNode.Parse($$"""
                [ { "UserId": "{{Source}}", "NowPlayingItem": { "Id": "{{Item}}", "Name": "Other" } },
                  { "UserId": "{{Dashed}}" },
                  { "UserId": "{{Item}}", "NowPlayingItem": { "Id": "{{Source}}", "Type": "Episode", "Name": "Welcome", "SeriesName": "Arcane", "SeriesId": "{{Dashed}}", "ParentIndexNumber": 1, "IndexNumber": 3 } } ]
                """);
            Assert(PartyPage.WatchingOf(sessions, Dashed) == new PartyPage.Watching("Arcane · S1 E3", Dashed), $"An episode: {PartyPage.WatchingOf(sessions, Dashed)}");
            Assert(PartyPage.WatchingOf(sessions, Source) == new PartyPage.Watching("Other", Item) && PartyPage.WatchingOf(sessions, "nobody") is null && PartyPage.WatchingOf(null, Item) is null, "A film, nothing");
            Assert(PartyPage.Description("ogsas", null) == "ogsas invites you to watch together in Mira."
                && PartyPage.Description("ogsas", new("Dune", Item)) == "ogsas is watching Dune. Join in Mira.", "The card's line");
            Assert(PartyPage.Link("https://pc.tail.ts.net", "abc") == "https://pc.tail.ts.net/Mira/party/abc"
                && PartyPage.AppLink("http://100.1.2.3:8096/", "abc", "srv") == "mira://party/abc?server=http%3A%2F%2F100.1.2.3%3A8096&id=srv", "Links");
            Assert(PartyPage.Origin(true, "https", "pc.tail.ts.net", "http", "pc.tail.ts.net", "") == "https://pc.tail.ts.net"
                && PartyPage.Origin(true, "https", null, "http", "pc.tail.ts.net", "/jf") == "https://pc.tail.ts.net/jf", "Behind Tailscale Serve or Funnel: https, never port 80");
            Assert(PartyPage.Origin(false, "https", "evil.example", "http", "192.168.1.2:8096", "") == "http://192.168.1.2:8096"
                && PartyPage.Origin(true, null, "pc.tail.ts.net", "http", "127.0.0.1:8096", "") == "http://127.0.0.1:8096"
                && PartyPage.Origin(true, "https", "a/b@c", "http", "127.0.0.1:8096", "") == "http://127.0.0.1:8096", "Anyone else's forwarded headers, or a strange name: the request's own address");
            Assert(PartyPage.Render("<p title=\"{{a}}\">{{a}} {{b}}</p>", new Dictionary<string, string?> { ["a"] = "<x\" & y>", ["b"] = null }) == "<p title=\"&lt;x&quot; &amp; y&gt;\">&lt;x&quot; &amp; y&gt; </p>", "Written for HTML");
            Assert(PartyPage.TailnetAddress([IPAddress.Parse("192.168.1.2"), IPAddress.Parse("100.63.1.1"), IPAddress.Parse("100.114.247.13")]) == "100.114.247.13"
                && PartyPage.TailnetAddress([IPAddress.Parse("100.128.0.1")]) is null, "Tailscale's range only");

            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Mira.sln"))) repository = repository.Parent;
            var files = WebFiles.FromFolder(Path.Combine((repository ?? new DirectoryInfo(Environment.CurrentDirectory)).FullName, "src", "Mira.Web"));
            Assert(files.TryGet("party.html", out var page) && System.Text.Encoding.UTF8.GetString(page.Content) is var html
                && html.Contains($"v/{files.Revision}/js/party.js", StringComparison.Ordinal) && html.Contains("{{description}}", StringComparison.Ordinal)
                && ContentPolicy.StyleHashes(html).Count == 1, "The template, its revision written");
            return Task.CompletedTask;
        });
    }
}
