using System.IO;
using System.Text.Json.Nodes;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Inviter par lien »: what a guest's page may ask of Jellyfin through the extension's gateway (GuestRoutes), and the
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
    }
}
