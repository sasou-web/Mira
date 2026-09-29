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
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != outer; node = Parent(node))
        {
            if (node is ComboBox || node is TextBoxBase) return;
            if (horizontal && node is ScrollViewer nested && nested.ScrollableWidth > 0) { target = nested; break; }
        }
        if (horizontal && target.ScrollableWidth <= 0) return;
        var pixels = SystemParameters.WheelScrollLines < 0 ? outer.ViewportHeight * .85 : Math.Max(1, SystemParameters.WheelScrollLines) * 36;
        By(target, -e.Delta / 120d * pixels, horizontal);
        // Handle in the tunneling phase, before a horizontal child consumes vertical wheel input.
        e.Handled = true;
    }
    private static DependencyObject? Parent(DependencyObject node) => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    public static void By(ScrollViewer viewer, double pixels, bool horizontal = false) => Drivers.GetValue(viewer, v => new Driver(v)).Add(pixels, horizontal);
    public static void Cancel(ScrollViewer viewer) { if (Drivers.TryGetValue(viewer, out var driver)) driver.Stop(); }
    public static void Jump(ScrollViewer viewer, double offset = 0) { Cancel(viewer); viewer.ScrollToVerticalOffset(offset); }
    private sealed class Driver
    {
        private readonly ScrollViewer _viewer;
        private readonly ScrollMotion _motion = new();
        private bool _running, _horizontal;
        private long _last;
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
            _running = true; _last = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame;
        }
        private double Maximum => _horizontal ? _viewer.ScrollableWidth : _viewer.ScrollableHeight;
        private void Apply(double value) { if (_horizontal) _viewer.ScrollToHorizontalOffset(value); else _viewer.ScrollToVerticalOffset(value); }
        private void Frame(object? sender, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp(); var delta = (now - _last) / (double)Stopwatch.Frequency; _last = now;
            Apply(_motion.Step(Math.Min(.1, delta), Maximum));
            if (!_motion.Moving || !_viewer.IsVisible) Stop();
        }
        public void Stop() { if (_running) CompositionTarget.Rendering -= Frame; _running = false; }
    }
}
