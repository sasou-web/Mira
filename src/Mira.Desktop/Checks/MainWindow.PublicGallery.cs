using System.IO;
using System.Windows;
using Mira.Core;
using Mira.Core.TorLink;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    // Render real views with the built-in fictional catalogue; no connection is used.
    private async Task RunPublicGalleryAsync()
    {
        var index = Array.IndexOf(_args, "--public-gallery");
        var output = Path.GetFullPath(_args[index + 1]); Directory.CreateDirectory(output);
        try
        {
            if (!_demo || _client is not null) throw new InvalidOperationException("Public captures require an offline demo.");
            _fallbackRefresh.Stop(); _searchTimer.Stop(); Width = 1440; Height = 960;
            BrandAssets.Export(output);
            async Task Page(string view, string name, bool favorites = false)
            {
                SettingsOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
                ResetFilters(); _catalog.View = view; _catalog.Favorites = favorites; ShowFavoritesFilter(); RenderDemo(); SmoothScroll.Jump(LibraryScroll);
                await _heroReady; await CaptureAsync(output, name);
            }
            await Page("home", "01-home");
            await Page("library", "02-library");
            await Page("Movie", "03-films");
            await Page("Series", "04-series");
            await Page("library", "05-favorites", favorites: true);
            await ShowDetailsAsync(_demoItems!.First(x => x.Type == "Movie"));
            await CaptureAsync(output, "06-film-details");
            Settings_Click(this, new()); await CaptureAsync(output, "07-playback-settings");
            SelectSettingsTab("subtitles"); await CaptureAsync(output, "08-subtitles-settings");
            SelectSettingsTab("appearance"); await CaptureAsync(output, "09-appearance-settings");
            SelectSettingsTab("server"); await CaptureAsync(output, "10-server-settings");
            await Page("library", "11-hover");
            CardHoverEntered((MediaCard)PosterCards.Children[0]);
            await CaptureAsync(output, "11-hover"); ClosePreview();
            _filterBusy = true; SearchBox.Text = L("étoiles", "stars"); _filterBusy = false; RenderDemo();
            await CaptureAsync(output, "12-search");
            await Page("home", "01-home");
            Width = 1024; Height = 720; await CaptureAsync(output, "13-compact-window");
            Width = 1440; Height = 960;
            LoginArt.Source = null; UsernameBox.Text = ""; PasswordBox.Clear(); LoginError.Text = "";
            // The server the login screen found on the home network (fictional, like the rest).
            _loginServers = []; UseServer(new("http://192.168.1.20:8096/", "MEDIA-PC", "demo", ServerSearch.Place.Home));
            BackToLibrary.Visibility = LogoutButton.Visibility = Visibility.Collapsed;
            LoginOverlay.Visibility = Visibility.Visible; LoginOverlay.Opacity = 1;
            await CaptureAsync(output, "14-login");
            // Help for someone new, at the sizes of a 1080p screen (100, 125 and 150 % scaling) and of Mira's smallest
            // window: the welcome, sign-in, Jellyfin setup and "what's new" must fit whole, with nothing to scroll to.
            var sizes = new (int Width, int Height, string Name)[] { (1920, 1032, "1080p-100"), (1536, 826, "1080p-125"), (1280, 688, "1080p-150"), (960, 600, "minimum") };
            var drawn = new List<string>();
            async Task At((int Width, int Height, string Name) size, string name, FrameworkElement? fit, FrameworkElement? across = null)
            {
                Width = size.Width; Height = size.Height; await CaptureAsync(output, $"{name}-{size.Name}");
                var root = (FrameworkElement)Content; drawn.Add($"{name}-{size.Name}: {root.ActualWidth:0} × {root.ActualHeight:0}");
                if (fit is not null) AssertFits(fit, $"{name} at {size.Name}");
                if (across is not null) AssertContained(across, $"{name} at {size.Name}");
            }
            foreach (var size in sizes)
            {
                LoginOverlay.Visibility = Visibility.Visible; LoginOverlay.Opacity = 1; ShowWelcome(); await At(size, "15-welcome", WelcomeContent);
                WelcomeOverlay.Visibility = Visibility.Collapsed; ShowSignInForm(); LoginError.Text = L("Nom d’utilisateur ou mot de passe incorrect.", "Wrong username or password."); await At(size, "16-sign-in", LoginPanel);
                LoginError.Text = ""; ShowServerSetup(true); SetupFolderBox.Text = $@"C:\Users\{You}\Videos\Jellyfin"; SetupUserBox.Text = You;
                SetupStatus.Text = L("Accepte l’autorisation de Windows. Si l’installateur affiche « Could not start the Jellyfin Server service », choisis Ignorer.", "Accept Windows’ permission prompt. If the installer shows “Could not start the Jellyfin Server service”, choose Ignore.");
                await At(size, "17-jellyfin-setup", ServerSetupPanel);
                ShowSignInForm(); LoginOverlay.Visibility = Visibility.Collapsed;
                ShowWhatsNew(WhatsNew.All.Take(3).ToList()); await At(size, "18-whats-new", WhatsNewCard);
                WhatsNewOverlay.Visibility = Visibility.Collapsed;
            }
            OpenGuide(L($@"Jellyfin est prêt. Range tes vidéos dans C:\Users\{You}\Videos\Jellyfin.", $@"Jellyfin is ready. Put your videos in C:\Users\{You}\Videos\Jellyfin."));
            // Fictional folders and address: the demo has no server to read them from.
            await Task.Delay(50); GuideFolders.Children.Clear(); GuideFoldersHint.Visibility = Visibility.Collapsed; GuideTorLink.Visibility = Visibility.Visible;
            foreach (var library in new[] { L("Animes", "Anime"), L("Films", "Movies"), L("Séries", "Shows") }) GuideFolders.Children.Add(FolderRow(library, $@"C:\Users\{You}\Videos\Jellyfin\{library}", true));
            GuideJellyfinActions.Visibility = GuideRemote.Visibility = Visibility.Visible; GuideServerAddress.Text = "http://127.0.0.1:8096";
            GuideRemoteAddress.Text = "http://192.168.1.20:8096";
            // The phone, half ready: Mira web there, the firewall still in the way, Tailscale not installed yet.
            ++_phoneVersion; _phone = null; _phoneReadAt = DateTime.MaxValue;
            ShowPhone(new PhoneSetup.Facts("http://127.0.0.1:8096", true, Web: true, Home: "http://192.168.1.20:8096", Admin: true, TailnetAllowed: false,
                Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Private), Boot: new(true, true, false, true)));
            foreach (var size in new[] { sizes[0], sizes[2] }) { SmoothScroll.Jump(GuideScroll); await At(size, "19-guide", null, GuideContent); }
            // The phone's card whole, with « Tout préparer » and the address to type.
            foreach (var size in new[] { sizes[0], sizes[2] })
            {
                Width = size.Width; Height = size.Height; UpdateLayout();
                SmoothScroll.Jump(GuideScroll, GuidePhone.TranslatePoint(new Point(0, 0), GuideContent).Y); await At(size, "19-guide-phone", null, GuidePhone);
            }
            GuideOverlay.Visibility = Visibility.Collapsed;
            // The downloads page before TorLink is turned on: the list of downloaders.
            TorLinkOverlay.Visibility = Visibility.Visible; TorLinkOverlay.Opacity = 1; ShowDownloaders(true);
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20-downloads", null, DownloadersPanel);
            // Away from the PC of Jellyfin, the same page sends to it: what it has under way, and a search with the best
            // release of each episode first (fictional names; no relay is asked).
            ShowServerDownloadsSample();
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20-downloads-server", null, ServerDownloadsPanel);
            HideServerDownloads(); ShowDownloaders(false);
            TorLinkOverlay.Visibility = Visibility.Collapsed;
            _demo = false; _catalog.Items = []; _catalog.Resume = []; _catalog.NextUp = []; _catalog.TotalCount = 0; _catalog.Loading = false; _catalog.View = "home"; RenderLibrary();
            await At(sizes[2], "21-empty-library", null, EmptyState);
            if (EmptyGuide.Visibility != Visibility.Visible) throw new InvalidOperationException("The empty library does not lead to the guide.");
            _demo = true;
            // Maximized, as Mira opens on a screen smaller than its default size: everything on the screen, to its edges.
            ResetFilters(); _catalog.View = "home"; RenderDemo(); SmoothScroll.Jump(LibraryScroll); WindowState = WindowState.Maximized; await CaptureAsync(output, "22-maximized");
            drawn.Add($"22-maximized (Windows {Environment.OSVersion.Version.Build}): {AssertMaximizedOnScreen()}");
            WindowState = WindowState.Normal;
            // A title that cannot play: the player's own screen, then « Retour » back to the library.
            PlaybackFailed(L("mpv n’a pas pu lire ce média. Vérifie l’accès au fichier et la connexion Jellyfin.", "mpv couldn’t play this media. Check access to the file and the connection to Jellyfin."), _demoItems!.First(x => x.Type == "Movie"), mini: false, RetryText, () => Task.CompletedTask);
            foreach (var size in new[] { sizes[0], sizes[3] }) await At(size, "23-player-error", null, PlayerError);
            GoBack();
            if (PlayerErrorShown || PlayerShell.IsVisible || !LibraryShell.IsVisible) throw new InvalidOperationException("« Retour » does not leave the player's error screen.");
            await File.WriteAllLinesAsync(Path.Combine(output, "sizes.txt"), drawn);
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"PASS: {14 + drawn.Count} application views; built-in fictional catalogue; no account, server or personal media used.");
        }
        catch (Exception ex)
        {
            // The message names the view and the sizes; the gallery only ever shows the fictional catalogue.
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), $"FAIL: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }

    /// <summary>The Windows account of the captures' sample paths.</summary>
    private static string You => L("Toi", "You");

    private void ShowServerDownloadsSample()
    {
        _serverScreen = true;
        DownloadersPanel.Visibility = TorLinkTerminalHost.Visibility = Visibility.Collapsed;
        DownloadersButton.Visibility = TorLinkFolderButton.Visibility = TorLinkSettingsButton.Visibility = TorLinkKeysHint.Visibility = TorLinkViewButton.Visibility = Visibility.Collapsed;
        ServerDownloadsPanel.Visibility = Visibility.Visible; ServerDownloadsPanel.Opacity = 1;
        const long GiB = 1L << 30, MiB = 1L << 20;
        var now = DateTimeOffset.UtcNow;
        static string Hash(char c) => new(c, 40);
        RenderServerStatus(new RelayStatus(new("SALON-PC", true, now, true, null),
            [new("r1", "Les.Etoiles.du.Nord.S01E05.MULTI.1080p.WEB-DL.H264-NOTAG", 3 * GiB / 2, Hash('b'), "waiting", null, now)], [],
            [new(Hash('a'), "Les.Etoiles.du.Nord.S01E04.MULTI.1080p.WEB-DL.H264-NOTAG", "downloading", 41, 2 * MiB, 12, 29 * GiB / 10, 12 * GiB / 10, 750),
             new(Hash('c'), "La.Maison.du.Lac.2023.1080p.BluRay.x265.10bit-NOTAG", "paused", 63, 0, 0, 4 * GiB, 5 * GiB / 2)],
            [new(Hash('d'), "Le Dernier Phare (2022)", L("disponible dans Jellyfin", "available in Jellyfin"), null, true)]), now);
        ServerQueryBox.Text = "Les Étoiles du Nord"; ServerCategory.SelectedIndex = 1;
        ShowServerHits("sample", "Les Étoiles du Nord", "tv",
        [
            new("1", "Les.Etoiles.du.Nord.S01E05.MULTI.1080p.WEB-DL.H264-NOTAG", 3 * GiB / 2, 85, 4, "1337x", null),
            new("2", "Les Etoiles du Nord S01E05 1080p WEBRip x265-PLUME", 9 * GiB / 10, 240, 9, "tpb", null),
            new("3", "Les.Etoiles.du.Nord.S01E05.2160p.WEB-DL.DV.HEVC-NOTAG", 6 * GiB, 30, 2, "1337x", null),
            new("4", "Les.Etoiles.du.Nord.S01E04.MULTI.1080p.WEB-DL.H264-NOTAG", 3 * GiB / 2, 60, 3, "1337x", null),
            new("5", "Les.Etoiles.du.Nord.S01E04.VOSTFR.720p.HDTV.x264-NOTAG", 7 * GiB / 10, 12, 1, "tpb", null),
            new("6", "Les.Etoiles.du.Nord.S01.COMPLETE.MULTI.1080p.WEB-DL.x264-NOTAG", 9 * GiB, 20, 5, "1337x", null),
        ]);
    }
}
