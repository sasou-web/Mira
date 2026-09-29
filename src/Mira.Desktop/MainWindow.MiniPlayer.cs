using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>Floating mini-player: rounded, shadowed, dragged anywhere and snapped to the nearest
/// corner on release. Pushed past the right edge it tucks away behind a small handle.</summary>
public partial class MainWindow
{
    private const double MiniWidth = 384, MiniHeight = 216, MiniRadius = 12, MiniGap = 20, RailWidth = 68, TitleHeight = 34;
    private bool _miniRight = true, _miniBottom = true, _miniStowed, _miniDragging, _miniAnimating;
    private Point? _miniPress, _tabPress;
    private Point _miniGrab, _miniTopLeft;
    private readonly List<(long Time, Point Position)> _miniTrail = [];
    private int _miniAnimation;
    private bool MiniSettled => !_miniDragging && !_miniStowed && !_miniAnimating;
    private FrameworkElement Root => (FrameworkElement)Content;

    /// <summary>Resting area: clear of the navigation rail, the caption buttons and the window edges.</summary>
    private Rect MiniBounds()
    {
        var left = RailWidth + MiniGap; var top = TitleHeight + 8;
        return new Rect(left, top, Math.Max(MiniWidth, Root.ActualWidth - MiniGap - left), Math.Max(MiniHeight, Root.ActualHeight - MiniGap - top));
    }
    private Point MiniCorner(bool right, bool bottom)
    {
        var bounds = MiniBounds();
        return new(right ? bounds.Right - MiniWidth : bounds.Left, bottom ? bounds.Bottom - MiniHeight : bounds.Top);
    }
    private Point StowedPoint() => new(Root.ActualWidth + 16, MiniCorner(true, _miniBottom).Y);
    private void PlaceMini(Point topLeft)
    {
        _miniTopLeft = topLeft;
        PlayerShell.Margin = MiniFrame.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
    }
    private void EnterMiniLayout(bool keepPlacement)
    {
        ++_miniAnimation; _miniAnimating = _miniDragging = false; _miniPress = null;
        if (!keepPlacement) _miniStowed = false;
        PlayerShell.HorizontalAlignment = HorizontalAlignment.Left; PlayerShell.VerticalAlignment = VerticalAlignment.Top;
        PlayerShell.Width = MiniFrame.Width = MiniWidth; PlayerShell.Height = MiniFrame.Height = MiniHeight;
        PlayerShell.Clip = new RectangleGeometry(new Rect(0, 0, MiniWidth, MiniHeight), MiniRadius, MiniRadius);
        PlaceMini(_miniStowed ? StowedPoint() : MiniCorner(_miniRight, _miniBottom));
        MiniFrame.Visibility = _miniStowed ? Visibility.Collapsed : Visibility.Visible;
        if (_miniStowed) ShowMiniTab(); else MiniTab.Visibility = Visibility.Collapsed;
        _videoHost?.SetCornerRadius(MiniRadius * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }
    private void ResetMiniPlacement()
    {
        ++_miniAnimation; _miniAnimating = _miniDragging = _miniStowed = false; _miniPress = null;
        if (PlayerShell.IsMouseCaptured) PlayerShell.ReleaseMouseCapture();
        PlayerShell.HorizontalAlignment = HorizontalAlignment.Stretch; PlayerShell.VerticalAlignment = VerticalAlignment.Stretch;
        PlayerShell.Width = PlayerShell.Height = double.NaN; PlayerShell.Margin = new Thickness(0); PlayerShell.Clip = null;
        MiniFrame.Visibility = MiniTab.Visibility = MiniOutline.Visibility = Visibility.Collapsed;
        _videoHost?.SetCornerRadius(0);
    }
    /// <summary>Keeps the mini-player in its corner when the window is resized.</summary>
    private void KeepMiniInPlace()
    {
        if (!_miniPlayer || _miniDragging) return;
        ++_miniAnimation; _miniAnimating = false;
        PlaceMini(_miniStowed ? StowedPoint() : MiniCorner(_miniRight, _miniBottom));
        if (_miniStowed) ShowMiniTab(animate: false);
        PositionPlayerControls();
    }

    private void MiniPointerDown(MouseButtonEventArgs e)
    {
        if (!IsMiniBackground(e.OriginalSource as DependencyObject)) return;
        PlayerSurface.Focus(); e.Handled = true;
        if (e.ClickCount == 2) { _miniPress = null; _surfaceClick.Stop(); SetMiniPlayer(false); return; }
        _miniPress = Mouse.GetPosition(Root);
    }
    private void MiniPointerUp(MouseButtonEventArgs e)
    {
        if (_miniPress is null || _miniDragging) return;
        _miniPress = null; e.Handled = true;
        // A click without movement keeps its meaning: play / pause.
        _surfaceClick.Stop(); _surfaceClick.Start();
    }
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
        _surfaceClick.Stop(); ++_miniAnimation; _miniAnimating = false;
        _miniGrab = new Point(press.X - _miniTopLeft.X, press.Y - _miniTopLeft.Y);
        // The main window follows the pointer: the overlay window can then close without ending the drag.
        if (!PlayerShell.CaptureMouse()) { _miniPress = null; return; }
        _miniDragging = true; _miniTrail.Clear();
        ClosePlayerPopups(); MiniFrame.Visibility = Visibility.Visible;
    }
    private void MiniDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_miniDragging) return;
        var pointer = e.GetPosition(Root); var bounds = MiniBounds();
        // Free inside the window; to the right it may be pushed mostly out of view to tuck it away.
        PlaceMini(new Point(Math.Clamp(pointer.X - _miniGrab.X, bounds.Left, Math.Max(bounds.Left, Root.ActualWidth - 56)),
            Math.Clamp(pointer.Y - _miniGrab.Y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - MiniHeight))));
        var now = Stopwatch.GetTimestamp();
        _miniTrail.Add((now, _miniTopLeft));
        _miniTrail.RemoveAll(x => now - x.Time > Stopwatch.Frequency / 8);
    }
    private void MiniDrag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_miniDragging) return;
        e.Handled = true; EndMiniDrag(); PlayerShell.ReleaseMouseCapture();
    }
    private void MiniDrag_LostCapture(object sender, MouseEventArgs e) { if (_miniDragging) EndMiniDrag(); }
    private void EndMiniDrag()
    {
        _miniDragging = false; _miniPress = null;
        var velocity = new Vector();
        if (_miniTrail.Count > 1)
        {
            var (firstTime, first) = _miniTrail[0]; var (lastTime, last) = _miniTrail[^1];
            var seconds = (lastTime - firstTime) / (double)Stopwatch.Frequency;
            if (seconds > .005) velocity = (last - first) / seconds;
        }
        var bounds = MiniBounds();
        var overflow = _miniTopLeft.X + MiniWidth - Root.ActualWidth;
        // Project a flick a little forward, like a thrown card, before choosing where it lands.
        var landing = _miniTopLeft + velocity * .12 + new Vector(MiniWidth / 2, MiniHeight / 2);
        _miniBottom = landing.Y > bounds.Top + bounds.Height / 2;
        if (overflow > MiniWidth * .3 || (velocity.X > 1500 && _miniTopLeft.X + MiniWidth > bounds.Right - 8)) { StowMini(); return; }
        _miniRight = landing.X > bounds.Left + bounds.Width / 2;
        GlideMini(MiniCorner(_miniRight, _miniBottom), () => ShowPlayerControls());
    }
    private void GlideMini(Point target, Action? completed = null)
    {
        var id = ++_miniAnimation; var from = _miniTopLeft;
        if (Motion.Reduced || (target - from).Length < 1) { PlaceMini(target); _miniAnimating = false; PositionPlayerControls(); completed?.Invoke(); return; }
        _miniAnimating = true; ClosePlayerPopups();
        var clock = Stopwatch.StartNew(); var duration = Math.Clamp((target - from).Length * .9, 180, 320);
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            if (id != _miniAnimation) { CompositionTarget.Rendering -= frame; return; }
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration);
            PlaceMini(from + (target - from) * (1 - Math.Pow(1 - t, 3)));
            if (t < 1) return;
            CompositionTarget.Rendering -= frame; _miniAnimating = false; PositionPlayerControls(); completed?.Invoke();
        };
        CompositionTarget.Rendering += frame;
    }
    private void StowMini()
    {
        _miniStowed = true; _miniRight = true; ClosePlayerPopups();
        GlideMini(StowedPoint(), () => { if (_miniStowed) MiniFrame.Visibility = Visibility.Collapsed; });
        ShowMiniTab();
    }
    private void UnstowMini()
    {
        if (!_miniPlayer || !_miniStowed) return;
        _miniStowed = false; _tabPress = null; MiniFrame.Visibility = Visibility.Visible;
        HideMiniTab();
        GlideMini(MiniCorner(true, _miniBottom), () => ShowPlayerControls());
    }
    private void ShowMiniTab(bool animate = true)
    {
        var corner = MiniCorner(true, _miniBottom);
        MiniTab.Margin = new Thickness(0, corner.Y + MiniHeight / 2 - MiniTab.Height / 2, 0, 0);
        MiniTabPulse.Opacity = _paused ? .35 : 1;
        if (MiniTab.Visibility == Visibility.Visible && !animate) return;
        MiniTab.Visibility = Visibility.Visible; MiniTab.IsHitTestVisible = true;
        Motion.Animate(MiniTabShift, TranslateTransform.XProperty, 0, 260, animate ? MiniTab.Width + 4 : 0, Motion.EaseOut);
        Motion.Fade(MiniTab, 1, 200, animate ? 0 : 1);
    }
    private async void HideMiniTab()
    {
        MiniTab.IsHitTestVisible = false;
        Motion.Animate(MiniTabShift, TranslateTransform.XProperty, MiniTab.Width + 4, 180);
        Motion.Fade(MiniTab, 0, 180);
        if (!Motion.Reduced) await Task.Delay(190);
        if (!_miniStowed) MiniTab.Visibility = Visibility.Collapsed;
    }
    private void MiniTab_Click(object sender, RoutedEventArgs e) => UnstowMini();
    private void MiniTab_MouseDown(object sender, MouseButtonEventArgs e) => _tabPress = e.GetPosition(Root);
    private void MiniTab_MouseMove(object sender, MouseEventArgs e)
    {
        // Pulling the handle towards the centre brings the video back without waiting for the release.
        if (_tabPress is { } press && e.LeftButton == MouseButtonState.Pressed && press.X - e.GetPosition(Root).X > 12) UnstowMini();
    }
}
