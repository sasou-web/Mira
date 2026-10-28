namespace Mira.Core;

/// <summary>
/// « Image » of Mira for Windows (Réglages → Lecture): how mpv draws the video. « Standard » is mpv's own; « Cinéma »
/// (default) times each frame to the screen (a 24 images/s film on a 60 Hz screen without judder) and smooths banding,
/// cheap enough for a laptop's integrated graphics; « Maximale » adds mpv's high-quality scaling (its
/// <c>high-quality</c> profile), for a graphics card. Applied when a playback starts: <c>interpolation</c> sizes the
/// hardware decoder's frames when it opens. A PC that cannot keep up (<see cref="PictureWatch"/>) gets the level below.
/// </summary>
public static class PictureQuality
{
    public const string Standard = "standard", Cinema = "cinema", Best = "max";
    public static IReadOnlyList<(string Key, string Label, string Details)> Choices =>
    [
        (Standard, L("Standard", "Standard"), L("Le rendu d’origine de mpv, le plus léger", "mpv’s own rendering, the lightest")),
        (Cinema, L("Cinéma", "Cinema"), L("Mouvements fluides sur tout écran, sans bandes dans les dégradés", "Smooth motion on any screen, no banding in gradients")),
        (Best, L("Maximale", "Maximum"), L("Cinéma, avec la mise à l’échelle la plus fine : pour une vraie carte graphique", "Cinema, with the finest scaling: for a dedicated graphics card")),
    ];
    /// <summary>A saved choice Mira knows, else « Cinéma ».</summary>
    public static string Known(string? choice) => Choices.Any(x => x.Key == choice) ? choice! : Cinema;
    public static string Label(string? choice) => Choices.First(x => x.Key == Known(choice)).Label;
    /// <summary>The level under this one; null under « Standard ».</summary>
    public static string? Lower(string? choice) => Known(choice) switch { Best => Cinema, Cinema => Standard, _ => null };

    /// <summary>
    /// mpv's options for a level, each option any level sets: its value there, or null for mpv's own default (so going
    /// down a level while a video plays puts each one back).
    /// </summary>
    public static IReadOnlyList<(string Name, string? Value)> Options(string? choice)
    {
        var level = Known(choice);
        bool cinema = level != Standard, best = level == Best;
        return
        [
            // Frames timed to the screen's refresh, the audio resampled by at most 1 % to follow: no frame shown twice
            // as long as the next (24 on 60 Hz). Interpolation blends only the frames that fall between two refreshes.
            ("video-sync", cinema ? "display-resample" : null),
            ("interpolation", cinema ? "yes" : null),
            ("tscale", cinema ? "oversample" : null),
            ("deband", cinema ? "yes" : null),
            // mpv's high-quality profile (etc/builtin.conf).
            ("scale", best ? "ewa_lanczossharp" : null),
            ("scale-antiring", best ? "0.6" : null),
            ("hdr-peak-percentile", best ? "99.995" : null),
            ("hdr-contrast-recovery", best ? "0.30" : null),
        ];
    }
}

/// <summary>
/// Whether the PC keeps up with the « Image » chosen: frames mpv dropped while the video plays steadily. Not counted:
/// a pause, a seek, waiting for data, another speed, the window resized or put in full screen (display-timed frames
/// are skipped then, by design), and the few seconds after each.
/// </summary>
public sealed class PictureWatch
{
    /// <summary>Dropped frames in <see cref="Window"/> of steady playback that mean the PC cannot keep up: about 5 % at 24 images/s.</summary>
    public const int Limit = 12;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10), Settle = TimeSpan.FromSeconds(3);
    private readonly Queue<(DateTimeOffset At, long Drops)> _samples = new();
    private DateTimeOffset _settledAt = DateTimeOffset.MinValue;
    /// <summary>
    /// One look at the player; true once the drops since <see cref="Window"/> ago reach <see cref="Limit"/> (then it
    /// starts over, for the level below).
    /// </summary>
    public bool Struggling(DateTimeOffset now, long drops, bool steady)
    {
        if (!steady) { _samples.Clear(); _settledAt = now + Settle; return false; }
        if (now < _settledAt) { _samples.Clear(); return false; }
        // mpv's counter starts again with a new file or a reopened stream.
        if (_samples.Count > 0 && drops < _samples.Last().Drops) _samples.Clear();
        _samples.Enqueue((now, drops));
        while (_samples.Count > 1 && now - _samples.Peek().At > Window) _samples.Dequeue();
        if (drops - _samples.Peek().Drops < Limit) return false;
        Reset(now); return true;
    }
    public void Reset(DateTimeOffset now) { _samples.Clear(); _settledAt = now + Settle; }
}
