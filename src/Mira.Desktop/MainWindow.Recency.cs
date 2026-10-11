using Mira.Core;

namespace Mira.Desktop;

public partial class MainWindow
{
    private List<MediaItem> _recentPlayback = [];
    /// <summary>Films and series taken out of « Continuer à regarder », with when; kept per account in its library cache.</summary>
    private Dictionary<string, DateTimeOffset> _hiddenResume = [];
    private async Task RememberCurrentPlaybackAsync()
    {
        // The row on screen is already right: a cache that cannot be written only loses it for the next start.
        if (RecentSnapshot() is { } snapshot && !_demo && _store is { } store) await Task.Run(() => TryCache(() => store.RememberPlayback(snapshot)));
    }
    /// <summary>The same, waited for <paramref name="limit"/> at most: Mira is closing with Windows (see SessionEnding).</summary>
    private void RememberCurrentPlaybackNow(TimeSpan limit)
    {
        if (RecentSnapshot() is { } snapshot && !_demo && _store is { } store) Task.Run(() => TryCache(() => store.RememberPlayback(snapshot))).Wait(limit);
    }
    /// <summary>The title playing, put first in the recent list and in « Continuer à regarder »; null when nothing plays.</summary>
    private MediaItem? RecentSnapshot()
    {
        if (_session.Item is not { } item || !_loaded) return null;
        var at = DateTimeOffset.UtcNow; item.UserData.LastPlayedDate = at;
        var snapshot = item with { UserData = item.UserData with { } };
        _seasonThumbs.Apply([snapshot]);
        _recentPlayback = ContinueWatching.History(_recentPlayback.Append(snapshot));
        _resume.RemoveAll(x => x.Id == item.Id);
        if (!snapshot.UserData.Played && snapshot.UserData.PlaybackPositionTicks > 0) _resume.Insert(0, snapshot);
        // The position of the last watched title is predictable on returning to the home row.
        SmoothResumeToStart();
        return snapshot;
    }
    private void SmoothResumeToStart()
    { Views.SmoothScroll.Cancel(ResumeScroll); ResumeScroll.ScrollToHorizontalOffset(0); }
    /// <summary>Season thumbnails already asked for, by season: one request per new season, not per refresh.</summary>
    private readonly SeasonThumbs _seasonThumbs = new();
    private async Task RefreshMiniResumeAsync()
    {
        var generation = _playGeneration;
        await RememberCurrentPlaybackAsync();
        if (!_closing && _miniPlayer && generation == _playGeneration) RenderLibrary();
    }
}
