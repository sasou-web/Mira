using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The README's captures (--readme-gallery): the real window signed in to a real Jellyfin, the one of
    /// tools/gallery/setup.mjs (the demonstration library of tools/banner/media.sh and its TMDb artwork), named by MIRA_GALLERY_SERVER, _USER and _PASSWORD.
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
            async Task Shot(string name) { Keyboard.ClearFocus(); await ArtworkAsync(); await CaptureAsync(output, name, 2); }

            await Shot("windows-home");
            var film = _catalog.Resume.FirstOrDefault(x => x.Type == "Movie") ?? throw new InvalidOperationException("No film in « Continuer à regarder ».");
            await ShowDetailsAsync(film); await Shot("windows-film");
            DetailOverlay.Visibility = Visibility.Collapsed;
            ResetFilters(); _catalog.View = "Series"; await NavigateLibraryAsync();
            var series = _catalog.Items.FirstOrDefault(x => x.Type == "Series" && x.Name == "Arcane") ?? _catalog.Items.FirstOrDefault(x => x.Type == "Series") ?? throw new InvalidOperationException("No series in the library.");
            await ShowDetailsAsync(series);
            // Scrolled to its episodes, which the README's caption shows.
            DetailScroll.UpdateLayout(); SmoothScroll.Jump(DetailScroll, Math.Max(0, EpisodesHead.TranslatePoint(new Point(0, 0), DetailScroll).Y + DetailScroll.VerticalOffset - 90));
            await Shot("windows-series");
            DetailOverlay.Visibility = Visibility.Collapsed;
            ResetFilters(); _catalog.View = "Movie"; await NavigateLibraryAsync(); SmoothScroll.Jump(LibraryScroll);
            await Shot("windows-films");
            // The release banners' own screens (gallery.yml for banner.yml), never the README's.
            if (Environment.GetEnvironmentVariable("MIRA_GALLERY_BANNER") == "1") await BannerShotsAsync(output, Shot);
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
    /// <summary>
    /// The screens only a release's banner shows (tools/banner/plan.mjs): the films' filters open, a series' missing
    /// episodes at their place (Invincible's, from tools/web/fake-tvdb), the mini-player out against the right edge over
    /// Home, its controls shown, playing a film whose video gallery.yml made of its backdrop (libmpv as Mira installs it).
    /// </summary>
    private async Task BannerShotsAsync(string output, Func<string, Task> shot)
    {
        FiltersToggle_Click(this, new()); await shot("windows-filters"); CloseFilters();

        ResetFilters(); _catalog.View = "Series"; await NavigateLibraryAsync();
        var series = _catalog.Items.FirstOrDefault(x => x.Type == "Series" && x.Name == "Invincible") ?? throw new InvalidOperationException("No series named Invincible in the library.");
        await ShowDetailsAsync(series);
        for (var i = 0; i < 100 && _seriesVirtuals.Count == 0; i++) await Task.Delay(200);
        if (_seriesVirtuals.Count == 0) throw new InvalidOperationException("No missing episode of Invincible: is tools/web/fake-tvdb in Jellyfin's plugins?");
        await Task.Delay(300);
        DetailScroll.UpdateLayout(); SmoothScroll.Jump(DetailScroll, Math.Max(0, EpisodesHead.TranslatePoint(new Point(0, 0), DetailScroll).Y + DetailScroll.VerticalOffset - 90));
        await shot("windows-missing");
        DetailOverlay.Visibility = Visibility.Collapsed;

        _catalog.View = "home"; _catalog.ParentId = null; await NavigateLibraryAsync(); SmoothScroll.Jump(LibraryScroll);
        var film = _catalog.Resume.FirstOrDefault(x => x.Type == "Movie" && x.Name.Contains("Across the Spider-Verse", StringComparison.Ordinal))
            ?? _catalog.Resume.FirstOrDefault(x => x.Type == "Movie") ?? throw new InvalidOperationException("No film in « Continuer à regarder ».");
        _settings.Volume = 0; VolumeSlider.Value = 0;
        // The pointer resting on it, as far as the mini-player knows: it stays out, its controls with it.
        _miniPointerForCheck = () => true;
        try
        {
            await PlayAsync(film);
            for (var i = 0; i < 300 && (!_loaded || _videoWaits); i++) await Task.Delay(100);
            if (!_loaded || _mpv is null || _videoWaits) throw new InvalidOperationException("The film did not start: is libmpv in the profile (tools/mpv-engine.ps1)?");
            _mpv.Set("pause", "yes"); await Task.Delay(400);
            SetMiniPlayer(true); UpdateLayout(); await Task.Delay(600);
            if (!_miniPlayer || _miniTucked) throw new InvalidOperationException("The mini-player is not out against the edge.");
            ShowPlayerControls(); _lastPlayerInteraction = DateTimeOffset.UtcNow.AddMinutes(5);
            Keyboard.ClearFocus(); await ArtworkAsync();
            await CaptureWithVideoAsync(output, "windows-mini");
        }
        finally { _miniPointerForCheck = null; }
    }
    /// <summary>
    /// The window at twice its size with the mini-player's video in it: WPF does not draw mpv's own window, so mpv writes
    /// its frame, drawn where the video shows, under the player's surface and its buttons' row (a window of their own).
    /// </summary>
    private async Task CaptureWithVideoAsync(string directory, string name)
    {
        await CaptureAsync(directory, name, 2);
        var file = Path.Combine(directory, name + ".png"); var frameFile = Path.Combine(directory, name + "-frame.png");
        _mpv!.Command("screenshot-to-file", frameFile, "video");
        for (var i = 0; i < 100 && !File.Exists(frameFile); i++) await Task.Delay(100);
        await Task.Delay(300);
        static BitmapSource Read(string path)
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            return image;
        }
        var window = Read(file); var frame = Read(frameFile); File.Delete(frameFile);
        var root = Root; var size = new Size(root.ActualWidth, root.ActualHeight);
        var shell = new Rect(PlayerShell.TranslatePoint(new Point(), root), new Size(PlayerShell.ActualWidth, PlayerShell.ActualHeight));
        var surface = new Rect(PlayerSurface.TranslatePoint(new Point(), root), new Size(PlayerSurface.ActualWidth, PlayerSurface.ActualHeight));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(window, new Rect(size));
            dc.PushClip(new RectangleGeometry(shell, MiniRadius, MiniRadius));
            dc.DrawRectangle(Brushes.Black, null, shell);
            var scale = Math.Min(shell.Width / frame.PixelWidth, shell.Height / frame.PixelHeight);
            var shown = new Size(frame.PixelWidth * scale, frame.PixelHeight * scale);
            dc.DrawImage(frame, new Rect(new Point(shell.X + (shell.Width - shown.Width) / 2, shell.Y + (shell.Height - shown.Height) / 2), shown));
            dc.DrawRectangle(new VisualBrush(PlayerSurface), null, surface);
            if (PlayerTransport.IsVisible)
            {
                var at = InSurface(PlayerTransport, new Point());
                dc.DrawRectangle(new VisualBrush(PlayerTransport), null, new Rect(new Point(surface.X + at.X, surface.Y + at.Y), PlayerTransport.RenderSize));
            }
            dc.Pop();
        }
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX * 2), (int)Math.Ceiling(size.Height * dpi.DpiScaleY * 2), dpi.PixelsPerInchX * 2, dpi.PixelsPerInchY * 2, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file); encoder.Save(output);
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
