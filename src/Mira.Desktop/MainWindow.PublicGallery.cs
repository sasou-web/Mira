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
            async Task Page(string view, string name)
            {
                SettingsOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
                ResetFilters(); _view = view; RenderDemo(); SmoothScroll.Jump(LibraryScroll);
                await _heroReady; await CaptureAsync(output, name);
            }
            await Page("home", "01-home");
            await Page("library", "02-library");
            await Page("Movie", "03-films");
            await Page("Series", "04-series");
            await Page("favorites", "05-favorites");
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
            // Help for someone new: the welcome, the main points of an update, the guide and an empty library.
            // Each screen must fit the window, down to the smallest size Mira allows (1024 × 700).
            ShowWelcome(); await CaptureAsync(output, "15-welcome"); AssertContained(WelcomeContent, "Welcome");
            Width = 1024; Height = 720; await CaptureAsync(output, "15-welcome-compact"); AssertContained(WelcomeContent, "Compact welcome");
            Width = 1440; Height = 960; WelcomeOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
            ShowWhatsNew(WhatsNew.All.Take(1).ToList()); await CaptureAsync(output, "16-whats-new"); AssertContained(WhatsNewCard, "What's new");
            ShowWhatsNew(WhatsNew.All.Take(2).ToList()); Width = 1024; Height = 720; await CaptureAsync(output, "17-whats-new-compact"); AssertContained(WhatsNewCard, "Compact what's new");
            Width = 1440; Height = 960; WhatsNewOverlay.Visibility = Visibility.Collapsed;
            OpenGuide("Jellyfin est installé et configuré. Range tes vidéos dans C:\\Users\\Toi\\Videos\\Jellyfin : voici comment, et tout ce qu’il faut savoir pour la suite.");
            // Fictional folders and address: the demo has no server to read them from.
            await Task.Delay(50); GuideFolders.Children.Clear(); GuideFoldersHint.Visibility = Visibility.Collapsed;
            foreach (var (library, folder) in new[] { ("Animes", "Animes"), ("Films", "Films"), ("Séries", "Séries") }) GuideFolders.Children.Add(FolderRow(library, $@"C:\Users\Toi\Videos\Jellyfin\{folder}", true));
            GuideJellyfinActions.Visibility = GuideRemote.Visibility = Visibility.Visible; GuideServerAddress.Text = "http://127.0.0.1:8096";
            GuideRemoteAddress.Text = "http://192.168.1.20:8096";
            GuideRemoteHint.Text = "Ce PC doit rester allumé : Jellyfin y tourne en arrière-plan, même quand Mira est fermée. Les appareils doivent être connectés à la même box.";
            await CaptureAsync(output, "18-guide"); AssertContained(GuideContent, "Guide");
            SmoothScroll.Jump(GuideScroll, GuideScroll.ScrollableHeight); await CaptureAsync(output, "19-guide-end");
            GuideOverlay.Visibility = Visibility.Collapsed;
            _demo = false; _items = []; _resume = []; _nextUp = []; _totalCount = 0; _catalogLoading = false; _view = "home"; RenderLibrary();
            await CaptureAsync(output, "20-empty-library"); AssertContained(EmptyState, "Empty library");
            if (EmptyGuide.Visibility != Visibility.Visible) throw new InvalidOperationException("The empty library does not lead to the guide.");
            _demo = true;
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), "PASS: 21 application views; built-in fictional catalogue; no account, server or personal media used.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), "FAIL: " + ex.GetType().Name);
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }
}
