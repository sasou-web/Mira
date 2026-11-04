using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

/// <summary>
/// The trackpad over the picture, as in Apple's TV app and IINA: two fingers sideways move in the film (the bar follows,
/// the film catches up as the fingers go), up and down change the volume (the wheel too, as on Windows), a pinch out goes
/// full screen and a pinch in comes back, then shrinks to the mini-player. The fingers' direction, natural scrolling or not.
/// </summary>
public sealed partial class PlayerPage
{
    private readonly TrackpadSwipe _scrub = new();
    /// <summary>The fingers rested: the film goes exactly where the bar is.</summary>
    private readonly DispatcherTimer _scrubDone = new() { Interval = TimeSpan.FromMilliseconds(Trackpad.Gap + 30) };
    private bool _scrubbing;
    private double _scrubFrom, _scrubTo;
    private long _scrubSought;

    private void ListenToTrackpad()
    {
        AddHandler(PointerWheelChangedEvent, ScrubWheel, RoutingStrategies.Bubble);
        _scrubDone.Tick += (_, _) => { _scrubDone.Stop(); if (!_scrubbing) return; _scrubbing = false; SeekTo(_scrubTo, exact: true); };
    }
    /// <summary>For the self-check: where a swipe has taken the film so far.</summary>
    public double ScrubTarget => _scrubTo;

    private void ScrubWheel(object? sender, PointerWheelEventArgs e)
    {
        // The speaker's own wheel and the menus keep theirs; the mini-player leaves the library its gestures.
        if (e.Handled || _mini || !_loaded) return;
        var (x, y) = Trackpad.Finger(e.Delta.X, e.Delta.Y, MacTrackpad.Natural);
        var time = (long)e.Timestamp;
        // A new gesture goes on from where the last one put the bar, even before the film got there.
        if (_scrub.Ended(time)) _scrubFrom = _scrubbing ? _scrubTo : _session.Position;
        _scrub.Add(x, y, time);
        e.Handled = true;
        if (_scrub.Axis == SwipeAxis.Horizontal)
        {
            _scrubbing = true;
            var end = _session.Duration > 0 ? Math.Max(0, _session.Duration - .5) : double.MaxValue;
            _scrubTo = Math.Clamp(_scrubFrom + _scrub.X * Trackpad.SecondsPerUnit, 0, end);
            // The bar at once; the film to the nearest keyframe a few times a second, exactly once the fingers rest.
            if (Environment.TickCount64 - _scrubSought > 120) { _scrubSought = Environment.TickCount64; SeekTo(_scrubTo); }
            else { _timeline.Hold(_scrubTo); ShowTime(_scrubTo); }
            ShowControls();
            _scrubDone.Stop(); _scrubDone.Start();
        }
        // Up and down, or a mouse's wheel (too little at a time to decide a gesture): the volume.
        else if (Math.Abs(y) > Math.Abs(x)) ChangeVolume(Math.Clamp(y * Trackpad.VolumePerUnit, -5, 5));
    }

    /// <summary>A pinch, once per gesture: out (1) full screen or the player back from the mini-player; in (-1) out of full screen, else smaller.</summary>
    public void Pinched(int way)
    {
        if (_mini) { if (way > 0) SetMini(false); return; }
        var full = _shell.WindowState == WindowState.FullScreen;
        if (way > 0) { if (!full) ToggleFullscreen(); }
        else if (full) ToggleFullscreen();
        else SetMini(true);
    }
}
