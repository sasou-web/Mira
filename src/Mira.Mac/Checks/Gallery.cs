using Avalonia.Controls.ApplicationLifetimes;
using Mira.Core;
using Mira.Mac.Views;

namespace Mira.Mac.Checks;

/// <summary>
/// Mira --gallery &lt;folder&gt;: the window signed in to a real Jellyfin (the README's library of tools/gallery/setup.mjs,
/// MIRA_GALLERY_SERVER, _USER and _PASSWORD), each screen captured as Avalonia draws it (mac-home, mac-film,
/// mac-series, mac-films, mac-settings). For the release banners (tools/banner); on Linux under a virtual display too,
/// where AVALONIA_GLOBAL_SCALE_FACTOR=2 gives a Retina window. gallery-result.txt says PASS or FAIL.
/// </summary>
public static class Gallery
{
    public static async Task RunAsync(MainWindow window)
    {
        var args = window.Args;
        var output = Path.GetFullPath(args[Array.IndexOf(args, "--gallery") + 1]); Directory.CreateDirectory(output);
        var exit = 1;
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD");
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !args.Contains("--data"))
                throw new InvalidOperationException("The gallery needs MIRA_GALLERY_SERVER, _USER and _PASSWORD and an isolated --data profile.");
            window.Width = 1440; window.Height = 900;
            window.ShowLogin();
            await Until(() => window.Login is not null, 10);
            await window.Login!.SignInAsync(server, user, password);
            if (window.Session is null) throw new InvalidOperationException("Not signed in: " + window.Login?.Error);
            async Task Shot(string name) { await Task.Delay(2500); SelfCheck.CaptureWindow(window, output, name); }

            if (!await Until(() => window.Current is HomePage { Latest.Count: > 0 }, 30)) throw new InvalidOperationException("Home stayed empty.");
            var home = (HomePage)window.Current!;
            await Shot("mac-home");
            var film = home.Continue.FirstOrDefault(x => x.Type == "Movie") ?? home.Latest.First(x => x.Type == "Movie");
            window.Open(film);
            await Until(() => window.Current is DetailPage, 15); await Shot("mac-film");
            window.Go("Series");
            await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15);
            var series = home.Continue.FirstOrDefault(x => x.Type == "Episode") is { SeriesId: { } id } ? ((BrowsePage)window.Current!).Items.FirstOrDefault(x => x.Id == id) : null;
            window.Open(series ?? ((BrowsePage)window.Current!).Items.First());
            await Until(() => window.Current is DetailPage { Episodes.Count: > 0 }, 20); await Shot("mac-series");
            window.Go("Movie");
            await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15); await Shot("mac-films");
            window.Go("settings"); await Shot("mac-settings");
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"PASS: Mira signed in to {server} as {user}.");
            exit = 0;
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}"); }
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
    }

    private static async Task<bool> Until(Func<bool> condition, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(100); }
        return condition();
    }
}
