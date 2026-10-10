using System.Net;
using System.Net.Sockets;

namespace Mira.Core;

/// <summary>
/// Whether Windows' firewall lets a device on Tailscale reach Jellyfin on this PC, judged from its rules the way
/// Windows applies them: a matching block rule wins, then a matching allow rule (or a profile that allows by default)
/// lets the connection in. A Jellyfin started without its service gets only the rules of Windows' « Autoriser
/// l'accès » prompt, made at its first start: allow on the kind of network ticked (often Public, for the box), block
/// on the others. Tailscale marks its adapter Private (wgengine/router/osrouter/ifconfig_windows.go), so a box counted
/// as Public lets the home network in and keeps Tailscale out.
/// </summary>
public static class JellyfinFirewall
{
    /// <summary>Windows' network profiles (NET_FW_PROFILE_TYPE2); a rule for all of them carries every bit.</summary>
    public const int Domain = 1, Private = 2, Public = 4, AllProfiles = Domain | Private | Public;
    public const int Tcp = 6, AnyProtocol = 256;

    /// <summary>A firewall rule as Windows describes it (INetFwRule); null or empty program, ports or addresses mean any.</summary>
    public sealed record Rule(string Name, bool Enabled, bool Inbound, bool Allow, int Profiles, string? Program, int Protocol, string? LocalPorts, string? RemoteAddresses,
        IReadOnlyList<string>? Interfaces = null, string? InterfaceTypes = "All");
    /// <summary>The firewall on one profile: on or off, its default for incoming connections, « block all incoming ».</summary>
    public sealed record Profile(bool Enabled, bool DefaultAllow, bool BlockAll);
    /// <summary>Open: Tailscale's devices get through. Blocked: rules Mira may change keep them out. Closed: the whole profile refuses them.</summary>
    public enum Verdict { Open, Blocked, Closed }

    /// <summary>
    /// The profile of Tailscale's adapter, from the profiles active on this PC (CurrentProfileTypes): Tailscale marks
    /// it Private, so a PC with no private network active is one where Tailscale could not.
    /// </summary>
    public static int TailnetProfile(int active) =>
        (active & Private) != 0 || (active & (Domain | Public)) == 0 ? Private : (active & Public) != 0 ? Public : Domain;

    /// <summary>Whether a TCP connection from a Tailscale device to <paramref name="program"/> on <paramref name="port"/> gets in.</summary>
    public static Verdict Judge(IEnumerable<Rule> rules, Profile settings, string program, int port, int profile, string? adapter = null)
    {
        if (!settings.Enabled) return Verdict.Open;
        if (settings.BlockAll) return Verdict.Closed;
        var matching = rules.Where(x => Applies(x, program, port, profile, adapter)).ToList();
        if (matching.Any(x => !x.Allow)) return Verdict.Blocked;
        return settings.DefaultAllow || matching.Count > 0 ? Verdict.Open : Verdict.Blocked;
    }

    /// <summary>The block rules that keep Tailscale's devices out of <paramref name="program"/> on <paramref name="port"/>.</summary>
    public static IReadOnlyList<Rule> Blocking(IEnumerable<Rule> rules, string program, int port, int profile, string? adapter = null) =>
        rules.Where(x => !x.Allow && Applies(x, program, port, profile, adapter)).ToList();

    /// <summary>
    /// The profiles a block rule keeps once it no longer applies to Tailscale's: 0 when it covered that profile only,
    /// and is then turned off. A rule for every network keeps blocking the others.
    /// </summary>
    public static int WithoutProfile(int profiles, int profile) => profiles & AllProfiles & ~profile;

    /// <summary>
    /// Whether a rule applies to a TCP connection from Tailscale to <paramref name="program"/> on <paramref name="port"/>,
    /// arriving on Tailscale's adapter (<paramref name="adapter"/>, its name) under <paramref name="profile"/>. A rule
    /// for only part of Tailscale's range, or named addresses (LocalSubnet…), is someone's own choice and does not count.
    /// </summary>
    public static bool Applies(Rule rule, string program, int port, int profile, string? adapter = null) =>
        rule.Enabled && rule.Inbound && (rule.Profiles & profile) != 0
        && (string.IsNullOrWhiteSpace(rule.Program) || SameProgram(rule.Program, program))
        && rule.Protocol is Tcp or AnyProtocol
        && CoversPort(rule.LocalPorts, port)
        && CoversTailnet(rule.RemoteAddresses)
        && (rule.Interfaces is not { Count: > 0 } || adapter is not null && rule.Interfaces.Contains(adapter, StringComparer.OrdinalIgnoreCase))
        && (string.IsNullOrWhiteSpace(rule.InterfaceTypes) || rule.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase));

    /// <summary>Two paths to the same program as rules write them: environment variables expanded, case ignored.</summary>
    public static bool SameProgram(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
    private static string Clean(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    /// <summary>"*", a port, a range ("8000-9000") or a list of them. Keywords (RPC, IPHTTPS…) never cover a TCP port of Jellyfin.</summary>
    public static bool CoversPort(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*") return true;
        foreach (var part in ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && int.TryParse(range[0], out var single) && single == port) return true;
            if (range.Length == 2 && int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high) && low <= port && port <= high) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether remote addresses cover all of Tailscale's range: "*", or one entry holding 100.64.0.0/10 whole, as a
    /// subnet (with a mask, as the firewall writes them, or a prefix length) or a range.
    /// </summary>
    public static bool CoversTailnet(string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses) || addresses.Trim() == "*") return true;
        const uint first = 100u << 24 | 64u << 16, last = first | 0x003F_FFFF;
        foreach (var part in addresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Split('-', StringSplitOptions.TrimEntries) is [var from, var to] && IPv4(from) is { } low && IPv4(to) is { } high)
            {
                if (low <= first && last <= high) return true;
                continue;
            }
            if (part.Split('/', StringSplitOptions.TrimEntries) is not [var address, var size] || IPv4(address) is not { } network) continue;
            uint? mask = int.TryParse(size, out var bits) && bits is >= 0 and <= 32 ? bits == 0 ? 0u : uint.MaxValue << (32 - bits) : IPv4(size);
            if (mask is { } m && (network & m) <= first && last <= ((network & m) | ~m)) return true;
        }
        return false;
    }
    private static uint? IPv4(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [var a, var b, var c, var d]
            ? (uint)a << 24 | (uint)b << 16 | (uint)c << 8 | d : null;
}
