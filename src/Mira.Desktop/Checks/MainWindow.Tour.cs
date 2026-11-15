using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>The presentation tour's zoom of the pages (0 = Mira's own).</summary>
    private double _tourZoom;

    /// <summary>
    /// The presentation video's films (--tour &lt;folder&gt;, never merged): Mira signed in to the README's library (the
    /// gallery's MIRA_GALLERY_SERVER, _USER, _PASSWORD), its window over the whole screen, each scene played by the pointer
    /// and the keyboard while ffmpeg films it (<see cref="TourDirector"/>). A scene that fails is written down, the next goes on.
    /// </summary>
    private async Task RunTourAsync()
    {
        var output = Path.GetFullPath(_args[Array.IndexOf(_args, "--tour") + 1]); Directory.CreateDirectory(output);
        var result = Path.Combine(output, "tour-result.txt");
        var log = new List<string>();
        Friend? sam = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(output, "dilation.txt"), TimeDilation.Report);
            if (!TimeDilation.Loaded) throw new InvalidOperationException("No time dilation: MIRA_DILATE_DLL?");
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER") ?? throw new InvalidOperationException("MIRA_GALLERY_SERVER?");
            var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER")!; var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD")!;
            Motion.FollowsWindows = false; Motion.Reduced = false;
            // Mira's clocks run up to ten times slower: a keep-alive every 3 s of theirs is under Jellyfin's 60 s.
            JellyfinClient.KeepAliveEvery = TimeSpan.FromSeconds(3);
            _fallbackRefresh.Stop(); _searchTimer.Stop();
            TitleStorage.Shown = false;
            _settings.Discord.Offered = true; _settings.Picture = PictureQuality.Standard; _settings.Volume = 0; _settings.AutoNext = false;
            Connection connection;
            using (var client = new JellyfinClient(new(server, "", user, "", _profile.DeviceId))) connection = await client.LoginAsync(user, password);
            _profile.SaveConnection(connection);
            await ActivateConnectionAsync(connection);
            WhatsNewOverlay.Visibility = WelcomeOverlay.Visibility = GuideOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;

            // A friend, Sam, with a group of their own for « Watch together ».
            try
            {
                const string samPassword = "mira-tour-sam";
                if (_client is { } own && (await own.UsersAsync() ?? []).All(x => x.Name != "Sam"))
                    await own.CreateFriendAsync("Sam", samPassword, (await own.LibrariesAsync()).Select(x => x.Id).ToList());
                sam = await Friend.StartAsync(server, "Sam", samPassword);
                for (var i = 0; i < 100 && !sam.Listening; i++) await Task.Delay(100);
                await sam.Client.SyncNewAsync(Together.GroupName("Sam"));
                log.Add("sam: group made");
            }
            catch (Exception ex) { log.Add($"sam: {ex.GetType().Name}: {ex.Message}"); }

            WindowState = WindowState.Normal; Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
            Topmost = true; Activate();
            _tourZoom = SystemParameters.PrimaryScreenWidth / 1440; UpdateLayout(); if (UpdateZoom()) FitPages();
            ResetFilters(); _catalog.View = "home"; _catalog.ParentId = null; await NavigateLibraryAsync(); SmoothScroll.Jump(LibraryScroll);
            await Task.Delay(2500); await ArtworkAsync(); await Task.Delay(1500);

            var tour = new TourDirector(output);
            foreach (var (name, scene) in new (string, Func<TourDirector, Task>)[]
            {
                ("opening", OpeningSceneAsync), ("home", HomeSceneAsync), ("series", SeriesSceneAsync), ("settings", SettingsSceneAsync),
                ("movies", MoviesSceneAsync), ("together", TogetherSceneAsync),
            })
            {
                try { await scene(tour); log.Add($"{name}: done"); }
                catch (Exception ex) { log.Add($"{name}: FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
                finally { await tour.EndAsync(); }
            }
            await File.WriteAllTextAsync(result, (log.Any(x => x.Contains(": FAIL", StringComparison.Ordinal)) ? "FAIL\n" : "PASS\n") + string.Join("\n", log));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(result, $"FAIL: {ex}\n" + string.Join("\n", log));
            Environment.ExitCode = 1;
        }
        finally
        {
            TimeDilation.Set(1);
            if (sam is not null) await sam.DisposeAsync();
            Close();
        }
    }

    // —— The scenes ——

    /// <summary>Mira's opening, « Le clin d'œil », on the Home page: the veil held still, then the mark winks.</summary>
    private async Task OpeningSceneAsync(TourDirector tour)
    {
        // The veil left the screen at Mira's start by a fade, which still holds its opacity at 0: let go of it first.
        foreach (var part in new UIElement[] { StartupVeil, StartupShade, StartupWord, StartupMark })
        { part.BeginAnimation(OpacityProperty, null); part.CacheMode = null; }
        StartupVeil.RenderTransform = Transform.Identity;
        StartupVeil.Visibility = Visibility.Visible; StartupVeil.IsHitTestVisible = true; StartupVeil.Opacity = 1;
        StartupShade.Opacity = 1; StartupWord.Opacity = 1; StartupMark.Opacity = 1;
        UpdateLayout();
        tour.Place(ActualWidth + 50, ActualHeight * .5);
        await tour.BeginAsync("opening", 10);
        await TourDirector.Wait(900);
        tour.Mark("blink");
        if (!StartOpening(frozen: false)) throw new InvalidOperationException("The opening would not blink.");
        await _openingDone!.Task;
        await TourDirector.Wait(1400);
    }

    /// <summary>Home: the banner, Continue watching under the pointer, the rows further down, back up.</summary>
    private async Task HomeSceneAsync(TourDirector tour)
    {
        tour.Place(ActualWidth * .78, ActualHeight * .42);
        await tour.BeginAsync("home");
        await TourDirector.Wait(900);
        var row = CardsOnScreen().Where(x => x.TranslatePoint(new Point(), this).Y > ActualHeight * .45).Take(4).ToList();
        foreach (var card in row) { await tour.MoveAsync(card, .5, .42, 560); await TourDirector.Wait(520); }
        await tour.MoveToAsync(ActualWidth * .6, ActualHeight * .55, 700);
        await tour.WheelAsync(-3, 230); await TourDirector.Wait(900); await ArtworkAsync();
        if (CardsOnScreen().Skip(5).FirstOrDefault() is { } poster) { await tour.MoveAsync(poster, .5, .45, 650); await TourDirector.Wait(700); }
        await tour.WheelAsync(-3, 230); await TourDirector.Wait(1000); await ArtworkAsync();
        if (CardsOnScreen().Skip(9).FirstOrDefault() is { } other) { await tour.MoveAsync(other, .5, .45, 650); await TourDirector.Wait(700); }
        await tour.MoveToAsync(ActualWidth * .55, ActualHeight * .3, 600);
        await tour.WheelAsync(6, 110); await TourDirector.Wait(1200);
    }

    /// <summary>Series: Arcane, its episodes in a grid, the other season.</summary>
    private async Task SeriesSceneAsync(TourDirector tour)
    {
        tour.Place(ActualWidth * .55, ActualHeight * .3);
        await tour.BeginAsync("series");
        await TourDirector.Wait(400);
        await tour.MoveAsync(SeriesNav, .5, .5, 750); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1300);
        await ArtworkAsync();
        var arcane = await FindCardAsync(tour, "Arcane");
        await tour.MoveAsync(arcane, .5, .42, 750); await TourDirector.Wait(600); await tour.ClickAsync(); await TourDirector.Wait(1900);
        await tour.MoveToAsync(ActualWidth * .6, ActualHeight * .6, 600);
        for (var i = 0; i < 8 && EpisodesHead.TranslatePoint(new Point(), this).Y > ActualHeight * .3; i++) { await tour.WheelAsync(-1); await TourDirector.Wait(240); }
        await TourDirector.Wait(700); await ArtworkAsync();
        var tiles = Under<Button>(EpisodesPanel).Where(x => x.IsVisible && x.ActualWidth > 150 && OnScreen(x)).Take(3).ToList();
        foreach (var tile in tiles) { await tour.MoveAsync(tile, .5, .35, 600); await TourDirector.Wait(650); }
        var pills = SeasonPills.Children.OfType<Button>().Where(x => x.IsVisible).ToList();
        if (pills.Count > 1)
        {
            await tour.MoveAsync(pills[1], .5, .5, 700); await TourDirector.Wait(300); await tour.ClickAsync(); await TourDirector.Wait(1300);
            await ArtworkAsync();
            if (Under<Button>(EpisodesPanel).Where(x => x.IsVisible && x.ActualWidth > 150 && OnScreen(x)).Skip(1).FirstOrDefault() is { } tile) { await tour.MoveAsync(tile, .5, .35, 650); await TourDirector.Wait(800); }
            await tour.MoveAsync(pills[0], .5, .5, 700); await TourDirector.Wait(300); await tour.ClickAsync(); await TourDirector.Wait(1100);
        }
        await tour.KeyAsync(0x1B); await TourDirector.Wait(1000);
    }

    /// <summary>Films: a genre, the posters, Blade Runner 2049's page.</summary>
    private async Task MoviesSceneAsync(TourDirector tour)
    {
        tour.Place(ActualWidth * .5, ActualHeight * .45);
        await tour.BeginAsync("movies");
        await TourDirector.Wait(300);
        await tour.MoveAsync(MoviesNav, .5, .5, 750); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1300);
        await ArtworkAsync();
        var chip = GenreChips.Children.OfType<Button>().FirstOrDefault(x => Words(x).Contains("Science", StringComparison.OrdinalIgnoreCase))
            ?? GenreChips.Children.OfType<Button>().Skip(2).FirstOrDefault();
        if (chip is not null)
        {
            await tour.MoveAsync(chip, .5, .5, 800); await TourDirector.Wait(300); await tour.ClickAsync(); await TourDirector.Wait(1400);
            await ArtworkAsync();
            foreach (var card in CardsOnScreen().Take(3)) { await tour.MoveAsync(card, .5, .45, 520); await TourDirector.Wait(420); }
        }
        var film = await FindCardAsync(tour, "Blade Runner 2049");
        await tour.MoveAsync(film, .5, .42, 700); await TourDirector.Wait(600); await tour.ClickAsync(); await TourDirector.Wait(2000);
        await tour.MoveToAsync(ActualWidth * .62, ActualHeight * .62, 600);
        await tour.WheelAsync(-3, 260); await TourDirector.Wait(1100); await ArtworkAsync();
        await tour.WheelAsync(3, 200); await TourDirector.Wait(900);
    }

    /// <summary>On Blade Runner 2049's page: Sam's group joined, Lecture for the group, the player, the mini-player at the edge.</summary>
    private async Task TogetherSceneAsync(TourDirector tour)
    {
        tour.Place(ActualWidth * .62, ActualHeight * .62);
        await tour.BeginAsync("together");
        await TourDirector.Wait(300);
        await tour.MoveAsync(TogetherButton, .5, .5, 750); await TourDirector.Wait(300); await tour.ClickAsync(); await TourDirector.Wait(1200);
        var join = OpenMenuItems().FirstOrDefault(x => x.Header is string h && h.Contains("Sam", StringComparison.Ordinal));
        if (join is not null) { await tour.MoveAsync(join, .4, .5, 600); await TourDirector.Wait(350); await tour.ClickAsync(); await TourDirector.Wait(2200); }
        else await tour.KeyAsync(0x1B);
        await tour.MoveAsync(DetailPlay, .5, .5, 750); await SyncGroupClockAsync(); await TourDirector.Wait(250); await tour.ClickAsync();
        for (var i = 0; i < 400 && (!_loaded || _videoWaits); i++) await TourDirector.Wait(50);
        await TourDirector.Wait(1300);
        // The group starts paused, every device ready: Play, for everyone.
        if (_session.Paused)
        {
            // Mira's clock runs slower than Jellyfin's while filming: the group's clock read again just before, or
            // « play at » would be seconds away for this PC.
            await tour.MoveAsync(PauseButton, .5, .5, 750); await SyncGroupClockAsync(); await TourDirector.Wait(150); await tour.ClickAsync();
            for (var i = 0; i < 80 && _session.Paused; i++) await TourDirector.Wait(50);
        }
        await TourDirector.Wait(1800);
        await tour.MoveToAsync(ActualWidth * .45, ActualHeight * .55, 700); await TourDirector.Wait(900);
        await tour.MoveAsync(SeekBar, .62, .5, 800); await TourDirector.Wait(1200);
        await tour.MoveAsync(PlayerTracksButton, .5, .5, 700); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1700);
        await tour.KeyAsync(0x1B); await TourDirector.Wait(700);
        await tour.MoveAsync(MiniPlayerButton, .5, .5, 700); await TourDirector.Wait(300); await tour.ClickAsync(); await TourDirector.Wait(1200);
        await tour.MoveToAsync(ActualWidth * .4, ActualHeight * .45, 800);
        await TourDirector.Wait(2600);
        if (MiniFrame.IsVisible)
        {
            var mini = MiniFrame.PointToScreen(new Point(MiniFrame.ActualWidth / 2, MiniFrame.ActualHeight / 2));
            await tour.MoveToAsync(ActualWidth - 8, mini.Y, 900); await TourDirector.Wait(1300);
            mini = MiniFrame.PointToScreen(new Point(MiniFrame.ActualWidth / 2, MiniFrame.ActualHeight / 2));
            await tour.MoveToAsync(mini.X, mini.Y, 450); await TourDirector.Wait(500); await tour.ClickAsync(); await TourDirector.Wait(1800);
        }
        await tour.MoveToAsync(ActualWidth * .5, ActualHeight * .45, 500); await TourDirector.Wait(600);
        await tour.EndAsync();
        await StopPlaybackAsync(); await Task.Delay(1500);
        if (_group is not null) await LeaveGroupAsync();
        if (DetailOverlay.Visibility == Visibility.Visible) await CloseDetailsAsync();
        await Task.Delay(800);
    }

    /// <summary>Settings: the window over the app, a search, Discord's live preview, Privacy.</summary>
    private async Task SettingsSceneAsync(TourDirector tour)
    {
        tour.Place(ActualWidth * .5, ActualHeight * .5);
        await tour.BeginAsync("settings");
        await TourDirector.Wait(300);
        await tour.MoveAsync(SettingsNav, .5, .5, 800); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1700);
        for (var i = 0; i < 60 && (SettingsOverlay.Visibility != Visibility.Visible || PresentationSource.FromVisual(SettingsSearchBox) is null || !SettingsSearchBox.IsVisible); i++)
            await TourDirector.Wait(100);
        if (PresentationSource.FromVisual(SettingsSearchBox) is null || !SettingsSearchBox.IsVisible) throw new InvalidOperationException("Settings never opened.");
        await tour.MoveAsync(SettingsSearchBox, .3, .5, 700); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(300);
        await tour.TypeAsync("discord", 110); await TourDirector.Wait(1100);
        await tour.MoveAsync(DiscordSettingsNav, .5, .5, 700); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1500);
        if (_discordTabs is not null)
            foreach (var tab in Under<RadioButton>(_discordTabs).Where(x => x.IsVisible).Skip(1).Take(2))
            { await tour.MoveAsync(tab, .5, .5, 600); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(1200); }
        if (_discordStyles is not null && Under<RadioButton>(_discordStyles).Where(x => x.IsVisible).Skip(1).FirstOrDefault() is { } style)
        { await tour.MoveAsync(style, .5, .4, 700); await TourDirector.Wait(200); await tour.ClickAsync(); await TourDirector.Wait(1400); }
        await tour.MoveAsync(PrivacySettingsNav, .5, .5, 800); await TourDirector.Wait(250); await tour.ClickAsync(); await TourDirector.Wait(1300);
        await tour.MoveToAsync(ActualWidth * .6, ActualHeight * .55, 600);
        await tour.WheelAsync(-3, 260); await TourDirector.Wait(1200);
        await tour.KeyAsync(0x1B); await TourDirector.Wait(250); await tour.KeyAsync(0x1B); await TourDirector.Wait(1200);
    }

    // —— Helpers ——

    /// <summary>The card of a title on the page, scrolled to by the wheel when it is further down.</summary>
    private async Task<MediaCard> FindCardAsync(TourDirector tour, string name)
    {
        // The wheel turns what is under the pointer: never the rail.
        if (tour is not null) await tour.MoveToAsync(ActualWidth * .6, ActualHeight * .55, 500);
        for (var i = 0; i < 14; i++)
        {
            if (Under<MediaCard>(this).Where(x => x.IsVisible && x.ActualWidth > 0 && x.Item.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(x => x.TranslatePoint(new Point(), this) is var at && at.Y > 40 && at.X >= 0 && at.X + x.ActualWidth <= ActualWidth
                    && at.Y + x.ActualHeight < ActualHeight * .97) is { } card) return card;
            await tour!.WheelAsync(-1); await TourDirector.Wait(260); await ArtworkAsync();
        }
        throw new InvalidOperationException($"No card « {name} » on the page.");
    }

    private static string Words(Button button) => button.Content as string
        ?? string.Join(" ", Under<TextBlock>(button).Select(x => x.Text).Prepend((button.Content as TextBlock)?.Text ?? ""));

    private bool OnScreen(FrameworkElement element)
    {
        var at = element.TranslatePoint(new Point(), this);
        return new Rect(0, 0, ActualWidth, ActualHeight).Contains(new Rect(at, element.RenderSize));
    }

    private static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in Under<T>(child)) yield return deeper;
        }
    }

    /// <summary>The items of the menus open now (each a window of its own).</summary>
    private static List<MenuItem> OpenMenuItems() =>
        [.. PresentationSource.CurrentSources.OfType<HwndSource>().Select(x => x.RootVisual).OfType<DependencyObject>()
            .SelectMany(x => Under<MenuItem>(x)).Where(x => x.IsVisible && x.IsEnabled)];

    /// <summary>Every card of the page on the screen, top to bottom, left to right.</summary>
    private List<MediaCard> CardsOnScreen()
    {
        var screen = new Rect(0, 0, ActualWidth, ActualHeight);
        return [.. Under<MediaCard>(this).Where(x => x.IsVisible && x.ActualWidth > 0)
            .Select(x => (Card: x, At: x.TranslatePoint(new Point(), this)))
            .Where(x => screen.Contains(new Rect(x.At, x.Card.RenderSize)))
            .OrderBy(x => Math.Round(x.At.Y / 40)).ThenBy(x => x.At.X).Select(x => x.Card)];
    }
}
