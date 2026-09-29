using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Mira.Desktop.Playback;

public sealed class VideoHost : HwndHost
{
    public event Action<IntPtr>? Ready;
    private double _radius;
    private Size _pixels;
    private bool _clipped;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var hwnd = CreateWindowEx(0, "STATIC", "Mira video", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Dispatcher.BeginInvoke(() => Ready?.Invoke(hwnd));
        return new HandleRef(this, hwnd);
    }
    /// <summary>Rounds the native video corners (WPF clipping cannot reach a child HWND). Radius in device pixels; 0 removes it.</summary>
    public void SetCornerRadius(double devicePixels) { _radius = Math.Max(0, devicePixels); ApplyRegion(); }
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (rcBoundingBox.Size == _pixels) return;
        _pixels = rcBoundingBox.Size; ApplyRegion();
    }
    private void ApplyRegion()
    {
        if (Handle == IntPtr.Zero) return;
        if (_radius <= 0 || _pixels.Width < 1)
        {
            if (_clipped) { SetWindowRgn(Handle, IntPtr.Zero, true); _clipped = false; }
            return;
        }
        var diameter = (int)Math.Round(_radius * 2);
        var region = CreateRoundRectRgn(0, 0, (int)Math.Round(_pixels.Width) + 1, (int)Math.Round(_pixels.Height) + 1, diameter, diameter);
        // On success the system owns the region; otherwise release it here.
        if (SetWindowRgn(Handle, region, true) == 0) DeleteObject(region); else _clipped = true;
    }
    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int extended, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
}
