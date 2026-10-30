using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The presentation video's pictures (--demo-shots &lt;folder&gt;), for a montage that scrolls, zooms and plays them
    /// at 60 frames a second: each page of the real window (1920 × 1080, signed in as --readme-gallery) drawn whole at
    /// twice its size (the scrolled content in one picture; the rail and the title bar apart, with transparency), each
    /// card under the pointer, the search field as it is typed, and the player's controls over Spring (the film itself
    /// comes from its file). Positions in shots.json, in units of the window.
    /// </summary>
    private async Task RunDemoShotsAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--demo-shots") + 1]); Directory.CreateDirectory(output);
        var manifest = new Dictionary<string, object>(); var pages = new Dictionary<string, object>(); manifest["pages"] = pages;
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD");
            if (_demo || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data"))
                throw new InvalidOperationException("The shots need MIRA_GALLERY_SERVER, _USER and _PASSWORD, an isolated --data profile and no demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); _artworkScale = 2; _settings.HeroAutoPlay = false;
            Connection connection;
            using (var client = new JellyfinClient(new(server, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection);
            await ActivateConnectionAsync(connection);
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal; Left = 0; Top = 0; Width = 1920; Height = 1080; Activate();
            SetCursorPos(1919, 1079); await Task.Delay(1500); await ArtworkAsync();
            var root = (FrameworkElement)Content;
            manifest["window"] = new { width = root.ActualWidth, height = root.ActualHeight, scale = 2 };
            manifest["nav"] = new { home = Rounded(Box(HomeNav, root)), search = Rounded(Box(SearchNav, root)), movies = Rounded(Box(MoviesNav, root)), series = Rounded(Box(SeriesNav, root)) };

            // Home, and its banner on the next titles.
            var home = await PageShotAsync(output, "home", LibraryScroll, 14, [HeroPlay]);
            var content = (FrameworkElement)LibraryScroll.Content; var banners = new List<object>();
            for (var i = 1; i < Math.Min(4, _heroCandidates.Count); i++)
            {
                RenderHero(_heroCandidates[i]); await _heroReady; await Task.Delay(1600);
                banners.Add(new { name = _hero?.Name, file = SaveShot(output, $"home-banner-{i}.jpg", RenderArea(content, Box(Hero, content), Background)) });
            }
            RenderHero(_heroCandidates[0]); await _heroReady; await Task.Delay(1200);
            home["banners"] = banners; home["banner"] = Rounded(Box(Hero, content)); pages["home"] = home;

            await BrowseAsync("Series");
            pages["series"] = await PageShotAsync(output, "series", LibraryScroll, 6);
            var arcane = _catalog.Items.FirstOrDefault(x => x.Name == "Arcane") ?? throw new InvalidOperationException("No Arcane in Shows.");
            await ShowDetailsAsync(arcane); await DetailsAsync();
            pages["arcane"] = await PageShotAsync(output, "arcane", DetailScroll, 10, [DetailPlay, .. Descendants<Button>(EpisodesPanel).Where(x => x.IsVisible && x.ActualWidth > 200).Take(4)]);
            await CloseDetailsAsync();

            await BrowseAsync("Movie");
            pages["movies"] = await PageShotAsync(output, "movies", LibraryScroll, 24);
            var verse = _catalog.Items.FirstOrDefault(x => x.Name.Contains("Across the Spider-Verse")) ?? throw new InvalidOperationException("No Spider-Verse in Movies.");
            var spring = _catalog.Items.FirstOrDefault(x => x.Name == "Spring") ?? throw new InvalidOperationException("No Spring in Movies.");
            await ShowDetailsAsync(verse); await DetailsAsync();
            pages["verse"] = await PageShotAsync(output, "verse", DetailScroll, 6, [DetailPlay]);
            await CloseDetailsAsync();
            await ShowDetailsAsync(spring); await DetailsAsync();
            pages["spring"] = await PageShotAsync(output, "spring", DetailScroll, 4, [DetailPlay]);
            await CloseDetailsAsync();

            // Search: empty, typed letter by letter (the field alone), then what it found.
            SearchNav_Click(this, new()); await Task.Delay(1500); await ArtworkAsync();
            content = (FrameworkElement)LibraryScroll.Content;
            var empty = new Dictionary<string, object> { ["content"] = SaveShot(output, "search-empty.jpg", RenderArea(content, Box(content, content), Background)), ["width"] = content.ActualWidth, ["height"] = content.ActualHeight };
            var typing = new List<object>(); var field = Box(SearchField, content);
            foreach (var length in Enumerable.Range(1, 6))
            {
                SearchBox.Text = "spider"[..length]; SearchBox.CaretIndex = length; content.UpdateLayout();
                typing.Add(new { text = SearchBox.Text, file = SaveShot(output, $"search-type-{length}.jpg", RenderArea(content, field, Background)) });
            }
            await Task.Delay(2500); await ArtworkAsync();
            var search = await PageShotAsync(output, "search", LibraryScroll, 4);
            search["empty"] = empty; search["typing"] = typing; search["field"] = Rounded(field); pages["search"] = search;

            manifest["player"] = await PlayerShotsAsync(output, spring);
            await File.WriteAllTextAsync(Path.Combine(output, "demo-shots-result.txt"), $"PASS: the pictures of {server} as {user}.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "demo-shots-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Environment.ExitCode = 1;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "shots.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Close();
        }
    }
    private async Task BrowseAsync(string view)
    {
        _catalog.View = view; ResetFilters(); _filtersOpen = false;
        await NavigateLibraryAsync(); await Task.Delay(1200); await ArtworkAsync();
    }
    /// <summary>
    /// A page: its content whole (every poster loaded first), the window without it (rail and title bar, transparent),
    /// its cards' places, and each target under the real pointer, drawn alone with its surroundings.
    /// </summary>
    private async Task<Dictionary<string, object>> PageShotAsync(string output, string name, ScrollViewer scroll, int hovers, IEnumerable<FrameworkElement>? first = null)
    {
        for (var offset = 0d; offset < scroll.ExtentHeight; offset += scroll.ViewportHeight * .7)
        {
            SmoothScroll.Jump(scroll, offset); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await ArtworkAsync();
        }
        SmoothScroll.Jump(scroll); await Task.Delay(400);
        var root = (FrameworkElement)Content; var content = (FrameworkElement)scroll.Content;
        var page = new Dictionary<string, object>
        {
            ["content"] = SaveShot(output, $"{name}-content.jpg", RenderArea(content, Box(content, content), Background)),
            ["width"] = content.ActualWidth, ["height"] = content.ActualHeight, ["viewport"] = Rounded(Box(scroll, root)),
            ["chrome"] = SaveShot(output, $"{name}-chrome.png", RenderChrome(root, scroll == DetailScroll)),
            ["cards"] = Descendants<MediaCard>(content).Where(x => x.IsVisible && x.ActualWidth > 1).Select(x => new { name = x.Item.Name, type = x.Item.Type, rect = Rounded(Box(x, content)) }).ToList()
        };
        var shots = new List<object>(); var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var targets = (first ?? Enumerable.Empty<FrameworkElement>()).Concat(Descendants<MediaCard>(content).Where(x => x.IsVisible && x.ActualWidth > 1)).Distinct().ToList();
        foreach (var target in targets)
        {
            if (shots.Count >= hovers) break;
            var place = target.TransformToAncestor(content).TransformBounds(new Rect(target.RenderSize));
            if (place.Right > content.ActualWidth - 30) continue;
            SmoothScroll.Jump(scroll, Math.Clamp(place.Y + place.Height / 2 - scroll.ViewportHeight / 2, 0, scroll.ScrollableHeight));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var center = ScreenAt(target, .5, .5);
            if (center.X < 80 || center.X > SystemParameters.PrimaryScreenWidth - 10 || center.Y < 40 || center.Y > SystemParameters.PrimaryScreenHeight - 10) continue;
            SetCursorPos((int)center.X, (int)center.Y); await Task.Delay(650);
            var area = Rect.Inflate(place, 12, 12); area.Intersect(bounds);
            shots.Add(new { name = target is MediaCard card ? card.Item.Name : AutomationProperties.GetName(target) is { Length: > 0 } label ? label : target.Name, rect = Rounded(area), file = SaveShot(output, $"{name}-hover-{shots.Count:00}.jpg", RenderArea(content, area, Background)) });
        }
        SetCursorPos(1919, 1079); SmoothScroll.Jump(scroll); await Task.Delay(700);
        page["hovers"] = shots;
        return page;
    }
    /// <summary>The window without the page's scrolled content: the rail and the title bar over nothing.</summary>
    private BitmapSource RenderChrome(FrameworkElement root, bool details)
    {
        var hidden = details ? new UIElement[] { DetailScroll, LibraryShell } : new UIElement[] { LibraryScroll };
        var background = DetailOverlay.ReadLocalValue(Panel.BackgroundProperty);
        foreach (var element in hidden) element.Visibility = Visibility.Hidden;
        if (details) DetailOverlay.Background = null;
        try { root.UpdateLayout(); return RenderArea(root, Box(root, root), null); }
        finally
        {
            foreach (var element in hidden) element.Visibility = Visibility.Visible;
            if (details) DetailOverlay.SetValue(Panel.BackgroundProperty, background);
        }
    }
    /// <summary>The player on Spring: its controls each second as it plays, along the timeline as the pointer scrubs, then after the seek.</summary>
    private async Task<object> PlayerShotsAsync(string output, MediaItem spring)
    {
        await ShowDetailsAsync(spring); await DetailsAsync();
        await PlayAsync(spring, chosen: true);
        for (var i = 0; i < 240 && !_loaded; i++) await Task.Delay(250);
        if (!_loaded || _mpv is not { } mpv) throw new InvalidOperationException("Spring did not play within 60 s.");
        Activate(); SetCursorPos(960, 540); await Task.Delay(800);
        SeekTo(46); await Task.Delay(2000);
        var overlay = PlayerOverlayLayer.Child ?? throw new InvalidOperationException("No player controls.");
        async Task<object> Controls(string file, double? fraction = null)
        {
            OpenPlayerSurface(); _lastPlayerInteraction = DateTimeOffset.UtcNow;
            if (!_controlsVisible) { _controlsVisible = true; SetPlayerPanels(true, 0); }
            await Task.Delay(220); overlay.UpdateLayout();
            var position = mpv.Number("time-pos");
            return new { file = SaveShot(output, file, RenderArea(overlay, Box(overlay, overlay), null)), position, fraction };
        }
        var playing = new List<object>();
        for (var i = 0; i < 7; i++) { playing.Add(await Controls($"player-{i:00}.png")); await Task.Delay(760); }
        var scrub = new List<object>();
        for (var i = 0; i <= 16; i++)
        {
            var fraction = .1 + (.83 - .1) * i / 16; var point = ScreenAt(SeekBar, fraction, .5);
            SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(i == 0 ? 700 : 420);
            scrub.Add(await Controls($"player-scrub-{i:00}.png", fraction));
        }
        await ClickAsync(); await Task.Delay(1500);
        var after = new List<object>();
        for (var i = 0; i < 5; i++) { after.Add(await Controls($"player-after-{i:00}.png")); await Task.Delay(760); }
        return new { duration = mpv.Number("duration"), seekBar = Rounded(Box(SeekBar, overlay)), playing, scrub, after };
    }
    private static object Rounded(Rect rect) => new { x = Math.Round(rect.X, 1), y = Math.Round(rect.Y, 1), w = Math.Round(rect.Width, 1), h = Math.Round(rect.Height, 1) };
    private static Rect Box(FrameworkElement element, FrameworkElement ancestor) =>
        element == ancestor ? new Rect(element.RenderSize) : element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    /// <summary>A part of an element at twice its size, over a backdrop or transparent.</summary>
    private static BitmapSource RenderArea(FrameworkElement element, Rect area, Brush? backdrop)
    {
        const double scale = 2;
        var visual = new DrawingVisual(); var target = new Rect(0, 0, area.Width, area.Height);
        using (var context = visual.RenderOpen())
        {
            if (backdrop is not null) context.DrawRectangle(backdrop, null, target);
            context.DrawRectangle(new VisualBrush(element) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Viewport = target, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, target);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private static string SaveShot(string folder, string file, BitmapSource bitmap)
    {
        BitmapEncoder encoder = file.EndsWith(".png", StringComparison.Ordinal) ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 93 };
        encoder.Frames.Add(BitmapFrame.Create(encoder is PngBitmapEncoder ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
        using var stream = File.Create(Path.Combine(folder, file)); encoder.Save(stream);
        return file;
    }
}
