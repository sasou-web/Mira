using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using Mira.Desktop.Playback;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// The player going from one place to another. Reduced or expanded, the video's own window moves and resizes between the
/// two places, rounded on the way, and keeps playing: nothing to prepare, so the movement starts with the click, WPF has
/// nothing to draw while it runs, and mpv is never held up (a still of the picture, as the 0.9.2 moved, stops mpv's main
/// thread for as long as it takes: a blink at each end, and the sound starved). Entering and closing, a black sheet of
/// Windows' compositor fades over everything, the video included (<see cref="WindowShade"/>).
/// </summary>
public partial class MainWindow
{
    private const int MorphMs = 340, DarkenMs = 180, LiftMs = 320, DismissMs = 200, SpinnerAfterMs = 1200;
    private bool _morphing;
    private int _morphVersion;

    /// <summary>Reduces or expands the player as a person asks it: moving between the two places when it can.</summary>
    private async void SetMiniPlayerSmoothly(bool mini)
    {
        if (!_playing || _morphing || mini == _miniPlayer) return;
        // Out of full screen the window itself changes: it does so under the black sheet.
        if (_fullscreen && Shade is { } shade && !PlayerErrorShown && !_morphing)
        {
            _morphing = true; ClosePlayerPopups();
            await shade.DarkenAsync(DarkenMs);
            _morphing = false; SetMiniPlayer(mini);
            await RenderedAsync(2);
            await shade.LiftAsync(LiftMs);
            return;
        }
        if (Motion.Reduced || _fullscreen || _miniStowed || WindowState == WindowState.Minimized || PlayerErrorShown || !_loaded)
        {
            FrameProbe.Note($"{(mini ? "reduce" : "expand")} without movement: reduced motion {Motion.Reduced}, fullscreen {_fullscreen}, tucked away {_miniStowed}, error {PlayerErrorShown}, loaded {_loaded}");
            SetMiniPlayer(mini); return;
        }
        FrameProbe.Begin(mini ? "reduce" : "expand");
        var version = ++_morphVersion; _morphing = true;
        var host = _videoHost!; var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // First: their layered windows would otherwise be drawn again, whole, with the new layout.
        ClosePlayerPopups();
        var fromRadius = _miniPlayer ? MiniRadius * dpi : 0d; var from = host.BeginMove();
        using (FrameProbe.Time("SetMiniPlayer")) SetMiniPlayer(mini);
        // Expanding: the page stays under the growing video until the video covers it.
        if (!mini) LibraryShell.Visibility = NavigationRail.Visibility = TitleBar.Visibility = Visibility.Visible;
        PlayerShell.Opacity = 0; PlayerShell.IsHitTestVisible = false; MiniFrame.Opacity = 0;
        using (FrameProbe.Time("layout")) UpdateLayout();
        var to = host.LaidOut ?? from;
        // Growing, mpv draws at the final size at once: the picture stays sharp, scaled down by Windows. Shrinking, it
        // goes on at the size it has until the video is nearly there (MoveVideoAsync).
        if (to.Width > from.Width) host.DrawAt(to.Size);
        // The page's first drawing, heavy on a 4K screen, would hold back the first frames of the movement: it is drawn
        // now, the video unmoved over it, and the clock starts after it.
        await RenderedAsync(3);
        if (version != _morphVersion) return;
        FrameProbe.Mark("moving");
        await MoveVideoAsync(host, from, to, fromRadius, mini ? MiniRadius * dpi : 0, MorphMs);
        if (version != _morphVersion) return;
        if (_playing && !_miniPlayer) { LibraryShell.Visibility = NavigationRail.Visibility = TitleBar.Visibility = Visibility.Collapsed; LeavePagesForPlayer(); }
        EndMorph();
        // The controls' windows come back with the next tick, the video settled; their panels with the pointer.
        if (_playing && _miniPlayer) Motion.Fade(MiniFrame, 1, 140, 0);
    }
    /// <summary>
    /// Moves the video's own window from one place to the other, a step at each frame of the screen (WPF's own frames come
    /// at 60 to 100 a second, unevenly, when it has nothing to draw), timed by the clock rather than counted in frames: a
    /// late frame shows it further on, never a slower movement. WPF has nothing to draw meanwhile, and mpv goes on playing.
    /// </summary>
    /// <param name="leaving">Gathers speed instead of landing.</param>
    private Task MoveVideoAsync(VideoHost host, Rect from, Rect to, double fromRadius, double toRadius, int ms, bool leaving = false)
    {
        var done = new TaskCompletionSource();
        var version = _morphVersion; var clock = Stopwatch.StartNew(); var queued = 0; var steps = 0;
        var tell = !leaving && to.Width < from.Width;
        void Step()
        {
            Interlocked.Exchange(ref queued, 0);
            if (done.Task.IsCompleted) return;
            if (version != _morphVersion) { done.TrySetResult(); return; }
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            var k = leaving ? t * t : Glide(t);
            static double Mix(double a, double b, double k) => a + (b - a) * k;
            host.Move(new Rect(Mix(from.X, to.X, k), Mix(from.Y, to.Y, k), Mix(from.Width, to.Width, k), Mix(from.Height, to.Height, k)), Mix(fromRadius, toRadius, k));
            // Nearly there: mpv takes the small size now, in the movement, rather than as it lands.
            if (tell && k > .85) { tell = false; host.DrawAt(to.Size); }
            ++steps;
            if (t >= 1) { FrameProbe.Mark($"landed after {steps} steps"); done.TrySetResult(); }
        }
        Step();
        var pace = new Thread(() =>
        {
            while (!done.Task.IsCompleted)
            {
                var before = Stopwatch.GetTimestamp();
                VideoHost.AwaitScreen();
                // No composition to wait for (the screen off, a remote session): the clock alone.
                if (Stopwatch.GetElapsedTime(before).TotalMilliseconds < 1) Thread.Sleep(4);
                if (Interlocked.Exchange(ref queued, 1) == 0) Dispatcher.BeginInvoke(Step, System.Windows.Threading.DispatcherPriority.Send);
            }
        });
        pace.IsBackground = true; pace.Priority = ThreadPriority.AboveNormal; pace.Name = "Mira video movement"; pace.Start();
        // Whatever the screen does, the movement ends on time.
        _ = Task.Delay(ms + 120).ContinueWith(_ => Dispatcher.BeginInvoke(Step), TaskScheduler.Default);
        return done.Task;
    }
    /// <summary>
    /// A cubic Bézier (.4, 0, .2, 1): it gathers speed over the first frames and lands softly, without crawling: on the last
    /// pixels a window steps visibly. An ease-out from the first frame covered more than half the way in 80 ms: a jump.
    /// </summary>
    private static double Glide(double t)
    {
        static double Bezier(double a, double b, double s) => 3 * (1 - s) * (1 - s) * s * a + 3 * (1 - s) * s * s * b + s * s * s;
        double low = 0, high = 1;
        for (var i = 0; i < 14; i++) { var s = (low + high) / 2; if (Bezier(.4, .2, s) < t) low = s; else high = s; }
        return Bezier(0, 1, (low + high) / 2);
    }
    /// <summary>
    /// Closing the player. The whole player darkens to black, the page comes under the sheet with the progress just made,
    /// and the sheet lifts; the mini-player slides out of the window. mpv ends after the movement (its ending holds the
    /// interface's thread up while its window goes).
    /// </summary>
    private async Task<bool> ClosePlayerSmoothlyAsync()
    {
        var shade = _miniPlayer ? null : Shade;
        if (Motion.Reduced || _miniStowed || PlayerShell.Visibility != Visibility.Visible || PlayerErrorShown || _mpv is not { } mpv || !_loaded || _morphing || !_miniPlayer && shade is null)
        {
            FrameProbe.Note($"close without movement: reduced motion {Motion.Reduced}, tucked away {_miniStowed}, error {PlayerErrorShown}, loaded {_loaded}, moving {_morphing}, sheet {shade is not null}");
            return false;
        }
        var version = ++_morphVersion; _morphing = true;
        mpv.Set("pause", "yes"); mpv.Set("mute", "yes"); ClosePlayerPopups();
        var moved = new TaskCompletionSource(); _releaseWhen = moved.Task;
        try
        {
            await (shade?.DarkenAsync(DarkenMs) ?? DismissMiniAsync());
            if (version != _morphVersion) return true;
            await StopPlaybackAsync(); FrameProbe.Mark("stopped");
            // The page is there at once, under the sheet: no fade of its own.
            if (_returnToDetail is { } page && _detail?.Id == page.Id) { DetailOverlay.BeginAnimation(OpacityProperty, null); DetailOverlay.Opacity = 1; DetailOverlay.Visibility = Visibility.Visible; }
            using (FrameProbe.Time("ReturnToLibrary")) ReturnToLibrary(reveal: false);
            using (FrameProbe.Time("layout")) UpdateLayout();
            EndMorph();
            if (shade is not null) { await RenderedAsync(2); await shade.LiftAsync(LiftMs); }
            return true;
        }
        finally { _releaseWhen = Task.CompletedTask; moved.TrySetResult(); }
    }
    /// <summary>The mini-player leaves by the edge of the window it rests on.</summary>
    private Task DismissMiniAsync()
    {
        var host = _videoHost!; var scale = VisualTreeHelper.GetDpi(this).DpiScaleX; var from = host.BeginMove();
        PlayerShell.Opacity = 0; PlayerShell.IsHitTestVisible = false; Motion.Fade(MiniFrame, 0, DismissMs);
        var to = from; to.Y = _miniBottom ? Root.ActualHeight * scale + 16 : -from.Height - 16;
        return MoveVideoAsync(host, from, to, MiniRadius * scale, MiniRadius * scale, DismissMs, leaving: true);
    }
    /// <summary>After <paramref name="frames"/> frames drawn by WPF.</summary>
    private static Task RenderedAsync(int frames)
    {
        var done = new TaskCompletionSource(); var seen = 0;
        void Frame(object? sender, EventArgs e) { if (++seen < frames) return; CompositionTarget.Rendering -= Frame; done.TrySetResult(); }
        CompositionTarget.Rendering += Frame;
        return done.Task;
    }
    /// <summary>Puts everything back as the player shows it.</summary>
    private void EndMorph()
    {
        _morphing = false; _videoHost?.EndMove();
        MiniFrame.BeginAnimation(OpacityProperty, null); MiniFrame.Opacity = 1;
        PlayerShell.BeginAnimation(OpacityProperty, null); PlayerShell.Opacity = 1; PlayerShell.IsHitTestVisible = true;
    }
    /// <summary>
    /// Windows' black sheet over this window, made at first use; null when animations are off or Windows has none. The only
    /// thing that fades over the video.
    /// </summary>
    private WindowShade? Shade
    {
        get
        {
            if (Motion.Reduced) return null;
            if (!_shadeMade) { _shadeMade = true; _shade = WindowShade.TryCreate(new WindowInteropHelper(this).Handle); }
            return _shade;
        }
    }
    private WindowShade? _shade;
    private bool _shadeMade;
    /// <summary>mpv draws the first picture of this title unseen: it comes up from black (<see cref="RevealVideo"/>).</summary>
    private bool _videoWaits;
    /// <summary>A start that takes longer than usual: the sheet goes, black on black, and the spinner shows.</summary>
    private async Task LiftForSpinnerAsync(int generation)
    {
        await Task.Delay(SpinnerAfterMs);
        if (_videoWaits && generation == _playGeneration && Shade is { } shade) await shade.LiftAsync(140);
    }
    /// <summary>mpv's first picture is there: the video shows under the sheet, which lifts, and the controls with it.</summary>
    private async void RevealVideo()
    {
        if (!_videoWaits) return;
        _videoWaits = false;
        if (Shade is not { } shade) { _videoHost?.Conceal(false); ShowPlayerControls(); return; }
        await shade.CoverAsync();
        _videoHost?.Conceal(false);
        if (_playing) ShowPlayerControls();
        await shade.LiftAsync(LiftMs);
    }
}
