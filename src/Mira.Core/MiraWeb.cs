namespace Mira.Core;

/// <summary>
/// Mira web: the app for phones, served by Jellyfin itself at /Mira through Mira's Jellyfin plugin. Each Mira release
/// attaches the plugin and the repository manifest that Jellyfin installs and updates it from.
/// </summary>
public static class MiraWeb
{
    public static readonly Guid PluginId = new("4ea89259-3350-45b0-8045-f1ab627214dd");
    public const string PluginName = "Mira";
    /// <summary>Always the latest release's manifest: Jellyfin keeps the plugin up to date from it.</summary>
    public const string Repository = "https://github.com/sasou-web/Mira/releases/latest/download/jellyfin-manifest.json";
    public const string RepositoryName = "Mira";

    /// <summary>The page with the QR code for phones, at an address of the server they can reach.</summary>
    public static string SharePage(string server) => server.TrimEnd('/') + "/Mira/#/partager";
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
