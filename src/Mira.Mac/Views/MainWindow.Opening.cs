using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;
using static Mira.Core.Lang;

namespace Mira.Mac.Views;

/// <summary>
/// Mira's opening, as on Windows (MainWindow.Opening.cs of Mira.Desktop; ogsas, 2026-11-14, mock-up E « Le clin d'œil »):
/// while Mira loads, the mark and « mira » on black, still. Once the Home page is drawn (the last one kept, or Jellyfin's
/// answer), where it stands, « mira » fades and the mark shuts like an eyelid down to a line of light that goes out,
/// while the black lifts (<see cref="OpeningBlink"/> of Core). The rail is left alone: its mark and the page's button are
/// already in place under the black. The page takes clicks from the first frame of it.
/// </summary>
/// <remarks>
/// Drawn at each frame of the screen (<see cref="TopLevel.RequestAnimationFrame"/>, by the clock): only a scale and
/// opacities change. Without motion (<see cref="MacMotion.Reduced"/>), or the rail hidden (sign-in, a party), the black
/// fades as the pages do. Jellyfin slow to answer without a Home page kept: the black goes after
/// <see cref="OpeningWait"/> anyway, the page showing it waits.
/// </remarks>
public partial class MainWindow
{
    /// <summary>The longest the black waits for the Home page.</summary>
    private static readonly TimeSpan OpeningWait = TimeSpan.FromSeconds(4);
    private readonly Grid _veil = new() { ZIndex = 70 };
    private readonly Border _veilShade = new() { Background = Brushes.Black };
    private readonly ScaleTransform _veilLid = new();
    private readonly Icon _veilMark = new("brand", 56) { Foreground = Ui.Ink, RenderTransformOrigin = RelativePoint.Center };
    private readonly TextBlock _veilWord = new() { Text = "mira", FontSize = 23, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private Icon? _railMark;
    private readonly TaskCompletionSource _homeDrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _opening;
    private readonly Stopwatch _openingClock = new();
    private TaskCompletionSource? _openingDone;
    private int _openingFrames;
    /// <summary>How the opening went, for the self-check: blinked or faded, frames drawn.</summary>
    internal string OpeningReport { get; private set; } = "not yet";

    /// <summary>The veil over the whole window, its mark and « mira » in the middle; put in place once, at the start.</summary>
    private void BuildVeil()
    {
        Avalonia.Automation.AutomationProperties.SetName(_veil, L("Ouverture de Mira", "Opening Mira"));
        var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { _veilMark, _veilWord } };
        _veilMark.HorizontalAlignment = HorizontalAlignment.Center;
        _veil.Children.Add(_veilShade); _veil.Children.Add(middle);
        Grid.SetColumnSpan(_veil, 2); _root.Children.Add(_veil);
    }

    /// <summary>The Home page drawn for the first time (HomePage): the opening may start.</summary>
    internal void HomeDrawn() => _homeDrawn.TrySetResult();
    /// <summary>The veil gone at once, without its opening (the checks, which open no account of their own at the start).</summary>
    private void DropVeil() { _veil.IsVisible = false; _veil.IsHitTestVisible = false; OpeningReport = "dropped"; }

    /// <summary>Waits for the Home page (at most <see cref="OpeningWait"/>), then the opening.</summary>
    private async Task OpenWhenReadyAsync(Task started)
    {
        await Task.WhenAny(started, _homeDrawn.Task, Task.Delay(OpeningWait));
        // Laid out and drawn once before the first frame of the opening.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await OpenAsync();
    }

    /// <summary>The veil away, as the opening has it: the mark winks, or the veil fades. Over once it is gone.</summary>
    internal Task OpenAsync()
    {
        if (!_veil.IsVisible) return Task.CompletedTask;
        if (StartOpening(frozen: false)) return _openingDone!.Task;
        OpeningReport = MacMotion.Reduced ? "at once (reduced motion)" : "faded";
        return FadeVeilAsync();
    }
    private async Task FadeVeilAsync()
    {
        _veil.IsHitTestVisible = false;
        if (MacMotion.Reduced) { _veil.IsVisible = false; return; }
        _veil.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new Avalonia.Animation.Easings.CubicEaseIn() }];
        _veil.Opacity = 0;
        await Task.Delay(240);
        _veil.IsVisible = false; _veil.Transitions = null; _veil.Opacity = 1;
    }

    /// <summary>The Home page with its rail in sight, nothing over it.</summary>
    private bool CanBlink() => !MacMotion.Reduced && _veil.IsVisible && _rail.IsVisible && _overlay.Children.Count == 0;

    /// <param name="frozen">For the self-check: the clock does not run, each moment is drawn by <see cref="DrawOpening"/>.</param>
    internal bool StartOpening(bool frozen)
    {
        if (!CanBlink()) return false;
        _opening = true;
        // The page under it takes clicks at once.
        _veil.IsHitTestVisible = false;
        _veilMark.RenderTransform = _veilLid;
        _openingDone = new TaskCompletionSource();
        _openingFrames = 0;
        DrawOpening(0);
        if (frozen) return true;
        _openingClock.Restart();
        RequestAnimationFrame(OpeningFrame);
        return true;
    }

    private void OpeningFrame(TimeSpan _)
    {
        if (!_opening) return;
        _openingFrames++;
        var t = _openingClock.Elapsed.TotalSeconds;
        DrawOpening(t);
        if (OpeningBlink.Done(t)) FinishOpening();
        else RequestAnimationFrame(OpeningFrame);
    }

    /// <summary>The opening at <paramref name="t"/> seconds: « mira », the mark shutting then going out, the black.</summary>
    internal void DrawOpening(double t)
    {
        if (!_opening) return;
        _veilWord.Opacity = OpeningBlink.Word(t);
        _veilShade.Opacity = OpeningBlink.Shade(t);
        var (across, down) = OpeningBlink.Scale(t);
        _veilLid.ScaleX = across; _veilLid.ScaleY = down;
        _veilMark.Opacity = OpeningBlink.Mark(t);
    }

    /// <summary>The veil gone, its mark and word put back as they were for the next time.</summary>
    internal void FinishOpening()
    {
        if (!_opening) return;
        OpeningReport = $"blinked: {_openingClock.Elapsed.TotalMilliseconds:0} ms, {_openingFrames} frames";
        _openingClock.Stop();
        _opening = false;
        _veil.IsVisible = false;
        _veilMark.RenderTransform = null; _veilMark.Opacity = 1; _veilWord.Opacity = _veilShade.Opacity = 1;
        _openingDone?.TrySetResult();
    }

    /// <summary>For the self-check: the veil back over the page, as at the start, to draw the opening again.</summary>
    internal void ShowVeilForCheck() { _veil.IsVisible = true; _veil.IsHitTestVisible = true; _veil.Opacity = 1; }
    internal bool VeilShown => _veil.IsVisible;
    internal bool VeilTakesClicks => _veil.IsHitTestVisible;
    /// <summary>For the self-check: the veil's mark (its render transform included), its opacity and the black's, and whether the rail's mark is shown, in the window's units.</summary>
    internal (Rect Mark, double MarkOpacity, double Shade, bool RailMarkShown) OpeningPlaces()
    {
        Rect Bounds(Visual visual) => visual.TransformToVisual(_veil) is { } matrix ? new Rect(visual.Bounds.Size).TransformToAABB(matrix) : default;
        return (Bounds(_veilMark), _veilMark.Opacity, _veilShade.Opacity, _railMark is { IsEffectivelyVisible: true, Opacity: >= 1 });
    }
}
