namespace Mira.Core.TorLink;

/// <summary>
/// The TorLink release Mira installs when it is turned on in the Downloads page: npm package "torlnk" (MIT, by bairon,
/// https://github.com/baairon/torlink), fixed at one version that npm checks against the registry's integrity record.
/// </summary>
public sealed record TorLinkPackage(string Version)
{
    public const string Name = "torlnk";
    public static readonly TorLinkPackage Current = new("1.9.0");
    /// <summary>Mira's own TorLink, beside its Node.js: "data\torlink".</summary>
    public static string Folder(string profile) => Path.Combine(profile, "torlink");
    public static string PackageRoot(string profile) => Path.Combine(Folder(profile), "node_modules", Name);
    /// <summary>
    /// npm's arguments. Install scripts stay off: the only one builds an optional WebRTC module, which needs a C++
    /// toolchain on Windows and which TorLink does without (TCP, uTP and DHT peers).
    /// </summary>
    public IReadOnlyList<string> NpmArguments(string npmCli, string profile) =>
        [npmCli, "install", $"{Name}@{Version}", "--prefix", Folder(profile), "--omit=dev", "--ignore-scripts", "--no-fund", "--no-audit", "--no-update-notifier", "--loglevel=error"];
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
}
