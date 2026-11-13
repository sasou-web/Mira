using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, EnabledChanged));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    /// <summary>A row that scrolls sideways only (genre chips): the wheel moves it along until its end, then the page.</summary>
    public static readonly DependencyProperty AcrossProperty = DependencyProperty.RegisterAttached("Across", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false));
    public static bool GetAcross(DependencyObject obj) => (bool)obj.GetValue(AcrossProperty);
    public static void SetAcross(DependencyObject obj, bool value) => obj.SetValue(AcrossProperty, value);
    private static readonly ConditionalWeakTable<ScrollViewer, Driver> Drivers = new();
    private static void EnabledChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not ScrollViewer viewer) return;
        if (e.NewValue is true) { viewer.PreviewMouseWheel += Wheel; viewer.Unloaded += Unloaded; }
        else { viewer.PreviewMouseWheel -= Wheel; viewer.Unloaded -= Unloaded; Cancel(viewer); }
    }
    private static void Unloaded(object sender, RoutedEventArgs e) => Cancel((ScrollViewer)sender);
    private static void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer outer) return;
        var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var target = outer;
        // The innermost list under the pointer that can still move that way: a list of the settings or of a sheet inside
        // the page scrolls first, and the page takes over at its end.
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != outer; node = Parent(node))
        {
            if (node is ComboBox || node is TextBoxBase) return;
            if (!horizontal && node is ScrollViewer row && GetAcross(row) && (e.Delta > 0 ? row.HorizontalOffset > .5 : row.HorizontalOffset < row.ScrollableWidth - .5))
            { By(row, -e.Delta / 120d * 160, true); e.Handled = true; return; }
            if (node is ScrollViewer nested && (horizontal ? nested.ScrollableWidth > 0 : CanMove(nested, e.Delta))) { target = nested; break; }
        }
        if (horizontal && target.ScrollableWidth <= 0) return;
        var pixels = SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight * .85 : Math.Max(1, SystemParameters.WheelScrollLines) * 36;
        By(target, -e.Delta / 120d * pixels, horizontal);
        // Handle in the tunneling phase, before a horizontal child consumes vertical wheel input.
        e.Handled = true;
    }
    private static bool CanMove(ScrollViewer viewer, int delta) => viewer.ScrollableHeight > 0 && (delta > 0 ? viewer.VerticalOffset > .5 : viewer.VerticalOffset < viewer.ScrollableHeight - .5);
    private static DependencyObject? Parent(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    public static void By(ScrollViewer viewer, double pixels, bool horizontal = false) => Drivers.GetValue(viewer, v => new Driver(v)).Add(pixels, horizontal);
    public static void Cancel(ScrollViewer viewer) { if (Drivers.TryGetValue(viewer, out var driver)) driver.Stop(); }
    public static void Jump(ScrollViewer viewer, double offset = 0) { Cancel(viewer); viewer.ScrollToVerticalOffset(offset); }
    /// <summary>Eased back to the top, as the wheel moves it (at once when animations are reduced); nothing at the top already.</summary>
    public static void ToTop(ScrollViewer viewer) { if (viewer.VerticalOffset > .5) By(viewer, -viewer.VerticalOffset - viewer.ExtentHeight); }
    /// <summary>Steps made by the wheel, the keys or <see cref="By"/> so far: a list placed by a page that comes in makes none.</summary>
    public static long Steps { get; private set; }
    private sealed class Driver
    {
        private readonly ScrollViewer _viewer;
        private readonly ScrollMotion _motion = new();
        private bool _running, _horizontal;
        private long _last;
        /// <summary>The refresh of the screen the last step was drawn for (<see cref="RenderingEventArgs.RenderingTime"/>); null before the first.</summary>
        private TimeSpan? _lastFrame;
        public Driver(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.PreviewMouseLeftButtonDown += (_, e) => { for (var node = e.OriginalSource as DependencyObject; node is not null && node != viewer; node = Parent(node)) if (node is ScrollBar) { Stop(); break; } };
        }
        public void Add(double pixels, bool horizontal)
        {
            if (!_running || _horizontal != horizontal) { Stop(); _horizontal = horizontal; _motion.Reset(horizontal ? _viewer.HorizontalOffset : _viewer.VerticalOffset); }
            _motion.Add(pixels, Maximum);
            if (Motion.Reduced) { Apply(_motion.Step(10, Maximum)); return; }
            if (_running || !_motion.Moving) return;
            _running = true; _last = Stopwatch.GetTimestamp(); _lastFrame = null; CompositionTarget.Rendering += Frame;
        }
        private double Maximum => _horizontal ? _viewer.ScrollableWidth : _viewer.ScrollableHeight;
        private void Apply(double value) { Steps++; if (_horizontal) _viewer.ScrollToHorizontalOffset(value); else _viewer.ScrollToVerticalOffset(value); }
        /// <summary>
        /// One step per frame, as far as the time between the refreshes of the screen the frames are drawn for: the page
        /// then moves evenly on the screen, whenever the step itself ran (a step timed by the clock when it runs moves
        /// unevenly, late steps being longer). Raised more than once for a frame: one step only.
        /// </summary>
        private void Frame(object? sender, EventArgs e)
        {
            double delta;
            if (e is RenderingEventArgs { RenderingTime: var at })
            {
                if (at == _lastFrame) return;
                // The first step: the time since the wheel turned (a frame's time has nothing before it to count from).
                delta = _lastFrame is { } previous ? (at - previous).TotalSeconds : (Stopwatch.GetTimestamp() - _last) / (double)Stopwatch.Frequency;
                _lastFrame = at;
            }
            else { var now = Stopwatch.GetTimestamp(); delta = (now - _last) / (double)Stopwatch.Frequency; _last = now; }
            Apply(_motion.Step(Math.Clamp(delta, 0, .1), Maximum));
            if (!_motion.Moving || !_viewer.IsVisible) Stop();
        }
        public void Stop() { if (_running) CompositionTarget.Rendering -= Frame; _running = false; }
    }
}
