namespace Mira.Core;

/// <summary>
/// A name cut short by « … » (a download's « My Dress-Up Darling · Complete seaso… », an episode's name in its tile) slides
/// to show its end while the pointer rests on the place it sits in (its row, its tile): still for a moment, so its start can
/// be read, then along at a reading pace, eased at both ends, and it stays at its end; once the pointer leaves, it slides
/// back to its start and the « … » comes back. Nothing for a name that fits, nothing with animations reduced (its tooltip
/// says it whole). One instance per name, told what the pointer does, asked where the name is at each frame (Windows and Mac).
/// </summary>
public sealed class TitleSlide
{
    /// <summary>Still under the pointer before it slides: the start of the name is read first, and a pointer passing by moves nothing.</summary>
    public static readonly TimeSpan Rest = TimeSpan.FromSeconds(.8);
    /// <summary>Its pace along, in device-independent pixels a second, on average: slow enough to read as it goes.</summary>
    public const double Speed = 45;
    /// <summary>Along at least this long, a name cut by a letter or two included: never a jump.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromSeconds(.5);
    /// <summary>Back to its start once the pointer has left: quick, the pointer is already elsewhere.</summary>
    public static readonly TimeSpan Back = TimeSpan.FromSeconds(.32);
    /// <summary>The soft edges where the name goes under its box, in device-independent pixels.</summary>
    public const double Fade = 14;

    private enum Phase { Still, Resting, Going, AtEnd, Returning }
    private Phase _phase;
    private TimeSpan _since, _length;
    private double _from, _overflow;

    /// <summary>A name whose whole width (<paramref name="whole"/>) is wider than its box (<paramref name="shown"/>) by more than a pixel's rounding.</summary>
    public static bool IsCut(double whole, double shown) => double.IsFinite(whole) && double.IsFinite(shown) && shown > 0 && whole - shown > .5;

    /// <summary>
    /// Shown whole, past its box (no « … »): from the moment it starts to slide until it is back at its start. Still or
    /// resting under the pointer, it keeps its « … ».
    /// </summary>
    public bool Whole => _phase is Phase.Going or Phase.AtEnd or Phase.Returning;

    /// <summary>It moves at this moment: asked where it is at each frame.</summary>
    public bool Moving => _phase is Phase.Going or Phase.Returning;

    /// <summary>When it next changes by itself while still (its rest under the pointer over): no frame is needed before.</summary>
    public TimeSpan? Wakes => _phase == Phase.Resting ? _since + Rest : null;

    /// <summary>
    /// The pointer came onto its place, the name cut by <paramref name="overflow"/> pixels. A name on its way back slides
    /// on again from where it is, without the rest; one already sliding or at its end carries on.
    /// </summary>
    public void Enter(TimeSpan now, double overflow)
    {
        if (!(overflow > .5) || !double.IsFinite(overflow)) { Stop(); return; }
        _overflow = overflow;
        switch (_phase)
        {
            case Phase.Still: _phase = Phase.Resting; _since = now; break;
            case Phase.Returning: Go(now, At(now)); break;
        }
    }

    /// <summary>The pointer left its place: back to its start, at once when it had not moved yet.</summary>
    public void Leave(TimeSpan now)
    {
        switch (_phase)
        {
            case Phase.Resting: _phase = Phase.Still; break;
            case Phase.Going or Phase.AtEnd:
                var at = At(now);
                if (at <= .01) { _phase = Phase.Still; break; }
                _from = at; _since = now; _length = Back; _phase = Phase.Returning;
                break;
        }
    }

    /// <summary>Back at its start with its « … », at once (the name gone from the screen, animations reduced, its text changed to one that fits).</summary>
    public void Stop() { _phase = Phase.Still; _from = 0; }

    /// <summary>
    /// Its box or its text changed while it slides: it now overflows by <paramref name="overflow"/>. A name that fits again
    /// stops; otherwise it carries on to its new end, from where it is.
    /// </summary>
    public void Resize(TimeSpan now, double overflow)
    {
        if (_phase == Phase.Still || Math.Abs(overflow - _overflow) < .01) return;
        if (!(overflow > .5) || !double.IsFinite(overflow)) { Stop(); return; }
        var at = Math.Min(At(now), overflow);
        _overflow = overflow;
        switch (_phase)
        {
            case Phase.Going or Phase.AtEnd: Go(now, at); break;
            case Phase.Returning: _from = at; break;
        }
    }

    /// <summary>
    /// How far along it is at <paramref name="now"/>, from 0 (its start) to the pixels it overflows by (its end); moves it
    /// on to its next phase when the current one is over.
    /// </summary>
    public double At(TimeSpan now)
    {
        if (_phase == Phase.Resting && now - _since >= Rest) Go(_since + Rest, 0);
        switch (_phase)
        {
            case Phase.Going:
            {
                var t = Part(now);
                if (t >= 1) { _phase = Phase.AtEnd; return _overflow; }
                return _from + (_overflow - _from) * (1 - Math.Cos(Math.PI * t)) / 2;
            }
            case Phase.AtEnd: return _overflow;
            case Phase.Returning:
            {
                var t = Part(now);
                if (t >= 1) { Stop(); return 0; }
                var left = 1 - t;
                return _from * left * left * left;
            }
            default: return 0;
        }
    }

    /// <summary>
    /// How much shows of the name at its left and right edges at <paramref name="at"/> (from <see cref="At"/>), from 0 to 1:
    /// an edge fades only where the name goes on under it.
    /// </summary>
    public (double Left, double Right) Edges(double at)
    {
        if (!Whole || _overflow <= 0) return (1, 1);
        var left = 1 - Math.Clamp(at / Fade, 0, 1);
        var right = 1 - Math.Clamp((_overflow - at) / Fade, 0, 1);
        return (left, right);
    }

    private void Go(TimeSpan now, double from)
    {
        _from = Math.Clamp(from, 0, _overflow); _since = now; _phase = Phase.Going;
        _length = TimeSpan.FromSeconds(Math.Max(Shortest.TotalSeconds, (_overflow - _from) / Speed));
    }

    private double Part(TimeSpan now) => _length <= TimeSpan.Zero ? 1 : Math.Clamp((now - _since) / _length, 0, 1);
}
