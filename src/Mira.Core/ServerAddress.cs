using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mira.Core.Tailscale;

namespace Mira.Core;

/// <summary>A Jellyfin server found at an address, as its public information describes it (<paramref name="Id"/>: the same at every address).</summary>
public sealed record ServerInfo(string Address, string Name, string Version, string Id = "");

/// <summary>An address that leads to no usable Jellyfin server; the message is written for the login screen.</summary>
public sealed class ServerDiscoveryException(string message) : IOException(message);

/// <summary>
/// Turns what someone types in the login screen into a Jellyfin address: "192.168.1.20", "nas:8096",
/// "jellyfin.example.com" or a URL copied from Jellyfin's web page all lead to the server, checked through its
/// public information before any credential is sent.
/// </summary>
public static partial class ServerAddress
{
    /// <summary>Mira uses the user routes introduced by Jellyfin 10.9 (UserItems, UserViews, UserPlayedItems…).</summary>
    public static readonly Version MinimumVersion = new(10, 9);
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    /// <summary>Addresses to try, most secure first. An address typed with its scheme is the only candidate.</summary>
    public static IReadOnlyList<string> Candidates(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) throw new UserInputException(L("Indique l’adresse de ton serveur Jellyfin, par exemple 192.168.1.20.", "Enter your Jellyfin server’s address, for example 192.168.1.20."));
        if (text.Contains("://", StringComparison.Ordinal)) return [JellyfinClient.NormalizeServer(WithoutWebPage(text))];
        var authority = text.Split('/', 2)[0];
        var explicitPort = PortSuffix().IsMatch(authority);
        string Build(string scheme, int? port = null)
        {
            var uri = JellyfinClient.NormalizeServer(WithoutWebPage($"{scheme}://{text}"));
            return port is null ? uri : new UriBuilder(uri) { Port = port.Value }.Uri.AbsoluteUri;
        }
        if (explicitPort)
        {
            // 8920 is Jellyfin's HTTPS port; every other port is tried in plain HTTP first.
            var port = int.Parse(PortSuffix().Match(authority).Groups[1].Value);
            return port is 443 or 8920 ? [Build("https"), Build("http")] : [Build("http"), Build("https")];
        }
        // A name behind a reverse proxy usually answers in HTTPS; a PC or NAS on the network on Jellyfin's port 8096.
        return new[] { Build("https"), Build("http", 8096), Build("http") }.Distinct().ToArray();
    }
    /// <summary>
    /// The first candidate, in order, where a Jellyfin server answers. All of them are asked at once, so an address
    /// that never answers costs one timeout, not one per candidate.
    /// </summary>
    public static async Task<ServerInfo> DiscoverAsync(string input, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        var candidates = Candidates(input);
        using var http = new HttpClient(handler ?? TailnetNetwork.Default.Handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = candidates.Select(candidate => ProbeAsync(http, candidate, stop.Token)).ToList();
        int First(Func<int, bool> match) { for (var i = 0; i < probes.Count; i++) if (match(i)) return i; return -1; }
        // A name of the Internet never ends in http:// after https:// promptly failed on its certificate (every candidate is
        // the typed host): someone on the way can make HTTPS fail just so, then answer in plain HTTP. A later failure is
        // caught by the confirmation before the password leaves (ExposesPasswordAsync); a home address is not refused, as
        // a NAS often keeps its own self-signed certificate on 443. Judged on where the answer came from, after redirects.
        var internet = InternetName(new Uri(candidates[0]).IdnHost);
        bool Refused(int index) => internet && probes[index].Result.Info?.Address.StartsWith("http:", StringComparison.Ordinal) == true
            && First(i => i < index && probes[i].IsCompleted && probes[i].Result.Failure == ProbeFailure.Certificate) >= 0;
        bool Answered(int index) => probes[index].IsCompleted && probes[index].Result.Info is not null && !Refused(index);
        try
        {
            DateTime? graceEnds = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // The first candidate, in order, that has either answered or is still out.
                var first = First(i => !probes[i].IsCompleted || Answered(i));
                if (first < 0) break;
                if (probes[first].IsCompleted) return Supported(probes[first].Result.Info!);
                var pending = Task.WhenAny(probes.Where(x => !x.IsCompleted));
                // A less preferred address already answers: the preferred one gets a moment, not its whole timeout.
                if (First(Answered) is var answered and >= 0)
                {
                    graceEnds ??= DateTime.UtcNow + PreferenceGrace;
                    var left = graceEnds.Value - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return Supported(probes[answered].Result.Info!);
                    await Task.WhenAny(pending, Task.Delay(left, ct)).ConfigureAwait(false);
                }
                else await pending.ConfigureAwait(false);
            }
        }
        finally { stop.Cancel(); }
        // Every candidate is done, so one that answered was refused: typing its address is the way to choose it knowingly.
        if (First(i => probes[i].Result.Info is not null) is var plain and >= 0)
            throw new ServerDiscoveryException(L($"Le certificat HTTPS de ce serveur n’est pas reconnu. Mira n’essaie pas http:// à la place : ton mot de passe y passerait en clair. Si ce serveur est chez toi, tape son adresse complète, {candidates[plain].TrimEnd('/')} ; sinon, il lui faut un certificat valide.",
                $"This server’s HTTPS certificate isn’t recognized. Mira doesn’t try http:// instead: your password would go in clear text. If this server is at your home, type its full address, {candidates[plain].TrimEnd('/')}; otherwise, it needs a valid certificate."));
        var failures = probes.Select(x => x.Result).ToList();
        // Mira's Tailscale node said why: its words (not signed in, the PC asleep…), not a guess. A closed port is only one
        // candidate's (443 when Jellyfin listens on 8096): said only when every candidate found it so.
        var node = failures.Select(x => x.Tailnet).FirstOrDefault(x => x is not null && x != TailnetCodes.Closed)
            ?? (failures.All(x => x.Tailnet == TailnetCodes.Closed) ? TailnetCodes.Closed : null);
        if (node is not null && TailnetText.Problem(node, TailnetNetwork.Default.Peer(new Uri(candidates[0]).IdnHost)) is { } problem)
            throw new ServerDiscoveryException(problem);
        throw new ServerDiscoveryException(
            failures.Any(x => x.Failure == ProbeFailure.NotJellyfin) ? L("Cette adresse répond, mais ce n’est pas un serveur Jellyfin. Vérifie le port (8096 par défaut).", "This address answers, but it isn’t a Jellyfin server. Check the port (8096 by default).")
            : failures.Any(x => x.Failure == ProbeFailure.Certificate) ? L($"Le certificat HTTPS de ce serveur n’est pas reconnu. Si ce serveur est chez toi, tape son adresse complète en http://, par exemple http://{new Uri(candidates[0]).Host}:8096 ; sinon, il lui faut un certificat valide.",
                $"This server’s HTTPS certificate isn’t recognized. If this server is at your home, type its full address with http://, for example http://{new Uri(candidates[0]).Host}:8096; otherwise, it needs a valid certificate.")
            : failures.All(x => x.Failure == ProbeFailure.Timeout) ? L("Le serveur ne répond pas. Vérifie l’adresse et que Jellyfin est bien lancé.", "The server isn’t answering. Check the address and that Jellyfin is running.")
            : L("Aucun serveur Jellyfin trouvé à cette adresse. Vérifie qu’il est lancé et que l’adresse est correcte.", "No Jellyfin server found at this address. Check that it’s running and that the address is right."));
    }
    public static readonly TimeSpan PreferenceGrace = TimeSpan.FromSeconds(1.5);
    /// <summary>Said on the login screen before the password goes in clear across the Internet; a second click sends it.</summary>
    public static string PlainHttpWarning => L("Cette adresse passe par Internet, en http:// : rien n’y est chiffré, et ton mot de passe y passerait lisible par n’importe qui sur le trajet. Utilise plutôt son adresse https:// ou Tailscale, ou clique sur « Se connecter quand même ».",
        "This address goes over the Internet with http://: nothing is encrypted, and anyone along the way could read your password. Use its https:// address or Tailscale instead, or click “Sign in anyway”.");
    /// <summary>
    /// Whether signing in at this address sends the password in clear across the Internet: plain http:// to a server
    /// that is neither this computer, nor on the home network, nor on Tailscale. A name of the Internet
    /// (« jellyfin.example.com ») always counts, whatever it resolves to: the DNS of the network it is typed on may lie.
    /// Any other name is judged by the addresses it leads to, so « nas » is home; one that does not resolve counts as the Internet.
    /// </summary>
    public static async Task<bool> ExposesPasswordAsync(string address, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, CancellationToken ct = default)
    {
        var uri = new Uri(address);
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.');
        // Tailscale's names and the home network's own (mDNS) are answered by those networks, never by the Internet; so is
        // a device that Mira's own Tailscale node knows by its short name (« desktop-invefqh »).
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || TailnetNetwork.Default.IsTailnet(host)) return false;
        if (InternetName(host)) return true;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(ProbeTimeout);
            try { addresses = await (resolve ?? Dns.GetHostAddressesAsync)(host, limit.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested) { return true; }
        }
        // Every address, as the connection may use any of them.
        return addresses.Length == 0 || !addresses.All(x => LocalNetwork.IsHome(x) || LocalNetwork.OnThisNetwork(x));
    }
    /// <summary>
    /// A name the Internet's DNS answers for: dotted, not an address, and not under a suffix kept for home networks or
    /// Tailscale (« nas », « nas.lan », « pc.tail1234.ts.net » are not).
    /// </summary>
    private static bool InternetName(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.');
        return !IPAddress.TryParse(host, out _) && host.Contains('.')
            && !HomeSuffixes.Any(x => host.EndsWith(x, StringComparison.OrdinalIgnoreCase));
    }
    private static readonly string[] HomeSuffixes = [".local", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".ts.net"];
    private static ServerInfo Supported(ServerInfo info) =>
        ParseVersion(info.Version) is { } version && version < MinimumVersion
            ? throw new ServerDiscoveryException(L($"Mira a besoin de Jellyfin {MinimumVersion.Major}.{MinimumVersion.Minor} ou plus récent ; ce serveur est en {info.Version}. Mets Jellyfin à jour pour continuer.",
                $"Mira needs Jellyfin {MinimumVersion.Major}.{MinimumVersion.Minor} or later; this server runs {info.Version}. Update Jellyfin to continue."))
            : info;
    /// <summary>"10.10.7" or "10.11.0-rc2" → the version; null when the server says something else.</summary>
    public static Version? ParseVersion(string? text) =>
        Version.TryParse((text ?? "").Split('-', '+')[0].Trim(), out var version) ? version : null;
    /// <summary>Jellyfin's own page ("…:8096/web/#/home.html") stands for its server: keep what is before /web.</summary>
    private static string WithoutWebPage(string url)
    {
        var cut = url.IndexOfAny(['#', '?']);
        if (cut >= 0) url = url[..cut];
        // Only the path counts: a server simply named "web" keeps its name.
        var path = url.IndexOf('/', url.IndexOf("://", StringComparison.Ordinal) + 3);
        var web = path < 0 ? Match.Empty : WebPage().Match(url, path);
        return web.Success ? url[..web.Index] : url;
    }
    internal enum ProbeFailure { None, Unreachable, Timeout, Certificate, NotJellyfin }
    /// <param name="Tailnet">Why Mira's Tailscale node could not reach the address, when it went through it (<see cref="TailnetCodes"/>).</param>
    internal sealed record Probe(ServerInfo? Info, ProbeFailure Failure, string? Tailnet = null);
    /// <summary>Jellyfin's public information at <paramref name="address"/>, within <paramref name="wait"/> (<see cref="ProbeTimeout"/> by default).</summary>
    internal static async Task<Probe> ProbeAsync(HttpClient http, string address, CancellationToken ct, TimeSpan? wait = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? ProbeTimeout);
        try
        {
            using var response = await http.GetAsync(new Uri(new Uri(address), "System/Info/Public"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1_000_000) return new(null, ProbeFailure.NotJellyfin);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), default, timeout.Token).ConfigureAwait(false);
            var root = json.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            // Emby answers the same request: its product name tells them apart.
            if (string.IsNullOrWhiteSpace(Text("Id")) || Text("ProductName") is { } product && !product.Contains("Jellyfin", StringComparison.OrdinalIgnoreCase))
                return new(null, ProbeFailure.NotJellyfin);
            // A proxy may redirect http:// to https://: keep the address that actually answered.
            var final = response.RequestMessage?.RequestUri?.AbsoluteUri is { } answered && answered.EndsWith("System/Info/Public", StringComparison.OrdinalIgnoreCase)
                ? JellyfinClient.NormalizeServer(answered[..^"System/Info/Public".Length]) : address;
            return new(new ServerInfo(final, Text("ServerName") ?? "Jellyfin", Text("Version") ?? "", Text("Id")!), ProbeFailure.None);
        }
        // Never throws: the caller reads every probe's result. Stopped because another address won, it simply lost.
        catch (OperationCanceledException ex)
        {
            if (ct.IsCancellationRequested) return new(null, ProbeFailure.Unreachable);
            // Out of time on the way to a device of the tailnet: a node still starting, or a device Tailscale says is offline.
            return new(null, ProbeFailure.Timeout, TailnetNetwork.Default.Explain(ex, new Uri(address).IdnHost));
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException) { return new(null, ProbeFailure.Certificate); }
        catch (HttpRequestException ex) { return new(null, ProbeFailure.Unreachable, TailnetNetwork.Find(ex)?.Code); }
        catch (JsonException) { return new(null, ProbeFailure.NotJellyfin); }
        catch (ArgumentException) { return new(null, ProbeFailure.NotJellyfin); }
    }
    [GeneratedRegex(@":(\d{1,5})$")] private static partial Regex PortSuffix();
    [GeneratedRegex(@"/web(/|$)", RegexOptions.IgnoreCase)] private static partial Regex WebPage();
}
