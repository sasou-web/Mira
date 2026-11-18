using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>
/// The ring of a season's counter, beside « 2 of 6 watched » (maquette B, ogsas 2026-11-18): a faint circle, the part
/// watched drawn over it in white from the top, clockwise; once all are, the circle and its tick in the button's ink, as
/// the « Vu » glyph of <see cref="StateIcon"/>. Redrawn at once, never animated.
/// </summary>
public sealed class SeasonRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(SeasonRing), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The button's ink: grey at rest, white under the pointer.</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static readonly Geometry Circle = Shape(IconPaths.Ring), Tick = Shape(IconPaths.States["watched"].Marks);
    private static Geometry Shape(string path) { var value = Geometry.Parse(path); value.Freeze(); return value; }

    public SeasonRing(int seen, int total)
    {
        Seen = seen; Total = total; Width = Height = 20;
        SetBinding(ForegroundProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
    }
    public int Seen { get; }
    public int Total { get; }

    protected override Size MeasureOverride(Size availableSize) => new(Width, Height);
    protected override void OnRender(DrawingContext dc)
    {
        if (Total <= 0) return;
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new ScaleTransform(scale, scale));
        var ink = Foreground;
        if (Seen >= Total) { dc.DrawGeometry(null, Pen(ink, 2), Circle); dc.DrawGeometry(null, Pen(ink, 2), Tick); }
        else
        {
            dc.DrawGeometry(null, Pen(Faded(ink, .35), 2), Circle);
            if (Seen > 0)
            {
                var angle = (double)Seen / Total * 2 * Math.PI;
                var figure = new PathFigure { StartPoint = new Point(12, 3), IsClosed = false };
                figure.Segments.Add(new ArcSegment(new Point(12 + 9 * Math.Sin(angle), 12 - 9 * Math.Cos(angle)), new Size(9, 9), 0, Seen * 2 > Total, SweepDirection.Clockwise, true));
                dc.DrawGeometry(null, Pen(Palette.Get("Text"), 2), new PathGeometry([figure]));
            }
        }
        dc.Pop();
    }
    private static Brush Faded(Brush brush, double alpha)
    {
        if (brush is not SolidColorBrush solid) return brush;
        var color = solid.Color; color.A = (byte)(color.A * alpha);
        var faded = new SolidColorBrush(color); faded.Freeze(); return faded;
    }
    private static Pen Pen(Brush brush, double width) => new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
}
