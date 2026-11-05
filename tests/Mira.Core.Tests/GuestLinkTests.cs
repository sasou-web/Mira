using System.Net;
using System.Net.Http;
using Mira.Core;
using static TestSupport;

/// <summary>
/// « Inviter par lien » in the apps (GuestLinks): the address Funnel gives this PC, whether Funnel serves Mira, the
/// page that turns Funnel on, and what a link's line says.
/// </summary>
internal static class GuestLinkTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Inviter par lien : l’adresse de Funnel et son état lus dans Tailscale", () =>
        {
            Assert(GuestLinks.FunnelAddress("""{"Self":{"DNSName":"desktop-invefqh.tail1234.ts.net.","HostName":"DESKTOP"}}""") == "https://desktop-invefqh.tail1234.ts.net", "adresse");
            Assert(GuestLinks.FunnelAddress("""{"Self":{"DNSName":""}}""") is null && GuestLinks.FunnelAddress("""{"Self":{"DNSName":"pc."}}""") is null, "sans nom");
            Assert(GuestLinks.FunnelAddress("{") is null && GuestLinks.FunnelAddress(null) is null && GuestLinks.FunnelAddress("""{"BackendState":"Stopped"}""") is null, "illisible");
            // tailscale funnel status --json (ipn.ServeConfig): /Mira served and open to the Internet on 443.
            const string open = """{"TCP":{"443":{"HTTPS":true}},"Web":{"pc.tail1234.ts.net:443":{"Handlers":{"/Mira":{"Proxy":"http://127.0.0.1:8096/Mira"}}}},"AllowFunnel":{"pc.tail1234.ts.net:443":true}}""";
            Assert(GuestLinks.FunnelServesMira(open), "Funnel ouvert");
            Assert(!GuestLinks.FunnelServesMira(open.Replace("\"AllowFunnel\":{\"pc.tail1234.ts.net:443\":true}", "\"AllowFunnel\":{}", StringComparison.Ordinal)), "serve seulement (tailnet)");
            Assert(!GuestLinks.FunnelServesMira(open.Replace("/Mira", "/", StringComparison.Ordinal)), "autre chemin");
            Assert(!GuestLinks.FunnelServesMira(open.Replace(":443", ":8443", StringComparison.Ordinal)), "autre port");
            Assert(!GuestLinks.FunnelServesMira("{}") && !GuestLinks.FunnelServesMira("") && !GuestLinks.FunnelServesMira("No serve config"), "rien");
            Assert(GuestLinks.EnablePage("Funnel is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/funnel?node=nXyZ123CNTRL\n")
                == "https://login.tailscale.com/f/funnel?node=nXyZ123CNTRL", "page d’activation");
            Assert(GuestLinks.EnablePage("Available on the internet:\nhttps://pc.ts.net/Mira") is null, "pas de page");
            return Task.CompletedTask;
        });
        await Test("Inviter par lien : la ligne d’un lien et les refus de l’extension", () =>
        {
            var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
            var waiting = new GuestLink("a1", "Léa", now, now, now.AddDays(7), false, null, "https://pc.ts.net/Mira/#/invite/x");
            Assert(GuestLinks.State(waiting, now).StartsWith("Pas encore ouvert · s’efface seul le 30 octobre", StringComparison.Ordinal), GuestLinks.State(waiting, now));
            var used = waiting with { Opened = true, Device = "iPhone", Used = now.AddHours(-2) };
            Assert(GuestLinks.State(used, now).StartsWith("Ouvert sur iPhone · a servi à ", StringComparison.Ordinal), GuestLinks.State(used, now));
            var older = used with { Used = now.AddDays(-3), Ends = now.AddDays(4) };
            Assert(GuestLinks.State(older, now) == "Ouvert sur iPhone · a servi le 20 octobre · s’efface seul le 27 octobre sans servir", GuestLinks.State(older, now));
            Assert(GuestLinks.Refused(HttpStatusCode.Forbidden)!.Contains("administrateurs", StringComparison.Ordinal) && GuestLinks.Refused(HttpStatusCode.NotFound)!.Contains("mets à jour", StringComparison.Ordinal)
                && GuestLinks.Refused(HttpStatusCode.BadGateway) is null, "refus");
            Assert(GuestLinks.Message("https://x/Mira/#/invite/y") == "Regarde avec moi sur Mira : https://x/Mira/#/invite/y", "message");
            var listed = System.Text.Json.JsonSerializer.Deserialize<GuestLinkList>("""{"address":"https://pc.ts.net","invites":[],"tunnel":{"state":"open","at":"2026-10-29T09:33:29+00:00","text":"Le tunnel laisse passer plus que Mira."}}""")!;
            Assert(listed.Tunnel is { State: "open" } && GuestLinks.AddressLine(listed) == "Les liens commencent par https://pc.ts.net.\nLe tunnel laisse passer plus que Mira.", "adresse et tunnel : " + GuestLinks.AddressLine(listed));
            Assert(GuestLinks.AddressLine(listed with { Tunnel = null }) == "Les liens commencent par https://pc.ts.net." && GuestLinks.AddressLine(listed with { Address = null }) is null, "adresse seule, ou rien");
            return Task.CompletedTask;
        });
        await Test("Party d’un autre Jellyfin : invité ouvert par la passerelle, sans compte", async () =>
        {
            string? sent = null; Uri? asked = null;
            var answer = """{"token":"t0k","userId":"u1","userName":"Léa","serverId":"s1","host":"Sasha"}""";
            var status = HttpStatusCode.OK; var body = answer; var type = "application/json";
            using var client = new JellyfinClient(new("https://pc.tail1234.ts.net/", "", "", "", "0123456789abcdef0123456789abcdef"), new Handler(async request =>
            {
                asked = request.RequestUri; sent = await request.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, type) };
            }));
            var guest = await client.OpenPartyAsync("AbCdEfGhIjKlMnOpQr_-12", " Léa ");
            Assert(asked?.AbsoluteUri == "https://pc.tail1234.ts.net/Mira/guest/open", "adresse : " + asked);
            using (var doc = System.Text.Json.JsonDocument.Parse(sent!))
                Assert(doc.RootElement.GetProperty("party").GetString() == "AbCdEfGhIjKlMnOpQr_-12" && doc.RootElement.GetProperty("guest").GetString() == "Léa"
                    && doc.RootElement.GetProperty("device").GetString() == "0123456789abcdef0123456789abcdef" && doc.RootElement.GetProperty("name").GetString() == JellyfinClient.DeviceName, "corps : " + sent);
            Assert(guest == new PartyGuest(new("https://pc.tail1234.ts.net/Mira/guest/", "u1", "Léa", "t0k", "0123456789abcdef0123456789abcdef"), "Sasha"), "invité : " + guest);
            Assert(Together.IsGuest(guest.Connection) && !Together.IsGuest(guest.Connection with { Server = "https://pc.tail1234.ts.net/" }), "connexion d’invité");
            Assert(Together.GuestOrigin(guest.Connection) == "https://pc.tail1234.ts.net" && Together.GuestServer("http://192.168.1.10:8096") == "http://192.168.1.10:8096/Mira/guest/", "origine et passerelle");
            Assert(Together.GuestDevice("0123456789abcdef0123456789abcdef") is { Length: 32 } device && device.All(Uri.IsHexDigit) && device == Together.GuestDevice("0123456789abcdef0123456789abcdef")
                && device != Together.GuestDevice("autre-appareil") && !device.Contains("0123456789abcdef", StringComparison.Ordinal), "appareil de l’invité");
            Assert(Together.PartyName("  Léa\n ") == "Léa" && Together.PartyName(new string('a', 40)).Length == 30 && Together.PartyName(null) == "", "prénom");
            // The extension's refusal in its words; an extension without the gateway; a code that is not one.
            status = HttpStatusCode.NotFound; body = """{"error":"Ce lien ne marche plus."}""";
            var refused = await Throws<HttpRequestException>(() => client.OpenPartyAsync("AbCdEfGhIjKlMnOpQr_-12", "Léa"));
            Assert(refused.Message == "Ce lien ne marche plus." && refused.StatusCode == HttpStatusCode.NotFound, "refus : " + refused.Message);
            body = ""; type = "text/plain";
            var older = await Throws<HttpRequestException>(() => client.OpenPartyAsync("AbCdEfGhIjKlMnOpQr_-12", "Léa"));
            Assert(older.Message.Contains("mettre Mira à jour", StringComparison.Ordinal), "extension ancienne : " + older.Message);
            await Throws<UserInputException>(() => client.OpenPartyAsync("court", "Léa"));
        });
    }
}
