using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Mira.Mac.Views;

/// <summary>Mira's soft vector family, the same glyphs as the Windows app: rounded joins on a 24-unit grid.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<Icon, string>(nameof(Kind), "home");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static Icon() => AffectsRender<Icon>(KindProperty, ForegroundProperty);
    public Icon() { Width = 22; Height = 22; }
    public Icon(string kind, double size = 22) : this() { Kind = kind; Width = Height = size; }

    private static Geometry Shape(string path) => Geometry.Parse(path);
    // Two joined arches form a lowercase m, with the rhythm of two film loops.
    private static readonly Geometry BrandTile = Shape("M12 1C3 1 1 3 1 12S3 23 12 23 23 21 23 12 21 1 12 1Z");
    private static readonly Geometry BrandM = Shape("M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5");
    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["home"] = Shape("M3.5 10.5Q2.6 9.3 4 8.1L9.6 3.7Q12 1.8 14.4 3.7L20 8.1Q21.4 9.3 20.5 10.5V16.5Q20.5 21 16 21H8Q3.5 21 3.5 16.5Z M9 20V15Q9 12 12 12Q15 12 15 15V20"),
        ["search"] = Shape("M18.3 10.4A7.7 7.7 0 1 1 2.9 10.4A7.7 7.7 0 1 1 18.3 10.4 M16 16L21 21"),
        ["film"] = Shape("M7 3H17Q21 3 21 7V17Q21 21 17 21H7Q3 21 3 17V7Q3 3 7 3Z M7.5 3.5V20.5 M16.5 3.5V20.5 M3.5 8.3H7 M3.5 15.7H7 M17 8.3H20.5 M17 15.7H20.5 M8 12H16"),
        ["series"] = Shape("M7 7H17C21 7 21 9 21 13S21 19 17 19H7C3 19 3 17 3 13S3 7 7 7Z M8 3L12 7L16 3 M8 22H16"),
        ["library"] = Shape("M3 6Q3 3 6 3Q9 3 9 6Q9 9 6 9Q3 9 3 6Z M15 6Q15 3 18 3Q21 3 21 6Q21 9 18 9Q15 9 15 6Z M3 18Q3 15 6 15Q9 15 9 18Q9 21 6 21Q3 21 3 18Z M15 18Q15 15 18 15Q21 15 21 18Q21 21 18 21Q15 21 15 18Z"),
        ["settings"] = Shape("M9 4C9 1 15 1 15 4C18 2.5 22 7 19.5 9C23 10 23 14 19.5 15C22 17 18 21.5 15 20C15 23 9 23 9 20C6 21.5 2 17 4.5 15C1 14 1 10 4.5 9C2 7 6 2.5 9 4Z M15.3 12A3.3 3.3 0 1 1 8.7 12A3.3 3.3 0 1 1 15.3 12"),
        ["heart"] = Shape("M12 20C10 19 3 14 3 8.7C3 2.8 10 1.8 12 6C14 1.8 21 2.8 21 8.7C21 14 14 19 12 20Z"),
        ["user"] = Shape("M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M15 9A3 3 0 1 1 9 9A3 3 0 1 1 15 9 M6 18Q7 14 12 14Q17 14 18 18"),
        ["play"] = Shape("M7 4.5Q5 3.4 5 5.8V18.2Q5 20.6 7 19.5L19 13.4Q21.2 12 19 10.6Z"),
        ["pause"] = Shape("M6 4H8Q9 4 9 5V19Q9 20 8 20H6Q5 20 5 19V5Q5 4 6 4Z M16 4H18Q19 4 19 5V19Q19 20 18 20H16Q15 20 15 19V5Q15 4 16 4Z"),
        ["info"] = Shape("M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M12 11V17 M12 7V7.15"),
        ["back"] = Shape("M14.5 5L7.5 12L14.5 19"), ["next"] = Shape("M9.5 5L16.5 12L9.5 19"),
        ["down"] = Shape("M5 9L12 16L19 9"), ["close"] = Shape("M6 6L18 18M18 6L6 18"),
        ["refresh"] = Shape("M19.5 8A8 8 0 1 0 20 14M19.5 3V8.5H14"),
        ["check"] = Shape("M4.5 12.5L9.5 17.5L19.5 6.5"),
        ["volume"] = Shape("M15.6 8.4Q18.3 12 15.6 15.6M18.7 5.4Q23.2 12 18.7 18.6"),
        ["volume-low"] = Shape("M15.6 8.4Q18.3 12 15.6 15.6"),
        ["mute"] = Shape("M16.2 9.4L21.4 14.6M21.4 9.4L16.2 14.6"),
        ["next-episode"] = Shape("M19.6 5.2V18.8"),
        ["subtitles"] = Shape("M7 4H17Q21 4 21 8V16Q21 20 17 20H7Q3 20 3 16V8Q3 4 7 4Z M6 11H9M13 11H18M6 15H13M17 15H18"),
        ["fullscreen"] = Shape("M3 8V6Q3 3 6 3H8M16 3H18Q21 3 21 6V8M21 16V18Q21 21 18 21H16M8 21H6Q3 21 3 18V16"),
        ["collapse"] = Shape("M3 8H6Q8 8 8 6V3M16 3V6Q16 8 18 8H21M21 16H18Q16 16 16 18V21M8 21V18Q8 16 6 16H3"),
        ["clock"] = Shape("M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M12 6V12L16 14"),
        ["server"] = Shape("M7 3H17Q21 3 21 7V17Q21 21 17 21H7Q3 21 3 17V7Q3 3 7 3Z M3.5 12H20.5M7 7.5H7.1M7 16.5H7.1M12 7.5H17M12 16.5H17"),
        ["plus"] = Shape("M5 12H19M12 5V19"), ["minus"] = Shape("M5 12H19"),
        ["exit"] = Shape("M10 3H7Q3 3 3 7V17Q3 21 7 21H10M9 12H21M16 7L21 12L16 17"),
        ["sliders"] = Shape("M3 7H7M11 7H21M3 17H13M17 17H21 M11 7A2 2 0 1 1 7 7A2 2 0 1 1 11 7 M17 17A2 2 0 1 1 13 17A2 2 0 1 1 17 17"),
        ["rewind"] = Shape("M4 8A8.5 8.5 0 1 1 3.5 14M3 3V8H8"),
        ["forward"] = Shape("M20 8A8.5 8.5 0 1 0 20.5 14M21 3V8H16"),
        ["skip"] = Shape("M5 5Q3 4 3 6V18Q3 20 5 19L15 13Q17 12 15 11Z M20 5V19"),
        ["download"] = Shape("M12 3.5V14.5 M7.5 10.5L12 15L16.5 10.5 M4 16V17Q4 20.5 7.5 20.5H16.5Q20 20.5 20 17V16"),
        ["globe"] = Shape("M21 12A9 9 0 1 1 3 12A9 9 0 1 1 21 12 M3.5 12H20.5 M12 3Q8 7 8 12Q8 17 12 21 M12 3Q16 7 16 12Q16 17 12 21"),
        ["sparkle"] = Shape("M12 3Q12.9 10.2 20 12Q12.9 13.8 12 21Q11.1 13.8 4 12Q11.1 10.2 12 3Z M19 3V6M17.5 4.5H20.5")
    };
    private static readonly Geometry Speaker = Shape("M10.3 4.9Q12 3.8 12 5.8V18.2Q12 20.2 10.3 19.1L6.5 16.2H4.6Q2.6 16.2 2.6 14.2V9.8Q2.6 7.8 4.6 7.8H6.5Z");
    /// <summary>Solid parts, drawn under the strokes of the same kind: the player bar uses filled glyphs.</summary>
    private static readonly Dictionary<string, Geometry> Solids = new()
    {
        ["volume"] = Speaker, ["volume-low"] = Speaker, ["mute"] = Speaker,
        ["next-episode"] = Shape("M6.3 5.3Q4.2 4.1 4.2 6.5V17.5Q4.2 19.9 6.3 18.7L15.2 13.3Q17.3 12 15.2 10.7Z"),
        ["more"] = Shape("M13.55 5A1.55 1.55 0 1 1 10.45 5A1.55 1.55 0 1 1 13.55 5Z M13.55 12A1.55 1.55 0 1 1 10.45 12A1.55 1.55 0 1 1 13.55 12Z M13.55 19A1.55 1.55 0 1 1 10.45 19A1.55 1.55 0 1 1 13.55 19Z"),
        ["heart-filled"] = Shape("M12 20C10 19 3 14 3 8.7C3 2.8 10 1.8 12 6C14 1.8 21 2.8 21 8.7C21 14 14 19 12 20Z")
    };

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var scale = Math.Min(size.Width, size.Height) / 24;
        var ink = Foreground ?? Brushes.White;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((size.Width - 24 * scale) / 2, (size.Height - 24 * scale) / 2)))
        {
            if (Kind == "brand")
            {
                context.DrawGeometry(ink, null, BrandTile);
                context.DrawGeometry(null, Round(new SolidColorBrush(Color.FromRgb(16, 16, 19)), 2.65), BrandM);
                return;
            }
            var solid = Solids.GetValueOrDefault(Kind);
            if (solid is not null) context.DrawGeometry(ink, Round(ink, 1), solid);
            var geometry = Shapes.GetValueOrDefault(Kind) ?? (solid is null ? Shapes["info"] : null);
            if (geometry is null) return;
            var filled = Kind is "play" or "pause";
            context.DrawGeometry(filled ? ink : null, filled ? null : Round(ink, Kind == "next-episode" ? 2.3 : 1.85), geometry);
        }
    }
    private static Pen Round(IBrush brush, double width) => new(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
