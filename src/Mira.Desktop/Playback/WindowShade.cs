using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Mira.Desktop.Playback;

/// <summary>
/// A black sheet over the whole window, mpv's window and the controls' windows included: nothing else can fade over the
/// video. WPF draws under native windows, and a layered window the size of a 4K screen cannot keep up. Windows' compositor
/// runs the fades on its own, at the screen's rate, whatever the interface's thread is doing. Attached to the window only
/// while it shows: a visual over the video would otherwise cost the video its direct path to the screen.
/// </summary>
internal sealed class WindowShade
{
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly SpriteVisual _sheet;
    private readonly CompositionEasingFunction _ease;
    private int _fade;
    private WindowShade(IntPtr window)
    {
        // A compositor needs a dispatcher queue on its thread; WPF's message loop serves it.
        if (_queue == IntPtr.Zero) Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = CurrentThread }, out _queue));
        _compositor = new Compositor();
        _compositor.As<IDesktopInterop>().CreateDesktopWindowTarget(window, true, out var target);
        _target = DesktopWindowTarget.FromAbi(target); Marshal.Release(target);
        _sheet = _compositor.CreateSpriteVisual();
        _sheet.RelativeSizeAdjustment = Vector2.One; _sheet.Brush = _compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0)); _sheet.Opacity = 0;
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(.4f, 0), new Vector2(.2f, 1));
    }
    private static IntPtr _queue;
    /// <summary>The sheet of this window; null where Windows cannot give one (it then has no fades, nothing else changes).</summary>
    public static WindowShade? TryCreate(IntPtr window)
    {
        try { return new WindowShade(window); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or TypeLoadException or NotSupportedException) { return null; }
    }
    /// <summary>Over the window at once, and on screen when this ends.</summary>
    public async Task CoverAsync()
    {
        ++_fade; _sheet.StopAnimation("Opacity"); _sheet.Opacity = 1; _target.Root = _sheet;
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
        if (fade == _fade) _target.Root = null;
    }
    private Task FadeAsync(float to, int ms)
    {
        ++_fade; _target.Root = _sheet;
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue"); animation.InsertKeyFrame(1, to, _ease);
        animation.Duration = TimeSpan.FromMilliseconds(ms);
        _sheet.StartAnimation("Opacity", animation);
        return Task.Delay(ms);
    }
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }
    private const int CurrentThread = 2;
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [DllImport("CoreMessaging")] private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
    [DllImport("dwmapi")] private static extern int DwmFlush();
}
