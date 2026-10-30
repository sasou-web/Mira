using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Mac.Views;

namespace Mira.Mac.Checks;

/// <summary>
/// « Tailscale intégré » end to end (mac.yml), with the node of Mira.app as it ships: Mira --data &lt;profile&gt;
/// --tailnet-check &lt;folder&gt; --tailnet-control &lt;url&gt; --mock-port &lt;port&gt; --media &lt;video&gt;, on the test
/// tailnet of mira-testnet, whose « Jellyfin PC » exposes on port 8096 the stand-in Jellyfin served here on
/// &lt;port&gt; (MockJellyfin, through <see cref="Bridge"/>). A profile never signed in: the login page offers the node for
/// the Jellyfin PC's Tailscale address, « Se connecter à Tailscale » logs it in (its login page opened here, which logs
/// it in on that tailnet), the Jellyfin PC found among its devices, sign-in and Home through it, a film played by mpv
/// through its proxy, the Jellyfin PC turned off (said as such, by its name) then on. Run again on the same profile: the
/// node starts with Mira and Home loads through it at once; « Se déconnecter de Tailscale » then erases its key and
/// state, and the server is said to need it. Lines PASS, INFO and FAIL in result.txt, then result-again.txt, with
/// captures beside them; exit code 1 when a check fails.
/// </summary>
public sealed class TailnetCheck
{
    private readonly MainWindow _window;
    private readonly Bridge? _bridge;
    private readonly Exception? _failed;

    /// <summary>Before Mira opens its account: the stand-in Jellyfin served from the start, as a Jellyfin PC already on.</summary>
    public TailnetCheck(MainWindow window)
    {
        _window = window;
        var media = Arg(window.Args, "--media");
        try { _bridge = int.TryParse(Arg(window.Args, "--mock-port"), out var port) && File.Exists(media) ? new Bridge(new MockJellyfin(Path.GetFullPath(media), Path.GetFullPath(media)), port) : null; }
        catch (Exception ex) when (ex is HttpListenerException or IOException) { _failed = ex; }
    }
    private static string Arg(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "";

    public async Task RunAsync()
    {
        var window = _window; var args = window.Args; var bridge = _bridge;
        string Arg(string name) => TailnetCheck.Arg(args, name);
        var output = Path.GetFullPath(Arg("--tailnet-check") is { Length: > 0 } folder && !folder.StartsWith("--") ? folder : "mira-tailnet-check");
        Directory.CreateDirectory(output);
        // Signed in by the first run: this one is Mira started again.
        var again = window.Settings.Tailnet;
        var checks = new List<string>(); var step = "start"; var exit = 1;
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add("PASS " + message); Console.WriteLine("PASS  " + message); }
        void Info(string message) { checks.Add("INFO " + message); Console.WriteLine("INFO  " + message); }
        async Task Until(string what, Func<bool> test, double seconds)
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); !test(); await Task.Delay(100))
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
        }
        // Each screen captured is also read as VoiceOver would: nothing it reaches without a name.
        async Task Shot(string name)
        {
            await Task.Delay(800);
            SelfCheck.CaptureWindow(window, output, name);
            var unnamed = SelfCheck.Unnamed(window);
            Require(unnamed.Count == 0, $"VoiceOver: everything is named ({name}){(unnamed.Count > 0 ? ": " + string.Join(", ", unnamed.Take(8)) : "")}");
        }
        using var bench = new HttpClient();
        var tailscale = window.Tailnet;
        try
        {
            var control = Arg("--tailnet-control"); var port = Arg("--mock-port");
            if (_failed is not null) throw new InvalidOperationException("The stand-in Jellyfin could not be served on port " + port, _failed);
            if (control.Length == 0 || bridge is null || !args.Contains("--data"))
                throw new InvalidOperationException("The tailnet check needs --tailnet-control, --mock-port, --media and an isolated --data profile.");
            bench.BaseAddress = new Uri(control.TrimEnd('/') + "/");
            var pc = JsonNode.Parse(await bench.GetStringAsync("mira/info"))!["jellyfin"]!;
            var name = (string)pc["name"]!; var ip = (string)pc["ips"]![0]!; var server = $"http://{ip}:8096";
            Info($"the test tailnet at {control}, its Jellyfin PC « {name} » at {ip}, its Jellyfin served here on port {port}");
            window.Settings.Volume = 0; window.Settings.AutoNext = false; window.Settings.AutoSkip = false;
            Require(tailscale.Offered, "this Mira carries Built-in Tailscale (mira-tailscale in Mira.app)");
            var key = Path.Combine(window.Profile.DirectoryPath, "tailscale.key"); var log = Path.Combine(window.Profile.TailnetDirectory, "mira-tailscale.log");

            if (!again)
            {
                step = "the login page";
                await Until("the login page", () => window.Login is not null, 10);
                var login = window.Login!;
                Require(window.Session is null && tailscale.Current is null && !File.Exists(key) && !Directory.Exists(window.Profile.TailnetDirectory),
                    "never signed in: nothing of Built-in Tailscale has started, nothing went to Tailscale");
                // The Jellyfin PC's Tailscale address, as its owner gives it to a friend.
                login.Server = ip;
                await Until("the offer", () => login.TailnetOffer.Shown, 5);
                Require(login.TailnetOffer is { Text: var offer, Note: var note, Button: var button } && offer == TailnetText.ServerOnTailnet && note == TailnetText.Privacy && button == TailnetText.SignIn,
                    "the login page offers Built-in Tailscale for an address on Tailscale, with what Tailscale sees of it");
                await Shot("tailnet-offer");
                await login.SignInAsync(ip, "Contrôle", "check");
                Require(login.Error?.Contains(TailnetText.Name, StringComparison.Ordinal) == true && window.Session is null, "signing in to Jellyfin first says to sign in to Built-in Tailscale: " + login.Error);

                step = "« Se connecter à Tailscale »";
                login.ClickTailnet();
                await Until("the node signed in", () => window.Settings.Tailnet && tailscale.Current?.State == TailnetStates.Running, 120);
                var tailnet = tailscale.Current!;
                Require(tailscale.Said == TailnetText.Connected && !tailnet.SigningIn && File.Exists(key) && File.Exists(log),
                    "« Se connecter à Tailscale »: its login page opened, the node joined the tailnet, kept for the next starts with its key and log on this Mac");
                Require(login.TailnetOffer is { Shown: true, Button: null } connected && connected.Text.StartsWith(TailnetText.Name + " · ", StringComparison.Ordinal),
                    "the login page shows it connected: " + login.TailnetOffer.Text);
                Info(login.TailnetOffer.Text);
                Require(File.ReadAllText(key).Trim().Length > 0 && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(key) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0), "its key kept in a file of this macOS account only (the check's profile has no keychain)");
                await Shot("tailnet-signed-in");
                // Read while the node may write to it.
                using (var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                    Require(!(await reader.ReadToEndAsync()).Contains("/auth/", StringComparison.Ordinal), "the node's log on this Mac keeps no login page");

                step = "the Jellyfin PC among the node's devices";
                var devices = ServerSearch.NodeDevices(tailnet.Node!.Status);
                Require(devices.Contains(ip), $"the node's devices offer the Jellyfin PC to the server search ({string.Join(", ", devices)})");
                var found = await ServerAddress.DiscoverAsync(name);
                Require(found.Address.Contains(name, StringComparison.OrdinalIgnoreCase), $"the Jellyfin PC answers by its short name through the node ({found.Name} {found.Version} at {found.Address})");

                step = "sign-in through the node";
                await login.SignInAsync(ip, "Contrôle", "check");
                await Until("signed in through the node", () => window.Session is not null, 60);
                Require(window.Session!.Connection.Server.Contains(ip, StringComparison.Ordinal), $"signs in to Jellyfin at {window.Session.Connection.Server}, through the node, without the warning for http://");
            }
            else
            {
                step = "Mira started again";
                await Until("the connection kept", () => window.Session is not null, 30);
                Require(window.Login is null && window.Session!.Connection.Server.Contains(ip, StringComparison.Ordinal), "signed in before: Mira opens on Home, not on the login page");
            }

            step = "Home";
            await Until("Home through the node", () => window.Current is HomePage { Latest.Count: > 0 } && !window.Unanswered, 60);
            Require(tailscale.Current?.State == TailnetStates.Running && TailnetNetwork.Default.Choose(ip).Path == TailnetPath.Node,
                again ? "the node started with Mira, before Jellyfin was asked anything: Home loads through it at once" : $"Home shows the server's titles ({((HomePage)window.Current!).Latest.Count}) through the node");
            await Shot(again ? "tailnet-home-again" : "tailnet-home");
            window.Go("settings");
            await Until("Réglages", () => window.Current is SettingsPage { Tailnet.Shown: { Visible: true, Way.Length: > 0 } }, 10);
            var shown = ((SettingsPage)window.Current!).Tailnet.Shown;
            Require(shown.Way == TailnetText.Path(TailnetText.ThroughNode) && shown.SignOut && !shown.SignIn, $"Réglages: « {shown.State} », « {shown.Way} », with « {TailnetText.SignOut} »");
            await Shot(again ? "tailnet-settings-again" : "tailnet-settings");
            window.Go("home");

            if (!again)
            {
                step = "a film through the node";
                var film = (await window.Session!.Client.BrowseAsync("Movie", limit: 50)).Items.FirstOrDefault(x => x.RunTimeTicks > 0) ?? throw new InvalidOperationException("No film in the library.");
                await window.PlayAsync(film, fromStart: true);
                await Until("the film plays through the node", () => window.Player is { IsFileLoaded: true }, 60);
                var engine = window.Player!.Engine!;
                Require(engine.Get("path") is { } path && path.StartsWith(server, StringComparison.OrdinalIgnoreCase) && engine.Get("http-proxy") is { } proxy && proxy.Contains("@127.0.0.1:", StringComparison.Ordinal),
                    $"mpv opens « {film.Name} » from the Jellyfin PC through the node's proxy");
                var before = window.Player.Position;
                await Until("the film moves on", () => (window.Player?.Position ?? 0) - before >= 2, 30);
                Require(true, "the film plays through the node");
                await window.StopPlaybackAsync();

                step = "the Jellyfin PC turned off";
                (await bench.PostAsync("mira/jellyfin?online=0", null)).EnsureSuccessStatusCode();
                await Until("the node sees the Jellyfin PC off", () => tailscale.Current?.Node?.Status?.Peer(ip) is { Online: false }, 90);
                await window.RefreshPagesAsync();
                await Until("the notice of a PC off", () => window.NoticeText?.StartsWith(name, StringComparison.OrdinalIgnoreCase) == true, 30);
                Require(window.Unanswered && window.NoticeText == TailnetText.Asleep(TailnetNetwork.Default.Peer(ip)), "the Jellyfin PC off: said as off or asleep, by its name on Tailscale: " + window.NoticeText);
                await Shot("tailnet-pc-off");
                (await bench.PostAsync("mira/jellyfin?online=1", null)).EnsureSuccessStatusCode();
                await Until("the node sees the Jellyfin PC again", () => tailscale.Current?.Node?.Status?.Peer(ip) is { Online: true }, 90);
                // A connection opened before it went off may fail once more: refreshed until Jellyfin answers, as « Actualiser » would be.
                step = "Home back";
                for (var timer = Stopwatch.StartNew(); ; await Task.Delay(2000))
                {
                    await window.RefreshPagesAsync(fromServer: true);
                    if (!window.Unanswered && window.Current is HomePage { Latest.Count: > 0 }) break;
                    if (timer.Elapsed.TotalSeconds > 90) throw new TimeoutException("Home back: not within 90 s.");
                }
                Require(true, "the Jellyfin PC on again: Home loads again through the node");
            }
            else
            {
                step = "« Se déconnecter de Tailscale »";
                await tailscale.SignOutAsync();
                Require(!window.Settings.Tailnet && tailscale.Said == TailnetText.SignedOut && tailscale.Current?.Node is null && !File.Exists(key) && !Directory.Exists(window.Profile.TailnetDirectory),
                    "« Se déconnecter de Tailscale »: the node told Tailscale, stopped, its key and state erased from this Mac");
                Require(TailnetNetwork.Default.Choose(ip).Path == TailnetPath.Direct, "signed out: nothing goes through the node any more");
                await window.RefreshPagesAsync();
                await Until("the notice of a server on Tailscale", () => window.NoticeText == TailnetText.ServerOnTailnet, 60);
                Require(window.NoticeAction == TailnetText.SignIn, "signed out, the server on Tailscale is said to need Built-in Tailscale, with « " + window.NoticeAction + " »");
                await Shot("tailnet-signed-out");
            }
            exit = 0;
        }
        catch (Exception ex)
        {
            var node = tailscale.Current?.Node;
            var state = $"(step: {step}; node {node?.Helper.ToString() ?? "none"}, {node?.Status?.State ?? "no status"}, failure {node?.Failure?.Code ?? "none"} {node?.Failure?.Message}; " +
                $"login « {window.Login?.Error} », Tailscale « {tailscale.Said} », notice « {window.NoticeText} »; requests {bridge?.Count ?? 0})";
            checks.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + " " + state + "\n" + ex.StackTrace);
            Console.WriteLine("FAIL  " + checks[^1]);
        }
        finally { bridge?.Dispose(); }
        await File.WriteAllLinesAsync(Path.Combine(output, again ? "result-again.txt" : "result.txt"), checks);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
    }

    /// <summary>The test tailnet logs a node in when its login page is opened: opened here, as a browser would, rather than in one.</summary>
    public static async Task OpenLoginPageAsync(string page)
    {
        using var browser = new HttpClient();
        try { using var _ = await browser.GetAsync(page); }
        catch (HttpRequestException) { }
    }

    /// <summary>
    /// MockJellyfin on a TCP port of this Mac, where mira-testnet's Jellyfin PC forwards its port 8096:
    /// each request handed to the stand-in as Jellyfin would get it, its answer sent back (a range of the file as is).
    /// </summary>
    private sealed class Bridge : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpMessageInvoker _mock;
        private int _count;
        public int Count => _count;

        public Bridge(MockJellyfin mock, int port)
        {
            _mock = new HttpMessageInvoker(mock);
            // Any host: the requests name the Jellyfin PC (its Tailscale address or name), which a prefix for 127.0.0.1 would
            // turn away before any of them got here.
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
            _ = AcceptAsync();
        }

        private async Task AcceptAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => AnswerAsync(context));
            }
        }

        private async Task AnswerAsync(HttpListenerContext context)
        {
            Interlocked.Increment(ref _count);
            var asked = context.Request; var answer = context.Response;
            try
            {
                // Jellyfin's WebSocket: none here, as an older Jellyfin behind a proxy that refuses it; Mira listens again later.
                if (asked.IsWebSocketRequest) { answer.StatusCode = 404; return; }
                using var request = new HttpRequestMessage(new HttpMethod(asked.HttpMethod), new Uri("http://127.0.0.1:8096" + asked.RawUrl));
                if (asked.HasEntityBody)
                {
                    using var body = new MemoryStream();
                    await asked.InputStream.CopyToAsync(body);
                    request.Content = new ByteArrayContent(body.ToArray());
                    if (asked.ContentType is { Length: > 0 } type) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
                }
                foreach (var header in new[] { "Authorization", "Range", "Accept-Language" })
                    if (asked.Headers[header] is { } value) request.Headers.TryAddWithoutValidation(header, value);
                using var response = await _mock.SendAsync(request, CancellationToken.None);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                answer.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } contentType) answer.ContentType = contentType.ToString();
                if (response.Content.Headers.ContentRange is { } range) answer.AddHeader("Content-Range", range.ToString());
                if (response.Headers.AcceptRanges.Count > 0) answer.AddHeader("Accept-Ranges", string.Join(", ", response.Headers.AcceptRanges));
                answer.ContentLength64 = bytes.Length;
                if (asked.HttpMethod != "HEAD") await answer.OutputStream.WriteAsync(bytes);
            }
            // mpv closing a connection it no longer needs (a seek), or Mira closing.
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                try { answer.Close(); }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); }
            catch (ObjectDisposedException) { }
            _mock.Dispose();
        }
    }
}
