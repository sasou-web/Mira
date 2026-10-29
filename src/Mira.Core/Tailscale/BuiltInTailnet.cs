using System.Diagnostics;

namespace Mira.Core.Tailscale;

/// <summary>What helps with a failure of the node: the button of the notice that says it.</summary>
public enum TailnetRemedy
{
    None,
    /// <summary>« Se connecter à Tailscale »: never signed in, signed out, or its sign-in expired.</summary>
    SignIn,
    /// <summary>« Ouvrir la console de Tailscale »: the tailnet's administrator must approve this device.</summary>
    Console,
    /// <summary>« Réessayer »: the node stopped.</summary>
    Retry,
}

/// <summary>What follows from a change of the node, for the app to do or say, each once (<see cref="BuiltInTailnet.Update"/>).</summary>
/// <param name="SignedIn">The sign-in just ended: <see cref="BuiltInTailnet.SignedIn"/> is now true, to keep in the settings.</param>
/// <param name="Lost">Signed in before and no longer (<see cref="TailnetCodes.Expired"/> or <see cref="TailnetCodes.NeedsLogin"/>): a notice with « Se connecter à Tailscale ».</param>
/// <param name="Expiry">Its key expires soon (<see cref="TailnetText.Expiry"/>).</param>
/// <param name="Joined">It joined the tailnet (started, back from no network): what waited for it may go now.</param>
public sealed record TailnetNews(bool SignedIn = false, string? Lost = null, string? Expiry = null, bool Joined = false)
{
    public static TailnetNews None { get; } = new();
}

/// <summary>
/// « Tailscale intégré » as Mira for Windows and Mac drive it, apart from their windows, which give it what differs:
/// where its executable is, its state's key, how a page opens. Nothing goes to Tailscale before the person signs in;
/// once they have (<see cref="SignedIn"/>, kept by the app in PlayerSettings.Tailnet), the node starts with the app,
/// before Jellyfin is asked anything, and stops with it; signed out, its key and state leave the device. Its node is
/// handed to <see cref="TailnetNetwork"/>, which decides each connection. Its methods run on the app's UI thread, as
/// <see cref="Update"/> does after <see cref="Changed"/>.
/// </summary>
public sealed class BuiltInTailnet : IAsyncDisposable
{
    public sealed record Options
    {
        /// <summary>The helper's executable, checked; or why it cannot run here (missing, blocked, replaced), in the app's words. Called off the UI thread.</summary>
        public required Func<(string? Path, string? Problem)> Locate { get; init; }
        /// <summary>Arguments before the helper's own: the checks' stand-in only.</summary>
        public IReadOnlyList<string> Arguments { get; init; } = [];
        /// <summary>The node's folder: its state, encrypted with <see cref="StateKey"/>, and its log.</summary>
        public required string Directory { get; init; }
        /// <summary>The key of its state (DPAPI on Windows, the keychain on a Mac); null when it cannot be read now.</summary>
        public required Func<byte[]?> StateKey { get; init; }
        /// <summary>Erases the key and <see cref="Directory"/> (signed out); throws IOException or UnauthorizedAccessException when it cannot.</summary>
        public required Action Forget { get; init; }
        /// <summary>Opens a page in the browser: the node's login page (a check opens it itself).</summary>
        public required Action<string> Open { get; init; }
        /// <summary>Mira's version: the helper's must be the same.</summary>
        public required string AppVersion { get; init; }
        /// <summary>The device's name on the tailnet (the helper makes it a DNS label: « mira-desktop-1234 »).</summary>
        public string Hostname { get; init; } = "Mira-" + Environment.MachineName;
        /// <summary>The helper's process, just started (Windows ties it to Mira's life).</summary>
        public Action<Process>? Started { get; init; }
        /// <summary>Why the helper cannot run, in the system's words (an antivirus, Gatekeeper); null for Mira's general ones.</summary>
        public Func<TailnetException, string?>? Failed { get; init; }
        /// <summary>Another coordination server than Tailscale's: the checks' own tailnet, never otherwise.</summary>
        public string? ControlUrl { get; init; }
        public TailnetNetwork Network { get; init; } = TailnetNetwork.Default;
        /// <summary>How long a sign-in waits for the node's saved state (signed in already?) before asking for a login page.</summary>
        public TimeSpan StateWait { get; init; } = TimeSpan.FromSeconds(15);
    }

    private readonly Options _options;
    private Task<TailnetProcess?>? _preparing;
    /// <summary>Changes as the node stops (signed out, the app closing): a node made meanwhile is not used.</summary>
    private int _version;
    private bool _closed;
    /// <summary>The node's state last seen by <see cref="Update"/>, to say a change once.</summary>
    private string? _lastState;
    private bool _expirySaid;

    public BuiltInTailnet(Options options) => _options = options;

    /// <summary>The node changed, or this: raised on any thread; the app then calls <see cref="Update"/> on its own and shows it.</summary>
    public event Action? Changed;
    /// <summary>The helper, once its executable was found; null before, and once stopped.</summary>
    public TailnetProcess? Node { get; private set; }
    /// <summary>Why the node cannot run here (its executable missing, blocked, replaced, its folder refused), in words; null when it can.</summary>
    public string? Problem { get; private set; }
    /// <summary>Signed in from this device: the node starts with the app. Set by the app from its settings, then by sign-in and sign-out.</summary>
    public bool SignedIn { get; set; }
    /// <summary>« Se connecter à Tailscale » asked and not done yet: done once the node runs.</summary>
    public bool SigningIn { get; private set; }
    /// <summary>The login page opened in the browser: « Rouvrir la page de connexion » opens it again.</summary>
    public string? Page { get; private set; }
    /// <summary>The helper's state: <see cref="TailnetHelper.Off"/> before it was made.</summary>
    public TailnetHelper Helper => Node?.Helper ?? TailnetHelper.Off;
    /// <summary>The node's state (<see cref="TailnetStates"/>) while its helper runs; null otherwise.</summary>
    public string? State => Node is { Helper: TailnetHelper.Running } node ? node.Status?.State : null;

    /// <summary>At the app's start: signed in before, the node starts now (the network waits for it).</summary>
    public async Task StartWithAppAsync()
    {
        if (SignedIn && await PrepareAsync() is not null) _ = StartAsync();
    }

    /// <summary>
    /// Starts the node, from its saved state (signed in or not). False when it cannot: <see cref="Line"/> says why, and
    /// it tries again by itself when that can help.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (await PrepareAsync() is not { } node) return false;
        try { await node.StartAsync(); return true; }
        catch (TailnetException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// « Se connecter à Tailscale »: the node started, then its login page opened in the browser; done once the node runs
    /// (<see cref="Update"/>). Asked again meanwhile: the page opened again. Also « Réessayer » for a node signed in before
    /// that stopped. Returns what to say beside the button; null for nothing new.
    /// </summary>
    public async Task<string?> SignInAsync()
    {
        if (SigningIn)
        {
            // The node's own page when it has a newer one.
            if ((Node?.Status?.AuthUrl ?? Page) is not { } again) return null;
            _options.Open(again);
            return TailnetText.PageOpened;
        }
        SigningIn = true; Page = null; Changed?.Invoke();
        try
        {
            if (!await StartAsync() || Node is not { } node) { SigningIn = false; return Problem ?? FailureText() ?? TailnetText.NotStarted; }
            // Its saved state first: signed in already, or waiting for the console.
            var waited = Stopwatch.StartNew();
            while (node.Status?.State is null or TailnetStates.Starting && node.Helper == TailnetHelper.Running && waited.Elapsed < _options.StateWait) await Task.Delay(200);
            if (!SigningIn || !ReferenceEquals(node, Node)) return null;
            // Said by Update: signed in, or waiting for the console.
            if (node.Status?.State is TailnetStates.Running or TailnetStates.MachineAuth) return null;
            var page = await node.LoginAsync();
            if (!SigningIn || !ReferenceEquals(node, Node)) return null;
            Page = page; _options.Open(page);
            return TailnetText.PageOpened;
        }
        catch (TailnetException ex) { SigningIn = false; return TailnetText.Problem(ex.Code) ?? TailnetText.NoAnswer; }
        catch (ObjectDisposedException) { SigningIn = false; return null; }
        finally { Changed?.Invoke(); }
    }

    /// <summary>
    /// « Se déconnecter de Tailscale »: the node leaves the tailnet (« Logged out » in the console, where the device stays
    /// listed until removed), stops, and its key and state leave this device; it no longer starts with the app. Returns
    /// what to say.
    /// </summary>
    public async Task<string> SignOutAsync()
    {
        var told = false;
        if (Node is { Helper: TailnetHelper.Running } node)
            try { await node.LogoutAsync(); told = true; }
            catch (TailnetException) { }
            catch (ObjectDisposedException) { }
        await StopAsync();
        SignedIn = false; SigningIn = false; Page = null; _lastState = null; _expirySaid = false;
        try { await Task.Run(_options.Forget); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return TailnetText.NotErased(ex.Message, _options.Directory); }
        finally { Changed?.Invoke(); }
        return told ? TailnetText.SignedOut : TailnetText.SignedOutHere;
    }

    /// <summary>After sleep, or another network: the node looks at the network afresh rather than waiting to notice.</summary>
    public async Task RebindAsync()
    {
        if (Node is not { Helper: TailnetHelper.Running } node) return;
        try { await node.RebindAsync(); }
        catch (TailnetException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The network stops using the node, then the helper ends (its state kept for the next start).</summary>
    public async Task StopAsync()
    {
        var node = Node;
        Node = null; _preparing = null; _version++;
        if (node is null) return;
        _options.Network.Use(null);
        await node.DisposeAsync();
        Changed?.Invoke();
    }

    /// <summary>The app closing: as <see cref="StopAsync"/>, and nothing starts any more.</summary>
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await StopAsync();
    }

    /// <summary>
    /// What follows from the node's change, on the app's thread after <see cref="Changed"/>: the sign-in done, the sign-in
    /// lost, its key expiring soon, the tailnet joined; each said once.
    /// </summary>
    public TailnetNews Update(DateTimeOffset now)
    {
        if (_closed || Node is not { } node) return TailnetNews.None;
        var status = node.Status; var state = node.Helper == TailnetHelper.Running ? status?.State : null;
        var news = TailnetNews.None;
        if (state == TailnetStates.Running && SigningIn) { SigningIn = false; Page = null; SignedIn = true; news = news with { SignedIn = true }; }
        // Signed in before, then no longer (its key expired, the device removed from the console).
        else if (SignedIn && !SigningIn && state is TailnetStates.NeedsLogin or TailnetStates.Expired && state != _lastState)
            news = news with { Lost = state == TailnetStates.Expired ? TailnetCodes.Expired : TailnetCodes.NeedsLogin };
        if (state == TailnetStates.Running && !_expirySaid && TailnetText.Expiry(status, now) is { } expiry) { _expirySaid = true; news = news with { Expiry = expiry }; }
        if (state == TailnetStates.Running && _lastState != TailnetStates.Running) news = news with { Joined = true };
        if (state is not null) _lastState = state;
        return news;
    }

    /// <summary>The node's state in a line (<see cref="TailnetText.State"/>), or why it cannot run here.</summary>
    public string Line()
    {
        if (Problem is not null) return Problem;
        // Signed in or signing in, not made yet (the app just opened, its executable being checked): it comes.
        if (Node is not { } node) return TailnetText.State(SignedIn || SigningIn ? TailnetHelper.Starting : TailnetHelper.Off, null);
        var helper = node.Helper;
        return TailnetText.State(helper, helper == TailnetHelper.Running ? node.Status : null, helper == TailnetHelper.Failed ? FailureText() : null);
    }

    /// <summary>The button of a notice that says a failure of the node (<see cref="TailnetCodes"/>).</summary>
    public static TailnetRemedy Remedy(string code) => code switch
    {
        TailnetCodes.NeedsLogin or TailnetCodes.Expired => TailnetRemedy.SignIn,
        TailnetCodes.MachineAuth => TailnetRemedy.Console,
        TailnetCodes.Stopped or TailnetCodes.Helper => TailnetRemedy.Retry,
        _ => TailnetRemedy.None,
    };

    /// <summary>Why the helper cannot run, in the system's words when the app has some, else Mira's.</summary>
    private string? FailureText() => Node?.Failure is { } failure ? _options.Failed?.Invoke(failure) ?? TailnetText.Problem(failure.Code) : null;

    /// <summary>The node, made once: its executable found and checked off the UI thread, then handed to the network.</summary>
    private async Task<TailnetProcess?> PrepareAsync()
    {
        var preparing = _preparing ??= PrepareCoreAsync();
        var node = await preparing;
        // Missing or refused: looked for again the next time (the app repaired, the antivirus' verdict changed).
        if (node is null && ReferenceEquals(_preparing, preparing)) _preparing = null;
        return node;
    }
    private async Task<TailnetProcess?> PrepareCoreAsync()
    {
        var version = _version;
        var (path, problem) = await Task.Run(() =>
        {
            var found = _options.Locate();
            if (found.Path is null) return found;
            // Its log goes beside its state from the first line on, and leaves with it.
            try { System.IO.Directory.CreateDirectory(_options.Directory); return found; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, TailnetText.NoFolder(ex.Message)); }
        });
        if (_closed || version != _version) return null;
        Problem = problem;
        if (path is null) { Changed?.Invoke(); return null; }
        var node = new TailnetProcess(new()
        {
            Executable = path, Arguments = _options.Arguments, Directory = _options.Directory, Hostname = _options.Hostname, AppVersion = _options.AppVersion,
            LogFile = Path.Combine(_options.Directory, "mira-tailscale.log"), StateKey = _options.StateKey, Started = _options.Started, ControlUrl = _options.ControlUrl,
        });
        node.HelperChanged += () => Changed?.Invoke();
        node.StatusChanged += _ => Changed?.Invoke();
        Node = node;
        _options.Network.Use(node);
        return node;
    }
}
