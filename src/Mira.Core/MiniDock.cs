namespace Mira.Core;

/// <summary>
/// The mini-player of Mira for Windows and Mac, at rest against the window's right edge: tucked away by itself, a sliver of
/// the video showing, it slides out while the pointer rests on it and back once the pointer has left. Where it rests across
/// and down (in device-independent pixels of the window), and when it comes out or goes back: one instance per mini-player,
/// told what the pointer does at each tick.
/// </summary>
public sealed class MiniDock
{
    /// <summary>The mini-player's size and its gap to the window's edges, in device-independent pixels.</summary>
    public const double Width = 384, Height = 216, Gap = 20;
    /// <summary>What shows of it tucked away, against the edge: enough to see the film move and to find it with the pointer.</summary>
    public const double Peek = 14;
    /// <summary>Out after the player shrinks to it, before it tucks itself away: long enough to see where the film went.</summary>
    public static readonly TimeSpan FirstRest = TimeSpan.FromSeconds(1.6);
    /// <summary>
    /// After the pointer has left it, before it goes back: a pointer that slips off its edge does not send it away, and it
    /// does not hang about once left (0.7 s in 0.9.7: « pas réactif »).
    /// </summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(.3);
    /// <summary>The pointer resting on the sliver, before it comes out: only a pointer flying past to the window's edge is let go by.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(.04);
    /// <summary>How often the pointer is looked at while it is in use: once a frame of a 60 Hz screen.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(15);
    /// <summary>The slide out and the slide back, in milliseconds.</summary>
    public const int OutMs = 240, BackMs = 260;
    /// <summary>
    /// The slide's curves, as CSS's cubic-bezier(x1, y1, x2, y2). Out: half the way in the first fifth, then it lands softly
    /// (the curve of the player's shrinking, slow for its first frames, felt late under the pointer); back: a little softer.
    /// </summary>
    public static readonly (double X1, double Y1, double X2, double Y2) OutCurve = (.2, 0, 0, 1), BackCurve = (.3, 0, .1, 1);

    /// <summary>Where a cubic-bezier curve is at <paramref name="t"/> (0 to 1) of its time, from 0 to 1.</summary>
    public static double Ease((double X1, double Y1, double X2, double Y2) curve, double t)
    {
        if (!(t > 0)) return 0;
        if (t >= 1) return 1;
        static double Bezier(double a, double b, double s) => 3 * (1 - s) * (1 - s) * s * a + 3 * (1 - s) * s * s * b + s * s * s;
        double low = 0, high = 1;
        for (var i = 0; i < 20; i++) { var s = (low + high) / 2; if (Bezier(curve.X1, curve.X2, s) < t) low = s; else high = s; }
        return Bezier(curve.Y1, curve.Y2, (low + high) / 2);
    }

    private TimeSpan? _overSince, _backAt;

    /// <summary>Out, whole in view; otherwise tucked away against the edge.</summary>
    public bool Out { get; private set; } = true;

    /// <summary>The player has just shrunk to it: out, and tucked away after <see cref="FirstRest"/> unless the pointer is on it.</summary>
    public void Arrive(TimeSpan now) { Out = true; _overSince = null; _backAt = now + FirstRest; }

    /// <summary>Out at once, as after a drag: back after <see cref="Linger"/> once the pointer is off it.</summary>
    public void Show(TimeSpan now) { Out = true; _overSince = null; _backAt = now + Linger; }

    /// <summary>
    /// What the pointer does now. Returns true when it should move: <see cref="Out"/> then says where to.
    /// </summary>
    /// <param name="over">The pointer is on what shows of it (the sliver tucked away, all of it out), Mira's window under the pointer.</param>
    /// <param name="held">Out whatever the pointer does: dragged, a menu or a question of its own open, the keyboard in it.</param>
    public bool Update(TimeSpan now, bool over, bool held)
    {
        if (held) { _overSince = null; _backAt = null; return Set(true); }
        if (over)
        {
            _backAt = null;
            if (Out) return false;
            _overSince ??= now;
            return now - _overSince >= Dwell && Set(true);
        }
        _overSince = null;
        if (!Out) return false;
        _backAt ??= now + Linger;
        return now >= _backAt && Set(false);
    }

    private bool Set(bool shown)
    {
        if (Out == shown) return false;
        Out = shown; _overSince = null;
        return true;
    }

    /// <summary>Its left edge, out or tucked away, in a window <paramref name="windowWidth"/> wide.</summary>
    public static double Left(bool shown, double windowWidth) => shown ? windowWidth - Gap - Width : windowWidth - Peek;

    /// <summary>
    /// Its top edge: <paramref name="share"/> of the way from the highest it goes (<paramref name="top"/>, under the title bar)
    /// to the lowest (<see cref="Gap"/> above the window's bottom), so that it keeps its place, in proportion, when the
    /// window changes size.
    /// </summary>
    public static double Top(double share, double top, double windowHeight)
    {
        var lowest = Math.Max(top, windowHeight - Gap - Height);
        return top + (lowest - top) * Math.Clamp(double.IsFinite(share) ? share : 1, 0, 1);
    }

    /// <summary>Where it was let go, as a share of the way down (<see cref="Top"/>): 1, the bottom, when there is no room.</summary>
    public static double Share(double y, double top, double windowHeight)
    {
        var lowest = windowHeight - Gap - Height;
        return lowest - top < 1 || !double.IsFinite(y) ? 1 : Math.Clamp((y - top) / (lowest - top), 0, 1);
    }
}
