namespace Mira.Core;

/// <summary>Frame-rate independent easing, with immediate reversal and bounded accumulated wheel input.</summary>
public sealed class ScrollMotion
{
    public double Position { get; private set; }
    public double Target { get; private set; }
    public bool Moving => Math.Abs(Target - Position) > .2;
    public void Reset(double position) => Position = Target = Math.Max(0, position);
    public void Add(double delta, double maximum)
    {
        if (Math.Sign(delta) != Math.Sign(Target - Position)) Target = Position;
        Target = Math.Clamp(Target + delta, 0, Math.Max(0, maximum));
    }
    public double Step(double seconds, double maximum)
    {
        Target = Math.Clamp(Target, 0, Math.Max(0, maximum));
        Position += (Target - Position) * (1 - Math.Exp(-Math.Max(0, seconds) / .065));
        Position = Math.Clamp(Position, 0, Math.Max(0, maximum));
        if (!Moving) Position = Target;
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
