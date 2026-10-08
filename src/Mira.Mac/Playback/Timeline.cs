using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mira.Core;

namespace Mira.Mac.Playback;

/// <summary>
/// The player's progress bar: played, buffered, cut by chapters. The whole height answers the pointer: a press jumps
/// to the point aimed at and continues as a drag, with the picture following; hovering shows the time and chapter.
/// </summary>
public sealed class Timeline : Control
{
    private double _duration, _position, _buffered, _hover = -1;
    private bool _dragging;
    private IReadOnlyList<ChapterMark> _chapters = [];
    private static readonly IBrush Rest = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255)), Buffer = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
        Played = new SolidColorBrush(Color.Parse("#F5F5F7")), Label = new SolidColorBrush(Color.Parse("#E6141416"));
    private static readonly Typeface Face = new(new FontFamily("avares://Mira/Assets/Fonts#Nunito Sans"), FontStyle.Normal, FontWeight.SemiBold);
    /// <summary>A position chosen with the pointer (pressed or dragged), in seconds.</summary>
    public event Action<double>? Seek;
    public bool Dragging => _dragging;
    public Timeline() { Height = 28; Cursor = new Cursor(StandardCursorType.Hand); ClipToBounds = false; }

    public void SetPlayback(double position, double duration, double buffered)
    {
        if (_dragging) return;
        _position = position; _duration = duration; _buffered = buffered; InvalidateVisual();
    }
    public void SetChapters(IReadOnlyList<ChapterMark> chapters) { _chapters = chapters; InvalidateVisual(); }
    public void Hold(double position) { _position = position; InvalidateVisual(); }

    private double TimeAt(double x) => _duration <= 0 ? 0 : Math.Clamp(x / Math.Max(1, Bounds.Width), 0, 1) * _duration;
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true; e.Pointer.Capture(this);
        _position = TimeAt(e.GetPosition(this).X); Seek?.Invoke(_position); InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this).X;
        if (_dragging) { var target = TimeAt(_hover); if (Math.Abs(target - _position) > 0.25) { _position = target; Seek?.Invoke(target); } }
        InvalidateVisual();
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false; e.Pointer.Capture(null);
        Seek?.Invoke(TimeAt(e.GetPosition(this).X));
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = -1; InvalidateVisual(); }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width; var hot = _hover >= 0 || _dragging;
        var thickness = hot ? 6 : 4;
        // Chapters split the bar with small gaps; the segment under the pointer thickens.
        var starts = _duration > 0 ? _chapters.Select(c => c.Start).Where(s => s > 0.5 && s < _duration - 0.5).OrderBy(s => s).ToList() : [];
        var edges = new List<double> { 0 }; edges.AddRange(starts.Select(s => s / _duration * width)); edges.Add(width);
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var left = edges[i] + (i == 0 ? 0 : 1.5); var right = edges[i + 1] - (i == edges.Count - 2 ? 0 : 1.5);
            if (right <= left) continue;
            var under = hot && _hover >= edges[i] && _hover < edges[i + 1];
            var t = under ? thickness + 2 : thickness; var top = (Bounds.Height - t) / 2;
            void Fill(IBrush brush, double end) { var w = Math.Min(right, end) - left; if (w > 0) context.DrawRectangle(brush, null, new Rect(left, top, w, t), t / 2, t / 2); }
            Fill(Rest, right);
            if (_duration > 0) { Fill(Buffer, _buffered / _duration * width); Fill(Played, _position / _duration * width); }
        }
        if (_duration > 0 && hot)
        {
            var x = _position / _duration * width;
            context.DrawEllipse(Played, null, new Point(x, Bounds.Height / 2), 7, 7);
        }
        if (_hover >= 0 && _duration > 0)
        {
            var time = TimeAt(_hover);
            var chapter = _chapters.LastOrDefault(c => c.Start <= time).Title;
            var text = new FormattedText(Clock(time) + (string.IsNullOrWhiteSpace(chapter) ? "" : "  ·  " + chapter), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, Brushes.White);
            var boxWidth = text.Width + 20; var x = Math.Clamp(_hover - boxWidth / 2, 0, Math.Max(0, width - boxWidth));
            context.DrawRectangle(Label, null, new Rect(x, -34, boxWidth, 26), 6, 6);
            context.DrawText(text, new Point(x + 10, -34 + (26 - text.Height) / 2));
        }
    }
    public static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }
}

/// <summary>A turning arc, shown until the first picture.</summary>
public sealed class LoadingArc : Control
{
    private double _angle;
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    public LoadingArc() { Width = Height = 46; _timer.Tick += (_, _) => { _angle = (_angle + 7) % 360; InvalidateVisual(); }; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) { if (IsVisible) _timer.Start(); else _timer.Stop(); }
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); if (IsVisible) _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }
    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2); var radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 2;
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), 3), center, radius, radius);
        var start = _angle * Math.PI / 180; var end = start + Math.PI / 2;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start)), false);
            g.ArcTo(new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#F5F5F7")), 3, lineCap: PenLineCap.Round), geometry);
    }
}
