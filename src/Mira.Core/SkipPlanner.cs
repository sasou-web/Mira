namespace Mira.Core;

/// <summary>
/// The passages of one playback and the next-episode shortcut, as both players offer them (MainWindow.PlayerSegments on
/// Windows, PlayerPage on Mac): Jellyfin's segments and chapters, the file's chapters, the passage offered under the
/// playhead, the openings already jumped over and the countdown to the next episode. The pill itself stays with each app.
/// </summary>
public sealed class SkipPlanner
{
    public List<SkipSegment> ServerSegments { get; set; } = [];
    public List<ChapterMark> ServerChapters { get; set; } = [];
    public List<ChapterMark> FileChapters { get; set; } = [];
    /// <summary>File chapters first (the source), Jellyfin's chapter list otherwise.</summary>
    public List<ChapterMark> Chapters => FileChapters.Count > 0 ? FileChapters : ServerChapters;
    /// <summary>The passages, from <see cref="Rebuild"/>.</summary>
    public List<SkipSegment> Segments { get; set; } = [];
    /// <summary>The episode that follows, when there is one.</summary>
    public MediaItem? Next { get; set; }
    /// <summary>
    /// The film this playback is the trailer of (« Bande-annonce avant le film », <see cref="Trailers.PlaysFirst"/>): the
    /// pill offers to skip the whole trailer to it, and the end of the trailer plays it. Set after <see cref="Reset"/>.
    /// </summary>
    public MediaItem? Film { get; set; }
    /// <summary>The passage offered over the video.</summary>
    public SkipSegment? Active { get; set; }
    /// <summary>The pill plays the next episode: skipping would only leave credits, previews or a few seconds.</summary>
    public bool ToNext { get; set; }
    /// <summary>Where the countdown to the next episode ends, in playback time: pausing pauses it.</summary>
    public double? CountdownEnd { get; set; }
    /// <summary>The viewer stays on this episode: no countdown, and no automatic jump when it ends.</summary>
    public bool Stay { get; set; }
    /// <summary>Openings and recaps already jumped over by themselves in this title: going back into one plays it.</summary>
    public HashSet<SkipSegment> AutoSkipped { get; } = [];
    public bool Counting => CountdownEnd is not null && Active is not null;
    /// <summary>Where, in playback time, <see cref="Active"/> was first offered: the pill shows by itself from there.</summary>
    public double OfferedAt { get; set; } = double.NaN;
    /// <summary>
    /// How long the pill shows by itself, in playback time, once its passage starts (or once the playhead lands in it);
    /// after that only with the player's controls, as Netflix and Plex do: a pill over the whole opening was in the way.
    /// </summary>
    public const double ShownFor = 4;

    /// <summary>A new title: nothing known yet.</summary>
    public void Reset()
    {
        ServerSegments = []; Segments = []; FileChapters = []; ServerChapters = [];
        Active = null; OfferedAt = double.NaN; Next = null; Film = null; ToNext = false; CountdownEnd = null; Stay = false; AutoSkipped.Clear();
    }
    /// <summary>Typed server segments win; chapter titles only fill what the server left empty (see <see cref="PlaybackMarkers.Passages"/>).</summary>
    public void Rebuild(double duration) => Segments = PlaybackMarkers.Passages(ServerSegments, Chapters, duration);
    /// <summary>
    /// The opening or recap to jump over at <paramref name="position"/>, with « Passer les intros et les récaps »: once
    /// each (see <see cref="PlaybackMarkers.AutoSkip"/>), so it counts as jumped from here.
    /// </summary>
    public SkipSegment? AutoSkip(double position, bool enabled)
    {
        if (PlaybackMarkers.AutoSkip(Segments, position, enabled, AutoSkipped) is not { } passage) return null;
        AutoSkipped.Add(passage); return passage;
    }
    /// <summary>
    /// Moves to the passage offered at <paramref name="position"/>, none when <paramref name="offering"/> is false. True
    /// when it changed: the pill then says <see cref="Label"/>, and the countdown starts over with « Enchaîner les
    /// épisodes » (<paramref name="autoNext"/>) when the pill leads to the next episode, unless the viewer stays.
    /// </summary>
    public bool Offer(double position, double duration, bool offering, bool autoNext)
    {
        var active = !offering ? null
            : Film is not null ? new SkipSegment(SkipKind.Trailer, 0, Math.Max(duration, 1))
            : PlaybackMarkers.Offer(Segments, position, duration, Next is not null);
        if (active == Active) return false;
        Active = active; CountdownEnd = null; OfferedAt = position;
        if (active is { } segment)
        {
            ToNext = Next is not null && PlaybackMarkers.EndsPlayback(Segments, segment, duration);
            if (ToNext && autoNext && !Stay) CountdownEnd = PlaybackMarkers.CountdownEnd(position, segment);
        }
        return true;
    }
    /// <summary>
    /// Whether the pill is over the video at <paramref name="position"/>: always with the player's controls or during the
    /// countdown; paused, only with the controls; playing, for <see cref="ShownFor"/> seconds after its passage started.
    /// Going back to before that moment shows it again.
    /// </summary>
    public bool Shows(double position, bool controls, bool paused)
    {
        if (Active is null) return false;
        if (controls || Counting) return true;
        if (paused) return false;
        return !double.IsFinite(OfferedAt) || position < OfferedAt + ShownFor;
    }
    public string Label(bool episode) => Active is { } segment ? PlaybackMarkers.Label(segment.Kind, episode, ToNext) : "";
    /// <summary>The next episode, when the countdown is over at <paramref name="position"/>; the countdown stops there.</summary>
    public MediaItem? Due(double position)
    {
        if (!Counting || Math.Ceiling(CountdownEnd!.Value - position) > 0 || Next is not { } next) return null;
        CountdownEnd = null; return next;
    }
    /// <summary>The pill during the countdown, null without one.</summary>
    public string? CountdownText(double position) =>
        Counting ? L("Épisode suivant dans ", "Next episode in ") + $"{Math.Max(1, (int)Math.Ceiling(CountdownEnd!.Value - position))} s" : null;
    /// <summary>How much of the countdown has gone at <paramref name="position"/>, 0 to 1: the ring of the next episode's card.</summary>
    public double CountdownDone(double position) =>
        Counting && double.IsFinite(position) ? Math.Clamp(1 - (CountdownEnd!.Value - position) / PlaybackMarkers.CountdownLength, 0, 1) : 0;
    /// <summary>Stays on this episode: the countdown stops, and the pill only offers the next one. Returns its label.</summary>
    public string Cancel()
    {
        CountdownEnd = null; Stay = true;
        return PlaybackMarkers.Label(SkipKind.Outro, true, true);
    }
}
