using System.Security.Cryptography;

namespace Mira.Core.TorLink;

/// <summary>
/// The TorLink release Mira installs when it is turned on in the Downloads page: npm package "torlnk" (MIT, by bairon,
/// https://github.com/baairon/torlink) at one version, and each of its dependencies at the version of the package-lock.json
/// shipped inside Mira (TorLink/Lock, reviewed when it was pinned): npm ci installs exactly that tree and refuses any
/// archive whose sha512 differs from the lock's.
/// </summary>
public sealed record TorLinkPackage(string Version)
{
    public const string Name = "torlnk", VersionFile = "version.txt";
    public static readonly TorLinkPackage Current = new("1.9.0");
    /// <summary>Mira's own TorLink, beside its Node.js: "data\torlink".</summary>
    public static string Folder(string profile) => Path.Combine(profile, "torlink");
    /// <summary>A newer TorLink, prepared while the current one may be running; it takes its place at TorLink's next opening.</summary>
    public static string NextFolder(string profile) => Path.Combine(profile, "torlink-next");
    public static string PackageRoot(string profile) => PackageIn(Folder(profile));
    public static string PackageIn(string folder) => Path.Combine(folder, "node_modules", Name);
    /// <summary>The package.json and package-lock.json npm ci reads, written into the folder it installs.</summary>
    public static byte[] Manifest { get; } = Resource("package.json");
    public static byte[] Lock { get; } = Resource("package-lock.json");
    private static readonly string LockSha256 = Convert.ToHexString(SHA256.HashData(Lock)).ToLowerInvariant();
    /// <summary>What version.txt holds once npm ci followed this lock: the version and the lock's SHA-256. A TorLink
    /// installed by an older Mira (npm install, without a lock) has none and counts as not current.</summary>
    public string Stamp => $"{Version} {LockSha256}";
    public bool IsInstalledIn(string folder)
    {
        try { return File.Exists(Path.Combine(PackageIn(folder), "dist", "cli.cjs")) && File.ReadAllText(Path.Combine(folder, VersionFile)).Trim() == Stamp; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>
    /// npm's arguments, run in <paramref name="folder"/> where the manifest and the lock were written. Install scripts stay
    /// off: six packages declare one (TorLink's own WebRTC set-up, native builds of optional modules with JavaScript
    /// fallbacks, a "pnpm only" check), none of which TorLink needs on Windows (TCP, uTP and DHT peers).
    /// </summary>
    public static IReadOnlyList<string> NpmArguments(string npmCli, string folder) =>
        [npmCli, "ci", "--prefix", folder, "--omit=dev", "--ignore-scripts", "--no-fund", "--no-audit", "--no-update-notifier", "--loglevel=error"];
    /// <summary>The most useful line of npm's error output: its "npm error" summary, else the last line.</summary>
    public static string? NpmProblem(string output)
    {
        var lines = output.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var code = lines.FirstOrDefault(x => x.StartsWith("npm error code ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("npm ERR! code ", StringComparison.OrdinalIgnoreCase));
        var detail = lines.FirstOrDefault(x => (x.StartsWith("npm error ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("npm ERR! ", StringComparison.OrdinalIgnoreCase)) && x != code
            && !x.Contains("A complete log", StringComparison.OrdinalIgnoreCase));
        var text = code is null ? detail ?? lines.LastOrDefault() : code[(code.IndexOf("code ", StringComparison.OrdinalIgnoreCase) + 5)..] + (detail is null ? "" : " · " + Strip(detail));
        return text is null ? null : text.Length <= 220 ? text : text[..219] + "…";
        static string Strip(string line) => line.StartsWith("npm error ", StringComparison.OrdinalIgnoreCase) ? line[10..] : line.StartsWith("npm ERR! ", StringComparison.OrdinalIgnoreCase) ? line[9..] : line;
    }
    private static byte[] Resource(string name)
    {
        using var stream = typeof(TorLinkPackage).Assembly.GetManifestResourceStream("Mira.Core.TorLink." + name)
            ?? throw new InvalidOperationException("TorLink's " + name + " is missing from Mira.Core.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
