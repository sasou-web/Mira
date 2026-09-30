using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A Jellyfin server found at an address, as its public information describes it.</summary>
public sealed record ServerInfo(string Address, string Name, string Version);

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
        if (text.Length == 0) throw new ArgumentException("Indique l’adresse de ton serveur Jellyfin, par exemple http://127.0.0.1:8096.");
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
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = candidates.Select(candidate => ProbeAsync(http, candidate, stop.Token)).ToList();
        try
        {
            DateTime? graceEnds = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // The first candidate, in order, that has either answered or is still out.
                var first = probes.FirstOrDefault(x => !x.IsCompleted || x.Result.Info is not null);
                if (first is null) break;
                if (first.IsCompleted) return Supported(first.Result.Info!);
                var pending = Task.WhenAny(probes.Where(x => !x.IsCompleted));
                // A less preferred address already answers: the preferred one gets a moment, not its whole timeout.
                if (probes.FirstOrDefault(x => x.IsCompleted && x.Result.Info is not null) is { } answered)
                {
                    graceEnds ??= DateTime.UtcNow + PreferenceGrace;
                    var left = graceEnds.Value - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return Supported(answered.Result.Info!);
                    await Task.WhenAny(pending, Task.Delay(left, ct)).ConfigureAwait(false);
                }
                else await pending.ConfigureAwait(false);
            }
        }
        finally { stop.Cancel(); }
        var failures = probes.Select(x => x.Result).ToList();
        throw new ServerDiscoveryException(
            failures.Any(x => x.Failure == ProbeFailure.NotJellyfin) ? "Cette adresse répond, mais ce n’est pas un serveur Jellyfin. Vérifie le port (8096 par défaut)."
            : failures.Any(x => x.Failure == ProbeFailure.Certificate) ? "Le certificat HTTPS de ce serveur n’est pas reconnu par Windows. Utilise son adresse http:// sur ton réseau, ou un certificat valide."
            : failures.All(x => x.Failure == ProbeFailure.Timeout) ? "Le serveur ne répond pas. Vérifie l’adresse et que Jellyfin est bien lancé."
            : "Aucun serveur Jellyfin trouvé à cette adresse. Vérifie qu’il est lancé et que l’adresse est correcte.");
    }
    public static readonly TimeSpan PreferenceGrace = TimeSpan.FromSeconds(1.5);
    private static ServerInfo Supported(ServerInfo info) =>
        ParseVersion(info.Version) is { } version && version < MinimumVersion
            ? throw new ServerDiscoveryException($"Mira a besoin de Jellyfin {MinimumVersion.Major}.{MinimumVersion.Minor} ou plus récent ; ce serveur est en {info.Version}. Mets Jellyfin à jour pour continuer.")
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
    private enum ProbeFailure { None, Unreachable, Timeout, Certificate, NotJellyfin }
    private sealed record Probe(ServerInfo? Info, ProbeFailure Failure);
    private static async Task<Probe> ProbeAsync(HttpClient http, string address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
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
            return new(new ServerInfo(final, Text("ServerName") ?? "Jellyfin", Text("Version") ?? ""), ProbeFailure.None);
        }
        // Never throws: the caller reads every probe's result. Stopped because another address won, it simply lost.
        catch (OperationCanceledException) { return new(null, ct.IsCancellationRequested ? ProbeFailure.Unreachable : ProbeFailure.Timeout); }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException) { return new(null, ProbeFailure.Certificate); }
        catch (HttpRequestException) { return new(null, ProbeFailure.Unreachable); }
        catch (JsonException) { return new(null, ProbeFailure.NotJellyfin); }
        catch (ArgumentException) { return new(null, ProbeFailure.NotJellyfin); }
    }
    [GeneratedRegex(@":(\d{1,5})$")] private static partial Regex PortSuffix();
    [GeneratedRegex(@"/web(/|$)", RegexOptions.IgnoreCase)] private static partial Regex WebPage();
}
