using Mira.Core;

namespace Mira.Desktop;

public partial class MainWindow
{
    private List<MediaItem> _recentPlayback = [];
    private async Task RememberCurrentPlaybackAsync()
    {
        if (_playingItem is not { } item || !_loaded) return;
        var at = DateTimeOffset.UtcNow; item.UserData.LastPlayedDate = at;
        var snapshot = item with { UserData = item.UserData with { } };
        _recentPlayback = ContinueWatching.History(_recentPlayback.Append(snapshot));
        _resume.RemoveAll(x => x.Id == item.Id);
        if (!snapshot.UserData.Played && snapshot.UserData.PlaybackPositionTicks > 0) _resume.Insert(0, snapshot);
        // The position of the last watched title is predictable on returning to the home row.
        SmoothResumeToStart();
        if (!_demo && _store is { } store) await Task.Run(() => store.RememberPlayback(snapshot));
    }
    private void SmoothResumeToStart()
    { Views.SmoothScroll.Cancel(ResumeScroll); ResumeScroll.ScrollToHorizontalOffset(0); }
    private async Task RefreshMiniResumeAsync()
    {
        var generation = _playGeneration;
        await RememberCurrentPlaybackAsync();
        if (!_closing && _miniPlayer && generation == _playGeneration) RenderLibrary();
    }
}
