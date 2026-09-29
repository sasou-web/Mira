using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mira.Desktop.Playback;

/// <summary>Lets DWM draw the rounded outline and shadow around the native window,
/// including the embedded video. Maximized and fullscreen windows stay edge-to-edge.</summary>
public sealed class WindowFrame
{
    private readonly Window _window;
    private bool _fullscreen;
    public const double CornerRadius = 8;
    public bool Rounded => !_fullscreen && _window.WindowState == WindowState.Normal;

    public WindowFrame(Window window)
    {
        _window = window;
        window.SourceInitialized += (_, _) => Apply();
        window.StateChanged += (_, _) => window.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Render);
        window.SizeChanged += (_, _) => Apply();
        window.Activated += (_, _) => Apply();
        window.Deactivated += (_, _) => Apply();
    }

    public void SetFullscreen(bool fullscreen) { _fullscreen = fullscreen; Apply(); }

    private void Apply()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        UpdateContentInset(handle);
        Set(handle, 20, 1); // DWMWA_USE_IMMERSIVE_DARK_MODE
        Set(handle, 33, Rounded ? 2 : 1); // DWMWA_WINDOW_CORNER_PREFERENCE
        // COLORREF is BGR. DWM draws a single physical-pixel outline.
        Set(handle, 34, Rounded ? (_window.IsActive ? 0x00423F3C : 0x002E2C2A) : unchecked((int)0xFFFFFFFE));
    }

    private void UpdateContentInset(IntPtr handle)
    {
        if (_window.Content is not FrameworkElement content) return;
        var inset = new Thickness(0);
        if (!_fullscreen && _window.WindowState == WindowState.Maximized)
        {
            var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetWindowRect(handle, out var bounds) && GetMonitorInfo(MonitorFromWindow(handle, 2), ref monitor))
            {
                // Windows places the invisible resize frame outside the work area when maximized.
                // Keep our custom caption buttons and content inside the visible rectangle.
                var dpi = VisualTreeHelper.GetDpi(_window);
                inset = new Thickness(Math.Max(0, monitor.Work.Left - bounds.Left) / dpi.DpiScaleX,
                    Math.Max(0, monitor.Work.Top - bounds.Top) / dpi.DpiScaleY,
                    Math.Max(0, bounds.Right - monitor.Work.Right) / dpi.DpiScaleX,
                    Math.Max(0, bounds.Bottom - monitor.Work.Bottom) / dpi.DpiScaleY);
            }
        }
        content.Margin = inset;
    }

    private static void Set(IntPtr handle, int attribute, int value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
