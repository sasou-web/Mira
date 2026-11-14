using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// A one-line name cut short by « … » that slides to show its end while the pointer rests on its place (the nearest
/// element marked <see cref="ZoneProperty"/> above it: a row, a tile; else the name itself), or the keyboard is in that
/// place; back to its start once left (<see cref="TitleSlide"/>). Wraps the name's <see cref="TextBlock"/>, which keeps its
/// trimming at rest: sliding, it is laid out whole and moved under a box with soft edges. Each frame of the slide is placed
/// for the screen's refresh it is drawn for, as the wheel's scrolling is (<see cref="SmoothScroll"/>): even at 240 Hz.
/// </summary>
public sealed class SlidingText : Decorator
{
    /// <summary>The place whose hover slides the names in it (a row, a tile).</summary>
    public static readonly DependencyProperty ZoneProperty = DependencyProperty.RegisterAttached("Zone", typeof(bool), typeof(SlidingText), new PropertyMetadata(false));
    public static bool GetZone(DependencyObject obj) => (bool)obj.GetValue(ZoneProperty);
    public static void SetZone(DependencyObject obj, bool value) => obj.SetValue(ZoneProperty, value);

    /// <summary>
    /// True while a page scrolls under a still pointer (set by the window): rows going by under it slide nothing, as the
    /// posters do not light up; the pointer's next movement counts.
    /// </summary>
    public static Func<bool>? Quiet { get; set; }

    /// <summary>The name, wrapped; <paramref name="text"/> must be one line (no wrapping).</summary>
    public static SlidingText Wrap(TextBlock text) => new() { Child = text };

    /// <summary>Marks <paramref name="zone"/> as the place whose hover slides the names in it, and returns it.</summary>
    public static T Zone<T>(T zone) where T : DependencyObject { SetZone(zone, true); return zone; }

    private static readonly long Started = Stopwatch.GetTimestamp();
    private static TimeSpan Now => Stopwatch.GetElapsedTime(Started);

    private readonly TitleSlide _slide = new();
    private readonly TranslateTransform _move = new();
    private readonly DispatcherTimer _rest = new(DispatcherPriority.Render);
    private readonly GradientStop _leftEdge = new(Colors.Black, 0), _leftInner = new(Colors.Black, 0), _rightInner = new(Colors.Black, 1), _rightEdge = new(Colors.Black, 1);
    private readonly LinearGradientBrush _mask;
    private UIElement? _zone;
    private bool _whole, _running;
    private double _natural;
    /// <summary>The clock at the first frame of a slide, and that frame's refresh: the frames after it are timed by their refresh.</summary>
    private (TimeSpan Clock, TimeSpan Frame)? _frameBase;
    private TimeSpan? _lastFrame;

    public SlidingText()
    {
        // In the box's own pixels: the name's fades stay at its edges, whatever moves under them.
        _mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), GradientStops = [_leftEdge, _leftInner, _rightInner, _rightEdge] };
        _rest.Tick += (_, _) => { _rest.Stop(); Run(); };
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement gone && gone.RenderTransform == _move) gone.ClearValue(RenderTransformProperty);
        if (visualAdded is UIElement added) added.RenderTransform = _move;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child) return default;
        // Its whole width first, then (at rest) as wide as it may be, where it trims itself.
        child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        _natural = child.DesiredSize.Width;
        if (!_whole) child.Measure(constraint);
        return new Size(Math.Min(child.DesiredSize.Width, constraint.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size size)
    {
        if (Child is not { } child) return size;
        child.Arrange(new Rect(0, 0, _whole ? Math.Max(_natural, size.Width) : size.Width, size.Height));
        if (_whole)
        {
            // Its text or its box changed while it slides.
            _slide.Resize(Now, _natural - size.Width);
            if (!_slide.Whole) Dispatcher.BeginInvoke(Place, DispatcherPriority.Render);
            else if (!_running) Place();
        }
        return size;
    }

    private void Hook()
    {
        Unhook();
        DependencyObject? node = this;
        while (node is not null && !(node is UIElement element && GetZone(element))) node = VisualTreeHelper.GetParent(node);
        _zone = node as UIElement ?? this;
        _zone.MouseEnter += Entered; _zone.MouseMove += Moved; _zone.MouseLeave += Left;
        _zone.IsKeyboardFocusWithinChanged += FocusChanged;
    }

    private void Unhook()
    {
        if (_zone is { } zone) { zone.MouseEnter -= Entered; zone.MouseMove -= Moved; zone.MouseLeave -= Left; zone.IsKeyboardFocusWithinChanged -= FocusChanged; }
        _zone = null;
        _slide.Stop(); _rest.Stop(); Place();
    }

    private void Entered(object sender, MouseEventArgs e) { if (Quiet?.Invoke() != true) Begin(); }
    private void Moved(object sender, MouseEventArgs e) { if (!_whole && !_rest.IsEnabled && Quiet?.Invoke() != true) Begin(); }
    private void Left(object sender, MouseEventArgs e) => End();
    private void FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && InputManager.Current.MostRecentInputDevice is KeyboardDevice) Begin();
        else if (e.NewValue is false && _zone?.IsMouseOver != true) End();
    }

    private void Begin()
    {
        if (Motion.Reduced || !IsVisible || !TitleSlide.IsCut(_natural, ActualWidth)) return;
        _slide.Enter(Now, _natural - ActualWidth);
        Run();
    }

    private void End() { _slide.Leave(Now); _rest.Stop(); Run(); }

    /// <summary>Where the slide is now: waits out its rest, follows the frames while it moves, sits still otherwise.</summary>
    private void Run()
    {
        Place();
        if (_slide.Wakes is { } wakes)
        {
            _rest.Interval = wakes - Now > TimeSpan.Zero ? wakes - Now : TimeSpan.FromMilliseconds(1);
            _rest.Start();
        }
        if (_slide.Moving && !_running) { _running = true; _frameBase = null; _lastFrame = null; CompositionTarget.Rendering += Frame; }
    }

    private void Frame(object? sender, EventArgs e)
    {
        var now = Now;
        if (e is RenderingEventArgs { RenderingTime: var at })
        {
            if (at == _lastFrame) return;
            _lastFrame = at;
            _frameBase ??= (now, at);
            now = _frameBase.Value.Clock + (at - _frameBase.Value.Frame);
        }
        Place(now);
        if (!_slide.Moving || !IsVisible) { if (!IsVisible) { _slide.Stop(); Place(); } Stop(); }
    }

    private void Stop() { if (_running) CompositionTarget.Rendering -= Frame; _running = false; }

    private void Place() => Place(Now);

    private void Place(TimeSpan now)
    {
        var at = _slide.At(now);
        if (_slide.Whole != _whole)
        {
            _whole = _slide.Whole;
            ClipToBounds = _whole;
            OpacityMask = _whole ? _mask : null;
            // Moving text keeps its shapes from frame to frame rather than being refitted to the pixels at each one.
            if (Child is { } child) TextOptions.SetTextHintingMode(child, _whole ? TextHintingMode.Animated : TextHintingMode.Auto);
            InvalidateMeasure();
        }
        _move.X = -at;
        if (!_whole) return;
        var (left, right) = _slide.Edges(at);
        var width = Math.Max(1, ActualWidth);
        var fade = Math.Min(.45, TitleSlide.Fade / width);
        _mask.EndPoint = new Point(width, 0);
        _leftInner.Offset = fade; _rightInner.Offset = 1 - fade;
        _leftEdge.Color = Color.FromScRgb((float)left, 0, 0, 0);
        _rightEdge.Color = Color.FromScRgb((float)right, 0, 0, 0);
    }
}
