using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// One point of a version, as the "what's new" screen and the release page show it, in French and in English
/// (<see cref="Title"/> and <see cref="Text"/>: in Mira's language). <paramref name="Web"/>: Mira web shows it too,
/// once, on the phone (« Nouveau dans Mira », js/whats-new.js); Windows and the Mac show every point.
/// </summary>
public sealed record Highlight(string Icon, string FrenchTitle, string FrenchText, string EnglishTitle, string EnglishText, bool Web = false)
{
    public string Title => L(FrenchTitle, EnglishTitle);
    public string Text => L(FrenchText, EnglishText);
}
/// <summary>A version's main points: a one-line summary and a few highlights, never the full changelog.</summary>
public sealed record ReleaseHighlights(string Version, string FrenchSummary, string EnglishSummary, IReadOnlyList<Highlight> Items)
{
    public string Summary => L(FrenchSummary, EnglishSummary);
    public Version Number => Mira.Core.Updates.ReleaseFeed.ParseVersion(Version) ?? new(0, 0, 0);
}

/// <summary>What Mira shows on opening: the welcome on a new profile, the main points after an update, or nothing.</summary>
public enum StartupScreen { None, Welcome, WhatsNew }

/// <summary>
/// The main points of each version, from WhatsNew.json (built into Mira). The same text feeds the screen shown after an
/// update and the release page on GitHub; the details stay in CHANGELOG.md.
/// </summary>
public static class WhatsNew
{
    public static IReadOnlyList<ReleaseHighlights> All { get; } = Parse(Embedded());

    /// <summary>The entries of WhatsNew.json: French in "summary", "title", "text", English in their "en" (French without it).</summary>
    public static IReadOnlyList<ReleaseHighlights> Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<List<Entry>>(json, Json.Options) ?? [];
        return entries.Select(x => new ReleaseHighlights(x.Version ?? "", x.Summary ?? "", x.En?.Summary ?? x.Summary ?? "",
                (x.Items ?? []).Select(i => new Highlight(i.Icon ?? "", i.Title ?? "", i.Text ?? "", i.En?.Title ?? i.Title ?? "", i.En?.Text ?? i.Text ?? "", i.Web)).ToList()))
            .OrderByDescending(x => x.Number).ToList();
    }
    private sealed record Entry(string? Version, string? Summary, English? En, List<Point>? Items);
    private sealed record Point(string? Icon, string? Title, string? Text, bool Web, English? En);
    private sealed record English(string? Summary, string? Title, string? Text);
    /// <summary>
    /// The versions after <paramref name="seen"/> up to <paramref name="current"/>, newest first. Without a known
    /// <paramref name="seen"/> version, only <paramref name="current"/>'s.
    /// </summary>
    public static IReadOnlyList<ReleaseHighlights> Since(Version? seen, Version current, IReadOnlyList<ReleaseHighlights>? entries = null) =>
        (entries ?? All).Where(x => x.Number <= Trim(current) && (seen is null ? x.Number == Trim(current) : x.Number > Trim(seen))).ToList();
    /// <summary>
    /// A profile Mira has never opened gets the welcome; one that was used by an earlier version gets the main points
    /// of what it has not seen yet. <paramref name="knownProfile"/>: a session or settings already exist, so the
    /// version that wrote them predates the record of what was seen.
    /// </summary>
    public static StartupScreen Decide(string seenVersion, Version current, bool knownProfile, IReadOnlyList<ReleaseHighlights>? entries = null)
    {
        var seen = Mira.Core.Updates.ReleaseFeed.ParseVersion(seenVersion);
        if (seen is null && !knownProfile) return StartupScreen.Welcome;
        if (seen is not null && Trim(seen) >= Trim(current)) return StartupScreen.None;
        return Since(seen, current, entries).Count > 0 ? StartupScreen.WhatsNew : StartupScreen.None;
    }
    /// <summary>The release page's text on GitHub, in English like the README (the French stays in the app, in WhatsNew.json): what Mira is (SignPath Foundation
    /// wants the download page to describe the program), the summary, the highlights, how to install on Windows, on a Mac and on a phone, where the details are, and
    /// the "Code signing policy" line it asks for on every download page, with its credit only when the Windows files carry
    /// its signature (<paramref name="signed"/>). With <paramref name="banner"/>, the version's banner on top
    /// (docs/releases/x.y.z.jpg, drawn by tools/banner), shown from the release's tag, which never changes.</summary>
    public static string ReleaseNotes(ReleaseHighlights release, bool signed = true, bool banner = false)
    {
        var text = new StringBuilder();
        if (banner) text.Append("![Mira ").Append(release.Version).Append("](").Append(BannerUrl(release.Version)).Append(")\n\n");
        text.Append("**Mira** is a cinema-style Jellyfin client: a Windows app with mpv built in, its Mac version, and Mira web for iPhone, iPad and Android, served by your Jellyfin server.\n\n");
        text.Append(release.EnglishSummary).Append("\n\n");
        foreach (var item in release.Items) text.Append("- **").Append(item.EnglishTitle).Append("** — ").Append(item.EnglishText).Append('\n');
        text.Append("\n**On Windows**: `Mira-").Append(release.Version).Append("-win-x64-setup.exe`. Copies already installed update themselves.\n\n");
        text.Append("**On Mac** (Apple silicon, macOS 15 or later): `Mira-").Append(release.Version).Append("-mac-arm64.dmg`. The first launch asks for an authorisation: [install Mira on Mac (French)](https://github.com/sasou-web/Mira/blob/v")
            .Append(release.Version).Append("/docs/INSTALLATION-MAC.md).\n\n");
        text.Append("**On iPhone, iPad and Android**: Mira web, served by Jellyfin. In Mira for Windows, **Guide → Mira on your phone → Set everything up**: [Mira on iPhone, iPad and Android (French)](https://github.com/sasou-web/Mira/blob/v")
            .Append(release.Version).Append("/docs/INSTALLATION-WEB.md).\n\n");
        text.Append("Every detail: [CHANGELOG](https://github.com/sasou-web/Mira/blob/v").Append(release.Version).Append("/CHANGELOG.md)\n\n");
        text.Append("**Code signing policy**: ").Append(signed
            ? "Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org). This version's Windows files are signed; "
            : "this version's Windows files are not signed (application to SignPath Foundation not filed yet); ");
        text.Append("the details are in the [code signing policy](https://github.com/sasou-web/Mira#code-signing-policy).\n");
        return text.ToString();
    }
    /// <summary>Where GitHub serves a version's banner: the file at its tag.</summary>
    public static string BannerUrl(string version) => $"https://raw.githubusercontent.com/sasou-web/Mira/v{version}/docs/releases/{version}.jpg";
    private static Version Trim(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    private static string Embedded()
    {
        using var stream = typeof(WhatsNew).Assembly.GetManifestResourceStream("Mira.Core.WhatsNew.json")
            ?? throw new InvalidOperationException("WhatsNew.json is not built into Mira.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
