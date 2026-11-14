using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Mira's opening (« Le m prend sa place ») in the public gallery's real window: the start of this run glided (not
    /// faded), then the opening drawn again moment by moment on the Home page, its clock held (27-opening-*): the page
    /// takes clicks from the first moment, the mark lands exactly on the rail's, the tile exactly on Accueil, whose ink
    /// is then the tile's; once over, the sliding tile is there and the veil gone. Without motion: at once.
    /// </summary>
    private async Task<string> CheckOpeningAsync(string output)
    {
        void Fail(string what) => throw new InvalidOperationException("Opening: " + what);
        if (!_openingReport.StartsWith("glided", StringComparison.Ordinal)) Fail($"this run's start did not glide ({_openingReport}).");
        var started = _openingReport;

        SettingsOverlay.Visibility = GuideOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Normal; Width = 1440; Height = 900;
        ResetFilters(); _catalog.View = "home"; RenderDemo(); SmoothScroll.Jump(LibraryScroll);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ShownRailButton() != HomeNav) Fail("Accueil is not the page shown.");

        Rect Bounds(FrameworkElement element) => element.TransformToVisual(StartupVeil).TransformBounds(new Rect(element.RenderSize));
        StartupVeil.Visibility = Visibility.Visible; StartupVeil.IsHitTestVisible = true; StartupVeil.Opacity = 1;
        if (!StartOpening(frozen: true)) Fail("it would not glide on the Home page.");
        var drawn = new List<string>();
        foreach (var ms in new[] { 0, 120, 250, 380, 470, 560, 1400 })
        {
            DrawOpening(ms / 1000d);
            await CaptureAsync(output, $"27-opening-{ms:0000}");
            drawn.Add($"27-opening-{ms:0000}");
            if (ms == 0)
            {
                // The Home page answers from the first frame: a click in the middle of the window reaches it, not the veil.
                var middle = new Point(ActualWidth / 2, ActualHeight / 2);
                var hit = InputHitTest(middle) as DependencyObject;
                if (hit is null || hit is Visual v && (v == StartupVeil || v.IsDescendantOf(StartupVeil))) Fail("the veil still takes the clicks as it goes.");
                if (_libraryCovered) Fail("the library is still hidden under the lifting black.");
            }
        }
        // Just before landing, the mark is where the rail's is (and the same size); landed, the rail's own.
        DrawOpening(OpeningGlide.Travel - 1e-4);
        UpdateLayout();
        var moving = Bounds(StartupMark); var rail = Bounds(RailMark);
        if (Math.Abs(moving.Left + moving.Width / 2 - (rail.Left + rail.Width / 2)) > 1 || Math.Abs(moving.Top + moving.Height / 2 - (rail.Top + rail.Height / 2)) > 1 || Math.Abs(moving.Width - rail.Width) > 1)
            Fail($"the mark lands at {moving}, the rail's is at {rail}.");
        DrawOpening(OpeningGlide.Travel);
        if (StartupMark.Visibility == Visibility.Visible || RailMark.Opacity < 1) Fail("the rail's own mark does not take over once landed.");
        // Over: the tile on Accueil exactly, its ink as on the tile.
        DrawOpening(OpeningGlide.Longest);
        var drop = RailDrop.Drawing.Bounds; var home = HomeNav.TransformToVisual(RailDrop).TransformBounds(new Rect(HomeNav.RenderSize));
        if (Math.Abs(drop.Left - home.Left) > .5 || Math.Abs(drop.Top - home.Top) > .5 || Math.Abs(drop.Width - home.Width) > .5 || Math.Abs(drop.Height - home.Height) > .5)
            Fail($"the tile lands at {drop}, Accueil is at {home}.");
        if (HomeNav.Foreground is not SolidColorBrush ink || Palette.Get("OnAccent") is not SolidColorBrush lit || ink.Color != lit.Color) Fail("Accueil's ink is not the tile's once it lands.");
        FinishOpening();
        if (StartupVeil.Visibility != Visibility.Collapsed || RailIndicator.Opacity < 1 || !RailDrop.Drawing.Bounds.IsEmpty) Fail("the sliding tile is not back, or the veil not gone.");

        // Without motion: the Home page at once, nothing glides.
        var reduced = Motion.Reduced; Motion.Reduced = true;
        try
        {
            StartupVeil.Visibility = Visibility.Visible; StartupVeil.IsHitTestVisible = true; StartupVeil.Opacity = 1;
            var at = Environment.TickCount64;
            await OpenAsync();
            if (StartupVeil.Visibility != Visibility.Collapsed || Environment.TickCount64 - at > 100 || !_openingReport.StartsWith("at once", StringComparison.Ordinal)) Fail($"without motion, the veil does not go at once ({_openingReport}).");
        }
        finally { Motion.Reduced = reduced; }
        return $"{string.Join(", ", drawn)}: start of this run {started}";
    }
}
