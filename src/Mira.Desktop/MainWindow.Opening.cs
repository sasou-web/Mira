using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Mira's opening (ogsas, 2026-11-14, mock-up E « Le clin d'œil »): while Mira loads, the mark and « mira » on black,
/// still. Once the Home page is ready, where it is, « mira » fades and the mark shuts like an eyelid down to a line of
/// light that goes out, while the black lifts (<see cref="OpeningBlink"/> of Core, shared with the Mac). The rail is
/// left alone: its mark and the page's tile are already in place under the black. The page takes clicks from the first
/// frame of it: the veil no longer does.
/// </summary>
/// <remarks>
/// Drawn at each frame of the screen (<see cref="CompositionTarget.Rendering"/>, by the clock, not by frames counted):
/// only a scale and opacities change, nothing is laid out again. Without motion, in high contrast, or with the rail
/// hidden under another screen (sign-in, welcome, a party), the veil fades as it always did.
/// </remarks>
public partial class MainWindow
{
    private bool _opening;
    private readonly Stopwatch _openingClock = new();
    private readonly ScaleTransform _openingLid = new();
    private TaskCompletionSource? _openingDone;
    /// <summary>How the opening went, for the checks: blinked or faded, frames drawn.</summary>
    private string _openingReport = "not yet";
    private int _openingFrames;
    private bool _openingShut;
    private TimeSpan _openingLastFrame;

    /// <summary>The veil away, as the opening has it: the mark winks, or the veil fades. Over once it is gone.</summary>
    private Task OpenAsync()
    {
        if (!StartOpening(frozen: false)) { _openingReport = Motion.Reduced ? "at once (reduced motion)" : "faded"; return Motion.HideAsync(StartupVeil, 220); }
        return _openingDone!.Task;
    }

    /// <summary>The Home page with its rail in sight, nothing else over it.</summary>
    private bool CanBlink() => !Motion.Reduced && !HighContrast && StartupVeil.IsVisible && NavigationRail.IsVisible
        && LoginOverlay.Visibility != Visibility.Visible && WelcomeOverlay.Visibility != Visibility.Visible && PartyOverlay.Visibility != Visibility.Visible;

    /// <param name="frozen">For the checks: the clock does not run, each moment is drawn by <see cref="DrawOpening"/>.</param>
    private bool StartOpening(bool frozen)
    {
        if (!CanBlink()) return false;
        _opening = true;
        _openingDone = new TaskCompletionSource();
        // Around the mark's centre, whatever its size.
        StartupMark.RenderTransformOrigin = new Point(.5, .5); StartupMark.RenderTransform = _openingLid;
        // The page under it takes clicks at once; the veil's black is no longer a page that hides the library.
        StartupVeil.IsHitTestVisible = false; UpdateLibraryCover();
        _openingFrames = 0; _openingShut = false; _openingLastFrame = TimeSpan.Zero;
        DrawOpening(0);
        if (frozen) return true;
        FrameProbe.Begin("opening", 600);
        _openingClock.Restart();
        CompositionTarget.Rendering += OpeningFrame;
        return true;
    }

    private void OpeningFrame(object? sender, EventArgs e)
    {
        // Raised more than once for one frame: one drawing per rendering time.
        if (e is RenderingEventArgs args) { if (args.RenderingTime == _openingLastFrame) return; _openingLastFrame = args.RenderingTime; }
        _openingFrames++;
        var t = _openingClock.Elapsed.TotalSeconds;
        DrawOpening(t);
        if (OpeningBlink.Done(t)) FinishOpening();
    }

    /// <summary>The opening at <paramref name="t"/> seconds: « mira », the mark shutting then going out, the black.</summary>
    private void DrawOpening(double t)
    {
        if (!_opening) return;
        StartupWord.Opacity = OpeningBlink.Word(t);
        StartupShade.Opacity = OpeningBlink.Shade(t);
        var (across, down) = OpeningBlink.Scale(t);
        _openingLid.ScaleX = across; _openingLid.ScaleY = down;
        StartupMark.Opacity = OpeningBlink.Mark(t);
        if (t >= OpeningBlink.Shut && !_openingShut) { _openingShut = true; FrameProbe.Mark("shut"); }
    }

    /// <summary>The veil gone, its mark and word put back as they were for the next time.</summary>
    private void FinishOpening()
    {
        if (!_opening) return;
        CompositionTarget.Rendering -= OpeningFrame;
        _openingReport = $"blinked: {_openingClock.Elapsed.TotalMilliseconds:0} ms, {_openingFrames} frames";
        _openingClock.Stop();
        _opening = false;
        StartupVeil.Visibility = Visibility.Collapsed;
        StartupMark.RenderTransform = Transform.Identity; StartupMark.Opacity = StartupWord.Opacity = StartupShade.Opacity = 1;
        _openingDone?.TrySetResult();
    }
}
