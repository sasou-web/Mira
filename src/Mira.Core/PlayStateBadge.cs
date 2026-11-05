namespace Mira.Core;

/// <summary>
/// The pause or the play just asked for, in the middle of the picture (Mira for Windows and Mac): the icon alone, white,
/// over two soft shadows that keep it readable on a bright picture, with no disc that would grey a dark one. There at
/// once, held, then gone while it opens up, in 0.62 s. Sizes in device-independent pixels, times in milliseconds.
/// </summary>
public static class PlayStateBadge
{
    /// <summary>The icon's side, on Mira's 24-unit grid.</summary>
    public const double Glyph = 52;
    /// <summary>The square kept around it for its shadows at its largest: the window or the layer it is drawn in.</summary>
    public const double Room = 128;
    /// <summary>
    /// The play triangle's weight sits left of its box: moved right by this share of its side, its centre of mass lands
    /// in the middle. The pause bars stay where they are.
    /// </summary>
    public const double PlayNudge = .075;
    /// <summary>A shadow under the icon: how far down it falls, its blur as a Gaussian's standard deviation, its black's opacity.</summary>
    public readonly record struct Shadow(double Down, double Sigma, double Opacity);
    /// <summary>A close one for the icon's edge, then a wide one around both: readable on snow or a sky.</summary>
    public static readonly Shadow Edge = new(1, 1, .45), Glow = new(2, 7, .5);
    /// <summary>Opaque at <see cref="In"/>, at its size at <see cref="Settle"/>, held until <see cref="Hold"/>, gone at <see cref="End"/>.</summary>
    public const double In = 70, Settle = 160, Hold = 360, End = 620;
    /// <summary>Its size as it comes in, and as it leaves.</summary>
    public const double FromScale = .94, ToScale = 1.26;
    /// <summary>With fewer animations: shown as it is for this long, then gone at once.</summary>
    public const double Still = 650;

    /// <summary>
    /// Its opacity and size <paramref name="ms"/> after the press. <paramref name="from"/> is the opacity it had at the
    /// press: pressed again while it still shows, it goes on from there and never flashes off.
    /// </summary>
    public static (double Opacity, double Scale) At(double ms, double from = 0)
    {
        if (double.IsNaN(ms) || ms >= End) return (0, ToScale);
        ms = Math.Max(0, ms); from = double.IsFinite(from) ? Math.Clamp(from, 0, 1) : 0;
        // Out of sight quickly once it has been seen: easing in, not a long see-through tail over the film.
        var opacity = ms < In ? from + (1 - from) * Out(ms / In, 2) : ms < Hold ? 1 : 1 - Math.Pow((ms - Hold) / (End - Hold), 2);
        var scale = ms < Hold ? FromScale + (1 - FromScale) * Out(ms / Settle, 3) : 1 + (ToScale - 1) * Out((ms - Hold) / (End - Hold), 2);
        return (opacity, scale);
    }
    private static double Out(double x, double power) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), power);
}
