using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mira.Core.Tailscale;

namespace Mira.Core;

/// <summary>
/// The Jellyfin servers the login screen offers, so that nobody has to know an address: one on this computer, those
/// that answer Jellyfin's own discovery on the home network (« Who is JellyfinServer? » broadcast on UDP 7359, as
/// Jellyfin's apps ask it), and those on the person's Tailscale devices (port 8096). Nothing goes beyond the home
/// network and Tailscale: only Jellyfin's public information is asked, as the login screen asks it of a typed address.
/// </summary>
public static class ServerSearch
{
    /// <summary>Jellyfin's discovery port, on which it answers <see cref="Question"/>.</summary>
    public const int Port = 7359;
    public const string Question = "Who is JellyfinServer?";
    /// <summary>How long answers are awaited: a server asleep or offline costs this, no more.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2.5);
    /// <summary>A large tailnet (a company's) is not asked device by device.</summary>
    public const int MaxDevices = 32;
    /// <summary>Where a server was found: its address on this computer works only here, at home only at home, on Tailscale everywhere.</summary>
    public enum Place { ThisComputer, Tailnet, Home }
    public sealed record Found(string Address, string Name, string Id, Place Where);

    /// <summary>
    /// Every server once, the address that serves best first (this computer, then Tailscale, which also works at home and
    /// wherever the server or this computer goes, then the home network); this computer's server first, then those at
    /// home, then those found only through Tailscale.
    /// </summary>
    public static IReadOnlyList<Found> Ranked(IEnumerable<Found> found) => found
        .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .Select(server => (Best: server.OrderBy(x => x.Where).First(), Rank: server.Min(x => x.Where switch { Place.ThisComputer => 0, Place.Home => 1, _ => 2 })))
        .OrderBy(x => x.Rank).ThenBy(x => x.Best.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Best.Address, StringComparer.Ordinal)
        .Select(x => x.Best).ToList();

    /// <summary>
    /// A server's answer to <see cref="Question"/>, from <paramref name="from"/>: <c>{"Address":"http://192.168.1.20:8096",
    /// "Id":"…","Name":"…"}</c>, Address being the one it is reached at from that network (or its published address). An
    /// address on no network (0.0.0.0, loopback) is replaced by the one the answer came from. Null for anything else.
    /// </summary>
    public static Found? Reply(byte[] data, IPAddress from)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("Id") is not { Length: > 0 } id || !Uri.TryCreate(Text("Address"), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return null;
            if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
            if (IPAddress.TryParse(address.Host.Trim('[', ']'), out var host) && (IPAddress.IsLoopback(host) || host.Equals(IPAddress.Any) || host.Equals(IPAddress.IPv6Any)) && !IPAddress.IsLoopback(from))
                address = new UriBuilder(address) { Host = from.ToString() }.Uri;
            return new(JellyfinClient.NormalizeServer(address.AbsoluteUri), Text("Name") is { Length: > 0 } name ? name : "Jellyfin", id, Place.Home);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or UriFormatException) { return null; }
    }

    /// <summary>
    /// The devices worth asking on Tailscale, from <c>tailscale status --json</c>: online, able to run Jellyfin (not a
    /// phone), and the person's or shared with them, never an exit node of a VPN sold through Tailscale (Mullvad's
    /// carry a Location): their Tailscale IPv4 addresses, <see cref="MaxDevices"/> at most.
    /// </summary>
    public static IReadOnlyList<string> TailnetDevices(string? status)
    {
        if (status is null) return [];
        try
        {
            using var json = JsonDocument.Parse(status);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object) return [];
            var found = new List<string>();
            foreach (var peer in peers.EnumerateObject().Select(x => x.Value))
            {
                if (peer.ValueKind != JsonValueKind.Object || !(peer.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True)) continue;
                if (peer.TryGetProperty("Location", out var location) && location.ValueKind == JsonValueKind.Object) continue;
                if (peer.TryGetProperty("DNSName", out var dns) && dns.ValueKind == JsonValueKind.String && dns.GetString()!.TrimEnd('.').EndsWith(".mullvad.ts.net", StringComparison.OrdinalIgnoreCase)) continue;
                if (peer.TryGetProperty("OS", out var os) && os.ValueKind == JsonValueKind.String && os.GetString() is "iOS" or "android" or "tvOS") continue;
                if (!peer.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
                if (ips.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).FirstOrDefault(LocalNetwork.IsTailnet) is { } address && !found.Contains(address))
                    found.Add(address);
                if (found.Count == MaxDevices) break;
            }
            return found;
        }
        catch (JsonException) { return []; }
    }
    /// <summary>The same devices from the status of Mira's own node (« Tailscale intégré »).</summary>
    public static IReadOnlyList<string> NodeDevices(TailnetStatus? status) => status?.State != TailnetStates.Running ? [] : status.Peers
        .Where(x => x.Online && x.Os is not ("iOS" or "android" or "tvOS") && x.DnsName?.TrimEnd('.').EndsWith(".mullvad.ts.net", StringComparison.OrdinalIgnoreCase) != true)
        .Select(x => x.Ips.FirstOrDefault(LocalNetwork.IsTailnet)).OfType<string>().Distinct().Take(MaxDevices).ToList();

    /// <summary>
    /// Asks it all at once and waits <see cref="Wait"/> at most: Jellyfin on this computer, the home network
    /// (<paramref name="home"/> replaces the broadcast in the tests), and <paramref name="tailnet"/>'s devices.
    /// </summary>
    public static async Task<IReadOnlyList<Found>> FindAsync(IReadOnlyList<string> tailnet, HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<IReadOnlyList<Found>>>? home = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        var asked = new List<Task<IReadOnlyList<Found>>> { AskAsync(http, "http://127.0.0.1:8096/", Place.ThisComputer, ct), (home ?? AskHomeAsync)(ct) };
        asked.AddRange(tailnet.Distinct().Take(MaxDevices).Select(device => AskAsync(http, $"http://{device}:8096/", Place.Tailnet, ct)));
        // Each one stops by itself after Wait; one that would not (a stuck connection) is left behind, not waited for.
        try { await Task.WhenAll(asked).WaitAsync(Wait + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
        catch (TimeoutException) { }
        return Ranked(asked.Where(x => x.IsCompletedSuccessfully).SelectMany(x => x.Result));
    }
    private static async Task<IReadOnlyList<Found>> AskAsync(HttpClient http, string address, Place where, CancellationToken ct)
    {
        var probe = await ServerAddress.ProbeAsync(http, address, ct, Wait).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return probe.Info is { Id.Length: > 0 } info ? [new Found(info.Address, info.Name, info.Id, where)] : [];
    }
    /// <summary>
    /// <see cref="Question"/> broadcast on every network of this computer, then the answers for <see cref="Wait"/>. No
    /// network, or one the system keeps Mira off (macOS asks the person first), simply answers nothing.
    /// </summary>
    private static async Task<IReadOnlyList<Found>> AskHomeAsync(CancellationToken ct)
    {
        var found = new List<Found>();
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            // Windows reports a « port unreachable » from one address as an error of the whole socket: not here.
            if (OperatingSystem.IsWindows()) udp.Client.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var question = Encoding.UTF8.GetBytes(Question);
            foreach (var target in BroadcastAddresses())
                try { await udp.SendAsync(question, new IPEndPoint(target, Port), ct).ConfigureAwait(false); } catch (SocketException) { }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Wait);
            for (var errors = 0; errors < 10 && found.Count < MaxDevices;)
            {
                UdpReceiveResult answer;
                try { answer = await udp.ReceiveAsync(limit.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                catch (SocketException) { errors++; continue; }
                if (Reply(answer.Buffer, answer.RemoteEndPoint.Address) is { } server) found.Add(server);
            }
        }
        catch (SocketException) { }
        return found;
    }
    /// <summary>Everyone on the network this computer is on, and each of its networks' own broadcast address (Windows sends the first on one of them only).</summary>
    private static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    IPAddress mask;
                    try { mask = unicast.IPv4Mask; } catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException) { continue; }
                    if (Broadcast(unicast.Address, mask) is { } target && !targets.Contains(target)) targets.Add(target);
                }
            }
        }
        catch (NetworkInformationException) { }
        return targets;
    }
    /// <summary>
    /// The broadcast address of <paramref name="address"/>'s network (192.168.1.255 for 192.168.1.20/24); null for a
    /// network that has none (a single address), Tailscale's (it carries no broadcast) and a self-assigned address.
    /// </summary>
    public static IPAddress? Broadcast(IPAddress address, IPAddress? mask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || mask is null || mask.AddressFamily != AddressFamily.InterNetwork) return null;
        if (LocalNetwork.IsTailnet(address.ToString()) || address.GetAddressBytes() is [169, 254, ..] || IPAddress.IsLoopback(address)) return null;
        var (bytes, bits) = (address.GetAddressBytes(), mask.GetAddressBytes());
        if (bits.All(x => x == 255) || bits.All(x => x == 0)) return null;
        for (var i = 0; i < 4; i++) bytes[i] |= (byte)~bits[i];
        return new IPAddress(bytes);
    }

    /// <summary>Said under the address while the search runs.</summary>
    public static string Searching => L("Recherche de ton serveur Jellyfin…", "Looking for your Jellyfin server…");
    /// <summary>Said under the address it fills in; <paramref name="here"/>: « ce PC », « ce Mac ».</summary>
    public static string Said(Found found, string here) => found.Where switch
    {
        Place.ThisComputer => L($"Jellyfin trouvé sur {here}.", $"Jellyfin found on {here}."),
        Place.Tailnet => L($"Trouvé par Tailscale : {found.Name}. Cette adresse marche chez toi comme ailleurs.", $"Found through Tailscale: {found.Name}. This address works at home and away."),
        _ => L($"Trouvé sur ton réseau : {found.Name}.", $"Found on your network: {found.Name}."),
    };
    /// <summary>A button for each other server found: its name and where it is.</summary>
    public static string Choice(Found found) => $"{found.Name} · {(Uri.TryCreate(found.Address, UriKind.Absolute, out var uri) ? uri.Host : found.Address)}";
    public static string Others => L("Autres serveurs trouvés :", "Other servers found:");
    /// <summary>Nothing answered: what to type instead.</summary>
    public static string NoneFound(string here) => L($"Aucun Jellyfin trouvé sur {here} ni sur ton réseau. Tape l’adresse du PC où il tourne (par exemple 192.168.1.20), ou son adresse Tailscale (100.x.y.z).",
        $"No Jellyfin found on {here} or on your network. Type the address of the PC it runs on (for example 192.168.1.20), or its Tailscale address (100.x.y.z).");
    public static string SearchAgain => L("Chercher à nouveau", "Search again");
}
