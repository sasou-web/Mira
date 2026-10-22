using System.Windows;
using System.Windows.Media;
using Mira.Core;

namespace Mira.Desktop.Views;

/// <summary>Mira's soft vector family: rounded joins and generous counters on a 24-unit grid.</summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(Icon), new FrameworkPropertyMetadata("home", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private static Geometry Shape(string path) { var value = Geometry.Parse(path); value.Freeze(); return value; }
    // Two joined arches form a lowercase m, with the rhythm of two film loops.
    private static readonly Geometry BrandTile = Shape(IconPaths.BrandTile);
    private static readonly Geometry BrandM = Shape(IconPaths.BrandM);
    /// <summary>The shared outlines (<see cref="IconPaths.Strokes"/>), and those only this app draws.</summary>
    private static readonly IReadOnlyDictionary<string, Geometry> Shapes = IconPaths.Strokes.Concat(new Dictionary<string, string>
    {
        ["expand"] = "M3 8V6Q3 3 6 3H8M16 3H18Q21 3 21 6V8M21 16V18Q21 21 18 21H16M8 21H6Q3 21 3 18V16",
        ["bookmark"] = "M6 20V6Q6 3 9 3H15Q18 3 18 6V20Q18 21 17 20.5L12 17L7 20.5Q6 21 6 20Z",
        ["maximize"] = "M7 4H17Q20 4 20 7V17Q20 20 17 20H7Q4 20 4 17V7Q4 4 7 4Z",
        ["restore"] = "M8 7V5Q8 3 10 3H19Q21 3 21 5V14Q21 16 19 16H17 M6 8H14Q17 8 17 11V18Q17 21 14 21H6Q3 21 3 18V11Q3 8 6 8Z",
        ["mini"] = "M9 20H7Q3 20 3 16V8Q3 4 7 4H17Q21 4 21 8V10 M14 13H19Q21 13 21 15V18Q21 20 19 20H14Q12 20 12 18V15Q12 13 14 13Z",
        ["folder"] = "M3.5 7.5Q3.5 4.5 6.5 4.5H9.2Q10.2 4.5 10.9 5.3L11.9 6.5H17.5Q20.5 6.5 20.5 9.5V16.5Q20.5 19.5 17.5 19.5H6.5Q3.5 19.5 3.5 16.5Z",
        ["trash"] = "M4 6.5H20 M9.5 6.5V5Q9.5 3.5 11 3.5H13Q14.5 3.5 14.5 5V6.5 M6 6.5L6.9 18.5Q7.1 20.5 9.1 20.5H14.9Q16.9 20.5 17.1 18.5L18 6.5 M10 10.5V16.5M14 10.5V16.5",
        ["help"] = "M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M9.4 9.4Q9.6 7 12 7Q14.6 7 14.6 9.4Q14.6 11.1 12.8 11.9Q12 12.3 12 13.4V13.8 M12 17V17.15",
        ["phone"] = "M9 2.5H15Q18 2.5 18 5.5V18.5Q18 21.5 15 21.5H9Q6 21.5 6 18.5V5.5Q6 2.5 9 2.5Z M11 18.2H13"
    }).ToDictionary(x => x.Key, x => Shape(x.Value));
    /// <summary>True for a kind this family draws (used to check the icons named in WhatsNew.json).</summary>
    public static bool Draws(string kind) => kind == "brand" || Shapes.ContainsKey(kind) || Solids.ContainsKey(kind);
    private static readonly Geometry Speaker = Shape(IconPaths.Speaker);
    /// <summary>Solid parts, drawn under the strokes of the same kind: the player bar uses filled glyphs.</summary>
    private static readonly IReadOnlyDictionary<string, Geometry> Solids = new Dictionary<string, Geometry>
    {
        ["volume"] = Speaker, ["volume-low"] = Speaker, ["mute"] = Speaker,
        // A favourite: the heart, filled.
        ["heart-fill"] = Shape(IconPaths.Heart),
        ["next-episode"] = Shape(IconPaths.NextEpisode),
        ["previous-episode"] = Shape(IconPaths.PreviousEpisode),
        ["more"] = Shape(IconPaths.More)
    };
    protected override Size MeasureOverride(Size availableSize) => new(double.IsNaN(Width) ? 22 : Width, double.IsNaN(Height) ? 22 : Height);
    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (Kind == "brand")
        {
            dc.DrawGeometry(Foreground, null, BrandTile);
            dc.DrawGeometry(null, RoundPen(new SolidColorBrush(Color.FromRgb(16, 16, 19)), 2.65), BrandM);
        }
        else
        {
            var solid = Solids.GetValueOrDefault(Kind);
            // A thin round pen softens the corners of solid shapes, as the strokes do for outlines.
            if (solid is not null) dc.DrawGeometry(Foreground, RoundPen(Foreground, 1), solid);
            var geometry = Shapes.GetValueOrDefault(Kind) ?? (solid is null ? Shapes["info"] : null);
            var filled = Kind is "play" or "pause";
            if (geometry is not null && Kind is "home" or "film" or "series" or "library" or "settings")
            {
                // Alpha in the brush avoids an offscreen opacity layer for these tiny glyphs.
                if (Foreground is SolidColorBrush ink)
                { var color = ink.Color; color.A = (byte)(color.A * .09); var wash = new SolidColorBrush(color); wash.Freeze(); dc.DrawGeometry(wash, null, geometry); }
            }
            if (geometry is not null) dc.DrawGeometry(filled ? Foreground : null, filled ? null : RoundPen(Foreground, Kind == "next-episode" ? 2.3 : 1.85), geometry);
            if (Kind is "rewind" or "forward")
            {
                var text = new FormattedText("10", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 7.5, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point((24 - text.Width) / 2, 9));
            }
        }
        dc.Pop(); dc.Pop();
    }
    private static Pen RoundPen(Brush brush, double width) => new(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
}
