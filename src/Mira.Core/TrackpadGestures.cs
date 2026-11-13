namespace Mira.Core;

/// <summary>Which way a trackpad gesture goes, chosen after its first few points of travel.</summary>
public enum SwipeAxis { Undecided, Horizontal, Vertical }

/// <summary>Where the fingers are in a gesture, as macOS says it for each scroll event (<see cref="Trackpad.Phase"/>).</summary>
public enum ScrollPhase
{
    /// <summary>Not said: a mouse's wheel, a scroll Mira could not read. A pause of <see cref="Trackpad.Gap"/> ends a gesture.</summary>
    Unknown,
    /// <summary>The fingers touched the trackpad.</summary>
    Began,
    /// <summary>The fingers moving on it.</summary>
    Moved,
    /// <summary>The fingers left it.</summary>
    Ended,
    /// <summary>The momentum macOS adds once the fingers left: the page keeps scrolling, the fingers did nothing more.</summary>
    Momentum
}

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
    /// <summary>Sideways travel (150 points) after which letting go goes back or forward, as Safari's swipe between pages.</summary>
    public const double Reach = 3;
    /// <summary>A flick (units per ms, 600 points a second) that turns the page however short the travel, or brings it back.</summary>
    public const double Flick = .012;
    /// <summary>The span (ms) of the fingers' last travel their speed is measured on.</summary>
    public const long SpeedSpan = 80;
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

    /// <summary>
    /// A wheel with notches, whose turns Mira eases as macOS's own apps and Windows' Mira do (a trackpad's and a Magic
    /// Mouse's points come as they go, eased by macOS already): as macOS says (<paramref name="precise"/>), else by its
    /// travel, whole lines for a wheel.
    /// </summary>
    public static bool Notched(bool? precise, double deltaX, double deltaY) =>
        precise is { } said ? !said : (deltaX != 0 || deltaY != 0) && deltaX == Math.Round(deltaX) && deltaY == Math.Round(deltaY);
    /// <summary>
    /// A scroll event's NSEventPhase and momentumPhase (masks: began 1, stationary 2, changed 4, ended 8, cancelled 16,
    /// may begin 32), both zero for a mouse's wheel.
    /// </summary>
    public static ScrollPhase Phase(ulong phase, ulong momentumPhase) =>
        momentumPhase != 0 ? ScrollPhase.Momentum
        : (phase & (1 | 32)) != 0 ? ScrollPhase.Began
        : (phase & (8 | 16)) != 0 ? ScrollPhase.Ended
        : (phase & (2 | 4)) != 0 ? ScrollPhase.Moved
        : ScrollPhase.Unknown;
}

/// <summary>One gesture of two fingers on the trackpad, from the scroll events macOS sends for it.</summary>
public sealed class TrackpadSwipe
{
    private long _last = long.MinValue;
    private bool _touching;
    /// <summary>The fingers' last positions (time, sideways travel), for their speed.</summary>
    private readonly Queue<(long Time, double X)> _recent = new();
    /// <summary>The fingers' travel since the gesture began (right and up positive).</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public SwipeAxis Axis { get; private set; }
    /// <summary>Taken by what is under the pointer (a row that can still scroll that way): no page change in this gesture.</summary>
    public bool Claimed { get; private set; }
    /// <summary>Let go (<see cref="Release"/>): the rest of the gesture, momentum included, does nothing more.</summary>
    public bool Done { get; private set; }
    /// <summary>
    /// The page turn this gesture makes, fixed once it goes sideways: -1 back (fingers to the right, the page before
    /// comes from the left, as in Safari), 1 forward, 0 none (not sideways yet, or up and down).
    /// </summary>
    public int Way { get; private set; }
    /// <summary>The last event began a new gesture.</summary>
    public bool Started { get; private set; }
    /// <summary>The fingers left the trackpad: what follows is macOS's momentum, no travel of theirs.</summary>
    public bool Coasting { get; private set; }

    /// <summary>Adds an event; true when it decides the gesture is sideways (the moment to see what is under the pointer).</summary>
    public bool Add(double fingerX, double fingerY, long milliseconds, ScrollPhase phase = ScrollPhase.Unknown)
    {
        Started = Starts(milliseconds, phase);
        if (Started) { X = Y = 0; Axis = SwipeAxis.Undecided; Claimed = Done = Coasting = false; Way = 0; _recent.Clear(); }
        _last = milliseconds;
        _touching = phase is ScrollPhase.Began or ScrollPhase.Moved;
        // A flick would otherwise turn a page by its momentum alone, and a scrub run on after the fingers stopped.
        if (phase == ScrollPhase.Momentum) { Coasting = true; return false; }
        X += fingerX; Y += fingerY;
        _recent.Enqueue((milliseconds, X));
        while (_recent.Count > 2 && milliseconds - _recent.Peek().Time > Trackpad.SpeedSpan) _recent.Dequeue();
        if (Axis != SwipeAxis.Undecided || Math.Max(Math.Abs(X), Math.Abs(Y)) < Trackpad.Lock) return false;
        // Mostly sideways: a scroll down a page always drifts a little, which never goes back a page.
        Axis = Math.Abs(X) > 1.5 * Math.Abs(Y) ? SwipeAxis.Horizontal : SwipeAxis.Vertical;
        if (Axis == SwipeAxis.Horizontal) Way = X > 0 ? -1 : 1;
        return Axis == SwipeAxis.Horizontal;
    }
    /// <summary>
    /// An event at <paramref name="milliseconds"/> begins a new gesture: the fingers touch again, or move again after
    /// leaving (a « began » without travel never reaches Mira), or a pause; never the momentum of the last one, however
    /// late it comes. Asked before <see cref="Add"/>, while the last gesture is still there to be let go.
    /// </summary>
    public bool Starts(long milliseconds, ScrollPhase phase) =>
        phase == ScrollPhase.Began || phase is ScrollPhase.Moved or ScrollPhase.Ended && !_touching || phase != ScrollPhase.Momentum && Ended(milliseconds);
    /// <summary>The fingers' sideways speed at their last event (units per ms, right positive), over their last <see cref="Trackpad.SpeedSpan"/>.</summary>
    public double Speed
    {
        get
        {
            if (_recent.Count < 2) return 0;
            var (time, x) = _recent.Peek();
            return _last > time ? (X - x) / (_last - time) : 0;
        }
    }
    /// <summary>The gesture is over at <paramref name="milliseconds"/> (or the clock went back).</summary>
    public bool Ended(long milliseconds) => _last == long.MinValue || milliseconds - _last > Trackpad.Gap || milliseconds < _last;
    public void Claim() => Claimed = true;
    /// <summary>
    /// How far the fingers took the page turn (units, never below 0): the page follows them while they move. 0 for a
    /// gesture that turns nothing (claimed, let go, up and down).
    /// </summary>
    public double Turn => Way == 0 || Claimed || Done ? 0 : Math.Max(0, Way < 0 ? X : -X);
    /// <summary>
    /// The fingers let go, once per gesture: <see cref="Way"/> when the page turns, past <see cref="Trackpad.Reach"/> or
    /// flicked its way, 0 when it comes back (short of it, or flicked back). <paramref name="rested"/>: the fingers stood
    /// still before leaving (no momentum followed), so their last speed no longer counts.
    /// </summary>
    public int Release(bool rested)
    {
        if (Way == 0 || Claimed || Done) return 0;
        var turn = Turn;
        Done = true;
        var speed = rested ? 0 : -Way * Speed;
        if (speed <= -Trackpad.Flick) return 0;
        return turn >= Trackpad.Reach || speed >= Trackpad.Flick && turn >= 2 * Trackpad.Lock ? Way : 0;
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
