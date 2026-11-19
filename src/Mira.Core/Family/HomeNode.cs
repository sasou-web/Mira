using System.Diagnostics;
using Mira.Core.Tailscale;

namespace Mira.Core.Family;

/// <summary>
/// The home's own Tailscale node (« Le foyer » away from home): a second mira-tailscale, apart from « Tailscale intégré »
/// (the person's own Tailscale, if any), in its own folder with its own state key, logged in once by a home link's key on
/// the tailnet of the home's administrator, as a device of the tag:mira-home tag that reaches nothing but that Jellyfin.
/// It reaches only the home's Jellyfin (<see cref="TailnetNetwork.UseHome"/>); the person needs no Tailscale account.
/// Its state stays (the key is single-use, the device keeps its place) until the device leaves the home.
/// </summary>
public sealed class HomeNode : IAsyncDisposable
{
    public sealed record Options
    {
        /// <summary>The helper's executable, checked; or why it cannot run here. Called off the UI thread.</summary>
        public required Func<(string? Path, string? Problem)> Locate { get; init; }
        /// <summary>Arguments before the helper's own: the checks' stand-in only.</summary>
        public IReadOnlyList<string> Arguments { get; init; } = [];
        /// <summary>The node's folder: its state and its log, apart from « Tailscale intégré »'s.</summary>
        public required string Directory { get; init; }
        /// <summary>The key of its state (DPAPI, the keychain); null when it cannot be read now.</summary>
        public required Func<byte[]?> StateKey { get; init; }
        /// <summary>Erases the key and <see cref="Directory"/> (the device left the home).</summary>
        public required Action Forget { get; init; }
        public required string AppVersion { get; init; }
        /// <summary>The device's name on the administrator's tailnet.</summary>
        public string Hostname { get; init; } = "Mira-" + Environment.MachineName;
        public Action<Process>? Started { get; init; }
        /// <summary>The checks' own coordination server, never otherwise.</summary>
        public string? ControlUrl { get; init; }
        public TailnetNetwork Network { get; init; } = TailnetNetwork.Default;
        /// <summary>How long a key may take to bring the node on the tailnet.</summary>
        public TimeSpan JoinWait { get; init; } = TimeSpan.FromSeconds(45);
    }

    private readonly Options _options;
    public HomeNode(Options options) => _options = options;

    /// <summary>The helper while it runs for the home; null before and once stopped.</summary>
    public TailnetProcess? Node { get; private set; }
    /// <summary>The node changed (on any thread).</summary>
    public event Action? Changed;
    /// <summary>The node's state while its helper runs (<see cref="TailnetStates"/>); null otherwise.</summary>
    public string? State => Node is { Helper: TailnetHelper.Running } node ? node.Status?.State : null;

    /// <summary>
    /// Starts the node for the home's Jellyfin at <paramref name="host"/>, logged in by <paramref name="key"/> when its
    /// state has no login yet (a home link's; null once joined): null once it is on the tailnet, otherwise why not
    /// (<see cref="TailnetCodes"/>: <see cref="TailnetCodes.NeedsLogin"/> for a key spent, taken back or expired, or the
    /// device removed from the tailnet). Already running for this host: its state at once.
    /// </summary>
    public async Task<string?> StartAsync(string host, string? key, CancellationToken ct = default)
    {
        TailnetProcess? node;
        // Two pages asking at once (the hall at Mira's start, a profile chosen): one node, never two helpers.
        await _making.WaitAsync(ct);
        try { node = await MakeAsync(host, key, ct); }
        finally { _making.Release(); }
        if (node is null) return TailnetCodes.Helper;
        try { await node.StartAsync(ct); }
        catch (TailnetException ex) { return ex.Code; }
        catch (ObjectDisposedException) { return TailnetCodes.Stopped; }
        // On the tailnet, or why not: a node that has no login with a key may say « needs login » a moment before it uses it.
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < _options.JoinWait)
        {
            if (!ReferenceEquals(node, Node)) return TailnetCodes.Stopped;
            if (node.Helper is TailnetHelper.Failed) return node.Failure?.Code ?? TailnetCodes.Helper;
            var state = node.Helper == TailnetHelper.Running ? node.Status?.State : null;
            if (state == TailnetStates.Running && node.Proxy is not null) return null;
            if (state is TailnetStates.Expired or TailnetStates.MachineAuth) return state == TailnetStates.Expired ? TailnetCodes.Expired : TailnetCodes.MachineAuth;
            // Without a key, nothing will log it in; with one, Tailscale had its time to take it.
            if (state == TailnetStates.NeedsLogin && (key is null || waited.Elapsed > TimeSpan.FromSeconds(12))) return TailnetCodes.NeedsLogin;
            await Task.Delay(200, ct);
        }
        return node.Status?.State is { } last ? TailnetRoute.Failure(last, null) : TailnetCodes.Unreachable;
    }

    private readonly SemaphoreSlim _making = new(1, 1);

    /// <summary>The node for this host: the one already there, or a new one (with the link's key) in its place; null without the helper.</summary>
    private async Task<TailnetProcess?> MakeAsync(string host, string? key, CancellationToken ct)
    {
        // A new link's key for a node signed out (the device removed, then a new link): a new node, the key used.
        if (Node is { Helper: TailnetHelper.Running or TailnetHelper.Starting } running && _options.Network.HomeHost == TailnetRoute.Clean(host)
            && !(key is not null && running.Status?.State == TailnetStates.NeedsLogin)) return running;
        await StopAsync();
        var (path, _) = await Task.Run(() =>
        {
            var found = _options.Locate();
            if (found.Path is null) return found;
            try { System.IO.Directory.CreateDirectory(_options.Directory); return found; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, ex.Message); }
        }, ct);
        if (path is null) return null;
        var node = new TailnetProcess(new()
        {
            Executable = path, Arguments = _options.Arguments, Directory = _options.Directory, Hostname = _options.Hostname, AppVersion = _options.AppVersion,
            LogFile = Path.Combine(_options.Directory, "mira-tailscale.log"), StateKey = _options.StateKey, Started = _options.Started, ControlUrl = _options.ControlUrl,
            AuthKey = string.IsNullOrEmpty(key) ? null : key,
        });
        node.HelperChanged += () => Changed?.Invoke();
        node.StatusChanged += _ => Changed?.Invoke();
        Node = node;
        _options.Network.UseHome(node, host);
        Changed?.Invoke();
        return node;
    }

    /// <summary>The home's Jellyfin goes nowhere any more, the helper ends; its state stays for the next start.</summary>
    public async Task StopAsync()
    {
        var node = Node;
        Node = null;
        if (node is null) return;
        _options.Network.UseHome(null, null);
        await node.DisposeAsync();
        Changed?.Invoke();
    }

    /// <summary>The device left the home: the node signs out of the tailnet when it can, stops, and its state leaves this device.</summary>
    public async Task LeaveAsync()
    {
        if (Node is { Helper: TailnetHelper.Running } node)
            try { await node.LogoutAsync(); }
            catch (TailnetException) { }
            catch (ObjectDisposedException) { }
        await StopAsync();
        for (var attempt = 1; ; attempt++)
            try { await Task.Run(_options.Forget); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) return;
                await Task.Delay(500 * attempt);
            }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
