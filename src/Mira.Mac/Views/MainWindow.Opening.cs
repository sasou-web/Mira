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
/// Mira's opening, as on Windows (MainWindow.Opening.cs of Mira.Desktop; ogsas, 2026-11-14, mock-up A « Le m prend sa
/// place »): while Mira loads, the mark and « mira » on black, still. Once the Home page is drawn (the last one kept, or
/// Jellyfin's answer), « mira » fades, the black lifts, the mark glides from the middle to its place at the top of the
/// rail, then the page's tile drops from under it onto its button as a jelly (<see cref="OpeningGlide"/> of Core). The
/// page takes clicks from the first frame of it.
/// </summary>
/// <remarks>
/// Drawn at each frame of the screen (<see cref="TopLevel.RequestAnimationFrame"/>, by the clock): only transforms and
/// opacities change, and the tile is drawn by a control of its own under the rail's buttons (<see cref="RailDrop"/>), the
/// button's own white background held off meanwhile. Without motion (<see cref="MacMotion.Reduced"/>), or the rail hidden
/// (sign-in, a party), the black fades as the pages do. Jellyfin slow to answer without a Home page kept: the black goes
/// after <see cref="OpeningWait"/> anyway, the page showing it waits.
/// </remarks>
public partial class MainWindow
{
    /// <summary>The longest the black waits for the Home page.</summary>
    private static readonly TimeSpan OpeningWait = TimeSpan.FromSeconds(4);
    private readonly Grid _veil = new() { ZIndex = 70 };
    private readonly Border _veilShade = new() { Background = Brushes.Black };
    private readonly Icon _veilMark = new("brand", 56) { Foreground = Ui.Ink, RenderTransformOrigin = RelativePoint.TopLeft };
    private readonly TextBlock _veilWord = new() { Text = "mira", FontSize = 23, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly RailDrop _railDrop = new();
    private Icon? _railMark;
    private readonly TaskCompletionSource _homeDrawn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OpeningGlide? _opening;
    private Button? _openingButton;
    private readonly Stopwatch _openingClock = new();
    private TaskCompletionSource? _openingDone;
    private Point _openingFrom;
    /// <summary>The button's ink as the tile covers it: the rail's, then the lit button's (Button.rail of App.axaml).</summary>
    private readonly SolidColorBrush _openingInk = new();
    private static readonly Color Unlit = Color.Parse("#B6B6BD"), Lit = Color.Parse("#0B0B0B");
    private int _openingFrames;
    /// <summary>How the opening went, for the self-check: glided or faded, frames drawn.</summary>
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
        // Laid out and drawn once: the places measured are the real ones.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await OpenAsync();
    }

    /// <summary>The veil away, as the opening has it: glided to the rail, or faded. Over once it is gone.</summary>
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

    /// <summary>The rail in sight, its page's button lit and its mark there to land on.</summary>
    private Button? GlideTarget() => !MacMotion.Reduced && _veil.IsVisible && _rail.IsVisible && _railMark is { IsEffectivelyVisible: true } && _overlay.Children.Count == 0
        && LitRail is { } lit && _railButtons.TryGetValue(lit, out var button) && button.IsEffectivelyVisible ? button : null;

    /// <param name="frozen">For the self-check: the clock does not run, each moment is drawn by <see cref="DrawOpening"/>.</param>
    internal bool StartOpening(bool frozen)
    {
        if (GlideTarget() is not { } button || _railMark is not { } railMark) return false;
        Rect? Bounds(Visual visual) => visual.TransformToVisual(_veil) is { } matrix ? new Rect(visual.Bounds.Size).TransformToAABB(matrix) : null;
        if (Bounds(_veilMark) is not { Width: > 0 } mark || Bounds(railMark) is not { Width: > 0 } rail || Bounds(button) is not { Width: > 0 } place) return false;
        var radius = button.CornerRadius.TopLeft * place.Width / Math.Max(1, button.Bounds.Width);
        _opening = new OpeningGlide(new OpeningGlide.Spot(mark.Center.X, mark.Center.Y, mark.Width), new OpeningGlide.Spot(rail.Center.X, rail.Center.Y, rail.Width), place.Left, place.Top, place.Width, place.Height, radius);
        _openingFrom = mark.Center; _openingButton = button;
        // The page under it takes clicks at once.
        _veil.IsHitTestVisible = false;
        railMark.Opacity = 0;
        // Its own white background held off: the tile drawn under it takes its place.
        button.Background = Brushes.Transparent;
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
        if (_opening is not { } glide) return;
        _openingFrames++;
        // Another page chosen meanwhile (a click on the rail from the first frame): the opening ends where things are now.
        if (LitRail is not { } lit || !_railButtons.TryGetValue(lit, out var shown) || !ReferenceEquals(shown, _openingButton)) { FinishOpening(); return; }
        var t = _openingClock.Elapsed.TotalSeconds;
        DrawOpening(t);
        if (glide.Done(t)) FinishOpening();
        else RequestAnimationFrame(OpeningFrame);
    }

    /// <summary>The opening at <paramref name="t"/> seconds: « mira », the black, the mark on its way, the tile and the button's ink.</summary>
    internal void DrawOpening(double t)
    {
        if (_opening is not { } glide) return;
        _veilWord.Opacity = OpeningGlide.Word(t);
        _veilShade.Opacity = OpeningGlide.Shade(t);
        // Landed: the rail's own mark takes over (the veil's only made see-through: hidden, the word would move up under it).
        if (t >= OpeningGlide.Travel) { if (_railMark is { } railMark) railMark.Opacity = 1; _veilMark.Opacity = 0; }
        else
        {
            if (_railMark is { } railMark) railMark.Opacity = 0;
            _veilMark.Opacity = 1;
            var at = glide.MarkAt(t);
            // In the mark's own units (those of the veil): around its centre, stretched along its way, shrunk, then moved.
            var size = _veilMark.Bounds.Size; var cx = size.Width / 2; var cy = size.Height / 2;
            var scale = at.Size / Math.Max(1, size.Width); var stretch = 1 + at.Stretch;
            var matrix = Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(-at.Angle) * Matrix.CreateScale(stretch, 1 / Math.Sqrt(stretch))
                * Matrix.CreateRotation(at.Angle) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(cx + at.X - _openingFrom.X, cy + at.Y - _openingFrom.Y);
            _veilMark.RenderTransform = new MatrixTransform(matrix);
        }
        var drop = glide.DropAt(t);
        if (drop.Shown && _veil.TransformToVisual(_railDrop) is { } into)
        {
            var area = new Rect(drop.Left, drop.Top, drop.Width, drop.Height).TransformToAABB(into);
            _railDrop.Show(area, drop.Radius * area.Width / Math.Max(1e-6, drop.Width));
        }
        else _railDrop.Show(null, 0);
        if (_openingButton is { } button)
        {
            _openingInk.Color = Mix(Unlit, Lit, glide.Ink(t));
            if (!ReferenceEquals(button.Foreground, _openingInk)) button.Foreground = _openingInk;
        }
    }

    /// <summary>All in place: the rail's mark, the button lit by its own style, the veil gone.</summary>
    internal void FinishOpening()
    {
        if (_opening is null) return;
        OpeningReport = $"glided: {_openingClock.Elapsed.TotalMilliseconds:0} ms, {_openingFrames} frames";
        _openingClock.Stop();
        _opening = null;
        _railDrop.Show(null, 0);
        if (_railMark is { } railMark) railMark.Opacity = 1;
        if (_openingButton is { } button) { button.ClearValue(Button.BackgroundProperty); button.ClearValue(Button.ForegroundProperty); }
        _openingButton = null;
        _veil.IsVisible = false;
        _veilMark.RenderTransform = null; _veilMark.Opacity = 1; _veilWord.Opacity = _veilShade.Opacity = 1;
        _openingDone?.TrySetResult();
    }

    /// <summary>For the self-check: the veil back over the page, as at the start, to draw the opening again.</summary>
    internal void ShowVeilForCheck() { _veil.IsVisible = true; _veil.IsHitTestVisible = true; _veil.Opacity = 1; }
    internal bool VeilShown => _veil.IsVisible;
    internal bool VeilTakesClicks => _veil.IsHitTestVisible;
    /// <summary>For the self-check: the mark on its way (its render transform included) and the rail's, the tile drawn and the button it lands on, in the window's units.</summary>
    internal (Rect Mark, Rect Rail, Rect? Drop, Rect Button, bool RailMarkShown) OpeningPlaces()
    {
        Rect Bounds(Visual visual) => visual.TransformToVisual(_veil) is { } matrix ? new Rect(visual.Bounds.Size).TransformToAABB(matrix) : default;
        var button = _openingButton ?? (LitRail is { } lit ? _railButtons.GetValueOrDefault(lit) : null);
        Rect? drop = _railDrop.Area is { } area && _railDrop.TransformToVisual(_veil) is { } outOf ? area.TransformToAABB(outOf) : null;
        return (Bounds(_veilMark), _railMark is null ? default : Bounds(_railMark), drop, button is null ? default : Bounds(button), _railMark?.Opacity >= 1);
    }

    private static Color Mix(Color from, Color to, double share) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * share), (byte)Math.Round(from.R + (to.R - from.R) * share),
        (byte)Math.Round(from.G + (to.G - from.G) * share), (byte)Math.Round(from.B + (to.B - from.B) * share));
}

/// <summary>The tile dropping onto its button as Mira opens: drawn by the rail under its buttons, nothing laid out again.</summary>
internal sealed class RailDrop : Control
{
    private static readonly IBrush Fill = Ui.Brush("#F5F5F7");
    private double _radius;
    public Rect? Area { get; private set; }
    public RailDrop() { IsHitTestVisible = false; }
    public void Show(Rect? area, double radius)
    {
        if (area == Area && radius == _radius) return;
        Area = area; _radius = radius; InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        if (Area is { } area) context.DrawRectangle(Fill, null, new RoundedRect(area, Math.Min(_radius, Math.Min(area.Width, area.Height) / 2)));
    }
}
