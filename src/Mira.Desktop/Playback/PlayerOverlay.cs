using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;

namespace Mira.Desktop.Playback;

/// <summary>
/// The player's controls over mpv's child HWND, in a layered child window of the main window: WPF cannot draw over a
/// native child (airspace). As a child, the controls belong to the window as the person sees it: a screenshot, a
/// screen or window share (Discord, OBS, Xbox Game Bar) shows them with the video. Unlike Popup, its size is not capped
/// at 75% of the monitor, so it can cover the entire fullscreen video.
/// </summary>
[ContentProperty(nameof(Child))]
public sealed class PlayerOverlay : FrameworkElement
{
    private HwndSource? _source;
    private FrameworkElement? _child;
    private long _activatedByClickAt = long.MinValue;
    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;
    public FrameworkElement? Child
    {
        get => _child;
        set { if (_child is not null) RemoveLogicalChild(_child); _child = value; if (value is not null) AddLogicalChild(value); }
    }
    protected override IEnumerator LogicalChildren => new[] { Child }.GetEnumerator();
    public static readonly DependencyProperty PlacementTargetProperty = DependencyProperty.Register(nameof(PlacementTarget), typeof(FrameworkElement), typeof(PlayerOverlay));
    public FrameworkElement PlacementTarget { get => (FrameworkElement)GetValue(PlacementTargetProperty); set => SetValue(PlacementTargetProperty, value); }
    public bool IsOpen
    {
        get => _source is not null;
        set
        {
            if (value == IsOpen) return;
            if (!value) { if (_source is { } source) { _source = null; source.RootVisual = null; source.Dispose(); } return; }
            if (Child is null || PresentationSource.FromVisual(PlacementTarget) is not HwndSource owner) return;
            var rect = Bounds(owner.Handle); _placed = Rect.Empty;
            var parameters = new HwndSourceParameters(L("Mira · commandes de lecture", "Mira · playback controls"))
            {
                // WS_CHILD | WS_CLIPSIBLINGS. WS_EX_NOACTIVATE keeps the keyboard where it is, as WPF does for its
                // menus: the window's own focus, routed by WPF to the control focused here.
                ParentWindow = owner.Handle, WindowStyle = 0x40000000 | 0x04000000, ExtendedWindowStyle = 0x08000000,
                UsesPerPixelTransparency = true,
                PositionX = (int)rect.X, PositionY = (int)rect.Y,
                Width = Math.Max(1, (int)rect.Width), Height = Math.Max(1, (int)rect.Height)
            };
            _source = new HwndSource(parameters) { SizeToContent = SizeToContent.Manual, RootVisual = Child };
            _source.AddHook(Hook);
            Reposition();
        }
    }
    /// <summary>
    /// A click on the controls of a window in the background brings it to the front (as any click on a window); WPF's
    /// handling of the click follows. Recorded so that this click is not also taken as a pause.
    /// </summary>
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_MOUSEACTIVATE, before the button message: MA_ACTIVATE.
        if (message != 0x0021) return IntPtr.Zero;
        if (GetActiveWindow() != GetAncestor(hwnd, 2)) _activatedByClickAt = Environment.TickCount64;
        handled = true; return new IntPtr(1);
    }
    /// <summary>The click being handled is the one that brought the window to the front (asked once per click).</summary>
    public bool TakeActivatingClick()
    {
        var activating = Environment.TickCount64 - _activatedByClickAt < 500;
        _activatedByClickAt = long.MinValue;
        return activating;
    }
    private Rect _placed = Rect.Empty;
    public void Reposition()
    {
        if (_source is null || Child is null || !PlacementTarget.IsVisible) return;
        var parent = GetParent(_source.Handle);
        var rect = Bounds(parent);
        Child.Width = PlacementTarget.ActualWidth; Child.Height = PlacementTarget.ActualHeight;
        // Moving a per-pixel transparent window makes Windows recompose it: skip calls that change nothing.
        if (rect == _placed) return;
        _placed = rect;
        // Above the video's own window (HWND_TOP), shown, never activated.
        SetWindowPos(_source.Handle, IntPtr.Zero, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, 0x0050);
    }
    /// <summary>The placement target in the parent window's client coordinates, in physical pixels.</summary>
    private Rect Bounds(IntPtr parent)
    {
        var topLeft = PlacementTarget.PointToScreen(new Point());
        var bottomRight = PlacementTarget.PointToScreen(new Point(PlacementTarget.ActualWidth, PlacementTarget.ActualHeight));
        var origin = new NativePoint();
        ClientToScreen(parent, ref origin);
        return new Rect(new Point(topLeft.X - origin.X, topLeft.Y - origin.Y), new Point(bottomRight.X - origin.X, bottomRight.Y - origin.Y));
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32")] private static extern IntPtr GetActiveWindow();
}
