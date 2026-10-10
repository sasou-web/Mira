using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Mira.Desktop.Playback;

/// <summary>Occupies the monitor bounds, including the taskbar area, and restores the original window.</summary>
public sealed class FullscreenWindow(Window window)
{
    private WindowState _state;
    private Rect _restoreBounds;
    private ResizeMode _resize;
    private WindowChrome? _chrome;
    private WindowStyle _style;
    private bool _topmost;
    private double _minimumWidth, _minimumHeight;
    public bool Active { get; private set; }
    public Rect MonitorPixels { get; private set; }
    public void Enter(Rect? testPixels = null)
    {
        if (Active) return;
        var handle = new WindowInteropHelper(window).Handle;
        _state = window.WindowState; _restoreBounds = window.RestoreBounds; _resize = window.ResizeMode;
        _chrome = WindowChrome.GetWindowChrome(window); _topmost = window.Topmost;
        _minimumWidth = window.MinWidth; _minimumHeight = window.MinHeight;
        MonitorPixels = testPixels ?? ScreenBounds(handle);
        window.WindowState = WindowState.Normal;
        // The native caption style (kept for Windows' open, close and minimize animations) must not
        // show once the custom chrome is removed: fullscreen is a plain borderless surface.
        _style = window.WindowStyle; window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, null); window.ResizeMode = ResizeMode.NoResize;
        window.MinWidth = window.MinHeight = 0;
        Active = true; window.Topmost = window.IsActive;
        if (!SetWindowPos(handle, IntPtr.Zero, (int)MonitorPixels.X, (int)MonitorPixels.Y, (int)MonitorPixels.Width, (int)MonitorPixels.Height, 0x0030))
        { Exit(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Exit()
    {
        if (!Active) return;
        Active = false; window.Topmost = _topmost; WindowChrome.SetWindowChrome(window, _chrome); window.WindowStyle = _style; window.ResizeMode = _resize;
        window.WindowState = WindowState.Normal;
        if (!_restoreBounds.IsEmpty) { window.Left = _restoreBounds.Left; window.Top = _restoreBounds.Top; window.Width = _restoreBounds.Width; window.Height = _restoreBounds.Height; }
        window.WindowState = _state;
        window.MinWidth = _minimumWidth; window.MinHeight = _minimumHeight;
    }
    public void ActiveChanged() { if (Active) window.Topmost = window.IsActive || _topmost; }
    public Rect WindowPixels()
    { GetWindowRect(new WindowInteropHelper(window).Handle, out var rect); return rect.ToRect(); }
    public static Rect ScreenBounds(IntPtr handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Monitor.ToRect();
    }
    /// <summary>The monitor's area without the taskbar, in physical pixels: where a maximized window is visible.</summary>
    public static Rect WorkArea(IntPtr handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info.Work.ToRect();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    { public int Left, Top, Right, Bottom; public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
