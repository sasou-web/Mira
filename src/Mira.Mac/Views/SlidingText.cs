using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// A one-line name cut short by « … » that slides to show its end while the pointer rests on its place (the nearest
/// control marked <see cref="ZoneProperty"/> above it: a row, a tile; else the name itself), or the keyboard comes into
/// that place; back to its start once left (<see cref="TitleSlide"/>, as on Windows). Wraps the name's
/// <see cref="TextBlock"/>, which keeps its trimming at rest: sliding, it is laid out whole and moved under a box with soft
/// edges, one step per frame of the screen (<see cref="TopLevel.RequestAnimationFrame"/>), placed by the clock, only while it moves.
/// </summary>
public sealed class SlidingText : Decorator
{
    /// <summary>The place whose hover slides the names in it (a row, a tile).</summary>
    public static readonly AttachedProperty<bool> ZoneProperty = AvaloniaProperty.RegisterAttached<SlidingText, Control, bool>("Zone");
    public static bool GetZone(Control control) => control.GetValue(ZoneProperty);
    public static void SetZone(Control control, bool value) => control.SetValue(ZoneProperty, value);

    /// <summary>The name, wrapped; <paramref name="text"/> must be one line (no wrapping).</summary>
    public static SlidingText Wrap(TextBlock text) => new() { Child = text };

    /// <summary>Marks <paramref name="zone"/> as the place whose hover slides the names in it, and returns it.</summary>
    public static T Zone<T>(T zone) where T : Control { SetZone(zone, true); return zone; }

    private static readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();
    private static TimeSpan Now => System.Diagnostics.Stopwatch.GetElapsedTime(Started);

    private readonly TitleSlide _slide = new();
    private readonly TranslateTransform _move = new();
    private readonly DispatcherTimer _rest = new(DispatcherPriority.Render);
    private readonly GradientStop _leftEdge = new(Colors.Black, 0), _leftInner = new(Colors.Black, 0), _rightInner = new(Colors.Black, 1), _rightEdge = new(Colors.Black, 1);
    private readonly LinearGradientBrush _mask;
    private Control? _zone;
    private bool _whole, _running;
    private double _natural;

    public SlidingText()
    {
        _mask = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(1, 0, RelativeUnit.Absolute), GradientStops = { _leftEdge, _leftInner, _rightInner, _rightEdge } };
        _rest.Tick += (_, _) => { _rest.Stop(); Run(); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ChildProperty) return;
        if (change.OldValue is Control gone && gone.RenderTransform == _move) gone.RenderTransform = null;
        if (change.NewValue is Control added) added.RenderTransform = _move;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unhook();
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
            if (!_slide.Whole) Dispatcher.UIThread.Post(Place, DispatcherPriority.Render);
            else if (!_running) Place();
        }
        return size;
    }

    private void Hook()
    {
        Unhook();
        Visual? node = this;
        while (node is not null && !(node is Control control && GetZone(control))) node = node.GetVisualParent();
        _zone = node as Control ?? this;
        _zone.PointerEntered += Entered; _zone.PointerMoved += Moved; _zone.PointerExited += Left;
        _zone.GotFocus += Focused; _zone.LostFocus += Unfocused;
    }

    private void Unhook()
    {
        if (_zone is { } zone) { zone.PointerEntered -= Entered; zone.PointerMoved -= Moved; zone.PointerExited -= Left; zone.GotFocus -= Focused; zone.LostFocus -= Unfocused; }
        _zone = null;
        _slide.Stop(); _rest.Stop(); Place();
    }

    // Rows going by under a still pointer while the page scrolls slide nothing, as the posters do not light up; its next movement counts.
    private void Entered(object? sender, PointerEventArgs e) { if (!Ui.Scrolling) Begin(); }
    private void Moved(object? sender, PointerEventArgs e) { if (!_whole && !_rest.IsEnabled && !Ui.Scrolling) Begin(); }
    private void Left(object? sender, PointerEventArgs e) => End();
    private void Focused(object? sender, GotFocusEventArgs e) { if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) Begin(); }
    private void Unfocused(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Focus going from one button of the row to another stays in it.
        Dispatcher.UIThread.Post(() => { if (_zone is { IsKeyboardFocusWithin: false, IsPointerOver: false }) End(); });
    }

    /// <summary>For the self-check: cut short where it stands, how far it has slid, the pointer brought onto its place or taken away.</summary>
    public bool IsCut => TitleSlide.IsCut(_natural, Bounds.Width);
    public double Slid => -_move.X;
    public bool Whole => _whole;
    public double Overflow => Math.Max(0, _natural - Bounds.Width);
    public int Frames { get; private set; }
    public void HoverForCheck(bool over) { if (over) Begin(); else End(); }

    private void Begin()
    {
        if (MacMotion.Reduced || !IsEffectivelyVisible || !TitleSlide.IsCut(_natural, Bounds.Width)) return;
        _slide.Enter(Now, _natural - Bounds.Width);
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
        if (_slide.Moving && !_running && TopLevel.GetTopLevel(this) is { } top) { _running = true; top.RequestAnimationFrame(Frame); }
    }

    // Placed by the clock when the frame is drawn, not by the frame's own time: on the Mac runner Avalonia's frame time ran
    // several times slower than the clock (the slide crawled 11 px in 8 s); macOS draws each frame right after this call.
    private void Frame(TimeSpan at)
    {
        Frames++;
        Place(Now);
        if (_slide.Moving && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is { } top) { top.RequestAnimationFrame(Frame); return; }
        _running = false;
        if (!IsEffectivelyVisible) { _slide.Stop(); Place(); }
    }

    private void Place() => Place(Now);

    private void Place(TimeSpan now)
    {
        var at = _slide.At(now);
        if (_slide.Whole != _whole)
        {
            _whole = _slide.Whole;
            ClipToBounds = _whole;
            OpacityMask = _whole ? _mask : null;
            InvalidateMeasure();
        }
        _move.X = -at;
        if (!_whole) return;
        var (left, right) = _slide.Edges(at);
        var width = Math.Max(1, Bounds.Width);
        var fade = Math.Min(.45, TitleSlide.Fade / width);
        _mask.EndPoint = new RelativePoint(width, 0, RelativeUnit.Absolute);
        _leftInner.Offset = fade; _rightInner.Offset = 1 - fade;
        _leftEdge.Color = Color.FromArgb((byte)Math.Round(left * 255), 0, 0, 0);
        _rightEdge.Color = Color.FromArgb((byte)Math.Round(right * 255), 0, 0, 0);
    }
}
