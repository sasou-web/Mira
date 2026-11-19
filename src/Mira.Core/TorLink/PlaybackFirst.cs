namespace Mira.Core.TorLink;

/// <summary>
/// Watching first (ogsas, 2026-11-19: a film stuck loading while qBittorrent downloaded): on the PC of Jellyfin, while
/// something plays from it, its qBittorrent goes slower, the films and its downloads often sharing one drive (a USB disk
/// beside a laptop): every piece written is a seek away from the film Jellyfin reads, and every piece shared one more read.
/// qBittorrent's other speed limits, on while a session plays (paused ones not), off a minute after the last one stopped:
/// a seek or the next episode doesn't let it loose in between.
/// </summary>
public static class PlaybackFirst
{
    /// <summary>Received at 5 MB/s at most while someone watches (40 Mbit/s: a USB disk keeps up with that and a film).</summary>
    public const int Download = 5 * 1024 * 1024;
    /// <summary>Shared at 1 MB/s at most: reading for peers is the drive's time too, and a viewer away needs the uplink.</summary>
    public const int Upload = 1024 * 1024;
    /// <summary>How long downloads stay slow after the last playback stopped.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromMinutes(1);
    /// <summary>How often Mira reads Jellyfin's sessions while qBittorrent runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    /// <summary>Something plays from Jellyfin right now: any session's, this PC's own too, unless it is paused.</summary>
    public static bool Watching(IEnumerable<Viewer> viewers) => viewers.Any(x => !x.Paused);

    /// <summary>qBittorrent slowed: someone watches, or watched less than <see cref="Linger"/> ago.</summary>
    public static bool Slow(bool watching, DateTimeOffset? lastWatched, DateTimeOffset now) =>
        watching || lastWatched is { } last && now - last < Linger;
}
