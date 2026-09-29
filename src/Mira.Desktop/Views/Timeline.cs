using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>Playback timeline. mpv is polled a few times per second; between polls the played
/// position is extrapolated on every frame and drawn without pixel snapping, so it glides.
/// The whole control height is clickable, a press jumps to the pointer and keeps dragging,
/// and hovering shows the time (and chapter) under the pointer. Chapters appear as dots.</summary>
public sealed class Timeline : RangeBase
{
    private static readonly Brush Rest = Frozen(Color.FromArgb(72, 255, 255, 255));
    private static readonly Brush Buffered = Frozen(Color.FromArgb(108, 255, 255, 255));
    private static readonly Brush Ahead = Frozen(Color.FromArgb(150, 255, 255, 255));
    private static readonly Brush Played = Frozen(Color.FromRgb(245, 245, 247));
    private static readonly Brush MarkAhead = Frozen(Color.FromArgb(240, 245, 245, 247));
    private static readonly Brush MarkPassed = Frozen(Color.FromArgb(215, 22, 22, 25));
    private static readonly Brush ThumbShadow = Frozen(Color.FromArgb(80, 0, 0, 0));
    private readonly Border _bubble;
    private readonly TextBlock _bubbleTime, _bubbleTitle;
    private IReadOnlyList<ChapterMark> _chapters = [];
    private double _sample, _speed = 1, _buffered, _hoverX, _emphasis, _renderedX = double.NaN, _holdTarget;
    private long _sampleTime, _lastFrame, _holdUntil;
    private bool _playing, _dragging, _hovering, _live = true, _attached, _bubbleShown;
    public event Action? SeekStarted;
    public event Action<double>? Seeking;
    public event Action<double>? SeekCompleted;
    public bool IsDragging => _dragging;
    public IReadOnlyList<ChapterMark> Chapters => _chapters;
    /// <summary>Position currently drawn, including the extrapolation since the last poll.</summary>
    public double DisplayedPosition => Displayed(Stopwatch.GetTimestamp());

    static Timeline()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(typeof(Timeline)));
        // Clicking the bar must not take keyboard focus: arrows and Space stay player shortcuts.
        FocusableProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(false));
    }
    public Timeline()
    {
        MinHeight = 24; Cursor = Cursors.Hand;
        _bubbleTitle = new TextBlock { FontSize = 11, Foreground = Frozen(Color.FromRgb(206, 206, 212)), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240, Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };
        _bubbleTime = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center };
        var text = new StackPanel(); text.Children.Add(_bubbleTitle); text.Children.Add(_bubbleTime);
        _bubble = new Border
        {
            Child = text, Background = Frozen(Color.FromArgb(238, 20, 20, 23)), BorderBrush = Frozen(Color.FromArgb(48, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5), IsHitTestVisible = false, Opacity = 0
        };
        AddVisualChild(_bubble);
        IsVisibleChanged += (_, _) => { if (IsVisible) Attach(); else Detach(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) { _hovering = false; HideBubble(); } InvalidateVisual(); };
        Unloaded += (_, _) => Detach();
    }

    /// <summary>False while the controls are faded out: no per-frame redraws nobody can see.</summary>
    public bool Live
    {
        get => _live;
        set { if (_live == value) return; _live = value; if (value) { InvalidateVisual(); Attach(); } }
    }
    public void SetPlayback(double position, double duration, bool playing, double speed)
    {
        if (!double.IsFinite(position)) return;
        if (double.IsFinite(duration) && duration > 0 && Math.Abs(Maximum - duration) > .001) Maximum = duration;
        var now = Stopwatch.GetTimestamp();
        // Right after a seek mpv can still report the old time for a poll or two.
        if (now < _holdUntil && Math.Abs(position - _holdTarget) > 1.5) return;
        _holdUntil = 0;
        var shown = Displayed(now);
        // A poll landing a frame or two away is blended in rather than snapped: no backwards shimmer.
        _sample = playing && _playing && !_dragging && Math.Abs(position - shown) < .5 ? shown + (position - shown) * .35 : position;
        _sampleTime = now; _playing = playing; _speed = double.IsFinite(speed) && speed > 0 ? speed : 1;
        if (!_dragging) SetCurrentValue(ValueProperty, Math.Clamp(position, Minimum, Maximum));
        if (playing) Attach(); else InvalidateVisual();
    }
    /// <summary>Shows <paramref name="seconds"/> immediately and ignores stale polls until mpv catches up.</summary>
    public void Hold(double seconds)
    {
        _holdTarget = seconds; _holdUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 8 / 10;
        _sample = seconds; _sampleTime = Stopwatch.GetTimestamp();
        if (!_dragging) SetCurrentValue(ValueProperty, Math.Clamp(seconds, Minimum, Maximum));
        InvalidateVisual();
    }
    public void SetBuffered(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds - _buffered) < .05) return;
        _buffered = seconds; InvalidateVisual();
    }
    public void SetChapters(IEnumerable<ChapterMark> chapters)
    {
        _chapters = chapters.Where(c => double.IsFinite(c.Start) && c.Start >= 0).OrderBy(c => c.Start).ToList();
        InvalidateVisual();
    }
    public void Reset()
    {
        _sample = _buffered = 0; _playing = _dragging = false; _holdUntil = 0; _chapters = [];
        SetCurrentValue(ValueProperty, 0d); HideBubble(); InvalidateVisual();
    }
    public static string Label(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private double Displayed(long now)
    {
        if (_dragging) return Value;
        if (!_playing) return _sample;
        var elapsed = Math.Clamp((now - _sampleTime) / (double)Stopwatch.Frequency, 0, .5);
        return Math.Clamp(_sample + elapsed * _speed, Minimum, Maximum);
    }
    private double Fraction(double seconds) => Maximum > Minimum ? Math.Clamp((seconds - Minimum) / (Maximum - Minimum), 0, 1) : 0;
    private double X(double seconds) => Fraction(seconds) * ActualWidth;
    private double SecondsAt(double x) => Minimum + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * (Maximum - Minimum);

    private void Attach()
    {
        if (_attached || !IsVisible) return;
        _attached = true; _lastFrame = Stopwatch.GetTimestamp(); CompositionTarget.Rendering += Frame;
    }
    private void Detach() { if (!_attached) return; _attached = false; CompositionTarget.Rendering -= Frame; }
    private void Frame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var delta = Math.Min(.1, (now - _lastFrame) / (double)Stopwatch.Frequency); _lastFrame = now;
        var target = (_hovering || _dragging) && IsEnabled ? 1d : 0d; var dirty = false;
        if (_emphasis != target)
        {
            _emphasis = Motion.Reduced ? target : _emphasis + (target - _emphasis) * (1 - Math.Exp(-delta * 16));
            if (Math.Abs(_emphasis - target) < .01) _emphasis = target;
            dirty = true;
        }
        if (_live && _playing && !_dragging && ActualWidth > 0)
        {
            // Redraw only when the played edge moves by a visible fraction of a device pixel.
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            // Anti-aliased steps of 0.4 px still read as continuous motion, at a fraction of the repaints:
            // every repaint of the transparent overlay window costs a full-window update.
            if (double.IsNaN(_renderedX) || Math.Abs(X(Displayed(now)) - _renderedX) * scale >= .4) dirty = true;
        }
        if (dirty) InvalidateVisual();
        if (_emphasis == target && (!_playing || !_live)) Detach();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _bubble : throw new ArgumentOutOfRangeException(nameof(index));
    protected override Size MeasureOverride(Size constraint)
    {
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, MinHeight);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var bubble = _bubble.DesiredSize;
        var x = Math.Clamp(_hoverX - bubble.Width / 2, -10, Math.Max(-10, size.Width - bubble.Width + 10));
        _bubble.Arrange(new Rect(new Point(x, size.Height / 2 - 15 - bubble.Height), bubble));
        return size;
    }
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth; var height = ActualHeight;
        // Transparent fill: the full control height is a hit target, not only the thin line.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width <= 0) return;
        var middle = height / 2; var thickness = 3 + 2 * _emphasis;
        var position = Displayed(Stopwatch.GetTimestamp()); var played = X(position); _renderedX = played;
        Bar(dc, 0, width, middle, thickness, Rest);
        if (_buffered > position) Bar(dc, played, X(_buffered), middle, thickness, Buffered);
        if (_hovering && !_dragging && _hoverX > played) Bar(dc, played, _hoverX, middle, thickness, Ahead);
        Bar(dc, 0, played, middle, thickness, Played);
        var mark = 2.3 + 1.1 * _emphasis;
        foreach (var chapter in _chapters)
        {
            if (chapter.Start <= .5 || chapter.Start >= Maximum - .5) continue;
            var x = X(chapter.Start);
            dc.DrawEllipse(x <= played ? MarkPassed : MarkAhead, null, new Point(x, middle), mark, mark);
        }
        if (!IsEnabled) return;
        var thumb = 5.5 + 1.8 * _emphasis;
        dc.DrawEllipse(ThumbShadow, null, new Point(played, middle + .6), thumb + 1.4, thumb + 1.4);
        dc.DrawEllipse(Played, null, new Point(played, middle), thumb, thumb);
    }
    private static void Bar(DrawingContext dc, double from, double to, double middle, double thickness, Brush brush)
    {
        if (to - from < .01) return;
        dc.DrawRoundedRectangle(brush, null, new Rect(from, middle - thickness / 2, to - from, thickness), thickness / 2, thickness / 2);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); if (!IsEnabled) return; _hovering = true; Hover(e.GetPosition(this).X); Attach(); }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _hovering = false;
        if (!_dragging) HideBubble();
        Attach(); InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsEnabled) return;
        var x = e.GetPosition(this).X;
        if (_dragging) { SetCurrentValue(ValueProperty, SecondsAt(x)); Seeking?.Invoke(Value); }
        _hovering = true; Hover(x);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || Maximum <= Minimum) return;
        e.Handled = true; _dragging = true; CaptureMouse();
        var x = e.GetPosition(this).X; SetCurrentValue(ValueProperty, SecondsAt(x));
        _hovering = true; Hover(x); Attach(); InvalidateVisual();
        SeekStarted?.Invoke(); Seeking?.Invoke(Value);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        e.Handled = true; Complete(); ReleaseMouseCapture();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (_dragging) Complete(); }
    private void Complete()
    {
        _dragging = false; var target = Value; Hold(target);
        Attach(); SeekCompleted?.Invoke(target);
    }
    private void Hover(double x)
    {
        _hoverX = Math.Clamp(x, 0, ActualWidth);
        var seconds = _dragging ? Value : SecondsAt(x);
        _bubbleTime.Text = Label(seconds);
        var title = _chapters.LastOrDefault(c => c.Start <= seconds + .01).Title;
        _bubbleTitle.Text = title ?? "";
        _bubbleTitle.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        if (!_bubbleShown && Maximum > Minimum) { _bubbleShown = true; Motion.Fade(_bubble, 1, 120, ease: Motion.EaseOut); }
        InvalidateArrange(); InvalidateVisual();
    }
    private void HideBubble() { if (!_bubbleShown) return; _bubbleShown = false; Motion.Fade(_bubble, 0, 140); }
    protected override void OnValueChanged(double oldValue, double newValue) { base.OnValueChanged(oldValue, newValue); InvalidateVisual(); }
    protected override void OnMaximumChanged(double oldMaximum, double newMaximum) { base.OnMaximumChanged(oldMaximum, newMaximum); InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);
    private static SolidColorBrush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private sealed class TimelinePeer(Timeline owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(Timeline);
    }
}
