using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// The glyph of a state button of a title's page (<see cref="IconPaths.States"/>: Favori, Vu, Suivre, Garder sur ce PC):
/// its outline off, a solid white shape on, a ring of how far a copy is while it comes. The button around it stays the
/// same rounded square in every state: a change only pops this drawing (a short spring of its scale, inside the
/// button), never the row, so a click on the page moves nothing (checked by the public gallery, <c>24-title-*</c>).
/// </summary>
public sealed class StateIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(StateIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The button's ink, as <see cref="Icon"/> takes it.</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private string _kind = "favorite";
    private bool _on;
    private double? _progress;
    private readonly ScaleTransform _pop = new(1, 1);
    private static readonly Dictionary<string, (Geometry Off, Geometry Solid, Geometry? Lines, Geometry? Marks, Geometry? Ring)> Shapes =
        IconPaths.States.ToDictionary(x => x.Key, x => (Shape(x.Value.Off), Shape(x.Value.Solid), Optional(x.Value.Lines), Optional(x.Value.Marks), x.Value.Off.StartsWith(IconPaths.Ring, StringComparison.Ordinal) ? Shape(IconPaths.Ring) : null));
    private static Geometry Shape(string path) { var value = Geometry.Parse(path); value.Freeze(); return value; }
    private static Geometry? Optional(string path) => path.Length == 0 ? null : Shape(path);

    public StateIcon(string kind)
    {
        _kind = kind; Width = Height = 20;
        RenderTransform = _pop; RenderTransformOrigin = new Point(.5, .5);
        SetBinding(ForegroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
    }
    public string Kind => _kind;
    public bool On => _on;
    /// <summary>How far a copy is, from 0 to 1, while it comes; null otherwise.</summary>
    public double? Progress => _progress;

    /// <summary>
    /// The state to draw. A change of on or off pops the glyph once the button is on screen (not when the page is drawn
    /// for another title, nor with reduced motion); the same state drawn again, or a copy a little further, does not.
    /// </summary>
    public void Show(string kind, bool on, double? progress = null)
    {
        var flipped = on != _on || kind != _kind;
        _kind = kind; _on = on; _progress = progress is { } value ? Math.Clamp(value, 0, 1) : null;
        InvalidateVisual();
        if (flipped && IsVisible && IsLoaded && !Motion.Reduced) Pop(on);
    }
    /// <summary>
    /// On, the shape lands from smaller with a little overshoot, as something set; off, it settles from a little smaller,
    /// without one. Only this glyph's scale: the button and the row do not change.
    /// </summary>
    private void Pop(bool on)
    {
        var spring = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(on ? 340 : 220) };
        spring.KeyFrames.Add(new DiscreteDoubleKeyFrame(on ? .62 : .8, KeyTime.FromPercent(0)));
        spring.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), on ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = .55 } : Motion.EaseOut));
        _pop.BeginAnimation(ScaleTransform.ScaleXProperty, spring, HandoffBehavior.SnapshotAndReplace);
        _pop.BeginAnimation(ScaleTransform.ScaleYProperty, spring, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 20 : Width, double.IsNaN(Height) ? 20 : Height);
    protected override void OnRender(DrawingContext dc)
    {
        if (!Shapes.TryGetValue(_kind, out var shape)) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var ink = Foreground;
        if (_on)
        {
            // A thin pen of the same ink softens the solid's corners, as the outlines' round joins do.
            dc.DrawGeometry(ink, Pen(ink, 1), shape.Solid);
            if (shape.Lines is { } lines) dc.DrawGeometry(null, Pen(ink, 2), lines);
            if (shape.Marks is { } marks) dc.DrawGeometry(null, Pen(Palette.Get("OnAccent"), 2.2), marks);
        }
        else if (_progress is { } done && shape.Ring is not null && shape.Marks is { } marks)
        {
            // A copy under way: the circle faint, how far it is drawn over it from the top, clockwise; the arrow inside.
            dc.DrawGeometry(null, Pen(Faded(ink, .3), 2), shape.Ring);
            if (done > 0) dc.DrawGeometry(null, Pen(ink, 2), Arc(done));
            dc.DrawGeometry(null, Pen(ink, 2), marks);
        }
        else
        {
            // Closed glyphs get a faint fill of their own ink, as Icon's do: a little volume.
            if (_kind is "favorite" or "follow") dc.DrawGeometry(Faded(ink, .15), null, shape.Off);
            dc.DrawGeometry(null, Pen(ink, 2), shape.Off);
        }
        dc.Pop(); dc.Pop();
    }
    /// <summary>The part of the ring (radius 9 round the middle of the grid) from the top, clockwise, as far as <paramref name="done"/>.</summary>
    private static Geometry Arc(double done)
    {
        if (done >= .999) return Shape(IconPaths.Ring);
        var angle = done * 2 * Math.PI;
        var end = new Point(12 + 9 * Math.Sin(angle), 12 - 9 * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(12, 3), IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(9, 9), 0, done > .5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]); geometry.Freeze();
        return geometry;
    }
    private static Brush Faded(Brush brush, double alpha)
    {
        if (brush is not SolidColorBrush solid) return brush;
        var color = solid.Color; color.A = (byte)(color.A * alpha);
        var faded = new SolidColorBrush(color); faded.Freeze(); return faded;
    }
    private static Pen Pen(Brush brush, double width) => new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
}
