using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Mira.Core;

namespace Mira.Mac.Views;

/// <summary>
/// The ring of a season's counter, beside « 2 of 6 watched » (maquette B, ogsas 2026-11-18), as Windows' SeasonRing: a
/// faint circle, the part watched drawn over it in white from the top, clockwise; once all are, the circle and its tick
/// in the button's ink. Redrawn at once, never animated.
/// </summary>
public sealed class SeasonRing : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<SeasonRing>();
    /// <summary>The button's ink: grey at rest, white under the pointer.</summary>
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    static SeasonRing() => AffectsRender<SeasonRing>(ForegroundProperty);
    private static readonly Geometry Circle = Geometry.Parse(IconPaths.Ring), Tick = Geometry.Parse(IconPaths.States["watched"].Marks);

    public SeasonRing(int seen, int total) { Seen = seen; Total = total; Width = Height = 20; }
    public int Seen { get; }
    public int Total { get; }

    public override void Render(DrawingContext context)
    {
        if (Total <= 0) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var ink = Foreground ?? Ui.Muted;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (Seen >= Total) { context.DrawGeometry(null, Round(ink, 2), Circle); context.DrawGeometry(null, Round(ink, 2), Tick); return; }
            context.DrawGeometry(null, Round(ink is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, .35) : ink, 2), Circle);
            if (Seen <= 0) return;
            var angle = (double)Seen / Total * 2 * Math.PI;
            var geometry = new StreamGeometry();
            using (var draw = geometry.Open())
            {
                draw.BeginFigure(new Point(12, 3), false);
                draw.ArcTo(new Point(12 + 9 * Math.Sin(angle), 12 - 9 * Math.Cos(angle)), new Size(9, 9), 0, Seen * 2 > Total, SweepDirection.Clockwise);
                draw.EndFigure(false);
            }
            context.DrawGeometry(null, Round(Ui.Ink, 2), geometry);
        }
    }
    private static Pen Round(IBrush brush, double width) => new(brush, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
}
