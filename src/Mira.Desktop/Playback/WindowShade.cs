using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;

namespace Mira.Desktop.Playback;

/// <summary>
/// A black sheet over the whole window, mpv's window and the controls' windows included: nothing else can fade over the
/// video. WPF draws under native windows, and a layered window the size of a 4K screen cannot keep up. Windows' compositor
/// runs the fades on its own, at the screen's rate, whatever the interface's thread is doing. Attached to the window only
/// while it shows: a visual over the video would otherwise cost the video its direct path to the screen.
/// </summary>
internal sealed class WindowShade
{
    private readonly WindowLayers _layers;
    private readonly Compositor _compositor;
    private readonly SpriteVisual _sheet;
    private readonly CompositionEasingFunction _ease;
    private int _fade;
    public WindowShade(WindowLayers layers)
    {
        _layers = layers; _compositor = layers.Compositor;
        _sheet = _compositor.CreateSpriteVisual();
        _sheet.RelativeSizeAdjustment = Vector2.One; _sheet.Brush = _compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0)); _sheet.Opacity = 0;
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.2f, 1));
    }
    /// <summary>Over the window at once, and on screen when this ends.</summary>
    public async Task CoverAsync()
    {
        ++_fade; _sheet.StopAnimation("Opacity"); _sheet.Opacity = 1; _layers.Show(_sheet, top: true);
        // Off this thread: the compositor commits on it. Three of the screen's frames, then the sheet is on screen.
        await Task.Run(() => { DwmFlush(); DwmFlush(); DwmFlush(); });
    }
    /// <summary>Darkens the window to black, from what shows now. Ends once it is black.</summary>
    public Task DarkenAsync(int ms) => FadeAsync(1, ms);
    /// <summary>Lifts the sheet, from what shows now; gone from the window when this ends.</summary>
    public async Task LiftAsync(int ms)
    {
        var fade = _fade + 1;
        await FadeAsync(0, ms);
        if (fade == _fade) _layers.Hide(_sheet);
    }
    private Task FadeAsync(float to, int ms)
    {
        ++_fade; _layers.Show(_sheet, top: true);
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, to, _ease);
        animation.Duration = TimeSpan.FromMilliseconds(ms);
        _sheet.StartAnimation("Opacity", animation);
        return Task.Delay(ms);
    }
    [DllImport("dwmapi")] private static extern int DwmFlush();
}
