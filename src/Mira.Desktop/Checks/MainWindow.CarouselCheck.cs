using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The home banner against a real Jellyfin, on its own clock (never <see cref="HeroFrame"/> called by hand, as the motion
    /// check does): it moves on by itself, a real press on another bar shows that title, and the bars show no wash under
    /// the pointer (0.9.7: a wash, and a banner stuck on its first title).
    /// </summary>
    private async Task CheckCarouselAsync(List<string> checks, Action<bool, string> require)
    {
        string State() => $"candidates {_heroCandidates.Count}, shown {_hero?.DisplayTitle ?? "none"}, busy {_heroBusy}, clock {_heroClockAttached}, progress {_carouselTime.Progress:0.00}, "
            + $"active {IsActive}, view {_catalog.View}, library shown {LibraryShell.IsVisible}, reduced {Motion.Reduced}, auto {_settings.HeroAutoPlay}, playing {_playing}, "
            + $"over copy {HeroCopy.IsMouseOver} synopsis {HeroSynopsis.IsMouseOver} bars {HeroDots.IsMouseOver}, keyboard {_keyboardNavigation && Hero.IsKeyboardFocusWithin}, "
            + $"scrolled {LibraryScroll.VerticalOffset:0}, overlays {string.Join(" ", new FrameworkElement[] { DetailOverlay, SettingsOverlay, TorLinkOverlay, LoginOverlay, PartyOverlay, GuideOverlay, WelcomeOverlay, WhatsNewOverlay }.Where(x => x.Visibility == Visibility.Visible).Select(x => x.Name))}";
        if (_heroCandidates.Count < 2) { checks.Add("INFO the banner has a single title: " + State()); return; }
        var placed = (Left, Top);
        try
        {
            // The window's top left at the screen's, the pointer away from the banner: nothing holds it still.
            Left = 0; Top = 0; Activate(); UpdateLayout(); SmoothScroll.Jump(LibraryScroll);
            SetCursorPos(2, 2); await Task.Delay(400); UpdateHeroClock();
            var first = _hero?.Id;
            for (var timer = Stopwatch.StartNew(); _hero?.Id == first; await Task.Delay(100))
                if (timer.Elapsed.TotalSeconds > _carouselTime.Duration + 8) { require(false, "the banner moves on by itself: " + State()); return; }
            require(true, $"the banner moves on by itself, on its own clock ({State()})");

            var bars = HeroDots.Children.OfType<Button>().ToList();
            var index = _heroCandidates.FindIndex(x => x.Id == _hero?.Id);
            var target = (index + 2) % _heroCandidates.Count;
            if (target >= bars.Count) { checks.Add("INFO bars and titles differ: " + State()); return; }
            var spot = bars[target].PointToScreen(new Point(bars[target].ActualWidth / 2, bars[target].ActualHeight / 2));
            var work = SystemParameters.WorkArea; var dpi = VisualTreeHelper.GetDpi(this);
            if (spot.X < 0 || spot.Y < 0 || spot.X >= work.Right * dpi.DpiScaleX || spot.Y >= work.Bottom * dpi.DpiScaleY) { checks.Add($"INFO the bars are off the screen ({spot}): no real press"); return; }
            var point = ((int)Math.Round(spot.X), (int)Math.Round(spot.Y));
            SetCursorPos(point.Item1, point.Item2); await Task.Delay(300);
            var wash = bars[target].Template?.FindName("Hover", bars[target]) is UIElement layer ? layer.Opacity : 0;
            require(bars[target].IsMouseOver && wash == 0, $"a bar under the pointer shows no wash (under it {bars[target].IsMouseOver}, wash {wash:0.00})");
            var wanted = _heroCandidates[target].Id;
            await Press(point);
            for (var timer = Stopwatch.StartNew(); _hero?.Id != wanted; await Task.Delay(100))
                if (timer.Elapsed.TotalSeconds > 8) { require(false, $"a real press on a bar shows its title ({_heroCandidates[target].DisplayTitle}): " + State()); return; }
            require(true, $"a real press on a bar shows its title ({_hero?.DisplayTitle})");
        }
        finally { SetCursorPos(2, 2); (Left, Top) = placed; UpdateLayout(); }
    }
}
