using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Mira.Core;

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
    private static readonly Geometry BrandTile = Shape(IconPaths.BrandTile);
    private static readonly Geometry BrandM = Shape(IconPaths.BrandM);
    // The mini-player's own, as on Windows.
    private static readonly Dictionary<string, string> Player = new()
    {
        ["expand"] = "M3 8V6Q3 3 6 3H8M16 3H18Q21 3 21 6V8M21 16V18Q21 21 18 21H16M8 21H6Q3 21 3 18V16",
        ["mini"] = "M9 20H7Q3 20 3 16V8Q3 4 7 4H17Q21 4 21 8V10 M14 13H19Q21 13 21 15V18Q21 20 19 20H14Q12 20 12 18V15Q12 13 14 13Z"
    };
    private static readonly Dictionary<string, Geometry> Shapes = IconPaths.Strokes.Concat(Player).ToDictionary(x => x.Key, x => Shape(x.Value));
    private static readonly Geometry Speaker = Shape(IconPaths.Speaker);
    /// <summary>Solid parts, drawn under the strokes of the same kind: the player bar uses filled glyphs.</summary>
    private static readonly Dictionary<string, Geometry> Solids = new()
    {
        ["volume"] = Speaker, ["volume-low"] = Speaker, ["mute"] = Speaker,
        ["next-episode"] = Shape(IconPaths.NextEpisode),
        ["previous-episode"] = Shape(IconPaths.PreviousEpisode),
        ["more"] = Shape(IconPaths.More),
        ["heart-filled"] = Shape(IconPaths.Heart)
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
            context.DrawGeometry(filled ? ink : null, filled ? null : Round(ink, Kind is "next-episode" or "previous-episode" ? 2.3 : 1.85), geometry);
        }
    }
    private static Pen Round(IBrush brush, double width) => new(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
