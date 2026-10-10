using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>The address phones, TVs and other PCs on the home network use to reach a Jellyfin running on this PC.</summary>
public static partial class LocalNetwork
{
    /// <summary>An IPv4 address of this PC, with its adapter's name (« Wi-Fi », « Ethernet »…).</summary>
    public sealed record Candidate(string Address, bool HasGateway, bool Virtual, string Adapter = "");
    /// <summary>A real adapter with a gateway (the box) first; never loopback or a self-assigned 169.254 address.</summary>
    public static string? PreferredIPv4(IEnumerable<Candidate> candidates) => Preferred(candidates)?.Address;
    public static Candidate? Preferred(IEnumerable<Candidate> candidates) => candidates
        .Where(x => IPAddress.TryParse(x.Address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !x.Address.StartsWith("169.254.", StringComparison.Ordinal))
        .OrderBy(x => x.Virtual).ThenByDescending(x => x.HasGateway).ThenByDescending(x => IsPrivate(x.Address))
        .FirstOrDefault();
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
    /// <summary>
    /// Whether a server's host is this PC: loopback, its name, or one of its own addresses (a Jellyfin here that Mira
    /// reaches at this PC's network address).
    /// </summary>
    public static bool IsThisPc(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            if (!IPAddress.TryParse(host, out var ip)) return host.Equals(Dns.GetHostName(), StringComparison.OrdinalIgnoreCase);
            return IPAddress.IsLoopback(ip) || NetworkInterface.GetAllNetworkInterfaces().SelectMany(nic => nic.GetIPProperties().UnicastAddresses).Any(x => x.Address.Equals(ip));
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException) { return false; }
    }
    /// <summary>This PC's address on the home network, read from its network adapters.</summary>
    public static string? ThisPc() => ThisPcOnNetwork()?.Address;
    /// <summary>This PC's address on the home network and the adapter that has it.</summary>
    public static Candidate? ThisPcOnNetwork()
    {
        try
        {
            return Preferred(
                from nic in NetworkInterface.GetAllNetworkInterfaces()
                where nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                let properties = nic.GetIPProperties()
                let gateway = properties.GatewayAddresses.Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.Equals(IPAddress.Any))
                from address in properties.UnicastAddresses
                where address.Address.AddressFamily == AddressFamily.InterNetwork
                select new Candidate(address.Address.ToString(), gateway, VirtualAdapter().IsMatch(nic.Name + " " + nic.Description), nic.Name));
        }
        catch (NetworkInformationException) { return null; }
    }
    /// <summary>
    /// Tailscale's private network: each device gets an address from 100.64.0.0/10. Jellyfin counts only the home
    /// network ranges as local, and Mira's setup leaves its access from elsewhere off: this range has to be added.
    /// </summary>
    public const string TailnetRange = "100.64.0.0/10";
    /// <summary>What Jellyfin counts as local when it is given no list: loopback and the private ranges (IPv4 and IPv6).</summary>
    public static readonly IReadOnlyList<string> JellyfinDefaultSubnets = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7", "fe80::/10"];
    public static bool IsTailnet(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [100, var second, _, _] && second is >= 64 and < 128;
    /// <summary>This PC's Tailscale address among its adapters: a 100.64.0.0/10 address on an adapter named Tailscale.</summary>
    public static string? TailnetIPv4(IEnumerable<(string Address, string Adapter)> addresses) =>
        addresses.Where(x => IsTailnet(x.Address) && x.Adapter.Contains("tailscale", StringComparison.OrdinalIgnoreCase)).Select(x => x.Address).FirstOrDefault();
    /// <summary>This PC's address on Tailscale, when Tailscale is installed and connected; null otherwise.</summary>
    public static string? ThisPcOnTailnet()
    {
        try
        {
            return TailnetIPv4(
                from nic in NetworkInterface.GetAllNetworkInterfaces()
                where nic.OperationalStatus == OperationalStatus.Up
                from address in nic.GetIPProperties().UnicastAddresses
                select (address.Address.ToString(), nic.Name + " " + nic.Description));
        }
        catch (NetworkInformationException) { return null; }
    }
    /// <summary>
    /// Jellyfin's local networks with Tailscale's added: an empty list stands for Jellyfin's defaults, which are kept,
    /// so the home network still counts. Access from the Internet stays off.
    /// </summary>
    public static List<string> WithTailnet(IReadOnlyList<string> subnets)
    {
        var list = (subnets.Count == 0 ? JellyfinDefaultSubnets : subnets).ToList();
        if (!list.Contains(TailnetRange, StringComparer.OrdinalIgnoreCase)) list.Add(TailnetRange);
        return list;
    }
    private static bool IsPrivate(string address) => address.StartsWith("192.168.", StringComparison.Ordinal) || address.StartsWith("10.", StringComparison.Ordinal)
        || Regex.IsMatch(address, @"^172\.(1[6-9]|2\d|3[01])\.");
    [GeneratedRegex("virtual|hyper-v|vethernet|vmware|virtualbox|wsl|docker|tailscale|zerotier|wireguard|vpn|tap-", RegexOptions.IgnoreCase)] private static partial Regex VirtualAdapter();
}
