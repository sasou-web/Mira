namespace Mira.Core;

/// <summary>Which way a trackpad gesture goes, chosen after its first few points of travel.</summary>
public enum SwipeAxis { Undecided, Horizontal, Vertical }

/// <summary>
/// The rules of Mira for Mac's trackpad gestures, kept apart from Avalonia to be tested anywhere. macOS sends a two-finger
/// swipe as scroll events, momentum included, and Avalonia passes on neither their phase nor the end of the fingers'
/// contact: a gesture is the run of events without a pause of <see cref="Gap"/>. Distances are Avalonia's wheel units
/// (50 points of a trackpad each), in the direction of the fingers (<see cref="Finger"/>).
/// </summary>
public static class Trackpad
{
    /// <summary>A pause this long ends a gesture (ms): the fingers left the trackpad and the momentum died out.</summary>
    public const long Gap = 150;
    /// <summary>The travel after which a gesture's axis is chosen, about 15 points.</summary>
    public const double Lock = .3;
    /// <summary>Sideways travel (150 points) that goes back or forward, as Safari's swipe between pages.</summary>
    public const double Reach = 3;
    /// <summary>Seconds of the film per unit of sideways travel in the player: a 150-point swipe moves 6 seconds.</summary>
    public const double SecondsPerUnit = 2;
    /// <summary>Volume points per unit of travel up or down over the picture (as the wheel over it on Windows).</summary>
    public const double VolumePerUnit = 12;
    /// <summary>The pinch (sum of macOS's magnifications) that changes the player: full screen out, back or smaller in.</summary>
    public const double Pinch = .25;
    /// <summary>The size of the grids' posters, as a share of their usual width.</summary>
    public const double ZoomMin = .75, ZoomMax = 1.6;

    /// <summary>
    /// The fingers' own movement (right and up positive) from a wheel delta (positive when the content moves right and
    /// down). With natural scrolling (the Mac's default) the content follows the fingers; without it, it goes the other way.
    /// </summary>
    public static (double X, double Y) Finger(double deltaX, double deltaY, bool natural) => natural ? (deltaX, -deltaY) : (-deltaX, deltaY);

    /// <summary>A grid's zoom after a pinch of <paramref name="magnification"/> (positive outwards), within its bounds.</summary>
    public static double Zoom(double zoom, double magnification) => Math.Clamp(zoom * (1 + magnification), ZoomMin, ZoomMax);
}

/// <summary>One gesture of two fingers on the trackpad, from the scroll events macOS sends for it.</summary>
public sealed class TrackpadSwipe
{
    private long _last = long.MinValue;
    /// <summary>The fingers' travel since the gesture began (right and up positive).</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public SwipeAxis Axis { get; private set; }
    /// <summary>Taken by what is under the pointer (a row that can still scroll that way): no page change in this gesture.</summary>
    public bool Claimed { get; private set; }
    /// <summary>The gesture already went back or forward: the rest of it, momentum included, does nothing more.</summary>
    public bool Done { get; private set; }

    /// <summary>Adds an event; true when it decides the gesture is sideways (the moment to see what is under the pointer).</summary>
    public bool Add(double fingerX, double fingerY, long milliseconds)
    {
        if (Ended(milliseconds)) { X = Y = 0; Axis = SwipeAxis.Undecided; Claimed = Done = false; }
        _last = milliseconds;
        X += fingerX; Y += fingerY;
        if (Axis != SwipeAxis.Undecided || Math.Max(Math.Abs(X), Math.Abs(Y)) < Trackpad.Lock) return false;
        // Mostly sideways: a scroll down a page always drifts a little, which never goes back a page.
        Axis = Math.Abs(X) > 1.5 * Math.Abs(Y) ? SwipeAxis.Horizontal : SwipeAxis.Vertical;
        return Axis == SwipeAxis.Horizontal;
    }
    /// <summary>The gesture is over at <paramref name="milliseconds"/> (or the clock went back).</summary>
    public bool Ended(long milliseconds) => _last == long.MinValue || milliseconds - _last > Trackpad.Gap || milliseconds < _last;
    public void Claim() => Claimed = true;
    /// <summary>How far toward a page change, from -1 (forward) to 1 (back), 0 for a gesture that will not change the page.</summary>
    public double Progress => Axis == SwipeAxis.Horizontal && !Claimed && !Done ? Math.Clamp(X / Trackpad.Reach, -1, 1) : 0;
    /// <summary>
    /// Once per gesture, when it has gone far enough: -1 back (fingers to the right, the page before slides in from the
    /// left, as in Safari), 1 forward; otherwise 0.
    /// </summary>
    public int Swiped()
    {
        if (Axis != SwipeAxis.Horizontal || Claimed || Done || Math.Abs(X) < Trackpad.Reach) return 0;
        Done = true;
        return X > 0 ? -1 : 1;
    }
}

/// <summary>One pinch on the trackpad: macOS sends it as small magnifications, added up until a pause of <see cref="Trackpad.Gap"/>.</summary>
public sealed class TrackpadPinch
{
    private long _last = long.MinValue;
    public double Total { get; private set; }
    public bool Done { get; private set; }
    public void Add(double magnification, long milliseconds)
    {
        if (_last == long.MinValue || milliseconds - _last > Trackpad.Gap || milliseconds < _last) { Total = 0; Done = false; }
        _last = milliseconds; Total += magnification;
    }
    /// <summary>Once per pinch, past <see cref="Trackpad.Pinch"/>: 1 outwards, -1 inwards; otherwise 0.</summary>
    public int Pinched()
    {
        if (Done || Math.Abs(Total) < Trackpad.Pinch) return 0;
        Done = true;
        return Total > 0 ? 1 : -1;
    }
}
