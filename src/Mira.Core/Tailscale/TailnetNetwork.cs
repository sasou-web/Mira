using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace Mira.Core.Tailscale;

/// <summary>What <see cref="TailnetNetwork"/> follows of Mira's node: <see cref="TailnetProcess"/>, or the tests' stand-in.</summary>
public interface ITailnetNode
{
    /// <summary>The node's status; null while no helper runs.</summary>
    TailnetStatus? Status { get; }
    /// <summary>Its proxy on this computer; null while no helper runs.</summary>
    TailnetProxy? Proxy { get; }
    /// <summary>Why there is no node, while there is none: <see cref="TailnetCodes.Starting"/> (it comes), or a failure's code.</summary>
    string? Unavailable { get; }
    /// <summary>Any of the above changed (on any thread).</summary>
    event Action? Changed;
}

/// <summary>
/// Mira's connections to Jellyfin servers (requests, images, downloads, the WebSocket) and mpv's: straight as always,
/// or through Mira's own Tailscale node when <see cref="TailnetRoute"/> says so, decided again for each connection. One
/// handler, shared by every client of Mira that is given none: with the node off (the default) it connects exactly as
/// .NET does by itself. A failure of the node throws a <see cref="TailnetException"/>, which HttpClient wraps
/// (<see cref="Find"/> takes it out).
/// </summary>
public sealed class TailnetNetwork
{
    /// <summary>The network of Mira's apps.</summary>
    public static TailnetNetwork Default { get; } = new();
    /// <summary>
    /// How long a connection waits for a node that is starting (the app's first requests, after waking up), its caller's
    /// own limit permitting (12 seconds for a JellyfinClient).
    /// </summary>
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(20);
    /// <summary>The header of the node's proxy that names why it could not connect (tailnet.ProxyHeader of src/Mira.Tailscale).</summary>
    public const string ReasonHeader = "X-Mira-Tailscale";

    private readonly Func<IReadOnlyCollection<IPAddress>> _readLocal;
    private readonly string? _computer;
    private readonly object _gate = new();
    private ITailnetNode? _node;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyCollection<IPAddress>? _local;
    /// <summary>The node's last status: its devices' names are known again while it starts (a short name typed as the server).</summary>
    private TailnetStatus? _known;
    private long _localRead;

    /// <param name="local">This computer's addresses (the tests'); by default those of its network adapters that are up.</param>
    /// <param name="computer">This computer's name (the tests'); by default the system's.</param>
    public TailnetNetwork(Func<IReadOnlyCollection<IPAddress>>? local = null, string? computer = null)
    {
        _readLocal = local ?? LocalAddresses;
        _computer = computer ?? (local is null ? ComputerName() : null);
        if (local is null) NetworkChange.NetworkAddressChanged += (_, _) => { lock (_gate) _local = null; Pulse(); };
        Handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            // The system's proxy (HttpClient's default) for what goes straight; never for what goes through the node.
            Proxy = new SystemProxy(this), UseProxy = true,
            // Clients share this handler: no cookie of one may go with another's requests (Jellyfin's API takes none).
            UseCookies = false,
            // A pooled connection follows the way chosen when it opened: chosen again now and then.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        Invoker = new HttpMessageInvoker(Handler, disposeHandler: false);
    }

    /// <summary>For HttpClient: <c>new HttpClient(Handler, disposeHandler: false)</c>.</summary>
    public SocketsHttpHandler Handler { get; }
    /// <summary>For ClientWebSocket.ConnectAsync, which then connects the way requests do.</summary>
    public HttpMessageInvoker Invoker { get; }

    /// <summary>
    /// The node connections may go through from now on, null to go back to the system's network alone (the person turned
    /// it off, or signed out). Called again with another process, it follows that one; with the same, nothing changes.
    /// </summary>
    public void Use(TailnetProcess? process)
    {
        if (process is not null && Volatile.Read(ref _node) is ProcessNode current && ReferenceEquals(current.Process, process)) return;
        Follow(process is null ? null : new ProcessNode(process));
    }

    /// <summary>As <see cref="Use"/>, for any node (the tests' stand-in).</summary>
    public void Follow(ITailnetNode? node)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_node, node)) return;
            if (_node is not null) _node.Changed -= Pulse;
            _node = node;
            if (node is not null) node.Changed += Pulse;
            // Turned off or signed out: its devices' names are forgotten with it.
            else _known = null;
        }
        Pulse();
    }

    /// <summary>Whether a host is a device of the tailnet, the node's devices included (« desktop-invefqh »).</summary>
    public bool IsTailnet(string host) => TailnetRoute.IsTailnet(host, Volatile.Read(ref _node)?.Status ?? Volatile.Read(ref _known));

    /// <summary>The device of the tailnet a host names, as the node last saw it (its name, whether it is online, when it was last).</summary>
    public TailnetDevice? Peer(string host) => (Volatile.Read(ref _node)?.Status ?? Volatile.Read(ref _known))?.Peer(TailnetRoute.Clean(host));

    /// <summary>The way to a host now (see <see cref="TailnetRoute.Choose"/>).</summary>
    public TailnetChoice Choose(string host)
    {
        var node = Volatile.Read(ref _node);
        var status = node?.Status;
        if (status?.Peers.Count > 0) Volatile.Write(ref _known, status);
        return TailnetRoute.Choose(host, node is not null, status, node?.Proxy is not null, Local(), node?.Unavailable, Volatile.Read(ref _known), _computer);
    }

    /// <summary>The way to a host, after waiting <see cref="StartWait"/> at most for a node that is starting.</summary>
    public async ValueTask<TailnetChoice> ChooseAsync(string host, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + StartWait;
        while (true)
        {
            Task changed;
            lock (_gate) changed = _changed.Task;
            var choice = Choose(host);
            var left = deadline - DateTime.UtcNow;
            if (choice is not { Path: TailnetPath.Fail, Code: TailnetCodes.Starting } || left <= TimeSpan.Zero) return choice;
            try { await changed.WaitAsync(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    /// <summary>
    /// What mpv needs to open <paramref name="url"/> now: null when it goes straight; else the node's proxy for mpv's
    /// http-proxy option (its credentials in it: ffmpeg answers a 407 with them, and opens https:// by CONNECT), and the
    /// Proxy-Authorization header to send with each http:// request, so that none waits for a 407 first. Throws the
    /// node's failure when the way is closed, before mpv tries.
    /// </summary>
    public (string Proxy, string Authorization)? ForPlayer(Uri url)
    {
        if (url.Scheme is not ("http" or "https")) return null;
        var choice = Choose(url.IdnHost);
        if (choice.Path == TailnetPath.Direct) return null;
        if (choice.Path == TailnetPath.Fail || Volatile.Read(ref _node)?.Proxy is not { } proxy) throw new TailnetException(choice.Code ?? TailnetCodes.Helper);
        return ($"http://{Uri.EscapeDataString(proxy.User)}:{Uri.EscapeDataString(proxy.Password)}@{proxy.Address}", proxy.Authorization);
    }

    /// <summary>The node's failure in an exception from HttpClient or ClientWebSocket (which wrap it); null when it is something else.</summary>
    public static TailnetException? Find(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is TailnetException found) return found;
            if (e is AggregateException { InnerExceptions.Count: 1 } single) return Find(single.InnerExceptions[0]);
        }
        return null;
    }

    /// <summary>
    /// Why a connection to <paramref name="host"/> failed, in the node's words (<see cref="TailnetCodes"/>): the reason it
    /// gave (<see cref="Find"/>); for a request that ran out of time, what holds the way now (a node still starting), or
    /// <see cref="TailnetCodes.PeerOffline"/> on the way to a device Tailscale says is offline (the node gives such a
    /// device a few seconds, at times more than the request had). Null when the node has nothing to say.
    /// </summary>
    public string? Explain(Exception exception, string host)
    {
        if (Find(exception) is { } failure) return failure.Code;
        if (exception is not (OperationCanceledException or TimeoutException)) return null;
        var choice = Choose(host);
        return choice.Path == TailnetPath.Fail ? choice.Code
            : choice.Path == TailnetPath.Node && Peer(host) is { Online: false } ? TailnetCodes.PeerOffline : null;
    }

    private void Pulse()
    {
        TaskCompletionSource changed;
        lock (_gate) { changed = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        changed.TrySetResult();
    }

    /// <summary>This computer's addresses, read again after a network change, or after 5 seconds.</summary>
    private IReadOnlyCollection<IPAddress> Local()
    {
        lock (_gate)
        {
            if (_local is not null && Environment.TickCount64 - _localRead < 5000) return _local;
        }
        var local = _readLocal();
        lock (_gate) { _local = local; _localRead = Environment.TickCount64; }
        return local;
    }

    private static string? ComputerName()
    {
        try { return Dns.GetHostName(); }
        catch (SocketException) { return null; }
    }

    private static IReadOnlyCollection<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus is OperationalStatus.Up or OperationalStatus.Unknown)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address).ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or PlatformNotSupportedException) { return []; }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var target = context.DnsEndPoint;
        var choice = await ChooseAsync(target.Host, ct).ConfigureAwait(false);
        switch (choice.Path)
        {
            case TailnetPath.Node when Volatile.Read(ref _node)?.Proxy is { } proxy:
                return await TunnelAsync(proxy, TailnetRoute.Clean(target.Host), target.Port, ct).ConfigureAwait(false);
            case TailnetPath.Node: throw new TailnetException(TailnetCodes.Helper, "the node's proxy is gone");
            case TailnetPath.Fail: throw new TailnetException(choice.Code ?? TailnetCodes.Helper);
        }
        // As SocketsHttpHandler connects without a callback.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A connection through the node's proxy (CONNECT, credentials sent ahead): once it answers 200, the stream is the
    /// device's. Otherwise its header says why (<see cref="ReasonHeader"/>).
    /// </summary>
    internal static async ValueTask<Stream> TunnelAsync(TailnetProxy proxy, string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            try { await socket.ConnectAsync(IPEndPoint.Parse(proxy.Address), ct).ConfigureAwait(false); }
            catch (SocketException ex) { throw new TailnetException(TailnetCodes.Helper, "the node's proxy does not answer", ex); }
            var stream = new NetworkStream(socket, ownsSocket: true);
            var authority = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Authorization: {proxy.Authorization}\r\n\r\n"), ct).ConfigureAwait(false);
            var (status, reason) = await ReadAnswerAsync(stream, ct).ConfigureAwait(false);
            if (status == 200) return stream;
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new TailnetException(status switch
            {
                // New credentials: the helper started again since this proxy was read.
                407 => TailnetCodes.Helper,
                _ => reason is { Length: > 0 } && TailnetCodes.All.Contains(reason) ? reason : TailnetCodes.Unreachable,
            }, $"the node's proxy answered {status} for {authority}");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The proxy's answer to CONNECT, read byte by byte so that nothing of the device's own stream is taken.</summary>
    private static async ValueTask<(int Status, string? Reason)> ReadAnswerAsync(Stream stream, CancellationToken ct)
    {
        var head = new List<byte>(256);
        var one = new byte[1];
        while (head.Count < 8192)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new TailnetException(TailnetCodes.Helper, "the node's proxy closed the connection");
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || !int.TryParse(parts[1], out var status))
            throw new TailnetException(TailnetCodes.Helper, "the node's proxy answered something else than HTTP");
        var reason = lines.Skip(1).Select(x => x.Split(':', 2)).Where(x => x.Length == 2 && x[0].Trim().Equals(ReasonHeader, StringComparison.OrdinalIgnoreCase))
            .Select(x => x[1].Trim()).FirstOrDefault();
        return (status, reason);
    }

    /// <summary>HttpClient's own proxy, except for what goes through the node: that connects through <see cref="ConnectAsync"/> itself.</summary>
    private sealed class SystemProxy(TailnetNetwork network) : IWebProxy
    {
        public ICredentials? Credentials { get => HttpClient.DefaultProxy.Credentials; set => HttpClient.DefaultProxy.Credentials = value; }
        public Uri? GetProxy(Uri destination) => Straight(destination) ? HttpClient.DefaultProxy.GetProxy(destination) : null;
        public bool IsBypassed(Uri host) => !Straight(host) || HttpClient.DefaultProxy.IsBypassed(host);
        private bool Straight(Uri destination) => network.Choose(destination.IdnHost).Path == TailnetPath.Direct;
    }

    /// <summary>The helper process, as the network sees it.</summary>
    private sealed class ProcessNode : ITailnetNode
    {
        private readonly TailnetProcess _process;
        public TailnetProcess Process => _process;
        public ProcessNode(TailnetProcess process)
        {
            _process = process;
            process.StatusChanged += _ => Changed?.Invoke();
            process.ProxyChanged += _ => Changed?.Invoke();
            process.HelperChanged += () => Changed?.Invoke();
        }
        public TailnetStatus? Status => _process.Status;
        public TailnetProxy? Proxy => _process.Proxy;
        public string? Unavailable => _process.Helper switch
        {
            TailnetHelper.Starting or TailnetHelper.Running => TailnetCodes.Starting,
            // Started again soon: worth the wait.
            TailnetHelper.Restarting when _process.NextAttempt is { } next && next - DateTimeOffset.UtcNow < StartWait => TailnetCodes.Starting,
            TailnetHelper.Restarting or TailnetHelper.Failed => _process.Failure?.Code ?? TailnetCodes.Helper,
            _ => TailnetCodes.Stopped,
        };
        public event Action? Changed;
    }
}
