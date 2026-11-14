using System.Windows;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Mira's opening (« Le clin d'œil ») in the public gallery's real window: the start of this run blinked (not faded),
    /// then the opening drawn again moment by moment on the Home page, its clock held (27-opening-*): the page takes
    /// clicks from the first moment, the mark shuts where it stood to a line as wide as it, then goes out with the black;
    /// the rail is never touched (its mark and the page's tile shown throughout). Once over, the veil gone and its mark
    /// put back. Without motion: at once.
    /// </summary>
    private async Task<string> CheckOpeningAsync(string output)
    {
        void Fail(string what) => throw new InvalidOperationException("Opening: " + what);
        if (!_openingReport.StartsWith("blinked", StringComparison.Ordinal)) Fail($"this run's start did not blink ({_openingReport}).");
        var started = _openingReport;

        SettingsOverlay.Visibility = GuideOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Normal; Width = 1440; Height = 900;
        ResetFilters(); _catalog.View = "home"; RenderDemo(); SmoothScroll.Jump(LibraryScroll);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (ShownRailButton() != HomeNav) Fail("Accueil is not the page shown.");

        Rect Bounds(FrameworkElement element) => element.TransformToVisual(StartupVeil).TransformBounds(new Rect(element.RenderSize));
        StartupVeil.Visibility = Visibility.Visible; StartupVeil.IsHitTestVisible = true; StartupVeil.Opacity = 1;
        UpdateLayout();
        var still = Bounds(StartupMark);
        if (!StartOpening(frozen: true)) Fail("it would not blink on the Home page.");
        var drawn = new List<string>();
        foreach (var ms in new[] { 0, 60, 120, 180, 250, 310 })
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
            // The rail is left as it is: its mark and the page's tile in place under the black all along.
            if (RailMark.Opacity < 1 || RailIndicator.Opacity < 1) Fail($"the rail's mark or tile is hidden at {ms} ms.");
        }
        // Shut: where it stood, its centre unmoved, a line a little wider than the mark and 5.5 % of its height.
        DrawOpening(OpeningBlink.Shut);
        UpdateLayout();
        var line = Bounds(StartupMark);
        if (Math.Abs(line.Left + line.Width / 2 - (still.Left + still.Width / 2)) > .5 || Math.Abs(line.Top + line.Height / 2 - (still.Top + still.Height / 2)) > .5
            || Math.Abs(line.Width - still.Width * OpeningBlink.Wide) > .5 || Math.Abs(line.Height - still.Height * OpeningBlink.Slit) > .5 || StartupMark.Opacity < 1)
            Fail($"shut, the mark is at {line}; it stood at {still}.");
        DrawOpening(OpeningBlink.Length);
        if (StartupMark.Opacity > 1e-6 || StartupShade.Opacity > 1e-6 || StartupWord.Opacity > 1e-6) Fail("the line or the black is still there once over.");
        FinishOpening();
        if (StartupVeil.Visibility != Visibility.Collapsed || StartupMark.Opacity < 1 || StartupMark.RenderTransform is ScaleTransform) Fail("the veil is not gone, or its mark not put back.");

        // Without motion: the Home page at once, nothing winks.
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
