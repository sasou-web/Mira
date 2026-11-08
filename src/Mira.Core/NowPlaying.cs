namespace Mira.Core;

/// <summary>A press of the system's media controls: a key of the keyboard, a headset's button, Control Center.</summary>
public enum MediaKey { Play, Pause, TogglePause, Stop, Back, Forward, Next, Seek }

/// <summary>What the system's media controls show of the playback: the title, where it is, whether it plays.</summary>
/// <param name="Ready">The file is open: before (or between two titles), the keys do nothing.</param>
/// <param name="CanNext">There is an episode after this one.</param>
public sealed record NowPlayingState(string Title, string Subtitle, bool Ready, bool Paused, bool CanNext, double Position, double Duration, double Rate)
{
    /// <summary>The speed the system moves the bar at: still while paused or loading.</summary>
    public double Moving => Ready && !Paused && double.IsFinite(Rate) && Rate > 0 ? Rate : 0;

    /// <summary>Times the system can draw: finite, never below zero, the position never past the end.</summary>
    public NowPlayingState Tidy()
    {
        var duration = double.IsFinite(Duration) ? Math.Max(0, Duration) : 0;
        var position = double.IsFinite(Position) ? Math.Max(0, Position) : 0;
        if (duration > 0) position = Math.Min(position, duration);
        return this with { Position = position, Duration = duration, Rate = double.IsFinite(Rate) && Rate > 0 ? Rate : 1 };
    }
}

/// <summary>
/// The system's media controls as a remote for the player that is open, never a second player: on the Mac, the keyboard's
/// play and track keys (F7 to F9), the button of AirPods or a headset, « Now Playing » of Control Center and the menu bar;
/// the same keys as the system media controls of Windows (WindowsIntegration). The system moves the bar by itself from
/// the position and speed it was told: <see cref="Due"/> tells it again only when that changes (a pause, a seek, another
/// speed), not every tick.
/// </summary>
public sealed class NowPlaying
{
    /// <summary>The ±10 s of Control Center and the previous-track key, as the player's ← and →.</summary>
    public const double Skip = 10;
    /// <summary>Seconds the real position may stray from the one the system works out before it is told again.</summary>
    public const double Drift = 1.5;
    private DateTimeOffset _toldAt;

    /// <summary>The state last told to the system; null when nothing plays.</summary>
    public NowPlayingState? Told { get; private set; }

    /// <summary>True when <paramref name="state"/> must be told: the first time, any change but the position, or a position the system would not have reached on its own.</summary>
    public bool Due(NowPlayingState state, DateTimeOffset now)
    {
        state = state.Tidy();
        if (Told is not { } told || told.Title != state.Title || told.Subtitle != state.Subtitle || told.Ready != state.Ready || told.Paused != state.Paused
            || told.CanNext != state.CanNext || told.Rate != state.Rate || Math.Abs(told.Duration - state.Duration) >= 1) return true;
        var expected = told.Position + Math.Max(0, (now - _toldAt).TotalSeconds) * told.Moving;
        if (told.Duration > 0) expected = Math.Min(expected, told.Duration);
        return Math.Abs(state.Position - expected) > Drift;
    }

    /// <summary>Remembers what was just told, with when.</summary>
    public void Tell(NowPlayingState state, DateTimeOffset now) { Told = state.Tidy(); _toldAt = now; }

    /// <summary>Nothing plays any more: the next title is told from the start.</summary>
    public void Clear() => Told = null;

    /// <summary>
    /// Whether the system may send <paramref name="key"/>: play, pause and stop once the file is open, the jumps and the
    /// bar with a length, the next track only to an episode that has one (as on Windows).
    /// </summary>
    public static bool Enabled(MediaKey key, NowPlayingState? state) => state is { Ready: true } s && key switch
    {
        MediaKey.Back or MediaKey.Forward or MediaKey.Seek => s.Duration > 0,
        MediaKey.Next => s.CanNext,
        _ => true,
    };

    /// <summary>Play and Pause: whether the player's pause must change for it (TogglePause always does).</summary>
    public static bool Changes(MediaKey key, bool paused) => key switch
    {
        MediaKey.Play => paused,
        MediaKey.Pause => !paused,
        MediaKey.TogglePause => true,
        _ => false,
    };
}
