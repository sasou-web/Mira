using System.IO;
using System.Windows;
using Mira.Core;
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
                ResetFilters(); _view = view; _favorites = favorites; ShowFavoritesFilter(); RenderDemo(); SmoothScroll.Jump(LibraryScroll);
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
            _filterBusy = true; SearchBox.Text = "étoiles"; _filterBusy = false; RenderDemo();
            await CaptureAsync(output, "12-search");
            await Page("home", "01-home");
            Width = 1024; Height = 720; await CaptureAsync(output, "13-compact-window");
            Width = 1440; Height = 960;
            LoginArt.Source = null; UsernameBox.Text = ""; PasswordBox.Clear(); LoginError.Text = "";
            ServerBox.Text = "http://127.0.0.1:8096";
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
                WelcomeOverlay.Visibility = Visibility.Collapsed; ShowSignInForm(); LoginError.Text = "Nom d’utilisateur ou mot de passe incorrect."; await At(size, "16-sign-in", LoginPanel);
                LoginError.Text = ""; ShowServerSetup(true); SetupFolderBox.Text = @"C:\Users\Toi\Videos\Jellyfin"; SetupUserBox.Text = "Toi";
                SetupStatus.Text = "Accepte l’autorisation de Windows. Si l’installateur affiche « Could not start the Jellyfin Server service », choisis Ignorer.";
                await At(size, "17-jellyfin-setup", ServerSetupPanel);
                ShowSignInForm(); LoginOverlay.Visibility = Visibility.Collapsed;
                ShowWhatsNew(WhatsNew.All.Take(3).ToList()); await At(size, "18-whats-new", WhatsNewCard);
                WhatsNewOverlay.Visibility = Visibility.Collapsed;
            }
            OpenGuide("Jellyfin est prêt. Range tes vidéos dans C:\\Users\\Toi\\Videos\\Jellyfin.");
            // Fictional folders and address: the demo has no server to read them from.
            await Task.Delay(50); GuideFolders.Children.Clear(); GuideFoldersHint.Visibility = Visibility.Collapsed; GuideTorLink.Visibility = Visibility.Visible;
            foreach (var (library, folder) in new[] { ("Animes", "Animes"), ("Films", "Films"), ("Séries", "Séries") }) GuideFolders.Children.Add(FolderRow(library, $@"C:\Users\Toi\Videos\Jellyfin\{folder}", true));
            GuideJellyfinActions.Visibility = GuideRemote.Visibility = Visibility.Visible; GuideServerAddress.Text = "http://127.0.0.1:8096";
            GuideRemoteAddress.Text = "http://192.168.1.20:8096";
            GuideRemoteHint.Text = "Ce PC doit rester allumé et sur la même box. Si un appareil ne trouve pas le serveur, autorise Jellyfin dans le pare-feu de Windows.";
            // Away from home, before Tailscale is installed: what to do.
            _awayAddress = null; GuideAway.Visibility = Visibility.Visible; ShowAway(null);
            foreach (var size in new[] { sizes[0], sizes[2] }) { SmoothScroll.Jump(GuideScroll); await At(size, "19-guide", null, GuideContent); }
            GuideOverlay.Visibility = Visibility.Collapsed;
            // The downloads page before TorLink is turned on: the list of downloaders.
            TorLinkOverlay.Visibility = Visibility.Visible; TorLinkOverlay.Opacity = 1; ShowDownloaders(true);
            foreach (var size in new[] { sizes[0], sizes[2] }) await At(size, "20-downloads", null, DownloadersPanel);
            TorLinkOverlay.Visibility = Visibility.Collapsed;
            _demo = false; _items = []; _resume = []; _nextUp = []; _totalCount = 0; _catalogLoading = false; _view = "home"; RenderLibrary();
            await At(sizes[2], "21-empty-library", null, EmptyState);
            if (EmptyGuide.Visibility != Visibility.Visible) throw new InvalidOperationException("The empty library does not lead to the guide.");
            _demo = true;
            // Maximized, as Mira opens on a screen smaller than its default size: everything on the screen, to its edges.
            ResetFilters(); _view = "home"; RenderDemo(); SmoothScroll.Jump(LibraryScroll); WindowState = WindowState.Maximized; await CaptureAsync(output, "22-maximized");
            drawn.Add($"22-maximized (Windows {Environment.OSVersion.Version.Build}): {AssertMaximizedOnScreen()}");
            WindowState = WindowState.Normal;
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
}
