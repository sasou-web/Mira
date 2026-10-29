using System.Net;
using System.Net.Sockets;

namespace Mira.Core.Tailscale;

/// <summary>How a connection goes: as before Mira had its own node, through the node, or not at all.</summary>
public enum TailnetPath
{
    /// <summary>The system's network: the home network, the Internet, the Tailscale app when it runs.</summary>
    Direct,
    /// <summary>Mira's own Tailscale node (its proxy on this computer).</summary>
    Node,
    /// <summary>A device of the tailnet that nothing can reach now: <see cref="TailnetChoice.Code"/> says why.</summary>
    Fail,
}

/// <summary>The way to a host, and the reason (<see cref="TailnetCodes"/>) when there is none.</summary>
public readonly record struct TailnetChoice(TailnetPath Path, string? Code = null)
{
    public static TailnetChoice Direct { get; } = new(TailnetPath.Direct);
    public static TailnetChoice Node { get; } = new(TailnetPath.Node);
    public static TailnetChoice Fail(string code) => new(TailnetPath.Fail, code);
}

/// <summary>
/// Which way each connection to a server goes, decided again for each one from what is known at that moment (the
/// node's state and devices, this computer's addresses): only a device of the tailnet ever goes through Mira's node,
/// never this computer, the home network or the Internet; with the node off, nothing changes from before. Cases shared
/// with the iPhone app: tests/fixtures/tailnet.json.
/// </summary>
public static class TailnetRoute
{
    /// <summary>Tailscale's addresses: 100.64.0.0/10 for IPv4, fd7a:115c:a1e0::/48 for IPv6.</summary>
    public static bool InRange(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes is [100, >= 64 and < 128, _, _]
            : bytes is [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..];
    }

    /// <summary>A host as an address names it, without brackets or final dot; an address written as Tailscale writes it (IPv4 as IPv4).</summary>
    public static string Clean(string host)
    {
        host = host.Trim().Trim('[', ']').TrimEnd('.');
        if (!IPAddress.TryParse(host, out var ip)) return host;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>
    /// Whether a host is a device of a tailnet: one of Tailscale's addresses, a MagicDNS name (« pc.tail1234.ts.net »,
    /// or under the node's own suffix), or the name of one of the node's devices (« desktop-invefqh »).
    /// </summary>
    public static bool IsTailnet(string host, TailnetStatus? node = null)
    {
        host = Clean(host);
        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host, out var ip)) return InRange(ip) || node?.Peer(host) is not null || node?.Self is { } self && Names(self, host);
        return host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)
            || node?.MagicDnsSuffix is { Length: > 0 } suffix && host.EndsWith("." + suffix.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || node?.Peer(host) is not null || node?.Self is { } me && Names(me, host);
    }

    /// <summary>Whether a host names this device: one of its addresses, its MagicDNS name or its short name.</summary>
    public static bool Names(TailnetDevice device, string host)
    {
        host = Clean(host);
        if (IPAddress.TryParse(host, out var ip)) return device.Ips.Any(x => IPAddress.TryParse(x, out var own) && Same(own, ip));
        return !string.IsNullOrEmpty(device.DnsName) && (host.Equals(device.DnsName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || host.Equals(device.DnsName.Split('.')[0], StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The way to <paramref name="host"/>. <paramref name="node"/>: the node's status, null while its helper does not
    /// run; <paramref name="nodeOn"/>: whether the person turned the node on; <paramref name="proxy"/>: whether its proxy
    /// is there; <paramref name="local"/>: this computer's addresses, the Tailscale app's among them when it is
    /// connected; <paramref name="withoutNode"/>: the reason when no node says one, from its helper (starting, or the
    /// code of its failure); <paramref name="known"/>: the node's last status, whose devices' names still count while
    /// it starts again; <paramref name="computer"/>: this computer's name (a Jellyfin here is never reached through the node).
    /// </summary>
    public static TailnetChoice Choose(string host, bool nodeOn, TailnetStatus? node, bool proxy, IReadOnlyCollection<IPAddress> local,
        string? withoutNode = null, TailnetStatus? known = null, string? computer = null)
    {
        var names = node ?? known;
        host = Clean(host);
        // This computer: never through the node.
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return TailnetChoice.Direct;
        if (!string.IsNullOrEmpty(computer) && host.Split('.')[0].Equals(computer.Split('.')[0], StringComparison.OrdinalIgnoreCase)) return TailnetChoice.Direct;
        var literal = IPAddress.TryParse(host, out var ip) ? ip : null;
        if (literal is not null && (IPAddress.IsLoopback(literal) || local.Any(x => Same(x, literal)))) return TailnetChoice.Direct;
        if (!IsTailnet(host, names)) return TailnetChoice.Direct;
        var device = names?.Peer(host);
        // A device whose address is this computer's own: the Tailscale app of this very computer.
        if (device is not null && device.Ips.Any(x => IPAddress.TryParse(x, out var own) && local.Any(y => Same(y, own)))) return TailnetChoice.Direct;
        if (!nodeOn) return TailnetChoice.Direct;
        // The node itself: nothing of Mira listens there.
        if (names?.Self is { } self && Names(self, host)) return TailnetChoice.Fail(TailnetCodes.Refused);
        // The Tailscale app connected on this computer, maybe to another tailnet: it takes what the node does not know.
        var app = local.Any(InRange);
        if (node?.State == TailnetStates.Running && proxy) return device is not null || !app ? TailnetChoice.Node : TailnetChoice.Direct;
        if (app) return TailnetChoice.Direct;
        return TailnetChoice.Fail(node?.State switch
        {
            TailnetStates.NeedsLogin => TailnetCodes.NeedsLogin,
            TailnetStates.Expired => TailnetCodes.Expired,
            TailnetStates.MachineAuth => TailnetCodes.MachineAuth,
            TailnetStates.Starting => TailnetCodes.Starting,
            TailnetStates.Stopped => TailnetCodes.Stopped,
            // Its proxy gone with it: the helper ended.
            TailnetStates.Running => TailnetCodes.Helper,
            _ => withoutNode ?? TailnetCodes.Helper,
        });
    }

    private static bool Same(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (b.IsIPv4MappedToIPv6) b = b.MapToIPv4();
        return a.Equals(b) || a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());
    }
}
