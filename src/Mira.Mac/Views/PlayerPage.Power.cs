using System.Diagnostics;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// The Mac sleeping during playback (a lid closed in the middle of a film), as on Windows (MainWindow.Power.cs): before
/// sleeping, playback pauses where it is; on waking, a stream from Jellyfin is opened again at that position, still
/// paused, once Jellyfin answers (its connection rarely survives sleep: mpv would wait for data forever). What mpv says of
/// the old stream meanwhile (an end, an error) is the connection, never the end of the title. macOS says it
/// (<see cref="Services.MacPower"/>); the player's tick sees it too, when the wall clock went on while the Mac's own
/// clock, which stops while it sleeps, did not.
/// </summary>
public sealed partial class PlayerPage
{
    /// <summary>From macOS's notice before sleeping (or the clocks apart) until the waking is handled.</summary>
    private bool _asleep;
    /// <summary>Woken: Jellyfin waited for, before its stream is opened again.</summary>
    private bool _waking;
    private DateTime _asleepSince, _lastTick = DateTime.UtcNow;
    private long _lastAwake = Stopwatch.GetTimestamp();
    /// <summary>Slept this long at least: the stream is opened again.</summary>
    private static readonly TimeSpan SleepGap = TimeSpan.FromSeconds(15);
    /// <summary>How long Jellyfin is waited for after waking (the network comes back a few seconds after the Mac).</summary>
    private static readonly TimeSpan WakeWait = TimeSpan.FromSeconds(60);

    /// <summary>A playback starting: the Mac is awake, no earlier sleep is left to handle.</summary>
    private void Awake() { _asleep = false; _waking = false; _lastTick = DateTime.UtcNow; _lastAwake = Stopwatch.GetTimestamp(); }

    /// <summary>macOS is about to sleep: paused where it is, Jellyfin told, at once (macOS does not wait long).</summary>
    public void Suspending()
    {
        if (_stopped) return;
        _asleep = true; _asleepSince = DateTime.UtcNow;
        PauseForSleep();
    }
    /// <summary>macOS woke up.</summary>
    public void Woke() => _ = WokenAsync();

    private void PauseForSleep()
    {
        if (!_loaded || _mpv is not { } mpv) return;
        _session.Position = Reported(mpv.Number("time-pos", _session.Position));
        if (mpv.Flag("pause")) return;
        mpv.Set("pause", "yes"); _session.Paused = true; _pauseIcon.Kind = "play"; ShowControls();
        if (_session.Reported && _shell.Session is { } session) _ = session.Sync.RecordAsync("progress", Report());
    }

    /// <summary>
    /// Each tick, before mpv is asked anything: the wall clock went on more than the Mac's own since the last tick, so the
    /// Mac slept, said or not (a timer only late, App Nap, moves both clocks); and once awake again, the waking is handled
    /// even if macOS's notice never comes.
    /// </summary>
    private void NoticeSleep()
    {
        var now = DateTime.UtcNow; var awake = Stopwatch.GetTimestamp();
        var slept = now - _lastTick - Stopwatch.GetElapsedTime(_lastAwake, awake) > SleepGap;
        if (slept && !_asleep && !_waking) { _asleep = true; _asleepSince = _lastTick; }
        _lastTick = now; _lastAwake = awake;
        if (_asleep && now - _asleepSince > SleepGap) _ = WokenAsync();
    }

    /// <summary>
    /// A stream from Jellyfin is opened again at its position, paused, once Jellyfin answers (a minute at most); a file of
    /// this Mac (a copy kept) stays as it was. Opened again, one that cannot load is a cut stream (PlayerPage.Stream.cs):
    /// opened once more, or said.
    /// </summary>
    private async Task WokenAsync()
    {
        if (!_asleep) return;
        _asleep = false;
        _lastTick = DateTime.UtcNow; _lastAwake = Stopwatch.GetTimestamp();
        // A cut already handled waits for Jellyfin and opens it again itself.
        if (_stopped || _recovering || _mpv is null || !Streaming) return;
        var generation = _generation;
        // Woken without macOS's notice before (the clocks only): paused now, as it would have been.
        PauseForSleep();
        _waking = true; _spinner.IsVisible = true;
        var deadline = DateTime.UtcNow + WakeWait;
        while (_shell.Session is { } session && DateTime.UtcNow < deadline)
        {
            if (await AnswersAsync(session.Client)) break;
            if (generation != _generation || _stopped) return;
            await Task.Delay(2000);
        }
        if (generation != _generation || _stopped || _mpv is null) return;
        try
        {
            if (await ReopenStreamAsync(always: true, paused: true)) _recoveryLoad = true;
            else if (generation == _generation) { _waking = false; EndReopenLoading(); }
        }
        catch (Exception ex) when (Errors.Expected(ex))
        {
            if (generation != _generation || _stopped) return;
            _waking = false;
            await StopAsync();
            await _shell.CloseOverlayPlayerAsync(this);
            _shell.Notice(L("La lecture n’a pas pu reprendre après la mise en veille. Relance-la depuis « Continuer à regarder ».", "Playback couldn’t resume after sleep. Start it again from “Continue watching”."));
        }
    }

    /// <summary>For the self-check: macOS's notices, in turn.</summary>
    public void SleepForCheck() => Suspending();
    public void WakeForCheck() => Woke();
    /// <summary>For the self-check: the Mac slept <paramref name="seconds"/> without a word from macOS (its clock alone says so).</summary>
    public void ClockJumpForCheck(double seconds) => _lastTick -= TimeSpan.FromSeconds(seconds);
}
