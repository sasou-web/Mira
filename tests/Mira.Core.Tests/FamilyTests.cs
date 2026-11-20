using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Family;
using Mira.Core.Tailscale;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Le foyer » in the apps: the home link the extension makes read back (and nothing else taken for one), the family's
/// side of /Mira/home and its refusals as the apps show them, the home's own Tailscale node and the one host it reaches.
/// </summary>
internal static class FamilyTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var until = DateTimeOffset.UtcNow.AddHours(48);
        string Made(string address = "http://100.88.12.34:8096", string key = "tskey-auth-kAbC123CNTRL-0123456789abcdef", string name = "Maison") =>
            HomeTailnet.Link(new HomeTailnet.LinkData(1, name, Id, address, key, until.ToUnixTimeSeconds()));

        await Test("Foyer : le lien que fait l’extension se relit, sur sa page comme dans Mira", () =>
        {
            var page = Made();
            Assert(page.StartsWith(HomeLink.Page, StringComparison.Ordinal), "The link's page");
            foreach (var text in new[] { page, HomeLink.App + page[(page.IndexOf('#') + 1)..], "mira://home/#" + page[(page.IndexOf('#') + 1)..] })
            {
                var link = HomeLink.Read(text);
                Assert(link is { Name: "Maison", ServerId: Id, Address: "http://100.88.12.34:8096/", Key: "tskey-auth-kAbC123CNTRL-0123456789abcdef" }, "Read back: " + text[..20]);
                Assert(link!.Host == "100.88.12.34" && !link.Expired(DateTimeOffset.UtcNow) && link.Expired(until.AddSeconds(1)), "Its host and its end");
                Assert(link.Is("01234567-89ab-cdef-0123-456789ABCDEF") && !link.Is("ffffffffffffffffffffffffffffffff") && !link.Is(null), "The server's id, with or without dashes");
            }
            Assert(HomeLink.Read(Made(address: "http://100.88.12.34:8096/jellyfin"))?.Address == "http://100.88.12.34:8096/jellyfin/", "A Jellyfin under a path");
            return Task.CompletedTask;
        });

        await Test("Foyer : rien d’autre n’est pris pour un lien du foyer", () =>
        {
            Assert(HomeLink.Read(Made(address: "http://192.168.1.42:8096")) is null, "An address outside Tailscale's: the home's node reaches only the tailnet");
            Assert(HomeLink.Read(Made(address: "http://jellyfin.example.com")) is null, "A name, which could be anywhere");
            Assert(HomeLink.Read(Made(address: "http://alex:secret@100.88.12.34:8096")) is null, "Credentials in the address");
            Assert(HomeLink.Read(Made(key: "not-a-key")) is null, "A key that is not Tailscale's");
            var page = Made();
            Assert(HomeLink.Read(page[..^6]) is null, "Cut short");
            Assert(HomeLink.Read(page + "<script>") is null, "Something after it");
            Assert(HomeLink.Read("mira://party/abc") is null && HomeLink.Read("https://example.com/home#" + page[(page.IndexOf('#') + 1)..]) is null, "Another link, another site");
            Assert(HomeLink.Read(HomeLink.App + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"v\":2,\"name\":\"x\",\"id\":\"" + Id + "\",\"url\":\"http://100.88.12.34:8096\",\"key\":\"tskey-auth-x\",\"until\":1}"))) is null, "Another version");
            var odd = HomeLink.Read(Made(name: "Chez\u0007 Alex " + new string('x', 120)));
            Assert(odd is not null && !odd.Name.Contains('\u0007') && odd.Name.Length <= 80, "A name cleaned and kept short");
            return Task.CompletedTask;
        });

        await Test("Foyer : rejoindre, les profils, ouvrir avec un code, comme l’extension répond", async () =>
        {
            var seen = new List<(string Path, string? Token, string Body)>();
            var answers = new Queue<HttpResponseMessage>();
            HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            var handler = new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                seen.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues(JellyfinClient.HomeHeader, out var values) ? values.Single() : null, body));
                return answers.Dequeue();
            });
            using var client = new JellyfinClient(new("http://100.88.12.34:8096", "", "", "", "app-device-0001"), handler);
            answers.Enqueue(Json(HttpStatusCode.OK, new { home = true }));
            Assert(await client.HomeOnAsync() == true, "The home is on");
            answers.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("", Encoding.UTF8, "text/html") });
            Assert(await client.HomeOnAsync() is null, "No extension, or an older one: no home");

            answers.Enqueue(Json(HttpStatusCode.Unauthorized, new { error = "Ce n’est pas le mot de passe du foyer." }));
            var wrong = await Throws<HomeRefusedException>(() => client.HomeJoinAsync("mauvais", "app-device-0001", "Mira on MAISON-PC"));
            Assert(wrong.Kind == HomeRefusal.Wrong && wrong.Message == "Ce n’est pas le mot de passe du foyer.", "A wrong password, in the extension's words");
            answers.Enqueue(Json(HttpStatusCode.TooManyRequests, new { error = "Trop d’essais : attends quelques minutes." }));
            Assert((await Throws<HomeRefusedException>(() => client.HomeJoinAsync("x", "app-device-0001", "x"))).Kind == HomeRefusal.TooMany, "Too many tries");
            answers.Enqueue(Json(HttpStatusCode.Conflict, new { error = "Le foyer a déjà trop d’appareils." }));
            Assert((await Throws<HomeRefusedException>(() => client.HomeJoinAsync("x", "app-device-0001", "x"))).Kind == HomeRefusal.Full, "A full home");
            answers.Enqueue(Json(HttpStatusCode.NotFound, new { error = "Ce Jellyfin n’a pas de foyer." }));
            Assert((await Throws<HomeRefusedException>(() => client.HomeJoinAsync("x", "app-device-0001", "x"))).Kind == HomeRefusal.NoHome, "No home any more");

            answers.Enqueue(Json(HttpStatusCode.OK, new { token = "home-token", profiles = new[] { new { id = "u1", name = "Sam", imageTag = (string?)null, pin = false }, new { id = "u2", name = "Lina", imageTag = (string?)"tag", pin = true } }, current = (string?)null, deviceId = "mira-home-d1" }));
            var joined = await client.HomeJoinAsync("le bon mot de passe", "app-device-0001", "Mira on MAISON-PC");
            Assert(joined is { Token: "home-token", DeviceId: "mira-home-d1", Profiles: [{ Name: "Sam", Pin: false }, { Name: "Lina", Pin: true, ImageTag: "tag" }] }, "Joined: the token, the profiles");
            Assert(seen[^1].Path == "/Mira/home/join" && seen[^1].Token is null && seen[^1].Body.Contains("\"device\":\"app-device-0001\"", StringComparison.Ordinal), "The app's own id, no token yet");

            answers.Enqueue(Json(HttpStatusCode.Forbidden, new { error = "Ce profil a un code.", pin = true, left = 5 }));
            Assert((await Throws<HomeRefusedException>(() => client.HomeOpenAsync("home-token", "u2", null))).Kind == HomeRefusal.PinAsked, "A PIN asked");
            Assert(seen[^1].Token == "home-token", "The home token in its header");
            answers.Enqueue(Json(HttpStatusCode.Forbidden, new { error = "Ce n’est pas le bon code.", pin = true, left = 3 }));
            var bad = await Throws<HomeRefusedException>(() => client.HomeOpenAsync("home-token", "u2", "1111"));
            Assert(bad is { Kind: HomeRefusal.PinWrong, Left: 3 }, "A wrong PIN, the tries left");
            answers.Enqueue(Json(HttpStatusCode.TooManyRequests, new { error = "Trop d’essais.", pin = true, wait = 280 }));
            Assert(await Throws<HomeRefusedException>(() => client.HomeOpenAsync("home-token", "u2", "1111")) is { Kind: HomeRefusal.PinWait, Wait: 280 }, "Locked a while");
            answers.Enqueue(Json(HttpStatusCode.Unauthorized, new { error = "Cet appareil n’est plus dans le foyer." }));
            Assert((await Throws<HomeRefusedException>(() => client.HomeProfilesAsync("home-token"))).Kind == HomeRefusal.OutOfHome, "Out of the home: its password again");

            answers.Enqueue(Json(HttpStatusCode.OK, new { accessToken = "jellyfin-token", userId = "u2", userName = "Lina", serverId = Id, deviceId = "mira-home-d1" }));
            var opened = await client.HomeOpenAsync("home-token", "u2", "2468");
            Assert(opened is { Server: "http://100.88.12.34:8096/", UserId: "u2", UserName: "Lina", Token: "jellyfin-token", DeviceId: "mira-home-d1", ServerId: Id }, "The profile's own session, under the home's device id");
            Assert(seen[^1].Body.Contains("\"pin\":\"2468\"", StringComparison.Ordinal), "Its PIN sent");

            answers.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
            Assert(await client.HomeLeaveAsync("home-token"), "Left");
            answers.Enqueue(Json(HttpStatusCode.Unauthorized, new { error = "Cet appareil n’est plus dans le foyer." }));
            Assert(await client.HomeLeaveAsync("home-token"), "Already out: left all the same");
        });

        await Test("Foyer : un lien refusé à l’administrateur dit pourquoi (politique Tailscale)", async () =>
        {
            var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"error\":\"Ta politique Tailscale laisse les appareils du foyer aller ailleurs.\",\"policy\":\"too-open\",\"open\":[{\"section\":\"acls\",\"number\":1,\"sources\":[\"*\"]}]}", Encoding.UTF8, "application/json"),
            }));
            using var client = new JellyfinClient(new("http://192.168.1.42:8096", "admin", "Alex", "token", "device-0001"), handler);
            var refused = await Throws<HomeLinkRefusedException>(() => client.HomeLinkAsync());
            Assert(refused is { Policy: "too-open", Open: [{ Section: "acls", Number: 1, Sources: ["*"] }] }, "Its verdict and the rule at fault");
            Assert(HomeText.Policy("too-open") != HomeText.Policy("ok") && HomeText.Policy(null).Length > 0, "Each verdict said");
        });

        await Test("Loin de chez toi : un seul état, celui du foyer pour son administrateur, sinon celui de cet appareil", () =>
        {
            var set = new HomeTailnetState { Connected = true, Policy = "ok" };
            Assert(AwayFromHome.For(set, true, youReach: false).Level == AwayLevel.Ready, "The home's Tailscale set up: ready, whatever this PC does");
            Assert(AwayFromHome.For(new HomeTailnetState(), true, youReach: true).Level == AwayLevel.Off, "Not set up: to set up, even signed in here");
            Assert(AwayFromHome.For(set with { Policy = "open" }, true, false).Level == AwayLevel.Attention, "A policy to fix");
            Assert(AwayFromHome.For(set with { Broad = true }, true, false).Level == AwayLevel.Attention, "A client that can do too much");
            Assert(AwayFromHome.For(null, true, youReach: true) is { Level: AwayLevel.Ready, Word: "Prêt" }, "No home: this device's own way");
            Assert(AwayFromHome.For(null, true, youReach: false).Level == AwayLevel.Off, "Neither");
            Assert(AwayFromHome.For(null, false, youReach: false) is { Level: AwayLevel.Unknown, Word: "" }, "Still reading: nothing said");
            return Task.CompletedTask;
        });

        await Test("Foyer : seul le Jellyfin du foyer passe par le nœud du foyer", () =>
        {
            var network = new TailnetNetwork(() => [], computer: null);
            var home = new StandIn { Unavailable = TailnetCodes.Starting, Status = new() { State = TailnetStates.Starting } };
            network.FollowHome(home, "100.88.12.34");
            Assert(network.Choose("100.88.12.34") is { Path: TailnetPath.Fail, Code: TailnetCodes.Starting }, "Its node starting: it waits for it");
            Assert(network.Choose("100.88.12.35").Path == TailnetPath.Direct && network.Choose("192.168.1.42").Path == TailnetPath.Direct, "Nothing else of the tailnet through it");
            home.Status = new() { State = TailnetStates.Running }; home.Proxy = new("127.0.0.1:40001", "u", "p"); home.Change();
            Assert(network.Choose("100.88.12.34").Path == TailnetPath.Node && network.IsTailnet("100.88.12.34"), "On the tailnet: through it");
            Assert(network.ForPlayer(new Uri("http://100.88.12.34:8096/Videos/1/stream"))?.Proxy.Contains("127.0.0.1:40001", StringComparison.Ordinal) == true, "mpv through the home's proxy");
            // The person's own Tailscale, on another tailnet: it keeps its devices, never the home's Jellyfin.
            var own = new StandIn { Status = new() { State = TailnetStates.Running }, Proxy = new("127.0.0.1:40002", "u", "p") };
            network.Follow(own);
            Assert(network.ForPlayer(new Uri("http://100.88.12.34:8096/x"))?.Proxy.Contains("40001", StringComparison.Ordinal) == true, "The home's Jellyfin through the home's node");
            Assert(network.ForPlayer(new Uri("http://100.64.0.2:8096/x"))?.Proxy.Contains("40002", StringComparison.Ordinal) == true, "The person's own devices through their own");
            home.Status = new() { State = TailnetStates.NeedsLogin }; home.Change();
            Assert(network.Choose("100.88.12.34") is { Path: TailnetPath.Fail, Code: TailnetCodes.NeedsLogin }, "Removed from the tailnet: said, never through the person's own");
            network.FollowHome(null, null); network.Follow(null);
            Assert(network.Choose("100.88.12.34").Path == TailnetPath.Direct && network.HomeHost is null, "Out of the home: as before");
            return Task.CompletedTask;
        });

        await Test("Foyer : le nœud du foyer entre par la clé du lien, une clé usée le dit", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "mira-home-node-" + Guid.NewGuid().ToString("N"));
            var (executable, arguments) = FakeTailnet.Command();
            var network = new TailnetNetwork(() => [], computer: null);
            HomeNode Node() => new(new()
            {
                Locate = () => (executable, null), Arguments = arguments, Directory = folder, StateKey = () => new byte[32], AppVersion = "test", Network = network,
                Forget = () => { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }, JoinWait = TimeSpan.FromSeconds(20),
            });
            try
            {
                await using (var spent = Node())
                {
                    Assert(await spent.StartAsync("100.64.0.2", "tskey-auth-spent") == TailnetCodes.NeedsLogin, "A spent key: said");
                    Assert(network.Choose("100.64.0.2") is { Path: TailnetPath.Fail, Code: TailnetCodes.NeedsLogin }, "And nothing goes elsewhere meanwhile");
                    await spent.StopAsync();
                }
                Assert(network.HomeHost is null, "Stopped: the network forgets it");
                var node = Node();
                Assert(await node.StartAsync("100.64.0.2", "tskey-auth-good") is null, "A good key: on the tailnet");
                Assert(network.Choose("100.64.0.2").Path == TailnetPath.Node && network.HomeHost == "100.64.0.2", "The home's Jellyfin through it");
                Assert(await node.StartAsync("100.64.0.2", null) is null, "Started again for the same home: as it is");
                await node.LeaveAsync();
                Assert(!Directory.Exists(folder) && network.HomeHost is null, "Left: its state gone, the network as before");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        });
    }

    private sealed class StandIn : ITailnetNode
    {
        public TailnetStatus? Status { get; set; }
        public TailnetProxy? Proxy { get; set; }
        public string? Unavailable { get; set; }
        public event Action? Changed;
        public void Change() => Changed?.Invoke();
    }
}
