using System.IO;
using System.Net;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>BuiltInTailnet, « Tailscale intégré » as Mira for Windows and Mac drive it, against the helper's stand-in (FakeTailnet).</summary>
internal static class BuiltInTailnetTests
{
    public static async Task RunAsync(TestRun run)
    {
        // What the app gives it, and what it did with it: the pages it opened, the erasing of its key and state.
        (BuiltInTailnet Tailnet, TailnetNetwork Network, List<string> Opened, Func<int> Located) Make(string name, string[] fake, Func<(string?, string?)>? locate = null, Action? forget = null)
        {
            var folder = Path.Combine(run.Root, name);
            Directory.CreateDirectory(folder);
            var (executable, arguments) = FakeTailnet.Command(fake);
            var opened = new List<string>(); var located = 0;
            var network = new TailnetNetwork(() => [IPAddress.Parse("192.168.1.20")], "mira-pc");
            var tailnet = new BuiltInTailnet(new()
            {
                Locate = () => { Interlocked.Increment(ref located); return locate?.Invoke() ?? (executable, null); },
                Arguments = arguments, Directory = Path.Combine(folder, "tailscale"), StateKey = () => new byte[32],
                Forget = forget ?? (() => { if (Directory.Exists(Path.Combine(folder, "tailscale"))) Directory.Delete(Path.Combine(folder, "tailscale"), recursive: true); }),
                Open = page => { lock (opened) opened.Add(page); }, AppVersion = "test", Network = network, StateWait = TimeSpan.FromSeconds(3),
                Failed = failure => failure.Code == TailnetCodes.Helper ? "Bloqué par l’antivirus." : null,
            });
            return (tailnet, network, opened, () => Volatile.Read(ref located));
        }
        var now = DateTimeOffset.UtcNow;

        await run.Test("Tailscale intégré : rien ne part avant « Se connecter », la page s’ouvre, puis connecté, utilisé, gardé", async () =>
        {
            var (tailnet, network, opened, located) = Make("builtin-signin", []);
            await using var owned = tailnet;
            var changes = 0; tailnet.Changed += () => Interlocked.Increment(ref changes);
            await tailnet.StartWithAppAsync();
            Assert(tailnet.Node is null && located() == 0 && tailnet.Line() == "Pas connecté.", "Something started before the person signed in");
            Assert(network.Choose("100.64.0.2").Path == TailnetPath.Direct, "With the node off, a device of the tailnet does not go as before");

            Assert(await tailnet.SignInAsync() == TailnetText.PageOpened && tailnet.SigningIn && tailnet.Page == FakeTailnet.Page, "The sign-in does not open the login page");
            lock (opened) Assert(opened.SequenceEqual([FakeTailnet.Page]), "The page was not opened once in the browser");
            Assert(await tailnet.SignInAsync() == TailnetText.PageOpened, "Asked again, the page is not opened again");
            lock (opened) Assert(opened.Count == 2, "Asked again, the page is not opened again");
            await WaitUntil(() => tailnet.State == TailnetStates.Running);
            var news = tailnet.Update(now);
            Assert(news is { SignedIn: true, Joined: true, Lost: null, Expiry: null } && tailnet.SignedIn && !tailnet.SigningIn && tailnet.Page is null, "The sign-in done is not told, or not kept: " + news);
            Assert(tailnet.Update(now) == TailnetNews.None, "The sign-in done is told twice");
            Assert(tailnet.Line() == "Connecté : ogsas@example.com." && Volatile.Read(ref changes) > 0, "Signed in, not shown as such");
            Assert(network.Choose("100.64.0.2").Path == TailnetPath.Node && network.Choose("desktop-jellyfin").Path == TailnetPath.Node, "Jellyfin's PC is not reached through the node");
            Assert(network.Choose("192.168.1.198").Path == TailnetPath.Direct && network.Choose("mira-pc").Path == TailnetPath.Direct, "The home network or this computer goes through the node");

            Assert(await tailnet.SignOutAsync() == TailnetText.SignedOut, "Signed out, not said as told to Tailscale");
            Assert(!tailnet.SignedIn && tailnet.Node is null && tailnet.Line() == "Pas connecté." && !Directory.Exists(Path.Combine(run.Root, "builtin-signin", "tailscale")), "Signed out, the node stays on, or its state on this device");
            Assert(network.Choose("100.64.0.2").Path == TailnetPath.Direct, "Signed out, the network still uses the node");
        });

        await run.Test("Tailscale intégré : connecté avant, il démarre avec Mira ; l’expiration proche dite une fois", async () =>
        {
            var expiry = (now + TimeSpan.FromDays(5)).ToString("O");
            var (tailnet, network, opened, _) = Make("builtin-again", ["--state", "running", "--key-expiry", expiry]);
            tailnet.SignedIn = true;
            Assert(tailnet.Line() == "Démarrage…", "Signed in, not shown as starting before the node is made");
            await tailnet.StartWithAppAsync();
            await WaitUntil(() => tailnet.State == TailnetStates.Running);
            var news = tailnet.Update(now);
            Assert(news is { Joined: true, SignedIn: false, Lost: null, Expiry: { } soon } && soon.Contains("expire le"), "Started with Mira: joined or its expiry not told: " + news);
            Assert(tailnet.Update(now) == TailnetNews.None, "The expiry is told twice");
            lock (opened) Assert(opened.Count == 0, "A page opened for a node signed in before");
            Assert(network.Choose("nas").Path == TailnetPath.Node, "A device known by its short name is not reached through the node");
            await tailnet.DisposeAsync();
            Assert(tailnet.Node is null && !await tailnet.StartAsync() && tailnet.Node is null, "Mira closing, the node starts again");
            Assert(tailnet.Update(now) == TailnetNews.None && network.Choose("100.64.0.2").Path == TailnetPath.Direct, "Mira closing, the node is still used");
        });

        await run.Test("Tailscale intégré : une connexion expirée pendant l’absence, dite une fois avec « Se connecter »", async () =>
        {
            var (tailnet, network, _, _) = Make("builtin-expired", ["--state", "expired", "--login-waits"]);
            await using var owned = tailnet;
            tailnet.SignedIn = true;
            await tailnet.StartWithAppAsync();
            await WaitUntil(() => tailnet.State == TailnetStates.Expired);
            Assert(tailnet.Update(now) is { Lost: TailnetCodes.Expired, Joined: false } && tailnet.Update(now).Lost is null, "An expired sign-in is not told, or told twice");
            Assert(BuiltInTailnet.Remedy(TailnetCodes.Expired) == TailnetRemedy.SignIn && tailnet.Line().Contains("expiré"), "An expired sign-in does not lead to « Se connecter »");
            Assert(network.Choose("100.64.0.2") == TailnetChoice.Fail(TailnetCodes.Expired), "A device of the tailnet is tried while the sign-in expired");
            Assert(await tailnet.SignInAsync() == TailnetText.PageOpened && tailnet.SigningIn, "Signing in again does not open the page");
        });

        await run.Test("Tailscale intégré : en attente d’approbation, la console ; introuvable ou bloqué, dit dans les mots du système", async () =>
        {
            var (waiting, _, opened, _) = Make("builtin-approval", ["--state", "machine-auth"]);
            await using (waiting)
            {
                Assert(await waiting.SignInAsync() is null && waiting.SigningIn, "Waiting for the console, something else is said");
                lock (opened) Assert(opened.Count == 0, "Waiting for the console, the login page opened");
                Assert(waiting.Line() == TailnetText.WaitingApproval + "." && BuiltInTailnet.Remedy(TailnetCodes.MachineAuth) == TailnetRemedy.Console, "Waiting for the console, not said so");
            }

            var (missing, _, _, located) = Make("builtin-missing", [], locate: () => (null, "Il manque."));
            Assert(await missing.SignInAsync() == "Il manque." && !missing.SigningIn && missing.Problem == "Il manque." && missing.Line() == "Il manque.", "A missing helper is not said");
            Assert(await missing.SignInAsync() == "Il manque." && located() == 2, "A missing helper is not looked for again");

            var (blocked, _, _, _) = Make("builtin-blocked", ["--silent"]);
            await using (blocked)
            {
                Assert(await blocked.SignInAsync() == "Bloqué par l’antivirus." && !blocked.SigningIn, "A helper that ends at once is not said in the system's words");
                await WaitUntil(() => blocked.Helper == TailnetHelper.Failed, seconds: 10);
                Assert(blocked.Line() == "Bloqué par l’antivirus.", "A helper that cannot run is not shown in the system's words: " + blocked.Line());
            }
        });

        await run.Test("Tailscale intégré : déconnecté sans réseau ou sans pouvoir effacer, il le dit", async () =>
        {
            var (offline, _, _, _) = Make("builtin-offline", ["--silent"]);
            offline.SignedIn = true;
            Assert(await offline.SignOutAsync() == TailnetText.SignedOutHere && !offline.SignedIn, "Signed out without Tailscale told, not said so");
            var (stuck, _, _, _) = Make("builtin-stuck", [], forget: () => throw new IOException("fichier utilisé"));
            stuck.SignedIn = true;
            Assert(await stuck.SignOutAsync() is { } said && said.Contains("fichier utilisé") && said.Contains("builtin-stuck") && !stuck.SignedIn, "Files left behind are not said, with their folder");
            foreach (var code in TailnetCodes.All)
                Assert(BuiltInTailnet.Remedy(code) == code switch
                {
                    TailnetCodes.NeedsLogin or TailnetCodes.Expired => TailnetRemedy.SignIn,
                    TailnetCodes.MachineAuth => TailnetRemedy.Console,
                    TailnetCodes.Stopped or TailnetCodes.Helper => TailnetRemedy.Retry,
                    _ => TailnetRemedy.None,
                }, $"« {code} » leads to the wrong button");
        });
    }
}
