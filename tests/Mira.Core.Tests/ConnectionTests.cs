using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Core.Updates;
using Mira.Desktop.Services;
using static TestSupport;

/// <summary>Connecting to a server: typed address, discovery, certificate, password over http, session and Mac profile. Free of WPF: also run outside Windows.</summary>
internal static class ConnectionTests
{
    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        var testRoot = run.Root;
        await Test("URL de serveur : sous-chemin conservé, URL invalide refusée", () =>
        {
            Assert(JellyfinClient.NormalizeServer(" https://example.test/jellyfin ") == "https://example.test/jellyfin/", "Base path lost");
            foreach (var bad in new[] { "file:///tmp/video", "http://user:secret@localhost", "http://localhost?api_key=x", "localhost:8096" })
            { try { JellyfinClient.NormalizeServer(bad); throw new Exception("Accepted invalid URI"); } catch (ArgumentException) { } }
            return Task.CompletedTask;
        });
        await Test("Adresse saisie : sans http://, avec port, ou copiée depuis la page web de Jellyfin", () =>
        {
            void Expect(string input, params string[] expected)
            { var actual = ServerAddress.Candidates(input); Assert(actual.SequenceEqual(expected), $"{input} → {string.Join(" ; ", actual)}"); }
            Expect("192.168.1.20", "https://192.168.1.20/", "http://192.168.1.20:8096/", "http://192.168.1.20/");
            Expect(" nas:8096 ", "http://nas:8096/", "https://nas:8096/");
            Expect("jellyfin.example.test:8920", "https://jellyfin.example.test:8920/", "http://jellyfin.example.test:8920/");
            Expect("example.test/jellyfin", "https://example.test/jellyfin/", "http://example.test:8096/jellyfin/", "http://example.test/jellyfin/");
            Expect("http://192.168.1.20:8096/web/#/home.html", "http://192.168.1.20:8096/");
            Expect("https://example.test/jellyfin/web/index.html#!/details?id=1", "https://example.test/jellyfin/");
            Expect("localhost:8096/web", "http://localhost:8096/", "https://localhost:8096/");
            Expect("http://web:8096/", "http://web:8096/");
            Expect("[::1]:8096", "http://[::1]:8096/", "https://[::1]:8096/");
            foreach (var bad in new[] { "", "   ", "http://user:secret@localhost", "ftp://example.test" })
            { try { ServerAddress.Candidates(bad); throw new Exception("Accepted invalid address: " + bad); } catch (ArgumentException) { } }
            Assert(ServerAddress.ParseVersion("10.11.0-rc2") == new Version(10, 11, 0) && ServerAddress.ParseVersion("inconnue") is null, "Server version parsing");
            return Task.CompletedTask;
        });
        await Test("Erreurs attendues : une saisie refusée est dite, une ArgumentException de Mira ou de .NET est un défaut signalé", async () =>
        {
            // What the person typed: refused with a message written for them, and counted as expected on Windows and Mac.
            foreach (var input in Inputs())
            {
                try { input(); throw new Exception("Accepted"); }
                catch (UserInputException ex) { Assert(ExpectedErrors.Are(ex) && ex.Message.Length > 0 && !ex.Message.Contains("Parameter"), "Input refusal: " + ex.Message); }
            }
            try { await JellyfinSetup.ConfigureAsync(new Uri("http://127.0.0.1:1/"), "", "secret", MediaFolders.Under(Path.Combine(testRoot, "setup-input"))); throw new Exception("Accepted"); }
            catch (UserInputException) { }
            // Anything else from the Argument family is a bug: it reaches the app's handler (errors.log) instead of a quiet catch.
            foreach (var bug in new Exception[] { new ArgumentException("x"), new ArgumentNullException("x"), new ArgumentOutOfRangeException("x"), new InvalidOperationException(), new NullReferenceException() })
                Assert(!ExpectedErrors.Are(bug), bug.GetType().Name + " counted as expected");
            foreach (var expected in new Exception[] { new HttpRequestException(), new IOException(), new ServerDiscoveryException("x"), new UnauthorizedAccessException(), new TaskCanceledException(), new JsonException(), new Microsoft.Data.Sqlite.SqliteException("x", 11) })
                Assert(ExpectedErrors.Are(expected), expected.GetType().Name + " not counted as expected");
            static Action[] Inputs() => [() => ServerAddress.Candidates("  "), () => JellyfinClient.NormalizeServer("http://user:secret@localhost")];
        });
        await Test("Installation de Jellyfin : la suite part dès que Jellyfin répond, même si son installateur attend une réponse", async () =>
        {
            var server = new Uri("http://127.0.0.1:8096/");
            var ready = false; var probes = 0;
            var handler = new Handler(request =>
            {
                probes++;
                if (!ready) throw new HttpRequestException("Connection refused");
                return Task.FromResult(request.RequestUri!.AbsolutePath == "/System/Ping" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"Jellyfin Server\"") }
                    : JsonResponse(new { Id = "a", Version = "12.1.0", StartupWizardCompleted = false }));
            });
            // « Could not start the Jellyfin Server service » left unanswered: the installer never ends, Jellyfin starts.
            var asking = new TaskCompletionSource<int>(); var waited = new List<int>();
            var wait = JellyfinSetup.AwaitInstallerAsync(asking.Task, server, new SyncProgress<int>(n => { waited.Add(n); if (n == 3) ready = true; }), handler, TimeSpan.FromMilliseconds(5));
            var answered = await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(answered is { ExitCode: null, State: { Version: "12.1.0", WizardCompleted: false } } && waited.SequenceEqual([1, 2, 3]), $"Jellyfin answering while the installer asks: {answered}, {string.Join(",", waited)}");
            // The installer ends first, Jellyfin not yet started: its exit code, for the wait that follows.
            ready = false; probes = 0;
            var ended = new TaskCompletionSource<int>();
            var exit = JellyfinSetup.AwaitInstallerAsync(ended.Task, server, new SyncProgress<int>(n => { if (n == 1) ended.SetResult(2); }), handler, TimeSpan.FromSeconds(30));
            Assert(await exit.WaitAsync(TimeSpan.FromSeconds(5)) == new InstallerWait(2, null) && probes == 1, "Installer ending before Jellyfin answers");
            // Already ended: nothing asked of Jellyfin.
            probes = 0;
            Assert(await JellyfinSetup.AwaitInstallerAsync(Task.FromResult(0), server, null, handler) == new InstallerWait(0, null) && probes == 0, "Ended installer");
        });
        await Test("Recherche du serveur : première adresse Jellyfin dans l’ordre, autres serveurs et versions anciennes refusés", async () =>
        {
            HttpResponseMessage Info(string version = "10.10.3", string product = "Jellyfin Server") => JsonResponse(new { ServerName = "Salon", Version = version, ProductName = product, Id = "server-1" });
            Handler Serve(Func<Uri, HttpResponseMessage> answer) => new(request => Task.FromResult(answer(request.RequestUri!)));
            var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
            // The HTTPS port is closed and port 80 is a router page: the server is on 8096.
            var found = await ServerAddress.DiscoverAsync("nas", Serve(uri =>
            {
                asked.Add(uri.AbsoluteUri);
                return uri.Scheme == "https" ? throw new HttpRequestException("refusé") : uri.Port == 8096 ? Info() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>routeur</html>") };
            }));
            Assert(found == new ServerInfo("http://nas:8096/", "Salon", "10.10.3", "server-1"), "Wrong server: " + found);
            Assert(asked.All(x => x.EndsWith("/System/Info/Public")), "Something other than the public information was requested");
            // Both answer: HTTPS wins.
            Assert((await ServerAddress.DiscoverAsync("nas", Serve(_ => Info()))).Address == "https://nas/", "HTTPS not preferred");
            // HTTPS never answers (a firewall dropping it): port 8096 is used after a short grace, not the whole timeout.
            var clock = Stopwatch.StartNew();
            var silent = await ServerAddress.DiscoverAsync("nas", new Handler(async request =>
            {
                if (request.RequestUri!.Scheme == "https") await Task.Delay(Timeout.Infinite);
                return request.RequestUri.Port == 8096 ? Info() : new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            Assert(silent.Address == "http://nas:8096/" && clock.Elapsed < ServerAddress.ProbeTimeout - TimeSpan.FromSeconds(2), $"Waited {clock.Elapsed} for {silent.Address}");
            // A proxy redirecting to HTTPS: the final address is kept.
            var redirected = await ServerAddress.DiscoverAsync("http://example.test/jellyfin", Serve(uri =>
            { var response = Info(); response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.test/jellyfin/System/Info/Public"); return response; }));
            Assert(redirected.Address == "https://example.test/jellyfin/", "Redirected address not kept: " + redirected.Address);
            async Task Refused(string input, Handler handler, string expected)
            {
                try { await ServerAddress.DiscoverAsync(input, handler); throw new Exception("Accepted: " + input); }
                catch (ServerDiscoveryException ex) { Assert(ex.Message.Contains(expected), $"Unexpected message for {input}: {ex.Message}"); }
            }
            await Refused("http://nas:8096", Serve(_ => Info("4.8.10", "Emby Server")), "pas un serveur Jellyfin");
            await Refused("http://nas:8096", Serve(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), "pas un serveur Jellyfin");
            await Refused("http://nas:8096", Serve(_ => Info("10.8.13")), "10.8.13");
            await Refused("https://nas", Serve(_ => throw new HttpRequestException("TLS", new System.Security.Authentication.AuthenticationException())), "certificat");
            await Refused("nas", Serve(_ => throw new HttpRequestException("refusé")), "Aucun serveur Jellyfin");
        });
        await Test("Serveur trouvé sans le taper : réponse de Jellyfin sur le réseau, appareils Tailscale, ordre et adresse gardée", () =>
        {
            var lan = IPAddress.Parse("192.168.1.198");
            byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
            // Jellyfin 12.1's answer, as read in its container: the address it is reached at from that network.
            var reply = ServerSearch.Reply(Bytes("""{"Address":"http://192.168.1.198:8096","Id":"0d07f480c3c54c588da1d5ba7c7a1662","Name":"DESKTOP-INVEFQH","EndpointAddress":null}"""), lan);
            Assert(reply == new ServerSearch.Found("http://192.168.1.198:8096/", "DESKTOP-INVEFQH", "0d07f480c3c54c588da1d5ba7c7a1662", ServerSearch.Place.Home), "Answer: " + reply);
            // An address on no network is the one the answer came from; a published one is kept.
            Assert(ServerSearch.Reply(Bytes("""{"Address":"http://127.0.0.1:8096","Id":"a","Name":"nas"}"""), lan)?.Address == "http://192.168.1.198:8096/"
                && ServerSearch.Reply(Bytes("""{"Address":"http://0.0.0.0:8096/jellyfin","Id":"a"}"""), lan) is { Address: "http://192.168.1.198:8096/jellyfin/", Name: "Jellyfin" }
                && ServerSearch.Reply(Bytes("""{"Address":"https://jellyfin.example.test","Id":"a","Name":"nas"}"""), lan)?.Address == "https://jellyfin.example.test/", "Address of the answer");
            foreach (var bad in new[] { "", "Who is JellyfinServer?", "[]", """{"Address":"http://192.168.1.198:8096"}""", """{"Address":"ftp://nas","Id":"a"}""", """{"Address":"nas:8096","Id":"a"}""", """{"Address":"http://u:p@nas","Id":"a"}""", """{"Address":7,"Id":"a"}""" })
                Assert(ServerSearch.Reply(Bytes(bad), lan) is null, "Accepted: " + bad);

            // Each server once, at the address that serves best: Tailscale's also works at home, and wherever either goes.
            ServerSearch.Found At(string id, string name, string address, ServerSearch.Place where) => new(address, name, id, where);
            var ranked = ServerSearch.Ranked([
                At("far", "Chez Léa", "http://100.70.0.9:8096/", ServerSearch.Place.Tailnet),
                At("nas", "Salon", "http://192.168.1.20:8096/", ServerSearch.Place.Home),
                At("pc", "PC", "http://192.168.1.198:8096/", ServerSearch.Place.Home), At("PC", "PC", "http://100.114.247.13:8096/", ServerSearch.Place.Tailnet),
                At("here", "Ici", "http://192.168.1.10:8096/", ServerSearch.Place.Home), At("here", "Ici", "http://127.0.0.1:8096/", ServerSearch.Place.ThisComputer),
            ]);
            Assert(ranked.Select(x => x.Address).SequenceEqual(["http://127.0.0.1:8096/", "http://100.114.247.13:8096/", "http://192.168.1.20:8096/", "http://100.70.0.9:8096/"]), "Ranked: " + string.Join(" ; ", ranked));

            // Tailscale: the devices that may run Jellyfin, online, never a VPN's exit nodes.
            const string status = """
                {"BackendState":"Running","Self":{"HostName":"nouveau-pc","TailscaleIPs":["100.80.0.2"]},"Peer":{
                 "k1":{"HostName":"DESKTOP-INVEFQH","DNSName":"desktop-invefqh.tail1234.ts.net.","OS":"windows","TailscaleIPs":["fd7a:115c:a1e0::1","100.114.247.13"],"Online":true},
                 "k2":{"HostName":"iPhone","OS":"iOS","TailscaleIPs":["100.80.0.3"],"Online":true},
                 "k3":{"HostName":"vieux-pc","OS":"windows","TailscaleIPs":["100.80.0.4"],"Online":false},
                 "k4":{"HostName":"se-got-wg-001","DNSName":"se-got-wg-001.mullvad.ts.net.","OS":"linux","TailscaleIPs":["100.90.0.1"],"Online":true,"Location":{"Country":"Sweden"}},
                 "k5":{"HostName":"nas","OS":"linux","TailscaleIPs":["100.80.0.5"],"Online":true},
                 "k6":{"HostName":"ip6","OS":"linux","TailscaleIPs":["fd7a:115c:a1e0::6"],"Online":true}}}
                """;
            Assert(ServerSearch.TailnetDevices(status).SequenceEqual(["100.114.247.13", "100.80.0.5"]), "Devices: " + string.Join(", ", ServerSearch.TailnetDevices(status)));
            Assert(ServerSearch.TailnetDevices(null).Count == 0 && ServerSearch.TailnetDevices("not json").Count == 0 && ServerSearch.TailnetDevices("""{"BackendState":"Stopped"}""").Count == 0, "No Tailscale");
            var many = "{\"Peer\":{" + string.Join(",", Enumerable.Range(1, 50).Select(i => $"\"k{i}\":{{\"OS\":\"linux\",\"TailscaleIPs\":[\"100.80.1.{i}\"],\"Online\":true}}")) + "}}";
            Assert(ServerSearch.TailnetDevices(many).Count == ServerSearch.MaxDevices, "A large tailnet asked device by device");
            // The same from Mira's own node (« Tailscale intégré »), once it is signed in.
            var node = new TailnetStatus
            {
                State = TailnetStates.Running,
                Peers =
                [
                    new() { HostName = "DESKTOP-INVEFQH", DnsName = "desktop-invefqh.tail1234.ts.net", Os = "windows", Ips = ["100.114.247.13", "fd7a:115c:a1e0::1"], Online = true },
                    new() { HostName = "iPhone", Os = "iOS", Ips = ["100.80.0.3"], Online = true },
                    new() { HostName = "vieux-pc", Os = "windows", Ips = ["100.80.0.4"], Online = false },
                    new() { HostName = "se-got-wg-001", DnsName = "se-got-wg-001.mullvad.ts.net", Os = "linux", Ips = ["100.90.0.1"], Online = true },
                    new() { HostName = "nas", Os = "linux", Ips = ["100.80.0.5"], Online = true },
                    new() { HostName = "ip6", Os = "linux", Ips = ["fd7a:115c:a1e0::6"], Online = true },
                ],
            };
            Assert(ServerSearch.NodeDevices(node).SequenceEqual(["100.114.247.13", "100.80.0.5"]), "Devices of the node: " + string.Join(", ", ServerSearch.NodeDevices(node)));
            Assert(ServerSearch.NodeDevices(node with { State = TailnetStates.NeedsLogin }).Count == 0 && ServerSearch.NodeDevices(null).Count == 0, "A node not signed in has devices");

            // Where the question is broadcast: each network's own broadcast address, none for Tailscale or a single address.
            IPAddress? Broadcast(string address, string mask) => ServerSearch.Broadcast(IPAddress.Parse(address), IPAddress.Parse(mask));
            Assert(Broadcast("192.168.1.20", "255.255.255.0")?.ToString() == "192.168.1.255" && Broadcast("10.0.5.3", "255.255.0.0")?.ToString() == "10.0.255.255"
                && Broadcast("100.114.247.13", "255.192.0.0") is null && Broadcast("192.168.1.20", "255.255.255.255") is null && Broadcast("169.254.3.4", "255.255.0.0") is null
                && ServerSearch.Broadcast(IPAddress.Parse("fe80::1"), IPAddress.Parse("255.255.255.0")) is null && ServerSearch.Broadcast(IPAddress.Parse("192.168.1.20"), null) is null, "Broadcast addresses");

            // What the login screen says.
            Assert(ServerSearch.Said(ranked[0], "ce PC") == "Jellyfin trouvé sur ce PC." && ServerSearch.Said(ranked[2], "ce PC") == "Trouvé sur ton réseau : Salon."
                && ServerSearch.Said(ranked[1], "ce PC").StartsWith("Trouvé par Tailscale : PC.") && ServerSearch.Choice(ranked[2]) == "Salon · 192.168.1.20", "Texts");
            return Task.CompletedTask;
        });
        await Test("Serveur trouvé sans le taper : ce PC, le réseau et Tailscale demandés ensemble, un appareil muet n’attend pas plus que la recherche", async () =>
        {
            HttpResponseMessage Info(string id, string name) => JsonResponse(new { ServerName = name, Version = "12.1.0", ProductName = "Jellyfin Server", Id = id });
            var asked = new System.Collections.Concurrent.ConcurrentBag<string>();
            var handler = new Handler(async request =>
            {
                asked.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
                return request.RequestUri.Host switch
                {
                    "100.114.247.13" => Info("pc", "DESKTOP-INVEFQH"),
                    "100.80.0.5" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>routeur</html>") },
                    "100.80.0.6" => await Task.Delay(Timeout.Infinite, CancellationToken.None).ContinueWith(_ => Info("x", "x")),
                    _ => throw new HttpRequestException("refusé"),
                };
            });
            var home = (CancellationToken _) => Task.FromResult<IReadOnlyList<ServerSearch.Found>>([
                new("http://192.168.1.198:8096/", "DESKTOP-INVEFQH", "pc", ServerSearch.Place.Home), new("http://192.168.1.20:8096/", "Salon", "nas", ServerSearch.Place.Home)]);
            var clock = Stopwatch.StartNew();
            var found = await ServerSearch.FindAsync(["100.114.247.13", "100.80.0.5", "100.80.0.6"], handler, home);
            Assert(clock.Elapsed < ServerSearch.Wait + TimeSpan.FromSeconds(2), $"Waited {clock.Elapsed}");
            Assert(found.Select(x => (x.Address, x.Where)).SequenceEqual([("http://100.114.247.13:8096/", ServerSearch.Place.Tailnet), ("http://192.168.1.20:8096/", ServerSearch.Place.Home)]), "Found: " + string.Join(" ; ", found));
            Assert(asked.All(x => x.EndsWith("/System/Info/Public")) && asked.Contains("http://127.0.0.1:8096/System/Info/Public") && asked.Count == 4, "Asked: " + string.Join(", ", asked));
            // Jellyfin on this PC comes first, at 127.0.0.1.
            var here = await ServerSearch.FindAsync([], new Handler(request => Task.FromResult(request.RequestUri!.IsLoopback ? Info("pc", "DESKTOP-INVEFQH") : throw new HttpRequestException("refusé"))), home);
            Assert(here.Select(x => x.Address).SequenceEqual(["http://127.0.0.1:8096/", "http://192.168.1.20:8096/"]) && here[0].Where == ServerSearch.Place.ThisComputer, "This PC: " + string.Join(" ; ", here));
        });
        await Test("Certificat HTTPS refusé : pas de repli en http:// pour un nom d’Internet, et la raison est dite", async () =>
        {
            HttpResponseMessage Info() => JsonResponse(new { ServerName = "Salon", Version = "10.10.3", ProductName = "Jellyfin Server", Id = "server-1" });
            HttpRequestException Untrusted() => new("TLS", new System.Security.Authentication.AuthenticationException("certificat refusé"));
            async Task Refused(string input, Handler handler, params string[] expected)
            {
                try { await ServerAddress.DiscoverAsync(input, handler); throw new Exception("Accepted: " + input); }
                catch (ServerDiscoveryException ex) { Assert(expected.All(ex.Message.Contains), $"Unexpected message for {input}: {ex.Message}"); }
            }
            // HTTPS shows a certificate the computer refuses while HTTP answers: someone on the way could stage exactly this.
            await Refused("jellyfin.example.test", new Handler(request => request.RequestUri!.Scheme == "https" ? throw Untrusted() : Task.FromResult(Info())), "certificat", "en clair", "http://jellyfin.example.test:8096");
            // At home, a NAS keeps its own self-signed certificate on 443 while Jellyfin answers on 8096: found, as before.
            foreach (var home in new[] { "nas", "192.168.1.20", "nas.lan" })
                Assert((await ServerAddress.DiscoverAsync(home, new Handler(request => request.RequestUri!.Scheme == "https" ? throw Untrusted() : Task.FromResult(Info())))).Address == $"http://{home}:8096/", "Home refused: " + home);
            // HTTP redirected to a valid https:// address: what answered is encrypted, so nothing is refused.
            var moved = await ServerAddress.DiscoverAsync("example.test", new Handler(request =>
            {
                if (request.RequestUri!.Scheme == "https") throw Untrusted();
                var response = Info(); response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://www.example.test/System/Info/Public"); return Task.FromResult(response);
            }));
            Assert(moved.Address == "https://www.example.test/", "Redirect to HTTPS refused: " + moved.Address);
            // The certificate is refused after HTTP has already answered, within the moment HTTPS is given: refused all the same.
            await Refused("jellyfin.example.test", new Handler(async request =>
            {
                if (request.RequestUri!.Scheme == "https") { await Task.Delay(300); throw Untrusted(); }
                return Info();
            }), "certificat", "http://jellyfin.example.test:8096");
            // Nothing answers in HTTP: the certificate is still the reason given, with the address to type at home.
            await Refused("nas", new Handler(request => throw (request.RequestUri!.Scheme == "https" ? Untrusted() : new HttpRequestException("refusé"))), "certificat", "http://nas:8096");
            // A port typed for HTTP: HTTP comes first, and HTTPS failing on it (TLS spoken to a plain port) changes nothing.
            Assert((await ServerAddress.DiscoverAsync("nas:8096", new Handler(request => request.RequestUri!.Scheme == "https" ? throw Untrusted() : Task.FromResult(Info())))).Address == "http://nas:8096/", "HTTP refused on its own port");
            // http:// typed in full is a choice: it is the only address tried.
            Assert((await ServerAddress.DiscoverAsync("http://nas:8096", new Handler(_ => Task.FromResult(Info())))).Address == "http://nas:8096/", "Typed http:// refused");
        });
        await Test("Mot de passe en http:// : demandé seulement hors de chez toi (adresses résolues), jamais pour le réseau local ni Tailscale", async () =>
        {
            var looked = new List<string>();
            var names = new Dictionary<string, string[]>
            {
                ["nas"] = ["192.168.1.20", "fe80::1"], ["localhost"] = ["::1", "127.0.0.1"], ["jellyfin.example.com"] = ["203.0.113.7"],
                ["mixte.example.com"] = ["192.168.1.20", "203.0.113.7"], ["vide.example.com"] = [], ["nas.example.com"] = ["192.168.1.20"], ["nas.lan"] = ["192.168.1.20"]
            };
            Task<IPAddress[]> Resolve(string host, CancellationToken ct)
            {
                looked.Add(host);
                return names.TryGetValue(host, out var found) ? Task.FromResult(found.Select(IPAddress.Parse).ToArray()) : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
            }
            foreach (var home in new[] { "https://jellyfin.example.com/", "http://nas:8096/", "http://localhost:8096/", "http://127.0.0.1:8096/", "http://10.0.0.5:8096/", "http://172.16.0.1/",
                "http://172.31.255.254/", "http://192.168.1.20:8096/jellyfin/", "http://169.254.10.1:8096/", "http://100.101.102.103:8096/", "http://[fd7a:115c:a1e0::1]:8096/",
                "http://[fe80::1]:8096/", "http://[::1]:8096/", "http://[::ffff:192.168.1.20]:8096/", "http://pc.tail1234.ts.net:8096/", "http://NAS.local.:8096/", "http://nas.lan:8096/" })
                Assert(!await ServerAddress.ExposesPasswordAsync(home, Resolve), "Asked for a home address: " + home);
            Assert(looked.SequenceEqual(["nas", "localhost", "nas.lan"]), "Looked up: " + string.Join(", ", looked));
            foreach (var internet in new[] { "http://jellyfin.example.com:8096/", "http://mixte.example.com/", "http://vide.example.com/", "http://inconnu.example.com/",
                "http://8.8.8.8:8096/", "http://172.32.0.1/", "http://100.128.0.1/", "http://[2001:db8::1]:8096/", "http://nas.example.com:8096/" })
                Assert(await ServerAddress.ExposesPasswordAsync(internet, Resolve), "Not asked for an Internet address: " + internet);
            // A name of the Internet is never looked up: the DNS of a hotspot could place it at home.
            Assert(!looked.Any(x => x.EndsWith(".example.com", StringComparison.Ordinal)), "Looked up: " + string.Join(", ", looked));
            // Home networks also give each device a global IPv6 address: one on this computer's own /64 stays at home.
            (IPAddress, int)[] own = [(IPAddress.Parse("2a01:e0a:1f2:3c40::12"), 64)];
            Assert(LocalNetwork.SharesNetwork(IPAddress.Parse("2a01:e0a:1f2:3c40:aaaa::5"), own) && !LocalNetwork.SharesNetwork(IPAddress.Parse("2a01:e0a:1f2:3c41::5"), own), "Same /64");
            Assert(!LocalNetwork.SharesNetwork(IPAddress.Parse("2a01:e0a:1f2:3c41::5"), [(IPAddress.Parse("2a01:e0a:1f2:3c40::12"), 48)]), "A /48 counted as one house");
            Assert(LocalNetwork.SharesNetwork(IPAddress.Parse("2a01:e0a::7"), [(IPAddress.Parse("2a01:e0a::7"), 128)]) && !LocalNetwork.SharesNetwork(IPAddress.Parse("2a01:e0a::8"), [(IPAddress.Parse("2a01:e0a::7"), 127)]), "Prefix bits");
            Assert(!LocalNetwork.SharesNetwork(IPAddress.Parse("10.0.0.1"), own), "IPv4 compared with IPv6");
        });
        await Test("Écoute de Jellyfin : la session part dans l’en-tête, jamais dans l’adresse (Jellyfin 10.11 et suivants)", async () =>
        {
            // Jellyfin 10.11+ answers 403 "Token is required" to /socket?api_key=… unless legacy authorization is on.
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var server = new HttpListener(); server.Prefixes.Add($"http://127.0.0.1:{port}/"); server.Start();
            string? url = null, authorization = null;
            var serving = Task.Run(async () =>
            {
                var context = await server.GetContextAsync();
                url = context.Request.RawUrl; authorization = context.Request.Headers["Authorization"];
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"MessageType\":\"LibraryChanged\",\"Data\":{}}"), System.Net.WebSockets.WebSocketMessageType.Text, true, default);
                await Task.Delay(500);
            });
            using var client = new JellyfinClient(new Connection($"http://127.0.0.1:{port}/", "user-1", "Alice", "jeton-secret", "device-1"));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var changed = 0;
            var listening = client.ListenAsync(() => { changed++; stop.Cancel(); }, stop.Token);
            try { await listening; } catch (OperationCanceledException) { }
            await serving.WaitAsync(TimeSpan.FromSeconds(5)); server.Stop();
            Assert(changed == 1, "The library change was not received");
            Assert(url == "/socket" && authorization is { } header && header.Contains("Token=\"jeton-secret\"") && header.Contains("Client=\"Mira\""), $"Socket request: {url} | {authorization}");
        });
        await Test("Authentification : corps JSON, jeton dans l’en-tête, recherche encodée", async () =>
        {
            var requests = new List<(string Url, string Header, string Body)>();
            using var client = new JellyfinClient(new("http://localhost/jellyfin/", "", "", "", "device"), new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "", body));
                return JsonResponse(request.RequestUri.AbsolutePath.EndsWith("AuthenticateByName") ? new { AccessToken = "test-secret", User = new { Id = "user-1", Name = "Alice" } } : new { Items = Array.Empty<object>(), TotalRecordCount = 0 });
            }));
            await client.LoginAsync("Alice", "password-test"); await client.BrowseAsync(search: "été & lune");
            Assert(requests[0].Body.Contains("password-test"), "Missing authentication body");
            Assert(requests[1].Header.Contains("test-secret"), "Missing authorization header");
            Assert(requests[1].Url.Contains("/jellyfin/Items?"), "Lost reverse-proxy path");
            Assert(!requests.Any(r => r.Url.Contains("test-secret") || r.Url.Contains("password-test")), "Credential in request URI");
            Assert(requests[1].Url.Contains("%26"), "Search not escaped");
        });
        await Test("Hors de chez toi : adresse Tailscale de ce PC, et Jellyfin qui accepte ses appareils sans s’ouvrir à Internet", async () =>
        {
            Assert(new[] { "100.64.0.1", "100.101.102.103", "100.127.255.254" }.All(LocalNetwork.IsTailnet) && !new[] { "100.63.255.255", "100.128.0.1", "192.168.1.20", "fd7a:115c:a1e0::1", "x" }.Any(LocalNetwork.IsTailnet), "Tailscale's range");
            Assert(LocalNetwork.TailnetIPv4([("100.70.0.9", "Ethernet Realtek PCIe"), ("192.168.1.20", "Tailscale Tailscale Tunnel"), ("100.101.102.103", "Tailscale Tailscale Tunnel")]) == "100.101.102.103", "Only an address of the Tailscale adapter counts");
            Assert(LocalNetwork.TailnetIPv4([("100.70.0.9", "Ethernet Realtek PCIe")]) is null, "A 100.64 address from the Internet provider is not Tailscale");
            Assert(LocalNetwork.ForOtherDevices("http://127.0.0.1:8096", "100.101.102.103") == "http://100.101.102.103:8096", "Address on Tailscale");
            Assert(LocalNetwork.WithTailnet([]).SequenceEqual(LocalNetwork.JellyfinDefaultSubnets.Append(LocalNetwork.TailnetRange)), "Jellyfin's defaults kept, Tailscale added");
            Assert(LocalNetwork.WithTailnet(["192.168.1.0/24"]).SequenceEqual(["192.168.1.0/24", LocalNetwork.TailnetRange]) && LocalNetwork.WithTailnet(["100.64.0.0/10"]).Count == 1, "A chosen list is kept; no duplicate");

            // Jellyfin's network settings, read then written back whole with only the local networks changed.
            var stored = """{"EnableRemoteAccess":false,"LocalNetworkSubnets":[],"KnownProxies":["10.0.0.1"],"EnableHttps":false,"InternalHttpPort":8096}""";
            var admin = true; string? posted = null;
            using var client = new JellyfinClient(new("http://localhost/", "user-1", "Alice", "secret", "device"), new Handler(async request =>
            {
                if (!admin && request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (request.Method == HttpMethod.Post) { stored = posted = await request.Content!.ReadAsStringAsync(); return new HttpResponseMessage(HttpStatusCode.NoContent); }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stored, Encoding.UTF8, "application/json") };
            }));
            Assert(await client.TailnetAllowedAsync() == false, "Tailscale allowed before being asked");
            Assert(await client.AllowTailnetAsync() && await client.TailnetAllowedAsync() == true, "Tailscale not allowed after being asked");
            var sent = JsonDocument.Parse(posted!).RootElement;
            Assert(sent.GetProperty("LocalNetworkSubnets").EnumerateArray().Select(x => x.GetString()).SequenceEqual(LocalNetwork.WithTailnet([])) && !sent.GetProperty("EnableRemoteAccess").GetBoolean()
                && sent.GetProperty("KnownProxies")[0].GetString() == "10.0.0.1" && sent.GetProperty("InternalHttpPort").GetInt32() == 8096, "Other network settings changed: " + posted);
            stored = """{"EnableRemoteAccess":true,"LocalNetworkSubnets":[]}""";
            Assert(await client.TailnetAllowedAsync() == true, "Access from elsewhere already on lets Tailscale in");
            admin = false; stored = """{"EnableRemoteAccess":false,"LocalNetworkSubnets":[]}""";
            Assert(!await client.AllowTailnetAsync(), "A guest changed Jellyfin's network settings");
            // The account's policy says who may install Mira web and let Tailscale in.
            foreach (var (user, expected) in new (string, bool?)[] { ("""{"Name":"Alice","Policy":{"IsAdministrator":true}}""", true), ("""{"Policy":{"IsAdministrator":false}}""", false), ("""{"Name":"Alice"}""", null), ("[]", null) })
            {
                stored = user;
                Assert(await client.IsAdministratorAsync() == expected, $"Administrator read from {user}");
            }
        });
        await Test("Serveur muet : le délai de Jellyfin (12 s) lève une annulation qui laisse intact le jeton de Mira", async () =>
        {
            // The premise of the window's filters, which only skip a cancellation when (ct.IsCancellationRequested): those
            // filters themselves need the WPF window and are not run here.
            // Never answers: only HttpClient's own limit ends the request, shortened from its real 12 s for the test.
            using var client = new JellyfinClient(new("http://localhost/", "u", "Alice", "token", "device"), new Handler(_ => new TaskCompletionSource<HttpResponseMessage>().Task));
            var http = (HttpClient)typeof(JellyfinClient).GetField("_http", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(client)!;
            Assert(http.Timeout == TimeSpan.FromSeconds(12), "Jellyfin's time limit changed: " + http.Timeout);
            http.Timeout = TimeSpan.FromSeconds(1);
            using var refresh = new CancellationTokenSource(); var clock = Stopwatch.StartNew();
            try { await client.BrowseAsync(ct: refresh.Token); throw new Exception("A silent server answered"); }
            // This one must reach the offline notice, not leave the skeleton on screen.
            catch (OperationCanceledException ex) { Assert(!refresh.IsCancellationRequested && ex.InnerException is TimeoutException && clock.Elapsed > TimeSpan.FromSeconds(.9), $"Timeout not told apart from a cancellation ({clock.Elapsed})"); }
            using var replaced = new CancellationTokenSource(); var pending = client.BrowseAsync(ct: replaced.Token); replaced.Cancel();
            try { await pending; throw new Exception("A cancelled refresh completed"); }
            catch (OperationCanceledException) { Assert(replaced.IsCancellationRequested, "Mira's own cancellation was lost"); }
        });
        await Test("Déconnexion : Jellyfin oublie la session (Sessions/Logout), sans jamais bloquer ni lever", async () =>
        {
            string? asked = null, header = null;
            using (var client = new JellyfinClient(new("http://localhost/jellyfin/", "u", "Alice", "jeton-secret", "device"), new Handler(request =>
            {
                asked = $"{request.Method} {request.RequestUri!.AbsolutePath}"; header = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            })))
                Assert(await client.LogoutAsync(), "Logout not confirmed");
            Assert(asked == "POST /jellyfin/Sessions/Logout" && header is { } sent && sent.Contains("Token=\"jeton-secret\""), $"Logout request: {asked} | {header}");
            JellyfinClient Answering(Func<Task<HttpResponseMessage>> answer) => new(new("http://localhost/", "u", "Alice", "jeton", "device"), new Handler(_ => answer()));
            // Already revoked from Jellyfin's dashboard: nothing is left to close.
            using (var client = Answering(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))) Assert(await client.LogoutAsync(), "A revoked session reported as still open");
            using (var client = Answering(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))) Assert(!await client.LogoutAsync(), "A server error reported as closed");
            using (var client = Answering(() => throw new HttpRequestException("refusé"))) Assert(!await client.LogoutAsync(), "A refused connection reported as closed");
            // A server that never answers (the PC that hosts it is off): the sign-out goes on after a few seconds.
            var clock = Stopwatch.StartNew();
            using (var client = Answering(() => new TaskCompletionSource<HttpResponseMessage>().Task)) Assert(!await client.LogoutAsync(), "A silent server reported as closed");
            Assert(clock.Elapsed < JellyfinClient.LogoutTimeout + TimeSpan.FromSeconds(2), $"Logout waited {clock.Elapsed}");
            // Never signed in: nothing to send.
            var requested = false;
            using (var client = new JellyfinClient(new("http://localhost/", "", "", "", "device"), new Handler(_ => { requested = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); })))
                Assert(!await client.LogoutAsync() && !requested, "Logout sent without a session");
            Assert(AccountData.SignedOut("ce PC", true, null) == "Session fermée sur ce PC et sur Jellyfin.", "Sign-out message");
            Assert(AccountData.SignedOut("ce Mac", false, true) is var message && message.Contains("Appareils") && message.EndsWith("effacées de ce Mac."), "Sign-out message: " + message);
        });
        await Test("Autoriser un appareil (Quick Connect) : code à 6 chiffres, inconnu, désactivé sans fin de session", async () =>
        {
            foreach (var (typed, code) in new[] { ("458128", "458128"), (" 458 128 ", "458128"), ("458-128", "458128"), ("458\u202f128", "458128"), ("45812", null), ("4581289", null), ("458a28", null), ("", null), (null, null) })
                Assert(JellyfinClient.QuickConnectCode(typed) == code, $"Code « {typed} » → {JellyfinClient.QuickConnectCode(typed)}");
            var asked = new List<string>();
            JellyfinClient Server(bool enabled, HttpStatusCode authorize) => new(new("http://localhost/jellyfin/", "u1", "Alice", "jeton", "device"), new Handler(request =>
            {
                asked.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/QuickConnect/Enabled")
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(enabled ? "true" : "false") }
                    : new HttpResponseMessage(authorize) { Content = new StringContent(authorize == HttpStatusCode.OK ? "true" : "") });
            }));
            using (var client = Server(true, HttpStatusCode.OK)) Assert(await client.ApproveCodeAsync("458128") == CodeApproval.Approved, "Code not approved");
            Assert(asked.SequenceEqual(["GET /jellyfin/QuickConnect/Enabled", "POST /jellyfin/QuickConnect/Authorize?code=458128&userId=u1"]), string.Join(" | ", asked));
            using (var client = Server(true, HttpStatusCode.NotFound)) Assert(await client.ApproveCodeAsync("000000") == CodeApproval.Unknown, "Unknown code");
            // Turned off, Jellyfin answers 401 to Authorize: never asked, so that the session is not taken for ended.
            asked.Clear();
            using (var client = Server(false, HttpStatusCode.Unauthorized)) Assert(await client.ApproveCodeAsync("458128") == CodeApproval.Disabled && asked.Count == 1, "Disabled: " + string.Join(" | ", asked));
            using (var client = Server(true, HttpStatusCode.Unauthorized))
            { try { await client.ApproveCodeAsync("458128"); throw new Exception("A refused session went unnoticed"); } catch (UnauthorizedAccessException) { } }
            // An administrator approves a friend's phone for the friend's account.
            asked.Clear();
            using (var client = Server(true, HttpStatusCode.OK)) Assert(await client.ApproveCodeAsync("458128", "ami 2") == CodeApproval.Approved && asked[^1] == "POST /jellyfin/QuickConnect/Authorize?code=458128&userId=ami%202", string.Join(" | ", asked));
        });
        await Test("Ajouter un ami : son compte ne voit que les bibliothèques choisies, ne télécharge rien, retiré si ses droits ne passent pas", async () =>
        {
            var repository = Repository() ?? throw new Exception("Repository not found from " + AppContext.BaseDirectory);
            var made = File.ReadAllText(Path.Combine(repository, "tests", "fixtures", "jellyfin-new-user.json"));
            var policy = (JsonNode.Parse(made)!["policy"] as JsonObject)!;
            VirtualFolder[] folders = [new("Animes", "tvshows", [], "a1"), new("Films", "movies", [], "f1"), new("Séries", "tvshows", [], "s1"), new("Collections", "boxsets", [], "c1"), new("Sans id", "movies", [])];
            Assert(string.Join(",", Friends.Choices(folders).Select(x => x.Name)) == "Animes,Films,Séries", "Choices");
            Assert(string.Join(",", Friends.Folders(folders, ["f1", "S1"])) == "f1,s1,c1" && Friends.Folders(folders, []).Count == 0 && Friends.Folders(folders, ["c1"]).Count == 0, "Folders");
            var friend = Friends.Policy(policy, ["f1", "c1"]);
            Assert(friend["EnableAllFolders"]!.GetValue<bool>() == false && string.Join(",", friend["EnabledFolders"]!.AsArray().Select(x => x!.GetValue<string>())) == "f1,c1"
                && friend["EnableContentDownloading"]!.GetValue<bool>() == false && friend["EnableSyncTranscoding"]!.GetValue<bool>() == false && friend["EnableMediaConversion"]!.GetValue<bool>() == false
                && friend["IsAdministrator"]!.GetValue<bool>() == false, "Friend policy: " + friend.ToJsonString());
            // Everything else as Jellyfin set it (sent back whole), and the policy read left as it was.
            Assert(friend.Count == policy.Count && friend["IsHidden"]!.GetValue<bool>() && friend["SyncPlayAccess"]!.GetValue<string>() == "CreateAndJoinGroups"
                && friend["AuthenticationProviderId"]!.GetValue<string>() == policy["AuthenticationProviderId"]!.GetValue<string>() && policy["EnableAllFolders"]!.GetValue<bool>(), "Rest of the policy");
            foreach (var (name, problem) in new[] { ("Léa", null), ("  Marc-André ", null), ("o’neil", null), ("名前", null), ("", "Écris son prénom."), ("a/b", "ces signes"), ("a,b", "ces signes"), ("ami mira", "existe déjà"), ("Ami Mira ", "existe déjà") })
                Assert(Friends.NameProblem(name, ["Ami Mira", "mira"]) is var found && (problem is null ? found is null : found?.Contains(problem) == true), $"Name « {name} » → {Friends.NameProblem(name, ["Ami Mira", "mira"])}");
            Assert(Friends.CleanName("  o’neil ") == "o'neil", "Clean name");
            Assert(Friends.Joined(["Films"]) == "Films" && Friends.Joined(["Films", "Séries"]) == "Films et Séries" && Friends.Joined(["Animes", "Films", "Séries"]) == "Animes, Films et Séries", "Joined");
            var passwords = Enumerable.Range(0, 50).Select(_ => Friends.NewPassword()).ToList();
            Assert(passwords.Distinct().Count() == 50 && passwords.All(x => x.Length == 19 && x.Split('-') is { Length: 4 } parts && parts.All(p => p.Length == 4 && p.All(Friends.Alphabet.Contains))), "Passwords: " + passwords[0]);
            // Created, its policy read and sent back; refused, the account is removed again; a refused name in words.
            var asked = new List<string>();
            JellyfinClient Server(HttpStatusCode create, HttpStatusCode setPolicy) => new(new("http://localhost/", "u1", "Alice", "jeton", "device"), new Handler(async request =>
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
                asked.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
                var path = request.RequestUri.AbsolutePath;
                if (path == "/Users/New") return new HttpResponseMessage(create) { Content = new StringContent(create == HttpStatusCode.OK ? "{\"Name\":\"Léa\",\"Id\":\"ami1\"}" : "Error processing request.") };
                if (path == "/Users/ami1" && request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"Name\":\"Léa\",\"Id\":\"ami1\",\"Policy\":{policy.ToJsonString()}}}") };
                if (path == "/Users/ami1/Policy")
                {
                    var sent = JsonNode.Parse(body)!.AsObject();
                    asked[^1] += sent["EnableAllFolders"]!.GetValue<bool>() ? " all" : " " + string.Join(",", sent["EnabledFolders"]!.AsArray().Select(x => x!.GetValue<string>()));
                    return new HttpResponseMessage(setPolicy);
                }
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            using (var client = Server(HttpStatusCode.OK, HttpStatusCode.NoContent))
                Assert((await client.CreateFriendAsync("Léa", "k7mq-t3vx-9hcw-p2ra", ["f1", "c1"])).Id == "ami1" && asked.SequenceEqual(["POST /Users/New", "GET /Users/ami1", "POST /Users/ami1/Policy f1,c1"]), string.Join(" | ", asked));
            asked.Clear();
            using (var client = Server(HttpStatusCode.OK, HttpStatusCode.InternalServerError))
            {
                try { await client.CreateFriendAsync("Léa", "k7mq-t3vx-9hcw-p2ra", ["f1"]); throw new Exception("A policy refused went unnoticed"); }
                catch (HttpRequestException) { }
                Assert(asked.SequenceEqual(["POST /Users/New", "GET /Users/ami1", "POST /Users/ami1/Policy f1", "DELETE /Users/ami1"]), "Removed again: " + string.Join(" | ", asked));
            }
            using (var client = Server(HttpStatusCode.BadRequest, HttpStatusCode.NoContent))
            {
                try { await client.CreateFriendAsync("Léa", "k7mq-t3vx-9hcw-p2ra", ["f1"]); throw new Exception("A refused name went unnoticed"); }
                catch (UserInputException ex) { Assert(ex.Message.Contains("« Léa »"), ex.Message); }
            }
            asked.Clear();
            using (var client = Server(HttpStatusCode.OK, HttpStatusCode.NoContent))
            {
                try { await client.CreateFriendAsync("Léa", "k7mq-t3vx-9hcw-p2ra", []); throw new Exception("An account without library made"); }
                catch (UserInputException) { Assert(asked.Count == 0, "Asked: " + string.Join(" | ", asked)); }
            }
        });
        await Test("Saga d’un film : ses autres films dans l’ordre, rien sans collection ni d’un Jellyfin qui ne sait pas le dire", async () =>
        {
            var asked = new List<string>();
            JellyfinClient Server(HttpStatusCode collections, string boxes) => new(new("http://localhost/", "u1", "Alice", "jeton", "device"), new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery; asked.Add(path);
                if (path.StartsWith("/Items/f2/Collections")) return Task.FromResult(new HttpResponseMessage(collections) { Content = new StringContent(boxes) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"Items":[{"Id":"f1","Name":"Un","Type":"Movie"},{"Id":"f2","Name":"Deux","Type":"Movie"},{"Id":"f3","Name":"Trois","Type":"Movie"}]}""") });
            }));
            using (var client = Server(HttpStatusCode.OK, """{"Items":[{"Id":"b1","Name":"Trilogie - Saga","Type":"BoxSet"}]}"""))
            {
                var saga = await client.SagaAsync("f2");
                Assert(saga is { Name: "Trilogie - Saga" } && saga.Items.Select(x => x.Id).SequenceEqual(["f1", "f3"]), $"Saga: {saga?.Name} {string.Join(",", saga?.Items.Select(x => x.Id) ?? [])}");
                Assert(asked.SequenceEqual(["/Items/f2/Collections?userId=u1&limit=1", "/Items?userId=u1&parentId=b1&sortBy=ProductionYear,PremiereDate,SortName&fields=Overview,Genres,ChildCount"]), string.Join(" | ", asked));
            }
            using (var client = Server(HttpStatusCode.OK, """{"Items":[]}""")) Assert(await client.SagaAsync("f2") is null, "A saga without a collection");
            using (var client = Server(HttpStatusCode.OK, """{"Items":[{"Id":"p1","Name":"Liste","Type":"Playlist"}]}""")) Assert(await client.SagaAsync("f2") is null, "A playlist taken for a saga");
            using (var client = Server(HttpStatusCode.NotFound, "")) Assert(await client.SagaAsync("f2") is null, "An older Jellyfin (404): no saga");
        });
        await Test("Effacer les données d’un compte : son cache et ses affiches seulement, ni les autres comptes ni les réglages", async () =>
        {
            var folder = Path.Combine(testRoot, "erase-account");
            Connection Account(string user) => new("http://nas:8096/", user, user, "jeton-" + user, "device");
            var (alice, bob) = (Account("alice"), Account("bob"));
            // The names already on disk: a cache written by an earlier version is the one erased.
            Assert(AccountData.LibraryProfile(alice) == "http://nas:8096/|alice", "Library cache renamed");
            Assert(AccountData.ImageFolder(folder, alice) == Path.Combine(folder, "images", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("http://nas:8096/alice")))[..16]), "Artwork folder renamed");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(folder).FullName, "settings.json"), "{}"); File.WriteAllText(Path.Combine(folder, "device-id"), "device");
            LibraryStore Fill(Connection account)
            {
                var store = new LibraryStore(folder, AccountData.LibraryProfile(account));
                store.Save("home", new ItemsResult { TotalRecordCount = 1 }); store.Enqueue("start", Report());
                File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(AccountData.ImageFolder(folder, account)).FullName, "affiche.img"), [1, 2, 3]);
                return store;
            }
            var before = Directory.GetFiles(folder).ToHashSet();
            var kept = Fill(alice);
            var aliceFiles = Directory.GetFiles(folder).Where(x => !before.Contains(x)).ToList();
            // A copy set aside as damaged is this account's too.
            var damaged = aliceFiles.Single(x => x.EndsWith(".db")) + ".bad-20261010120000"; File.WriteAllText(damaged, "abîmée"); aliceFiles.Add(damaged);
            Fill(bob);
            // The store is still referenced, its connections pooled, as when Mira has just closed the session.
            Assert(await AccountData.EraseAsync(folder, alice), "Erase reported a file left");
            Assert(aliceFiles.All(x => !File.Exists(x)) && !Directory.Exists(AccountData.ImageFolder(folder, alice)), "Alice's data left: " + string.Join(", ", aliceFiles.Where(File.Exists)));
            var other = new LibraryStore(folder, AccountData.LibraryProfile(bob));
            Assert(other.Load<ItemsResult>("home") is { TotalRecordCount: 1 } && other.PendingCount == 1 && File.Exists(Path.Combine(AccountData.ImageFolder(folder, bob), "affiche.img")), "Bob's data touched");
            Assert(File.Exists(Path.Combine(folder, "settings.json")) && File.Exists(Path.Combine(folder, "device-id")), "Settings or device identity erased");
            var fresh = new LibraryStore(folder, AccountData.LibraryProfile(alice));
            Assert(fresh.Load<ItemsResult>("home") is null && fresh.PendingCount == 0 && fresh.DamagedCopy is null, "Alice's cache came back");
            GC.KeepAlive(kept);
            // Nothing to erase is not a failure.
            Assert(await AccountData.EraseAsync(Path.Combine(testRoot, "erase-nothing"), alice), "Erasing nothing failed");
        });
        await Test("Mac : jeton de session dans le trousseau, repris du fichier, gardé dans le fichier si le trousseau refuse", () =>
        {
            var folder = Path.Combine(testRoot, "mac-profile"); var session = Path.Combine(folder, "session.json"); var keychain = new TokenStandIn();
            string InFile() => JsonDocument.Parse(File.ReadAllBytes(session)).RootElement.GetProperty("Token").GetString() ?? "";
            // Written by a Mira without the keychain: the token is in the file.
            var before = new Mira.Mac.Services.Profile(folder); var connection = new Connection("http://nas:8096/", "user", "Alice", "token-1", before.DeviceId);
            before.SaveConnection(connection);
            Assert(InFile() == "token-1" && before.LoadConnection() == connection, "Session without a keychain not kept in its file");
            // First read with it: moved to the keychain, then out of the file.
            var profile = new Mira.Mac.Services.Profile(folder, keychain);
            Assert(profile.LoadConnection() == connection && keychain.Tokens[folder] == "token-1" && InFile() == "", "Token not moved to the keychain");
            Assert(profile.LoadConnection() == connection && new Mira.Mac.Services.Profile(folder).LoadConnection() is null, "Token not read back from the keychain alone");
            profile.SaveConnection(connection with { Token = "token-2" });
            Assert(InFile() == "" && keychain.Tokens[folder] == "token-2" && profile.LoadConnection()?.Token == "token-2", "A new token not kept in the keychain");
            // Refused (locked, question declined): sign in again, and the file keeps the token until the keychain takes it.
            keychain.Refuse = true;
            Assert(profile.LoadConnection() is null, "A session without its token opened");
            profile.SaveConnection(connection with { Token = "token-3" });
            Assert(InFile() == "token-3" && profile.LoadConnection()?.Token == "token-3" && InFile() == "token-3", "Token lost when the keychain refused it");
            // Refused by this version: not asked again at each launch, only at the next sign-in.
            keychain.Refuse = false;
            Assert(profile.LoadConnection()?.Token == "token-3" && InFile() == "token-3" && keychain.Tokens[folder] == "token-2", "The keychain asked again at launch");
            profile.SaveConnection(connection with { Token = "token-3" });
            Assert(profile.LoadConnection()?.Token == "token-3" && InFile() == "" && keychain.Tokens[folder] == "token-3", "Token left in the file once the keychain took it");
            // Signed out: in neither place.
            profile.ClearConnection();
            Assert(!File.Exists(session) && !keychain.Tokens.ContainsKey(folder) && profile.LoadConnection() is null, "Session left after signing out");
            // Signed out while the keychain kept the token: removed at a later launch.
            profile.SaveConnection(connection with { Token = "token-4" }); keychain.Refuse = true; profile.ClearConnection();
            Assert(!File.Exists(session) && keychain.Tokens[folder] == "token-4", "Stand-in: the delete was not refused");
            keychain.Refuse = false;
            Assert(profile.LoadConnection() is null && !keychain.Tokens.ContainsKey(folder), "Token left in the keychain after signing out");
            return Task.CompletedTask;
        });
    }
}
