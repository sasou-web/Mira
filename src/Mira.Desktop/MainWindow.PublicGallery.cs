using System.IO;
using System.Windows;
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
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), "PASS: 14 application views; built-in fictional catalogue; no account, server or personal media used.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-result.txt"), "FAIL: " + ex.GetType().Name);
            Environment.ExitCode = 1;
        }
        finally { Close(); }
    }
}
