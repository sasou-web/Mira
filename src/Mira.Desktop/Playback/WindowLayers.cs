using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Mira.Desktop.Playback;

/// <summary>
/// Windows' compositor over the whole window, above its child windows (mpv's and the controls'): the only place where
/// anything can be drawn over the video. A window takes a single such layer, shared here by the black sheet of the
/// player's fades (<see cref="WindowShade"/>) and the mini-player's corners (<see cref="VideoCorners"/>). Attached to the
/// window only while one of them shows: a visual over the video would otherwise cost the video its direct path to the
/// screen. Positions are in the window's client pixels.
/// </summary>
internal sealed class WindowLayers
{
    public Compositor Compositor { get; }
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private WindowLayers(IntPtr window)
    {
        // A compositor needs a dispatcher queue on its thread; WPF's message loop serves it.
        if (_queue == IntPtr.Zero) Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = CurrentThread }, out _queue));
        Compositor = new Compositor();
        Compositor.As<IDesktopInterop>().CreateDesktopWindowTarget(window, true, out var target);
        _target = DesktopWindowTarget.FromAbi(target); Marshal.Release(target);
        _root = Compositor.CreateContainerVisual(); _root.RelativeSizeAdjustment = System.Numerics.Vector2.One;
    }
    private static IntPtr _queue;
    /// <summary>The layer of this window; null where Windows cannot give one (no fades then, and corners as the window region cuts them).</summary>
    public static WindowLayers? TryCreate(IntPtr window)
    {
        try { return new WindowLayers(window); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or TypeLoadException or NotSupportedException) { return null; }
    }
    /// <summary>Shows this visual over the window: above the others when <paramref name="top"/>, otherwise under them.</summary>
    public void Show(Visual visual, bool top)
    {
        if (visual.Parent is null) { if (top) _root.Children.InsertAtTop(visual); else _root.Children.InsertAtBottom(visual); }
        if (_target.Root is null) _target.Root = _root;
    }
    /// <summary>Takes it away; the layer leaves the window once nothing is left on it.</summary>
    public void Hide(Visual visual)
    {
        if (visual.Parent is not null) _root.Children.Remove(visual);
        if (_root.Children.Count == 0 && _target.Root is not null) _target.Root = null;
    }
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool topmost, out IntPtr target);
    }
    private const int CurrentThread = 2;
    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { public int Size, ThreadType, Apartment; }
    [DllImport("CoreMessaging")] private static extern int CreateDispatcherQueueController(QueueOptions options, out IntPtr controller);
}
