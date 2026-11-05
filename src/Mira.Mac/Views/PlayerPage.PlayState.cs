using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Mira.Core;
using Mira.Mac.Services;

namespace Mira.Mac.Views;

// The pause and play flash in the middle of the picture, as on Windows.
public sealed partial class PlayerPage
{
    private readonly Icon _flashIcon = new("pause", PlayStateBadge.Glyph) { Foreground = Brushes.White };
    private readonly ScaleTransform _flashScale = new(1, 1);
    private Panel? _flash;
    private DispatcherTimer? _flashClock;
    private DateTimeOffset _flashStart;
    private double _flashFrom;
    private bool _flashStill;

    /// <summary>
    /// The state just asked for (click on the picture, the button, Space or K): the icon alone over its shadows, there
    /// at once, gone in 0.62 s while it opens up (<see cref="PlayStateBadge"/>, the same curve as Windows' keyframes).
    /// Never in the mini-player nor over the spinner; with « Reduce motion », shown still.
    /// </summary>
    private void ShowPlayState(bool paused)
    {
        if (_mini || _spinner.IsVisible) return;
        HideSeekPill();
        if (_flash is null)
        {
            // The close shadow on the icon, the wide one around both, as on Windows.
            _flashIcon.Effect = Shadow(PlayStateBadge.Edge);
            _flash = new Panel
            {
                Width = PlayStateBadge.Glyph, Height = PlayStateBadge.Glyph, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false, IsVisible = false, Opacity = 0, RenderTransform = _flashScale, RenderTransformOrigin = RelativePoint.Center,
                Effect = Shadow(PlayStateBadge.Glow), Children = { _flashIcon }
            };
            _root.Children.Add(_flash);
            _flashClock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000 / 120.0) };
            _flashClock.Tick += (_, _) => FlashFrame();
        }
        _flashIcon.Kind = paused ? "pause" : "play";
        // The play triangle's weight sits left of its box: moved so its centre of mass is in the middle.
        _flashIcon.RenderTransform = paused ? null : new TranslateTransform(PlayStateBadge.PlayNudge * PlayStateBadge.Glyph, 0);
        // Pressed again while it shows: the opacity goes on from where it is, the size starts again.
        _flashFrom = _flash.IsVisible ? _flash.Opacity : 0;
        _flashStill = MacMotion.Reduced;
        _flash.IsVisible = true; _flashStart = DateTimeOffset.UtcNow;
        FlashFrame(); _flashClock!.Start();
    }
    /// <summary>A jump said by the pill takes the middle of the picture at once.</summary>
    private void HidePlayState()
    {
        _flashClock?.Stop();
        if (_flash is null) return;
        _flash.IsVisible = false; _flash.Opacity = 0;
    }
    private void FlashFrame()
    {
        if (_flash is null) return;
        var t = (DateTimeOffset.UtcNow - _flashStart).TotalMilliseconds;
        if (t >= (_flashStill ? PlayStateBadge.Still : PlayStateBadge.End) || _mini) { HidePlayState(); return; }
        var (opacity, scale) = _flashStill ? (1d, 1d) : PlayStateBadge.At(t, _flashFrom);
        _flash.Opacity = opacity; _flashScale.ScaleX = _flashScale.ScaleY = scale;
    }
    /// <summary>Falling straight down. Avalonia's blur radius r is drawn by Skia with a standard deviation of 0.288675 r + 0.5.</summary>
    private static DropShadowEffect Shadow(PlayStateBadge.Shadow shadow) =>
        new() { Color = Colors.Black, OffsetX = 0, OffsetY = shadow.Down, BlurRadius = Math.Max(0, (shadow.Sigma - .5) / .288675), Opacity = shadow.Opacity };
}
