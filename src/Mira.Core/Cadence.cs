namespace Mira.Core;

/// <summary>How often Mira asks Jellyfin and tells it: no more often than a person could notice.</summary>
public static class Cadence
{
    /// <summary>
    /// The background refresh of the page on screen. Jellyfin's WebSocket says when the library changes; while it is
    /// down those changes are missed, so the page is refreshed every 45 s, otherwise only every 5 min, in case a
    /// notification was lost.
    /// </summary>
    public static readonly TimeSpan RefreshUnheard = TimeSpan.FromSeconds(45), RefreshListening = TimeSpan.FromMinutes(5);
    /// <summary>A timer tick a little early still counts.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);
    /// <param name="listening">Jellyfin's WebSocket is open.</param>
    /// <param name="lastRefresh">When the last refresh that reached Jellyfin started.</param>
    public static bool RefreshDue(bool listening, DateTimeOffset lastRefresh, DateTimeOffset now) =>
        now - lastRefresh >= (listening ? RefreshListening : RefreshUnheard) - Slack;
    /// <summary>Progress told to Jellyfin while a video plays (as jellyfin-web does).</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Whether to report progress now: at once when playback pauses or resumes, or when asked (a seek, playback
    /// restarted, written as <see cref="DateTimeOffset.MinValue"/>); every <see cref="ProgressInterval"/> while it plays,
    /// never while it stays paused (the position does not move).
    /// </summary>
    public static bool ProgressDue(bool pauseChanged, bool paused, DateTimeOffset lastReport, DateTimeOffset now) =>
        pauseChanged || lastReport == DateTimeOffset.MinValue || !paused && now - lastReport >= ProgressInterval;
    /// <summary>
    /// Reports still waiting after <paramref name="failures"/> failed deliveries are tried again after 3 s, then less
    /// and less often, up to every 30 s; a new report, or Jellyfin answering again, tries them at once.
    /// </summary>
    public static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, 3 * Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));
}

/// <summary>The status line under the library: offline first, then what is waiting to be sent, then the last delivery.</summary>
public static class SyncStatus
{
    /// <param name="offlineSince">When a refresh last failed to reach Jellyfin; null once one has reached it again.</param>
    /// <returns>The text, and whether it is a warning.</returns>
    public static (string Text, bool Warning) Describe(int pending, string? error, DateTimeOffset? lastSynced, DateTimeOffset? offlineSince)
    {
        var waiting = pending > 0 ? $"{pending} envoi{(pending > 1 ? "s" : "")} en attente" : null;
        // A report delivered after the failure proves Jellyfin answers again.
        if (Offline(lastSynced, offlineSince)) return ("○  Hors connexion · " + (waiting ?? "cache local"), true);
        if (waiting is not null) return ("◌  " + waiting + (error is not null ? " · " + error : ""), true);
        // A local storage failure matters even with nothing waiting: the next reports will not be kept.
        if (error is not null) return ("◌  " + error, true);
        return (lastSynced is { } t ? $"●  Synchronisé à {t.ToLocalTime():HH:mm:ss}" : "●  Connecté à Jellyfin", false);
    }
    public static bool Offline(DateTimeOffset? lastSynced, DateTimeOffset? offlineSince) => offlineSince is { } since && !(lastSynced > since);
}
