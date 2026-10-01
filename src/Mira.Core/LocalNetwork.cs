using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>The address phones, TVs and other PCs on the home network use to reach a Jellyfin running on this PC.</summary>
public static partial class LocalNetwork
{
    public sealed record Candidate(string Address, bool HasGateway, bool Virtual);
    /// <summary>A real adapter with a gateway (the box) first; never loopback or a self-assigned 169.254 address.</summary>
    public static string? PreferredIPv4(IEnumerable<Candidate> candidates) => candidates
        .Where(x => IPAddress.TryParse(x.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !x.Address.StartsWith("169.254.", StringComparison.Ordinal))
        .OrderBy(x => x.Virtual).ThenByDescending(x => x.HasGateway).ThenByDescending(x => IsPrivate(x.Address))
        .Select(x => x.Address).FirstOrDefault();
    /// <summary>
    /// The server address to give other devices: a server on this PC (127.0.0.1, localhost) answers them at this PC's
    /// network address, on the same port; any other server keeps its own address. Null when this PC has none.
    /// </summary>
    public static string? ForOtherDevices(string server, string? thisPc)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)) return null;
        if (!uri.IsLoopback) return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/');
        return thisPc is null ? null : new UriBuilder(uri) { Host = thisPc }.Uri.GetLeftPart(UriPartial.Authority);
    }
    /// <summary>This PC's address on the home network, read from its network adapters.</summary>
    public static string? ThisPc()
    {
        try
        {
            return PreferredIPv4(
                from nic in NetworkInterface.GetAllNetworkInterfaces()
                where nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                let properties = nic.GetIPProperties()
                let gateway = properties.GatewayAddresses.Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.Equals(IPAddress.Any))
                from address in properties.UnicastAddresses
                where address.Address.AddressFamily == AddressFamily.InterNetwork
                select new Candidate(address.Address.ToString(), gateway, VirtualAdapter().IsMatch(nic.Name + " " + nic.Description)));
        }
        catch (NetworkInformationException) { return null; }
    }
    private static bool IsPrivate(string address) => address.StartsWith("192.168.", StringComparison.Ordinal) || address.StartsWith("10.", StringComparison.Ordinal)
        || Regex.IsMatch(address, @"^172\.(1[6-9]|2\d|3[01])\.");
    [GeneratedRegex("virtual|hyper-v|vethernet|vmware|virtualbox|wsl|docker|tailscale|zerotier|wireguard|vpn|tap-", RegexOptions.IgnoreCase)] private static partial Regex VirtualAdapter();
}
