using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Mira.Core.Updates;
using Mira.Mac.Views;

namespace Mira.Mac.Services;

/// <summary>
/// New versions of Mira for Mac: the latest release on GitHub that carries a Mac disk image. Mira says so once per
/// launch, unless « Rechercher les nouvelles versions » is off, and opens the release page; macOS then installs it like
/// any downloaded app.
/// </summary>
public static class Updates
{
    public const string Releases = "https://github.com/sasou-web/Mira/releases/latest";
    public static readonly Uri Project = new("https://github.com/sasou-web/Mira");
    private const string Latest = "https://api.github.com/repos/sasou-web/Mira/releases/latest";
    private sealed record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("html_url")] string Page, [property: JsonPropertyName("assets")] Asset[] Assets);
    private sealed record Asset([property: JsonPropertyName("name")] string Name);

    public static Version Current => typeof(Updates).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The newer version and its page, or null (up to date, offline, or no Mac image in it).</summary>
    public static async Task<(Version Version, Uri Page)?> FindAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Mira-Mac/{Current}");
        var release = await http.GetFromJsonAsync<Release>(Latest, ct);
        if (release is null || !Version.TryParse(release.Tag.TrimStart('v', 'V'), out var version)) return null;
        if (!release.Assets.Any(x => x.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))) return null;
        // Only Mira's own release pages are opened, as on Windows; any other address gives way to the latest release.
        return version > Current ? (version, ReleaseFeed.ReleasePage(release.Page) ?? new Uri(Releases)) : null;
    }

    /// <param name="asked">From « Rechercher une mise à jour »: GitHub is asked even with the automatic search off.</param>
    public static async Task CheckAsync(MainWindow window, bool asked = false)
    {
        // Off: nothing leaves for GitHub unless asked.
        if (!asked && !window.Settings.AutoUpdate) return;
        try
        {
            if (await FindAsync() is { } update) window.Notice($"Mira {update.Version} est disponible.", "Télécharger", () => Open(update.Page));
            else if (asked) window.Notice($"Mira {Current} est à jour.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        { if (asked) window.Notice("Impossible de vérifier les mises à jour pour le moment."); }
    }

    /// <summary>
    /// Opens a web page in the default browser: an HTTPS address only, or HTTP with <paramref name="plainHttp"/> (a
    /// Jellyfin at home or on Tailscale), given to macOS' open as a single argument.
    /// </summary>
    public static void Open(Uri page, bool plainHttp = false)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps && !(plainHttp && page.Scheme == Uri.UriSchemeHttp)) return;
        try
        {
            // By its full path, so that no other « open » found on the PATH runs; Linux is where Mira's checks run.
            var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(page.AbsoluteUri);
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
