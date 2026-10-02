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
        ApplySeasonThumbs([snapshot]);
        _recentPlayback = ContinueWatching.History(_recentPlayback.Append(snapshot));
        _resume.RemoveAll(x => x.Id == item.Id);
        if (!snapshot.UserData.Played && snapshot.UserData.PlaybackPositionTicks > 0) _resume.Insert(0, snapshot);
        // The position of the last watched title is predictable on returning to the home row.
        SmoothResumeToStart();
        if (!_demo && _store is { } store) await Task.Run(() => store.RememberPlayback(snapshot));
    }
    private void SmoothResumeToStart()
    { Views.SmoothScroll.Cancel(ResumeScroll); ResumeScroll.ScrollToHorizontalOffset(0); }
    /// <summary>Season thumbnails already asked for, by season: one request per new season, not per refresh.</summary>
    private readonly Dictionary<string, string?> _seasonThumbs = [];
    /// <summary>Asks Jellyfin for the thumbnails of the seasons not seen yet; a failure only leaves those cards on their next choice.</summary>
    private async Task LoadSeasonThumbsAsync(JellyfinClient client, IEnumerable<MediaItem> items, CancellationToken ct)
    {
        var list = items.ToList();
        var missing = Artwork.Seasons(list).Where(x => !_seasonThumbs.ContainsKey(x)).ToList();
        if (missing.Count > 0)
        {
            try { foreach (var (season, tag) in await client.SeasonThumbsAsync(missing, ct)) _seasonThumbs[season] = tag; }
            catch (Exception ex) when (IsExpected(ex) && ex is not OperationCanceledException) { }
        }
        ApplySeasonThumbs(list);
    }
    /// <summary>On a worker thread, pass a copy of the known thumbnails: the interface thread may be adding to them.</summary>
    private void ApplySeasonThumbs(IEnumerable<MediaItem> items, IReadOnlyDictionary<string, string?>? known = null)
    {
        known ??= _seasonThumbs;
        foreach (var item in items)
            if (item.Type == "Episode" && item.SeasonId is { } season && known.TryGetValue(season, out var tag)) item.SeasonThumbImageTag = tag;
    }
    private async Task RefreshMiniResumeAsync()
    {
        var generation = _playGeneration;
        await RememberCurrentPlaybackAsync();
        if (!_closing && _miniPlayer && generation == _playGeneration) RenderLibrary();
    }
}
