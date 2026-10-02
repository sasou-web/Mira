using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    // An opt-in, read-only UI smoke check. Use a separate --data profile: no playback or library mutations.
    private async Task RunVisualCheckAsync()
    {
        var index = Array.IndexOf(_args, "--visual-check");
        var output = Path.GetFullPath(_args[index + 1]); Directory.CreateDirectory(output);
        try
        {
            BrandAssets.Export(output);
            if (!_startupComplete || StartupVeil.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Startup veil did not release the interface.");
            if (!new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var glyphs) || !glyphs.FontUri.ToString().Contains("NunitoSans", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Bundled font was not resolved.");
            if (_items.Count == 0) { await CaptureAsync(output, "00-empty"); await File.WriteAllTextAsync(Path.Combine(output, "connection-check.txt"), NoticeText.Text + "\n" + LoginError.Text + "\n" + SyncLabel.Text); throw new InvalidOperationException("Visual check requires a connected library or --demo."); }
            Width = 1440; Height = 900;
            await SettleVisualAsync(); await CaptureAsync(output, "01-home");
            await File.WriteAllTextAsync(Path.Combine(output, "layout.txt"), $"Scroll style active: {LibraryScroll.Style == FindResource(typeof(ScrollViewer))}; template active: {LibraryScroll.Template == ((Style)FindResource(typeof(ScrollViewer))).Setters.OfType<Setter>().First(s => s.Property == Control.TemplateProperty).Value}; genre choices: {GenreFilter.Items.Count}; year choices: {YearFilter.Items.Count}");
            _view = "library"; await RefreshAsync(); await SettleVisualAsync(); await CaptureAsync(output, "02-library");
            AssertContained(PosterCards, "Poster grid"); AssertContained(SortFilter, "Catalog filters");
            if (!_demo && _items.Any(x => x.Genres.Length > 0) && GenreFilter.Items.Count < 2) throw new InvalidOperationException("Missing genre facets.");
            if (GenreFilter.Items.Count > 1)
            {
                _filterBusy = true; GenreFilter.SelectedIndex = 1; _filterBusy = false;
                await RefreshAsync();
                if (_items.Any(x => !x.Genres.Contains(Choice(GenreFilter)))) throw new InvalidOperationException("Genre filter did not apply.");
                await CaptureAsync(output, "12-filtered");
            }
            _filterBusy = true; SearchBox.Text = "mira-ui-test-no-matching-title-93718"; _filterBusy = false; await RefreshAsync();
            if (_items.Count != 0 || EmptyState.Visibility != Visibility.Visible) throw new InvalidOperationException("Empty state did not appear.");
            await CaptureAsync(output, "13-empty-search"); ResetFilters(); await RefreshAsync();
            var series = _items.FirstOrDefault(x => x.Type == "Series") ?? _items[0];
            await ShowDetailsAsync(series);
            if (_images is { } cache) await Task.WhenAll(_episodes.Select(x => cache.EpisodeAsync(x)));
            await CaptureAsync(output, "03-detail");
            Settings_Click(this, new()); await CaptureAsync(output, "04-playback-settings");
            SelectSettingsTab("subtitles"); await CaptureAsync(output, "05-subtitle-settings");
            SelectSettingsTab("appearance"); await CaptureAsync(output, "06-appearance-settings");
            if (HeroAutoPlayCheck.IsChecked == true && (HeroAutoPlayCheck.Template.FindName("Track", HeroAutoPlayCheck) as Border)?.Background is SolidColorBrush switchBrush && switchBrush.Color.R != 245) throw new InvalidOperationException("Enabled switch looks disabled.");
            SelectSettingsTab("server"); await CaptureAsync(output, "07-server-settings");
            SettingsOverlay.Visibility = Visibility.Collapsed; _view = "home"; await RefreshAsync();
            Width = 1920; Height = 1080; await SettleVisualAsync(); await CaptureAsync(output, "08-home-wide");
            Width = 1024; Height = 720; await CaptureAsync(output, "09-home-small");
            _view = "library"; await RefreshAsync(); await CaptureAsync(output, "10-library-small");
            AssertContained(SortFilter, "Small catalog filters");
            Settings_Click(this, new()); SelectSettingsTab("subtitles"); await CaptureAsync(output, "11-settings-small");
            AssertContained(SettingsScroll, "Small settings");
            await RunMotionChecksAsync(output);
            SettingsOverlay.Visibility = DetailOverlay.Visibility = Visibility.Collapsed;
            LoginArt.Source = null; UsernameBox.Text = ""; PasswordBox.Clear(); LoginError.Text = "";
            LogoutButton.Visibility = BackToLibrary.Visibility = Visibility.Collapsed;
            LoginOverlay.Visibility = Visibility.Visible; LoginOverlay.Opacity = 1;
            Width = 1440; Height = 900; await CaptureAsync(output, "18-login");
            AssertContained(LoginPanel, "Login panel");
            Width = 1024; Height = 700; await CaptureAsync(output, "19-login-small"); AssertContained(LoginPanel, "Small login panel");
            var loginBottom = ConnectButton.TranslatePoint(new Point(0, ConnectButton.ActualHeight), (UIElement)Content).Y;
            if (loginBottom > ActualHeight - 25) throw new InvalidOperationException("Login action falls below the window.");
            LoginArt.Source = HeroImage.Source; BackToLibrary.Visibility = LogoutButton.Visibility = Visibility.Visible;
            Width = 1440; Height = 900; await CaptureAsync(output, "20-account");
            await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS: screens rendered at 1024, 1440 and 1920 pixels; genre facets, filtering, empty state and layout bounds checked. No playback or library mutations performed.");
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "FAIL: " + ex.GetType().Name + "\n" + ex.StackTrace); }
        finally { Close(); }
    }
    private async Task SettleVisualAsync()
    {
        if (_images is { } cache) { await Task.WhenAll(_items.Select(x => cache.GetAsync(x)).Concat(_resume.Concat(_nextUp).Select(x => cache.LandscapeAsync(x, 720)))); if (_hero is { } hero) await LoadHeroImageAsync(hero); }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private async Task CaptureAsync(string directory, string name)
    {
        if (!Motion.Reduced) await Task.Delay(440);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); UpdateLayout();
        var visual = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(file);
    }
    /// <summary>The element, as drawn (a Viewbox may scale it), lies wholly inside the window: nothing to scroll to.</summary>
    private void AssertFits(FrameworkElement element, string label)
    {
        var root = (FrameworkElement)Content;
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > root.ActualWidth + 1 || bounds.Bottom > root.ActualHeight + 1)
            throw new InvalidOperationException($"{label} does not fit the window: {bounds} in {root.ActualWidth:0} × {root.ActualHeight:0}.");
    }
    /// <summary>
    /// Maximized, Mira's content covers the screen's work area exactly: none of it (title bar buttons, rail, bottom
    /// edge) hidden in the resize frame Windows puts beyond the screen. Returns what was measured, in physical pixels.
    /// </summary>
    private string AssertMaximizedOnScreen()
    {
        var root = (FrameworkElement)Content;
        var work = Playback.FullscreenWindow.WorkArea(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var shown = new Rect(root.PointToScreen(new Point()), root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight)));
        var measured = $"content {shown.Left:0},{shown.Top:0} → {shown.Right:0},{shown.Bottom:0} on work area {work.Left:0},{work.Top:0} → {work.Right:0},{work.Bottom:0}";
        if (Math.Abs(shown.Left - work.Left) > 1 || Math.Abs(shown.Top - work.Top) > 1 || Math.Abs(shown.Right - work.Right) > 1 || Math.Abs(shown.Bottom - work.Bottom) > 1)
            throw new InvalidOperationException("The maximized window does not match the screen: " + measured + ".");
        return measured;
    }
    private void AssertContained(FrameworkElement element, string label)
    {
        var point = element.TranslatePoint(new Point(), (UIElement)Content);
        if (point.X < 0 || point.X + element.ActualWidth > ActualWidth + 1) throw new InvalidOperationException(label + " overflows the window.");
    }
}
