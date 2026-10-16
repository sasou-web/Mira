using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop;

/// <summary>
/// Windows shutting down (or Setup closing Mira to replace its files), and the PC sleeping during playback: before
/// sleeping, playback pauses where it is; on waking, a stream from Jellyfin is reopened at that position, still paused,
/// once Jellyfin answers again. What mpv said of the stream meanwhile (an end, an error) is a broken connection, never
/// the end of the title: it is not counted as watched.
/// </summary>
public partial class MainWindow
{
    private enum WakeState { None, Waiting, Reloading }
    private WakeState _wake;
    /// <summary>From Windows' notice before sleeping (or a long pause of the player's tick) until the waking is handled.</summary>
    private bool _asleep;
    private DateTime _asleepSince, _lastTick = DateTime.UtcNow;
    /// <summary>A file was given to mpv and its END_FILE is still to come (loaded or not): reopening the stream skips that end.</summary>
    private bool _fileOpen;
    private int _skipEnds;
    private string? _playLocation, _playAuthorization;
    private (string? Audio, string? Subtitle)? _reloadTracks;
    /// <summary>The player's 100 ms tick stopped this long: the PC slept, even if Windows did not say so.</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(15);
    /// <summary>How long Jellyfin is waited for after waking (the network comes back a few seconds after the PC).</summary>
    private static readonly TimeSpan WakeWait = TimeSpan.FromSeconds(60);

    private void InitializePower()
    {
        Application.Current.SessionEnding += SessionEnding;
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    /// <summary>
    /// What closing must not lose, done at once and synchronously, before anything awaited: the window's place, the
    /// settings page's changes, the volume, and where the title playing was (in the local « Continuer à regarder »).
    /// </summary>
    private void KeepForNextStart()
    {
        Attempt(RememberWindow);
        // The settings page says changes are kept on leaving it: closing Mira leaves it too.
        Attempt(AutoSaveSettings);
        Attempt(() =>
        {
            if (_mpv is null) return;
            _settings.Volume = _mpv.Level;
            if (!_playing || !_loaded || _session.Item is not { } item) return;
            _session.Position = ReportedPosition(_mpv.Number("time-pos", _session.Position));
            // As StopPlaybackAsync decides it: past the threshold, the title is watched.
            if (_session.Duration > 0 && _session.Position >= _session.Duration * LibraryStore.WatchedThreshold) { item.UserData.Played = true; item.UserData.PlaybackPositionTicks = 0; }
            else item.UserData.PlaybackPositionTicks = TimeSpan.FromSeconds(Math.Max(0, _session.Position)).Ticks;
        });
        Attempt(() => _profile.SaveSettings(_settings));
        Attempt(() => RememberCurrentPlaybackNow(TimeSpan.FromSeconds(1.5)));
    }

    /// <summary>
    /// Windows shuts down or signs out, or the Restart Manager (Setup's CloseApplications) closes Mira: WPF then closes
    /// the window without waiting for anything asynchronous, and the process may end as soon as this returns. What must
    /// survive is written here, in about 3 s at most; the closing that follows does the rest if it has time.
    /// </summary>
    private void SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        KeepForNextStart();
        Attempt(() =>
        {
            if (!_session.Reported || _sync is null || _session.Item is null) return;
            // Queued on this PC, sent to Jellyfin now or by the next Mira; not a second time by the closing that follows.
            _session.Reported = false;
            var completed = _loaded && _session.Duration > 0 && _session.Position >= _session.Duration * LibraryStore.WatchedThreshold;
            _sync.RecordAsync(completed ? "complete" : "stop", Report()).Wait(TimeSpan.FromSeconds(1.5));
        });
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        // Mira can only act before sleeping while this runs: the pause and the position are taken synchronously.
        try
        {
            if (e.Mode == PowerModes.Suspend) Dispatcher.Invoke(Suspending, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
            else if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() => _ = WokenAsync());
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
    }
    private void Suspending()
    {
        if (_closing) return;
        _asleep = true; _asleepSince = DateTime.UtcNow;
        if (!_playing || !_loaded || _mpv is not { } mpv) return;
        // Logged, never thrown back at Windows' notice.
        Attempt(() =>
        {
            _session.Position = ReportedPosition(mpv.Number("time-pos", _session.Position));
            mpv.Set("pause", "yes"); _session.Paused = true; ShowPauseState(true); UpdateWindowsPlayback();
            if (!_demo && _sync is not null && _session.Reported) _ = _sync.RecordAsync("progress", Report());
        });
    }
    /// <summary>
    /// Called by the player's tick before it polls mpv: a tick long overdue means the PC slept, perhaps without Windows'
    /// notice; and once the PC is awake again, the waking is handled even if Windows' notice never comes.
    /// </summary>
    private void NoticeSleep()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTick > SleepGap && !_asleep && _wake == WakeState.None) { _asleep = true; _asleepSince = _lastTick; }
        _lastTick = now;
        if (_asleep && now - _asleepSince > SleepGap) _ = WokenAsync();
    }
    /// <summary>
    /// A stream from Jellyfin is reopened at its position, paused, once Jellyfin answers (a minute at most): its connection
    /// rarely survives sleep. A file on this PC plays on as it was.
    /// </summary>
    private async Task WokenAsync()
    {
        if (!_asleep) return;
        _asleep = false;
        if (_closing || !_playing || _mpv is null || !IsStream(_playLocation)) return;
        var generation = _playGeneration; var client = _client;
        _wake = WakeState.Waiting; SetPlayerLoading(true);
        var deadline = DateTime.UtcNow + WakeWait;
        while (client is not null && DateTime.UtcNow < deadline)
        {
            try { if (await client.PingAsync()) break; }
            // HttpClient's own time limit: Jellyfin not reachable yet. Disposed: another account, playback stopped.
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
            if (generation != _playGeneration || !_playing) return;
            await Task.Delay(2000);
        }
        if (generation != _playGeneration || !_playing || _mpv is null || _closing) return;
        try
        {
            // The tracks chosen are chosen again once it is open (FileLoaded).
            _reloadTracks = (_mpv.Get("aid"), _mpv.Get("sid"));
            if (_fileOpen) _skipEnds++;
            _fileOpen = true; _loaded = false; SeekBar.IsEnabled = false; _wake = WakeState.Reloading;
            _mpv.Load(_playLocation!, _session.Position, _playAuthorization);
            _mpv.Set("pause", "yes");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            await StopPlaybackAsync(); ReturnToLibrary();
            SetNotice(L("La lecture n’a pas pu reprendre après la mise en veille. Relance-la depuis « Continuer à regarder ».", "Playback couldn’t resume after sleep. Start it again from “Continue watching”."));
        }
    }
    private static bool IsStream(string? location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    /// <summary>The audio and subtitles chosen before sleeping, on the reopened stream (external subtitles are added in the same order).</summary>
    private void RestoreTracks()
    {
        if (_mpv is null || _reloadTracks is not var (audio, subtitle)) return;
        _reloadTracks = null;
        if (!string.IsNullOrEmpty(audio)) _mpv.Set("aid", audio);
        if (!string.IsNullOrEmpty(subtitle)) _mpv.Set("sid", subtitle);
    }
}
