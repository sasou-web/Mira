using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>The screens connected now and the main window's place among them, in device pixels (per-monitor DPI aware).</summary>
internal static class Screens
{
    /// <summary>Each screen's work area and scale, the primary screen first; empty when Windows cannot list them.</summary>
    public static List<ScreenFit.Screen> All()
    {
        var screens = new List<(bool Primary, ScreenFit.Screen Screen)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96d : 1;
            var work = info.Work;
            screens.Add(((info.Flags & 1) != 0, new(new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), scale)));
            return true;
        }, IntPtr.Zero);
        return screens.OrderByDescending(x => x.Primary).Select(x => x.Screen).ToList();
    }
    public static (double X, double Y)? Pointer() => GetCursorPos(out var point) ? (point.X, point.Y) : null;
    /// <summary>The window's top-left corner while it is neither maximized nor minimized; null otherwise.</summary>
    public static (double X, double Y)? NormalCorner(IntPtr window) =>
        window != IntPtr.Zero && !IsZoomed(window) && !IsIconic(window) && GetWindowRect(window, out var rect) ? (rect.Left, rect.Top) : null;
    /// <summary>
    /// Puts the window at <paramref name="target"/>. Moved first without its size: reaching a screen of another scale,
    /// WPF resizes it for that scale (and the minimum size of the first screen does not apply there); then sized.
    /// </summary>
    public static void Move(IntPtr window, ScreenFit.Pixels target)
    {
        var (x, y, width, height) = ((int)target.Left, (int)target.Top, (int)target.Width, (int)target.Height);
        if (!SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder)) return;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, NoZOrder | NoActivate | NoOwnerZOrder)) return;
            if (GetWindowRect(window, out var rect) && rect.Left == x && rect.Top == y && rect.Right - rect.Left == width && rect.Bottom - rect.Top == height) return;
        }
    }

    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
