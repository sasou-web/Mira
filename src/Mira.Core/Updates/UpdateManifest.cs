using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mira.Core.Updates;

public sealed class UpdateException(string message, HttpStatusCode? status = null) : Exception(message)
{
    /// <summary>GitHub's answer when it refused a request: 404 when the release or the file does not exist.</summary>
    public HttpStatusCode? Status { get; } = status;
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
/// ignore: the Mac disk image and Mira web's Jellyfin plugin, which Mira for Windows checks before Jellyfin installs it.
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

    public static UpdateManifest Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Text(JsonElement element, string name) => ReleaseFeed.Text(element, name);
            var product = Text(root, "product");
            var version = ReleaseFeed.ParseVersion(Text(root, "version")) ?? throw new UpdateException("Le manifeste de mise à jour n’indique pas de version valide.");
            if (product != ProductName) throw new UpdateException("Le manifeste de mise à jour ne concerne pas Mira.");
            if (!root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            var files = Entries(list, Kinds, unknown: false);
            if (files.Count == 0) throw new UpdateException("Le manifeste de mise à jour ne liste aucun fichier.");
            // A kind added later is skipped here, as this version skips "others" as a whole in older copies.
            var others = root.TryGetProperty("others", out var more) ? more.ValueKind == JsonValueKind.Array ? Entries(more, OtherKinds, unknown: true)
                : throw new UpdateException("Le manifeste de mise à jour contient un fichier invalide.") : [];
            return new UpdateManifest(product, version, files) { Others = others };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { throw new UpdateException("Le manifeste de mise à jour est illisible."); }
    }
    /// <summary>The entries of one list, each checked; one file per kind. With <paramref name="unknown"/>, a kind this
    /// version does not know is skipped instead of refused.</summary>
    private static List<UpdateFile> Entries(JsonElement list, string[] kinds, bool unknown)
    {
        var files = new List<UpdateFile>();
        foreach (var entry in list.EnumerateArray())
        {
            var file = new UpdateFile(ReleaseFeed.Text(entry, "kind"), ReleaseFeed.Text(entry, "name"), ReleaseFeed.Number(entry, "size"), ReleaseFeed.Text(entry, "sha256").ToLowerInvariant());
            if (unknown && entry.ValueKind == JsonValueKind.Object && !kinds.Contains(file.Kind)) continue;
            if (!kinds.Contains(file.Kind) || !FileName.IsMatch(file.Name) || file.Size is <= 0 or > MaximumFileSize || !Hex.IsMatch(file.Sha256) || files.Any(f => f.Kind == file.Kind))
                throw new UpdateException("Le manifeste de mise à jour contient un fichier invalide.");
            files.Add(file);
        }
        return files;
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
        if (files.Count == 0) throw new UpdateException($"Aucun fichier de la version {version} dans {directory}.");
        var more = new List<UpdateFile>();
        if (others is not null)
            foreach (var kind in OtherKinds)
                more.Add(Describe(kind, Path.Combine(others, OtherName(kind, version))) ?? throw new UpdateException($"{OtherName(kind, version)} manque dans {others}."));
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
