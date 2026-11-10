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
            // The presentation video's own (MIRA_GALLERY_MORE, never merged): each on its own, one missing is one picture less.
            if (Environment.GetEnvironmentVariable("MIRA_GALLERY_MORE") is { Length: > 0 })
            {
                async Task More(string name, Func<Task> shots)
                {
                    try { await shots(); }
                    catch (Exception ex) { await File.AppendAllTextAsync(Path.Combine(output, "gallery-more.txt"), $"{name}: {ex.GetType().Name}: {ex.Message}\n"); }
                }
                await More("home", async () =>
                {
                    window.Go("home"); await Until(() => window.Current is HomePage { Latest.Count: > 0 }, 15); await Shot("mac-home-2");
                    ((HomePage)window.Current!).ScrollToEnd(); await Shot("mac-home-end"); ((HomePage)window.Current!).ScrollToHome();
                });
                foreach (var (title, name) in new[] { ("Spider-Man: Across the Spider-Verse", "mac-verse"), ("Spring", "mac-spring"), ("Spirited Away", "mac-spirited") })
                    await More(name, async () =>
                    {
                        window.Go("Movie"); await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15);
                        var found = ((BrowsePage)window.Current!).Items.FirstOrDefault(x => x.Name.Replace(":", "").StartsWith(title.Replace(":", ""), StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No " + title);
                        window.Open(found); await Until(() => window.Current is DetailPage, 15); await Shot(name);
                    });
                await More("shows", async () => { window.Go("Series"); await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15); await Shot("mac-shows"); });
                await More("search", async () =>
                {
                    window.Go("search"); await Until(() => window.Current is BrowsePage, 10); await Shot("mac-search-empty");
                    ((BrowsePage)window.Current!).Search("spider"); await Until(() => window.Current is BrowsePage { Items.Count: > 0 }, 15); await Shot("mac-search");
                });
                await More("whatsnew", async () =>
                {
                    window.Go("home"); await Until(() => window.Current is HomePage { Latest.Count: > 0 }, 15); await Task.Delay(1500);
                    window.ShowWhatsNew(WhatsNew.Recent(Mira.Mac.Services.Updates.Current));
                    await Until(() => window.CurrentSheet is { IsVisible: true }, 5); await Shot("mac-whatsnew");
                });
            }
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
