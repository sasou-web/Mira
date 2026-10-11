using Mira.Core.Updates;

namespace Mira.Core;

/// <summary>
/// Mira web: the app for phones, served by Jellyfin itself at /Mira through Mira's Jellyfin plugin. Each Mira release
/// attaches the plugin and the repository manifest that Jellyfin installs and updates it from.
/// </summary>
public static class MiraWeb
{
    public static readonly Guid PluginId = new("4ea89259-3350-45b0-8045-f1ab627214dd");
    public const string PluginName = "Mira";
    /// <summary>
    /// Always the latest release's manifest, from which Jellyfin installs every new version by itself, trusting GitHub
    /// alone: the manual route of docs/INSTALLATION-WEB.md, and what Mira for Windows registered up to 0.7.4.
    /// </summary>
    public const string LatestRepository = "https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json";
    public const string RepositoryName = "Mira";
    /// <summary>
    /// The manifest of one release, which only ever offers that version: what Mira for Windows registers, once it has
    /// checked that version against the release's signed manifest (<see cref="MiraWebRelease"/>).
    /// </summary>
    public static string RepositoryFor(Version version) => UpdateClient.ReleaseFile(version, ReleaseChecks.JellyfinManifestName).AbsoluteUri;
    /// <summary>Whether a repository registered in Jellyfin is one of Mira's: the latest release, or one of them.</summary>
    public static bool IsRepository(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host == "github.com"
        && uri.AbsolutePath.StartsWith("/sasou-web/Mira/releases/", StringComparison.OrdinalIgnoreCase);
    /// <summary>The version a repository of Mira's offers alone (<see cref="RepositoryFor"/>); null for any other, the
    /// latest release's included.</summary>
    public static Version? PinnedVersion(string? url)
    {
        if (!IsRepository(url)) return null;
        var path = new Uri(url!).AbsolutePath.Split('/');
        // /sasou-web/Mira/releases/download/vX.Y.Z/jellyfin-manifest.json
        return path is [_, _, _, _, "download", var tag, ReleaseChecks.JellyfinManifestName] ? ReleaseFeed.ParseVersion(tag) : null;
    }

    /// <summary>
    /// The page with the QR code for phones, at an address of the server they can reach; with
    /// <paramref name="everywhere"/> (this PC on Tailscale), the code is for that one first.
    /// </summary>
    public static string SharePage(string server, string? everywhere = null) =>
        server.TrimEnd('/') + "/Mira/#/partager" + (everywhere is null ? "" : "?partout=" + Uri.EscapeDataString(everywhere.TrimEnd('/')));
}

/// <summary>How installing Mira web on Jellyfin went.</summary>
public enum WebAppInstall
{
    /// <summary>Installed, Jellyfin restarted, and /Mira answers.</summary>
    Ready,
    /// <summary>Installed, but Jellyfin has not answered again yet: it will serve /Mira once restarted.</summary>
    Restarting,
    /// <summary>This account may not install plugins (not an administrator of Jellyfin).</summary>
    NotAllowed,
    /// <summary>Jellyfin found no version for itself in Mira's repository (no Internet, or a Jellyfin too old).</summary>
    Unavailable,
}
