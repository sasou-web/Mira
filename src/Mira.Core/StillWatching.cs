namespace Mira.Core;

/// <summary>
/// « Tu regardes toujours ? »: after <see cref="Episodes"/> episodes in a row with nobody touching anything, the next one
/// does not start by itself; the player stops at the end of the one playing and asks, so a night asleep in front of a
/// series does not mark ten episodes watched. The same on Windows, Mac and iPhone (StillWatching.swift; cases shared in
/// tests/fixtures/still-watching.json). Never in a group: the group goes on for everyone. On by default
/// (<see cref="PlayerSettings.StillWatching"/>).
/// </summary>
public sealed class StillWatching
{
    /// <summary>Episodes played in a row, untouched, before the next one waits for an answer.</summary>
    public const int Episodes = 3;
    private readonly List<DateTimeOffset> _starts = [];
    private DateTimeOffset _touched = DateTimeOffset.MinValue;

    /// <summary>
    /// An episode or a film starts. Started by hand (Lecture, the next-episode button, « Continuer »): a new run, from
    /// this one; by itself (the countdown, the end of the previous episode): one more in the run.
    /// </summary>
    public void Started(DateTimeOffset at, bool byItself)
    {
        if (!byItself) _starts.Clear();
        _starts.Add(at);
    }

    /// <summary>
    /// Someone is there: an input the system does not see (a controller read by Mira) or one Mira counts itself (a touch on
    /// the iPhone). The apps also pass the system's last input to <see cref="Asks"/>.
    /// </summary>
    public void Touched(DateTimeOffset at)
    {
        if (at > _touched) _touched = at;
    }

    /// <summary>The run is over: the player closed.</summary>
    public void Clear() { _starts.Clear(); _touched = DateTimeOffset.MinValue; }

    /// <summary>
    /// The next episode would start by itself now: true when it should ask instead. <paramref name="lastInput"/> is the
    /// system's last keyboard, mouse or touch input (Windows' GetLastInputInfo, macOS' CGEventSource), MinValue unknown;
    /// an episode is untouched when it started after the last input.
    /// </summary>
    public bool Asks(DateTimeOffset lastInput, bool enabled)
    {
        if (!enabled) return false;
        var input = lastInput > _touched ? lastInput : _touched;
        return Untouched(input) >= Episodes;
    }

    /// <summary>Episodes of this run that started after <paramref name="input"/>.</summary>
    public int Untouched(DateTimeOffset input) => _starts.Count(start => start >= input);

    public static string Question => L("Tu regardes toujours ?", "Are you still watching?");
    /// <summary>The screen's line: why it stopped, and what comes next.</summary>
    public static string Reason(string? next) =>
        L($"Mira s’est arrêté après {Episodes} épisodes de suite sans que rien ne soit touché.", $"Mira stopped after {Episodes} episodes in a row with nothing touched.")
        + (string.IsNullOrWhiteSpace(next) ? "" : L(" À suivre : ", " Up next: ") + next);
    public static string Continue => L("Continuer", "Keep watching");
    public static string Setting => L("Demander si tu regardes toujours", "Ask if you’re still watching");
    public static string SettingHelp => L($"Après {Episodes} épisodes de suite sans rien toucher, Mira s’arrête et demande avant le suivant.", $"After {Episodes} episodes in a row without touching anything, Mira stops and asks before the next one.");
}
