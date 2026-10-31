using System.Windows;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Desktop.Services;

namespace Mira.Desktop;

/// <summary>
/// « Tailscale intégré » (src/Mira.Tailscale, driven by BuiltInTailnet): Mira's own node on the person's Tailscale
/// network, for a PC without the Tailscale app that reaches its Jellyfin away from home. Here, what Windows adds (its
/// executable checked beside Mira, its key kept by DPAPI, its process tied to Mira's) and what shows: an offer on the
/// login screen, a row in Réglages → Jellyfin, notices. TailnetNetwork decides each connection: never the node for a
/// Jellyfin on this PC, nor for what the Tailscale app connected here reaches.
/// </summary>
public partial class MainWindow
{
    private BuiltInTailnet? _tailnet;
    /// <summary>The Tailscale app connected on this PC: it reaches the tailnet itself, the node has nothing to add.</summary>
    private bool _tailscaleAppHere;

    /// <summary>This Mira carries the node (Mira.Desktop.csproj): otherwise nothing of it shows.</summary>
    private static bool TailnetBuilt => TailnetExecutable.Expected is not null;
    private bool TailnetOffered => TailnetBuilt && !_demo;
    /// <summary>Said as the node starts for a sign-in, for Windows' firewall prompt at its first start.</summary>
    private static string TailnetFirewall => L("Si Windows demande d’autoriser « Mira (Tailscale) » sur ses réseaux, accepte : la connexion directe est plus rapide. Refuser marche aussi, par un relais de Tailscale.",
        "If Windows asks to allow “Mira (Tailscale)” on its networks, accept: a direct connection is faster. Refusing works too, through a Tailscale relay.");
#if MIRA_VALIDATION
    /// <summary>The checks' own tailnet (tools/tailscale, mira-testnet): its coordination server instead of Tailscale's.</summary>
    private string? TailnetControl => Array.IndexOf(_args, "--tailnet-control") is var at and >= 0 && at + 1 < _args.Length ? _args[at + 1] : null;
#endif

    /// <summary>The node of this profile, made at the first need.</summary>
    private BuiltInTailnet Tailnet
    {
        get
        {
            if (_tailnet is not null) return _tailnet;
            var profile = _profile;
            _tailnet = new BuiltInTailnet(new()
            {
                Locate = () => TailnetExecutable.Locate(profile.DirectoryPath), Directory = profile.TailnetDirectory, StateKey = profile.TailnetKey,
                Forget = profile.ForgetTailnet, Open = OpenTailnetPage, AppVersion = JellyfinClient.AppVersion, Started = ChildProcesses.BindToMira, Failed = TailnetFailureText,
#if MIRA_VALIDATION
                ControlUrl = TailnetControl,
#endif
            });
            _tailnet.SignedIn = _settings.Tailnet;
            _tailnet.Changed += () => Dispatcher.BeginInvoke(TailnetChanged);
            return _tailnet;
        }
    }

    /// <summary>The node's login page, in the browser; the checks' own tailnet logs the node in as its page opens: opened by the check itself.</summary>
    private void OpenTailnetPage(string page)
    {
#if MIRA_VALIDATION
        if (TailnetControl is not null) { OpenTestLoginPage(page); return; }
#endif
        OpenWebPage(page);
    }

    /// <summary>At Mira's start, signed in before: the node starts before Jellyfin is asked anything (the network waits for it).</summary>
    private async Task StartTailnetWithMiraAsync()
    {
        if (!_settings.Tailnet || !TailnetBuilt || _args.Contains("--demo")) return;
        await Tailnet.StartWithAppAsync();
    }

    /// <summary>Mira closing: the network stops using the node, then the helper ends (its state kept).</summary>
    private async Task StopTailnetAsync()
    {
        if (_tailnet is { } tailnet) await tailnet.DisposeAsync();
    }

    /// <summary>After sleep, or another network: the node looks at the network afresh rather than waiting to notice.</summary>
    private Task RebindTailnetAsync() => _tailnet?.RebindAsync() ?? Task.CompletedTask;

    private async void TailnetSignIn_Click(object sender, RoutedEventArgs e) => await SignInToTailnetAsync();
    /// <summary>
    /// « Se connecter à Tailscale »: the node started, then its login page opened in the browser; done once the node runs
    /// (TailnetChanged). Clicked again meanwhile: the same page opened again. Also « Réessayer » for a node that stopped.
    /// </summary>
    private async Task SignInToTailnetAsync()
    {
        var tailnet = Tailnet;
        if (!tailnet.SigningIn) TailnetSay(TailnetFirewall);
        var said = await tailnet.SignInAsync();
        if (said is not null) TailnetSay(tailnet.Page is not null ? said + " " + TailnetFirewall : said);
        RenderTailnet();
    }

    private async void TailnetSignOut_Click(object sender, RoutedEventArgs e) => await SignOutOfTailnetAsync();
    /// <summary>
    /// « Se déconnecter de Tailscale »: the node leaves the tailnet (« Logged out » in the console, where the device stays
    /// listed until removed), then its key and state leave this PC; it no longer starts with Mira.
    /// </summary>
    private async Task SignOutOfTailnetAsync()
    {
        TailnetSignOutButton.IsEnabled = false; TailnetSay(TailnetText.SigningOut);
        try
        {
            var said = await Tailnet.SignOutAsync();
            _settings.Tailnet = false;
            if (!TrySaveSettings()) AddNotice(SettingsNotSaved);
            TailnetSay(said);
        }
        finally { TailnetSignOutButton.IsEnabled = true; RenderTailnet(); }
    }

    private void TailnetConsole_Click(object sender, RoutedEventArgs e) => OpenWebPage(TailscaleNode.Machines);
    private void TailnetPolicy_Click(object sender, RoutedEventArgs e) => OpenWebPage(TailscaleNode.Policy);
    /// <summary>The setting that lets the node start during an outage of Tailscale, for the tailnet's policy (TailnetText.Outage).</summary>
    private void TailnetOutageCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TailnetText.OutageSetting.Replace("\n", Environment.NewLine, StringComparison.Ordinal)); TailnetSay(TailnetText.OutageCopied); }
        catch (System.Runtime.InteropServices.ExternalException) { TailnetSay(L("Le presse-papiers est occupé : réessaie.", "The clipboard is busy: try again.")); }
    }

    /// <summary>On the UI thread, as the node changes: what follows from it, said once (BuiltInTailnet.Update), then shown.</summary>
    private void TailnetChanged()
    {
        if (_closing || _tailnet is not { } tailnet) return;
        var news = tailnet.Update(DateTimeOffset.UtcNow);
        if (news.SignedIn) TailnetSignedIn();
        if (news.Lost is { } lost) SetNotice(TailnetText.Problem(lost)!, TailnetText.SignIn, () => _ = SignInToTailnetAsync());
        if (news.Expiry is { } expiry) AddNotice(expiry);
        // Joined (started, back from no network): what waited for it goes now.
        if (news.Joined && _client is not null && _offlineSince is not null) _ = RefreshAsync(quiet: true);
        RenderTailnet();
    }

    /// <summary>The node joined the tailnet after « Se connecter à Tailscale »: kept for the next starts, and used at once.</summary>
    private void TailnetSignedIn()
    {
        _settings.Tailnet = true;
        if (!TrySaveSettings()) AddNotice(SettingsNotSaved);
        TailnetSay(TailnetText.Connected);
        // Back from the browser, where the sign-in ended.
        if (!ValidationRun) RestoreFromWindows();
        if (LoginOverlay.Visibility == Visibility.Visible && ServerFieldFree) FindServer();
    }

    /// <summary>Said under the node in Réglages, after one of its buttons.</summary>
    private void TailnetSay(string text) => TailnetStatus.Text = text;

    /// <summary>The login screen's offer and the settings' row, as the node is now.</summary>
    private void RenderTailnet()
    {
        RenderTailnetLogin();
        RenderTailnetRow();
    }

    /// <summary>
    /// Réglages → Jellyfin: where the node is and how Mira reaches Jellyfin, what it is for and what Tailscale sees of it,
    /// then the buttons that fit: sign in (or its page again), approve in the console, try again, sign out.
    /// </summary>
    private void RenderTailnetRow()
    {
        TailnetRow.Visibility = TailnetOffered ? Visibility.Visible : Visibility.Collapsed;
        if (!TailnetOffered) return;
        var tailnet = _tailnet; var helper = tailnet?.Helper ?? TailnetHelper.Off; var state = tailnet?.State;
        var signedIn = _settings.Tailnet; var signingIn = tailnet?.SigningIn == true; var page = tailnet?.Page;
        TailnetTitle.Text = TailnetText.Name;
        // The Tailscale app connected here (Jellyfin's PC): said, rather than « Pas connecté ».
        TailnetState.Text = _tailscaleAppHere && !signedIn && !signingIn ? TailnetText.AppHere : TailnetLine();
        TailnetWayText.Text = TailnetWay() is { } way ? TailnetText.Path(way) : "";
        TailnetWayText.Visibility = TailnetWayText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TailnetAbout.Text = TailnetText.Purpose + " " + TailnetText.Privacy;
        // On a tailnet that doesn't let it start during an outage of Tailscale: the setting, gone once the policy has it.
        TailnetOutage.Visibility = tailnet?.AsksOutageCache == true ? Visibility.Visible : Visibility.Collapsed;
        TailnetOutageText.Text = TailnetText.Outage;
        TailnetOutageCopyButton.Content = TailnetText.CopyOutage;
        TailnetPolicyButton.Content = TailnetText.OpenPolicy;
        var stopped = signedIn && helper is TailnetHelper.Off or TailnetHelper.Stopped or TailnetHelper.Failed or TailnetHelper.Restarting;
        var needsLogin = state is TailnetStates.NeedsLogin or TailnetStates.Expired;
        TailnetSignInButton.Content = signingIn && page is not null ? TailnetText.ReopenPage : stopped ? TailnetText.Retry : TailnetText.SignIn;
        TailnetSignInButton.IsEnabled = !signingIn || page is not null;
        TailnetSignInButton.Visibility = tailnet?.Problem is null && (!signedIn || signingIn || needsLogin || stopped) && state != TailnetStates.MachineAuth ? Visibility.Visible : Visibility.Collapsed;
        TailnetSignOutButton.Content = TailnetText.SignOut;
        TailnetSignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        TailnetConsoleButton.Content = TailnetText.Console;
        TailnetConsoleButton.Visibility = signedIn || state == TailnetStates.MachineAuth ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The login screen, for a PC without the Tailscale app: the node offered when the server is on Tailscale or none was
    /// found here, its sign-in followed, and once in, what it is connected as.
    /// </summary>
    private void RenderTailnetLogin()
    {
        var tailnet = _tailnet; var helper = tailnet?.Helper ?? TailnetHelper.Off; var state = tailnet?.State;
        var signingIn = tailnet?.SigningIn == true; var page = tailnet?.Page;
        var wanted = OnTailnet(ServerBox.Text) || _loginNoneFound;
        var show = TailnetOffered && !_tailscaleAppHere && tailnet?.Problem is null && (signingIn || _settings.Tailnet || wanted);
        TailnetLogin.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var engaged = signingIn || _settings.Tailnet;
        TailnetLoginText.Text = state == TailnetStates.Running ? $"{TailnetText.Name} · {TailnetLine()}"
            : engaged ? TailnetLine()
            : OnTailnet(ServerBox.Text) ? TailnetText.ServerOnTailnet : TailnetText.AwayFromHome;
        TailnetLoginNote.Text = engaged ? (state is null or TailnetStates.Starting && signingIn ? TailnetFirewall : "") : TailnetText.Privacy;
        TailnetLoginNote.Visibility = TailnetLoginNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TailnetLoginButton.Content = state == TailnetStates.MachineAuth ? TailnetText.Console
            : signingIn && page is not null ? TailnetText.ReopenPage
            : engaged && helper is TailnetHelper.Failed or TailnetHelper.Restarting or TailnetHelper.Stopped ? TailnetText.Retry : TailnetText.SignIn;
        TailnetLoginButton.IsEnabled = !signingIn || page is not null || state == TailnetStates.MachineAuth;
        TailnetLoginButton.Visibility = state == TailnetStates.Running || helper == TailnetHelper.Starting && !signingIn ? Visibility.Collapsed : Visibility.Visible;
    }
    private void TailnetLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_tailnet?.State == TailnetStates.MachineAuth) OpenWebPage(TailscaleNode.Machines);
        else _ = SignInToTailnetAsync();
    }

    /// <summary>The node's state in a line, or why it cannot run here.</summary>
    private string TailnetLine() => _tailnet?.Line() ?? TailnetText.State(_settings.Tailnet ? TailnetHelper.Starting : TailnetHelper.Off, null);

    /// <summary>Why the helper cannot run here in Windows' words: blocked by the antivirus, another version, its key; else an antivirus is likely.</summary>
    private static string? TailnetFailureText(TailnetException failure)
    {
        if (TailnetExecutable.Blocked(failure) || failure.InnerException is { } inner && TailnetExecutable.Blocked(inner)) return TailnetExecutable.Quarantined;
        return failure.Code is TailnetCodes.Version or TailnetCodes.Key ? TailnetText.Problem(failure.Code)
            : L("Tailscale intégré s’arrête dès son lancement : un antivirus le bloque peut-être (Sécurité Windows → Historique de protection).", "Built-in Tailscale stops as soon as it starts: an antivirus may be blocking it (Windows Security → Protection history).");
    }

    /// <summary>
    /// An address on Tailscale: one of its IPs (100.x.y.z), a MagicDNS name (….ts.net), or the name of a device the node
    /// knows. Without the Tailscale app here, only the node reaches it.
    /// </summary>
    private static bool OnTailnet(string? address) => HostOf(address) is { } host && TailnetNetwork.Default.IsTailnet(host);
    /// <summary>The host of an address as typed (« desktop-invefqh », « 100.114.247.13:8096 », « http://pc.tail1234.ts.net »).</summary>
    private static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var text = address.Trim();
        return Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text, UriKind.Absolute, out var uri) ? uri.IdnHost : text;
    }

    /// <summary>How Mira reaches the Jellyfin it is connected to (TailnetNetwork decides); null when not connected, or not one of these ways.</summary>
    private string? TailnetWay()
    {
        if (_demo || _client?.Connection.Server is not { } server || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost;
        return TailnetNetwork.Default.Choose(host).Path switch
        {
            TailnetPath.Node => TailnetText.ThroughNode,
            // A device of the tailnet the node does not take: the Tailscale app's, when it is connected here.
            TailnetPath.Direct when TailnetNetwork.Default.IsTailnet(host) => _tailscaleAppHere ? TailnetText.ThroughApp : null,
            TailnetPath.Direct => uri.IsLoopback || LocalNetwork.IsThisPc(uri.Host) || System.Net.IPAddress.TryParse(host, out var ip) && LocalNetwork.IsHome(ip) ? TailnetText.Direct : null,
            // Closed: the state line says why.
            _ => null,
        };
    }

    /// <summary>Whether the Tailscale app is connected on this PC, read off the UI thread, then shown.</summary>
    private async Task ReadTailscaleAppAsync()
    {
        if (!TailnetOffered) return;
        _tailscaleAppHere = await Task.Run(() => LocalNetwork.ThisPcOnTailnet() is not null);
        RenderTailnet();
    }

    /// <summary>
    /// Jellyfin did not answer because of Tailscale: through the node, why (its device off, Jellyfin stopped on it, the
    /// node signed out…), with the button that helps; without the node or the Tailscale app, that this server needs one.
    /// Null when Tailscale has nothing to do with it. <paramref name="error"/> null: still no answer, said as for a
    /// request that ran out of time (what holds the way now).
    /// </summary>
    private (string Text, string? Action, Action? Act)? TailnetSays(string server, Exception? error)
    {
        if (!TailnetOffered || HostOf(server) is not { } host) return null;
        var network = TailnetNetwork.Default;
        if (network.Explain(error ?? new TimeoutException(), host) is { } code && TailnetText.Problem(code, network.Peer(host)) is { } said)
            return BuiltInTailnet.Remedy(code) switch
            {
                TailnetRemedy.SignIn => (said, TailnetText.SignIn, () => _ = SignInToTailnetAsync()),
                TailnetRemedy.Console => (said, TailnetText.Console, () => OpenWebPage(TailscaleNode.Machines)),
                TailnetRemedy.Retry => (said, TailnetText.Retry, () => _ = Tailnet.StartAsync()),
                _ => (said, null, null),
            };
        if (network.IsTailnet(host) && !_tailscaleAppHere && !_settings.Tailnet) return (TailnetText.ServerOnTailnet, TailnetText.SignIn, () => _ = SignInToTailnetAsync());
        return null;
    }
}
