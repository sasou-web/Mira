namespace Mira.Core;

/// <summary>
/// One playback's state, free of WPF and of mpv: what plays and from which source, where it is, what Jellyfin was told,
/// the seek mpv has not caught up with yet, and the rules that turn it into a start position, a report and an end.
/// The Windows player keeps one (MainWindow.Player); a new playback starts it again with <see cref="Begin"/>.
/// </summary>
public sealed class PlaybackSession
{
    /// <summary>How long a requested position wins over the older one mpv may still report for a poll or two.</summary>
    public static readonly TimeSpan SeekHold = TimeSpan.FromMilliseconds(800);
    /// <summary>A reported position this close to the requested one is mpv having caught up.</summary>
    public const double SeekTolerance = 1.5;

    public MediaItem? Item { get; private set; }
    public MediaSource? Source { get; private set; }
    /// <summary>Jellyfin's play session, or one of Mira's when Jellyfin gave none.</summary>
    public string PlaySessionId { get; private set; } = "";
    /// <summary>« DirectPlay », or « Transcode » for Jellyfin's conversion (<see cref="ServerStream.PlayMethod"/>).</summary>
    public string PlayMethod { get; private set; } = "DirectPlay";
    /// <summary>In seconds.</summary>
    public double Position { get; set; }
    public double Duration { get; set; }
    public bool Paused { get; set; }
    /// <summary>Jellyfin was told this playback started: it must be told how it stops.</summary>
    public bool Reported { get; set; }
    /// <summary>mpv's reason, when the stream stopped on an error.</summary>
    public string? Failure { get; set; }
    /// <summary>When progress was last sent; <see cref="DateTimeOffset.MinValue"/> asks for a report at once (see <see cref="Cadence.ProgressDue"/>).</summary>
    public DateTimeOffset LastReport { get; set; }
    private double _pendingSeek;
    private DateTimeOffset _pendingUntil;

    /// <summary>
    /// Where a title starts: where it was left (<paramref name="back"/> seconds before, see <see cref="ResumeBack"/>),
    /// unless positions are not remembered or it was watched to the end (or nearly, <see cref="LibraryStore.WatchedThreshold"/>),
    /// which starts over instead of on its last seconds.
    /// </summary>
    public static double StartAt(MediaItem item, bool remember, int back = 0) =>
        !remember || item.UserData.Played || item.Progress >= LibraryStore.WatchedThreshold ? 0 : ResumeBack(TimeSpan.FromTicks(item.UserData.PlaybackPositionTicks).TotalSeconds, back);

    /// <summary>The choices of « Reprendre un peu avant », in seconds; 0 resumes exactly where the title was left.</summary>
    public static readonly int[] ResumeBackChoices = [0, 5, 10, 15];
    /// <summary>Under this much watched (seconds), a title resumes exactly where it was left.</summary>
    public const double ResumeBackFrom = 30;
    /// <summary>
    /// « Reprendre un peu avant »: <paramref name="back"/> seconds before where the title was left, to pick up the thread
    /// rather than land mid-sentence; exactly there under <see cref="ResumeBackFrom"/> seconds watched. A title joined in
    /// « Regarder ensemble » starts where the group is. As Mira web (resumeBack, tests/fixtures/resume.json).
    /// </summary>
    public static double ResumeBack(double position, int back) => back <= 0 || position < ResumeBackFrom ? position : Math.Max(0, position - back);

    /// <summary>A new playback: nothing told to Jellyfin yet, no failure, no seek waiting; its length from Jellyfin until mpv knows it.</summary>
    public void Begin(MediaItem item, MediaSource? source, string playSessionId, double position, string playMethod = "DirectPlay")
    {
        Item = item; Source = source; PlaySessionId = playSessionId; PlayMethod = playMethod;
        Position = position; Duration = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        Reported = false; Failure = null; _pendingSeek = 0; _pendingUntil = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// The same playback on another stream (another quality or track, the connection back): Jellyfin hears of it in the
    /// next progress report, under the new play session. What it was told and where it is stay.
    /// </summary>
    public void Reopened(MediaSource source, string playSessionId, string playMethod)
    {
        Source = source; PlaySessionId = playSessionId; PlayMethod = playMethod; Failure = null; LastReport = DateTimeOffset.MinValue;
    }

    /// <summary>A seek asked for: the position is the target at once, and progress is reported at the next tick.</summary>
    public void Hold(double target, DateTimeOffset now)
    {
        Position = target; _pendingSeek = target; _pendingUntil = now + SeekHold; LastReport = DateTimeOffset.MinValue;
    }

    /// <summary>The position to believe: mpv's, unless a seek was just asked for and mpv still reports elsewhere.</summary>
    public double Settled(double reported, DateTimeOffset now) =>
        now < _pendingUntil && Math.Abs(reported - _pendingSeek) > SeekTolerance ? _pendingSeek : reported;

    /// <summary>
    /// Whether a playback that stops counts as watched: it had loaded, and it ended or got past
    /// <see cref="LibraryStore.WatchedThreshold"/> of its length.
    /// </summary>
    public bool Completes(bool loaded, bool ended) => loaded && (ended || Duration > 0 && Position >= Duration * LibraryStore.WatchedThreshold);

    /// <param name="level">The player's volume, on the slider's scale (above 100 when boosted).</param>
    public PlaybackReport Report(double level, bool muted) => new()
    {
        ItemId = Item?.Id ?? "",
        MediaSourceId = Source?.Id ?? "",
        PlaySessionId = PlaySessionId,
        PositionTicks = TimeSpan.FromSeconds(Math.Max(0, Position)).Ticks,
        IsPaused = Paused,
        VolumeLevel = VolumeBoost.Reported(level),
        IsMuted = muted,
        PlayMethod = PlayMethod
    };

    /// <summary>
    /// A title watched to the end, in every list that holds a copy of it: seen, and starting over next time. It leaves
    /// « Continuer à regarder » and « À suivre ».
    /// </summary>
    public static void MarkWatched(MediaItem finished, IEnumerable<MediaItem> copies, params List<MediaItem>[] leaves)
    {
        foreach (var copy in copies.Append(finished).Where(x => x.Id == finished.Id)) { copy.UserData.Played = true; copy.UserData.PlaybackPositionTicks = 0; }
        foreach (var list in leaves) list.RemoveAll(x => x.Id == finished.Id);
    }
}
