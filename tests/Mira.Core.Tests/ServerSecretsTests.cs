using System.IO;
using System.Text.Json.Nodes;
using Mira.Jellyfin;
using static TestSupport;

/// <summary>
/// The extension's secrets on the server (ServerSecrets): sealed by DPAPI on Windows, its account's alone elsewhere,
/// the files of 0.9.15 and older (in clear, a password kept for each guest) read and written again at once.
/// </summary>
internal static class ServerSecretsTests
{
    private const string Item = "90892981f28a0ccb86a6e55a3a0039c6";

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Secrets du serveur : scellés sous Windows, rendus tels quels, jamais lus d’un autre compte", () =>
        {
            var sealedText = ServerSecrets.Seal("tskey-client-secret");
            Assert(ServerSecrets.Open(sealedText) == "tskey-client-secret" && ServerSecrets.Seal("") == "" && ServerSecrets.Open(null) is null, "Read back");
            Assert(OperatingSystem.IsWindows()
                ? sealedText.StartsWith(ServerSecrets.Prefix, StringComparison.Ordinal) && !sealedText.Contains("secret", StringComparison.Ordinal) && ServerSecrets.Seal("tskey-client-secret") != sealedText
                : sealedText == "tskey-client-secret", "Sealed on Windows only: " + sealedText);
            ServerSecrets.Plain();
            Assert(ServerSecrets.Open("dpapi:AAAAAAAAAAAAAAAAAAAAAAAA") is null && ServerSecrets.Open("dpapi:pas du base64") is null, "Sealed elsewhere or damaged: nothing");
            Assert(!ServerSecrets.Plain() && ServerSecrets.Open("en clair") == "en clair" && ServerSecrets.Plain() && !ServerSecrets.Plain(), "A secret in clear counted, once");
            return Task.CompletedTask;
        });

        await Test("Secrets du serveur : les fichiers de la 0.9.15 scellés au démarrage, mots de passe des invités oubliés", () =>
        {
            var now = DateTimeOffset.Parse("2026-11-20T03:00:00Z");
            var root = Path.Combine(run.Root, "server-secrets");
            Directory.CreateDirectory(root);
            string Old(string name, string text)
            {
                var file = Path.Combine(root, name);
                File.WriteAllText(file, text);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("644", 8));
                return file;
            }

            void Closed(string file, params string[] secrets)
            {
                var text = File.ReadAllText(file);
                if (OperatingSystem.IsWindows())
                    Assert(secrets.All(x => !text.Contains(x, StringComparison.Ordinal)) && text.Contains(ServerSecrets.Prefix, StringComparison.Ordinal), "Sealed: " + Path.GetFileName(file));
                else
                    Assert(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Its account's alone: " + Path.GetFileName(file));
            }

            // « Le foyer » away from home: the OAuth client.
            var tailnetFile = Old("Mira.home-tailnet.json", """{ "clientId": "kAbC123CNTRL", "clientSecret": "tskey-client-kAbC123CNTRL-secret", "leaving": [] }""");
            Assert(new HomeTailnet(tailnetFile, () => now).Client == ("kAbC123CNTRL", "tskey-client-kAbC123CNTRL-secret"), "OAuth client read from the old file");
            Closed(tailnetFile, "tskey-client-kAbC123CNTRL-secret");
            Assert(new HomeTailnet(tailnetFile, () => now).Client == ("kAbC123CNTRL", "tskey-client-kAbC123CNTRL-secret"), "And from the sealed one");

            // The guests' links: the codes and the API key sealed, the passwords gone.
            var code = GuestRoutes.NewCode();
            var party = GuestRoutes.NewCode();
            var key = "0123456789abcdef0123456789abcdef";
            var invitesFile = Old("Mira.invites.json", $$"""
                { "address": "https://maison-pc.tail1234.ts.net", "key": "{{key}}",
                  "invites": [ { "id": "a1", "code": "{{code}}", "name": "Alex", "userId": "{{Item}}", "password": "mot-de-passe-de-l-invite", "hostId": "h", "hostName": "Sam", "created": "2026-11-19T20:00:00Z", "used": "2026-11-19T20:00:00Z" } ],
                  "parties": [ { "code": "{{party}}", "hostId": "h", "hostName": "Sam", "created": "2026-11-19T20:00:00Z" } ], "gone": [] }
                """);
            var invites = new GuestInvites(invitesFile, () => now);
            Assert(invites.Key == key && invites.Open(code, "device-a", "1.2.3.4") is (GuestInvites.Opening.Opened, { Name: "Alex" }) && invites.PartyOf("h", "Sam").Code == party, "Links read from the old file");
            Assert(!File.ReadAllText(invitesFile).Contains("mot-de-passe-de-l-invite", StringComparison.Ordinal) && !File.ReadAllText(invitesFile).Contains("\"password\"", StringComparison.Ordinal), "No guest's password kept");
            Closed(invitesFile, code, party, key);
            var again = new GuestInvites(invitesFile, () => now);
            Assert(again.Key == key && again.Open(code, "device-a", "1.2.3.4").Result == GuestInvites.Opening.Opened, "And from the sealed one");
            Assert(GuestInvites.Salvage(File.ReadAllText(invitesFile)) is { Key: var salvaged, Gone: var ids } && salvaged == key && ids.Contains(Item), "A damaged file still gives its key and accounts");

            // A code sealed by another account: the link goes, its account to delete.
            var lostFile = Old("Mira.invites-lost.json", $$"""
                { "invites": [ { "id": "a2", "code": "dpapi:AAAAAAAAAAAAAAAAAAAAAAAA", "name": "Sam", "userId": "{{Item}}", "hostId": "h", "hostName": "Alex", "created": "2026-11-19T20:00:00Z", "used": "2026-11-19T20:00:00Z" } ], "gone": [] }
                """);
            var lost = new GuestInvites(lostFile, () => now);
            Assert(lost.Count == 0 && lost.Sweep().Contains(Item), "Unreadable code: the link goes, its account to delete");

            // « Sous-titres automatiques »: the API keys and the password.
            var subtitlesFile = Old("Mira.subtitles.json", """{ "enabled": true, "openSubtitles": { "username": "alex", "password": "os-password", "apiKey": "os-key" }, "subDl": { "enabled": true, "apiKey": "subdl-key" } }""");
            Assert(new SubtitleConfig(subtitlesFile).Current is { OpenSubtitles: { Password: "os-password", ApiKey: "os-key", Username: "alex" }, SubDl.ApiKey: "subdl-key" }, "Subtitles read from the old file");
            Closed(subtitlesFile, "os-password", "os-key", "subdl-key");
            Assert(new SubtitleConfig(subtitlesFile).Current is { OpenSubtitles.Password: "os-password", SubDl.ApiKey: "subdl-key" }, "And from the sealed one");
            var damaged = Old("Mira.subtitles-lost.json", """{ "openSubtitles": { "password": "dpapi:AAAAAAAAAAAAAAAAAAAAAAAA" } }""");
            Assert(new SubtitleConfig(damaged).Current is { OpenSubtitles.Password: "", OpenSubtitles.ApiKey: "" }, "Unreadable password: asked again, never null");
            Assert(new SubtitleConfig(damaged).Public().ToJsonString() is { Length: > 0 }, "Shown without it");

            // The proof key: the same secret after it is sealed (the keys given still prove).
            var secret = new byte[32];
            secret[0] = 7;
            var proofFile = Old("Mira.proof.key", Convert.ToBase64String(secret));
            Assert(ProofSecrets.Read(proofFile) is { } read && read.SequenceEqual(secret), "Proof secret read from the old file");
            Closed(proofFile, Convert.ToBase64String(secret));
            Assert(ProofSecrets.Read(proofFile) is { } kept && kept.SequenceEqual(secret), "The same after it is sealed");

            // Hashes only, but its account's alone too.
            var homeFile = Path.Combine(root, "Mira.home.json");
            var home = new HomeFamily(homeFile, () => now, iterations: 1000);
            home.SetPassword("famille-ok");
            if (!OperatingSystem.IsWindows()) Assert(File.GetUnixFileMode(homeFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "The home's file: its account's alone");
            return Task.CompletedTask;
        });
    }
}
