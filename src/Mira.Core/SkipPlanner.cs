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

    /// <summary>A new title: nothing known yet.</summary>
    public void Reset()
    {
        ServerSegments = []; Segments = []; FileChapters = []; ServerChapters = [];
        Active = null; Next = null; ToNext = false; CountdownEnd = null; Stay = false; AutoSkipped.Clear();
    }
    /// <summary>Typed server segments win; chapter titles only fill what the server left empty.</summary>
    public void Rebuild(double duration) => Segments = PlaybackMarkers.Merge(ServerSegments, PlaybackMarkers.FromChapters(Chapters, duration));
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
        var active = offering ? PlaybackMarkers.Offer(Segments, position, duration, Next is not null) : null;
        if (active == Active) return false;
        Active = active; CountdownEnd = null;
        if (active is { } segment)
        {
            ToNext = Next is not null && PlaybackMarkers.EndsPlayback(Segments, segment, duration);
            if (ToNext && autoNext && !Stay) CountdownEnd = PlaybackMarkers.CountdownEnd(position, segment);
        }
        return true;
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
    /// <summary>Stays on this episode: the countdown stops, and the pill only offers the next one. Returns its label.</summary>
    public string Cancel()
    {
        CountdownEnd = null; Stay = true;
        return PlaybackMarkers.Label(SkipKind.Outro, true, true);
    }
}
