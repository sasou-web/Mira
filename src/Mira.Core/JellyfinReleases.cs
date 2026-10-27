using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A Jellyfin Server for Windows newer than the one installed, as winget's reviewed manifest gives it.</summary>
public sealed record JellyfinRelease(Version Version, JellyfinServerPackage Package);

/// <summary>
/// « Jellyfin 12.3 est disponible »: the versions of Jellyfin Server in winget's catalogue (microsoft/winget-pkgs on
/// GitHub, whose manifests Microsoft's pipeline checks against the installer before they are merged), the source of the
/// version Mira installs (<see cref="JellyfinServerPackage.Current"/>), read live: the address on repo.jellyfin.org and
/// the SHA-256 come from GitHub, the file from Jellyfin, and both must agree. A version reaches winget a few days after
/// its release. Nothing is sent but the requests themselves (GitHub, already asked for Mira's own updates).
/// </summary>
public static partial class JellyfinReleases
{
    public static readonly Uri Catalog = new("https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/j/Jellyfin/Server");
    public static Uri InstallerManifest(string version) => new($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/j/Jellyfin/Server/{Uri.EscapeDataString(version)}/Jellyfin.Server.installer.yaml");
    /// <summary>Jellyfin's notes for a version (« 12.3 »), where winget's manifest points (ReleaseNotesUrl).</summary>
    public static Uri Notes(string version) => new($"https://github.com/jellyfin/jellyfin/releases/tag/v{Uri.EscapeDataString(version)}");
    /// <summary>How often Mira asks, at most.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>« 12.1.0 » (Jellyfin's System/Info), « 12.3 » (winget), « 12.1.0.0 », « v12.3 »: major, minor, patch (0 when missing); null otherwise.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (text is null || VersionText().Match(text.Trim()) is not { Success: true } match) return null;
        static int Part(Group group) => group.Success && int.TryParse(group.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new(Part(match.Groups[1]), Part(match.Groups[2]), Part(match.Groups[3]));
    }

    /// <summary>The version folders of GitHub's listing of the catalogue (its contents API: an array of entries, folders typed « dir »).</summary>
    public static IReadOnlyList<string> Versions(JsonElement listing) => listing.ValueKind != JsonValueKind.Array ? []
        : listing.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("type", out var type) && type.ValueEquals("dir")
                && x.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && FolderName().IsMatch(name.GetString()!))
            .Select(x => x.GetProperty("name").GetString()!).ToList();

    /// <summary>
    /// The version to offer over <paramref name="installed"/>: the newest of the same major version, else the newest
    /// of a later one. Null when none is newer.
    /// </summary>
    public static string? Pick(IEnumerable<string> versions, Version installed)
    {
        var newer = versions.Select(x => (Name: x, Version: ParseVersion(x))).Where(x => x.Version is { } v && v > installed).ToList();
        var same = newer.Where(x => x.Version!.Major == installed.Major).ToList();
        return (same.Count > 0 ? same : newer).OrderByDescending(x => x.Version).Select(x => x.Name).FirstOrDefault();
    }

    /// <summary>
    /// The x64 installer of winget's installer manifest for <paramref name="version"/>: its address, only an .exe on
    /// https://repo.jellyfin.org, and its SHA-256. Null when the manifest is for another version or another package,
    /// or gives anything else.
    /// </summary>
    public static JellyfinServerPackage? ParseInstaller(string yaml, string version)
    {
        string? identifier = null, manifestVersion = null; var inInstallers = false;
        var installers = new List<(string? Architecture, string? Url, string? Sha256)>();
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith('#') || YamlEntry().Match(line) is not { Success: true } entry) continue;
            var (key, value) = (entry.Groups["key"].Value, Unquote(entry.Groups["value"].Value));
            if (entry.Groups["indent"].Length == 0 && !entry.Groups["dash"].Success)
            {
                inInstallers = key == "Installers";
                if (key == "PackageIdentifier") identifier = value;
                else if (key == "PackageVersion") manifestVersion = value;
                continue;
            }
            if (!inInstallers) continue;
            // Each « - » of the Installers list starts an installer.
            if (entry.Groups["dash"].Success) installers.Add((null, null, null));
            if (installers.Count == 0) continue;
            installers[^1] = key switch
            {
                "Architecture" => installers[^1] with { Architecture = value },
                "InstallerUrl" => installers[^1] with { Url = value },
                "InstallerSha256" => installers[^1] with { Sha256 = value },
                _ => installers[^1],
            };
        }
        if (identifier != "Jellyfin.Server" || manifestVersion != version) return null;
        var (_, url, sha256) = installers.FirstOrDefault(x => x.Architecture == "x64");
        if (url is null || sha256 is null || !Sha256().IsMatch(sha256) || !Uri.TryCreate(url, UriKind.Absolute, out var address)) return null;
        if (address.Scheme != Uri.UriSchemeHttps || address.Host != "repo.jellyfin.org" || !address.IsDefaultPort || address.UserInfo.Length > 0 || address.Query.Length > 0 || address.Fragment.Length > 0
            || !InstallerName().IsMatch(Path.GetFileName(address.AbsolutePath))) return null;
        return new(version, address, sha256.ToUpperInvariant());
    }
    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'') ? value[1..^1] : value;
    }

    /// <summary>
    /// The release to offer over <paramref name="installed"/> (Jellyfin's own version), from winget's catalogue; null when
    /// none is newer. An <see cref="HttpRequestException"/> or <see cref="IOException"/> when GitHub cannot be read or
    /// gives something else than expected.
    /// </summary>
    public static async Task<JellyfinRelease?> FindAsync(Version installed, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(20);
        // GitHub's API answers nothing without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mira", JellyfinClient.AppVersion));
        using var listing = new HttpRequestMessage(HttpMethod.Get, Catalog);
        listing.Headers.Accept.ParseAdd("application/vnd.github+json");
        listing.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        string folders;
        using (var response = await http.SendAsync(listing, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            folders = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        using var json = JsonDocument.Parse(folders);
        if (Pick(Versions(json.RootElement), installed) is not { } version) return null;
        var manifest = await http.GetStringAsync(InstallerManifest(version), ct).ConfigureAwait(false);
        return ParseInstaller(manifest, version) is { } package ? new(ParseVersion(version)!, package)
            : throw new IOException(L($"Le catalogue de winget décrit Jellyfin {version} autrement que prévu : Mira ne le propose pas.", $"winget’s catalogue describes Jellyfin {version} differently than expected: Mira doesn’t offer it."));
    }

    [GeneratedRegex(@"^v?(\d{1,4})(?:\.(\d{1,4}))?(?:\.(\d{1,6}))?(?:\.\d{1,6})?$", RegexOptions.CultureInvariant)] private static partial Regex VersionText();
    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}(?:\.\d{1,6}){0,2}$", RegexOptions.CultureInvariant)] private static partial Regex FolderName();
    [GeneratedRegex(@"^(?<indent>\s*)(?<dash>-\s+)?(?<key>[A-Za-z0-9]+):\s*(?<value>.*)$", RegexOptions.CultureInvariant)] private static partial Regex YamlEntry();
    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex Sha256();
    [GeneratedRegex(@"^jellyfin_[0-9.]+_windows-x64\.exe$", RegexOptions.CultureInvariant)] private static partial Regex InstallerName();
}
