using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Views;

namespace Mira.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// « Mode canapé » in the public gallery's real window: full screen, the pages zoomed as <see cref="Couch.Zoom"/> says
    /// for the screen (a runner's 1024 × 768 keeps 1), the arrows from the page to the rail and back, then a poster opened
    /// by a controller's A and closed by its B, and the window as before once it is off.
    /// </summary>
    private async Task<string> CheckCouchAsync(string output)
    {
        void Fail(string what) => throw new InvalidOperationException("Couch mode: " + what);
        FrameworkElement? Focused() => Keyboard.FocusedElement as FrameworkElement;
        Rect Where(FrameworkElement control) => control.TransformToAncestor((Visual)Content).TransformBounds(new Rect(control.RenderSize));
        async Task Key(Key key) { Press(key); await Task.Delay(250); }

        SettingsOverlay.Visibility = DetailOverlay.Visibility = LoginOverlay.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Normal;
        ResetFilters(); _catalog.View = "Movie"; RenderDemo(); SmoothScroll.Jump(LibraryScroll);
        await _heroReady;
        SetCouchMode(true);
        await Task.Delay(600);
        var root = (FrameworkElement)Content;
        if (!_couchScreen || _fullscreenWindow is not { Active: true }) Fail("the window is not full screen.");
        if (Math.Abs(_zoom - Couch.Zoom(root.ActualWidth, root.ActualHeight)) > .01) Fail($"zoom {_zoom:0.00} for {root.ActualWidth:0} × {root.ActualHeight:0}.");
        if (MaximizeButton.IsVisible) Fail("the maximize button is still shown.");
        FocusFirst(); await Task.Delay(200);
        if (Focused() is not { } start || !start.IsDescendantOf(LibraryShell)) Fail("no control of the page has the focus at the start: " + Focused()?.GetType().Name);
        await CaptureAsync(output, "25-couch");

        // ↓ goes lower; ← reaches the rail, → comes back into the page.
        var top = Where(Focused()!).Top;
        await Key(System.Windows.Input.Key.Down);
        if (Focused() is not { } below || Where(below).Top <= top) Fail("↓ does not move the focus down.");
        for (var i = 0; i < 12 && Focused()?.IsDescendantOf(NavigationRail) != true; i++) await Key(System.Windows.Input.Key.Left);
        if (Focused()?.IsDescendantOf(NavigationRail) != true) Fail("← never reaches the rail.");
        await Key(System.Windows.Input.Key.Right);
        if (Focused()?.IsDescendantOf(LibraryShell) != true) Fail("→ does not come back into the page.");

        // Down to a poster, opened by a controller's A, closed by its B.
        for (var i = 0; i < 10 && Focused() is not MediaCard; i++) { Do(Couch.Action.Down); await Task.Delay(200); }
        if (Focused() is not MediaCard card) { Fail("↓ never reaches a poster."); return ""; }
        await CaptureAsync(output, "25b-couch-poster");
        Do(Couch.Action.Select); await Task.Delay(1200);
        if (DetailOverlay.Visibility != Visibility.Visible) Fail("A does not open the title.");
        await CaptureAsync(output, "25c-couch-title");
        Do(Couch.Action.Back); await Task.Delay(1200);
        if (DetailOverlay.Visibility == Visibility.Visible) Fail("B does not close the title.");
        // Backspace steps back too.
        Do(Couch.Action.Select); await Task.Delay(1200);
        await Key(System.Windows.Input.Key.Back); await Task.Delay(1000);
        if (DetailOverlay.Visibility == Visibility.Visible) Fail("Backspace does not close the title.");

        var zoom = _zoom;
        SetCouchMode(false);
        await Task.Delay(600);
        if (_couchScreen || _fullscreenWindow is { Active: true } || Math.Abs(_zoom - 1) > .01 || !MaximizeButton.IsVisible) Fail("turned off, the window is not as before.");
        return $"25-couch: zoom {zoom:0.00} at {root.ActualWidth:0} × {root.ActualHeight:0}, arrows, rail, poster {card.Item?.Name ?? "?"}, A, B, Backspace";
    }
}
