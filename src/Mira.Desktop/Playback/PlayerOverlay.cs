using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mira.Desktop.Playback;

/// <summary>A non-activating owned surface over mpv's child HWND. Unlike Popup, its size
/// is not capped at 75% of the monitor, so it can cover the entire fullscreen video.</summary>
[ContentProperty(nameof(Child))]
public sealed class PlayerOverlay : FrameworkElement
{
    private HwndSource? _source;
    private FrameworkElement? _child;
    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;
    public FrameworkElement? Child
    {
        get => _child;
        set { if (_child is not null) RemoveLogicalChild(_child); _child = value; if (value is not null) AddLogicalChild(value); }
    }
    protected override IEnumerator LogicalChildren => new[] { Child }.GetEnumerator();
    public static readonly DependencyProperty PlacementTargetProperty = DependencyProperty.Register(nameof(PlacementTarget), typeof(FrameworkElement), typeof(PlayerOverlay));
    public FrameworkElement PlacementTarget { get => (FrameworkElement)GetValue(PlacementTargetProperty); set => SetValue(PlacementTargetProperty, value); }
    public bool ExcludeFromCapture { get; set; } = true;
    /// <summary>Whether Windows accepted the capture exclusion for the current overlay window.</summary>
    public bool CaptureExcluded { get; private set; }
    public bool IsOpen
    {
        get => _source is not null;
        set
        {
            if (value == IsOpen) return;
            if (!value) { if (_source is { } source) { _source = null; source.RootVisual = null; source.Dispose(); } return; }
            if (Child is null || PresentationSource.FromVisual(PlacementTarget) is not HwndSource owner) return;
            var rect = Bounds(); _placed = Rect.Empty;
            var parameters = new HwndSourceParameters("Mira · commandes de lecture")
            {
                ParentWindow = owner.Handle, WindowStyle = unchecked((int)0x80000000),
                ExtendedWindowStyle = 0x08000080, UsesPerPixelOpacity = true,
                PositionX = (int)rect.X, PositionY = (int)rect.Y,
                Width = Math.Max(1, (int)rect.Width), Height = Math.Max(1, (int)rect.Height)
            };
            _source = new HwndSource(parameters) { SizeToContent = SizeToContent.Manual, RootVisual = Child };
            _source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            { if (message == 0x0021) { handled = true; return new IntPtr(3); } return IntPtr.Zero; });
            // Screenshots and captures (Win+Maj+S, Impr. écran, Xbox Game Bar) see the video without the controls.
            // Before Windows 10 2004 this value would black out the controls instead, so it is not applied there.
            if (ExcludeFromCapture && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) CaptureExcluded = SetWindowDisplayAffinity(_source.Handle, 0x11);
            Reposition();
        }
    }
    private Rect _placed = Rect.Empty;
    public void Reposition()
    {
        if (_source is null || Child is null || !PlacementTarget.IsVisible) return;
        var rect = Bounds();
        Child.Width = PlacementTarget.ActualWidth; Child.Height = PlacementTarget.ActualHeight;
        // Moving a per-pixel transparent window makes Windows recompose it: skip calls that change nothing.
        if (rect == _placed) return;
        _placed = rect;
        SetWindowPos(_source.Handle, IntPtr.Zero, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, 0x0054);
    }
    private Rect Bounds()
    {
        var topLeft = PlacementTarget.PointToScreen(new Point());
        var bottomRight = PlacementTarget.PointToScreen(new Point(PlacementTarget.ActualWidth, PlacementTarget.ActualHeight));
        return new Rect(topLeft, bottomRight);
    }
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
