using System.Text;
using System.Text.Json;

namespace Mira.Core;

/// <summary>One point of a version, as the "what's new" screen and the release page show it.</summary>
public sealed record Highlight(string Icon, string Title, string Text);
/// <summary>A version's main points: a one-line summary and a few highlights, never the full changelog.</summary>
public sealed record ReleaseHighlights(string Version, string Summary, IReadOnlyList<Highlight> Items)
{
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

    public static IReadOnlyList<ReleaseHighlights> Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<List<ReleaseHighlights>>(json, Json.Options) ?? [];
        return entries.OrderByDescending(x => x.Number).ToList();
    }
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
    /// <summary>The release page's text on GitHub: the summary, the highlights, how to install on Windows, on a Mac and on a
    /// phone, and where the details are.</summary>
    public static string ReleaseNotes(ReleaseHighlights release)
    {
        var text = new StringBuilder();
        text.Append(release.Summary).Append("\n\n");
        foreach (var item in release.Items) text.Append("- **").Append(item.Title).Append("** — ").Append(item.Text).Append('\n');
        text.Append("\n**Sur Windows** : `Mira-").Append(release.Version).Append("-win-x64-setup.exe`. Les copies déjà installées se mettent à jour toutes seules.\n\n");
        text.Append("**Sur Mac** (puce Apple, macOS 14 ou plus) : `Mira-").Append(release.Version).Append("-mac-arm64.dmg`. La première ouverture demande une autorisation : [installer Mira sur Mac](https://github.com/sasou-web/Mira/blob/v")
            .Append(release.Version).Append("/docs/INSTALLATION-MAC.md).\n\n");
        text.Append("**Sur iPhone, iPad et Android** : Mira web, servie par Jellyfin. Dans Mira pour Windows, **Guide → Mira sur ton téléphone → Tout préparer** : [Mira sur iPhone, iPad et Android](https://github.com/sasou-web/Mira/blob/v")
            .Append(release.Version).Append("/docs/INSTALLATION-WEB.md).\n\n");
        text.Append("Tous les détails : [CHANGELOG](https://github.com/sasou-web/Mira/blob/v").Append(release.Version).Append("/CHANGELOG.md)\n");
        return text.ToString();
    }
    private static Version Trim(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build));
    private static string Embedded()
    {
        using var stream = typeof(WhatsNew).Assembly.GetManifestResourceStream("Mira.Core.WhatsNew.json")
            ?? throw new InvalidOperationException("WhatsNew.json is not built into Mira.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
