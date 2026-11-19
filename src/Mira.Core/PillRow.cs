namespace Mira.Core;

/// <summary>
/// The season pills of a series' page (« Les numéros », <see cref="EpisodeTiles.Compact"/>) going from one season to
/// another, Windows and Mac (maquette A « La gelée », ogsas 2026-11-18, /mnt/project-files/animation-saisons/): one white
/// pill under the row sent as a jelly (<see cref="JellyPill"/>, as Réglages | Aide) to where the chosen season will stand,
/// while each pill opens from its round number to its whole name, or back, and its neighbours follow. The row wraps; a
/// pill going to another line fades in there instead of crossing the page.
/// </summary>
public static class PillRow
{
    /// <summary>How long a pill takes to open or close, in seconds.</summary>
    public const double Opening = .26;
    /// <summary>A round pill: as tall as wide.</summary>
    public const double Round = 36;
    /// <summary>The white pill fading in on another line, in seconds.</summary>
    public const double Fade = .18;

    /// <summary>The ease of an opening: exponential, out, of exponent 5 (<c>Motion.Enter</c> of Windows), 0 to 1.</summary>
    public static double Enter(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (1 - Math.Exp(-5 * t)) / (1 - Math.Exp(-5));
    }

    /// <summary>The number alone, from 1 to 0 over the first 40 % of an opening.</summary>
    public static double Number(double open) => 1 - Math.Clamp(open / .4, 0, 1);
    /// <summary>The whole name, from 0 to 1 between 30 % and the end of an opening: never both readable together.</summary>
    public static double Name(double open) => Math.Clamp((open - .3) / .7, 0, 1);

    /// <summary>A pill's width at <paramref name="open"/> (0 round, 1 whole).</summary>
    public static double Width(double round, double whole, double open) => round + (whole - round) * Math.Clamp(open, 0, 1);

    /// <summary>
    /// Each pill's place in a row of <paramref name="room"/> that wraps: left to right, <paramref name="gap"/> between two,
    /// a new line (<paramref name="height"/> and <paramref name="lineGap"/> below) when the next would pass the edge. A
    /// pill wider than the room alone on its line.
    /// </summary>
    public static (double X, double Y)[] Layout(IReadOnlyList<double> widths, double height, double gap, double lineGap, double room)
    {
        var places = new (double, double)[widths.Count];
        double x = 0, y = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            if (x > 0 && x + widths[i] > room + .01) { x = 0; y += height + lineGap; }
            places[i] = (x, y);
            x += widths[i] + gap;
        }
        return places;
    }

    /// <summary>
    /// The white pill as drawn over the pill shown (<paramref name="cellLeft"/>-<paramref name="cellRight"/>, as it stands
    /// now): once it covers more than half of it, it grows to cover it all, smoothly, so the jelly's rebound inward never
    /// leaves a piece of the name dark on black (Dell, 2026-11-18: « Season 2 » cut at the pill's edge). Its rebound
    /// outward stays.
    /// </summary>
    public static (double Left, double Right) Drawn(double left, double right, double cellLeft, double cellRight)
    {
        var width = cellRight - cellLeft;
        if (width <= 0) return (left, right);
        var cover = (Math.Min(right, cellRight) - Math.Max(left, cellLeft)) / width;
        var t = Math.Clamp((cover - .5) / .4, 0, 1); t = t * t * (3 - 2 * t);
        return (left + (Math.Min(left, cellLeft) - left) * t, right + (Math.Max(right, cellRight) - right) * t);
    }

    /// <summary>
    /// Where a line of pills that scrolls (maquette A « Le fondu », ogsas 2026-11-19) goes so that the pill from
    /// <paramref name="left"/> to <paramref name="right"/> stands clear of its fades (<paramref name="fade"/> wide, at most a
    /// third of the <paramref name="viewport"/>): as it is when already clear, else just far enough; a pill wider than the
    /// room by its start. Within 0 and <paramref name="scrollable"/>, where no fade is drawn.
    /// </summary>
    public static double Reveal(double left, double right, double offset, double viewport, double scrollable, double fade)
    {
        if (scrollable <= 0 || viewport <= 0) return 0;
        fade = Math.Min(fade, viewport / 3);
        var to = offset;
        if (right > to + viewport - fade) to = right - viewport + fade;
        if (left < to + fade) to = left - fade;
        return Math.Clamp(to, 0, scrollable);
    }

    /// <summary>One pill opening or closing: from where it was when told, eased towards 0 or 1; told again mid-way, from where it stands.</summary>
    public sealed class Opener
    {
        private double _from, _to, _since;
        public double Target => _to;
        public void Put(double open) { _from = _to = open; _since = 0; }
        public void Go(double open, double now)
        {
            if (open == _to) return;
            _from = At(now); _to = open; _since = now;
        }
        public double At(double now) => _from == _to ? _to : _from + (_to - _from) * Enter((now - _since) / Opening);
        public bool Moving(double now) => _from != _to && now - _since < Opening;
    }
}
