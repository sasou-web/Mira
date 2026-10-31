namespace Mira.Core.Tailscale;

/// <summary>
/// Where Mira's own Tailscale node is, as mira-tailscale reports it (tailnet.Status of src/Mira.Tailscale, read from its
/// JSON as it is).
/// </summary>
public sealed record TailnetStatus
{
    /// <summary>One of <see cref="TailnetStates"/>.</summary>
    public string State { get; init; } = TailnetStates.Starting;
    /// <summary>The page that logs the node in, while it needs one.</summary>
    public string? AuthUrl { get; init; }
    /// <summary>While running, whether this device cannot reach the tailnet: no network, or no Tailscale relay.</summary>
    public bool Offline { get; init; }
    public TailnetDevice? Self { get; init; }
    /// <summary>The account the node is logged in with (its login name).</summary>
    public string? User { get; init; }
    /// <summary>The tailnet's name.</summary>
    public string? Tailnet { get; init; }
    public string? MagicDnsSuffix { get; init; }
    public IReadOnlyList<TailnetDevice> Peers { get; init; } = [];
    public IReadOnlyList<TailnetWarning> Warnings { get; init; } = [];
    /// <summary>Whether the tailnet's policy lets the node keep its network map, to start from it during an outage of Tailscale.</summary>
    public bool OutageCache { get; init; }
    /// <summary>The device a server address leads to: one of its Tailscale IPs, its MagicDNS name or its short name.</summary>
    public TailnetDevice? Peer(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        return Peers.FirstOrDefault(x => x.Ips.Contains(host, StringComparer.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(x.DnsName) && (host.Equals(x.DnsName, StringComparison.OrdinalIgnoreCase) || host.Equals(x.DnsName.Split('.')[0], StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>The node's states (<see cref="TailnetStatus.State"/>).</summary>
public static class TailnetStates
{
    /// <summary>Connecting to Tailscale, or again after being stopped.</summary>
    public const string Starting = "starting";
    /// <summary>Never logged in, or logged out: <see cref="TailnetProcess.LoginAsync"/> gives the page.</summary>
    public const string NeedsLogin = "needs-login";
    /// <summary>Its key expired (180 days by default): it logs in again.</summary>
    public const string Expired = "expired";
    /// <summary>The tailnet's admin must approve this new device in Tailscale's admin console.</summary>
    public const string MachineAuth = "machine-auth";
    /// <summary>On the tailnet.</summary>
    public const string Running = "running";
    /// <summary>Stopped.</summary>
    public const string Stopped = "stopped";
}

/// <summary>A device of the tailnet.</summary>
public sealed record TailnetDevice
{
    public string Id { get; init; } = "";
    public string HostName { get; init; } = "";
    /// <summary>Its MagicDNS name, without the final dot (« desktop-abc.tail1234.ts.net »).</summary>
    public string? DnsName { get; init; }
    /// <summary>Its Tailscale addresses, IPv4 first.</summary>
    public IReadOnlyList<string> Ips { get; init; } = [];
    public string? Os { get; init; }
    /// <summary>Whether it is connected to Tailscale.</summary>
    public bool Online { get; init; }
    public DateTimeOffset? KeyExpiry { get; init; }
    /// <summary>When it was last connected, while it is not.</summary>
    public DateTimeOffset? LastSeen { get; init; }
}

/// <summary>One of the node's health problems, in Tailscale's words (English).</summary>
public sealed record TailnetWarning
{
    /// <summary>Tailscale's code (« network-status », « no-derp-home »…).</summary>
    public string Code { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Whether it keeps the node from reaching the other devices.</summary>
    public bool Connectivity { get; init; }
}

/// <summary>
/// The node's proxy on this computer, behind its own user name and password, both drawn anew at each launch of the
/// helper: SOCKS5 for .NET, HTTP proxying for mpv.
/// </summary>
public sealed record TailnetProxy(string Address, string User, string Password)
{
    /// <summary>The Proxy-Authorization header of a request through the HTTP proxy (mpv sends it from the start).</summary>
    public string Authorization => "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(User + ":" + Password));
}
