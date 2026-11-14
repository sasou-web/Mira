namespace Mira.Core;

/// <summary>
/// Mira's opening on Windows and Mac, once the Home page is ready (ogsas, 2026-11-14, mock-up A « Le m prend sa place »
/// chosen among four, <c>/mnt/project-files/animation-entree/</c>): while Mira loads, nothing moves (the mark and « mira »
/// on black, as before). Then « mira » fades, the black lifts off the Home page, the mark glides from the middle of the
/// window to its place at the top of the rail, shrinking to its size there and stretched a little along the way, and
/// once it lands, the page's tile comes out from under it and drops onto its button as a jelly (<see cref="JellyPill"/>,
/// the switches' pill: the bottom edge first, the top following). It adds no wait: it starts when the Home page is
/// ready, which takes clicks from its first frame. Times in seconds since then, places in the window's units, solved at
/// any time: the same curve at 60 Hz or 240 Hz.
/// </summary>
public sealed class OpeningGlide
{
    /// <summary>« mira » gone.</summary>
    public const double WordOut = .11;
    /// <summary>The black lifts from <see cref="ShadeFrom"/>, gone <see cref="ShadeOut"/> later.</summary>
    public const double ShadeFrom = .04, ShadeOut = .30;
    /// <summary>The mark's way from the middle to the rail.</summary>
    public const double Travel = .43;
    /// <summary>The tile leaves the mark (just before it lands: the mark is already still to the eye).</summary>
    public const double DropFrom = .41;
    /// <summary>The mark at most this much longer along its way (and as much thinner across), at its fastest.</summary>
    public const double MaxStretch = .09;
    /// <summary>Whatever the springs do, the opening is over by then.</summary>
    public const double Longest = 1.4;
    /// <summary>The share of the mark's square its rounded tile fills (22 of the 24 units of its drawing).</summary>
    public const double Tile = 22d / 24;

    /// <summary>A point and a size: the mark's centre and its side.</summary>
    public readonly record struct Spot(double X, double Y, double Size);
    /// <summary>The mark at a moment: its centre, its side, how much longer it is along its way, and that way's angle (radians).</summary>
    public readonly record struct Mark(double X, double Y, double Size, double Stretch, double Angle);
    /// <summary>The tile at a moment (window's units, its corner radius); <see cref="Shown"/> false before it leaves the mark.</summary>
    public readonly record struct Drop(double Left, double Top, double Width, double Height, double Radius, bool Shown);

    private readonly Spot _from, _to;
    private readonly (double Left, double Top, double Width, double Height, double Radius) _button;
    private readonly JellyPill _drop = new();

    /// <param name="from">The mark on the black, in the middle (its centre and side).</param>
    /// <param name="to">The mark in the rail.</param>
    /// <param name="left">The page's button in the rail, where the tile lands, and the tile's corner radius there.</param>
    public OpeningGlide(Spot from, Spot to, double left, double top, double width, double height, double radius)
    {
        _from = from; _to = to; _button = (left, top, width, height, radius);
        // Out of the mark's own tile, its size, downwards: the bottom edge leads (JellyPill: the edge towards which it goes).
        var tile = to.Size * Tile;
        _drop.Put(to.Y - tile / 2, to.Y + tile / 2);
        _drop.Go(top, top + height, DropFrom);
    }

    /// <summary>« mira »'s opacity.</summary>
    public static double Word(double t) => 1 - OutCubic(t / WordOut);
    /// <summary>The black's opacity over the Home page.</summary>
    public static double Shade(double t) => 1 - OutCubic((t - ShadeFrom) / ShadeOut);
    /// <summary>How far along its way the mark is, 0 to 1: a soft start, a long landing (CSS's cubic-bezier(.5, 0, .12, 1)).</summary>
    public static double Way(double t) => Bezier(t / Travel);

    public Mark MarkAt(double t)
    {
        var p = Way(t);
        // Stretched by its speed (in ways per travel), as the switches' pill is: the same area, longer along its way.
        var speed = t is <= 0 or >= Travel ? 0 : (Way(t + .008) - Way(t - .008)) / (.016 / Travel);
        return new Mark(Lerp(_from.X, _to.X, p), Lerp(_from.Y, _to.Y, p), Lerp(_from.Size, _to.Size, p),
            Math.Min(MaxStretch, speed * .035), Math.Atan2(_to.Y - _from.Y, _to.X - _from.X));
    }

    public Drop DropAt(double t)
    {
        var (left, top, width, height, radius) = _button;
        if (t < DropFrom) return new Drop(_to.X, _to.Y, 0, 0, 0, false);
        var (upper, lower, across) = _drop.At(t);
        var tile = _to.Size * Tile;
        var startMiddle = _to.Y; var endMiddle = top + height / 2;
        // How far the tile has come, by its middle: its width and its corners follow, from the mark's tile to the button's.
        var come = endMiddle == startMiddle ? 1 : Math.Clamp(((upper + lower) / 2 - startMiddle) / (endMiddle - startMiddle), 0, 1);
        var wide = Lerp(tile, width, come) * across;
        var middle = Lerp(_to.X, left + width / 2, come);
        var tall = Math.Max(0, lower - upper);
        var corner = Math.Min(Lerp(tile * .27, radius, come), Math.Min(wide, tall) / 2);
        return new Drop(middle - wide / 2, upper, wide, tall, corner, true);
    }

    /// <summary>How lit the button's ink is (0 its usual, 1 as on the tile): how much of it the tile covers.</summary>
    public double Ink(double t)
    {
        var drop = DropAt(t);
        return drop.Shown ? SegmentedSwitch.Lit(drop.Top, drop.Top + drop.Height, _button.Top, _button.Top + _button.Height) : 0;
    }

    /// <summary>The mark in the rail, the tile still on its button: nothing left to draw.</summary>
    public bool Done(double t) => t >= Longest || t >= Travel && t > DropFrom && !_drop.Moving(t);

    private static double Lerp(double from, double to, double share) => from + (to - from) * share;
    private static double OutCubic(double t) { t = Math.Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }

    /// <summary>cubic-bezier(.5, 0, .12, 1) at <paramref name="x"/> (0 to 1), solved by Newton's method.</summary>
    private static double Bezier(double x)
    {
        const double x1 = .5, y1 = 0, x2 = .12, y2 = 1;
        x = Math.Clamp(x, 0, 1);
        if (x is 0 or 1) return x;
        const double cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx, cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
        var s = x;
        for (var i = 0; i < 8; i++)
        {
            var f = ((ax * s + bx) * s + cx) * s - x; var d = (3 * ax * s + 2 * bx) * s + cx;
            if (Math.Abs(d) < 1e-6) break;
            s -= f / d;
        }
        // Newton may step past the ends near a flat start: bisect then.
        if (s is < 0 or > 1 || Math.Abs(((ax * s + bx) * s + cx) * s - x) > 1e-7)
        {
            double low = 0, high = 1; s = x;
            for (var i = 0; i < 40; i++) { var at = ((ax * s + bx) * s + cx) * s; if (at < x) low = s; else high = s; s = (low + high) / 2; }
        }
        return ((ay * s + by) * s + cy) * s;
    }
}
