using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using static TestSupport;

/// <summary>
/// « Your server » of « Security »: Jellyfin open to the whole Internet told from its network settings, the devices that
/// closing it would cut, and the settings closed with everything else kept; the setup's administrator password.
/// </summary>
internal static class ExposureTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;

        await Test("Serveur : Jellyfin ouvert à tout Internet, sauf liste d’adresses choisie", () =>
        {
            static bool Open(string json) => JellyfinExposure.OpenToInternet((JsonObject)JsonNode.Parse(json)!);
            Assert(!Open("""{"EnableRemoteAccess":false,"LocalNetworkSubnets":[]}"""), "Remote access off");
            Assert(Open("""{"EnableRemoteAccess":true}"""), "Remote access on, nothing else");
            Assert(Open("""{"EnableRemoteAccess":true,"RemoteIPFilter":[],"IsRemoteIPFilterBlacklist":false}"""), "An empty list lets everyone in");
            Assert(!Open("""{"EnableRemoteAccess":true,"RemoteIPFilter":["203.0.113.9"],"IsRemoteIPFilterBlacklist":false}"""), "A chosen list of allowed addresses is someone's choice");
            Assert(Open("""{"EnableRemoteAccess":true,"RemoteIPFilter":["203.0.113.9"],"IsRemoteIPFilterBlacklist":true}"""), "A block list still lets the rest in");
            Assert(Open("""{"EnableRemoteAccess":true,"RemoteIPFilter":["0.0.0.0/0"],"IsRemoteIPFilterBlacklist":false}"""), "Allowing every address narrows nothing");
            return Task.CompletedTask;
        });

        await Test("Serveur : ce qui compte comme la maison une fois fermé", () =>
        {
            IReadOnlyList<string> local = LocalNetwork.WithTailnet([]);
            foreach (var inside in new[] { "192.168.1.42", "10.0.0.7", "172.20.1.1", "100.88.12.34", "127.0.0.1", "::1", "::ffff:192.168.1.42", "fe80::1", "fd7a:115c:a1e0::1", "[2001:db8::1]:51000x", "", null })
                Assert(JellyfinExposure.IsLocal(inside, local), "Said outside: " + inside);
            foreach (var outside in new[] { "203.0.113.9", "::ffff:203.0.113.9", "[2001:db8::1]:51000", "2001:db8::1", "100.128.0.1", "172.32.0.1" })
                Assert(!JellyfinExposure.IsLocal(outside, local), "Said at home: " + outside);
            Assert(JellyfinExposure.IsLocal("192.168.5.5", ["192.168.5.1/24"]) && JellyfinExposure.IsLocal("192.168.5.5", ["192.168.5.5"]) && !JellyfinExposure.IsLocal("192.168.5.6", ["192.168.5.5/32"]),
                "Host bits past the prefix, a single address");
            Assert(!JellyfinExposure.IsLocal("192.168.1.42", ["10.0.0.0/8", "nonsense", "192.168.1.0/33"]), "A list without the home network turns it away");
            return Task.CompletedTask;
        });

        var sessions = """
            [{"DeviceId":"mira-pc","DeviceName":"MAISON-PC","Client":"Mira for Windows","UserName":"Alex","RemoteEndPoint":"192.168.1.42","LastActivityDate":"2026-11-20T11:59:00Z"},
             {"DeviceId":"tv-1","DeviceName":"Living room TV","Client":"Jellyfin Android TV","UserName":"Sam","RemoteEndPoint":"203.0.113.9","LastActivityDate":"2026-11-19T20:00:00Z"},
             {"DeviceId":"tv-1","DeviceName":"Living room TV","Client":"Jellyfin Android TV","UserName":"Sam","RemoteEndPoint":"203.0.113.9","LastActivityDate":"2026-11-18T20:00:00Z"},
             {"DeviceId":"phone-1","DeviceName":"Sam’s iPhone","Client":"Mira for iPhone","UserName":"Sam","RemoteEndPoint":"100.88.12.34","LastActivityDate":"2026-11-20T10:00:00Z"},
             {"DeviceId":"funnel","DeviceName":"Party","Client":"Mira","RemoteEndPoint":"127.0.0.1"},
             {"DeviceId":"laptop","DeviceName":"Lina’s MacBook","Client":"Mira for Mac","RemoteEndPoint":"[2001:db8::7]:4000","LastActivityDate":"2026-11-20T09:00:00Z"},
             {"DeviceName":"No id","RemoteEndPoint":"203.0.113.10"}]
            """;

        await Test("Serveur : les appareils venus d’ailleurs que la maison ou Tailscale, ceux que fermer couperait", () =>
        {
            using var document = JsonDocument.Parse(sessions);
            var outside = JellyfinExposure.Outside(document.RootElement, LocalNetwork.WithTailnet([]));
            Assert(string.Join(",", outside.Select(x => x.Name)) == "Lina’s MacBook,Living room TV", "Outside: " + string.Join(",", outside.Select(x => x.Name)));
            Assert(outside[1] is { App: "Jellyfin Android TV", UserName: "Sam", Address: "203.0.113.9" } && outside[1].LastActive == DateTimeOffset.Parse("2026-11-19T20:00:00Z"), "One line per device, its last time");
            Assert(outside[0].Address == "2001:db8::7", "An IPv6 address with its port");
            return Task.CompletedTask;
        });

        await Test("Serveur : fermer à Internet garde tout le reste, et jamais depuis un appareil venu d’ailleurs", async () =>
        {
            var stored = """{"EnableRemoteAccess":true,"LocalNetworkSubnets":["192.168.1.0/24"],"KnownProxies":["10.0.0.1"],"RemoteIPFilter":[],"InternalHttpPort":8096,"EnableIPv6":true}""";
            var here = "192.168.1.42"; var admin = true; string? posted = null;
            Handler Server() => new(async request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (!admin && path.EndsWith("/network", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (request.Method == HttpMethod.Post) { stored = posted = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
                var body = path.EndsWith("/Sessions", StringComparison.Ordinal) ? sessions.Replace("\"192.168.1.42\"", $"\"{here}\"") : stored;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
            using var client = new JellyfinClient(new("http://192.168.1.10:8096/", "user-1", "Alex", "secret", "mira-pc"), Server());

            var opening = await client.InternetOpeningAsync();
            Assert(opening is { Open: true, ThisOutside: false }, "Open, from home");
            // The home network is the chosen 192.168.1.0/24 (the MacBook's IPv6 and the TV are outside it).
            Assert(string.Join(",", opening!.Outside.Select(x => x.Id)) == "laptop,tv-1", "Who would be cut: " + string.Join(",", opening.Outside.Select(x => x.Id)));
            Assert(await client.CloseToInternetAsync(), "Not closed");
            var sent = JsonNode.Parse(posted!)!.AsObject();
            Assert(sent["EnableRemoteAccess"]!.GetValue<bool>() == false && sent["LocalNetworkSubnets"]!.AsArray().Select(x => x!.GetValue<string>()).SequenceEqual(["192.168.1.0/24", LocalNetwork.TailnetRange])
                && sent["KnownProxies"]![0]!.GetValue<string>() == "10.0.0.1" && sent["InternalHttpPort"]!.GetValue<int>() == 8096 && sent["EnableIPv6"]!.GetValue<bool>(), "Other settings changed: " + posted);
            Assert(await client.InternetOpeningAsync() is { Open: false, Outside.Count: 0 }, "Still said open once closed");

            stored = """{"EnableRemoteAccess":true,"LocalNetworkSubnets":[]}"""; here = "203.0.113.50"; posted = null;
            Assert(await client.InternetOpeningAsync() is { Open: true, ThisOutside: true } outsider && outsider.Outside.All(x => x.Id != "mira-pc"), "This device from outside");
            Assert(!await client.CloseToInternetAsync() && posted is null, "Closed from a device it would cut");
            admin = false;
            Assert(await client.InternetOpeningAsync() is null && !await client.CloseToInternetAsync(), "Not an administrator: nothing read, nothing changed");
        });

        await Test("Serveur : Jellyfin ouvert aux Wi-Fi publics par une règle du pare-feu", () =>
        {
            const string jellyfin = @"C:\Program Files\Jellyfin\Server\jellyfin.exe";
            JellyfinFirewall.Rule Allow(string name, int profiles, string? remote = "*", string? program = jellyfin, string? ports = null, bool allow = true) =>
                new(name, true, true, allow, profiles, program, JellyfinFirewall.Tcp, ports, remote);
            var on = new JellyfinFirewall.Profile(true, false, false);
            string Names(params JellyfinFirewall.Rule[] rules) => string.Join(",", JellyfinFirewall.OpenOnPublic(rules, on, jellyfin, 8096).Select(x => x.Name));
            var mira = Allow(JellyfinStartup.RuleName, JellyfinStartup.RuleProfiles, "LocalSubnet", ports: "8096");
            Assert(Names(mira, Allow(JellyfinStartup.RuleName, JellyfinStartup.RuleProfiles, LocalNetwork.TailnetRange, ports: "8096")) == "", "Mira's rule never opens public networks");
            Assert(Names(Allow("jellyfin.exe", JellyfinFirewall.Public), Allow("jellyfin.exe", JellyfinFirewall.Private)) == "jellyfin.exe", "Windows' prompt, public ticked");
            Assert(Names(Allow("old", JellyfinFirewall.AllProfiles, "LocalSubnet", ports: "8096")) == "old", "An older rule for every network");
            Assert(Names(Allow("port", JellyfinFirewall.Public, program: null, ports: "8096")) == "port" && Names(Allow("any port", JellyfinFirewall.Public, program: null, ports: "*")) == "", "A port rule counts; a rule for every program and port is not Jellyfin's");
            Assert(Names(Allow("tailnet", JellyfinFirewall.AllProfiles, LocalNetwork.TailnetRange)) == "", "Tailscale's range alone is no stranger");
            Assert(Names(Allow("open", JellyfinFirewall.Public), Allow("block", JellyfinFirewall.Public, allow: false)) == "", "A block rule keeps them out");
            Assert(JellyfinFirewall.OpenOnPublic([Allow("open", JellyfinFirewall.Public)], on with { Enabled = false }, jellyfin, 8096).Count == 0
                && JellyfinFirewall.OpenOnPublic([Allow("open", JellyfinFirewall.Public)], on with { BlockAll = true }, jellyfin, 8096).Count == 0, "Firewall off (not a rule's), or blocking all");
            // Lifted by the elevated script: an allow rule matched as such, never a block rule of the same name.
            var script = JellyfinStartup.Script(new([], [new("jellyfin.exe", jellyfin, JellyfinFirewall.Tcp, null, "*", JellyfinFirewall.AllProfiles, JellyfinFirewall.Domain | JellyfinFirewall.Private, Allow: true)], []));
            Assert(script.Contains("X = 1") && script.Contains("[int]$rule.Action -eq $lift.X") && !script.Contains("[int]$rule.Action -ne 0"), "Allow rule lifted by its action");
            Assert(JellyfinStartup.Script(new([], [new("b", jellyfin, JellyfinFirewall.Tcp, null, "*", JellyfinFirewall.Public, 0)], [])).Contains("X = 0"), "Block rules as before");
            return Task.CompletedTask;
        });

        await Test("Sécurité : le serveur ouvert passe avant les comptes, et « Laisser ouvert » le tait 30 jours", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var account = new Mira.Core.Family.SecurityAccount("a", "Sam", null, false, false, true, false, Mira.Core.Family.AccountWay.Password, null);
            var report = new Mira.Core.Family.SecurityReport([account], [], true, now) { Server = new(new InternetOpening(true, [], false), true) };
            var issues = Mira.Core.Family.AccountSecurity.Issues(report);
            Assert(issues.Count == 3 && issues[0].Server == Mira.Core.Family.ServerOpening.Internet && issues[1].Server == Mira.Core.Family.ServerOpening.PublicWifi && issues[2].Account == account, "Order");
            Assert(Mira.Core.Family.SecurityText.Summary(issues).StartsWith(L("Jellyfin est ouvert à Internet et aux Wi-Fi publics", "Jellyfin is open to the Internet and to public Wi-Fi"), StringComparison.Ordinal), "Summary: " + Mira.Core.Family.SecurityText.Summary(issues));
            var quiet = Mira.Core.Family.AccountSecurity.Issues(report, key => key == Mira.Core.Family.AccountSecurity.InternetKey);
            Assert(quiet.Count == 2 && quiet[0].Server == Mira.Core.Family.ServerOpening.PublicWifi, "Kept open on purpose: quiet");
            Assert(Mira.Core.Family.AccountSecurity.Issues(report with { Server = new(new InternetOpening(false, [], false), null) }).Count == 1, "Closed, and no firewall known: only the account");
            return Task.CompletedTask;
        });

        await Test("Installation : le mot de passe administrateur fait 10 caractères au moins", async () =>
        {
            Assert(JellyfinSetup.MinPasswordLength == 10 && !JellyfinSetup.LongEnough("123456789") && JellyfinSetup.LongEnough("1234567890") && !JellyfinSetup.LongEnough(null), "Ten characters");
            Assert(JellyfinSetup.LongEnough("🎬🎬🎬🎬🎬🎬🎬🎬🎬🎬") && !JellyfinSetup.LongEnough("🎬🎬🎬🎬🎬"), "Counted as signs typed, not UTF-16 units");
            var asked = false;
            var handler = new Handler(_ => { asked = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); });
            try { await JellyfinSetup.ConfigureAsync(new Uri("http://127.0.0.1:8096/"), "Alex", "1234", new MediaFolders("C:/V", "C:/V/Movies", "C:/V/Shows", "C:/V/Anime"), handler); Assert(false, "A 4-sign password accepted"); }
            catch (UserInputException ex) { Assert(ex.Message.Contains("10"), "Message: " + ex.Message); }
            Assert(!asked, "Jellyfin asked before the password was checked");
        });
    }
}
