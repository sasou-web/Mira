using System.Net;
using System.Net.NetworkInformation;
using Avalonia.Threading;
using Mira.Core;
using Mira.Core.Tailscale;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// « Tailscale intégré » on the Mac: Mira's own node on the person's Tailscale network (src/Mira.Tailscale, driven by
/// BuiltInTailnet as on Windows), for a Mac without the Tailscale app that reaches its Jellyfin away from home. Here,
/// what macOS adds (the node inside Mira.app, its key in the keychain, Gatekeeper, the Mac asleep or on another network)
/// and what the window says of it in notices; the login page (its offer) and the settings (TailnetSection) draw it again
/// on <see cref="Changed"/>. TailnetNetwork decides each connection: never the node for a Jellyfin on this Mac, nor for
/// what the Tailscale app connected here reaches.
/// </summary>
public sealed class BuiltInTailscale
{
    private readonly MainWindow _shell;
    private BuiltInTailnet? _tailnet;
    /// <summary>The checks' own tailnet (mira-testnet): its coordination server instead of Tailscale's, for --tailnet-check only.</summary>
    private readonly string? _control;
    /// <summary>Looks at the wall clock every 10 s: a jump means the Mac slept.</summary>
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(10) };
    private DateTimeOffset _ticked = DateTimeOffset.UtcNow;
    /// <summary>Another network: the node looks at it once the changes stop (they come in bursts).</summary>
    private readonly DispatcherTimer _network = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closed;

    public BuiltInTailscale(MainWindow shell)
    {
        _shell = shell;
        var args = shell.Args;
        _control = args.Contains("--tailnet-check") && Array.IndexOf(args, "--tailnet-control") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        // This Mira carries the node (tools/mac/package.sh): otherwise nothing of it shows (a build run from its sources).
        Offered = TailnetExecutable.Locate().Path is not null;
        if (!Offered) return;
        _clock.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var slept = now - _ticked > TimeSpan.FromSeconds(40);
            _ticked = now;
            if (slept) _ = RebindAsync();
        };
        _clock.Start();
        _network.Tick += (_, _) => { _network.Stop(); _ = RebindAsync(); };
        try { NetworkChange.NetworkAddressChanged += NetworkChanged; }
        catch (PlatformNotSupportedException) { }
    }

    /// <summary>This Mira carries the node: its offer, its settings and its notices show.</summary>
    public bool Offered { get; }
    /// <summary>The Tailscale app connected on this Mac: it reaches the tailnet itself, the node has nothing to add.</summary>
    public bool AppHere { get; set; }
    /// <summary>The node, once it was needed (signed in before, or « Se connecter à Tailscale »); null before.</summary>
    public BuiltInTailnet? Current => _tailnet;
    /// <summary>What the last of its buttons said (signed in, signed out, its page opened), under them in Réglages.</summary>
    public string Said { get; private set; } = "";
    /// <summary>Signing out: its button waits.</summary>
    public bool SigningOut { get; private set; }
    /// <summary>The node changed, or what is said of it: the login page and the settings draw it again. On the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Said as the node starts for a sign-in: macOS's firewall, when it is on, asks at its first start.</summary>
    public static string Firewall => L("Si macOS demande d’autoriser « mira-tailscale » à accepter des connexions entrantes, accepte : la connexion directe est plus rapide. Refuser marche aussi, par un relais de Tailscale.",
        "If macOS asks whether “mira-tailscale” may accept incoming connections, allow it: a direct connection is faster. Denying works too, through a Tailscale relay.");

    /// <summary>The node of this profile, made at the first need.</summary>
    private BuiltInTailnet Node
    {
        get
        {
            if (_tailnet is not null) return _tailnet;
            var profile = _shell.Profile;
            _tailnet = new BuiltInTailnet(new()
            {
                Locate = TailnetExecutable.Locate, Directory = profile.TailnetDirectory, StateKey = profile.TailnetKey, Forget = profile.ForgetTailnet,
                Open = OpenPage, AppVersion = JellyfinClient.AppVersion, Failed = FailureText, ControlUrl = _control,
            });
            _tailnet.SignedIn = _shell.Settings.Tailnet;
            _tailnet.Changed += () => Dispatcher.UIThread.Post(Update);
            return _tailnet;
        }
    }

    /// <summary>At Mira's start, signed in before: the node starts before Jellyfin is asked anything (the network waits for it).</summary>
    public async Task StartWithMiraAsync()
    {
        _ = ReadAppAsync();
        if (!Offered || !_shell.Settings.Tailnet) return;
        await Node.StartWithAppAsync();
    }

    /// <summary>Mira closing: the network stops using the node, then the helper ends (its state kept).</summary>
    public async Task DisposeAsync()
    {
        _closed = true; _clock.Stop(); _network.Stop();
        try { NetworkChange.NetworkAddressChanged -= NetworkChanged; }
        catch (PlatformNotSupportedException) { }
        if (_tailnet is { } tailnet) await tailnet.DisposeAsync();
    }

    /// <summary>
    /// « Se connecter à Tailscale »: the node started, then its login page opened in the browser; done once the node runs
    /// (<see cref="Update"/>). Clicked again meanwhile: the same page opened again. Also « Réessayer » for a node that stopped.
    /// </summary>
    public async Task SignInAsync()
    {
        if (!Offered || _closed) return;
        var tailnet = Node;
        if (!tailnet.SigningIn) Show(Firewall);
        var said = await tailnet.SignInAsync();
        if (said is not null) Show(tailnet.Page is not null ? said + " " + Firewall : said);
        Changed?.Invoke();
    }

    /// <summary>
    /// « Se déconnecter de Tailscale »: the node leaves the tailnet (« Logged out » in the console, where the device stays
    /// listed until removed), then its key and state leave this Mac; it no longer starts with Mira.
    /// </summary>
    public async Task SignOutAsync()
    {
        if (SigningOut || _closed) return;
        SigningOut = true; Show(TailnetText.SigningOut);
        try
        {
            var said = await Node.SignOutAsync();
            _shell.Settings.Tailnet = false;
            Show(Keep() ? said : said + " " + NotKept);
        }
        finally { SigningOut = false; Changed?.Invoke(); }
    }

    /// <summary>« Réessayer »: the node started again, from its saved state.</summary>
    public async Task RetryAsync()
    {
        if (!Offered || _closed) return;
        if (!await Node.StartAsync()) Show(_tailnet?.Problem ?? Line());
        Changed?.Invoke();
    }

    /// <summary>The tailnet's admin console, where a device waits for approval and where its key expiry can be turned off.</summary>
    public static void OpenConsole() => Updates.Open(new Uri(TailscaleNode.Machines));

    /// <summary>After sleep, or on another network: the node looks at the network afresh rather than waiting to notice.</summary>
    public Task RebindAsync() => _closed ? Task.CompletedTask : _tailnet?.RebindAsync() ?? Task.CompletedTask;

    private void NetworkChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (_closed) return; _network.Stop(); _network.Start(); });

    /// <summary>Whether the Tailscale app is connected on this Mac (an address of its own on the tailnet), read off the UI thread.</summary>
    public async Task ReadAppAsync()
    {
        if (!Offered) return;
        var here = await Task.Run(() => LocalNetwork.ThisPcOnTailnet() is not null);
        if (here == AppHere) return;
        AppHere = here;
        Changed?.Invoke();
    }

    /// <summary>On the UI thread, as the node changes: what follows from it, said once (BuiltInTailnet.Update), then shown.</summary>
    private void Update()
    {
        if (_closed || _shell.IsClosing || _tailnet is not { } tailnet) return;
        var news = tailnet.Update(DateTimeOffset.UtcNow);
        if (news.SignedIn) SignedIn();
        if (news.Lost is { } lost) _shell.Notice(TailnetText.Problem(lost)!, TailnetText.SignIn, () => _ = SignInAsync(), sticky: true);
        if (news.Expiry is { } expiry) _shell.Notice(expiry, sticky: true);
        // Joined (started, back from no network): what waited for it goes now.
        if (news.Joined) _shell.TailnetJoined();
        Changed?.Invoke();
    }

    /// <summary>The node joined the tailnet after « Se connecter à Tailscale »: kept for the next starts, and used at once.</summary>
    private void SignedIn()
    {
        _shell.Settings.Tailnet = true;
        Show(Keep() ? TailnetText.Connected : TailnetText.Connected + " " + NotKept);
        // Back from the browser, where the sign-in ended; the login page looks for the server again, among its devices.
        if (_control is null) _shell.Activate();
        _shell.Login?.FindAgain();
    }

    /// <summary>The settings saved with the node's choice; false when the disk refused them (the choice holds until Mira closes).</summary>
    private bool Keep()
    {
        try { _shell.Profile.SaveSettings(_shell.Settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static string NotKept => L("Tes réglages n’ont pas pu être enregistrés : ce choix vaudra jusqu’à la fermeture de Mira.", "Your settings couldn’t be saved: this choice will hold until Mira closes.");

    /// <summary>Says <paramref name="text"/> under the node's buttons in Réglages.</summary>
    public void Show(string text) { Said = text; Changed?.Invoke(); }

    /// <summary>The node's login page, in the browser; the checks' own tailnet logs the node in as its page opens: opened as a browser would.</summary>
    private void OpenPage(string page)
    {
        if (_control is not null) { _ = Checks.TailnetCheck.OpenLoginPageAsync(page); return; }
        if (Uri.TryCreate(page, UriKind.Absolute, out var uri)) Updates.Open(uri);
    }

    /// <summary>Why the helper cannot run here in macOS' words: Gatekeeper holds it, another version, its key; else it stops as it starts.</summary>
    private static string? FailureText(TailnetException failure)
    {
        if (failure.Code is TailnetCodes.Version or TailnetCodes.Key) return TailnetText.Problem(failure.Code);
        return TailnetExecutable.Blocked() ?? L("Tailscale intégré s’arrête dès son lancement : réinstalle Mira depuis son image disque.", "Built-in Tailscale stops as soon as it starts: reinstall Mira from its disk image.");
    }

    /// <summary>The node's state in a line, or why it cannot run here.</summary>
    public string Line() => _tailnet?.Line() ?? TailnetText.State(_shell.Settings.Tailnet ? TailnetHelper.Starting : TailnetHelper.Off, null);

    /// <summary>How Mira reaches the Jellyfin it is connected to (TailnetNetwork decides); null when not connected, or not one of these ways.</summary>
    public string? Way()
    {
        if (_shell.Session?.Connection.Server is not { } server || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost;
        return TailnetNetwork.Default.Choose(host).Path switch
        {
            TailnetPath.Node => TailnetText.ThroughNode,
            // A device of the tailnet the node does not take: the Tailscale app's, when it is connected here.
            TailnetPath.Direct when TailnetNetwork.Default.IsTailnet(host) => AppHere ? TailnetText.ThroughApp : null,
            TailnetPath.Direct => uri.IsLoopback || LocalNetwork.IsThisPc(uri.Host) || IPAddress.TryParse(host, out var ip) && LocalNetwork.IsHome(ip) ? TailnetText.Direct : null,
            // Closed: the state line says why.
            _ => null,
        };
    }

    /// <summary>
    /// Jellyfin did not answer because of Tailscale: through the node, why (its device off, Jellyfin stopped on it, the
    /// node signed out…), with the button that helps; without the node or the Tailscale app, that this server needs one.
    /// Null when Tailscale has nothing to do with it.
    /// </summary>
    public (string Text, string? Action, Action? Act)? Says(string? server, Exception error)
    {
        if (!Offered || HostOf(server) is not { } host) return null;
        var network = TailnetNetwork.Default;
        if (network.Explain(error, host) is { } code && TailnetText.Problem(code, network.Peer(host)) is { } said)
            return BuiltInTailnet.Remedy(code) switch
            {
                TailnetRemedy.SignIn => (said, TailnetText.SignIn, () => _ = SignInAsync()),
                TailnetRemedy.Console => (said, TailnetText.Console, OpenConsole),
                TailnetRemedy.Retry => (said, TailnetText.Retry, () => _ = RetryAsync()),
                _ => (said, null, null),
            };
        if (network.IsTailnet(host) && !AppHere && !_shell.Settings.Tailnet) return (TailnetText.ServerOnTailnet, TailnetText.SignIn, () => _ = SignInAsync());
        return null;
    }

    /// <summary>
    /// An address on Tailscale: one of its IPs (100.x.y.z), a MagicDNS name (….ts.net), or the name of a device the node
    /// knows. Without the Tailscale app here, only the node reaches it.
    /// </summary>
    public static bool OnTailnet(string? address) => HostOf(address) is { } host && TailnetNetwork.Default.IsTailnet(host);
    /// <summary>The host of an address as typed (« desktop-invefqh », « 100.114.247.13:8096 », « http://pc.tail1234.ts.net »).</summary>
    private static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var text = address.Trim();
        return Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text, UriKind.Absolute, out var uri) ? uri.IdnHost : text;
    }
}
