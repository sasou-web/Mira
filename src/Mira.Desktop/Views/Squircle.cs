using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mira.Desktop.Views;

/// <summary>
/// Mira's shape: a rounded rectangle whose corners ease into the sides (a squircle, as Apple draws them) instead of
/// a circle's arc meeting them at a crease. Flat: one plain fill and, when given, an even edge, as Mira web draws its
/// buttons (no light from above, no gradient: ogsas, 2026-11-04). Used where a Border would be: buttons, tiles, cards, fields.
/// </summary>
public sealed class Squircle : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(Squircle), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));
    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(Squircle), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));
    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(Squircle), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(Squircle), new FrameworkPropertyMetadata(new CornerRadius(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(Squircle), new FrameworkPropertyMetadata(new Thickness(), FrameworkPropertyMetadataOptions.AffectsMeasure));
    /// <summary>The content cut to the shape (a picture filling a card); off by default, so focus rings and shadows inside stay whole.</summary>
    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(nameof(ClipContent), typeof(bool), typeof(Squircle), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public Thickness BorderThickness { get => (Thickness)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    private Geometry? _shape; private Size _shapeSize; private double _shapeRadius = -1;
    private Geometry Shape(Size size, double radius)
    {
        if (_shape is null || size != _shapeSize || radius != _shapeRadius) { _shape = Geometry(new Rect(size), radius); _shapeSize = size; _shapeRadius = radius; }
        return _shape;
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset(); var room = new Size(Math.Max(0, constraint.Width - inset.Width), Math.Max(0, constraint.Height - inset.Height));
        if (Child is null) return inset;
        Child.Measure(room); return new Size(Child.DesiredSize.Width + inset.Width, Child.DesiredSize.Height + inset.Height);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var b = BorderThickness; var p = Padding;
        Child?.Arrange(new Rect(b.Left + p.Left, b.Top + p.Top, Math.Max(0, size.Width - b.Left - b.Right - p.Left - p.Right), Math.Max(0, size.Height - b.Top - b.Bottom - p.Top - p.Bottom)));
        Clip = ClipContent ? Shape(size, CornerRadius.TopLeft) : null;
        ClipChild(size, b);
        return size;
    }
    /// <summary>
    /// The content cut to the inside of the edge too: cut only to the outer shape, a filled child (the side column of
    /// the Settings window) covered the edge in the corners, where the curve passes inside its rectangle.
    /// </summary>
    private void ClipChild(Size size, Thickness b)
    {
        if (Child is not { } child) return;
        if (!ClipContent || b.Top <= 0) { if (child.Clip is not null && ReferenceEquals(child.Clip, _childClip)) child.ClearValue(ClipProperty); _childClip = null; return; }
        if (child.Clip is not null && !ReferenceEquals(child.Clip, _childClip)) return;
        var at = VisualTreeHelper.GetOffset(child);
        var inside = new Rect(b.Left - at.X, b.Top - at.Y, Math.Max(0, size.Width - b.Left - b.Right), Math.Max(0, size.Height - b.Top - b.Bottom));
        _childClip = Geometry(inside, Math.Max(0, CornerRadius.TopLeft - b.Top));
        child.Clip = _childClip;
    }
    private Geometry? _childClip;
    private Size Inset() { var b = BorderThickness; var p = Padding; return new Size(b.Left + b.Right + p.Left + p.Right, b.Top + b.Bottom + p.Top + p.Bottom); }
    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize; if (size.Width <= 0 || size.Height <= 0) return;
        var radius = CornerRadius.TopLeft; var shape = Shape(size, radius);
        if (Background is { } fill) dc.DrawGeometry(fill, null, shape);
        var stroke = BorderThickness.Top;
        if (BorderBrush is { } line && stroke > 0 && !Transparent(line))
            dc.DrawGeometry(null, new Pen(line, stroke), Geometry(new Rect(size).Inset(stroke / 2), Math.Max(0, radius - stroke / 2)));
    }
    private static bool Transparent(Brush brush) => brush.Opacity <= 0 || brush is SolidColorBrush { Color.A: 0 };

    /// <summary>
    /// The squircle in <paramref name="area"/>: each corner a cubic curve that leaves the side 1.6 radius before the
    /// corner and leaves it straight, so it reads as a softer, larger rounding than an arc of the same radius. A shape
    /// rounded to half its height (a capsule) keeps round ends.
    /// </summary>
    public static Geometry Geometry(Rect area, double radius)
    {
        var half = Math.Min(area.Width, area.Height) / 2; radius = Math.Max(0, radius);
        if (radius <= 0 || half <= 0) { var plain = new RectangleGeometry(area); plain.Freeze(); return plain; }
        var reach = Math.Min(radius * 1.6, half);
        // From Apple-like corners (0.2) to a circle's arc (0.4477) as the shape becomes a capsule.
        var k = .2 + (.4477 - .2) * Math.Clamp((radius * 1.6 - half) / Math.Max(.001, radius * .6), 0, 1);
        double l = area.Left, t = area.Top, r = area.Right, b = area.Bottom;
        var figure = new PathFigure { StartPoint = new Point(l + reach, t), IsClosed = true, IsFilled = true };
        void Side(double x, double y) => figure.Segments.Add(new LineSegment(new Point(x, y), true));
        void Corner(Point c1, Point c2, Point end) => figure.Segments.Add(new BezierSegment(c1, c2, end, true));
        Side(r - reach, t); Corner(new(r - reach * k, t), new(r, t + reach * k), new(r, t + reach));
        Side(r, b - reach); Corner(new(r, b - reach * k), new(r - reach * k, b), new(r - reach, b));
        Side(l + reach, b); Corner(new(l + reach * k, b), new(l, b - reach * k), new(l, b - reach));
        Side(l, t + reach); Corner(new(l, t + reach * k), new(l + reach * k, t), new(l + reach, t));
        var geometry = new PathGeometry { Figures = { figure } }; geometry.Freeze(); return geometry;
    }
}

internal static class RectExtensions
{
    public static Rect Inset(this Rect rect, double by) => rect.Width > 2 * by && rect.Height > 2 * by ? new Rect(rect.X + by, rect.Y + by, rect.Width - 2 * by, rect.Height - 2 * by) : rect;
}
