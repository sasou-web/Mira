using System.Net.Http;
using System.Windows.Threading;
using Mira.Core;
using Mira.Core.TorLink;

namespace Mira.Desktop;

/// <summary>
/// Watching first (<see cref="PlaybackFirst"/>): on the PC of Jellyfin, every 10 seconds while Mira's qBittorrent runs,
/// Jellyfin's sessions read; something playing turns on qBittorrent's slower limits, a minute without turns them off.
/// The profile is Mira's: the mode it finds is set to what Mira wants, even one left on by a Mira that quit.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _playbackFirstTimer = new() { Interval = PlaybackFirst.Every };
    private DateTimeOffset? _lastWatched;
    private bool _playbackFirstReading;

    private void StartPlaybackFirst()
    {
        _playbackFirstTimer.Tick += async (_, _) => await PlaybackFirstAsync();
        _playbackFirstTimer.Start();
    }

    private async Task PlaybackFirstAsync()
    {
        if (_playbackFirstReading || _demo || !JellyfinHere || OwnClient is not { } jellyfin || _qbHost?.Client is not { } qbittorrent) return;
        _playbackFirstReading = true;
        try
        {
            var ct = _connectionLifetime.Token;
            var now = DateTimeOffset.UtcNow;
            var watching = PlaybackFirst.Watching(await jellyfin.ViewersAsync(ct));
            if (watching) _lastWatched = now;
            await qbittorrent.SlowAsync(PlaybackFirst.Slow(watching, _lastWatched, now), ct);
        }
        // Jellyfin or qBittorrent not answering: tried again in 10 seconds (qBittorrent started again by the downloads' list).
        catch (Exception ex) when (IsExpected(ex) || ex is HttpRequestException or QBittorrentException) { }
        finally { _playbackFirstReading = false; }
    }
}
