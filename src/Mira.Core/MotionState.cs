namespace Mira.Core;

/// <summary>
/// Frame-rate independent easing, with immediate reversal and bounded accumulated wheel input. Each input starts a cubic
/// ease-out from where the list stands to the new target, over <see cref="Duration"/>: fast at once, and still at its end.
/// (An exponential approach never arrives: its last pixels crept on for a fifth of a second after the wheel stopped.)
/// </summary>
public sealed class ScrollMotion
{
    /// <summary>From an input to the list standing still.</summary>
    public const double Duration = .24;
    private double _from, _elapsed = Duration;
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool Moving => _elapsed < Duration && Position != Target;
    public void Reset(double position) { Position = Target = _from = Math.Max(0, position); _elapsed = Duration; }
    public void Add(double delta, double maximum)
    {
        if (Math.Sign(delta) != Math.Sign(Target - Position)) Target = Position;
        Target = Math.Clamp(Target + delta, 0, Math.Max(0, maximum));
        _from = Position; _elapsed = Target == Position ? Duration : 0;
    }
    public double Step(double seconds, double maximum)
    {
        Target = Math.Clamp(Target, 0, Math.Max(0, maximum));
        _elapsed = Math.Min(Duration, _elapsed + Math.Max(0, seconds));
        var left = 1 - _elapsed / Duration;
        Position = _elapsed >= Duration ? Target : _from + (Target - _from) * (1 - left * left * left);
        Position = Math.Clamp(Position, 0, Math.Max(0, maximum));
        return Position;
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
