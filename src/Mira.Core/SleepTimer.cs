namespace Mira.Core;

/// <summary>
/// The player's sleep timer (« Minuterie »), the same on Windows and Mac (Mira web: js/player/sleep.js): a pause after
/// some minutes of the clock, paused or not, or a stop at the end of the episode rather than the next one. It carries
/// on into the next episode, and goes when it has done its work or the player closes.
/// </summary>
public sealed class SleepTimer
{
    /// <summary>The durations offered, in minutes.</summary>
    public static readonly int[] Choices = [15, 30, 60];
    public DateTimeOffset? Until { get; private set; }
    /// <summary>The episode playing is the last: the next one does not start, its offer does not show.</summary>
    public bool AtEnd { get; private set; }
    /// <summary>The menu's choice: minutes among <see cref="Choices"/>, 0 for « at the end of the episode », null when off.</summary>
    public int? Chosen { get; private set; }
    /// <summary>Starts the timer from a menu choice (<see cref="Chosen"/>).</summary>
    public void Choose(int? minutes, DateTimeOffset now)
    {
        Chosen = minutes is >= 0 ? minutes : null;
        Until = minutes is > 0 ? now.AddMinutes(minutes.Value) : null;
        AtEnd = minutes == 0;
    }
    public void Clear() => Choose(null, default);
    /// <summary>True once, when the minutes are over: the player pauses, and the timer is off.</summary>
    public bool Due(DateTimeOffset now)
    {
        if (Until is not { } until || now < until) return false;
        Clear();
        return true;
    }
    /// <summary>« Pause dans 23 min », « Arrêt à la fin de l’épisode »; null when off.</summary>
    public string? Status(DateTimeOffset now) =>
        AtEnd ? "Arrêt à la fin de l’épisode"
        : Until is { } until ? $"Pause dans {Duration(Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes)))}"
        : null;
    /// <summary>« 15 min », « 1 h », « 1 h 30 ».</summary>
    public static string Duration(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00}";
}
