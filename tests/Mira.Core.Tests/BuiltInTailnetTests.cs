using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mira.Core;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>
/// BuiltInTailnet, « Tailscale intégré » as Mira for Windows and Mac drive it, against the helper's stand-in
/// (FakeTailnet); with MIRA_TAILNET_HELPER and MIRA_TAILNET_TESTNET (the tailscale job of ci.yml), with the real helper on
/// a Tailscale network of its own too.
/// </summary>
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

        if (Environment.GetEnvironmentVariable("MIRA_TAILNET_HELPER") is { Length: > 0 } helper && Environment.GetEnvironmentVariable("MIRA_TAILNET_TESTNET") is { Length: > 0 } testnet)
            await run.Test("Tailscale intégré : de bout en bout, le vrai nœud connecté par sa page, relancé avec Mira, puis déconnecté de Tailscale",
                () => RealNodeAsync(run, helper, testnet));
    }

    /// <summary>
    /// The controller of the apps with the real helper (built with version « test ») on mira-testnet's network, whose
    /// Jellyfin PC exposes a small HTTP server on its port 8096: the sign-in by the login page, Mira's requests through
    /// the network it was given, the node started again with the app on its saved state, then signed out.
    /// </summary>
    private static async Task RealNodeAsync(TestRun run, string helper, string testnet)
    {
        var folder = Path.Combine(run.Root, "builtin-real");
        Directory.CreateDirectory(folder);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        using var serving = new CancellationTokenSource();
        var served = ServeAsync(server, serving.Token);
        var start = new ProcessStartInfo(testnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-addr", "127.0.0.1:0", "-forward", $"8096=127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}", "-dir", Path.Combine(folder, "bench") }) start.ArgumentList.Add(argument);
        using var bench = Process.Start(start) ?? throw new Exception("mira-testnet did not start");
        var benchLog = new StringBuilder();
        bench.ErrorDataReceived += (_, e) => { lock (benchLog) benchLog.AppendLine(e.Data); };
        bench.BeginErrorReadLine();
        var directory = Path.Combine(folder, "tailscale"); var log = Path.Combine(directory, "mira-tailscale.log");
        string Diagnostics() { lock (benchLog) return (File.Exists(log) ? File.ReadAllText(log) : "(no log)") + "\n--- mira-testnet\n" + benchLog; }
        try
        {
            var line = await bench.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90)) ?? "";
            Assert(line.StartsWith("MIRA-TESTNET ", StringComparison.Ordinal), "mira-testnet said: " + line + "\n" + benchLog);
            using var info = JsonDocument.Parse(line["MIRA-TESTNET ".Length..]);
            var control = info.RootElement.GetProperty("control").GetString()!;
            var ip = info.RootElement.GetProperty("jellyfin").GetProperty("ips")[0].GetString()!;
            var key = RandomNumberGenerator.GetBytes(32); var forgotten = 0;
            // The person's browser: opening the test network's login page logs the node in.
            using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            var opened = new List<Task<HttpResponseMessage>>();
            var network = new TailnetNetwork(() => [IPAddress.Parse("192.168.1.20")], "mira-pc");
            BuiltInTailnet Make() => new(new()
            {
                Locate = () => (helper, null), Directory = directory, StateKey = () => key, AppVersion = "test", ControlUrl = control, Network = network,
                Forget = () => { Interlocked.Increment(ref forgotten); Directory.Delete(directory, recursive: true); },
                Open = page => { lock (opened) opened.Add(browser.GetAsync(page)); },
            });
            try
            {
                var tailnet = Make();
                await using (tailnet)
                {
                    Assert(await tailnet.SignInAsync() == TailnetText.PageOpened && tailnet.SigningIn, "The sign-in does not open the login page: " + tailnet.Line());
                    await WaitUntil(() => tailnet.State == TailnetStates.Running && tailnet.Node?.Status?.Peer(ip) is { Online: true } && tailnet.Node.Proxy is not null, 90);
                    lock (opened) Assert(opened.Count == 1 && opened[0].Result.IsSuccessStatusCode, "The login page was not opened once");
                    var news = tailnet.Update(DateTimeOffset.UtcNow);
                    Assert(news is { SignedIn: true, Joined: true, Lost: null } && tailnet.SignedIn && !tailnet.SigningIn, "The sign-in done is not told: " + news);
                    Assert(tailnet.Line().StartsWith("Connecté", StringComparison.Ordinal), "Signed in, not shown as such: " + tailnet.Line());
                    Assert(ServerSearch.NodeDevices(tailnet.Node!.Status).Contains(ip), "The Jellyfin PC is not offered to the server search");
                    Assert(network.Choose(ip).Path == TailnetPath.Node && network.Choose("jellyfin-pc").Path == TailnetPath.Node, "The Jellyfin PC is not reached through the node");
                    using (var http = new HttpClient(network.Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        Assert(await http.GetStringAsync($"http://{ip}:8096/System/Ping") == "Jellyfin Server", "A request does not reach the Jellyfin PC by its address");
                        Assert(await http.GetStringAsync("http://jellyfin-pc:8096/System/Ping") == "Jellyfin Server", "A request does not reach the Jellyfin PC by its short name");
                    }
                }
                Assert(tailnet.Node is null && network.Choose(ip).Path == TailnetPath.Direct && File.Exists(Path.Combine(directory, "mira-tailscale.log")), "Closed, the node is still used, or its state left");

                // Mira started again: signed in before, the node joins on its saved state, without a page.
                var again = Make();
                await using (again)
                {
                    again.SignedIn = true;
                    await again.StartWithAppAsync();
                    await WaitUntil(() => again.State == TailnetStates.Running && again.Node?.Status?.Peer(ip) is { Online: true }, 60);
                    var news = again.Update(DateTimeOffset.UtcNow);
                    Assert(news is { Joined: true, SignedIn: false, Lost: null }, "Started again, not joined as before: " + news);
                    lock (opened) Assert(opened.Count == 1, "Started again, a login page opened");
                    Assert(await again.Node!.ProbeAsync("jellyfin-pc:8096", TimeSpan.FromSeconds(20)) is null, "Started again, the Jellyfin PC does not answer");
                    Assert(!File.ReadAllText(log).Contains("/auth/", StringComparison.Ordinal), "The node's log shows a login page");
                    Assert(await again.SignOutAsync() == TailnetText.SignedOut, "Signed out, not said as told to Tailscale");
                    Assert(!again.SignedIn && again.Node is null && forgotten == 1 && !Directory.Exists(directory) && network.Choose(ip).Path == TailnetPath.Direct,
                        "Signed out, the node stays used, or its state on this device");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new Exception(ex.Message + "\n" + Diagnostics(), ex); }
        }
        finally
        {
            try { bench.Kill(); } catch (InvalidOperationException) { }
            await bench.WaitForExitAsync();
            serving.Cancel(); server.Stop();
            try { await served; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>The Jellyfin PC's port 8096: « Jellyfin Server » to every request, as System/Ping answers.</summary>
    private static async Task ServeAsync(TcpListener server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var connection = await server.AcceptTcpClientAsync(ct);
            _ = Task.Run(async () =>
            {
                using (connection)
                {
                    try
                    {
                        var stream = connection.GetStream(); var head = new StringBuilder(); var buffer = new byte[1024];
                        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer, ct) is var read and > 0) head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (head.Length == 0) return;
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 15\r\nConnection: close\r\n\r\nJellyfin Server"), ct);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                }
            }, ct);
        }
    }
}
