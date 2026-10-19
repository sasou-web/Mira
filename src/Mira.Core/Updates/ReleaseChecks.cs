using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mira.Core.Updates;

/// <summary>
/// The files a release gets from the workflows (the Mac disk image, Mira web's plugin and the manifest Jellyfin installs
/// it from), checked the same way by the release tooling (tools/Mira.Release, before signing and before publishing) and
/// by Mira for Windows (before it points Jellyfin at a version of Mira web). Each refusal is an <see cref="UpdateException"/>.
/// </summary>
public static class ReleaseChecks
{
    public const string JellyfinManifestName = "jellyfin-manifest.json";

    /// <summary>
    /// jellyfin-manifest.json of release v<paramref name="version"/>: Mira's plugin alone, in that version alone,
    /// downloaded from that release, and the MD5 Jellyfin checks before installing it is <paramref name="zip"/>'s. One
    /// version only: Jellyfin installs the newest it is offered, and only this one was checked.
    /// </summary>
    public static void CheckJellyfinManifest(byte[] json, Version version, byte[] zip)
    {
        string? problem;
        try { problem = JellyfinManifestProblem(json, version, zip); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { problem = L("illisible", "unreadable"); }
        if (problem is not null) throw new UpdateException(L($"{JellyfinManifestName} de la version {version.ToString(3)} : {problem}.", $"{JellyfinManifestName} of version {version.ToString(3)}: {problem}."));
    }
    private static string? JellyfinManifestProblem(byte[] json, Version version, byte[] zip)
    {
        using var document = JsonDocument.Parse(json);
        var packages = document.RootElement;
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != 1) return L("une seule extension attendue", "a single plugin expected");
        var package = packages[0];
        if (!Guid.TryParse(ReleaseFeed.Text(package, "guid"), out var guid) || guid != MiraWeb.PluginId || ReleaseFeed.Text(package, "name") != MiraWeb.PluginName)
            return L("ce n’est pas l’extension Mira", "it isn’t the Mira plugin");
        if (package.ValueKind != JsonValueKind.Object || !package.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() != 1)
            return L("une seule version attendue", "a single version expected");
        var entry = versions[0];
        var expected = version.ToString(3) + ".0";
        if (ReleaseFeed.Text(entry, "version") is var listed && listed != expected) return L($"version « {listed} » au lieu de {expected}", $"version “{listed}” instead of {expected}");
        var source = UpdateClient.ReleaseFile(version, UpdateManifest.OtherName("jellyfin", version)).AbsoluteUri;
        if (ReleaseFeed.Text(entry, "sourceUrl") is var url && url != source) return L($"adresse « {url} » au lieu de {source}", $"address “{url}” instead of {source}");
        // Jellyfin's own check of the download, before it installs the plugin.
        if (!string.Equals(ReleaseFeed.Text(entry, "checksum"), Convert.ToHexString(MD5.HashData(zip)), StringComparison.OrdinalIgnoreCase))
            return L("le checksum n’est pas le MD5 de l’extension", "the checksum isn’t the plugin’s MD5");
        return null;
    }

    /// <summary>
    /// <paramref name="path"/> and the SHA-256 published beside it (<c>&lt;file&gt;.sha256</c>, as sha256sum and shasum
    /// write it: the hash, then the file's name) agree.
    /// </summary>
    public static void CheckSha256File(string path)
    {
        var name = Path.GetFileName(path);
        string[] line;
        try { line = File.ReadAllText(path + ".sha256", Encoding.ASCII).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new UpdateException(L($"{name}.sha256 est introuvable ou illisible.", $"{name}.sha256 is missing or unreadable.")); }
        string hash;
        try { using var stream = File.OpenRead(path); hash = Convert.ToHexString(SHA256.HashData(stream)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new UpdateException(L($"{name} est introuvable ou illisible.", $"{name} is missing or unreadable.")); }
        // In binary mode, shasum marks the name with a star.
        if (line.Length != 2 || line[1].TrimStart('*') != name || !string.Equals(line[0], hash, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(L($"{name}.sha256 ne correspond pas à {name}.", $"{name}.sha256 doesn’t match {name}."));
    }

    /// <summary>
    /// What the workflows attached to the draft of release v<paramref name="version"/>, downloaded into
    /// <paramref name="directory"/>: the disk image matches its SHA-256, jellyfin-manifest.json its plugin.
    /// </summary>
    public static void CheckOthers(Version version, string directory)
    {
        CheckSha256File(Path.Combine(directory, UpdateManifest.OtherName("mac", version)));
        var zip = Path.Combine(directory, UpdateManifest.OtherName("jellyfin", version));
        var manifest = Path.Combine(directory, JellyfinManifestName);
        foreach (var path in new[] { zip, manifest })
            if (!File.Exists(path)) throw new UpdateException(L($"{Path.GetFileName(path)} manque dans {directory}.", $"{Path.GetFileName(path)} is missing from {directory}."));
        CheckJellyfinManifest(File.ReadAllBytes(manifest), version, File.ReadAllBytes(zip));
    }
}
