using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mira.Jellyfin;

/// <summary>
/// « Le foyer » away from home: the family's devices reach this Jellyfin through Tailscale, without the Tailscale app,
/// a Tailscale account, nor the Tailscale console. The administrator connects the home once to their tailnet with an
/// OAuth client of theirs (<see cref="Scopes"/>, tag <see cref="Tag"/>); then each « home
/// link » carries a key of its own, single-use, that ends after <see cref="LinkLife"/>, tagged <see cref="Tag"/>,
/// which Mira's built-in Tailscale on the family's device logs in with. The tailnet's policy must let a device of that
/// tag reach this PC's Jellyfin and nothing else: <see cref="CheckPolicy"/> reads it before every link, and refuses
/// one otherwise. A device taken out of the home leaves the tailnet (its node deleted), at once or at the next call
/// that can reach Tailscale. What it keeps, in one file beside the plugins' settings: the OAuth client (its secret
/// too: the extension needs it to make a link), the nodes still to delete. Free of ASP.NET, on a clock of its own,
/// for the tests (<see cref="HomeController"/> serves it, <see cref="TailscaleApi"/> calls Tailscale).
/// </summary>
public sealed class HomeTailnet
{
    /// <summary>The tag of the family's devices on the tailnet: their keys carry it, the policy limits it to Jellyfin.</summary>
    public const string Tag = "tag:mira-home";
    /// <summary>
    /// The tag of friends' devices, joined by an invitation (<see cref="FriendInvites"/>): owned by <see cref="Tag"/> so
    /// the home's OAuth client makes their keys too, limited to Jellyfin like it, apart so friends can be listed and cut off.
    /// </summary>
    public const string FriendTag = "tag:mira-friend";
    /// <summary>
    /// The OAuth client's scopes: keys of the tag, the tag's devices (listed, deleted), and the policy, which Mira reads
    /// before every link and puts right itself when the administrator asks (<see cref="HomePolicyFix"/>).
    /// </summary>
    public static readonly string[] Scopes = ["auth_keys", "devices:core", "policy_file"];
    /// <summary>How long a home link's key can be used (once).</summary>
    public static readonly TimeSpan LinkLife = TimeSpan.FromHours(48);
    /// <summary>A node of the tag that never joined the home (the link's key used, no password typed) goes after this long.</summary>
    public static readonly TimeSpan Unjoined = TimeSpan.FromDays(7);
    /// <summary>Where a home link opens: the site, which never sees what follows the « # » (the browser keeps it).</summary>
    public const string LinkBase = "https://miracinema.app/home#";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string? _file;
    private readonly Func<DateTimeOffset> _now;
    private Store _store = new();

    public HomeTailnet(string? file, Func<DateTimeOffset> now)
    {
        _file = file;
        _now = now;
        Load();
    }

    /// <summary>The OAuth client, or null when the home is not connected to a tailnet.</summary>
    public (string Id, string Secret)? Client
    {
        get
        {
            lock (_gate)
            {
                return _store.ClientId is { Length: > 0 } id && _store.ClientSecret is { Length: > 0 } secret ? (id, secret) : null;
            }
        }
    }

    /// <summary>When the OAuth client was set.</summary>
    public DateTimeOffset? Connected
    {
        get { lock (_gate) return _store.ClientId is null ? null : _store.Connected; }
    }

    /// <summary>
    /// The client's id as its secret carries it (« tskey-client-&lt;id&gt;-&lt;secret&gt; »), so the administrator pastes
    /// one thing; null when the secret is not of that shape (the id is then asked for).
    /// </summary>
    public static string? IdOfSecret(string? secret)
    {
        const string Prefix = "tskey-client-";
        if (secret is null || !secret.Trim().StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = secret.Trim()[Prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        return dash >= 4 && dash < rest.Length - 1 ? rest[..dash] : null;
    }

    /// <summary>The scopes the home needs that <paramref name="granted"/> lacks (none when Tailscale did not say, or for « all »).</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string>? granted) =>
        granted is null || granted.Any(x => string.Equals(x, "all", StringComparison.OrdinalIgnoreCase))
            ? []
            : Scopes.Where(s => !granted.Any(x => string.Equals(x, s, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>How long « Undo » stays offered once Mira has fixed the policy.</summary>
    public static readonly TimeSpan UndoFor = TimeSpan.FromMinutes(10);

    /// <summary>The policy as it was before Mira's fix, the policy Mira wrote, and until when it can be put back.</summary>
    public (string Before, string Written, DateTimeOffset Until)? Undo
    {
        get
        {
            lock (_gate)
            {
                return _undo is { } undo && undo.Until > _now() ? undo : null;
            }
        }
    }

    private (string Before, string Written, DateTimeOffset Until)? _undo;

    /// <summary>Mira's fix written: what was there before, for <see cref="UndoFor"/> (in memory only).</summary>
    public void Fixed(string before, string written)
    {
        lock (_gate)
        {
            _undo = (before, written, _now() + UndoFor);
        }
    }

    /// <summary>« Undo » used, or no longer possible.</summary>
    public void Undone()
    {
        lock (_gate)
        {
            _undo = null;
        }
    }

    /// <summary>
    /// Tailscale refused to make this PC's key never expire (the client's devices:core may not reach a device that is
    /// not of its tags): not offered again until the extension restarts.
    /// </summary>
    public bool KeyRefused { get; set; }

    /// <summary>Tailscale refused the policy with Funnel's node attribute: the fix is made without it until the extension restarts.</summary>
    public bool FunnelRefused { get; set; }

    /// <summary>An OAuth client's id and secret as Tailscale gives them: printable, without spaces.</summary>
    public static bool GoodClient(string? id, string? secret) =>
        id is { Length: >= 4 and <= 128 } && secret is { Length: >= 8 and <= 256 }
        && id.All(c => c is > ' ' and < '\u007f') && secret.All(c => c is > ' ' and < '\u007f');

    /// <summary>The OAuth client set, or forgotten (null).</summary>
    public void SetClient(string? id, string? secret)
    {
        lock (_gate)
        {
            _store.ClientId = id is null ? null : id.Trim();
            _store.ClientSecret = id is null ? null : secret?.Trim();
            _store.Connected = id is null ? null : _now();
            Save();
        }
    }

    /// <summary>A node's Tailscale address to delete from the tailnet (its device left the home), kept until it is gone.</summary>
    public void Remove(string address)
    {
        if (TailnetAddress(address) is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_store.Leaving.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                _store.Leaving.Add(address);
                Save();
            }
        }
    }

    /// <summary>Every node of the tag to delete (the home turned off).</summary>
    public void RemoveAll()
    {
        lock (_gate)
        {
            _store.LeaveAll = true;
            Save();
        }
    }

    /// <summary>The addresses still to delete, and whether every node of the tag goes.</summary>
    public (IReadOnlyList<string> Addresses, bool All) Leaving()
    {
        lock (_gate) return (_store.Leaving.ToList(), _store.LeaveAll);
    }

    /// <summary>What was deleted: these addresses, or every node (<paramref name="all"/>).</summary>
    public void Left(IEnumerable<string> addresses, bool all)
    {
        lock (_gate)
        {
            foreach (var address in addresses)
            {
                _store.Leaving.RemoveAll(x => string.Equals(x, address, StringComparison.OrdinalIgnoreCase));
            }

            if (all)
            {
                _store.LeaveAll = false;
            }

            Save();
        }
    }

    /// <summary>
    /// The nodes of the tag to delete now: those at an address that left, all of them when the home was turned off, and
    /// those that never joined the home within <see cref="Unjoined"/> (a link's key used by someone who never typed the
    /// password). Never a node without the tag.
    /// </summary>
    public IReadOnlyList<TailnetDevice> ToDelete(IEnumerable<TailnetDevice> nodes, IEnumerable<string> joined)
    {
        var (leaving, all) = Leaving();
        var inHome = new HashSet<string>(joined.Select(x => Canonical(x)!).Where(x => x is not null), StringComparer.OrdinalIgnoreCase);
        var now = _now();
        return nodes.Where(x => x.Tagged).Where(x =>
            all
            || x.Addresses.Any(a => leaving.Contains(a, StringComparer.OrdinalIgnoreCase))
            || (!x.Addresses.Any(inHome.Contains) && x.Created is { } created && now - created >= Unjoined)).ToList();
    }

    // ---- Addresses ----

    private static readonly (IPAddress Network, int Bits)[] Ranges =
    [
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("fd7a:115c:a1e0::"), 48),
    ];

    /// <summary>Whether an address is a Tailscale one (100.64.0.0/10, fd7a:115c:a1e0::/48): its text, canonical, or null.</summary>
    public static string? TailnetAddress(string? text) =>
        IPAddress.TryParse(text, out var address) && InTailnet(address) ? Canonical(text) : null;

    private static string? Canonical(string? text)
    {
        if (!IPAddress.TryParse(text, out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.ScopeId = 0;
        }

        return address.ToString();
    }

    private static bool InTailnet(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return Ranges.Any(r => Within(address, r.Network, r.Bits));
    }

    private static bool Within(IPAddress address, IPAddress network, int bits)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xff : (0xff << (8 - bits)) & 0xff;
            if ((a[i] & mask) != (n[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>This PC's Tailscale address (IPv4), from its network interfaces: null when Tailscale is not on this PC.</summary>
    public static string? OwnAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up)
                .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                .Select(x => x.Address)
                .Where(x => x.AddressFamily == AddressFamily.InterNetwork && InTailnet(x))
                .Select(x => x.ToString())
                .OrderBy(x => x, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    // ---- The policy ----

    /// <summary>What the tailnet's policy lets a device of <see cref="Tag"/> do.</summary>
    public enum Verdict
    {
        /// <summary>This PC's Jellyfin port, nothing else.</summary>
        Ok,
        /// <summary>The tag is not in tagOwners: Tailscale refuses keys of it.</summary>
        NoTag,
        /// <summary>A rule lets the tag reach more than Jellyfin (the default policy's « * » does).</summary>
        TooOpen,
        /// <summary>Nothing lets the tag reach Jellyfin.</summary>
        NoAccess,
    }

    /// <summary>A rule of the policy that lets the tag reach more than Jellyfin: its section, its place (from 1), its sources.</summary>
    public sealed record OpenRule(string Section, int Number, IReadOnlyList<string> Sources);

    /// <summary>
    /// Whether the policy (as JSON, its HuJSON's comments gone) lets a device of <see cref="Tag"/> reach this PC's
    /// Jellyfin (<paramref name="server"/>, its Tailscale address, and <paramref name="port"/>) and nothing else.
    /// Careful rather than clever: every rule whose sources may hold such a device (« * », the tag, autogroup:tagged,
    /// any address or range that may be one of the tailnet's, anything not understood) must lead only to that port of
    /// that address, through TCP; no SSH rule may hold it; a grant with an app capability or a « via » is refused.
    /// </summary>
    public static (Verdict Verdict, IReadOnlyList<OpenRule> Open) CheckPolicy(JsonNode? policy, string server, int port, string tag = Tag)
    {
        if (policy is not JsonObject root)
        {
            return (Verdict.NoAccess, []);
        }

        var hosts = Section(root, "hosts") as JsonObject;
        if (Section(root, "tagOwners") is not JsonObject owners || !owners.Any(x => string.Equals(x.Key, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return (Verdict.NoTag, []);
        }

        var open = new List<OpenRule>();
        var reaches = false;
        if (Section(root, "acls") is JsonArray acls)
        {
            var number = 0;
            foreach (var rule in acls)
            {
                number++;
                if (rule is not JsonObject row)
                {
                    continue;
                }

                var sources = Strings(Field(row, "src") ?? Field(row, "users"));
                if (!sources.Any(x => MayHold(x, hosts, tag)))
                {
                    continue;
                }

                if (!Limited(row, "acls", hosts, server, [port]))
                {
                    open.Add(new OpenRule("acls", number, sources));
                }
                else
                {
                    reaches |= string.Equals(Text(Field(row, "action")), "accept", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        if (Section(root, "grants") is JsonArray grants)
        {
            var number = 0;
            foreach (var rule in grants)
            {
                number++;
                if (rule is not JsonObject row)
                {
                    continue;
                }

                var sources = Strings(Field(row, "src"));
                if (!sources.Any(x => MayHold(x, hosts, tag)))
                {
                    continue;
                }

                if (!Limited(row, "grants", hosts, server, [port]))
                {
                    open.Add(new OpenRule("grants", number, sources));
                }
                else
                {
                    reaches = true;
                }
            }
        }

        if (Section(root, "ssh") is JsonArray ssh)
        {
            var number = 0;
            foreach (var rule in ssh)
            {
                number++;
                if (rule is JsonObject row && Strings(Field(row, "src")) is var sources && sources.Any(x => MayHold(x, hosts, tag)))
                {
                    open.Add(new OpenRule("ssh", number, sources));
                }
            }
        }

        if (open.Count > 0)
        {
            return (Verdict.TooOpen, open);
        }

        return (reaches ? Verdict.Ok : Verdict.NoAccess, []);
    }

    /// <summary>
    /// The port Tailscale Funnel serves Mira on (<c>tailscale funnel --set-path=/Mira</c>): friends a PC is shared with
    /// reach it there too when their own Tailscale resolves its public name to its tailnet address. Only /Mira is served
    /// on it, never the rest of Jellyfin.
    /// </summary>
    public const int FunnelPort = 443;

    /// <summary>
    /// What the policy lets the people this PC is shared with do (autogroup:shared, and « * » that holds them): whether a
    /// rule lets them reach Jellyfin, and the rules of acls or grants that let them reach more than this PC's Jellyfin
    /// port and <see cref="FunnelPort"/>. SSH rules are left to the administrator.
    /// </summary>
    public static (bool Reaches, IReadOnlyList<OpenRule> Open) CheckShared(JsonNode? policy, string server, int port)
    {
        if (policy is not JsonObject root)
        {
            return (false, []);
        }

        var hosts = Section(root, "hosts") as JsonObject;
        var open = new List<OpenRule>();
        var reaches = false;
        foreach (var section in new[] { "acls", "grants" })
        {
            if (Section(root, section) is not JsonArray rules)
            {
                continue;
            }

            var number = 0;
            foreach (var rule in rules)
            {
                number++;
                if (rule is not JsonObject row)
                {
                    continue;
                }

                var sources = Strings(Field(row, "src") ?? (section == "acls" ? Field(row, "users") : null));
                if (!sources.Any(x => x.Trim() == "*" || string.Equals(x.Trim(), Shared, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!Limited(row, section, hosts, server, [port, FunnelPort]))
                {
                    open.Add(new OpenRule(section, number, sources));
                }
                else if (section == "grants" || string.Equals(Text(Field(row, "action")), "accept", StringComparison.OrdinalIgnoreCase))
                {
                    reaches |= Strings(Field(row, "dst") ?? Field(row, "ports")).Count > 0;
                }
            }
        }

        return (reaches, open);
    }

    /// <summary>The people a device of this tailnet was shared with.</summary>
    public const string Shared = "autogroup:shared";

    /// <summary>The rule that lets the people this PC is shared with reach its Jellyfin, and Mira through Funnel's port.</summary>
    public static string SharedGrant(string server, int port) =>
        $"{{\"src\": [\"{Shared}\"], \"dst\": [\"{server}\"], \"ip\": [\"tcp:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}\", \"tcp:{FunnelPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"]}}";

    /// <summary>The node attribute that lets this PC (only) open Mira to the Internet with Funnel.</summary>
    public static string FunnelAttribute(string server) => $"{{\"target\": [\"{server}\"], \"attr\": [\"funnel\"]}}";

    /// <summary>Whether some node attribute of the policy already gives Funnel (to this PC or to anyone: Mira does not guess whom).</summary>
    public static bool FunnelAllowed(JsonNode? policy) =>
        policy is JsonObject root && Section(root, "nodeAttrs") is JsonArray attrs
        && attrs.OfType<JsonObject>().Any(x => Strings(Field(x, "attr")).Any(a => string.Equals(a.Trim(), "funnel", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Whether a rule of acls or grants leads only to <paramref name="ports"/> of this PC, through TCP: no app capability,
    /// no « via », at least one destination.
    /// </summary>
    private static bool Limited(JsonObject row, string section, JsonObject? hosts, string server, IReadOnlyCollection<int> ports)
    {
        if (section == "acls")
        {
            var proto = Text(Field(row, "proto"));
            var targets = Strings(Field(row, "dst") ?? Field(row, "ports"));
            return targets.Count > 0 && (proto is null || string.Equals(proto, "tcp", StringComparison.OrdinalIgnoreCase) || proto == "6")
                && targets.All(x => AclTarget(x, hosts, server, ports));
        }

        var destinations = Strings(Field(row, "dst"));
        var ip = Strings(Field(row, "ip"));
        return Field(row, "app") is null && Field(row, "via") is null && destinations.Count > 0 && ip.Count > 0
            && destinations.All(x => Resolves(x, hosts, server)) && ip.All(x => ports.Any(p => GrantPort(x, p)));
    }

    /// <summary>
    /// What to add to the policy, once, as the administrator pastes it: the tag's owners, and the one rule. Friends' tag is
    /// owned by the home's too: the home's OAuth client, made with the home's tag, makes friends' keys.
    /// </summary>
    public static (string TagOwners, string Grant) Setup(string server, int port, string tag = Tag) =>
        (tag == FriendTag ? $"\"{tag}\": [\"autogroup:admin\", \"{Tag}\"]" : $"\"{tag}\": [\"autogroup:admin\"]",
         $"{{\"src\": [\"{tag}\"], \"dst\": [\"{server}\"], \"ip\": [\"tcp:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"]}}");

    private static JsonNode? Section(JsonObject root, string name) =>
        root.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonNode? Field(JsonObject row, string name) => Section(row, name);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray list => list.Select(x => Text(x) ?? "?").ToList(),
        JsonValue => [Text(node) ?? "?"],
        _ => [],
    };

    // Sources that never hold a tagged device: users, groups (users only), other tags, the user autogroups.
    private static readonly HashSet<string> UserGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "autogroup:member", "autogroup:self", "autogroup:shared", "autogroup:admin", "autogroup:owner", "autogroup:it-admin",
        "autogroup:network-admin", "autogroup:billing-admin", "autogroup:auditor", "autogroup:nonroot",
    };

    /// <summary>Whether a rule's source may hold a device of <see cref="Tag"/>.</summary>
    private static bool MayHold(string source, JsonObject? hosts, string tag)
    {
        var plain = source.Trim();
        if (plain == "*" || string.Equals(plain, tag, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (plain.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) || plain.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
            || UserGroups.Contains(plain) || (plain.Contains('@', StringComparison.Ordinal) && !plain.Contains(':', StringComparison.Ordinal)))
        {
            return false;
        }

        // An address, a range or a host's name: it may be a device of the tag unless it is wholly outside the tailnet's ranges.
        var range = HostValue(plain, hosts) ?? plain;
        return !OutsideTailnet(range);
    }

    private static string? HostValue(string name, JsonObject? hosts) =>
        hosts is null ? null : Text(hosts.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value);

    /// <summary>An address or a range (a.b.c.d/n) that does not overlap Tailscale's ranges at all.</summary>
    private static bool OutsideTailnet(string text)
    {
        var parts = text.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        var full = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var bits = parts.Length == 2 && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : full;
        if (bits < 0 || bits > full)
        {
            return false;
        }

        // Two ranges overlap when the shorter prefix holds the other's network.
        return !Ranges.Where(r => r.Network.AddressFamily == address.AddressFamily)
            .Any(r => Within(address, r.Network, Math.Min(bits, r.Bits)));
    }

    /// <summary>A destination that is only this PC: its address, alone (/32), or a host's name for it.</summary>
    private static bool Resolves(string target, JsonObject? hosts, string server)
    {
        var plain = HostValue(target.Trim(), hosts) ?? target.Trim();
        if (plain.EndsWith("/32", StringComparison.Ordinal))
        {
            plain = plain[..^3];
        }

        return string.Equals(Canonical(plain), Canonical(server), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An ACL destination « host:ports » that is only this PC's Jellyfin port.</summary>
    private static bool AclTarget(string target, JsonObject? hosts, string server, IReadOnlyCollection<int> allowed)
    {
        var colon = target.LastIndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var host = target[..colon].Trim('[', ']');
        var ports = target[(colon + 1)..];
        return Resolves(host, hosts, server) && ports.Split(',').All(x => allowed.Any(p => x.Trim() == p.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>A grant's « ip » that is only the Jellyfin port, through TCP.</summary>
    private static bool GrantPort(string ip, int port)
    {
        var plain = ip.Trim();
        var number = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return plain == number || string.Equals(plain, "tcp:" + number, StringComparison.OrdinalIgnoreCase) || plain == "6:" + number;
    }

    // ---- The home link ----

    /// <summary>What a home link carries: the server's name, its id, its address on the tailnet, the key, its end.</summary>
    public sealed record LinkData(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("id")] string ServerId,
        [property: JsonPropertyName("url")] string Address,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("until")] long Until);

    /// <summary>
    /// A home link: <see cref="LinkBase"/> then its data in base64url JSON. Never the home's password: the link lets a
    /// device reach Jellyfin, the password lets it in the home.
    /// </summary>
    public static string Link(LinkData data) =>
        LinkBase + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(data)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>A home link read back (what the apps do): null for anything else.</summary>
    public static LinkData? ReadLink(string? link)
    {
        var hash = link?.IndexOf('#', StringComparison.Ordinal) ?? -1;
        if (hash < 0 || link!.Length - hash > 4096)
        {
            return null;
        }

        var text = link[(hash + 1)..].Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - (text.Length % 4)) % 4);
        try
        {
            var data = JsonSerializer.Deserialize<LinkData>(Convert.FromBase64String(text));
            return data is { Version: 1, Key.Length: > 0, Address.Length: > 0 } ? data : null;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    // ---- The file ----

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            ServerSecrets.Restrict(_file);
            ServerSecrets.Plain();
            _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_file), Json) ?? new Store();
            _store.Leaving.RemoveAll(x => TailnetAddress(x) is null);
            if (ServerSecrets.Plain() && ServerSecrets.Sealing)
            {
                // The client's secret was in clear (0.9.15 and older): sealed now.
                Save();
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: put aside, the home is no longer connected to the tailnet until its administrator sets the client again.
            try { File.Move(_file, _file + ".bad-" + _now().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), true); }
            catch (Exception moving) when (moving is IOException or UnauthorizedAccessException) { }
            _store = new Store();
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            ServerSecrets.Write(_file, JsonSerializer.Serialize(_store, Json));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Kept in memory until the next change.
        }
    }

    internal sealed class Store
    {
        public string? ClientId { get; set; }
        /// <summary>Sealed in the file (<see cref="ServerSecrets"/>): it may write the tailnet's policy and make keys.</summary>
        [JsonConverter(typeof(ServerSecrets.Sealed))]
        public string? ClientSecret { get; set; }
        public DateTimeOffset? Connected { get; set; }
        public List<string> Leaving { get; set; } = [];
        public bool LeaveAll { get; set; }
    }
}

/// <summary>
/// A node of the tailnet as the extension reads it: its id for the API, its addresses, whether it has one of Mira's tags
/// (the home's or friends'), and whether that is friends' (<see cref="HomeTailnet.FriendTag"/>).
/// </summary>
public sealed record TailnetDevice(string Id, string Name, IReadOnlyList<string> Addresses, bool Tagged, DateTimeOffset? Created, DateTimeOffset? LastSeen)
{
    public bool Friend { get; init; }

    /// <summary>Whether its key never expires, as Tailscale says (null when it doesn't).</summary>
    public bool? KeyExpiryDisabled { get; init; }
}
