using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.Updates;

public sealed class UpdateException(string message, HttpStatusCode? status = null, bool network = false) : Exception(message)
{
    /// <summary>GitHub's answer when it refused a request: 404 when the release or the file does not exist, 403 or 429
    /// when it limits requests.</summary>
    public HttpStatusCode? Status { get; } = status;
    /// <summary>GitHub could not be reached, or did not answer in time: nothing it sent was refused.</summary>
    public bool Network { get; } = network;
}

/// <summary>One release file: "installer", "portable" or "zip" (Windows), "mac" or "jellyfin" (the other platforms),
/// with the size and SHA-256 the signature covers.</summary>
public sealed record UpdateFile(string Kind, string Name, long Size, string Sha256);

/// <summary>
/// mira-update.json, published with each release beside its files and signed by the maintainer (mira-update.json.sig).
/// Mira installs an update only when this manifest carries a valid signature from a key built into the application,
/// and only files whose size and SHA-256 match it: a release altered on GitHub, or in transit, is refused.
/// <see cref="Files"/> holds only the Windows files: copies of Mira up to 0.7.4 refuse a manifest with any other kind
/// there, and would never update again. The other platforms' files go in <see cref="Others"/>, a property those copies
/// ignore, and that copies from 0.7.5 read leniently (<see cref="Parse"/>): the Mac disk image and Mira web's Jellyfin
/// plugin, which Mira for Windows checks before Jellyfin installs it.
/// </summary>
public sealed record UpdateManifest(string Product, Version Version, IReadOnlyList<UpdateFile> Files)
{
    public const string AssetName = "mira-update.json", SignatureName = "mira-update.json.sig", ProductName = "Mira";
    public const long MaximumFileSize = 1L << 30;
    public static readonly string[] Kinds = ["installer", "portable", "zip"];
    public static readonly string[] OtherKinds = ["mac", "jellyfin"];
    public IReadOnlyList<UpdateFile> Others { get; init; } = [];
    /// <summary>The file of <paramref name="kind"/> ("mac" or "jellyfin") for this version, as the workflows name it.</summary>
    public static string OtherName(string kind, Version version) => kind switch
    {
        "mac" => $"Mira-{version.ToString(3)}-mac-arm64.dmg",
        "jellyfin" => $"mira-jellyfin-{version.ToString(3)}.zip",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
    // A bare file name: the name becomes a path in the profile's update folder.
    private static readonly Regex FileName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a manifest. "files" is always checked whole. "others" is read leniently by default: Windows updates parse
    /// this manifest too, and a later release's Mac or plugin entry this version would find wrong (a second one, a larger
    /// file) must not stop every installed copy from updating; an entry that cannot be used is skipped. With
    /// <paramref name="strict"/> (Mira web's check, the release tooling), such an entry refuses the manifest instead;
    /// a kind added later is skipped either way.
    /// </summary>
    public static UpdateManifest Parse(byte[] json, bool strict = false)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Text(JsonElement element, string name) => ReleaseFeed.Text(element, name);
            var product = Text(root, "product");
            var version = ReleaseFeed.ParseVersion(Text(root, "version")) ?? throw new UpdateException(L("Le manifeste de mise à jour n’indique pas de version valide.", "The update manifest doesn’t give a valid version."));
            if (product != ProductName) throw new UpdateException(L("Le manifeste de mise à jour ne concerne pas Mira.", "The update manifest isn’t for Mira."));
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) throw new UpdateException(L("Le manifeste de mise à jour ne liste aucun fichier.", "The update manifest lists no file."));
            var files = new List<UpdateFile>();
            foreach (var entry in list.EnumerateArray())
                files.Add(Entry(entry, Kinds, files) ?? throw Invalid());
            if (files.Count == 0) throw new UpdateException(L("Le manifeste de mise à jour ne liste aucun fichier.", "The update manifest lists no file."));
            var others = new List<UpdateFile>();
            if (root.TryGetProperty("others", out var more))
            {
                if (more.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in more.EnumerateArray())
                    {
                        // A kind added later: this version has no use for it.
                        if (entry.ValueKind == JsonValueKind.Object && !OtherKinds.Contains(ReleaseFeed.Text(entry, "kind"))) continue;
                        if (Entry(entry, OtherKinds, others) is { } file) others.Add(file);
                        else if (strict) throw Invalid();
                    }
                }
                else if (strict) throw Invalid();
            }
            return new UpdateManifest(product, version, files) { Others = others };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException(L("Le manifeste de mise à jour est illisible.", "The update manifest can’t be read.")); }
    }
    private static UpdateException Invalid() => new(L("Le manifeste de mise à jour contient un fichier invalide.", "The update manifest contains an invalid file."));
    /// <summary>One entry, if it is a valid file of one of <paramref name="kinds"/> not already in <paramref name="files"/>.</summary>
    private static UpdateFile? Entry(JsonElement entry, string[] kinds, List<UpdateFile> files)
    {
        var file = new UpdateFile(ReleaseFeed.Text(entry, "kind"), ReleaseFeed.Text(entry, "name"), ReleaseFeed.Number(entry, "size"), ReleaseFeed.Text(entry, "sha256").ToLowerInvariant());
        return kinds.Contains(file.Kind) && FileName.IsMatch(file.Name) && file.Size is > 0 and <= MaximumFileSize && Hex.IsMatch(file.Sha256) && !files.Any(f => f.Kind == file.Kind) ? file : null;
    }

    /// <summary>
    /// Manifest of the release files found in <paramref name="directory"/>, for the release tooling; with
    /// <paramref name="others"/>, also the Mac disk image and the Jellyfin plugin found there, both required.
    /// </summary>
    public static UpdateManifest Create(Version version, string directory, string? others = null)
    {
        var files = new List<UpdateFile>();
        foreach (var (kind, name) in new[] { ("installer", $"Mira-{version}-win-x64-setup.exe"), ("portable", $"Mira-{version}-win-x64-portable.exe"), ("zip", $"Mira-{version}-win-x64.zip") })
            if (Describe(kind, Path.Combine(directory, name)) is { } file) files.Add(file);
        if (files.Count == 0) throw new UpdateException(L($"Aucun fichier de la version {version} dans {directory}.", $"No file of version {version} in {directory}."));
        var more = new List<UpdateFile>();
        if (others is not null)
            foreach (var kind in OtherKinds)
                more.Add(Describe(kind, Path.Combine(others, OtherName(kind, version))) ?? throw new UpdateException(L($"{OtherName(kind, version)} manque dans {others}.", $"{OtherName(kind, version)} is missing from {others}.")));
        return new UpdateManifest(ProductName, version, files) { Others = more };
    }
    private static UpdateFile? Describe(string kind, string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return new UpdateFile(kind, Path.GetFileName(path), stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    /// <summary>"others" is written only when there are some: a Windows-only manifest stays as it always was.</summary>
    public byte[] ToJson()
    {
        static object Entry(UpdateFile f) => new { kind = f.Kind, name = f.Name, size = f.Size, sha256 = f.Sha256 };
        var options = new JsonSerializerOptions { WriteIndented = true };
        var files = Files.Select(Entry);
        return Others.Count == 0
            ? JsonSerializer.SerializeToUtf8Bytes(new { product = Product, version = Version.ToString(3), files }, options)
            : JsonSerializer.SerializeToUtf8Bytes(new { product = Product, version = Version.ToString(3), files, others = Others.Select(Entry) }, options);
    }
}

/// <summary>ECDSA P-256 over SHA-256, the 64-byte signature stored as base64 text. Keys are SubjectPublicKeyInfo, base64.</summary>
public static class UpdateSignature
{
    public static byte[] Sign(byte[] data, ECDsa key) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public static string Encode(byte[] signature) => Convert.ToBase64String(signature) + "\n";
    public static byte[]? Decode(string text)
    {
        try { var bytes = Convert.FromBase64String(text.Trim()); return bytes.Length == 64 ? bytes : null; }
        catch (FormatException) { return null; }
    }
    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static bool Verify(byte[] data, byte[] signature, IEnumerable<string> trustedKeys)
    {
        if (signature.Length != 64) return false;
        foreach (var key in trustedKeys)
        {
            using var ecdsa = ECDsa.Create();
            try { ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _); }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { continue; }
            if (ecdsa.KeySize == 256 && ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
        }
        return false;
    }
}

/// <summary>Public keys whose signatures Mira accepts for its updates. The private key never leaves the maintainer's PC
/// (tools/Mira.Release keeps it protected by Windows, outside the repository).</summary>
public static class UpdateKeys
{
    public static readonly IReadOnlyList<string> Trusted =
    [
        // Maintainer key created on 2026-09-29 (ECDSA P-256).
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkpHZt5DB4HdJvMnTsKOToLGllJljEJtRD4UPU7adK3Yt+InD+Bp9O+jwDbS+cq3TBweNNWmb6360lZDjestyCw=="
    ];
}
