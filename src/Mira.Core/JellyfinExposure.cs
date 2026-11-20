using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mira.Core;

/// <summary>A device Jellyfin saw lately from an address it would turn away once closed to the Internet.</summary>
public sealed record OutsideDevice(string Id, string Name, string App, string? UserName, string Address, DateTimeOffset? LastActive);

/// <summary>
/// Jellyfin open to the whole Internet (« Your server » of « Security », ogsas 2026-11-20, maquette A): its access from
/// elsewhere is on and no list of allowed addresses narrows it, so any address outside its local networks gets in and may
/// try passwords. Mira needs only the home network and Tailscale (counted local by <see cref="LocalNetwork.WithTailnet"/>);
/// a party's link reaches Jellyfin through Tailscale Funnel, from this PC itself. <see cref="Close"/> turns that access
/// off and counts Tailscale's range as local: applied at once, without restarting Jellyfin.
/// </summary>
public static class JellyfinExposure
{
    /// <summary>Whether Jellyfin's network settings (System/Configuration/network) let the whole Internet in.</summary>
    public static bool OpenToInternet(JsonObject network) => Flag(network, "EnableRemoteAccess") && !Narrowed(network);

    /// <summary>
    /// A list of allowed addresses (RemoteIPFilter, not a block list) that names some and not every address: someone
    /// chose who comes from outside, which Mira leaves alone.
    /// </summary>
    private static bool Narrowed(JsonObject network)
    {
        if (Flag(network, "IsRemoteIPFilterBlacklist")) return false;
        var allowed = Texts(network, "RemoteIPFilter");
        return allowed.Count > 0 && !allowed.Any(x => x.Trim() is "0.0.0.0/0" or "::/0" or "*" or "0.0.0.0" or "::");
    }

    /// <summary>The settings closed: access from elsewhere off, Tailscale's range local, every other setting kept as it was.</summary>
    public static JsonObject Close(JsonObject network)
    {
        var closed = (JsonObject)network.DeepClone();
        closed["EnableRemoteAccess"] = false;
        closed["LocalNetworkSubnets"] = new JsonArray(LocalNetwork.WithTailnet(Texts(network, "LocalNetworkSubnets")).Select(x => (JsonNode?)x).ToArray());
        return closed;
    }

    /// <summary>
    /// The networks Jellyfin counts as local once closed (<see cref="Close"/>): its own, or its defaults, with Tailscale's.
    /// </summary>
    public static IReadOnlyList<string> LocalOnceClosed(JsonObject network) => LocalNetwork.WithTailnet(Texts(network, "LocalNetworkSubnets"));

    /// <summary>
    /// Whether an address Jellyfin saw (a session's RemoteEndPoint: an address, sometimes with its port) is in
    /// <paramref name="subnets"/> (networks as Jellyfin writes them, or single addresses). This computer is always local.
    /// An address Mira cannot read counts as local: it is never said to be cut on a guess.
    /// </summary>
    public static bool IsLocal(string? endpoint, IReadOnlyList<string> subnets)
    {
        if (Address(endpoint) is not { } address) return true;
        if (IPAddress.IsLoopback(address)) return true;
        foreach (var subnet in subnets)
            if (Covers(subnet, address)) return true;
        return false;
    }

    /// <summary>
    /// The devices Jellyfin saw lately (GET Sessions) from outside <paramref name="subnets"/>, the most recent first, one
    /// line each; <paramref name="except"/> (this device) left out.
    /// </summary>
    public static IReadOnlyList<OutsideDevice> Outside(JsonElement sessions, IReadOnlyList<string> subnets, string? except = null)
    {
        var found = new Dictionary<string, OutsideDevice>(StringComparer.Ordinal);
        if (sessions.ValueKind != JsonValueKind.Array) return [];
        foreach (var session in sessions.EnumerateArray())
        {
            if (Text(session, "DeviceId") is not { } id || id == except) continue;
            var endpoint = Text(session, "RemoteEndPoint");
            if (Address(endpoint) is null || IsLocal(endpoint, subnets)) continue;
            DateTimeOffset? last = session.TryGetProperty("LastActivityDate", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null;
            var device = new OutsideDevice(id, Text(session, "DeviceName") ?? id, Text(session, "Client") ?? "", Text(session, "UserName"), Address(endpoint)!.ToString(), last);
            if (!found.TryGetValue(id, out var seen) || (seen.LastActive ?? DateTimeOffset.MinValue) < (last ?? DateTimeOffset.MinValue)) found[id] = device;
        }
        return [.. found.Values.OrderByDescending(x => x.LastActive ?? DateTimeOffset.MinValue)];
    }

    /// <summary>The address a session came from, as Jellyfin writes it: « 203.0.113.9 », « ::ffff:203.0.113.9 », « [2001:db8::1]:51000 ».</summary>
    public static IPAddress? Address(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var text = endpoint.Trim();
        IPAddress? address = IPAddress.TryParse(text, out var plain) ? plain : IPEndPoint.TryParse(text, out var withPort) ? withPort.Address : null;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// Whether a network as Jellyfin writes it (« 192.168.0.0/16 », « fc00::/7 », or one address, with or without
    /// « /32 ») holds <paramref name="address"/>; host bits written past the prefix are ignored, as Jellyfin does.
    /// </summary>
    public static bool Covers(string subnet, IPAddress address)
    {
        var parts = subnet.Trim().Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var network)) return false;
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        if (network.AddressFamily != address.AddressFamily) return false;
        var (a, b) = (network.GetAddressBytes(), address.GetAddressBytes());
        var bits = a.Length * 8;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > a.Length * 8)) return false;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : 0xFF << (8 - bits) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
        }
        return true;
    }

    private static bool Flag(JsonObject network, string name) => network[name] is JsonValue value && value.TryGetValue<bool>(out var on) && on;
    private static List<string> Texts(JsonObject network, string name) =>
        network[name] is JsonArray list ? list.Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().Where(x => x.Trim().Length > 0).ToList() : [];
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}
