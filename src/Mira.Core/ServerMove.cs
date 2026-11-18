using System.Net;
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
/// Jellyfin's public information, as the login screen asks it; the token goes only to the address that answered with
/// this server's id. The account keeps its data on this computer (<see cref="Connection.Account"/>). The iPhone does
/// the same in its way to Jellyfin (Way, WayPlan.neighbours), on the cases of tests/fixtures/server-move.json.
/// </summary>
public static class ServerMove
{
    /// <summary>Looked for at most this often while Jellyfin stays silent (each refresh that fails tries again).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    /// <summary>How long each address of the network has to answer.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Whether this account's Jellyfin may have moved: its id known, at an IPv4 address of the home network (10.x,
    /// 172.16–31.x, 192.168.x), never this computer, Tailscale, a name, nor a party's guest account.
    /// </summary>
    public static bool Movable(Connection connection) => connection.ServerId is { Length: > 0 } && !Together.IsGuest(connection) && HomeAddress(connection.Server) is not null;

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
    /// Looks for this account's server elsewhere on the home network (null: not found, or not <see cref="Movable"/>):
    /// on this computer and by Jellyfin's discovery (<paramref name="home"/> replaces it in the tests), then at each
    /// address of the networks this computer shares with the old one (<paramref name="own"/>: this computer's, read from
    /// its network adapters when not given). Each address found is asked again for this server's id before it is taken.
    /// </summary>
    public static async Task<Connection?> FindAsync(Connection connection, HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<IReadOnlyList<ServerSearch.Found>>>? home = null, IReadOnlyList<(IPAddress Address, int Prefix)>? own = null, CancellationToken ct = default)
    {
        if (!Movable(connection)) return null;
        using var http = new HttpClient(handler ?? TailnetNetwork.Default.Handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var found = await ServerSearch.FindAsync([], handler, home, ct).ConfigureAwait(false);
        if (Found(connection, found) is { } address && await AnswersAsync(http, address, connection.ServerId!, ct).ConfigureAwait(false) is { } confirmed)
            return Moved(connection, confirmed);
        // Discovery turned off on the server, or Jellyfin in a container that does not hear it: each address of the network.
        var neighbours = (own ?? OwnNetworks()).SelectMany(x => Neighbours(connection.Server, x.Address, x.Prefix)).Distinct().ToList();
        if (neighbours.Count == 0) return null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var asked = neighbours.Select(x => AnswersAsync(http, x, connection.ServerId!, stop.Token)).ToList();
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
    /// <summary>The address, as it answered, when Jellyfin there is this server; null otherwise.</summary>
    private static async Task<string?> AnswersAsync(HttpClient http, string address, string id, CancellationToken ct)
    {
        var probe = await ServerAddress.ProbeAsync(http, address, ct, Wait).ConfigureAwait(false);
        return probe.Info is { } info && SameId(info.Id, id) ? info.Address : null;
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

    /// <summary>Said once Mira moved to the new address: what Jellyfin did, which nothing else on screen shows.</summary>
    public static string Said(string from, string to)
    {
        static string Host(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
        return L($"Ta box a donné une autre adresse au PC de Jellyfin ({Host(from)} → {Host(to)}) : Mira l’a retrouvé tout seul.",
            $"Your router gave Jellyfin’s PC another address ({Host(from)} → {Host(to)}): Mira found it again on its own.");
    }
}
