using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Mira.Jellyfin;

/// <summary>
/// The web app's files: built into the plugin, or read from the folder named by MIRA_WEB_DIR while developing it.
/// Assets are served under a revision (a hash of every file), so a phone keeps them until Mira changes, then
/// fetches the new ones at once; index.html and the manifest name that revision.
/// </summary>
public sealed class WebFiles
{
    public sealed record WebFile(byte[] Content, string ContentType, string ETag);

    private const string Prefix = "web/";
    private const string RevisionMark = "{{rev}}";
    private const string VersionMark = "{{version}}";
    private static readonly Lazy<WebFiles> Embedded = new(LoadEmbedded);
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".webmanifest"] = "application/manifest+json; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".woff2"] = "font/woff2",
        [".txt"] = "text/plain; charset=utf-8",
    };
    private readonly Dictionary<string, WebFile> _files;

    private WebFiles(Dictionary<string, byte[]> contents)
    {
        Revision = RevisionOf(contents);
        _files = new(StringComparer.Ordinal);
        foreach (var (path, content) in contents)
        {
            if (!Types.TryGetValue(Path.GetExtension(path), out var type)) continue;
            // Pages that name the assets carry the revision; text files are otherwise served as they are.
            var bytes = path is "index.html" or "manifest.webmanifest"
                ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(content)
                    .Replace(RevisionMark, Revision, StringComparison.Ordinal).Replace(VersionMark, Version, StringComparison.Ordinal))
                : content;
            _files[path] = new(bytes, type, "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..16] + "\"");
        }
    }

    /// <summary>Mira's version, as the app shows it in its settings.</summary>
    public static string Version { get; } = typeof(WebFiles).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>12 hexadecimal characters that change whenever any file of the app changes.</summary>
    public string Revision { get; }

    /// <summary>Whether assets may be kept by the phone until the revision changes (not while developing).</summary>
    public bool Immutable { get; private init; } = true;

    public static WebFiles Current => Environment.GetEnvironmentVariable("MIRA_WEB_DIR") is { Length: > 0 } folder
        ? FromFolder(folder)
        : Embedded.Value;

    public bool TryGet(string path, out WebFile file) => _files.TryGetValue(path, out file!);

    private static string RevisionOf(Dictionary<string, byte[]> contents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, content) in contents.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\n"));
            hash.AppendData(content);
        }
        return Convert.ToHexString(hash.GetHashAndReset())[..12].ToLowerInvariant();
    }

    private static WebFiles LoadEmbedded()
    {
        var assembly = typeof(WebFiles).Assembly;
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // MSBuild names resources with the build machine's separator: web\js\app.js on Windows.
            var path = name.Replace('\\', '/');
            if (!path.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            contents[path[Prefix.Length..]] = Read(assembly, name);
        }
        return new WebFiles(contents);
    }

    private static byte[] Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static WebFiles FromFolder(string folder)
    {
        var root = Path.GetFullPath(folder);
        var contents = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        return new WebFiles(contents) { Immutable = false };
    }
}
