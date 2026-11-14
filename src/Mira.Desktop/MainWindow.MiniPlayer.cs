using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The mini-player, against the window's right edge. Tucked away by itself, a sliver of the video showing, it slides out
/// while the pointer rests on it and back once the pointer has left (<see cref="MiniDock"/>); a click on the picture brings
/// the whole player back. Dragged up or down, it rests at that height. The video's own window slides, a step at each frame
/// of the screen, as when the player shrinks (<c>MainWindow.PlayerMorph.cs</c>): the window clips what is past its edge.
/// </summary>
public partial class MainWindow
{
    private const double MiniWidth = MiniDock.Width, MiniHeight = MiniDock.Height, MiniRadius = 11, RailWidth = 68, TitleHeight = 34, MiniTop = TitleHeight + 8;
    /// <summary>Where it rests down the window, as a share of the way from under the title bar to the bottom.</summary>
    private double _miniShare = 1;
    /// <summary>Laid out against the edge, tucked away (or on its way there).</summary>
    private bool _miniTucked;
    private bool _miniDragging, _miniSliding;
    private int _miniSlide;
    private Point? _miniPress;
    private Point _miniGrab, _miniTopLeft;
    private readonly MiniDock _dock = new();
    private readonly Stopwatch _dockClock = Stopwatch.StartNew();
    private DispatcherTimer? _dockTimer;
    /// <summary>The pointer as the player check puts it; null, the real one (always, in the packages: Checks/ is left out).</summary>
    private Func<bool>? _miniPointerForCheck = null;
    /// <summary>A click on the mini-player brought the whole player back: the presses of the same double click go nowhere.</summary>
    private long _swallowPressesUntil;
    private bool MiniSettled => !_miniDragging && !_miniTucked && !_miniSliding;
    private FrameworkElement Root => (FrameworkElement)Content;

    /// <summary>Where it rests, out or tucked away, at its height.</summary>
    private Point MiniPoint(bool shown) => new(MiniDock.Left(shown, Root.ActualWidth), MiniDock.Top(_miniShare, MiniTop, Root.ActualHeight));
    private void PlaceMini(Point topLeft)
    {
        _miniTopLeft = topLeft;
        PlayerShell.Margin = MiniFrame.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
    }
    private void EnterMiniLayout(bool keepPlacement)
    {
        CancelMiniSlide(); FinishMiniSlide(); _miniDragging = false; _miniPress = null;
        if (!keepPlacement) { _miniTucked = false; _dock.Arrive(_dockClock.Elapsed); }
        PlayerShell.HorizontalAlignment = HorizontalAlignment.Left; PlayerShell.VerticalAlignment = VerticalAlignment.Top;
        PlayerShell.Width = MiniFrame.Width = MiniWidth; PlayerShell.Height = MiniFrame.Height = MiniHeight;
        PlayerShell.Clip = new RectangleGeometry(new Rect(0, 0, MiniWidth, MiniHeight), MiniRadius, MiniRadius);
        PlaceMini(MiniPoint(!_miniTucked));
        MiniFrame.Visibility = Visibility.Visible;
        _videoHost?.SetCornerRadius(MiniRadius * VisualTreeHelper.GetDpi(this).DpiScaleX);
        StartDock();
    }
    private void ResetMiniPlacement()
    {
        CancelMiniSlide(); FinishMiniSlide(); _dockTimer?.Stop();
        _miniDragging = _miniTucked = false; _miniPress = null;
        if (PlayerShell.IsMouseCaptured) PlayerShell.ReleaseMouseCapture();
        PlayerShell.HorizontalAlignment = HorizontalAlignment.Stretch; PlayerShell.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerShell.Width = PlayerShell.Height = double.NaN; PlayerShell.Margin = new Thickness(0); PlayerShell.Clip = null;
        MiniFrame.Visibility = MiniOutline.Visibility = Visibility.Collapsed;
        _videoHost?.SetCornerRadius(0);
    }
    /// <summary>
    /// Moved to a screen of another scale: the native video's rounded corners are in device pixels and are recomputed,
    /// and the mini-player and the controls' overlay window are placed again.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_pip is not null) return;
        if (_miniPlayer) { _videoHost?.SetCornerRadius(MiniRadius * newDpi.DpiScaleX); KeepMiniInPlace(); }
        else PositionPlayerControls();
    }
    /// <summary>Keeps the mini-player against the edge, at its height, when the window is resized.</summary>
    private void KeepMiniInPlace()
    {
        if (!_miniPlayer || _miniDragging) return;
        if (_miniSliding) { CancelMiniSlide(); FinishMiniSlide(); }
        PlaceMini(MiniPoint(!_miniTucked));
        PositionPlayerControls();
    }

    // ---- Tucked away and out: the pointer, the clock, the slide.

    private void StartDock()
    {
        if (_dockTimer is null)
        {
            _dockTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = MiniDock.Poll };
            _dockTimer.Tick += (_, _) => DockTick();
        }
        // Only in use, and never under the other checks: they find the mini-player out, as they leave it.
        if (!ValidationRun || _miniPointerForCheck is not null) _dockTimer.Start();
    }
    private void DockTick()
    {
        if (!_miniPlayer || !_playing || _pip is not null) { _dockTimer?.Stop(); return; }
        if (_morphing || WindowState == WindowState.Minimized || PlayerShell.Visibility != Visibility.Visible) return;
        var held = _miniDragging || _miniPress is not null || PlayerOptionsPopup.IsOpen || PlayerErrorShown
            || _keyboardNavigation && (PlayerSurface.IsKeyboardFocusWithin || PlayerTransport.IsKeyboardFocusWithin);
        if (_dock.Update(_dockClock.Elapsed, PointerOnMini(), held)) SlideMini(_dock.Out);
    }
    /// <summary>The pointer on what shows of the mini-player, Mira's window under it (not another window over Mira).</summary>
    private bool PointerOnMini()
    {
        if (_miniPointerForCheck is { } check) return check();
        if (Screens.Pointer() is not var (x, y)) return false;
        var window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero || GetAncestor(WindowAt(new CursorPoint { X = (int)x, Y = (int)y }), RootWindow) != window) return false;
        Point point;
        try { point = Root.PointFromScreen(new Point(x, y)); }
        catch (InvalidOperationException) { return false; }
        return MiniDock.Catches(point.X, point.Y, _miniTucked, MiniPoint(true).Y, Root.ActualWidth);
    }
    /// <summary>Slides out, or back against the edge. Retaken from where it is when the pointer changes its mind on the way.</summary>
    private async void SlideMini(bool shown)
    {
        if (!_miniPlayer || _morphing) return;
        _miniTucked = !shown; ClosePlayerPopups();
        var target = MiniPoint(shown);
        if (Motion.Reduced || _videoHost is not { } host || WindowState == WindowState.Minimized)
        {
            CancelMiniSlide(); FinishMiniSlide(); PlaceMini(target); AfterMiniSlide(shown); return;
        }
        var slide = ++_miniSlide; _miniSliding = true;
        var radius = MiniRadius * VisualTreeHelper.GetDpi(this).DpiScaleX;
        var from = host.BeginMove();
        // The black under the video and its shadow wait where it goes: they come back once it is there.
        PlayerShell.BeginAnimation(OpacityProperty, null); PlayerShell.Opacity = 0;
        MiniFrame.BeginAnimation(OpacityProperty, null); MiniFrame.Opacity = 0;
        PlaceMini(target); UpdateLayout();
        var to = host.LaidOut ?? from;
        var curve = shown ? MiniDock.OutCurve : MiniDock.BackCurve;
        await MoveVideoAsync(host, from, to, radius, radius, shown ? MiniDock.OutMs : MiniDock.BackMs, live: () => slide == _miniSlide, ease: t => MiniDock.Ease(curve, t));
        if (slide != _miniSlide) return;
        FinishMiniSlide(); AfterMiniSlide(shown);
    }
    private void AfterMiniSlide(bool shown)
    {
        PositionPlayerControls();
        if (shown && PointerOnMini()) ShowPlayerControls();
    }
    /// <summary>Stops a slide where it is: what comes next (a slide, a reduction or an expansion) moves the video on from there.</summary>
    private void CancelMiniSlide() => ++_miniSlide;
    /// <summary>A reduction, an expansion or the closing moves the video on from where the slide left it, with its own clock.</summary>
    private void TakeOverMiniSlide()
    {
        if (!_miniSliding) return;
        CancelMiniSlide(); _miniSliding = false;
        PlayerShell.Opacity = 1; MiniFrame.BeginAnimation(OpacityProperty, null); MiniFrame.Opacity = 1;
    }
    /// <summary>The video placed by layout again, the black under it and its shadow back.</summary>
    private void FinishMiniSlide()
    {
        if (!_miniSliding) return;
        _miniSliding = false;
        if (!_morphing) _videoHost?.EndMove();
        PlayerShell.Opacity = 1;
        if (_miniPlayer) Motion.Fade(MiniFrame, 1, 120, MiniFrame.Opacity); else MiniFrame.Opacity = 1;
    }

    // ---- Pressed, clicked, dragged.

    /// <param name="activating">The click brings Mira back to the front: it may still drag, never expands.</param>
    private void MiniPointerDown(MouseButtonEventArgs e, bool activating)
    {
        if (!IsMiniBackground(e.OriginalSource as DependencyObject)) return;
        PlayerSurface.Focus(); e.Handled = true;
        _miniPress = Mouse.GetPosition(Root); _miniPressExpands = !activating && !InBarBand(e.GetPosition(PlayerSurface));
    }
    private void MiniPointerUp(MouseButtonEventArgs e)
    {
        if (_miniPress is null || _miniDragging) return;
        _miniPress = null; e.Handled = true;
        // A click without movement on the picture (not on the title or the timeline's band) brings the whole player back.
        if (_miniPressExpands) ExpandMiniByClick(); else ShowPlayerControls();
    }
    private bool _miniPressExpands;
    /// <summary>A press on the sliver, tucked away or on its way: the click brings the whole player back as on the picture.</summary>
    private void MiniShell_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_miniPlayer || MiniSettled || _miniDragging || PlayerErrorShown) return;
        e.Handled = true; ExpandMiniByClick();
    }
    private void ExpandMiniByClick()
    {
        _swallowPressesUntil = Environment.TickCount64 + 500;
        SetMiniPlayerSmoothly(false);
    }
    /// <summary>The second press of a double click lands on the player growing over the page: it goes nowhere.</summary>
    private bool SwallowsPress() => Environment.TickCount64 < _swallowPressesUntil;
    private void Window_PreviewMouseDownSwallowed(object sender, MouseButtonEventArgs e) { if (SwallowsPress()) e.Handled = true; }
    /// <summary>Anything but the buttons and the timeline can be used as a grip.</summary>
    private bool IsMiniBackground(DependencyObject? node)
    {
        for (; node is not null && !ReferenceEquals(node, PlayerSurface); node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase or Timeline or System.Windows.Controls.Slider) return false;
        return node is not null;
    }
    private void BeginMiniDrag()
    {
        if (_miniPress is not { } press) return;
        if (_miniSliding) { CancelMiniSlide(); FinishMiniSlide(); }
        _miniGrab = new Point(press.X - _miniTopLeft.X, press.Y - _miniTopLeft.Y);
        // The main window follows the pointer: the overlay window can then close without ending the drag.
        if (!PlayerShell.CaptureMouse()) { _miniPress = null; return; }
        _miniDragging = true; _miniTucked = false;
        ClosePlayerPopups(); MiniFrame.Visibility = Visibility.Visible;
    }
    private void MiniDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_miniDragging) return;
        var pointer = e.GetPosition(Root);
        var left = RailWidth + MiniDock.Gap; var bottom = Math.Max(MiniTop, Root.ActualHeight - MiniDock.Gap - MiniHeight);
        PlaceMini(new Point(Math.Clamp(pointer.X - _miniGrab.X, left, Math.Max(left, Root.ActualWidth - MiniDock.Peek)), Math.Clamp(pointer.Y - _miniGrab.Y, MiniTop, bottom)));
    }
    private void MiniDrag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_miniDragging) return;
        e.Handled = true; EndMiniDrag(); PlayerShell.ReleaseMouseCapture();
    }
    private void MiniDrag_LostCapture(object sender, MouseEventArgs e) { if (_miniDragging) EndMiniDrag(); }
    /// <summary>Let go: back against the right edge, at the height it was let go at, out until the pointer leaves it.</summary>
    private void EndMiniDrag()
    {
        _miniDragging = false; _miniPress = null;
        _miniShare = MiniDock.Share(_miniTopLeft.Y, MiniTop, Root.ActualHeight);
        _dock.Show(_dockClock.Elapsed);
        SlideMini(true);
    }

    private const uint RootWindow = 2;
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern IntPtr WindowAt(CursorPoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
}
