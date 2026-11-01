using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Mira.Mac.Views;

// The pause and play flash in the middle of the picture, as on Windows and Mira web.
public sealed partial class PlayerPage
{
    private readonly Icon _flashIcon = new("pause", 32) { Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScaleTransform _flashScale = new(1, 1);
    private Border? _flash;
    private DispatcherTimer? _flashClock;
    private DateTimeOffset _flashStart;
    private const double FlashMs = 700;

    /// <summary>
    /// The state just asked for (click on the picture, the button, Space or K): a round badge that pops in, stays a
    /// moment and fades out, in 0.7 s. Never in the mini-player nor over the spinner.
    /// </summary>
    private void ShowPlayState(bool paused)
    {
        if (_mini || _spinner.IsVisible) return;
        if (_flash is null)
        {
            _flash = new Border
            {
                Width = 76, Height = 76, CornerRadius = new CornerRadius(38), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Background = Ui.Brush("#E0151517"), BorderBrush = Ui.Brush("#30FFFFFF"), BorderThickness = new Thickness(1),
                IsHitTestVisible = false, IsVisible = false, RenderTransform = _flashScale, RenderTransformOrigin = RelativePoint.Center, Child = _flashIcon
            };
            _root.Children.Add(_flash);
            _flashClock = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000 / 120.0) };
            _flashClock.Tick += (_, _) => FlashFrame();
        }
        _flashIcon.Kind = paused ? "pause" : "play";
        // The play triangle's weight sits left of its box: nudged so it looks centred in the circle.
        _flashIcon.Margin = paused ? default : new Thickness(3, 0, 0, 0);
        _flash.IsVisible = true; _flashStart = DateTimeOffset.UtcNow;
        FlashFrame(); _flashClock!.Start();
    }
    private void FlashFrame()
    {
        if (_flash is null) return;
        var t = (DateTimeOffset.UtcNow - _flashStart).TotalMilliseconds;
        if (t >= FlashMs || _mini) { _flashClock?.Stop(); _flash.IsVisible = false; _flash.Opacity = 0; return; }
        var (opacity, scale) = PlayState.At(t);
        _flash.Opacity = opacity; _flashScale.ScaleX = _flashScale.ScaleY = scale;
    }
}

/// <summary>The flash's curve, the same as Windows' keyframes: in by 110 ms (scale by 220 ms), held to 330 ms, out by 700 ms.</summary>
internal static class PlayState
{
    public static (double Opacity, double Scale) At(double ms)
    {
        static double Out(double x, double power) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), power);
        if (ms < 330) return (Out(ms / 110, 3), .82 + .18 * Out(ms / 220, 5));
        var fade = Out((ms - 330) / 370, 2);
        return (1 - fade, 1 + .12 * fade);
    }
}
