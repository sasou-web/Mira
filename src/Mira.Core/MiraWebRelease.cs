using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mira.Core.Updates;

namespace Mira.Core;

/// <summary>
/// Mira web as Mira for Windows has Jellyfin install it: the version of this Mira, checked against the release's signed
/// manifest before Jellyfin is pointed at it. Jellyfin itself checks only an MD5 that the same GitHub release gives it;
/// here the signature covers the plugin's size and SHA-256, the repository registered offers that version alone, and
/// the release must be immutable on GitHub, so that what Jellyfin downloads later is still what was checked here.
/// </summary>
public static class MiraWebRelease
{
    /// <summary>Mira's plugin is a few hundred kilobytes; anything far larger is not it, and is not read into memory.</summary>
    private const long MaximumPluginSize = 32L << 20;

    /// <summary>
    /// Checks Mira web <paramref name="version"/> on GitHub, through <paramref name="github"/> (which holds the trusted
    /// keys): the release is immutable, then mira-update.json and its signature, the plugin it lists (size, SHA-256), and
    /// the Jellyfin manifest that points to it (<see cref="ReleaseChecks.CheckJellyfinManifest"/>). Returns the repository
    /// to register in Jellyfin; an <see cref="UpdateException"/> says plainly why not, and nothing is to be registered then.
    /// </summary>
    public static async Task<string> VerifyAsync(UpdateClient github, Version version, CancellationToken ct = default)
    {
        var number = version.ToString(3);
        UpdateException NotPublished() => new($"GitHub n’a pas de Mira web signé pour Mira {number} (c’est le cas d’une copie de développement) : rien n’est installé.");
        UpdateException Altered() => new($"Mira web {number} ne correspond pas à la version signée par Mira : il n’est pas installé.");
        // What GitHub's answer means here. Only an unreachable GitHub is about this PC's Internet access; GitHub's own
        // refusals (a rate limit above all) say what they are; a release file larger than signed, or sent anywhere but
        // GitHub's file servers, is not the release Mira signed.
        UpdateException Failure(UpdateException ex, bool file) => ex switch
        {
            { Status: HttpStatusCode.NotFound } => NotPublished(),
            { Network: true } => new("Mira n’a pas pu vérifier Mira web sur GitHub : vérifie que ce PC a accès à Internet, puis réessaie."),
            { Status: not null } => ex,
            _ => file ? Altered() : ex,
        };
        async Task<byte[]> Fetch(string name, long limit)
        {
            try { return await github.ReleaseFileAsync(version, name, (int)limit, ct).ConfigureAwait(false); }
            catch (UpdateException ex) { throw Failure(ex, file: true); }
        }

        // A release that can still change on GitHub could offer Jellyfin, at its next update or reinstall, another plugin
        // than the one checked below: Jellyfin only checks an MD5 from that same release.
        bool immutable;
        try { immutable = await github.ImmutableAsync(version, ct).ConfigureAwait(false); }
        catch (UpdateException ex) { throw Failure(ex, file: false); }
        if (!immutable) throw new UpdateException($"La release {number} de Mira peut encore être modifiée sur GitHub : Mira web n’est pas installé depuis elle, car Jellyfin pourrait y trouver plus tard une autre extension que celle vérifiée.");

        var manifestBytes = await Fetch(UpdateManifest.AssetName, 64 * 1024).ConfigureAwait(false);
        var signature = UpdateSignature.Decode(Encoding.ASCII.GetString(await Fetch(UpdateManifest.SignatureName, 4096).ConfigureAwait(false)));
        if (!github.Signed(manifestBytes, signature)) throw new UpdateException($"Mira web {number} n’est pas signé par Mira : il n’est pas installé.");
        UpdateManifest manifest;
        // Strict: this release's own manifest, written by this version's tooling, has nothing to skip.
        try { manifest = UpdateManifest.Parse(manifestBytes, strict: true); }
        catch (UpdateException) { throw Altered(); }
        if (manifest.Version != version) throw Altered();
        // Releases up to 0.7.4 signed the Windows files only.
        var plugin = manifest.Others.FirstOrDefault(f => f.Kind == "jellyfin") ?? throw NotPublished();
        if (plugin.Name != UpdateManifest.OtherName("jellyfin", version) || plugin.Size > MaximumPluginSize) throw Altered();

        var zip = await Fetch(plugin.Name, plugin.Size).ConfigureAwait(false);
        if (zip.Length != plugin.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(zip)), plugin.Sha256, StringComparison.OrdinalIgnoreCase)) throw Altered();
        var jellyfinManifest = await Fetch(ReleaseChecks.JellyfinManifestName, 64 * 1024).ConfigureAwait(false);
        try { ReleaseChecks.CheckJellyfinManifest(jellyfinManifest, version, zip); }
        catch (UpdateException) { throw Altered(); }
        return MiraWeb.RepositoryFor(version);
    }
}
