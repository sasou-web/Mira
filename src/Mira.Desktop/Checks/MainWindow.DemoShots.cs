using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.Updates;
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
        var extras = new Dictionary<string, object>(); manifest["extras"] = extras; var failed = new List<string>(); manifest["failed"] = failed;
        // A screen the montage can do without: its failure is written down and the rest goes on.
        async Task Extra(string name, Func<Task<object>> shots)
        {
            try { extras[name] = await shots().WaitAsync(TimeSpan.FromSeconds(120)); }
            catch (Exception ex) { failed.Add($"{name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        try
        {
            var server = Environment.GetEnvironmentVariable("MIRA_GALLERY_SERVER"); var user = Environment.GetEnvironmentVariable("MIRA_GALLERY_USER");
            var password = Environment.GetEnvironmentVariable("MIRA_GALLERY_PASSWORD");
            if (_demo || string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || password is null || !_args.Contains("--data"))
                throw new InvalidOperationException("The shots need MIRA_GALLERY_SERVER, _USER and _PASSWORD, an isolated --data profile and no demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); _artworkScale = 2; _settings.HeroAutoPlay = false; Motion.FollowsWindows = false;
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
            // The filters unfolded over the same page.
            await Extra("filters", async () =>
            {
                var library = (FrameworkElement)LibraryScroll.Content;
                FiltersToggle_Click(this, new()); await Task.Delay(900); library.UpdateLayout();
                var top = new Rect(0, 0, library.ActualWidth, Math.Min(library.ActualHeight, 1200));
                object shot = new { file = SaveShot(output, "movies-filters.jpg", RenderArea(library, top, Background)), rect = Rounded(top), panel = Rounded(Box(CatalogFilters, library)), toggle = Rounded(Box(FiltersToggle, library)) };
                FiltersToggle_Click(this, new()); await Task.Delay(500);
                return shot;
            });
            await ShowDetailsAsync(verse); await DetailsAsync();
            pages["verse"] = await PageShotAsync(output, "verse", DetailScroll, 6, [DetailPlay]);
            // Its state buttons, off then on: the same squares, their glyphs alone changed.
            await Extra("states", async () =>
            {
                var page = (FrameworkElement)DetailScroll.Content; var row = Rect.Inflate(Box(DetailActions, page), 14, 14);
                var shots = new Dictionary<string, object>
                {
                    ["rect"] = Rounded(row), ["play"] = Rounded(Box(DetailPlay, page)), ["favorite"] = Rounded(Box(FavoriteButton, page)), ["watched"] = Rounded(Box(WatchedButton, page)),
                    ["together"] = TogetherButton.IsVisible ? Rounded(Box(TogetherButton, page)) : "", ["off"] = SaveShot(output, "verse-states-off.jpg", RenderArea(page, row, Background))
                };
                ShowFavorite(true); await Task.Delay(800); shots["favoriteOn"] = SaveShot(output, "verse-states-favorite.jpg", RenderArea(page, row, Background));
                ShowWatched(verse, true); await Task.Delay(800); shots["bothOn"] = SaveShot(output, "verse-states-both.jpg", RenderArea(page, row, Background));
                ShowFavorite(verse.UserData.IsFavorite); ShowWatched(verse, verse.UserData.Played); await Task.Delay(400);
                return shots;
            });
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

            // What the montage can do without, each on its own: « Nouveautés », Réglages, the iPhone's card of the guide.
            await Extra("whatsNew", async () =>
            {
                ShowWhatsNew(WhatsNew.Recent(ReleaseFeed.ParseVersion(JellyfinClient.AppVersion) ?? new Version(0, 0, 0))); await Task.Delay(1200); UpdateLayout();
                var items = (FrameworkElement)WhatsNewScroll.Content;
                object shot = new
                {
                    window = SaveShot(output, "whatsnew-window.jpg", RenderArea(root, Box(root, root), Background)),
                    card = SaveShot(output, "whatsnew-card.png", RenderArea(WhatsNewCard, Box(WhatsNewCard, WhatsNewCard), null)),
                    items = SaveShot(output, "whatsnew-items.png", RenderArea(items, Box(items, items), null)),
                    cardRect = Rounded(Box(WhatsNewCard, root)), cardSize = new { w = WhatsNewCard.ActualWidth, h = WhatsNewCard.ActualHeight },
                    scroll = Rounded(Box(WhatsNewScroll, WhatsNewCard)), itemsSize = new { w = items.ActualWidth, h = items.ActualHeight }
                };
                WhatsNewOverlay.Visibility = Visibility.Collapsed; await Task.Delay(300);
                return shot;
            });
            await Extra("settings", async () =>
            {
                Settings_Click(this, new()); await Task.Delay(1100);
                var tabs = new List<object>();
                foreach (var tab in new[] { "playback", "subtitles", "appearance", "privacy" })
                {
                    SelectSettingsTab(tab, animate: false); await Task.Delay(700); SmoothScroll.Jump(SettingsScroll); UpdateLayout();
                    var files = new List<string> { SaveShot(output, $"settings-{tab}.jpg", RenderArea(root, Box(root, root), Background)) };
                    if (SettingsScroll.ScrollableHeight > 200)
                    {
                        SmoothScroll.Jump(SettingsScroll, Math.Min(SettingsScroll.ScrollableHeight, SettingsScroll.ViewportHeight * .8)); await Task.Delay(400); UpdateLayout();
                        files.Add(SaveShot(output, $"settings-{tab}-2.jpg", RenderArea(root, Box(root, root), Background)));
                        SmoothScroll.Jump(SettingsScroll);
                    }
                    tabs.Add(new { tab, files, scroll = Rounded(Box(SettingsScroll, root)), scrollable = SettingsScroll.ScrollableHeight });
                }
                await CloseSettingsAnimatedAsync(); await Task.Delay(500);
                return tabs;
            });
            await Extra("phone", async () =>
            {
                OpenGuide(); await Task.Delay(1500);
                // Addresses of an example home, as in the documentation: the runner's own would mean nothing.
                ++_phoneVersion; _phone = null; _phoneReadAt = DateTime.MaxValue;
                ShowPhone(new PhoneSetup.Facts("http://127.0.0.1:8096", true, Web: true, Home: "http://192.168.1.42:8096", Admin: true, Tailnet: "http://100.88.12.34:8096", TailnetAllowed: true,
                    Firewall: new(JellyfinFirewall.Verdict.Open, JellyfinFirewall.Private, JellyfinFirewall.Verdict.Open), MiraRule: JellyfinFirewall.RuleState.Ready, Boot: new(true, true, true, true)));
                await Task.Delay(600); UpdateLayout();
                SmoothScroll.Jump(GuideScroll, Math.Max(0, GuidePhone.TranslatePoint(new Point(0, 0), GuideContent).Y - 40)); await Task.Delay(500); UpdateLayout();
                var guide = (FrameworkElement)GuideScroll.Content;
                object shot = new
                {
                    window = SaveShot(output, "guide-window.jpg", RenderArea(root, Box(root, root), Background)),
                    card = SaveShot(output, "guide-phone.jpg", RenderArea(guide, Rect.Inflate(Box(GuidePhone, guide), 4, 4), Background)),
                    cardRect = Rounded(Box(GuidePhone, root)), qr = PhoneQr.IsVisible ? Rounded(Box(PhoneQrCode, root)) : ""
                };
                GuideOverlay.Visibility = Visibility.Collapsed; await Task.Delay(300);
                return shot;
            });

            await Extra("couch", async () =>
            {
                SettingsOverlay.Visibility = DetailOverlay.Visibility = GuideOverlay.Visibility = WhatsNewOverlay.Visibility = Visibility.Collapsed;
                await BrowseAsync("home"); await Task.Delay(800); UpdateLibraryCover();
                SetCouchMode(true); await Task.Delay(1800); HideNotice(); await ArtworkAsync(); UpdateLayout(); UpdateLibraryCover(); await Task.Delay(500);
                var shots = new List<string>(); var screen = (FrameworkElement)Content;
                FocusFirst(); await Task.Delay(600);
                var state = $"library {LibraryShell.Opacity:0.##} {LibraryShell.Visibility}, covered {_libraryCovered}, playing {_playing}, couch screen {_couchScreen}, covers {string.Join(' ', _libraryCovers.Where(x => x.IsVisible).Select(x => (x as FrameworkElement)?.Name + ":" + x.Opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}";
                shots.Add(SaveShot(output, "couch-00.jpg", RenderArea(screen, Box(screen, screen), Background)));
                for (var i = 1; i <= 4; i++)
                {
                    Do(i <= 2 ? Couch.Action.Down : Couch.Action.Right); await Task.Delay(700); await ArtworkAsync();
                    shots.Add(SaveShot(output, $"couch-{i:00}.jpg", RenderArea(screen, Box(screen, screen), Background)));
                }
                object shot = new { files = shots, zoom = _zoom, width = screen.ActualWidth, height = screen.ActualHeight, state };
                SetCouchMode(false); await Task.Delay(1200); HideNotice();
                return shot;
            });

            manifest["player"] = await PlayerShotsAsync(output, spring, Extra);
            // After the film: a group made on its page (the invitation), then « Mode canapé » on the Home page.
            await Extra("together", async () =>
            {
                if (_playing) await StopPlaybackAsync();
                await Task.Delay(1200);
                if (DetailOverlay.Visibility != Visibility.Visible) { await ShowDetailsAsync(spring); await DetailsAsync(); }
                await EnterGroupAsync(c => c.SyncNewAsync(Together.GroupName(c.Connection.UserName)), invite: true, made: true);
                await Task.Delay(1500); UpdateLayout();
                if (_group is null || InviteOverlay.Visibility != Visibility.Visible) throw new InvalidOperationException("No group, or no invitation shown.");
                var page = (FrameworkElement)DetailScroll.Content; var row = Rect.Inflate(Box(DetailActions, page), 14, 14);
                var shot = new Dictionary<string, object>
                {
                    ["window"] = SaveShot(output, "invite-window.jpg", RenderArea(root, Box(root, root), Background)),
                    ["card"] = SaveShot(output, "invite-card.png", RenderArea(InviteCard, Box(InviteCard, InviteCard), null)),
                    ["cardRect"] = Rounded(Box(InviteCard, root)), ["group"] = _group.GroupName
                };
                await CloseInviteAsync(); await Task.Delay(700); UpdateLayout();
                shot["page"] = SaveShot(output, "spring-group-window.jpg", RenderArea(root, Box(root, root), Background));
                shot["row"] = SaveShot(output, "spring-group-row.jpg", RenderArea(page, row, Background)); shot["rowRect"] = Rounded(row);
                await LeaveGroupAsync(); await Task.Delay(800);
                return shot;
            });
            if (failed.Count > 0) await File.WriteAllLinesAsync(Path.Combine(output, "demo-shots-failed.txt"), failed);
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
        // A button under the real pointer (its hover is WPF's own), where it shows without scrolling.
        foreach (var target in first ?? [])
        {
            var place = Box(target, content);
            SmoothScroll.Jump(scroll, Math.Clamp(place.Y + place.Height / 2 - scroll.ViewportHeight / 2, 0, scroll.ScrollableHeight));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var center = ScreenAt(target, .5, .5);
            if (center.X < 80 || center.X > SystemParameters.PrimaryScreenWidth - 10 || center.Y < 40 || center.Y > SystemParameters.PrimaryScreenHeight - 10) continue;
            SetCursorPos((int)center.X, (int)center.Y); await Task.Delay(650);
            var area = Rect.Inflate(place, 12, 12); area.Intersect(bounds);
            shots.Add(new { name = AutomationProperties.GetName(target) is { Length: > 0 } label ? label : target.Name, rect = Rounded(area), scrolled = scroll.VerticalOffset, file = SaveShot(output, $"{name}-hover-{shots.Count:00}.jpg", RenderArea(content, area, Background)) });
        }
        SetCursorPos(1919, 1079); SmoothScroll.Jump(scroll); await Task.Delay(500);
        // A card lit as under the pointer (its ring and its zoom are its own animation), the page at its top.
        foreach (var card in Descendants<MediaCard>(content).Where(x => x.IsVisible && x.ActualWidth > 1))
        {
            if (shots.Count >= hovers + (first?.Count() ?? 0)) break;
            var place = Box(card, content);
            if (place.Right > content.ActualWidth - 30) continue;
            card.SetHover(true); await Task.Delay(330);
            var area = Rect.Inflate(place, 12, 12); area.Intersect(bounds);
            shots.Add(new { name = card.Item.Name, rect = Rounded(area), scrolled = 0d, file = SaveShot(output, $"{name}-hover-{shots.Count:00}.jpg", RenderArea(content, area, Background)) });
            card.SetHover(false);
        }
        await Task.Delay(300);
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
    private async Task<object> PlayerShotsAsync(string output, MediaItem spring, Func<string, Func<Task<object>>, Task> extra)
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
        var bar = Rounded(Box(SeekBar, overlay)); var duration = mpv.Number("duration");
        var root = (FrameworkElement)Content;
        // Its menus (windows of their own: drawn alone), paused, then reduced to the corner and out in its own small window.
        foreach (var (key, button) in new[] { ("moreMenu", PlayerMoreButton), ("tracksMenu", PlayerTracksButton) })
            await extra(key, async () =>
            {
                SetCursorPos(960, 400); await Controls($"player-{key}-under.png");
                TogglePlayerMenu(button); await Task.Delay(900); PlayerMenu.UpdateLayout();
                Rect OnScreen(FrameworkElement element)
                {
                    var from = root.PointToScreen(new Point(0, 0)); var at = element.PointToScreen(new Point(0, 0)); var dpi = VisualTreeHelper.GetDpi(root);
                    return new Rect((at.X - from.X) / dpi.DpiScaleX, (at.Y - from.Y) / dpi.DpiScaleY, element.ActualWidth, element.ActualHeight);
                }
                object anchor = ""; object place = "";
                try { anchor = Rounded(OnScreen(button)); } catch (InvalidOperationException) { }
                try { place = Rounded(OnScreen(PlayerMenu)); } catch (InvalidOperationException) { }
                object shot = new { file = SaveShot(output, $"player-{key}.png", RenderArea(PlayerMenu, new Rect(PlayerMenu.RenderSize), null)), under = $"player-{key}-under.png", size = new { w = PlayerMenu.ActualWidth, h = PlayerMenu.ActualHeight }, anchor, place };
                PlayerOptionsPopup.IsOpen = false; await Task.Delay(400);
                return shot;
            });
        await extra("paused", async () =>
        {
            mpv.Set("pause", "yes"); await Task.Delay(900);
            var shot = await Controls("player-paused.png");
            mpv.Set("pause", "no"); await Task.Delay(700);
            return shot;
        });
        await extra("mini", async () =>
        {
            SetMiniPlayer(true); await Task.Delay(1500); await ArtworkAsync(); UpdateLayout();
            SetCursorPos((int)ScreenAt(MiniFrame, .5, .5).X, (int)ScreenAt(MiniFrame, .5, .5).Y); await Task.Delay(900);
            var controls = PlayerOverlayLayer.Child as FrameworkElement;
            object shot = new
            {
                window = SaveShot(output, "mini-window.jpg", RenderArea(root, Box(root, root), Background)), frame = Rounded(Box(MiniFrame, root)),
                controls = controls is null ? "" : SaveShot(output, "mini-controls.png", RenderArea(controls, Box(controls, controls), null)),
                controlsSize = controls is null ? null : new { w = controls.ActualWidth, h = controls.ActualHeight }, position = mpv.Number("time-pos")
            };
            SetCursorPos(1919, 1079);
            return shot;
        });
        await extra("pip", async () =>
        {
            if (!_miniPlayer) { SetMiniPlayer(true); await Task.Delay(1200); }
            EnterPip(); await Task.Delay(1500);
            var pip = _pip ?? throw new InvalidOperationException("No small window.");
            pip.ShowControls(); await Task.Delay(700);
            var controls = pip.Controls.Child as FrameworkElement ?? throw new InvalidOperationException("No controls in the small window.");
            controls.UpdateLayout(); var bounds = pip.Bounds();
            object shot = new
            {
                controls = SaveShot(output, "pip-controls.png", RenderArea(controls, Box(controls, controls), null)), size = new { w = controls.ActualWidth, h = controls.ActualHeight },
                bounds = new { x = bounds.Left, y = bounds.Top, w = bounds.Width, h = bounds.Height }, window = SaveShot(output, "pip-behind.jpg", RenderArea(root, Box(root, root), Background)), position = mpv.Number("time-pos")
            };
            ReturnFromPip(); await Task.Delay(1500);
            return shot;
        });
        return new { duration, seekBar = bar, playing, scrub, after };
    }
    private async Task DetailsAsync() { await Task.Delay(500); await ArtworkAsync(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found) yield return found;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
    private static Point ScreenAt(FrameworkElement element, double x, double y) => element.PointToScreen(new Point(element.ActualWidth * x, element.ActualHeight * y));
    /// <summary>A real click where the pointer is.</summary>
    private async Task ClickAsync()
    {
        await Task.Delay(120);
        SendShotInput(1, [new ShotInput { Type = 0, Mouse = new() { Flags = 0x0002 } }], System.Runtime.InteropServices.Marshal.SizeOf<ShotInput>()); await Task.Delay(90);
        SendShotInput(1, [new ShotInput { Type = 0, Mouse = new() { Flags = 0x0004 } }], System.Runtime.InteropServices.Marshal.SizeOf<ShotInput>());
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(500);
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct ShotMouse { public int X, Y; public int Data; public uint Flags; public uint Time; public IntPtr Extra; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 40)] private struct ShotInput { [System.Runtime.InteropServices.FieldOffset(0)] public uint Type; [System.Runtime.InteropServices.FieldOffset(8)] public ShotMouse Mouse; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendShotInput(uint count, ShotInput[] inputs, int size);
    private static object Rounded(Rect rect) => new { x = Math.Round(rect.X, 1), y = Math.Round(rect.Y, 1), w = Math.Round(rect.Width, 1), h = Math.Round(rect.Height, 1) };
    private static Rect Box(FrameworkElement element, FrameworkElement ancestor) =>
        element == ancestor ? new Rect(element.RenderSize) : element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    /// <summary>
    /// A part of an element at twice its size, over a backdrop or transparent. The brush draws the element where its
    /// parent puts it (a page scrolled is drawn moved up): the area is moved the same way. Without the posters' pictures
    /// (RenderCache): made at the window's scale, they would be stretched.
    /// </summary>
    private static BitmapSource RenderArea(FrameworkElement element, Rect area, Brush? backdrop)
    {
        const double scale = 2;
        var visual = new DrawingVisual(); var target = new Rect(0, 0, area.Width, area.Height);
        var moved = VisualTreeHelper.GetOffset(element); var from = area; from.Offset(moved.X, moved.Y);
        using (var context = visual.RenderOpen())
        {
            if (backdrop is not null) context.DrawRectangle(backdrop, null, target);
            context.DrawRectangle(new VisualBrush(element) { Viewbox = from, ViewboxUnits = BrushMappingMode.Absolute, Viewport = target, ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, target);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(area.Width * scale), (int)Math.Ceiling(area.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var pictured = MediaCard.Live.Where(x => x.Pictured).ToList();
        foreach (var card in pictured) card.Pictured = false;
        try { bitmap.Render(visual); }
        finally { foreach (var card in pictured) card.Pictured = true; }
        bitmap.Freeze(); return bitmap;
    }
    private static string SaveShot(string folder, string file, BitmapSource bitmap)
    {
        BitmapEncoder encoder = file.EndsWith(".png", StringComparison.Ordinal) ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 93 };
        encoder.Frames.Add(BitmapFrame.Create(encoder is PngBitmapEncoder ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)));
        using var stream = File.Create(Path.Combine(folder, file)); encoder.Save(stream);
        return file;
    }
}
