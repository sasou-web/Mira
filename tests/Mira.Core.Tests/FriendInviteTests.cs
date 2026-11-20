using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Inviter un ami » by a link (FriendInvites, the policy for friends' tag): an invitation used once, refused when used,
/// ended, cancelled or wrong, tries counted, given back when Jellyfin opened nothing; friends' tag limited to Jellyfin
/// like the home's, added to the policy by Mira without touching anything else; friends' nodes kept by the sweep and
/// deleted when their device is taken out; /Mira/friend closed to Funnel.
/// </summary>
internal static class FriendInviteTests
{
    private const string Server = "100.88.12.34";
    private const string Alex = "0123456789abcdef0123456789abcdef";
    private const string Sam = "fedcba9876543210fedcba9876543210";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Inviter un ami par lien : l’invitation ne sert qu’une fois", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
            var file = Path.Combine(run.Root, "friends", "Mira.friends.json");
            var friends = new FriendInvites(file, () => now);
            var (token, invite) = friends.Invite(Alex, "Alex", "kKey1", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            Assert(token.Length >= 20 && invite.Token != token && !File.ReadAllText(file).Contains(token, StringComparison.Ordinal), "Only the token's hash is kept");

            var (result, used, device, replaced) = friends.Redeem(token, "device-alex-phone", "iPhone d’Alex", "100.101.8.8");
            Assert(result == FriendInvites.Redeeming.Done && used!.User == Alex && device is { Tailnet: "100.101.8.8", User: Alex } && replaced is null, "Used once, from the tailnet");
            Assert(FriendInvites.JellyfinDevice(device!) == "mira-friend-" + device!.Id, "Its own Jellyfin device id");
            Assert(friends.Redeem(token, "device-other", "Autre", "100.101.9.9").Result == FriendInvites.Redeeming.Used, "Never twice: « already used »");
            Assert(new FriendInvites(file, () => now).Redeem(token, "device-other", "Autre", "100.101.9.9").Result == FriendInvites.Redeeming.Used, "Still after a restart");

            // Ended, cancelled, wrong.
            var (late, _) = friends.Invite(Sam, "Sam", "kKey2", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            var (cancelled, entry) = friends.Invite(Sam, "Sam", "kKey3", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            Assert(friends.Cancel(entry.Id) is { KeyId: "kKey3" } && friends.Cancel(used!.Id) is null, "Cancelled: its key to revoke; never a used one");
            Assert(friends.Redeem(cancelled, "device-sam", "Sam", "100.101.7.7").Result == FriendInvites.Redeeming.Ended, "A cancelled invitation");
            var later = new FriendInvites(file, () => now + TimeSpan.FromHours(49));
            Assert(later.Redeem(late, "device-sam", "Sam", "100.101.7.7").Result == FriendInvites.Redeeming.Ended, "After 48 hours");
            Assert(friends.Redeem("pas-une-invitation", "device-sam", "Sam", "100.101.7.7").Result == FriendInvites.Redeeming.Wrong && friends.Redeem(null, "device-sam", "Sam", "x").Result == FriendInvites.Redeeming.Wrong, "Wrong");

            // Wrong tries counted per address, a right one is not.
            var tries = new FriendInvites(null, () => now);
            var (right, _) = tries.Invite(Sam, "Sam", "k", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            for (var i = 0; i < FriendInvites.TriesPerAddress; i++)
                tries.Redeem("faux-" + i, "device-x", "X", "100.101.6.6");
            Assert(tries.Redeem(right, "device-x", "X", "100.101.6.6").Result == FriendInvites.Redeeming.TooMany, "Too many wrong tries from one address");
            Assert(tries.Redeem(right, "device-y", "Y", "100.101.5.5").Result == FriendInvites.Redeeming.Done, "Another address still may");

            // Given back when Jellyfin opened nothing: the same link works again.
            var back = new FriendInvites(null, () => now);
            var (again, _) = back.Invite(Sam, "Sam", "k", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            var first = back.Redeem(again, "device-sam", "Sam", "100.101.7.7");
            back.Undo(first.Invite!, first.Device!);
            Assert(back.Devices().Count == 0 && back.Redeem(again, "device-sam", "Sam", "100.101.7.7").Result == FriendInvites.Redeeming.Done, "Given back");

            // The same device again for the same friend takes the place of its entry.
            var (second, _) = back.Invite(Sam, "Sam", "k2", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            var twice = back.Redeem(second, "device-sam", "Sam", "100.101.7.7");
            Assert(twice.Replaced is not null && back.Devices().Count == 1, "One entry per device");

            // A friend forgotten: their open invitations (keys to revoke) and devices.
            var (open, _) = back.Invite(Sam, "Sam", "kOpen", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            var (forgotOpen, forgotDevices) = back.Forget(Sam);
            Assert(forgotOpen is [{ KeyId: "kOpen" }] && forgotDevices.Count == 1 && back.Devices().Count == 0 && back.Invites().Count == 0, "Forgotten");
            Assert(back.Redeem(open, "device-sam", "Sam", "100.101.7.7").Result == FriendInvites.Redeeming.Wrong, "Nothing of theirs works any more");

            // Too many open at once.
            var full = new FriendInvites(null, () => now);
            for (var i = 0; i < FriendInvites.MaxOpen; i++)
                full.Invite(Sam, "Sam", "k" + i, HomeTailnet.FriendTag, now + FriendInvites.Life);
            Assert(full.Invite(Sam, "Sam", "kMore", HomeTailnet.FriendTag, now + FriendInvites.Life) is null, "At most " + FriendInvites.MaxOpen + " open");

            // Used invitations forgotten after a month.
            Assert(new FriendInvites(file, () => now + TimeSpan.FromDays(33)).Invites().Count == 0, "Forgotten a month after their end");
            return Task.CompletedTask;
        });

        await Test("Inviter un ami par lien : le lien porte la clé, l’invitation et le prénom", () =>
        {
            var data = new FriendInvites.LinkData(1, "Maison", "f0a1b2c3d4e5f60718293a4b5c6d7e8f", "http://100.88.12.34:8096", "tskey-auth-kAbC123-xyz", 1_800_000_000, "jeton-invitation", "Alex");
            var link = FriendInvites.Link(data);
            Assert(link.StartsWith("https://miracinema.app/friend#", StringComparison.Ordinal) && !link.Contains('+') && !link[(link.IndexOf('#') + 1)..].Contains('/'), "A link to the site, its data after the « # »");
            var hash = link[(link.IndexOf('#') + 1)..].Replace('-', '+').Replace('_', '/');
            hash += new string('=', (4 - (hash.Length % 4)) % 4);
            var read = JsonNode.Parse(Convert.FromBase64String(hash))!;
            Assert(read["v"]!.GetValue<int>() == 1 && read["invite"]!.GetValue<string>() == "jeton-invitation" && read["friend"]!.GetValue<string>() == "Alex" && read["key"]!.GetValue<string>() == "tskey-auth-kAbC123-xyz", "Read back");
            Assert(HomeTailnet.ReadLink(link) is { } asHome && asHome.Key == data.Key && asHome.Address == data.Address, "The home link's data, and more");
            return Task.CompletedTask;
        });

        await Test("Inviter un ami par lien : l’étiquette des amis limitée à Jellyfin, ajoutée par Mira", () =>
        {
            static HomeTailnet.Verdict Check(string text, string tag) => HomeTailnet.CheckPolicy(HomePolicyFix.Parse(text), Server, 8096, tag).Verdict;
            // The home's policy as Mira put it right: friends' tag nowhere yet.
            const string Home = "{\n\t\"tagOwners\": {\n\t\t\"tag:mira-home\": [\"autogroup:admin\"],\n\t},\n\t\"grants\": [\n\t\t{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]},\n"
                + "\t\t{\"src\": [\"autogroup:member\", \"autogroup:shared\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},\n\t],\n}";
            Assert(Check(Home, HomeTailnet.Tag) == HomeTailnet.Verdict.Ok && Check(Home, HomeTailnet.FriendTag) == HomeTailnet.Verdict.NoTag, "The home's tag right, friends' not there");
            var plan = HomePolicyFix.Make(Home, Server, 8096, HomeTailnet.FriendTag);
            Assert(plan is not null && plan.Changes.Select(x => x.Kind).SequenceEqual(["tag", "grant"]), "Only additions: the tag, its rule");
            Assert(plan!.Text.Contains("\"tag:mira-friend\": [\"autogroup:admin\", \"tag:mira-home\"]", StringComparison.Ordinal), "Owned by the home's tag: the home's OAuth client makes friends' keys");
            Assert(plan.Text.Contains("{\"src\": [\"tag:mira-friend\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]}", StringComparison.Ordinal), "Friends reach Jellyfin's port, nothing else");
            Assert(Check(plan.Text, HomeTailnet.FriendTag) == HomeTailnet.Verdict.Ok && Check(plan.Text, HomeTailnet.Tag) == HomeTailnet.Verdict.Ok, "Both right after");
            Assert(HomePolicyFix.Make(plan.Text, Server, 8096, HomeTailnet.FriendTag) is null, "Nothing more once right");

            // « * » still open: friends' fix narrows like the home's, never to Mira's own tags (the controller then leaves it to the home's fix).
            const string Open = "{\"tagOwners\": {\"tag:mira-home\": [], \"tag:mira-friend\": [\"tag:mira-home\"], \"tag:nas\": []}, \"grants\": [{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}";
            var narrowed = HomePolicyFix.Make(Open, Server, 8096, HomeTailnet.FriendTag);
            Assert(narrowed is not null && narrowed.Changes.Any(x => x.Kind == "narrow") && narrowed.Changes.First(x => x.Kind == "narrow").Sources is ["autogroup:member", "autogroup:shared", "tag:nas"], "Never narrowed to Mira's tags");
            var home = HomePolicyFix.Make(Open, Server, 8096);
            Assert(home is not null && home.Changes.First(x => x.Kind == "narrow").Sources is ["autogroup:member", "autogroup:shared", "tag:nas"] && Check(home.Text, HomeTailnet.Tag) == HomeTailnet.Verdict.Ok, "The home's fix leaves friends' tag out too");

            // A rule from friends' tag to more than Jellyfin is refused for it, and does not touch the home's.
            const string Wide = "{\"tagOwners\": {\"tag:mira-home\": [], \"tag:mira-friend\": []}, \"grants\": [{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]}, {\"src\": [\"tag:mira-friend\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}";
            Assert(Check(Wide, HomeTailnet.FriendTag) == HomeTailnet.Verdict.TooOpen && Check(Wide, HomeTailnet.Tag) == HomeTailnet.Verdict.Ok, "Each tag judged on its own");
            Assert(HomeTailnet.Setup(Server, 8096).TagOwners == "\"tag:mira-home\": [\"autogroup:admin\"]", "The home's setup unchanged");
            return Task.CompletedTask;
        });

        await Test("Inviter un ami par lien : les nœuds des amis, gardés puis retirés", async () =>
        {
            var now = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
            var calls = new List<string>();
            const string Devices = "{\"devices\": [{\"id\": \"n1\", \"hostname\": \"iphone-alex\", \"addresses\": [\"100.101.8.8\"], \"tags\": [\"tag:mira-friend\"], \"created\": \"2026-11-01T10:00:00Z\"},"
                + " {\"id\": \"n2\", \"hostname\": \"mira-salon\", \"addresses\": [\"100.101.1.2\"], \"tags\": [\"tag:mira-home\"], \"created\": \"2026-11-01T10:00:00Z\"},"
                + " {\"id\": \"n3\", \"hostname\": \"inconnu\", \"addresses\": [\"100.101.9.9\"], \"tags\": [\"tag:mira-friend\"], \"created\": \"2026-11-01T10:00:00Z\"},"
                + " {\"id\": \"n4\", \"hostname\": \"nas\", \"addresses\": [\"100.101.4.4\"], \"tags\": [\"tag:nas\"], \"created\": \"2025-11-01T10:00:00Z\"}]}";
            using var http = new HttpClient(new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {body}");
                HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                return request.RequestUri.AbsolutePath switch
                {
                    "/api/v2/oauth/token" => Json("{\"access_token\": \"tskey-api-temp\", \"expires_in\": 3600}"),
                    "/api/v2/tailnet/-/keys" => Json("{\"id\": \"kFriend\", \"key\": \"tskey-auth-kFriend-secret\", \"expires\": \"2026-11-22T10:00:00Z\"}"),
                    "/api/v2/tailnet/-/keys/kFriend" => new HttpResponseMessage(HttpStatusCode.OK),
                    "/api/v2/tailnet/-/keys/kGone" => Json("{\"message\": \"not found\"}", HttpStatusCode.NotFound),
                    "/api/v2/tailnet/-/devices" => Json(Devices),
                    "/api/v2/device/n1" => new HttpResponseMessage(HttpStatusCode.OK),
                    _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                };
            }));
            var api = new TailscaleApi(http, "kFriends" + Guid.NewGuid().ToString("N"), "tskey-client-secret", "https://api.example.test", () => now);
            var (key, id, _) = await api.CreateKeyAsync(FriendInvites.Life, CancellationToken.None, HomeTailnet.FriendTag);
            var asked = JsonNode.Parse(calls[^1][calls[^1].IndexOf('{')..])!;
            Assert(key == "tskey-auth-kFriend-secret" && asked["capabilities"]!["devices"]!["create"]!["tags"]![0]!.GetValue<string>() == "tag:mira-friend"
                && asked["capabilities"]!["devices"]!["create"]!["reusable"]!.GetValue<bool>() == false && asked["description"]!.GetValue<string>() == "Mira friend link", "A single-use key of friends' tag");
            Assert(await api.DeleteKeyAsync(id, CancellationToken.None) && await api.DeleteKeyAsync("kGone", CancellationToken.None) && !await api.DeleteKeyAsync("../devices", CancellationToken.None), "A key revoked; never a path");

            var nodes = await api.DevicesAsync(CancellationToken.None);
            Assert(nodes is [{ Tagged: true, Friend: true }, { Tagged: true, Friend: false }, { Tagged: true, Friend: true }, { Tagged: false, Friend: false }], "Mira's two tags, friends' told apart");
            Assert(await api.DeleteAsync(nodes[0], CancellationToken.None) && !await api.DeleteAsync(nodes[3], CancellationToken.None), "A friend's node may go, never one without Mira's tags");

            // The sweep: a friend's device that came in stays; a key used by someone who never redeemed goes after a week.
            var tailnet = new HomeTailnet(null, () => now);
            var friends = new FriendInvites(null, () => now);
            var (token, _) = friends.Invite(Alex, "Alex", "kFriend", HomeTailnet.FriendTag, now + FriendInvites.Life)!.Value;
            var alex = friends.Redeem(token, "device-alex", "iPhone d’Alex", "100.101.8.8").Device!;
            var joined = new[] { "100.101.1.2" }.Concat(friends.Devices().Select(x => x.Tailnet).OfType<string>());
            Assert(tailnet.ToDelete(nodes, joined).Select(x => x.Id).SequenceEqual(["n3"]), "Friends' devices kept, the stranger gone");
            friends.RemoveDevice(alex.Id);
            tailnet.Remove(alex.Tailnet!);
            Assert(tailnet.ToDelete(nodes, new[] { "100.101.1.2" }).Select(x => x.Id).SequenceEqual(["n1", "n3"]), "A friend's device taken out leaves the tailnet");

            // Never through Funnel.
            foreach (var path in new[] { "/Mira/friend/redeem", "/Mira/friend", "/Mira/friend/invite" })
                Assert(GuestRoutes.ThroughTunnel("POST", path, "", false) == GuestRoutes.Tunnel.None && GuestRoutes.ThroughTunnel("GET", path, "", false) == GuestRoutes.Tunnel.None, "Closed to Funnel: " + path);
        });
    }
}
