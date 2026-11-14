using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>
/// The time of the player's bar, Windows and Mac (maquette B « Ligne du chapitre », chosen by ogsas on 2026-11-14): the
/// line above the time says the chapter and when the title ends (« Part A · Ends at 10:41 PM », the end alone without
/// chapters), and a click on the time shows what is left instead of what is played (« −26:30 / 40:12 »), kept
/// (<see cref="PlayerSettings.TimeLeft"/>). The mini-player and picture in picture keep « played / length ».
/// </summary>
public static class PlayerTime
{
    /// <summary>« 1:02:03 », « 12:34 ».</summary>
    public static string Clock(double seconds) => Viewers.Clock(seconds);
    /// <summary>The time on the left of « / »: played, or what is left after a minus sign.</summary>
    public static string Position(double position, double duration, bool left) =>
        left && duration > 0 && double.IsFinite(duration) ? "−" + Clock(Math.Max(0, duration - Math.Max(0, position))) : Clock(position);
    /// <summary>
    /// « Fin à 22 h 41 », « Ends at 10:41 PM »: now plus what is left at this speed; null without a length. It moves on
    /// while the film is paused, as the end does.
    /// </summary>
    public static string? EndsAt(double position, double duration, double speed, DateTime now)
    {
        if (!(duration > 0) || !double.IsFinite(duration) || !double.IsFinite(position)) return null;
        var left = Math.Max(0, duration - Math.Max(0, position)) / (speed > 0 && double.IsFinite(speed) ? speed : 1);
        return L("Fin à ", "Ends at ") + MediaQuality.Clock(now.AddSeconds(left));
    }
    /// <summary>The line above the time: the chapter, then the end (« Part A · Ends at 10:41 PM »), either alone.</summary>
    public static string Line(string? chapter, string? ends) =>
        string.Join(" · ", new[] { chapter, ends }.Where(x => !string.IsNullOrWhiteSpace(x)));
}
