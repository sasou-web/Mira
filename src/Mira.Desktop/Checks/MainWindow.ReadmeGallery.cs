using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The README's captures (--readme-gallery): the real window signed in to a real Jellyfin, the one of
    /// tools/gallery/setup.mjs (free films and their TMDb artwork), named by MIRA_GALLERY_SERVER, _USER and _PASSWORD.
    /// Drawn at twice the window's size, artwork decoded for it, for screens of any density.
    /// </summary>
    private async Task RunReadmeGalleryAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--readme-gallery") + 1]); Directory.CreateDirectory(output);
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD");
            if (_demo || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data"))
                throw new InvalidOperationException("The README's captures need MIRA_GALLERY_SERVER, _USER and _PASSWORD, an isolated --data profile and no demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); Width = 1440; Height = 900; _artworkScale = 2;
            Connection connection;
            using (var client = new JellyfinClient(new(server, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection);
            await ActivateConnectionAsync(connection);
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
            async Task Shot(string name) { await ArtworkAsync(); await CaptureAsync(output, name, 2); }

            await Shot("windows-home");
            var film = _catalog.Resume.FirstOrDefault(x => x.Type == "Movie") ?? throw new InvalidOperationException("No film in « Continuer à regarder ».");
            await ShowDetailsAsync(film); await Shot("windows-film");
            DetailOverlay.Visibility = Visibility.Collapsed;
            ResetFilters(); _catalog.View = "Series"; await NavigateLibraryAsync();
            var series = _catalog.Items.FirstOrDefault(x => x.Type == "Series") ?? throw new InvalidOperationException("No series in the library.");
            await ShowDetailsAsync(series); await Shot("windows-series");
            DetailOverlay.Visibility = Visibility.Collapsed;
            ResetFilters(); _catalog.View = "Movie"; await NavigateLibraryAsync(); SmoothScroll.Jump(LibraryScroll);
            await Shot("windows-films");
            Settings_Click(this, new()); SelectSettingsTab("subtitles"); await Shot("windows-subtitles");
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"PASS: Mira signed in to {server} as {user}.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }
    /// <summary>Every card on screen and the banner drawn, 20 s at most (a missing poster leaves its card as it is).</summary>
    private async Task ArtworkAsync()
    {
        await Dispatcher.InvokeAsync(LoadVisibleArtwork, DispatcherPriority.Loaded);
        await Task.Delay(300);
        var cards = new List<MediaCard>();
        void Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is MediaCard card) cards.Add(card); else Walk(child);
            }
        }
        Walk(this);
        try { await Task.WhenAll(cards.Select(x => x.ImageReady).Append(_heroReady)).WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { }
        await Task.Delay(1200);
    }
}
