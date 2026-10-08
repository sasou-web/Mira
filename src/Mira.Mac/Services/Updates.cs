using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Mira.Mac.Views;

namespace Mira.Mac.Services;

/// <summary>
/// New versions of Mira for Mac: the latest release on GitHub that carries a Mac disk image. Mira says so once per
/// launch and opens the release page; macOS then installs it like any downloaded app.
/// </summary>
public static class Updates
{
    public const string Releases = "https://github.com/sasou-web/Mira/releases/latest";
    private const string Latest = "https://api.github.com/repos/sasou-web/Mira/releases/latest";
    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("html_url")] string Page, [property: JsonPropertyName("assets")] Asset[] Assets);
    private sealed record Asset([property: JsonPropertyName("name")] string Name);

    public static Version Current => typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The newer version and its page, or null (up to date, offline, or no Mac image in it).</summary>
    public static async Task<(Version Version, string Page)?> FindAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mira-Mac/{Current}");
        var release = await http.GetFromJsonAsync<Release>(Latest, ct);
        if (release is null || !Version.TryParse(release.Tag.TrimStart('v', 'V'), out var version)) return null;
        if (!release.Assets.Any(x => x.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))) return null;
        return version > Current ? (version, release.Page) : null;
    }

    public static async Task CheckAsync(MainWindow window, bool asked = false)
    {
        try
        {
            if (await FindAsync() is { } update) window.Notice($"Mira {update.Version} est disponible.", "Télécharger", () => Open(update.Page));
            else if (asked) window.Notice($"Mira {Current} est à jour.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        { if (asked) window.Notice("Impossible de vérifier les mises à jour pour le moment."); }
    }

    /// <summary>Opens a web page in the default browser.</summary>
    public static void Open(string url)
    {
        try
        {
            var start = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", [url]) : new System.Diagnostics.ProcessStartInfo("xdg-open", [url]);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
