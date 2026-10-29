using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using Mira.Core;
using Mira.Core.Tailscale;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// « Tailscale intégré » end to end (--tailnet-check, tools/server-check.ps1 -Testnet): Mira's own node on the test
    /// tailnet of mira-testnet (--tailnet-control), whose « Jellyfin PC » exposes the Jellyfin of the server check, and
    /// MIRA_CHECK_USER and _PASSWORD. A profile never signed in: the login screen offers the node for the Jellyfin PC's
    /// Tailscale address, « Se connecter à Tailscale » logs it in (its login page opened here, which logs it in on that
    /// tailnet), the Jellyfin PC found among its devices, sign-in and Home through it, a film played by mpv through its
    /// proxy, the Jellyfin PC turned off (said as such) then on. Run again on the same profile: the node starts with Mira
    /// and Home loads through it at once; « Se déconnecter de Tailscale » then erases its key and state, and the server
    /// is said to need it. Lines PASS, INFO and FAIL in result.txt, then result-again.txt; captures beside them.
    /// </summary>
    private async Task RunTailnetCheckAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--tailnet-check") + 1]); Directory.CreateDirectory(output);
        // Signed in by the first run: this one is Mira started again.
        var again = _settings.Tailnet;
        var checks = new List<string>(); var step = "start";
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add("PASS " + message); }
        void Info(string message) => checks.Add("INFO " + message);
        async Task Until(string what, Func<bool> test, double seconds)
        {
            step = what;
            for (var timer = Stopwatch.StartNew(); !test(); await Task.Delay(100))
                if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"{what}: not within {seconds} s.");
        }
        async Task Shot(string name) { await ArtworkAsync(); await CaptureAsync(output, name); }
        using var bench = new HttpClient();
        try
        {
            var user = Environment.GetEnvironmentVariable("MIRA_CHECK_USER"); var password = Environment.GetEnvironmentVariable("MIRA_CHECK_PASSWORD");
            if (_demo || TailnetControl is not { } control || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data"))
                throw new InvalidOperationException("The tailnet check needs --tailnet-control, MIRA_CHECK_USER and _PASSWORD, an isolated --data profile and no demo.");
            bench.BaseAddress = new Uri(control.TrimEnd('/') + "/");
            var pc = JsonNode.Parse(await bench.GetStringAsync("mira/info"))!["jellyfin"]!;
            var name = (string)pc["name"]!; var ip = (string)pc["ips"]![0]!; var server = $"http://{ip}:8096";
            Info($"the test tailnet at {control}, its Jellyfin PC « {name} » at {ip}");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); Width = 1440; Height = 900;
            _settings.Volume = 0; _settings.AutoNext = false; _settings.AutoSkip = false;
            Require(TailnetOffered, "this Mira carries Built-in Tailscale (mira-tailscale.exe, checked by its SHA-256)");
            var tailnet = Tailnet;
            var key = Path.Combine(_profile.DirectoryPath, "tailscale.protected"); var log = Path.Combine(_profile.TailnetDirectory, "mira-tailscale.log");

            if (!again)
            {
                step = "the login screen";
                WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = Visibility.Collapsed;
                Require(LoginOverlay.Visibility == Visibility.Visible && tailnet.Node is null && !File.Exists(key) && !Directory.Exists(_profile.TailnetDirectory),
                    "never signed in: nothing of Built-in Tailscale has started, nothing went to Tailscale");
                // The Jellyfin PC's Tailscale address, as its owner gives it to a friend.
                ServerBox.Text = ip; UsernameBox.Text = user; PasswordBox.Password = password;
                RenderTailnet();
                Require(TailnetLogin.Visibility == Visibility.Visible && TailnetLoginText.Text == TailnetText.ServerOnTailnet && TailnetLoginNote.Text == TailnetText.Privacy
                    && Equals(TailnetLoginButton.Content, TailnetText.SignIn), "the login screen offers Built-in Tailscale for an address on Tailscale, with what Tailscale sees of it");
                await Shot("tailnet-offer");
                Connect_Click(ConnectButton, new());
                await Until("signing in to Jellyfin first", () => LoginError.Text.Length > 0 && ConnectButton.IsEnabled, 15);
                Require(LoginError.Text.Contains("Tailscale intégré", StringComparison.Ordinal), "signing in to Jellyfin first says to sign in to Built-in Tailscale: " + LoginError.Text);

                step = "« Se connecter à Tailscale »";
                TailnetLogin_Click(TailnetLoginButton, new());
                await Until("the node signed in", () => _settings.Tailnet && tailnet.State == TailnetStates.Running, 120);
                Require(TailnetStatus.Text == TailnetText.Connected && !tailnet.SigningIn && File.Exists(key) && File.Exists(log),
                    "« Se connecter à Tailscale »: its login page opened, the node joined the tailnet, kept for the next starts with its key and log on this PC");
                RenderTailnetLogin();
                Require(TailnetLoginText.Text.StartsWith(TailnetText.Name + " · ", StringComparison.Ordinal) && TailnetLoginButton.Visibility == Visibility.Collapsed, "the login screen shows it connected: " + TailnetLoginText.Text);
                Info(TailnetLoginText.Text);
                await Shot("tailnet-signed-in");
                // Read while the node may write to it.
                using (var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                    Require(!(await reader.ReadToEndAsync()).Contains("/auth/", StringComparison.Ordinal), "the node's log on this PC keeps no login page");

                step = "the Jellyfin PC among the node's devices";
                var devices = ServerSearch.NodeDevices(tailnet.Node!.Status);
                Require(devices.Contains(ip), $"the node's devices offer the Jellyfin PC to the server search ({string.Join(", ", devices)})");
                var found = await ServerAddress.DiscoverAsync(name);
                Require(found.Address.Contains(name, StringComparison.OrdinalIgnoreCase), $"the Jellyfin PC answers by its short name through the node ({found.Name} {found.Version} at {found.Address})");

                step = "sign-in through the node";
                Connect_Click(ConnectButton, new());
                await Until("signed in through the node", () => LoginOverlay.Visibility == Visibility.Collapsed && _client is not null, 60);
                WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = Visibility.Collapsed;
                Require(_client!.Connection.Server.Contains(ip, StringComparison.Ordinal) && _client.Connection.UserName == user, $"signs in to Jellyfin at {_client.Connection.Server}, through the node, without the warning for http://");
            }
            else
            {
                step = "Mira started again";
                await Until("the connection kept", () => _client is not null, 30);
                Require(LoginOverlay.Visibility == Visibility.Collapsed && _client!.Connection.Server.Contains(ip, StringComparison.Ordinal), "signed in before: Mira opens on Home, not on the login screen");
            }

            step = "Home";
            // Answered by Jellyfin, not shown from the cache of the first run.
            await Until("Home through the node", () => _lastRefresh != DateTimeOffset.MinValue && _offlineSince is null && !_catalog.Loading && _catalog.Items.Count > 0, 60);
            Require(tailnet.State == TailnetStates.Running && TailnetNetwork.Default.Choose(ip).Path == TailnetPath.Node,
                again ? "the node started with Mira, before Jellyfin was asked anything: Home loads through it at once" : $"Home shows the server's titles ({_catalog.Items.Count}) through the node");
            await Shot(again ? "tailnet-home-again" : "tailnet-home");
            Settings_Click(this, new()); SelectSettingsTab("server");
            await Until("Réglages → Jellyfin", () => TailnetRow.Visibility == Visibility.Visible, 10);
            RenderTailnetRow();
            Require(TailnetWayText.Text == TailnetText.Path(TailnetText.ThroughNode) && TailnetSignOutButton.Visibility == Visibility.Visible && TailnetSignInButton.Visibility == Visibility.Collapsed,
                "Réglages → Jellyfin: « " + TailnetWayText.Text + " », with « Se déconnecter de Tailscale »");
            await Shot(again ? "tailnet-settings-again" : "tailnet-settings");
            SettingsOverlay.Visibility = Visibility.Collapsed;

            if (!again)
            {
                step = "a film through the node";
                var film = (await _client!.BrowseAsync("Movie", limit: 50)).Items.FirstOrDefault(x => x.RunTimeTicks > 0) ?? throw new InvalidOperationException("No film in the library.");
                await PlayAsync(film);
                await Until("the film plays through the node", () => _loaded, 60);
                Require(_stream is { } stream && stream.Location.StartsWith(server, StringComparison.OrdinalIgnoreCase) && _mpv!.Get("http-proxy") is { } proxy && proxy.Contains("@127.0.0.1:", StringComparison.Ordinal),
                    $"mpv opens « {film.Name} » from the Jellyfin PC through the node's proxy");
                var before = _mpv!.Number("time-pos");
                await Until("the film moves on", () => (_mpv?.Number("time-pos") ?? 0) - before >= 2, 30);
                Require(true, "the film plays through the node");
                await StopPlaybackAsync(); ReturnToLibrary();

                step = "the Jellyfin PC turned off";
                (await bench.PostAsync("mira/jellyfin?online=0", null)).EnsureSuccessStatusCode();
                await Until("the node sees the Jellyfin PC off", () => tailnet.Node?.Status?.Peer(ip) is { Online: false }, 60);
                HideNotice();
                await RefreshAsync();
                await Until("the notice of a PC off", () => Notice.Visibility == Visibility.Visible && NoticeText.Text.Contains("éteint, en veille ou sans Internet", StringComparison.Ordinal), 30);
                Require(NoticeText.Text.StartsWith(name, StringComparison.OrdinalIgnoreCase) && _offlineSince is not null, "the Jellyfin PC off: said as off or asleep, by its name on Tailscale: " + NoticeText.Text);
                await Shot("tailnet-pc-off");
                (await bench.PostAsync("mira/jellyfin?online=1", null)).EnsureSuccessStatusCode();
                await Until("the node sees the Jellyfin PC again", () => tailnet.Node?.Status?.Peer(ip) is { Online: true }, 60);
                // A connection opened before it went off may fail once more: refreshed until Jellyfin answers, as « Actualiser » would be.
                step = "Home back";
                for (var timer = Stopwatch.StartNew(); ; await Task.Delay(2000))
                {
                    await RefreshAsync();
                    if (_offlineSince is null && !_catalog.Loading && _catalog.Items.Count > 0) break;
                    if (timer.Elapsed.TotalSeconds > 90) throw new TimeoutException("Home back: not within 90 s.");
                }
                Require(true, "the Jellyfin PC on again: Home loads again through the node");
            }
            else
            {
                step = "« Se déconnecter de Tailscale »";
                await SignOutOfTailnetAsync();
                Require(!_settings.Tailnet && TailnetStatus.Text == TailnetText.SignedOut && tailnet.Node is null && !File.Exists(key) && !Directory.Exists(_profile.TailnetDirectory),
                    "« Se déconnecter de Tailscale »: the node told Tailscale, stopped, its key and state erased from this PC");
                Require(TailnetNetwork.Default.Choose(ip).Path == TailnetPath.Direct, "signed out: nothing goes through the node any more");
                HideNotice();
                await RefreshAsync();
                await Until("the notice of a server on Tailscale", () => Notice.Visibility == Visibility.Visible && NoticeText.Text == TailnetText.ServerOnTailnet, 60);
                Require(NoticeAction.Visibility == Visibility.Visible && Equals(NoticeAction.Content, TailnetText.SignIn),
                    "signed out, the server on Tailscale is said to need Built-in Tailscale, with « Se connecter à Tailscale »: " + NoticeText.Text);
                await Shot("tailnet-signed-out");
            }
        }
        catch (Exception ex)
        {
            var node = _tailnet?.Node;
            var state = $"(step: {step}; node {node?.Helper.ToString() ?? "none"}, {node?.Status?.State ?? "no status"}, failure {node?.Failure?.Code ?? "none"}; " +
                $"login « {LoginError.Text} », Tailscale « {TailnetStatus.Text} », notice « {NoticeText.Text} »; playing {_playing}, loaded {_loaded}, failure {_session.Failure ?? "none"})";
            checks.Add("FAIL " + ex.GetType().Name + ": " + ex.Message + " " + state + "\n" + ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(output, again ? "result-again.txt" : "result.txt"), checks);
            Close();
        }
    }

    /// <summary>The test tailnet logs a node in when its login page is opened: opened here, as a browser would, rather than in one.</summary>
    private static void OpenTestLoginPage(string page) => _ = Task.Run(async () =>
    {
        using var browser = new HttpClient();
        try { using var _ = await browser.GetAsync(page); }
        catch (HttpRequestException) { }
    });
}
