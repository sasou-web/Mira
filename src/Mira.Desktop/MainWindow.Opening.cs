using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mira.Core;
using Mira.Desktop.Services;
using Mira.Desktop.Views;

namespace Mira.Desktop;

/// <summary>
/// Mira's opening (ogsas, 2026-11-14, mock-up A « Le m prend sa place »): while Mira loads, the mark and « mira » on black,
/// still. Once the Home page is ready, « mira » fades, the black lifts, the mark glides from the middle to its place at
/// the top of the rail, then the page's tile drops from under it onto its button as a jelly (<see cref="OpeningGlide"/>
/// of Core, shared with the Mac). The page takes clicks from the first frame of it: the veil no longer does.
/// </summary>
/// <remarks>
/// Drawn at each frame of the screen (<see cref="CompositionTarget.Rendering"/>, by the clock, not by frames counted):
/// only transforms and opacities change, and the tile is a drawing of its own (<see cref="Sketch"/>): nothing is laid out
/// again. Without motion, in high contrast (the tile is the button's own background there), or with the rail hidden under
/// another screen (sign-in, welcome, a party), the veil fades as it always did.
/// </remarks>
public partial class MainWindow
{
    private OpeningGlide? _opening;
    private Button? _openingButton;
    private readonly Stopwatch _openingClock = new();
    private SolidColorBrush? _openingInk;
    private TaskCompletionSource? _openingDone;
    /// <summary>The mark in the veil's units, at the start (its centre), to move it from.</summary>
    private Point _openingFrom;
    /// <summary>How the opening went, for the checks: glided or faded, frames drawn.</summary>
    private string _openingReport = "not yet";
    private int _openingFrames;
    private bool _openingLanded, _openingDropped;
    private TimeSpan _openingLastFrame;

    /// <summary>The veil away, as the opening has it: glided to the rail, or faded. Over once it is gone.</summary>
    private Task OpenAsync()
    {
        if (!StartOpening(frozen: false)) { _openingReport = Motion.Reduced ? "at once (reduced motion)" : "faded"; return Motion.HideAsync(StartupVeil, 220); }
        return _openingDone!.Task;
    }

    /// <summary>The rail in sight, its page's button and its mark there to land on.</summary>
    private bool CanGlide() => !Motion.Reduced && !HighContrast && StartupVeil.IsVisible && NavigationRail.IsVisible && RailMark.IsVisible
        && LoginOverlay.Visibility != Visibility.Visible && WelcomeOverlay.Visibility != Visibility.Visible && PartyOverlay.Visibility != Visibility.Visible
        && ShownRailButton() is not null;

    /// <param name="frozen">For the checks: the clock does not run, each moment is drawn by <see cref="DrawOpening"/>.</param>
    private bool StartOpening(bool frozen)
    {
        UpdateLayout();
        if (!CanGlide() || ShownRailButton() is not { } button) return false;
        Rect Bounds(FrameworkElement element) => element.TransformToVisual(StartupVeil).TransformBounds(new Rect(element.RenderSize));
        var mark = Bounds(StartupMark); var rail = Bounds(RailMark); var place = Bounds(button);
        if (mark.IsEmpty || rail.IsEmpty || place.IsEmpty || mark.Width <= 0 || rail.Width <= 0) return false;
        // Its corners as the sliding tile has them, in the window's units.
        var radius = RailIndicator.CornerRadius.TopLeft * place.Width / Math.Max(1, button.ActualWidth);
        _opening = new OpeningGlide(new OpeningGlide.Spot(Mid(mark).X, Mid(mark).Y, mark.Width), new OpeningGlide.Spot(Mid(rail).X, Mid(rail).Y, rail.Width), place.Left, place.Top, place.Width, place.Height, radius);
        _openingFrom = Mid(mark); _openingButton = button;
        _openingInk = new SolidColorBrush(OpeningInk("TextTertiary"));
        _openingDone = new TaskCompletionSource();
        // The page under it takes clicks at once; the veil's black is no longer a page that hides the library.
        StartupVeil.IsHitTestVisible = false; UpdateLibraryCover();
        RailMark.Opacity = 0; RailIndicator.Opacity = 0;
        _openingFrames = 0; _openingLanded = _openingDropped = false; _openingLastFrame = TimeSpan.Zero;
        DrawOpening(0);
        if (frozen) return true;
        FrameProbe.Begin("opening", 1600);
        _openingClock.Restart();
        CompositionTarget.Rendering += OpeningFrame;
        SizeChanged += OpeningResized;
        return true;
        static Point Mid(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private void OpeningFrame(object? sender, EventArgs e)
    {
        // Raised more than once for one frame: one drawing per rendering time.
        if (e is RenderingEventArgs args) { if (args.RenderingTime == _openingLastFrame) return; _openingLastFrame = args.RenderingTime; }
        _openingFrames++;
        // Another page chosen meanwhile (a click on the rail from the first frame): the opening ends where things are now.
        if (ShownRailButton() != _openingButton) { FinishOpening(); return; }
        var t = _openingClock.Elapsed.TotalSeconds;
        DrawOpening(t);
        if (_opening!.Done(t)) FinishOpening();
    }
    private void OpeningResized(object sender, SizeChangedEventArgs e) => FinishOpening();

    /// <summary>The opening at <paramref name="t"/> seconds: « mira », the black, the mark on its way, the tile and the button's ink.</summary>
    private void DrawOpening(double t)
    {
        if (_opening is not { } glide) return;
        StartupWord.Opacity = OpeningGlide.Word(t);
        StartupShade.Opacity = OpeningGlide.Shade(t);
        var landed = t >= OpeningGlide.Travel;
        if (landed) { RailMark.Opacity = 1; StartupMark.Visibility = Visibility.Hidden; }
        else
        {
            var at = glide.MarkAt(t);
            // In the mark's own units (those of the veil): around its centre, stretched along its way, shrunk, then moved.
            var centre = new Point(StartupMark.ActualWidth / 2, StartupMark.ActualHeight / 2);
            var scale = at.Size / Math.Max(1, StartupMark.ActualWidth); var degrees = at.Angle * 180 / Math.PI;
            var matrix = Matrix.Identity;
            matrix.Translate(-centre.X, -centre.Y);
            matrix.Rotate(-degrees); matrix.Scale(1 + at.Stretch, 1 / Math.Sqrt(1 + at.Stretch)); matrix.Rotate(degrees);
            matrix.Scale(scale, scale);
            matrix.Translate(centre.X + at.X - _openingFrom.X, centre.Y + at.Y - _openingFrom.Y);
            if (StartupMark.RenderTransform is MatrixTransform { IsFrozen: false } move) move.Matrix = matrix;
            else StartupMark.RenderTransform = new MatrixTransform(matrix);
        }
        // The tile, from the veil's units to the rail's, and the button's ink as much as it covers it.
        var drop = glide.DropAt(t);
        using (var dc = RailDrop.Drawing.Open())
        {
            if (drop.Shown && Palette.Get("Text") is { } fill)
            {
                var into = StartupVeil.TransformToVisual(RailDrop);
                var area = into.TransformBounds(new Rect(drop.Left, drop.Top, drop.Width, drop.Height));
                var units = area.Width / Math.Max(1e-6, drop.Width);
                dc.DrawGeometry(fill, null, Squircle.Geometry(area, drop.Radius * units));
            }
        }
        RailIndicator.Opacity = 0;
        if (_openingButton is { } button && _openingInk is { } ink)
        {
            ink.Color = OpeningMix(OpeningInk("TextTertiary"), OpeningInk("OnAccent"), glide.Ink(t));
            if (!ReferenceEquals(button.Foreground, ink)) button.Foreground = ink;
        }
        if (t >= OpeningGlide.DropFrom && !_openingDropped) { _openingDropped = true; FrameProbe.Mark("tile"); }
        if (landed && !_openingLanded) { _openingLanded = true; FrameProbe.Mark("landed"); }
    }

    /// <summary>All in place: the rail's mark, the sliding tile under the page's button, the veil gone.</summary>
    private void FinishOpening()
    {
        if (_opening is null) return;
        CompositionTarget.Rendering -= OpeningFrame; SizeChanged -= OpeningResized;
        _openingReport = $"glided: {_openingClock.Elapsed.TotalMilliseconds:0} ms, {_openingFrames} frames";
        _openingClock.Stop();
        _opening = null; _openingButton = null; _openingInk = null;
        using (RailDrop.Drawing.Open()) { }
        RailMark.Opacity = 1;
        StartupVeil.Visibility = Visibility.Collapsed;
        StartupMark.RenderTransform = Transform.Identity; StartupMark.Visibility = Visibility.Visible; StartupWord.Opacity = StartupShade.Opacity = 1;
        UpdateNavigation();
        _openingDone?.TrySetResult();
    }

    private static Color OpeningInk(string key) => Palette.Get(key) is SolidColorBrush solid ? solid.Color : Colors.Gray;
    private static Color OpeningMix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}
