namespace Mira.Core;

/// <summary>
/// The wheel's motion: frame-rate independent, still at its end, with immediate reversal and bounded accumulated input,
/// and never a jolt in its speed. Each input plans a curve from where the list stands, at the speed it has, to the new
/// target, which it reaches still (speed and acceleration nought): a quartic whose speed at its start is the list's.
/// From rest, that is the cubic ease-out over <see cref="Duration"/>, fast at once. Moving, the curve lasts twice the
/// time between the last inputs, from <see cref="Shortest"/> to <see cref="Duration"/>: the next notch finds the list
/// still going, and it stops soon after the last. (A curve started again from rest at each input made the speed drop and
/// rise again at every notch, 44 to 268 % from one frame to the next: the jolts seen as a scroll that is not smooth.
/// An exponential approach never arrives: its last pixels crept on for a fifth of a second after the wheel stopped.)
/// </summary>
public sealed class ScrollMotion
{
    /// <summary>From an input to the list standing still, at most (from rest, exactly).</summary>
    public const double Duration = .24;
    /// <summary>The shortest curve, for a wheel spun fast: from its last notch to the list standing still.</summary>
    public const double Shortest = .12;
    // The curve under way: from _start, _distance further, over _length seconds; _kick is its speed at the start times
    // its length (the list's speed when it was planned). _clock counts the steps' time, which dates the inputs.
    private double _start, _distance, _kick, _length = Duration, _elapsed = Duration, _clock;
    private double _lastInput = double.NaN, _interval = double.NaN;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool Moving => _elapsed < _length && Position != Target;
    /// <summary>The list's speed (units per second, signed) where the last step left it.</summary>
    public double Speed
    {
        get
        {
            if (!Moving) return 0;
            var u = _elapsed / _length;
            return (1 - u) * (1 - u) * (_kick + (12 * _distance - 4 * _kick) * u) / _length;
        }
    }
    public void Reset(double position)
    {
        Position = Target = _start = Math.Max(0, position);
        _distance = _kick = 0; _length = _elapsed = Duration; _lastInput = _interval = double.NaN;
    }
    public void Add(double delta, double maximum)
    {
        var moving = Moving; var speed = Speed;
        // The time between inputs, from the steps' time: inputs within one frame count as one.
        if (!moving) _interval = double.NaN;
        else if (_clock > _lastInput) _interval = double.IsNaN(_interval) ? _clock - _lastInput : (_interval + _clock - _lastInput) / 2;
        _lastInput = _clock;
        var target = moving ? Target : Position;
        // Reversed: from where the list stands, at once (never on in the old direction).
        if (delta != 0 && ((target != Position && (delta > 0) != (target > Position)) || speed * delta < 0)) { target = Position; speed = 0; _interval = double.NaN; }
        Plan(Math.Clamp(target + delta, 0, Math.Max(0, maximum)), speed);
    }
    public double Step(double seconds, double maximum)
    {
        maximum = Math.Max(0, maximum);
        // The content shrank under the target: towards its new end, from where the list stands, at the speed it has.
        if (Target > maximum) { var speed = Speed; Position = Math.Min(Position, maximum); Plan(maximum, speed); }
        seconds = Math.Max(0, seconds); _clock += seconds;
        _elapsed = Math.Min(_length, _elapsed + seconds);
        Position = Math.Clamp(_elapsed >= _length ? Target : At(_elapsed / _length), 0, maximum);
        return Position;
    }
    private double At(double u) => _start + u * (_kick + u * (6 * _distance - 3 * _kick + u * (3 * _kick - 8 * _distance + u * (3 * _distance - _kick))));
    /// <summary>
    /// A curve from here to <paramref name="target"/>, starting at <paramref name="speed"/> if it goes that way (from rest
    /// otherwise). Its speed is (1 - u)² (kick + (12 distance - 4 kick) u) / length: never back while the kick is at most
    /// four times the distance; a list too fast for that comes to rest sooner, at the speed it has.
    /// </summary>
    private void Plan(double target, double speed)
    {
        Target = target; _start = Position; _distance = target - Position; _elapsed = 0;
        var distance = Math.Abs(_distance);
        if (distance == 0) { _kick = 0; _length = _elapsed = Duration; return; }
        var along = speed * _distance > 0 ? Math.Abs(speed) : 0;
        var length = along == 0 || double.IsNaN(_interval) ? Duration : Math.Clamp(2 * _interval, Shortest, Duration);
        var kick = along == 0 ? 3 * distance : along * length;
        if (kick > 4 * distance) { length = 4 * distance / along; kick = 4 * distance; }
        _length = length; _kick = Math.Sign(_distance) * kick;
    }
}

public sealed class CarouselTimeline(double durationSeconds = 8)
{
    public double Duration { get; } = Math.Max(1, durationSeconds);
    public double Elapsed { get; private set; }
    public double Progress => Math.Clamp(Elapsed / Duration, 0, 1);
    public bool Advance(double seconds, bool paused)
    { if (!paused) Elapsed = Math.Min(Duration, Elapsed + Math.Max(0, seconds)); return !paused && Elapsed >= Duration; }
    public void Reset() => Elapsed = 0;
}
