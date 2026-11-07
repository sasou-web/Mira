using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// « Now Playing » and the media keys (MacNowPlaying), as the system media controls of Windows: the title and its poster,
// where it is, whether it plays; F8, the button of AirPods or a headset, Control Center's bar act here as the player's
// own keys do (Space, ← →, N), the group included.
public sealed partial class PlayerPage
{
    private string _nowTitle = "", _nowSubtitle = "";

    /// <summary>A title starts: its name at once (loading), its poster as soon as it is read.</summary>
    private void TellTitle(MediaItem shown, string title, string subtitle)
    {
        _nowTitle = title; _nowSubtitle = subtitle;
        TellNowPlaying();
        if (_shell.Session is not { } session) return;
        var generation = _generation;
        _ = Task.Run(async () =>
        {
            try
            {
                var poster = await session.Images.PosterAsync(shown, 512);
                if (poster is null) return;
                using var png = new MemoryStream();
                poster.Save(png);
                var bytes = png.ToArray(); var (width, height) = (poster.PixelSize.Width, poster.PixelSize.Height);
                Dispatcher.UIThread.Post(() => { if (generation == _generation && !_stopped) MacNowPlaying.SetArtwork(title, bytes, width, height); });
            }
            catch (Exception ex) when (Errors.Expected(ex) || ex is ObjectDisposedException) { }
        });
    }

    /// <summary>The playback as it is now, told when it changed: each tick, a start, a stop.</summary>
    private void TellNowPlaying()
    {
        if (_nowTitle.Length == 0 || !ReferenceEquals(_shell.Player, this)) return;
        var ready = _loaded && !_stopped && _mpv is not null && !FailureShown;
        MacNowPlaying.Show(new NowPlayingState(_nowTitle, _nowSubtitle, ready, !ready || _session.Paused, _skips.Next is not null,
            _session.Position, _session.Duration, ready ? _mpv!.Number("speed", 1) : 1));
    }

    /// <summary>A press of the Mac's media controls, as the player's own keys.</summary>
    public void Remote(MediaKey key, double position)
    {
        if (!NowPlaying.Enabled(key, MacNowPlaying.Clock.Told) || _mpv is not { } mpv || !_loaded || _stopped || FailureShown) return;
        // A key of the Mac's own: someone is there (« Tu regardes toujours ? »), headset or Control Center included.
        Touched();
        switch (key)
        {
            case MediaKey.Play or MediaKey.Pause or MediaKey.TogglePause:
                if (NowPlaying.Changes(key, mpv.Flag("pause"))) TogglePause();
                break;
            case MediaKey.Back: Jump(-NowPlaying.Skip); break;
            case MediaKey.Forward: Jump(NowPlaying.Skip); break;
            case MediaKey.Seek when double.IsFinite(position): SeekTo(position, exact: true); ShowControls(); break;
            case MediaKey.Next when _skips.Next is { } next: _ = PlayNextAsync(next); break;
        }
        TellNowPlaying();
    }
}
