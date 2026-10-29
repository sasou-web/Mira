using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mira.Core;
using Mira.Core.Tailscale;
using static TestSupport;

/// <summary>
/// Mira's own Tailscale node, seen from the apps: the reasons the Go node gives (src/Mira.Tailscale) are those the apps
/// explain, a failure of the node is a network failure like any other, each address takes the way the three apps agree
/// on (tests/fixtures/tailnet.json), and Mira's requests, WebSocket and player reach a device of the tailnet through the
/// node's proxy. With MIRA_TAILNET_HELPER and MIRA_TAILNET_TESTNET (the tailscale job of ci.yml), the same with the real
/// helper on a Tailscale network of its own, and mpv too with MIRA_TAILNET_MPV.
/// </summary>
internal static class TailnetTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Tailscale intégré : chaque raison d’échec du nœud Go est connue des apps", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var go = File.ReadAllText(Path.Combine(repository, "src", "Mira.Tailscale", "tailnet", "dial.go"));
            var codes = Regex.Matches(go, @"^\s*Code\w+\s*=\s*""([a-z-]+)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
            Assert(codes.Count >= 14, "dial.go: " + codes.Count + " codes read");
            var missing = codes.Except(TailnetCodes.All).ToList();
            Assert(missing.Count == 0, "Unknown to the apps: " + string.Join(", ", missing));
            // The apps' own: the helper process, its version, the state's key.
            var appsOnly = TailnetCodes.All.Except(codes).Order(StringComparer.Ordinal).ToList();
            Assert(appsOnly.SequenceEqual([TailnetCodes.Helper, TailnetCodes.Key, TailnetCodes.Version]), "Not in Go: " + string.Join(", ", appsOnly));
            Assert(TailnetCodes.All.Distinct().Count() == TailnetCodes.All.Count, "A code twice");
            return Task.CompletedTask;
        });
        await Test("Tailscale intégré : un échec du nœud est une erreur réseau attendue", () =>
        {
            var failure = new TailnetException(TailnetCodes.PeerOffline);
            Assert(failure is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError, StatusCode: null }, "Not a connection error");
            Assert(ExpectedErrors.Are(failure) && failure.Code == "peer-offline" && failure.Message == "peer-offline", "Not expected, or code lost");
            var inner = new IOException("broken pipe");
            var said = new TailnetException(TailnetCodes.Helper, "The helper ended", inner);
            Assert(said.Message == "The helper ended" && said.InnerException == inner && said.Code == TailnetCodes.Helper, "Message or cause lost");
            return Task.CompletedTask;
        });
        await Test("Tailscale intégré : chaque adresse prend son chemin, direct, par le nœud ou refusée, comme sur iPhone (tests/fixtures/tailnet.json)", () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "tailnet.json")));
            var node = fixture.RootElement.GetProperty("node").Deserialize<TailnetStatus>(Json.Options)!;
            Assert(node.Peers.Count == 3 && node.Self?.Ips.Count == 2 && node.MagicDnsSuffix == "tail1234.ts.net" && node.Peers[1].LastSeen is not null, "The fixture's node is misread");
            var cases = 0;
            foreach (var c in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                string? Text(string name) => c.TryGetProperty(name, out var value) ? value.GetString() : null;
                bool Flag(string name, bool fallback) => c.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
                var status = Flag("status", true) ? node with { State = Text("state") ?? TailnetStates.Running } : null;
                IReadOnlyCollection<IPAddress> local = c.TryGetProperty("local", out var given)
                    ? given.EnumerateArray().Select(x => IPAddress.Parse(x.GetString()!)).ToList() : [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback];
                var choice = TailnetRoute.Choose(Text("host")!, Flag("on", true), status, Flag("proxy", true), local, Text("unavailable"),
                    Flag("known", false) ? node : null, Text("computer") ?? "MacBook-Air");
                var expected = new TailnetChoice(Enum.Parse<TailnetPath>(Text("path")!, ignoreCase: true), Text("code"));
                Assert(choice == expected, $"{Text("name")} ({Text("host")}): {choice}, expected {expected}");
                cases++;
            }
            Assert(cases >= 40, cases + " cases read");
            Assert(TailnetRoute.Clean("[FD7A:115C:A1E0:0:0:0:0:1]") == "fd7a:115c:a1e0::1" && TailnetRoute.Clean(" Desktop.ts.net. ") == "Desktop.ts.net", "An address not written as Tailscale writes it");
            return Task.CompletedTask;
        });
        await Test("Tailscale intégré : un appareil du tailnet est joint par le proxy du nœud (requêtes, WebSocket), le reste en direct comme avant", async () =>
        {
            await using var jellyfin = FakeJellyfin.Start();
            var credentials = new TailnetProxy("", "mira", "s3cret");
            await using var proxy = FakeProxy.Start(jellyfin.Port, credentials.Authorization, host => host switch
            {
                "100.114.247.13" or "desktop-invefqh" or "desktop-invefqh.tail1234.ts.net" => (200, null),
                "nas" => (502, TailnetCodes.PeerOffline),
                "box.example.internal" => (502, "not-a-code"),
                _ => (502, null),
            });
            var node = new FakeNode { Status = Running(), Proxy = credentials with { Address = proxy.Address } };
            var network = new TailnetNetwork(() => [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback], "MacBook-Air");
            network.Follow(node);
            using var http = new HttpClient(network.Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

            // The Jellyfin PC by its Tailscale address: through the node, Jellyfin's own headers as without it.
            using (var request = new HttpRequestMessage(HttpMethod.Get, "http://100.114.247.13:8096/System/Info/Public"))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"t\"");
                using var response = await http.SendAsync(request);
                Assert(response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).Contains("Jellyfin de test"), "No answer through the node");
            }
            var seen = jellyfin.Requests.Last();
            Assert(proxy.Targets.SequenceEqual(["100.114.247.13:8096"]), "Asked of the proxy: " + string.Join(", ", proxy.Targets));
            Assert(seen.Headers["Host"] == "100.114.247.13:8096" && seen.Headers["Authorization"] == "MediaBrowser Token=\"t\"" && !seen.Headers.ContainsKey("Proxy-Authorization"),
                "Jellyfin got other headers: " + string.Join(", ", seen.Headers));
            // Its short name and its MagicDNS name: the node resolves them; the connection is kept for the next request.
            Assert((await http.GetStringAsync("http://desktop-invefqh:8096/System/Info/Public")).Contains("Jellyfin de test") && proxy.Targets.Last() == "desktop-invefqh:8096", "Short name");
            await http.GetStringAsync("http://desktop-invefqh:8096/System/Ping");
            Assert(proxy.Targets.Count == 2, "A kept connection asked the proxy again");
            // This computer: straight, the proxy never asked.
            Assert((await http.GetStringAsync($"http://127.0.0.1:{jellyfin.Port}/System/Info/Public")).Contains("Jellyfin de test") && proxy.Targets.Count == 2, "This computer went through the node");
            // The WebSocket, as JellyfinClient opens it: both ways.
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(new Uri("ws://desktop-invefqh.tail1234.ts.net:8096/socket?deviceId=d"), network.Invoker, CancellationToken.None);
                var buffer = new byte[256];
                var first = await socket.ReceiveAsync(buffer, CancellationToken.None);
                Assert(Encoding.UTF8.GetString(buffer, 0, first.Count).Contains("LibraryChanged"), "Nothing from Jellyfin's WebSocket");
                await socket.SendAsync("{\"MessageType\":\"KeepAlive\"}"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
                var echo = await socket.ReceiveAsync(buffer, CancellationToken.None);
                Assert(Encoding.UTF8.GetString(buffer, 0, echo.Count).Contains("KeepAlive") && proxy.Targets.Last() == "desktop-invefqh.tail1234.ts.net:8096", "WebSocket not through the node");
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }

            // The node's reasons, out of HttpClient's exceptions. Each case on a port of its own: a kept connection would
            // skip the node.
            async Task<string?> Failure(string url)
            {
                try { await http.GetStringAsync(url); return null; }
                catch (HttpRequestException ex) { return TailnetNetwork.Find(ex)?.Code ?? "(" + ex.Message + ")"; }
            }
            Assert(await Failure("http://nas:8096/System/Info/Public") == TailnetCodes.PeerOffline, "The PC asleep");
            Assert(await Failure("http://100.80.0.5:8096/") == TailnetCodes.Unreachable && await Failure("http://box.example.internal:8096/") == TailnetCodes.Unreachable, "A 502 without a known reason");
            // Other credentials (the helper started again since they were read): its proxy refuses them.
            node.Proxy = credentials with { Address = proxy.Address, Password = "old" };
            Assert(await Failure("http://100.114.247.13:8097/") == TailnetCodes.Helper, "Credentials refused");
            node.Proxy = credentials with { Address = proxy.Address };
            // Not signed in: nothing leaves, whatever the device.
            var asked = proxy.Targets.Count;
            node.Status = Running() with { State = TailnetStates.NeedsLogin };
            node.Change();
            Assert(await Failure("http://100.114.247.13:8098/") == TailnetCodes.NeedsLogin && proxy.Targets.Count == asked, "Sent although not signed in");
            // Its helper gone: closed at once, the home network still straight.
            node.Status = null; node.Proxy = null; node.Unavailable = TailnetCodes.Version;
            node.Change();
            var clock = Stopwatch.StartNew();
            Assert(await Failure("http://100.114.247.13:8099/") == TailnetCodes.Version && clock.Elapsed < TimeSpan.FromSeconds(5), "Another version of the helper");
            Assert(network.Choose("192.168.1.198") == TailnetChoice.Direct, "The home network through the node");

            // A node that starts: the request waits for it (TailnetNetwork.StartWait), then goes through it.
            node.Unavailable = TailnetCodes.Starting;
            node.Change();
            var waiting = http.GetStringAsync("http://100.114.247.13:8100/System/Info/Public");
            await Task.Delay(400);
            Assert(!waiting.IsCompleted, "Did not wait for the node: " + (waiting.IsFaulted ? waiting.Exception!.InnerException!.Message : "answered"));
            node.Status = Running(); node.Proxy = credentials with { Address = proxy.Address }; node.Unavailable = null;
            node.Change();
            Assert((await waiting.WaitAsync(TimeSpan.FromSeconds(10))).Contains("Jellyfin de test"), "Not through the node once started");

            // Off: back to the system's network, nothing asked of the node.
            network.Use(null);
            Assert(network.Choose("100.114.247.13") == TailnetChoice.Direct && network.ForPlayer(new Uri("http://100.114.247.13:8096/Videos/1/stream")) is null, "The node still used once off");
        });
        await Test("Tailscale intégré : mpv reçoit le proxy du nœud et ses identifiants pour un appareil du tailnet, rien pour le reste", () =>
        {
            var node = new FakeNode { Status = Running(), Proxy = new TailnetProxy("127.0.0.1:41641", "mira", "p@ss:word/1") };
            var network = new TailnetNetwork(() => [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback], "MacBook-Air");
            Assert(network.ForPlayer(new Uri("http://100.114.247.13:8096/Videos/1/stream")) is null, "No node, yet a proxy");
            network.Follow(node);
            var player = network.ForPlayer(new Uri("http://desktop-invefqh:8096/Videos/1/stream?static=true"));
            Assert(player is { } given && given.Proxy == "http://mira:p%40ss%3Aword%2F1@127.0.0.1:41641" && given.Authorization == node.Proxy!.Authorization, "Proxy for mpv: " + player);
            Assert(network.ForPlayer(new Uri("https://desktop-invefqh.tail1234.ts.net/Videos/1/stream")) is not null, "https:// goes through CONNECT");
            Assert(network.ForPlayer(new Uri("http://192.168.1.198:8096/Videos/1/stream")) is null && network.ForPlayer(new Uri("file:///tmp/a.mkv")) is null, "Straight addresses got the proxy");
            node.Status = Running() with { State = TailnetStates.Expired };
            try { network.ForPlayer(new Uri("http://100.114.247.13:8096/Videos/1/stream")); throw new Exception("No failure"); }
            catch (TailnetException ex) { Assert(ex.Code == TailnetCodes.Expired, ex.Code); }
            return Task.CompletedTask;
        });
        await Test("Tailscale intégré : la raison du nœud, sous les exceptions de HttpClient et des WebSocket, ou devinée d’un délai dépassé", () =>
        {
            var reason = new TailnetException(TailnetCodes.Closed);
            Assert(TailnetNetwork.Find(new HttpRequestException("x", reason)) == reason, "Wrapped once");
            Assert(TailnetNetwork.Find(new WebSocketException("x", new HttpRequestException("y", new AggregateException(reason)))) == reason, "Wrapped by a WebSocket");
            Assert(TailnetNetwork.Find(new HttpRequestException("x", new IOException("y"))) is null && TailnetNetwork.Find(null) is null, "Found where there is none");
            var node = new FakeNode { Status = Running(), Proxy = new TailnetProxy("127.0.0.1:41641", "mira", "p") };
            var network = new TailnetNetwork(() => [IPAddress.Parse("192.168.1.20"), IPAddress.Loopback], "MacBook-Air");
            network.Follow(node);
            var timeout = new TaskCanceledException("timeout", new TimeoutException());
            Assert(network.Explain(timeout, "nas") == TailnetCodes.PeerOffline && network.Explain(timeout, "desktop-invefqh") is null, "Out of time towards a device asleep, or awake");
            Assert(network.Explain(new HttpRequestException("x", reason), "desktop-invefqh") == TailnetCodes.Closed && network.Explain(new IOException("x"), "nas") is null, "The node's own reason");
            Assert(network.Explain(timeout, "192.168.1.198") is null, "The home network");
            node.Status = null; node.Proxy = null; node.Unavailable = TailnetCodes.Starting;
            Assert(network.Explain(timeout, "100.114.247.13") == TailnetCodes.Starting, "Out of time while the node starts");
            return Task.CompletedTask;
        });
        if (Environment.GetEnvironmentVariable("MIRA_TAILNET_HELPER") is { Length: > 0 } helper && Environment.GetEnvironmentVariable("MIRA_TAILNET_TESTNET") is { Length: > 0 } testnet)
            await Test("Tailscale intégré : de bout en bout, le vrai nœud rejoint un réseau Tailscale de test et joint son Jellyfin (connexion, requêtes, WebSocket, mpv, PC éteint, clé expirée)",
                () => EndToEndAsync(run, helper, testnet, Environment.GetEnvironmentVariable("MIRA_TAILNET_MPV")));
    }

    /// <summary>The node as the fixture describes it, running.</summary>
    private static TailnetStatus Running() => new()
    {
        State = TailnetStates.Running, MagicDnsSuffix = "tail1234.ts.net", User = "ogsas@example.com",
        Self = new() { Id = "n0", HostName = "Mira-MacBook-Air", DnsName = "mira-macbook-air.tail1234.ts.net", Ips = ["100.64.0.9", "fd7a:115c:a1e0::9"], Online = true },
        Peers =
        [
            new() { Id = "n1", HostName = "DESKTOP-INVEFQH", DnsName = "desktop-invefqh.tail1234.ts.net", Ips = ["100.114.247.13", "fd7a:115c:a1e0::1"], Os = "windows", Online = true },
            new() { Id = "n2", HostName = "nas", DnsName = "nas.tail1234.ts.net", Ips = ["100.100.1.2"], Os = "linux", LastSeen = DateTimeOffset.Parse("2026-10-29T10:00:00Z") },
            new() { Id = "n3", HostName = "box", DnsName = "box.example.internal", Ips = ["100.80.0.5"], Os = "linux", Online = true },
        ],
    };

    /// <summary>
    /// The real thing on Linux: mira-testnet's Tailscale network (its coordination server, relay, and a Jellyfin PC that
    /// exposes the fake Jellyfin below on its port 8096), the helper started by TailnetProcess and signed in through its
    /// login page, then Mira's own clients through TailnetNetwork.Default.
    /// </summary>
    private static async Task EndToEndAsync(TestRun run, string helper, string testnet, string? mpvLibrary)
    {
        var folder = Path.Combine(run.Root, "tailnet");
        Directory.CreateDirectory(folder);
        var (wav, _) = AudioOutput.TestSound("stereo");
        await using var jellyfin = FakeJellyfin.Start(wav);
        var start = new ProcessStartInfo(testnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-addr", "127.0.0.1:0", "-forward", $"8096=127.0.0.1:{jellyfin.Port}", "-dir", Path.Combine(folder, "bench") }) start.ArgumentList.Add(argument);
        using var bench = Process.Start(start) ?? throw new Exception("mira-testnet did not start");
        var benchLog = new StringBuilder();
        bench.ErrorDataReceived += (_, e) => { lock (benchLog) benchLog.AppendLine(e.Data); };
        bench.BeginErrorReadLine();
        try
        {
            var line = await bench.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90)) ?? "";
            Assert(line.StartsWith("MIRA-TESTNET ", StringComparison.Ordinal), "mira-testnet said: " + line + "\n" + benchLog);
            using var info = JsonDocument.Parse(line["MIRA-TESTNET ".Length..]);
            var control = info.RootElement.GetProperty("control").GetString()!;
            var dnsName = info.RootElement.GetProperty("jellyfin").GetProperty("dnsName").GetString()!.TrimEnd('.');
            var key = RandomNumberGenerator.GetBytes(32);
            var log = Path.Combine(folder, "tailscale.log");
            await using var process = new TailnetProcess(new()
            {
                Executable = helper, Directory = Path.Combine(folder, "node"), Hostname = "Mira-Test Mac", AppVersion = "test",
                LogFile = log, StateKey = () => key, ControlUrl = control,
            });
            string Diagnostics() => (File.Exists(log) ? File.ReadAllText(log) : "(no log)") + "\n--- mira-testnet\n" + benchLog;
            using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
            try
            {
                await process.StartAsync();
                await WaitUntil(() => process.Status?.State == TailnetStates.NeedsLogin, 60);
                // The login page, as the person's browser opens it.
                (await browser.GetAsync(await process.LoginAsync())).EnsureSuccessStatusCode();
                await WaitUntil(() => process.Status is { State: TailnetStates.Running } s && s.Peers.Any(x => x.Online && x.HostName == "jellyfin-pc") && process.Proxy is not null, 60);
            }
            catch (Exception ex) { throw new Exception("The node did not join the test network: " + ex.Message + "\n" + Diagnostics(), ex); }
            TailnetNetwork.Default.Use(process);
            try
            {
                // The login screen: the Jellyfin PC's short name, through the node.
                var found = await ServerAddress.DiscoverAsync("jellyfin-pc");
                Assert(found.Name == "Jellyfin de test" && found.Address == "http://jellyfin-pc:8096/", "Found " + found);
                Assert(!await ServerAddress.ExposesPasswordAsync(found.Address), "Its short name counted as the Internet");
                // Mira's client: requests, then the WebSocket.
                using var client = new JellyfinClient(new Connection($"http://{dnsName}:8096/", "user", "test", "token", "device"));
                Assert(await client.PingAsync(), "No answer to System/Ping through the node");
                using (var listening = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var changed = new TaskCompletionSource();
                    var listen = client.ListenAsync(() => changed.TrySetResult(), listening.Token);
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    listening.Cancel();
                    try { await listen; } catch (OperationCanceledException) { }
                }
                Assert(jellyfin.Requests.Any(x => x.Path.StartsWith("/socket", StringComparison.Ordinal) && x.Headers.GetValueOrDefault("Authorization", "").Contains("Token=\"token\"")), "Jellyfin's WebSocket not opened with the session");
                Assert(jellyfin.Requests.All(x => !x.Headers.ContainsKey("Proxy-Authorization")), "The proxy's credentials reached Jellyfin");

                // The player: mpv opens the file through the node, its headers as Jellyfin needs them.
                if (mpvLibrary is { Length: > 0 })
                {
                    using var mpv = new HeadlessMpv(mpvLibrary);
                    var loaded = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    mpv.FileLoaded += () => loaded.TrySetResult(null);
                    mpv.Error += x => loaded.TrySetResult(x);
                    mpv.Ended += _ => loaded.TrySetResult("ended before loading");
                    mpv.Load($"http://{dnsName}:8096/Audio/1/stream.wav?static=true", 0, "MediaBrowser Client=\"Mira\", Token=\"token\"", paused: true);
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (!loaded.Task.IsCompleted && DateTime.UtcNow < until) { mpv.Poll(); await Task.Delay(50); }
                    Assert(loaded.Task.IsCompleted && loaded.Task.Result is null, "mpv did not open the file through the node: " + (loaded.Task.IsCompleted ? loaded.Task.Result : "no answer in 30 s"));
                    var media = jellyfin.Requests.Where(x => x.Path.StartsWith("/Audio/1/stream.wav", StringComparison.Ordinal)).ToList();
                    Assert(media.Count > 0 && media.All(x => x.Headers.GetValueOrDefault("Authorization") == "MediaBrowser Client=\"Mira\", Token=\"token\"" && !x.Headers.ContainsKey("Proxy-Authorization")),
                        "mpv's requests: " + string.Join(" | ", media.Select(x => string.Join(", ", x.Headers))));
                }

                // The Jellyfin PC turned off: the node says so, in words, wherever Mira asks.
                (await browser.PostAsync(control + "/mira/jellyfin?online=0", null)).EnsureSuccessStatusCode();
                await WaitUntil(() => process.Status?.Peers.FirstOrDefault(x => x.HostName == "jellyfin-pc") is { Online: false }, 90);
                var fresh = new TailnetNetwork();
                fresh.Use(process);
                using (var http = new HttpClient(fresh.Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) })
                {
                    try { await http.GetStringAsync("http://jellyfin-pc:8096/System/Ping"); throw new Exception("Answered with the PC off"); }
                    catch (HttpRequestException ex) { Assert(TailnetNetwork.Find(ex)?.Code == TailnetCodes.PeerOffline, "Not said asleep: " + ex); }
                }
                try { await ServerAddress.DiscoverAsync("jellyfin-pc"); throw new Exception("Found with the PC off"); }
                catch (ServerDiscoveryException ex) { Assert(ex.Message == TailnetText.Asleep(TailnetNetwork.Default.Peer("jellyfin-pc")), "The login screen said: " + ex.Message); }
                // Back on: Mira finds it again, once Tailscale has a path to it.
                (await browser.PostAsync(control + "/mira/jellyfin?online=1", null)).EnsureSuccessStatusCode();
                await WaitUntil(() => process.Status?.Peers.FirstOrDefault(x => x.HostName == "jellyfin-pc") is { Online: true }, 90);
                for (var attempt = 1; ; attempt++)
                {
                    try { Assert((await ServerAddress.DiscoverAsync("jellyfin-pc")).Name == "Jellyfin de test", "Another server"); break; }
                    catch (ServerDiscoveryException) when (attempt < 10) { await Task.Delay(1000); }
                }

                // This node's key expired: nothing leaves, and Mira says to sign in again.
                (await browser.PostAsync(control + "/mira/expire?name=" + Uri.EscapeDataString(process.Status!.Self!.HostName), null)).EnsureSuccessStatusCode();
                await WaitUntil(() => process.Status?.State == TailnetStates.Expired, 90);
                try { await ServerAddress.DiscoverAsync("jellyfin-pc"); throw new Exception("Found with the key expired"); }
                catch (ServerDiscoveryException ex) { Assert(ex.Message == TailnetText.Problem(TailnetCodes.Expired), "The login screen said: " + ex.Message); }
                try { TailnetNetwork.Default.ForPlayer(new Uri($"http://{dnsName}:8096/Videos/1/stream")); throw new Exception("mpv got the proxy with the key expired"); }
                catch (TailnetException ex) { Assert(ex.Code == TailnetCodes.Expired, ex.Code); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new Exception(ex.Message + "\n" + Diagnostics(), ex); }
            finally { TailnetNetwork.Default.Use(null); }
            Assert(!File.ReadAllText(log).Contains("/auth/", StringComparison.Ordinal), "The log shows a login page");
        }
        finally
        {
            try { bench.Kill(); } catch (InvalidOperationException) { }
            await bench.WaitForExitAsync();
        }
    }

    /// <summary>What <see cref="TailnetNetwork"/> follows of a node, set by the test.</summary>
    private sealed class FakeNode : ITailnetNode
    {
        public TailnetStatus? Status { get; set; }
        public TailnetProxy? Proxy { get; set; }
        public string? Unavailable { get; set; }
        public event Action? Changed;
        public void Change() => Changed?.Invoke();
    }

    /// <summary>mpv without a window or sound, from the system's libmpv (Linux CI).</summary>
    private sealed class HeadlessMpv : MpvClient
    {
        public HeadlessMpv(string library) : base(library, NativeLibrary.Load(library), unload: true)
        {
            Option("vo", "null"); Option("ao", "null"); Option("cache", "no");
            Initialize("mpv did not start");
        }
        protected override string PlaybackFailure => "mpv could not play the file";
    }

    /// <summary>One request read off a connection: its line and headers (several of a name joined by commas).</summary>
    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers);

    /// <summary>Reads a request or answer head byte by byte (what follows it stays in the stream); null at the end of the stream.</summary>
    private static async Task<Request?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var head = new List<byte>(512);
        var one = new byte[1];
        while (head.Count < 65536)
        {
            if (await stream.ReadAsync(one, ct) == 0) return null;
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n') break;
        }
        var lines = Encoding.Latin1.GetString(head.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var headers = lines.Skip(1).Select(x => x.Split(':', 2)).Where(x => x.Length == 2).GroupBy(x => x[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => string.Join(", ", x.Select(y => y[1].Trim())), StringComparer.OrdinalIgnoreCase);
        return new(first[0], first.Length > 1 ? first[1] : "", headers);
    }

    /// <summary>
    /// A Jellyfin as the node reaches it: its public information, System/Ping, a media file (with ranges, as FFmpeg asks)
    /// and its WebSocket (« LibraryChanged » on opening, then each message sent back); every request kept with its headers.
    /// </summary>
    private sealed class FakeJellyfin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private byte[] _media = [];
        public ConcurrentQueue<Request> Requests { get; } = new();
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static FakeJellyfin Start(byte[]? media = null)
        {
            var fake = new FakeJellyfin { _media = media ?? [] };
            fake._listener.Start();
            _ = fake.AcceptAsync();
            return fake;
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var connection = client;
            var stream = client.GetStream();
            try
            {
                while (await ReadHeadAsync(stream, _stop.Token) is { } request)
                {
                    Requests.Enqueue(request);
                    if (request.Headers.GetValueOrDefault("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        await WebSocketAsync(stream, request);
                        return;
                    }
                    var path = request.Path.Split('?')[0];
                    var (status, type, body, extra) = path switch
                    {
                        "/System/Info/Public" => ("200 OK", "application/json", Encoding.UTF8.GetBytes("{\"ServerName\":\"Jellyfin de test\",\"Version\":\"12.1.0\",\"ProductName\":\"Jellyfin Server\",\"Id\":\"0123456789abcdef0123456789abcdef\",\"StartupWizardCompleted\":true}"), ""),
                        "/System/Ping" => ("200 OK", "text/plain", "\"Jellyfin Server\""u8.ToArray(), ""),
                        "/Audio/1/stream.wav" => Media(request.Headers.GetValueOrDefault("Range")),
                        _ => ("404 Not Found", "text/plain", "Not found"u8.ToArray(), ""),
                    };
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n{extra}\r\n"), _stop.Token);
                    if (request.Method != "HEAD") await stream.WriteAsync(body, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or WebSocketException or ObjectDisposedException) { }
        }

        /// <summary>The media file, or the part a « Range: bytes=a-b » asks.</summary>
        private (string, string, byte[], string) Media(string? range)
        {
            var match = Regex.Match(range ?? "", @"^bytes=(\d+)-(\d*)$");
            if (!match.Success) return ("200 OK", "audio/wav", _media, "Accept-Ranges: bytes\r\n");
            var from = long.Parse(match.Groups[1].Value);
            var to = match.Groups[2].Length > 0 ? Math.Min(long.Parse(match.Groups[2].Value), _media.Length - 1) : _media.Length - 1;
            if (from >= _media.Length) return ("416 Range Not Satisfiable", "text/plain", [], $"Content-Range: bytes */{_media.Length}\r\n");
            return ("206 Partial Content", "audio/wav", _media[(int)from..(int)(to + 1)], $"Accept-Ranges: bytes\r\nContent-Range: bytes {from}-{to}/{_media.Length}\r\n");
        }

        private async Task WebSocketAsync(Stream stream, Request request)
        {
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(request.Headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
            using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            await socket.SendAsync("{\"MessageType\":\"LibraryChanged\",\"Data\":{}}"u8.ToArray(), WebSocketMessageType.Text, true, _stop.Token);
            var buffer = new byte[4096];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, _stop.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _stop.Token);
                    return;
                }
                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, _stop.Token);
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The node's proxy as the apps use it: CONNECT with the credentials sent ahead, then 200 and the device's stream (the
    /// fake Jellyfin, whatever the port asked), or 502 with the node's reason; 407 for other credentials.
    /// </summary>
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private int _forward;
        private string _authorization = "";
        private Func<string, (int Status, string? Reason)> _answer = _ => (502, null);
        /// <summary>The « host:port » of each tunnel asked with the right credentials.</summary>
        public ConcurrentQueue<string> Targets { get; } = new();
        public string Address => _listener.LocalEndpoint.ToString()!;

        public static FakeProxy Start(int forward, string authorization, Func<string, (int Status, string? Reason)> answer)
        {
            var fake = new FakeProxy { _forward = forward, _authorization = authorization, _answer = answer };
            fake._listener.Start();
            _ = fake.AcceptAsync();
            return fake;
        }

        private async Task AcceptAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var connection = client;
            var stream = client.GetStream();
            try
            {
                if (await ReadHeadAsync(stream, _stop.Token) is not { Method: "CONNECT" } request) return;
                if (request.Headers.GetValueOrDefault("Proxy-Authorization") != _authorization)
                {
                    await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Mira\"\r\nContent-Length: 0\r\n\r\n"u8.ToArray(), _stop.Token);
                    return;
                }
                Targets.Enqueue(request.Path);
                var host = request.Path[..request.Path.LastIndexOf(':')].Trim('[', ']');
                var (status, reason) = _answer(host);
                if (status != 200)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Bad Gateway\r\n" + (reason is null ? "" : $"{TailnetNetwork.ReasonHeader}: {reason}\r\n") + "Content-Length: 0\r\n\r\n"), _stop.Token);
                    return;
                }
                using var device = new TcpClient();
                await device.ConnectAsync(IPAddress.Loopback, _forward, _stop.Token);
                await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _stop.Token);
                var upstream = device.GetStream();
                await Task.WhenAny(stream.CopyToAsync(upstream, _stop.Token), upstream.CopyToAsync(stream, _stop.Token));
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
