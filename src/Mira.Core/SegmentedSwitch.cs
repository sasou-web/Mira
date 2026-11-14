namespace Mira.Core;

/// <summary>
/// A segmented switch of Mira for Windows and Mac (Réglages | Aide, the player's Quality and Sleep timer, the languages
/// of a subtitle search): a row of parts with one chosen, a solid pill behind it. A new choice sends the pill there as a
/// jelly (<see cref="JellyPill"/>: ogsas, 2026-11-14, « un petit sélecteur jelly avec une animation smooth »), the ink of
/// each part following how much of it the pill covers. One timing and one layout for every such switch.
/// </summary>
public static class SegmentedSwitch
{
    /// <summary>The pill's left edge for part <paramref name="index"/> of <paramref name="count"/> equal parts across <paramref name="width"/>.</summary>
    public static double At(int index, int count, double width) => count <= 1 || width <= 0 ? 0 : Math.Clamp(index, 0, count - 1) * width / count;

    /// <summary>
    /// Where a pill standing at <paramref name="offset"/> is, in parts (0 for the first, 1 for the second, 2.5 halfway
    /// between the third and the fourth): a switch shown in place of another (Réglages turned over to Aide, two windows)
    /// starts its pill where the other one's was, even mid-slide.
    /// </summary>
    public static double Part(double offset, int count, double width) => count <= 1 || width <= 0 ? 0 : Math.Clamp(offset * count / width, 0, count - 1);

    /// <summary>
    /// The width of each part across <paramref name="room"/>: as equal as can be, a part whose label needs more than its
    /// share (« Auto » beside « 6 ») getting what it needs and the others sharing the rest. Equal columns cut « Auto »
    /// (0.9.9, ogsas: 7 parts of 45 px, « Auto » needing 47). Short of room, each part shrinks in proportion.
    /// </summary>
    public static double[] Widths(IReadOnlyList<double> wanted, double room)
    {
        var count = wanted.Count;
        var widths = new double[count];
        if (count == 0 || room <= 0) return widths;
        var total = wanted.Sum(w => Math.Max(0, w));
        if (total >= room)
        {
            for (var i = 0; i < count; i++) widths[i] = total <= 0 ? room / count : Math.Max(0, wanted[i]) * room / total;
            return widths;
        }
        // The common width c: every part at max(wanted, c), the whole filling the room. The widest parts are set aside one
        // by one while they need more than what the others would share.
        var order = Enumerable.Range(0, count).OrderByDescending(i => wanted[i]).ToList();
        var left = room; var shared = count; var common = room / count;
        foreach (var i in order)
        {
            common = left / shared;
            if (wanted[i] <= common) break;
            left -= wanted[i]; shared--;
        }
        for (var i = 0; i < count; i++) widths[i] = Math.Max(wanted[i], common);
        return widths;
    }

    /// <summary>
    /// How lit a part's ink is, from 0 (muted) to 1 (lit): how much of the part the pill covers, eased so the ink settles
    /// at either end. A pill stretched across two parts lights both a little as it passes.
    /// </summary>
    public static double Lit(double pillLeft, double pillRight, double partLeft, double partRight)
    {
        var width = partRight - partLeft;
        if (width <= 0) return 0;
        var cover = Math.Clamp((Math.Min(pillRight, partRight) - Math.Max(pillLeft, partLeft)) / width, 0, 1);
        return cover * cover * (3 - 2 * cover);
    }
}

/// <summary>
/// The pill of a segmented switch going from one part to another as a jelly: each edge is a damped spring, the edge in
/// front stiffer than the one behind, so the pill stretches along the way, the back catching up with a soft rebound
/// (a few pixels past its place, then still). Squashed a little while stretched, as a drop of jelly would be. Springs
/// solved exactly, at any time: the same curve at 60 Hz or 240 Hz, and a new choice mid-way starts from where the pill
/// is and how fast each edge moves. Times in seconds, places in the switch's units.
/// </summary>
public sealed class JellyPill
{
    /// <summary>The edge in front: quick, barely past its place.</summary>
    public const double LeadHz = 3.8, LeadDamping = .82;
    /// <summary>The edge behind: slower, a little past its place before it settles (the rebound).</summary>
    public const double TrailHz = 2.7, TrailDamping = .72;
    /// <summary>The pill never longer than its resting width and this share of it, nor shorter than this share of it.</summary>
    public const double MaxStretch = .6, MinSquash = .8;
    /// <summary>How the height answers the width: (rest ÷ width) to this power, at most <see cref="MaxSwell"/> taller.</summary>
    public const double Squash = .3, MaxSwell = 1.06;
    /// <summary>Close enough and slow enough to be still, in units and units per second.</summary>
    private const double Near = .2, Slow = 4;

    private Edge _left, _right;
    /// <summary>The resting widths of where the pill comes from and where it goes.</summary>
    private double _restFrom, _restTo;

    /// <summary>Where the pill goes (or stands).</summary>
    public double TargetLeft => _left.Target;
    public double TargetRight => _right.Target;

    /// <summary>The pill put at once (no motion, first layout, a new width).</summary>
    public void Put(double left, double right)
    {
        _left = Edge.Still(left); _right = Edge.Still(right);
        _restFrom = _restTo = Math.Max(0, right - left);
    }

    /// <summary>The pill sent to [<paramref name="left"/>, <paramref name="right"/>] at <paramref name="now"/>, from where it is and as fast as it moves.</summary>
    public void Go(double left, double right, double now)
    {
        // From where the pill shows (held within its stretch), as fast as each edge moves.
        var (l, r, _) = At(now); var lv = _left.At(now).Speed; var rv = _right.At(now).Speed;
        // Going right: the right edge leads. Going left: the left one.
        var rightwards = left + right > l + r;
        _restFrom = _restTo; _restTo = Math.Max(0, right - left);
        _left = new Edge(left, l, lv, now, rightwards ? TrailHz : LeadHz, rightwards ? TrailDamping : LeadDamping);
        _right = new Edge(right, r, rv, now, rightwards ? LeadHz : TrailHz, rightwards ? LeadDamping : TrailDamping);
    }

    /// <summary>Whether the pill still moves at <paramref name="now"/>.</summary>
    public bool Moving(double now) => !_left.Settled(now) || !_right.Settled(now);

    /// <summary>The pill at <paramref name="now"/>: its edges, and its height as a share of its resting height.</summary>
    public (double Left, double Right, double Height) At(double now)
    {
        if (!Moving(now)) return (_left.Target, _right.Target, 1);
        var (l, _) = _left.At(now); var (r, _) = _right.At(now);
        var longest = Math.Max(_restFrom, _restTo); var shortest = Math.Min(_restFrom, _restTo);
        // Held within its stretch: the edge behind follows the one in front, as a band at its length would.
        var going = _right.Target + _left.Target - (_left.Start + _right.Start);
        var width = r - l;
        var most = longest * (1 + MaxStretch); var least = shortest * MinSquash;
        if (width > most) { if (going >= 0) l = r - most; else r = l + most; width = most; }
        else if (width < least && least > 0) { var middle = (l + r) / 2; l = middle - least / 2; r = middle + least / 2; width = least; }
        var height = 1d;
        if (width > longest && width > 0) height = Math.Pow(longest / width, Squash);
        else if (width < shortest && width > 0) height = Math.Min(MaxSwell, Math.Pow(shortest / width, Squash));
        return (l, r, height);
    }

    /// <summary>One edge: a damped spring towards <see cref="Target"/>, solved exactly from (<see cref="Start"/>, its speed then) at <see cref="Since"/>.</summary>
    private readonly record struct Edge(double Target, double Start, double Speed, double Since, double Hz, double Damping)
    {
        public static Edge Still(double at) => new(at, at, 0, 0, LeadHz, LeadDamping);

        public (double At, double Speed) At(double now)
        {
            var y0 = Start - Target;
            if (y0 == 0 && Speed == 0) return (Target, 0);
            var t = Math.Max(0, now - Since);
            var w = 2 * Math.PI * Hz; var a = Damping * w; var wd = w * Math.Sqrt(1 - Damping * Damping);
            var b = (Speed + a * y0) / wd;
            var e = Math.Exp(-a * t); var c = Math.Cos(wd * t); var s = Math.Sin(wd * t);
            return (Target + e * (y0 * c + b * s), e * ((wd * b - a * y0) * c + (-a * b - wd * y0) * s));
        }

        public bool Settled(double now)
        {
            var (at, speed) = At(now);
            return Math.Abs(at - Target) < Near && Math.Abs(speed) < Slow;
        }
    }
}
