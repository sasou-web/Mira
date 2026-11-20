using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// « Le foyer » away from home (HomeTailnet, TailscaleApi against a fake Tailscale): the policy read carefully before
/// every link (the default « * » refused, the tag limited to Jellyfin's port accepted, any way around it refused), the
/// home link and what it carries, the nodes deleted when their device leaves, the home is turned off, or a key was used
/// by someone who never typed the password, and only nodes of the home's tag.
/// </summary>
internal static class HomeTailnetTests
{
    private const string Server = "100.88.12.34";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Foyer loin de chez soi : la politique Tailscale limite les appareils du foyer à Jellyfin", () =>
        {
            static HomeTailnet.Verdict Check(string json) => HomeTailnet.CheckPolicy(JsonNode.Parse(json), Server, 8096).Verdict;
            const string Owners = "\"tagOwners\": {\"tag:mira-home\": [\"autogroup:admin\"]}";
            const string Grant = "{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]}";

            // A new tailnet's policy: everyone everywhere, the tag too.
            Assert(Check("{\"acls\": [{\"action\": \"accept\", \"src\": [\"*\"], \"dst\": [\"*:*\"]}]}") == HomeTailnet.Verdict.NoTag, "No tag in tagOwners");
            var open = HomeTailnet.CheckPolicy(JsonNode.Parse("{" + Owners + ", \"acls\": [{\"action\": \"accept\", \"src\": [\"*\"], \"dst\": [\"*:*\"]}], \"grants\": [" + Grant + "]}"), Server, 8096);
            Assert(open is (HomeTailnet.Verdict.TooOpen, [{ Section: "acls", Number: 1 }]), "The default « * » lets the tag go everywhere");

            // What Mira asks the administrator to write: « * » narrowed to people, and the one grant.
            var (tagOwners, grant) = HomeTailnet.Setup(Server, 8096);
            Assert(grant == Grant && tagOwners == "\"tag:mira-home\": [\"autogroup:admin\"]", "The setup Mira shows");
            Assert(Check("{" + Owners + ", \"acls\": [{\"action\": \"accept\", \"src\": [\"autogroup:member\", \"autogroup:shared\"], \"dst\": [\"*:*\"]}], \"grants\": [" + Grant + "]}") == HomeTailnet.Verdict.Ok, "People everywhere, the tag on Jellyfin only");
            Assert(Check("{" + Owners + ", \"grants\": [{\"src\": [\"group:famille\", \"alex@example.com\", \"tag:server\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}, " + Grant + "]}") == HomeTailnet.Verdict.Ok, "Groups, users and other tags never hold the tag");
            Assert(Check("{" + Owners + ", \"acls\": [{\"action\": \"accept\", \"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34:8096\"]}]}") == HomeTailnet.Verdict.Ok, "The same as an ACL");
            Assert(Check("{" + Owners + ", \"hosts\": {\"jellyfin\": \"100.88.12.34\"}, \"ACLs\": [{\"action\": \"accept\", \"src\": [\"tag:mira-home\"], \"dst\": [\"jellyfin:8096\"]}]}") == HomeTailnet.Verdict.Ok, "A host's name, sections in any case");
            Assert(Check("{" + Owners + ", \"grants\": [{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34/32\"], \"ip\": [\"8096\"]}]}") == HomeTailnet.Verdict.Ok, "The address alone, the port without its protocol");
            Assert(Check("{" + Owners + ", \"acls\": [{\"action\": \"accept\", \"src\": [\"autogroup:member\"], \"dst\": [\"*:*\"]}]}") == HomeTailnet.Verdict.NoAccess, "Nothing lets the tag reach Jellyfin");

            // Every way around it refused.
            foreach (var (rule, why) in new[]
            {
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"*\"]}", "Every port of the PC"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\", \"tcp:22\"]}", "One more port"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8000-9000\"]}", "A range of ports"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.0/24\"], \"ip\": [\"tcp:8096\"]}", "A range of addresses"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"autogroup:internet\"], \"ip\": [\"*\"]}", "An exit node"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"], \"via\": [\"tag:router\"]}", "Through a router"),
                ("{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"app\": {\"tailscale.com/cap/drive\": [{\"shares\": [\"*\"]}]}}", "An app capability"),
                ("{\"src\": [\"autogroup:tagged\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}", "Every tagged device"),
                ("{\"src\": [\"100.64.0.0/10\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}", "The tailnet's range"),
                ("{\"src\": [\"100.101.1.2\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}", "An address that may become a device of the home"),
                ("{\"src\": [\"ipset:famille\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}", "Something not understood"),
            })
                Assert(Check("{" + Owners + ", \"grants\": [" + rule + "]}") == HomeTailnet.Verdict.TooOpen, why);
            Assert(Check("{" + Owners + ", \"grants\": [" + Grant + "], \"ssh\": [{\"action\": \"accept\", \"src\": [\"*\"], \"dst\": [\"autogroup:self\"], \"users\": [\"root\"]}]}") == HomeTailnet.Verdict.TooOpen, "An SSH rule from anyone");
            Assert(Check("{" + Owners + ", \"grants\": [" + Grant + ", {\"src\": [\"192.168.1.0/24\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}") == HomeTailnet.Verdict.Ok, "A home network's range is not the tailnet's");
            Assert(Check("{" + Owners + ", \"acls\": [{\"action\": \"accept\", \"src\": [\"tag:mira-home\"], \"proto\": \"udp\", \"dst\": [\"100.88.12.34:8096\"]}]}") == HomeTailnet.Verdict.TooOpen, "UDP");
            Assert(HomeTailnet.CheckPolicy(null, Server, 8096).Verdict == HomeTailnet.Verdict.NoAccess, "No policy read");
            return Task.CompletedTask;
        });

        await Test("Foyer loin de chez soi : Mira corrige la politique Tailscale elle-même, commentaires gardés", async () =>
        {
            static HomeTailnet.Verdict Verdict(string text) => HomeTailnet.CheckPolicy(HomePolicyFix.Parse(text), Server, 8096).Verdict;

            // A new tailnet's policy, as Tailscale writes it (comments, trailing commas, tabs), with only the tag added
            // by the visual editor: Mira narrows « * » to people and adds the rule to Jellyfin.
            const string Fresh = "// Example/default ACLs for unrestricted connections.\n{\n\t// Declare static groups of users.\n\t\"tagOwners\": {\n\t\t\"tag:mira-home\": [\"autogroup:admin\"],\n\t\t\"tag:server\": [\"autogroup:admin\"],\n\t},\n\n"
                + "\t\"grants\": [\n\t\t// Allow all connections.\n\t\t{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},\n\t],\n\n"
                + "\t\"ssh\": [\n\t\t{\n\t\t\t\"action\": \"check\",\n\t\t\t\"src\":    [\"autogroup:member\"],\n\t\t\t\"dst\":    [\"autogroup:self\"],\n\t\t\t\"users\":  [\"autogroup:nonroot\", \"root\"],\n\t\t},\n\t],\n}\n";
            Assert(Verdict(Fresh) == HomeTailnet.Verdict.TooOpen, "The default « * » is too open");
            var plan = HomePolicyFix.Make(Fresh, Server, 8096);
            Assert(plan is not null && Verdict(plan.Text) == HomeTailnet.Verdict.Ok, "Put right");
            Assert(plan!.Changes.Select(x => x.Kind).SequenceEqual(["narrow", "grant"]) && plan.Changes[0] is { Section: "grants", Number: 1, Sources: ["autogroup:member", "autogroup:shared", "tag:server"] }, "What changed, as the settings show it");
            Assert(plan.Text.Contains("// Example/default ACLs for unrestricted connections.", StringComparison.Ordinal) && plan.Text.Contains("\t\t// Allow all connections.\n", StringComparison.Ordinal)
                && plan.Text.Contains("\"src\":    [\"autogroup:member\"]", StringComparison.Ordinal), "Comments and spacing kept");
            Assert(plan.Text.Contains("\t\"grants\": [\n\t\t{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]},\n\t\t// Allow all connections.", StringComparison.Ordinal), "The rule first, at the same indent");
            Assert(HomePolicyFix.Make(plan.Text, Server, 8096) is null, "Nothing more once right");

            // ogsas's paste of 2026-11-19, put right by hand: the tag and the rule there, « * » still open.
            const string Pasted = "{\n\t\"tagOwners\": {\n\t\t\"tag:mira-home\": [\"autogroup:admin\"],\n\t},\n\n\t\"grants\": [\n\t\t{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},\n"
                + "\t\t{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]},\n\t],\n}";
            var pasted = HomePolicyFix.Make(Pasted, Server, 8096);
            Assert(pasted is { Changes: [{ Kind: "narrow", Sources: ["autogroup:member", "autogroup:shared"] }] } && Verdict(pasted.Text) == HomeTailnet.Verdict.Ok, "Only « * » narrowed");

            // No tagOwners nor grants at all, ACLs of the old kind: both sections added, the ACL narrowed.
            const string Old = "{\n  \"acls\": [\n    {\"action\": \"accept\", \"src\": [\"*\"], \"dst\": [\"*:*\"]}, // everyone\n  ],\n}";
            var old = HomePolicyFix.Make(Old, Server, 8096);
            Assert(old is not null && Verdict(old.Text) == HomeTailnet.Verdict.Ok && old.Changes.Select(x => x.Kind).SequenceEqual(["tag", "narrow", "grant"]), "Old ACLs, nothing of the tag");
            Assert(old!.Text.Contains("\n  \"tagOwners\": {\n    \"tag:mira-home\": [\"autogroup:admin\"],\n  },", StringComparison.Ordinal) && old.Text.Contains("// everyone", StringComparison.Ordinal), "At the policy's own indent");

            // autogroup:tagged with other sources: the other tags instead; with people kept as they were.
            const string Tagged = "{\"tagOwners\": {\"tag:mira-home\": [], \"tag:nas\": []}, \"grants\": [{\"src\": [\"group:famille\", \"autogroup:tagged\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}";
            var tagged = HomePolicyFix.Make(Tagged, Server, 8096);
            Assert(tagged is not null && Verdict(tagged.Text) == HomeTailnet.Verdict.Ok && tagged.Changes[0].Sources is ["group:famille", "tag:nas"], "autogroup:tagged becomes the other tags");
            var alone = HomePolicyFix.Make("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [{\"src\": [\"group:famille\", \"autogroup:tagged\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}", Server, 8096);
            Assert(alone is not null && Verdict(alone.Text) == HomeTailnet.Verdict.Ok && alone.Changes[0].Sources is ["group:famille"] && !alone.Text.Contains("tagged", StringComparison.Ordinal), "No other tag: it goes");

            // Never a guess: the tag named with every port, a range of the tailnet, an SSH rule from anyone, a broken policy.
            foreach (var (text, why) in new[]
            {
                ("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [{\"src\": [\"tag:mira-home\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}", "The tag itself everywhere"),
                ("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [{\"src\": [\"100.64.0.0/10\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}", "The tailnet's range"),
                ("{\"tagOwners\": {\"tag:mira-home\": []}, \"ssh\": [{\"action\": \"accept\", \"src\": [\"*\"], \"dst\": [\"autogroup:self\"], \"users\": [\"root\"]}]}", "SSH from anyone"),
                ("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}", "Not closed"),
            })
                Assert(HomePolicyFix.Make(text, Server, 8096) is null, why);
            Assert(HomePolicyFix.Make(plan.Text, Server, 8096) is null, "Already right");

            // Tailscale's API: the policy read as HuJSON with its version, written back only at that version.
            var calls = new List<string>();
            var changed = false;
            using var http = new HttpClient(new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {request.Headers.Accept} {request.Content?.Headers.ContentType} {(request.Headers.TryGetValues("If-Match", out var match) ? string.Join(",", match) : "")} {body}");
                HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                switch (request.RequestUri.AbsolutePath)
                {
                    case "/api/v2/oauth/token":
                        return Json(body.Contains("tskey-client-all", StringComparison.Ordinal) ? "{\"access_token\": \"tskey-api-all\", \"expires_in\": 3600, \"scope\": \"all\"}"
                            : "{\"access_token\": \"tskey-api-temp\", \"expires_in\": 3600, \"scope\": \"auth_keys devices:core policy_file\"}");
                    case "/api/v2/tailnet/-/acl" when request.Method == HttpMethod.Get:
                        var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fresh, Encoding.UTF8, "application/hujson") };
                        answer.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
                        return answer;
                    case "/api/v2/tailnet/-/acl":
                        return changed ? Json("{\"message\": \"precondition failed\"}", HttpStatusCode.PreconditionFailed) : Json("{}");
                    default:
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            }));
            var api = new TailscaleApi(http, "kFix" + Guid.NewGuid().ToString("N"), "tskey-client-secret", "https://api.example.test");
            Assert(!TailscaleApi.TooBroad(await api.CheckAsync(CancellationToken.None)), "The scopes the home needs");
            Assert(TailscaleApi.TooBroad(await new TailscaleApi(http, "kAll" + Guid.NewGuid().ToString("N"), "tskey-client-all", "https://api.example.test").CheckAsync(CancellationToken.None)), "« All » refused: its secret stays on the server");
            Assert(!TailscaleApi.TooBroad(null) && !TailscaleApi.TooBroad(["auth_keys", "devices:core:read", "policy_file:read", "devices:posture_attributes"]) && TailscaleApi.TooBroad(["auth_keys", "dns"]), "Silent, reads, one more");
            var (read, version) = await api.PolicyTextAsync(CancellationToken.None);
            Assert(read == Fresh && version == "\"v1\"" && calls[^1].Contains("application/hujson", StringComparison.Ordinal), "Read as HuJSON, with its version");
            await api.SetPolicyAsync(plan.Text, version, CancellationToken.None);
            Assert(calls[^1].StartsWith("POST /api/v2/tailnet/-/acl", StringComparison.Ordinal) && calls[^1].Contains("application/hujson \"v1\"", StringComparison.Ordinal) && calls[^1].EndsWith(plan.Text, StringComparison.Ordinal), "Written as HuJSON, at that version");
            changed = true;
            try
            {
                await api.SetPolicyAsync(plan.Text, version, CancellationToken.None);
                Assert(false, "Changed meanwhile");
            }
            catch (TailscaleApi.TailscaleException error)
            {
                Assert(error.Failure == TailscaleApi.Failure.Changed, "Changed meanwhile");
            }
        });

        await Test("Foyer loin de chez soi : « Make the changes » limite les amis partagés à Jellyfin, autorise Funnel sur ce PC, garde la clé du PC", async () =>
        {
            static HomePolicyFix.Plan? Harden(string text, bool funnel = true) => HomePolicyFix.Make(text, Server, 8096, harden: true, funnel: funnel);
            static bool SharedOk(string text) => HomeTailnet.CheckShared(HomePolicyFix.Parse(text), Server, 8096) is (true, []);
            static HomeTailnet.Verdict Verdict(string text) => HomeTailnet.CheckPolicy(HomePolicyFix.Parse(text), Server, 8096).Verdict;
            const string Home = "{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]}";
            const string Shared = "{\"src\": [\"autogroup:shared\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\", \"tcp:443\"]}";

            // A policy put right by hand on 2026-11-19 (home and friends' tags, people everywhere): the home is right, the
            // people this PC is shared with still reach every port of it.
            const string ByHand = "{\n\t\"tagOwners\": {\n\t\t\"tag:mira-home\": [\"autogroup:admin\"],\n\t\t\"tag:mira-friend\": [\"autogroup:admin\", \"tag:mira-home\"],\n\t},\n\n\t\"grants\": [\n"
                + "\t\t// People.\n\t\t{\"src\": [\"autogroup:member\", \"autogroup:shared\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},\n\t\t" + Home + ",\n"
                + "\t\t{\"src\": [\"tag:mira-friend\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]},\n\t],\n}\n";
            Assert(Verdict(ByHand) == HomeTailnet.Verdict.Ok && HomeTailnet.CheckShared(HomePolicyFix.Parse(ByHand), Server, 8096) is (false, [{ Section: "grants", Number: 1 }]), "The home right, friends shared too far");
            Assert(HomePolicyFix.Make(ByHand, Server, 8096) is null, "Not hardened (a friend's first link): nothing more than before");
            var plan = Harden(ByHand);
            Assert(plan is not null && plan.Changes.Select(x => x.Kind).SequenceEqual(["unshare", "shared", "funnel"]) && plan.Changes[0] is { Section: "grants", Number: 1, Sources: ["autogroup:member"] },
                "Shared friends out of the wide rule, their own rule, Funnel: " + string.Join(", ", plan?.Changes.Select(x => x.Kind) ?? []));
            Assert(Verdict(plan!.Text) == HomeTailnet.Verdict.Ok && HomeTailnet.CheckPolicy(HomePolicyFix.Parse(plan.Text), Server, 8096, HomeTailnet.FriendTag).Verdict == HomeTailnet.Verdict.Ok && SharedOk(plan.Text), "Home, friends' tag and shared friends each on Jellyfin");
            Assert(plan.Text.Contains("\t\t// People.\n\t\t{\"src\": [\"autogroup:member\"], \"dst\": [\"*\"]", StringComparison.Ordinal) && plan.Text.Contains("\t\t" + Shared + ",\n", StringComparison.Ordinal)
                && plan.Text.Contains("\"tag:mira-friend\": [\"autogroup:admin\", \"tag:mira-home\"]", StringComparison.Ordinal), "Comments and both tags kept, the rule at the same indent");
            Assert(HomePolicyFix.Parse(plan.Text)!["nodeAttrs"] is JsonArray { Count: 1 } attrs && attrs[0]!.ToJsonString() == "{\"target\":[\"100.88.12.34\"],\"attr\":[\"funnel\"]}", "Funnel for this PC only");
            Assert(plan.Lines.Any(x => x.Contains("autogroup:shared", StringComparison.Ordinal)) && plan.Lines.Any(x => x.Contains("funnel", StringComparison.Ordinal)), "Its lines shown before the click");
            Assert(Harden(plan.Text) is null, "Nothing more once hardened");
            Assert(Harden(ByHand, funnel: false) is { Changes: [{ Kind: "unshare" }, { Kind: "shared" }] }, "Funnel refused by Tailscale once: the rest without it");

            // A new tailnet's « * »: people only, shared friends their own rule, the home's rule, Funnel.
            const string Fresh = "{\"tagOwners\": {\"tag:mira-home\": [], \"tag:nas\": []}, \"grants\": [{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}]}";
            var fresh = Harden(Fresh);
            Assert(fresh is not null && fresh.Changes.Select(x => x.Kind).SequenceEqual(["narrow", "grant", "shared", "funnel"]) && fresh.Changes[0].Sources is ["autogroup:member", "tag:nas"]
                && Verdict(fresh.Text) == HomeTailnet.Verdict.Ok && SharedOk(fresh.Text), "« * » hardened: " + string.Join(", ", fresh?.Changes.Select(x => x.Kind) ?? []));
            Assert(Mira.Core.Family.HomeGuide.Change(new("narrow", "grants", 1, ["autogroup:member"])).Contains("only your own", StringComparison.Ordinal)
                || Mira.Core.Family.HomeGuide.Change(new("narrow", "grants", 1, ["autogroup:member"])).Contains("tes propres appareils.", StringComparison.Ordinal), "Narrowed to people only, said so");

            // A rule for shared friends alone goes; one already limited to Jellyfin stays; no sharing at all, nothing added.
            var alone = Harden("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [" + Home + ", {\"src\": [\"autogroup:shared\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},], \"nodeAttrs\": [{\"target\": [\"autogroup:member\"], \"attr\": [\"funnel\"]}]}");
            Assert(alone is { Changes: [{ Kind: "unshare", Number: 2, Sources: [] }, { Kind: "shared" }] } && SharedOk(alone.Text) && Verdict(alone.Text) == HomeTailnet.Verdict.Ok, "Their rule removed, Funnel already given");
            Assert(Harden("{\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [" + Home + ", " + Shared + ", {\"src\": [\"autogroup:member\"], \"dst\": [\"*\"], \"ip\": [\"*\"]}], \"nodeAttrs\": [{\"target\": [\"100.88.12.34\"], \"attr\": [\"funnel\"]}]}") is null, "Already hardened");
            Assert(Harden("{\"tagOwners\": {\"tag:mira-home\": []}, \"acls\": [{\"action\": \"accept\", \"src\": [\"autogroup:member\"], \"dst\": [\"*:*\"]}], \"grants\": [" + Home + "], \"nodeAttrs\": [{\"target\": [\"autogroup:member\"], \"attr\": [\"funnel\"]}]}") is null, "Nobody shared with: no rule for them");
            var acl = Harden("{\"tagOwners\": {\"tag:mira-home\": []}, \"acls\": [{\"action\": \"accept\", \"src\": [\"autogroup:shared\", \"group:famille\"], \"dst\": [\"*:*\"]}], \"grants\": [" + Home + "]}", funnel: false);
            Assert(acl is { Changes: [{ Kind: "unshare", Section: "acls", Sources: ["group:famille"] }, { Kind: "shared" }] } && SharedOk(acl.Text), "Old ACLs too");
            Assert(Harden("{\"tagOwners\": {\"tag:mira-home\": []}, \"acls\": [{\"action\": \"accept\", \"src\": [\"autogroup:shared\"], \"dst\": [\"100.88.12.34:8096,443\"]}], \"grants\": [" + Home + "]}", funnel: false) is null, "An ACL to Jellyfin and Funnel's port is enough");

            // This PC's key: read from the devices' list, made to never expire.
            var devices = TailscaleApi.ReadDevices(System.Text.Json.Nodes.JsonNode.Parse("{\"devices\": [{\"id\": \"n1\", \"addresses\": [\"100.88.12.34\"], \"keyExpiryDisabled\": false}, {\"id\": \"n2\", \"addresses\": [\"100.88.12.35\"], \"keyExpiryDisabled\": true}, {\"id\": \"n3\", \"addresses\": []}]}"));
            Assert(devices is [{ KeyExpiryDisabled: false }, { KeyExpiryDisabled: true }, { KeyExpiryDisabled: null }], "keyExpiryDisabled read, or unknown");
            var calls = new List<string>();
            using var http = new HttpClient(new Handler(async request =>
            {
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {(request.Content is null ? "" : await request.Content.ReadAsStringAsync())}");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\": \"t\", \"expires_in\": 3600}" : "{}", Encoding.UTF8, "application/json") };
            }));
            var api = new TailscaleApi(http, "kKey" + Guid.NewGuid().ToString("N"), "tskey-client-secret", "https://api.example.test");
            await api.DisableKeyExpiryAsync("n1", CancellationToken.None);
            Assert(calls[^1] == "POST /api/v2/device/n1/key {\"keyExpiryDisabled\": true}", "POST /device/:id/key: " + calls[^1]);
            try
            {
                await api.DisableKeyExpiryAsync("../acl", CancellationToken.None);
                Assert(false, "Not a device's id");
            }
            catch (TailscaleApi.TailscaleException) { }

            // What the apps say: a protection to add, in warning; the policy right for home links.
            var harden = Mira.Core.Family.HomeGuide.Trouble("harden", changes: 3);
            Assert(harden is { Warn: true, Remedies: [Mira.Core.Family.HomeGuide.Remedy.Fix, Mira.Core.Family.HomeGuide.Remedy.SeeLines] } && Mira.Core.Family.HomeGuide.Trouble("too-open", changes: 2) is { Warn: false }, "Warning, not danger");
            foreach (var kind in new[] { "unshare", "shared", "funnel", "key" })
                Assert(Mira.Core.Family.HomeGuide.Change(new(kind, "grants", 1, kind == "unshare" ? [] : null)) is var said && said != kind && said.Length > 20, "Said in words: " + kind);
            Assert(Mira.Core.Family.AwayFromHome.For(new Mira.Core.Family.HomeTailnetState { Connected = true, Policy = "harden" }, true, false).Level == Mira.Core.Family.AwayLevel.Ready, "Hardening left: links still work");
        });

        await Test("Foyer loin de chez soi : le guide (secret seul, droits manquants, lignes changées, annuler 10 min, pannes en clair)", () =>
        {
            // One box: the client's id read from its secret; anything else asks the ID too.
            Assert(HomeTailnet.IdOfSecret("tskey-client-kAbC12DeFG11CNTRL-9fQ2abcdefghijklmnop") == "kAbC12DeFG11CNTRL" && HomeTailnet.IdOfSecret(" tskey-client-kAbC12DeFG11CNTRL-x ") == "kAbC12DeFG11CNTRL", "The id inside the secret");
            Assert(HomeTailnet.IdOfSecret(null) is null && HomeTailnet.IdOfSecret("tskey-auth-kAbC-x") is null && HomeTailnet.IdOfSecret("tskey-client-abc") is null && HomeTailnet.IdOfSecret("tskey-client-k-x") is null, "Not a client's secret");
            Assert(HomeTailnet.Missing(["auth_keys", "devices:core", "policy_file"]).Count == 0 && HomeTailnet.Missing(null).Count == 0 && HomeTailnet.Missing(["all"]).Count == 0, "Nothing missing (or unknown, or « All »: refused as too broad)");
            Assert(HomeTailnet.Missing(["auth_keys", "devices:core:read"]).SequenceEqual(["devices:core", "policy_file"]), "Read only is missing too");

            // The lines Mira adds or changes, shown before the click.
            var plan = HomePolicyFix.Make("{\n\t\"tagOwners\": {\n\t\t\"tag:mira-home\": [\"autogroup:admin\"],\n\t},\n\t\"grants\": [\n\t\t{\"src\": [\"*\"], \"dst\": [\"*\"], \"ip\": [\"*\"]},\n\t],\n}", Server, 8096);
            Assert(plan is { Lines: [_, _] } && plan.Lines.Any(x => x.Contains("autogroup:member", StringComparison.Ordinal)) && plan.Lines.Any(x => x.Contains("tcp:8096", StringComparison.Ordinal)), "The two lines changed: " + string.Join(" | ", plan?.Lines ?? []));

            // Undo: 10 minutes, in memory.
            var now = DateTimeOffset.Parse("2026-11-20T01:00:00Z");
            var tailnet = new HomeTailnet(null, () => now);
            Assert(tailnet.Undo is null, "Nothing to undo");
            tailnet.Fixed("avant", "après");
            Assert(tailnet.Undo is ("avant", "après", var until) && until == now + HomeTailnet.UndoFor, "Undo offered for 10 minutes");
            now += TimeSpan.FromMinutes(11);
            Assert(tailnet.Undo is null, "Too late");
            tailnet.Fixed("avant", "après");
            tailnet.Undone();
            Assert(tailnet.Undo is null, "Undone once");

            // What the apps say: one sentence and the button that fixes it.
            Assert(Mira.Core.Family.HomeGuide.LooksLikeSecret("tskey-client-kAbC12DeFG11CNTRL-9fQ2abcdef") && !Mira.Core.Family.HomeGuide.LooksLikeSecret("tskey-auth-kAbC12DeFG11CNTRL-9fQ2abcdef")
                && !Mira.Core.Family.HomeGuide.LooksLikeSecret("tskey-client-k x") && !Mira.Core.Family.HomeGuide.LooksLikeSecret(null), "A secret on the clipboard");
            Assert(Mira.Core.Family.HomeGuide.Masked("tskey-client-kAbC12DeFG11CNTRL-9fQ2abcdef") == "tskey-client-kAbC12…••••••••", "Shown without its secret part");
            static IReadOnlyList<Mira.Core.Family.HomeGuide.Remedy>? Remedies(string? code, IReadOnlyList<string>? missing = null, bool broad = false, int changes = 0) => Mira.Core.Family.HomeGuide.Trouble(code, missing, broad, changes)?.Remedies;
            Assert(Remedies("ok") is null && Remedies(null) is null, "Nothing wrong");
            Assert(Remedies("ok", broad: true) is [Mira.Core.Family.HomeGuide.Remedy.RedoKey, Mira.Core.Family.HomeGuide.Remedy.OpenCredentials] && Remedies("too-broad") is [Mira.Core.Family.HomeGuide.Remedy.RedoKey, _], "« All »: redo step 2");
            Assert(Remedies("missing", ["policy_file"]) is [Mira.Core.Family.HomeGuide.Remedy.RedoKey] && Mira.Core.Family.HomeGuide.Trouble("missing", ["auth_keys"])!.Title.Contains("Keys › Auth Keys", StringComparison.Ordinal), "A tick missing, named as the console names it");
            Assert(Remedies("too-open", changes: 2) is [Mira.Core.Family.HomeGuide.Remedy.Fix, Mira.Core.Family.HomeGuide.Remedy.SeeLines] && Remedies("too-open") is [Mira.Core.Family.HomeGuide.Remedy.OpenPolicy, _], "Rules: fixed by Mira, or not");
            Assert(Remedies("refused") is [Mira.Core.Family.HomeGuide.Remedy.SetUpAgain] && Remedies("no-tailscale") is [Mira.Core.Family.HomeGuide.Remedy.CheckAgain] && Remedies("no-write") is [Mira.Core.Family.HomeGuide.Remedy.RedoKey], "Each trouble its button");
            var checks = Mira.Core.Family.HomeGuide.Checks(new Mira.Core.Family.HomeTailnetState { Connected = true, Policy = "too-open", Tag = false, Fix = [new("tag", null, 0, null), new("narrow", "grants", 1, ["autogroup:member"])] });
            Assert(checks.Select(x => x.Mark).SequenceEqual([Mira.Core.Family.HomeGuide.Mark.Ok, Mira.Core.Family.HomeGuide.Mark.Ok, Mira.Core.Family.HomeGuide.Mark.Warn, Mira.Core.Family.HomeGuide.Mark.Warn]), "Step 4: the key, its rights, the tag, the rules");
            Assert(Mira.Core.Family.HomeGuide.Drawing(2).Lines.Count(x => x.On && x.Kind == Mira.Core.Family.HomeGuide.SketchKind.Scope) == 3, "The three ticks drawn");
            return Task.CompletedTask;
        });

        await Test("Foyer loin de chez soi : le lien du foyer et les appareils retirés du réseau Tailscale", async () =>
        {
            var data = new HomeTailnet.LinkData(1, "Maison", "f0a1b2c3d4e5f60718293a4b5c6d7e8f", "http://100.88.12.34:8096", "tskey-auth-kAbC123-xyz", 1_800_000_000);
            var link = HomeTailnet.Link(data);
            Assert(link.StartsWith("https://miracinema.app/home#", StringComparison.Ordinal) && !link[(link.IndexOf('#') + 1)..].Contains('=') && !link.Contains('+') && link.IndexOf('/', 28) < 0, "A link to the site, its data after the « # »");
            Assert(HomeTailnet.ReadLink(link) == data && HomeTailnet.ReadLink("mira://home#" + link[(link.IndexOf('#') + 1)..]) == data, "Read back, by the site or Mira");
            Assert(HomeTailnet.ReadLink("https://miracinema.app/home") is null && HomeTailnet.ReadLink("https://miracinema.app/home#pas-du-json") is null && HomeTailnet.ReadLink(null) is null, "Anything else");

            Assert(HomeTailnet.TailnetAddress("100.101.1.2") == "100.101.1.2" && HomeTailnet.TailnetAddress("::ffff:100.101.1.2") == "100.101.1.2"
                && HomeTailnet.TailnetAddress("fd7a:115c:a1e0::1") is not null && HomeTailnet.TailnetAddress("192.168.1.42") is null
                && HomeTailnet.TailnetAddress("100.128.0.1") is null && HomeTailnet.TailnetAddress("pas une adresse") is null, "Tailscale's addresses");

            // The home's file: the device joined through the tailnet keeps its address.
            var now = DateTimeOffset.Parse("2026-11-19T20:00:00Z");
            var home = new HomeFamily(null, () => now, iterations: 1000);
            home.SetPassword("famille-ok");
            var salon = home.Join("famille-ok", "device-salon", "Salon", "100.101.1.2").Device!;
            var maison = home.Join("famille-ok", "device-maison", "Maison", "192.168.1.42").Device!;
            Assert(salon.Tailnet == "100.101.1.2" && maison.Tailnet is null, "Joined through the tailnet, or not");

            var file = Path.Combine(run.Root, "home-tailnet", "Mira.home-tailnet.json");
            var tailnet = new HomeTailnet(file, () => now);
            Assert(tailnet.Client is null && !HomeTailnet.GoodClient("k123", "court") && !HomeTailnet.GoodClient("k 123", "tskey-client-secret") && HomeTailnet.GoodClient("kAbC123CNTRL", "tskey-client-kAbC123CNTRL-secret"), "The OAuth client");
            tailnet.SetClient("kAbC123CNTRL", "tskey-client-kAbC123CNTRL-secret");
            Assert(new HomeTailnet(file, () => now).Client == ("kAbC123CNTRL", "tskey-client-kAbC123CNTRL-secret"), "Read back after a restart");

            TailnetDevice Node(string id, string address, bool tagged = true, int days = 1) => new(id, id, [address, "fd7a:115c:a1e0::" + id.Length], tagged, now - TimeSpan.FromDays(days), now);
            var nodes = new[] { Node("salon", "100.101.1.2"), Node("jellyfin", Server, tagged: false, days: 400), Node("inconnu", "100.101.9.9", days: 8), Node("recent", "100.101.7.7", days: 2), Node("ami", "100.101.8.8", tagged: false, days: 30) };
            var joined = home.Devices().Select(x => x.Tailnet).OfType<string>().ToList();
            Assert(tailnet.ToDelete(nodes, joined).Select(x => x.Id).SequenceEqual(["inconnu"]), "A key used by someone who never joined, after a week; never a device without the tag");
            tailnet.Remove("100.101.1.2");
            tailnet.Remove("192.168.1.42");
            Assert(tailnet.Leaving() is ({ Count: 1 }, false) && tailnet.ToDelete(nodes, joined).Select(x => x.Id).SequenceEqual(["salon", "inconnu"]), "The device that left");
            tailnet.Left(["100.101.1.2"], false);
            tailnet.RemoveAll();
            Assert(tailnet.ToDelete(nodes, joined).Select(x => x.Id).SequenceEqual(["salon", "inconnu", "recent"]), "The home turned off: every node of the tag");
            tailnet.Left([], true);
            Assert(new HomeTailnet(file, () => now).Leaving() is ({ Count: 0 }, false), "Done with");

            // Tailscale's API, faked.
            var calls = new List<string>();
            var deviceList = "{\"devices\": [{\"id\": \"n1\", \"hostname\": \"mira-salon\", \"addresses\": [\"100.101.1.2\"], \"tags\": [\"tag:mira-home\"], \"created\": \"2026-11-18T10:00:00Z\", \"lastSeen\": \"2026-11-19T19:00:00Z\"},"
                + " {\"id\": \"n2\", \"hostname\": \"jellyfin\", \"addresses\": [\"100.88.12.34\"], \"created\": \"2025-01-01T00:00:00Z\"}, {\"hostname\": \"sans-id\"}]}";
            var refuse = false;
            using var http = new HttpClient(new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath} {request.Headers.Authorization} {body}");
                HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                return request.RequestUri.AbsolutePath switch
                {
                    "/api/v2/oauth/token" when refuse => Json("{\"message\": \"invalid client\"}", HttpStatusCode.Unauthorized),
                    "/api/v2/oauth/token" => Json("{\"access_token\": \"tskey-api-temp\", \"token_type\": \"Bearer\", \"expires_in\": 3600}"),
                    "/api/v2/tailnet/-/keys" => Json("{\"id\": \"kNew\", \"key\": \"tskey-auth-kNew-secret\", \"expires\": \"2026-11-21T20:00:00Z\"}"),
                    "/api/v2/tailnet/-/devices" => Json(deviceList),
                    "/api/v2/tailnet/-/acl" => Json("{" + "\"tagOwners\": {\"tag:mira-home\": []}, \"grants\": [{\"src\": [\"tag:mira-home\"], \"dst\": [\"100.88.12.34\"], \"ip\": [\"tcp:8096\"]}]}"),
                    "/api/v2/device/n1" => new HttpResponseMessage(HttpStatusCode.OK),
                    "/api/v2/device/gone" => Json("{\"message\": \"not found\"}", HttpStatusCode.NotFound),
                    _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                };
            }));
            var api = new TailscaleApi(http, "kTest" + Guid.NewGuid().ToString("N"), "tskey-client-secret", "https://api.example.test", () => now);
            var (key, id, expires) = await api.CreateKeyAsync(HomeTailnet.LinkLife, CancellationToken.None);
            Assert(key == "tskey-auth-kNew-secret" && id == "kNew" && expires == DateTimeOffset.Parse("2026-11-21T20:00:00Z"), "A key");
            Assert(calls[0].StartsWith("POST /api/v2/oauth/token", StringComparison.Ordinal) && calls[0].Contains("grant_type=client_credentials", StringComparison.Ordinal) && calls[0].Contains("client_secret=tskey-client-secret", StringComparison.Ordinal), "The token asked with the client's secret");
            var asked = JsonNode.Parse(calls[1][(calls[1].IndexOf('{'))..])!;
            Assert(calls[1].Contains("Bearer tskey-api-temp", StringComparison.Ordinal) && asked["expirySeconds"]!.GetValue<long>() == 172_800
                && asked["capabilities"]!["devices"]!["create"]!["reusable"]!.GetValue<bool>() == false && asked["capabilities"]!["devices"]!["create"]!["preauthorized"]!.GetValue<bool>()
                && asked["capabilities"]!["devices"]!["create"]!["ephemeral"]!.GetValue<bool>() == false && asked["capabilities"]!["devices"]!["create"]!["tags"]![0]!.GetValue<string>() == "tag:mira-home", "Single use, preauthorized, kept, the tag, 48 hours");
            Assert(HomeTailnet.CheckPolicy(await api.PolicyAsync(CancellationToken.None), Server, 8096).Verdict == HomeTailnet.Verdict.Ok && calls.Count(x => x.Contains("oauth", StringComparison.Ordinal)) == 1, "The policy, with the same token");
            var read = await api.DevicesAsync(CancellationToken.None);
            Assert(read is [{ Id: "n1", Name: "mira-salon", Tagged: true, Addresses: ["100.101.1.2"] }, { Id: "n2", Tagged: false }], "The devices");
            Assert(await api.DeleteAsync(read[0], CancellationToken.None) && !await api.DeleteAsync(read[1], CancellationToken.None) && !calls.Any(x => x.Contains("/device/n2", StringComparison.Ordinal)), "Only a node of the tag is deleted");
            Assert(await api.DeleteAsync(read[0] with { Id = "gone" }, CancellationToken.None), "Already gone");

            refuse = true;
            var refused = new TailscaleApi(http, "kOther" + Guid.NewGuid().ToString("N"), "tskey-client-wrong", "https://api.example.test", () => now);
            try
            {
                await refused.CheckAsync(CancellationToken.None);
                Assert(false, "A refused client");
            }
            catch (TailscaleApi.TailscaleException error)
            {
                Assert(error.Failure == TailscaleApi.Failure.Refused, "A refused client");
            }
        });
    }
}
