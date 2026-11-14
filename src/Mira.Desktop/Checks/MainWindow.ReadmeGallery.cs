using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;
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
        var series = _catalog.Items.FirstOrDefault(x => x.Type == "Series" && string.Equals(x.Name, "Invincible", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No series named Invincible in the library.");
        await ShowDetailsAsync(series);
        for (var i = 0; i < 100 && _seriesVirtuals.Count == 0; i++) await Task.Delay(200);
        if (_seriesVirtuals.Count == 0) throw new InvalidOperationException("No missing episode of Invincible: is tools/web/fake-tvdb in Jellyfin's plugins?");
        await Task.Delay(300);
        DetailScroll.UpdateLayout(); SmoothScroll.Jump(DetailScroll, Math.Max(0, EpisodesHead.TranslatePoint(new Point(0, 0), DetailScroll).Y + DetailScroll.VerticalOffset - 90));
        await shot("windows-missing");
        DetailOverlay.Visibility = Visibility.Collapsed;

        await DownloadsShotAsync(shot);

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
    /// The Downloads page « Régie » (windows-downloads) as the server PC of a real home would fill it, on the README's
    /// library: the posters are this Jellyfin's, the releases' names, sizes, speeds and dates those of real downloads of
    /// such titles (no relay is asked; the banner's videos are tiny black files, never their sizes).
    /// </summary>
    private async Task DownloadsShotAsync(Func<string, Task> shot)
    {
        ResetFilters(); _catalog.View = "Series"; await NavigateLibraryAsync();
        var series = _catalog.Items.Where(x => x.Type == "Series").ToList();
        ResetFilters(); _catalog.View = "Movie"; await NavigateLibraryAsync();
        var films = _catalog.Items.Where(x => x.Type == "Movie").ToList();
        static MediaItem Named(List<MediaItem> items, string name) =>
            items.FirstOrDefault(x => x.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"No title named {name} in the library.");
        var (invincible, samurai, robots) = (Named(series, "Invincible"), Named(series, "Blue Eye Samurai"), Named(series, "Love, Death"));
        var spirited = Named(films, "Spirited Away");
        const long GiB = 1L << 30, MiB = 1L << 20, TiB = 1L << 40;
        var now = DateTimeOffset.UtcNow;
        static string Hash(char c) => new(c, 40);
        const string taken = "Invincible.S01E04.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX";
        const string pack = "Love.Death.and.Robots.S01.1080p.NF.WEB-DL.DDP5.1.x264-NTb";
        List<RelayFollow> follows =
        [
            new("f1", invincible.Id, "Invincible", null, now.AddDays(-21), now.AddMinutes(-25), now.AddHours(2), false, new FollowNote("found", "S01E05"), "S01E03",
                [new("S01E04", taken, now.AddMinutes(-25))], [new("p1", "S01E05", "Invincible.S01E05.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX", 1652 * MiB, "waiting", now.AddMinutes(7), null)], null),
            new("f2", samurai.Id, "Blue Eye Samurai", null, now.AddDays(-48), now.AddHours(-3), now.AddHours(3), false, new FollowNote("nothing", "S01E03"), "S01E03",
                [new("S01E03", "Blue.Eye.Samurai.S01E03.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX", now.AddDays(-9))], [], new RelayAiring("S02E01", now.AddDays(4))),
        ];
        var status = new RelayStatus(new("LIVING-ROOM-PC", true, now, true, null, Follows: true, Replaces: true, Seasons: true, Free: 4 * TiB + 6 * TiB / 10),
            [new("r1", "Spirited.Away.2001.2160p.UHD.BluRay.x265.10bit.HDR.DTS-HD.MA.5.1-SWTYBLZ", 31 * GiB + 4 * GiB / 10, Hash('b'), "waiting", null, now.AddMinutes(-2), spirited.Id)], [],
            [new(Hash('a'), taken, "downloading", 64, 4 * MiB + MiB / 10, 37, 1618 * MiB, 1036 * MiB, 142),
             new(Hash('e'), pack, "downloading", 23, 8 * MiB + 4 * MiB / 10, 21, 9 * GiB + 8 * GiB / 10, 2 * GiB + 3 * GiB / 10, 940),
             new(Hash('c'), "Ponyo.2008.1080p.BluRay.x264.DTS-HD.MA.5.1-SbR", "paused", 63, 0, 0, 10 * GiB + 2 * GiB / 10, 6 * GiB + 4 * GiB / 10)],
            [.. films.Where(x => x.Name is "Flow" or "The Wild Robot" or "Suzume" or "Klaus").Select((x, i) => new RelayPlaced($"pl{i}", x.Name, L("disponible dans Jellyfin", "available in Jellyfin"), x.Id, true)),
             new("pl-inv", "Invincible · S01E03", L("disponible dans Jellyfin", "available in Jellyfin"), invincible.Id, true)],
            follows, [new("m1", robots.Id, 1, [], pack, 9 * GiB + 8 * GiB / 10, Hash('e'), "sent", null, now.AddMinutes(-40))]);
        TorLinkOverlay.Visibility = Visibility.Visible; TorLinkOverlay.Opacity = 1;
        ShowServerBoardForCheck(status, now);
        // Every poster asked of Jellyfin, then drawn.
        var ids = new[] { invincible.Id, samurai.Id, robots.Id, spirited.Id }.Concat(status.Placed.Select(x => x.ItemId!)).ToList();
        for (var i = 0; i < 100 && ids.Any(x => !_serverPosters.ContainsKey(x)); i++) await Task.Delay(100);
        await Task.Delay(1500);
        await shot("windows-downloads");
        HideServerDownloads(); TorLinkOverlay.Visibility = Visibility.Collapsed;
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
