using System.Net;
using System.Text.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Mira.Core.Tailscale;

namespace Mira.Core;

/// <summary>
/// Jellyfin found again once the box gives its PC another address on the home network (192.168.1.42 one day,
/// 192.168.1.57 after a restart): its own id (System/Info/Public), the same at every address, is looked for on this
/// computer, then by Jellyfin's discovery on the home network (<see cref="ServerSearch"/>), then at each address of the
/// network this computer shares with the old one, on its port. Only an address of the home network moves so: a name,
/// an address of the Internet or of Tailscale stays as it is. Nothing is asked beyond the home network, and only
/// Jellyfin's public information, as the login screen asks it, then its proof (<see cref="ServerProof"/>): the id is
/// public, and a device of another network (a hotel's, a friend's on the same 192.168.1.x) that answered with it would
/// otherwise have been sent the token. The token goes only to the address that signed this account's nonce; an account
/// without its proof key (extension absent or older) never moves. Nothing found: asked again less and less often
/// (<see cref="After"/>), as on a network that is not the home's. The account keeps its data on this computer
/// (<see cref="Connection.Account"/>). The iPhone does the same in its way to Jellyfin (Way, WayPlan.neighbours), on the
/// cases of tests/fixtures/server-move.json.
/// </summary>
public static class ServerMove
{
    /// <summary>Looked for at most this often while Jellyfin stays silent (each refresh that fails tries again).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    /// <summary>
    /// The wait before the next search after <paramref name="fruitless"/> found nothing in a row: 2 min, then twice as long
    /// each time, 30 min at most. Away from home (a hotel's network may be 192.168.1.x too), each address of its network
    /// is not asked every 2 min for hours.
    /// </summary>
    public static TimeSpan After(int fruitless) => TimeSpan.FromMinutes(Math.Min(30, 2 * Math.Pow(2, Math.Clamp(fruitless - 1, 0, 4))));
    /// <summary>How long each address of the network has to answer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Whether this account's Jellyfin may have moved: its id and its proof key known, at an IPv4 address of the home
    /// network (10.x, 172.16–31.x, 192.168.x), never this computer, Tailscale, a name, nor a party's guest account.
    /// </summary>
    public static bool Movable(Connection connection) => connection.ServerId is { Length: > 0 } && connection.ServerProof is { Length: > 0 }
        && ServerProof.PlainId(connection.UserId) is not null && !Together.IsGuest(connection) && HomeAddress(connection.Server) is not null;

    /// <summary>The IPv4 address of the home network in <paramref name="server"/>; null for anything else.</summary>
    public static IPAddress? HomeAddress(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.IPv4 || !IPAddress.TryParse(uri.Host, out var address)) return null;
        return address.GetAddressBytes() is [10, ..] or [172, >= 16 and < 32, ..] or [192, 168, ..] ? address : null;
    }

    /// <summary>The same server's id, written with or without its dashes, in any case.</summary>
    public static bool SameId(string? a, string? b) => a is { Length: > 0 } && b is { Length: > 0 } && Plain(a) == Plain(b);
    private static string Plain(string id) => id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// Where this account's server is now among the servers <paramref name="found"/>: the one with its id at another
    /// address, on this computer first, then on the home network (an https:// server only at an https:// address). Never
    /// through Tailscale: the address chosen at home stays at home (full quality, StreamQuality.AtHome).
    /// </summary>
    public static string? Found(Connection connection, IEnumerable<ServerSearch.Found> found) => found
        .Where(x => x.Where != ServerSearch.Place.Tailnet && SameId(x.Id, connection.ServerId) && Allowed(connection.Server, x.Address))
        .OrderBy(x => x.Where == ServerSearch.Place.ThisComputer ? 0 : 1)
        .Select(x => x.Address).FirstOrDefault();
    private static bool Allowed(string server, string address) =>
        Uri.TryCreate(server, UriKind.Absolute, out var from) && Uri.TryCreate(address, UriKind.Absolute, out var to)
        && (from.Scheme != Uri.UriSchemeHttps || to.Scheme == Uri.UriSchemeHttps)
        && !string.Equals(JellyfinClient.NormalizeServer(server), JellyfinClient.NormalizeServer(address), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The addresses to ask on the network this computer shares with <paramref name="server"/> (<paramref name="own"/>,
    /// <paramref name="prefix"/> bits): each one of it, at the server's scheme, port and path, the server's old address
    /// and this computer left out. None when the server's address is on another network (another home's Wi-Fi), or the
    /// network is a single address; a network wider than /24 is asked on the /24 around this computer only.
    /// </summary>
    public static IReadOnlyList<string> Neighbours(string server, IPAddress own, int prefix)
    {
        if (HomeAddress(server) is not { } old || !Uri.TryCreate(server, UriKind.Absolute, out var uri)) return [];
        if (own.IsIPv4MappedToIPv6) own = own.MapToIPv4();
        if (own.AddressFamily != AddressFamily.InterNetwork || prefix is < 1 or > 30) return [];
        var bits = Math.Max(prefix, 24);
        var mask = uint.MaxValue << (32 - bits);
        uint Number(IPAddress address) { var b = address.GetAddressBytes(); return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]; }
        var (start, here, before) = (Number(own) & mask, Number(own), Number(old));
        if ((before & mask) != start) return [];
        var last = start | ~mask;
        var list = new List<string>();
        for (var n = start + 1; n < last; n++)
        {
            if (n == here || n == before) continue;
            var host = new IPAddress([(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n]);
            list.Add(new UriBuilder(uri) { Host = host.ToString() }.Uri.AbsoluteUri);
        }
        return list;
    }

    /// <summary>The account at its server's new address; what names its data on this computer stays the address signed in at.</summary>
    public static Connection Moved(Connection connection, string address) =>
        connection with { Server = JellyfinClient.NormalizeServer(address), Origin = connection.Origin ?? connection.Server };

    /// <summary>
    /// Whether Jellyfin may be looked for elsewhere once silent: at another address of the home network
    /// (<see cref="Movable"/>), or through Tailscale away from home: its id known, signed in at an address of the home
    /// network (or reached through Tailscale for now, <see cref="Connection.Home"/>), never a party's guest.
    /// </summary>
    public static bool Searchable(Connection connection) => Movable(connection)
        || connection.ServerId is { Length: > 0 } && !Together.IsGuest(connection) && HomeAddress(connection.Home ?? connection.Server) is not null;

    /// <summary>
    /// The account through Jellyfin's Tailscale address while its home address is out of reach (away from home, on the
    /// Tailscale app or « Tailscale intégré »): the home address kept to come back to (<see cref="Connection.Home"/>), and
    /// what names its data on this computer unchanged.
    /// </summary>
    public static Connection Away(Connection connection, string address) => connection with
    {
        Server = JellyfinClient.NormalizeServer(address),
        Origin = connection.Origin ?? connection.Server,
        Home = connection.Home ?? connection.Server,
    };
    /// <summary>The account at its home address again (full quality), back from Tailscale.</summary>
    public static Connection Back(Connection connection, string address) => connection with
    {
        Server = JellyfinClient.NormalizeServer(address),
        Home = null,
        Origin = string.Equals(JellyfinClient.NormalizeServer(address), connection.Origin, StringComparison.OrdinalIgnoreCase) ? null : connection.Origin,
    };
    /// <summary>Where Jellyfin's Tailscale address is among the servers found: a device of the tailnet with this server's id.</summary>
    public static string? ThroughTailnet(Connection connection, IEnumerable<ServerSearch.Found> found) => found
        .Where(x => x.Where == ServerSearch.Place.Tailnet && SameId(x.Id, connection.ServerId)
            && !string.Equals(JellyfinClient.NormalizeServer(x.Address), JellyfinClient.NormalizeServer(connection.Server), StringComparison.OrdinalIgnoreCase))
        .Select(x => x.Address).FirstOrDefault();

    /// <summary>
    /// Looks for this account's server elsewhere (null: not found, or not <see cref="Searchable"/>), in this order: back
    /// at its home address after a time through Tailscale; on this computer and by Jellyfin's discovery
    /// (<paramref name="home"/> replaces it in the tests); at each address of the networks this computer shares with the
    /// old one (<paramref name="own"/>: this computer's, read from its network adapters when not given); then, away from
    /// home, on <paramref name="tailnet"/>'s devices (the Tailscale app's or « Tailscale intégré »'s, 8096). Each address
    /// found is asked again for this server's id, and on the home network for its proof (<see cref="ServerProof"/>),
    /// before it is taken. Through Tailscale, its proof too when the account has its key.
    /// </summary>
    public static async Task<Connection?> FindAsync(Connection connection, HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<IReadOnlyList<ServerSearch.Found>>>? home = null, IReadOnlyList<(IPAddress Address, int Prefix)>? own = null,
        IReadOnlyList<string>? tailnet = null, CancellationToken ct = default)
    {
        if (!Searchable(connection)) return null;
        using var http = new HttpClient(handler ?? TailnetNetwork.Default.Handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        // Back home: the home address answers again (the one signed in at: its id, and its proof when the key is known).
        if (connection.Home is { } homeAddress && await AnswersAsync(http, homeAddress, connection, ct, proofNeeded: false).ConfigureAwait(false) is { } back)
            return Back(connection, back);
        var found = await ServerSearch.FindAsync(tailnet ?? [], handler, home, ct, ServerSearch.TailnetWait).ConfigureAwait(false);
        if (Movable(connection))
        {
            if (Found(connection, found) is { } address && await AnswersAsync(http, address, connection, ct).ConfigureAwait(false) is { } confirmed)
                return Moved(connection, confirmed);
            if (await NeighbourAsync(http, connection, own, ct).ConfigureAwait(false) is { } neighbour) return neighbour;
        }
        // Away from home: through Tailscale, as the iPhone goes (Way). Never back to the same address.
        if (ThroughTailnet(connection, found) is { } device && await AnswersAsync(http, device, connection, ct, proofNeeded: false, ServerSearch.TailnetWait).ConfigureAwait(false) is { } reached)
            return Away(connection, reached);
        return null;
    }
    /// <summary>
    /// Through Tailscale for now (<see cref="Connection.Home"/>): the account at its home address again when it answers
    /// (back home, on another network, Mira opened at home); null otherwise. Asked on each change of network.
    /// </summary>
    public static async Task<Connection?> HomeAgainAsync(Connection connection, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (connection.Home is not { } address || connection.ServerId is not { Length: > 0 }) return null;
        using var http = new HttpClient(handler ?? TailnetNetwork.Default.Handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        return await AnswersAsync(http, address, connection, ct, proofNeeded: false).ConfigureAwait(false) is { } back ? Back(connection, back) : null;
    }
    /// <summary>Discovery turned off on the server, or Jellyfin in a container that does not hear it: each address of the network.</summary>
    private static async Task<Connection?> NeighbourAsync(HttpClient http, Connection connection, IReadOnlyList<(IPAddress Address, int Prefix)>? own, CancellationToken ct)
    {
        var neighbours = (own ?? OwnNetworks()).SelectMany(x => Neighbours(connection.Server, x.Address, x.Prefix)).Distinct().ToList();
        if (neighbours.Count == 0) return null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asked = neighbours.Select(x => AnswersAsync(http, x, connection, stop.Token)).ToList();
        try
        {
            while (asked.Count > 0)
            {
                var done = await Task.WhenAny(asked).ConfigureAwait(false);
                asked.Remove(done);
                if (await done.ConfigureAwait(false) is { } answered && Allowed(connection.Server, answered)) return Moved(connection, answered);
            }
        }
        finally { stop.Cancel(); }
        ct.ThrowIfCancellationRequested();
        return null;
    }
    /// <summary>The address, as it answered, when Jellyfin there is this server and proves it; null otherwise.</summary>
    /// <param name="proofNeeded">
    /// False for the address signed in at, and a device of the tailnet (the Tailscale network authenticates it): its
    /// proof asked only when the account has the key.
    /// </param>
    /// <param name="wait">How long it has to answer: <see cref="Wait"/>, longer through Tailscale (<see cref="ServerSearch.TailnetWait"/>).</param>
    private static async Task<string?> AnswersAsync(HttpClient http, string address, Connection connection, CancellationToken ct, bool proofNeeded = true, TimeSpan? wait = null)
    {
        var probe = await ServerAddress.ProbeAsync(http, address, ct, wait ?? Wait).ConfigureAwait(false);
        if (probe.Info is not { } info || !SameId(info.Id, connection.ServerId)) return null;
        if (!proofNeeded && connection.ServerProof is not { Length: > 0 }) return info.Address;
        return await ProvesAsync(http, info.Address, connection, ct, wait).ConfigureAwait(false) ? info.Address : null;
    }
    /// <summary>
    /// Whether Jellyfin at <paramref name="address"/> signs a new nonce with this account's proof key, asked without a
    /// session (<see cref="ServerProof"/>): only the server signed in to can.
    /// </summary>
    internal static async Task<bool> ProvesAsync(HttpClient http, string address, Connection connection, CancellationToken ct, TimeSpan? wait = null)
    {
        if (ServerProof.PlainId(connection.UserId) is not { } user || !Uri.TryCreate(address, UriKind.Absolute, out var uri)) return false;
        var nonce = ServerProof.NewNonce();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? Wait);
        try
        {
            using var response = await http.GetAsync(new Uri(uri, $"Mira/proof?user={user}&nonce={nonce}"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4096) return false;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), default, timeout.Token).ConfigureAwait(false);
            var proof = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("proof", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            // Signed for the address asked: a device that passed the question on to the server by another way is caught.
            return ServerProof.Holds(connection.ServerProof, nonce, uri.Authority, proof);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { ct.ThrowIfCancellationRequested(); return false; }
    }
    /// <summary>This computer's IPv4 networks at home: up, neither loopback nor a tunnel nor Tailscale.</summary>
    private static IReadOnlyList<(IPAddress Address, int Prefix)> OwnNetworks()
    {
        var list = new List<(IPAddress, int)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && HomeAddress($"http://{unicast.Address}/") is not null) list.Add((unicast.Address, unicast.PrefixLength));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException) { }
        return list;
    }

    /// <summary>Said once Mira goes through Tailscale: why videos may come in a lower quality, which nothing else shows.</summary>
    public static string SaidAway => L("Jellyfin ne répond pas à son adresse de la maison : Mira passe par Tailscale. Il reprendra l’adresse de la maison dès qu’elle répondra.",
        "Jellyfin isn’t answering at its home address: Mira goes through Tailscale, and will go back to the home address as soon as it answers.");

    /// <summary>Said once Mira moved to the new address: what Jellyfin did, which nothing else on screen shows.</summary>
    public static string Said(string from, string to)
    {
        static string Host(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
        return L($"Ta box a donné une autre adresse au PC de Jellyfin ({Host(from)} → {Host(to)}) : Mira l’a retrouvé tout seul.",
            $"Your router gave Jellyfin’s PC another address ({Host(from)} → {Host(to)}): Mira found it again on its own.");
    }
}
