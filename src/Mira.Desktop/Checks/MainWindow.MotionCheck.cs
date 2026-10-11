using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    private async Task RunMotionChecksAsync(string output)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var checks = new List<string>();
        SettingsOverlay.Visibility = DetailOverlay.Visibility = Visibility.Collapsed;
        Width = 1440; Height = 900; _view = "home"; ResetFilters(); await RefreshAsync(); await SettleVisualAsync(); await Task.Delay(500);
        var originalCards = PosterCards.Children.Cast<MediaCard>().ToArray(); RenderLibrary();
        Require(PosterCards.Children.Cast<MediaCard>().SequenceEqual(originalCards), "Unchanged refresh recreated cards."); checks.Add("PASS unchanged cards retain their instances across refresh");
        if (_resume.Count + _nextUp.Count > 0)
        {
            SmoothScroll.Jump(LibraryScroll); await Task.Delay(50);
            var card = (MediaCard)ResumeCards.Children[0];
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            card.RaiseEvent(wheel); Require(wheel.Handled, "Nested resume row swallowed vertical wheel.");
            await Task.Delay(65); var middle = LibraryScroll.VerticalOffset;
            await Task.Delay(550); var end = LibraryScroll.VerticalOffset;
            Require(middle > 0 && end > middle && end < 160, $"Wheel did not interpolate: {middle:0.0} -> {end:0.0}");
            checks.Add($"PASS wheel over resume card interpolates {middle:0.0} -> {end:0.0} px");
        }
        SmoothScroll.Jump(LibraryScroll); await Task.Delay(50);
        var firstHero = _hero?.Id; _carouselTime.Advance(8, false); HeroFrame(this, EventArgs.Empty); await _heroReady;
        Require(_heroCandidates.Count < 2 || _hero?.Id != firstHero, "Carousel failed to advance.");
        _carouselTime.Advance(_carouselTime.Duration / 2, false); UpdateHeroSegments();
        Require(_heroSegments.Any(x => x.Progress is > .45 and < .55), "Carousel progress bar did not represent elapsed time.");
        await CaptureAsync(output, "14-carousel-progress"); checks.Add("PASS carousel advances and progress segment reaches 50 percent");
        _view = "library"; await RefreshAsync(); await SettleVisualAsync();
        var poster = (MediaCard)PosterCards.Children[0];
        await Task.Delay(180);
        CardHoverEntered(poster);
        Require(poster.HoverVisible, "Integrated hover must appear without waiting for metadata.");
        await Task.Delay(45);
        Require(poster.HoverAmount > 0 && poster.HoverAmount < 1, "Hover must interpolate instead of snapping.");
        Require(Math.Abs(poster.HighlightOpacity - (poster.ArtworkZoom - 1) / (MediaCard.HoverZoom - 1)) < .001, "Highlight and zoom must share one clock.");
        var midway = poster.HoverAmount; poster.SetHover(false);
        Require(Math.Abs(poster.HoverAmount - midway) < .025, "Reversing hover must keep the current visual position.");
        poster.SetHover(true); await Task.Delay(190);
        checks.Add("PASS hover reacts immediately, interpolates highlight and zoom together, and reverses without a jump");
        await CaptureAsync(output, "15-integrated-hover");
        var originalWidth = poster.ActualWidth;
        var pointer = poster.TranslatePoint(new Point(poster.ActualWidth / 2, 70), LibraryShell);
        TrackHoverPointer(pointer); Require(_hoverCard == poster, "Hit testing must select the card under the pointer.");
        var step = poster.ActualHeight + poster.Margin.Top + poster.Margin.Bottom;
        LibraryScroll.ScrollToVerticalOffset(step); await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded); UpdateLayout();
        TrackHoverPointer(pointer);
        Require(_hoverCard is { } nextCard && nextCard != poster && nextCard.HoverVisible && !poster.HoverVisible, "Scrolling must transfer hover to the card under the stationary pointer.");
        Require(Math.Abs(poster.ActualWidth - originalWidth) < .1, "Hover must never resize card layout.");
        SmoothScroll.By(LibraryScroll, 85); await Task.Delay(35); TrackHoverPointer(pointer);
        Require(_hoverCard == CardAtLibraryPoint(pointer) && _hoverCard?.HoverVisible == true, "Hover must track before scroll inertia has ended.");
        Require(!LogicalText(poster).Any(x => x.Contains("Voir la fiche")), "Poster hover must not carry a caption.");
        Require(TitleBar.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 0, "Scrolling must not paint an opaque title bar.");
        checks.Add("PASS moving content transfers hover under a stationary pointer during scroll inertia without changing layout");
        checks.Add("PASS scrolling leaves the title bar transparent");
        SmoothScroll.Jump(LibraryScroll, LibraryScroll.ScrollableHeight); await Task.Delay(200);
        var lastPoster = (MediaCard)PosterCards.Children[^1]; CardHoverEntered(lastPoster);
        Require(lastPoster.HoverVisible, "Last row hover is available after scrolling.");
        await CaptureAsync(output, "16-hover-bottom-edge"); ClosePreview();
        var timer = Stopwatch.StartNew(); var details = ShowDetailsAsync(poster.Item);
        Require(DetailOverlay.IsVisible && DetailTitle.Text == poster.Item.DisplayTitle, "Detail content waits for the network.");
        checks.Add($"PASS detail title is presented synchronously in {timer.ElapsedMilliseconds} ms before awaiting metadata");
        await details; await CaptureAsync(output, "17-detail-ready");
        var oldReduced = Motion.Reduced; Motion.Reduced = true;
        SmoothScroll.Jump(DetailScroll); await Task.Delay(50); SmoothScroll.By(DetailScroll, 80); await Task.Delay(50);
        Require(DetailScroll.ScrollableHeight == 0 || Math.Abs(DetailScroll.VerticalOffset - Math.Min(80, DetailScroll.ScrollableHeight)) < 1, "Reduced motion must scroll immediately.");
        Motion.Reduced = oldReduced; checks.Add("PASS reduced motion scrolls without interpolation");
        Motion.Reveal(DetailOverlay, 0); var oldHide = Motion.HideAsync(DetailOverlay, 80); Motion.Reveal(DetailOverlay, 0);
        await oldHide; Require(DetailOverlay.IsVisible && DetailOverlay.IsHitTestVisible, "An old transition hid a newer page.");
        checks.Add("PASS rapid navigation keeps the latest page visible");
        await File.WriteAllLinesAsync(Path.Combine(output, "motion-checks.txt"), checks);
    }
    private static IEnumerable<string> LogicalText(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var value in LogicalText(child)) yield return value;
    }
}
