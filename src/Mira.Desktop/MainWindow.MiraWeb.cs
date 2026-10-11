using Mira.Core;
using Mira.Core.Updates;

namespace Mira.Desktop;

/// <summary>
/// Mira web on Jellyfin follows this Mira: the guide installs it, and after Mira updates itself, the next start moves
/// Jellyfin's repository of Mira web forward to the new version, which Jellyfin then installs as its own updates. Jellyfin
/// is only ever pointed at a release that Mira checked against its signed manifest.
/// </summary>
public partial class MainWindow
{
    /// <summary>The repository of Mira web for this Mira, checked on GitHub; an <see cref="UpdateException"/> says plainly why not.</summary>
    private static async Task<string> VerifiedMiraWebAsync()
    {
        var version = ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? throw new UpdateException("Cette copie de Mira n’a pas de numéro de version : Mira web ne peut pas être vérifié.");
        using var github = new UpdateClient(version, UpdateKeys.Trusted);
        return await MiraWebRelease.VerifyAsync(github, version);
    }

    /// <summary>
    /// At startup, where this Jellyfin has Mira's repository (from the guide, or added by hand) and this account manages
    /// its plugins: the repository moves forward to this version's checked one, never back, and nothing is installed or
    /// restarted (<see cref="JellyfinClient.KeepWebAppCurrentAsync"/>). Jellyfin's own update task installs the new
    /// version, under its own rules: a plugin uninstalled, disabled or kept from updating by the administrator stays so.
    /// Silent: not allowed, offline or not published, it is tried again at the next start; once done, not again for this
    /// version and this server.
    /// </summary>
    private async Task KeepMiraWebCurrentAsync()
    {
        if (_demo || _client is not { } client || ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) is not { } version) return;
        var done = $"{version.ToString(3)} {client.Connection.Server}";
        if (_settings.MiraWebUpdated == done) return;
        try
        {
            if (!await client.KeepWebAppCurrentAsync(version, VerifiedMiraWebAsync)) return;
            _settings.MiraWebUpdated = done; _profile.SaveSettings(_settings);
        }
        catch (Exception ex) when (ex is UpdateException || IsExpected(ex)) { }
    }
}
