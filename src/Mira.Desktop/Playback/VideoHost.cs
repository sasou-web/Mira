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
    /// <summary>
    /// A window class of Mira's, black behind mpv: a STATIC window paints itself in the system's light grey, and that was
    /// a flash of light on every start, from the moment the player shows until mpv's own window is there. The mouse goes
    /// through it to Mira, as through a STATIC (WM_NCHITTEST in <see cref="WndProc"/>).
    /// </summary>
    private const string ClassName = "MiraVideo";
    private static readonly bool Registered = Register();
    private static bool Register()
    {
        var module = GetModuleHandle(null);
        var window = new WindowClass
        {
            Size = Marshal.SizeOf<WindowClass>(), Instance = module, Name = ClassName, Background = GetStockObject(BlackBrush),
            Procedure = GetProcAddress(GetModuleHandle("user32"), "DefWindowProcW")
        };
        return RegisterClassEx(ref window) != 0;
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var hwnd = CreateWindowEx(0, Registered ? ClassName : "STATIC", "Mira video", 0x40000000 | 0x10000000 | 0x02000000 | 0x04000000,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        Dispatcher.BeginInvoke(() => Ready?.Invoke(hwnd));
        return new HandleRef(this, hwnd);
    }
    protected override IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == HitTest) { handled = true; return Transparent; }
        // HwndHost holds its window where layout says, whoever moves it.
        return message == PositionChanging && _moving ? IntPtr.Zero : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
    private const int BlackBrush = 4, HitTest = 0x0084, PositionChanging = 0x0046;
    private static readonly IntPtr Transparent = new(-1);
    /// <summary>Rounds the native video corners (WPF clipping cannot reach a child HWND). Radius in device pixels; 0 removes it.</summary>
    public void SetCornerRadius(double devicePixels) { _radius = Math.Max(0, devicePixels); if (!_moving) ApplyRegion(); }
    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        if (_moving) { _laidOut = rcBoundingBox; return; }
        base.OnWindowPositionChanged(rcBoundingBox);
        if (rcBoundingBox.Size == _pixels) return;
        _pixels = rcBoundingBox.Size; ApplyRegion();
    }
    /// <summary>
    /// The window itself moves and resizes, frame by frame, between the two places of the player (see
    /// <c>MainWindow.PlayerMorph.cs</c>). Windows stretches what mpv draws to the window's size, so mpv need not know: it
    /// is told nothing of the sizes on the way (a new size each frame made it reallocate everything each frame, the film
    /// stuttering and the graphics card busy under the movement), only the one it should draw at (<see cref="DrawAt"/>).
    /// Layout no longer places the window meanwhile.
    /// </summary>
    public Rect BeginMove()
    {
        _moving = true; _laidOut = null; _toldSize = default;
        _mpvWindow = GetWindow(Handle, FirstChild);
        if (_mpvWindow != IntPtr.Zero) { _mpvProcedure = SetWindowLongPtr(_mpvWindow, WindowProcedureSlot, Marshal.GetFunctionPointerForDelegate(_heldProcedure)); }
        GetWindowRect(Handle, out var box); var corner = new Dot { X = box.Left, Y = box.Top }; ScreenToClient(GetParent(Handle), ref corner);
        return new Rect(corner.X, corner.Y, box.Right - box.Left, box.Bottom - box.Top);
    }
    /// <summary>Until Windows has composed the screen's next frame.</summary>
    public static void AwaitScreen() => DwmFlush();
    /// <summary>Where layout wants the window, in the parent window's pixels: known once layout has run.</summary>
    public Rect? LaidOut => _laidOut;
    /// <summary>mpv draws at this size from now on, whatever the window's size on the way: its picture is scaled by Windows, sharp when it grows.</summary>
    public void DrawAt(Size pixels) { _toldSize = pixels; TellSize(_mpvWindow, Told, pixels); }
    private static void TellSize(IntPtr window, nint kind, Size pixels)
    {
        if (window != IntPtr.Zero) PostMessage(window, Resized, kind, ((int)Math.Round(pixels.Height) << 16) | ((int)Math.Round(pixels.Width) & 0xffff));
    }
    /// <summary>mpv's window during a movement: the sizes Windows gives it stop here, the one <see cref="DrawAt"/> tells goes on.</summary>
    private IntPtr HeldProcedure(IntPtr hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == Resized) { if (wParam != Told) return IntPtr.Zero; wParam = 0; }
        return CallWindowProc(_mpvProcedure, hwnd, message, wParam, lParam);
    }
    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, nint wParam, nint lParam);
    private readonly WindowProcedure _heldProcedure;
    public VideoHost() => _heldProcedure = HeldProcedure;
    private IntPtr _mpvWindow, _mpvProcedure;
    private Size _toldSize;
    private const int FirstChild = 5, WindowProcedureSlot = -4;
    private const uint Resized = 0x0005;
    private const nint Told = 0x4D49;
    /// <param name="pixels">The window, in the parent window's pixels.</param>
    /// <param name="radius">Its corners, in pixels.</param>
    public void Move(Rect pixels, double radius)
    {
        // Each edge on its pixel: a width rounded apart from its position makes the right edge shake on the way.
        var left = (int)Math.Round(pixels.Left); var top = (int)Math.Round(pixels.Top);
        var width = Math.Max(1, (int)Math.Round(pixels.Right) - left); var height = Math.Max(1, (int)Math.Round(pixels.Bottom) - top);
        // mpv's own window at the same moment (it would follow a frame later, showing black at the edges). Its thread
        // takes this when it can: SWP_ASYNCWINDOWPOS | SWP_NOMOVE, as mpv itself does.
        if (_mpvWindow != IntPtr.Zero) SetWindowPos(_mpvWindow, IntPtr.Zero, 0, 0, width, height, 0x4000 | 0x0002 | 0x0004 | 0x0010 | 0x0100 | 0x0200);
        // SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOCOPYBITS | SWP_NOOWNERZORDER
        SetWindowPos(Handle, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010 | 0x0100 | 0x0200);
        var diameter = (int)Math.Round(radius * 2);
        if (diameter <= 0) { if (_clipped) { SetWindowRgn(Handle, IntPtr.Zero, false); _clipped = false; } return; }
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (SetWindowRgn(Handle, region, false) == 0) DeleteObject(region); else _clipped = true;
    }
    /// <summary>The movement is over: the window where layout says, with the corners of that place.</summary>
    public void EndMove()
    {
        if (!_moving) return;
        _moving = false;
        var mpv = _mpvWindow; _mpvWindow = IntPtr.Zero;
        if (mpv != IntPtr.Zero) SetWindowLongPtr(mpv, WindowProcedureSlot, _mpvProcedure);
        if (_laidOut is { } place) { _pixels = default; OnWindowPositionChanged(place); if (place.Size != _toldSize) TellSize(mpv, 0, place.Size); }
        else ApplyRegion();
    }
    private bool _moving;
    private Rect? _laidOut;
    /// <summary>
    /// Nothing of the window shows, and it keeps its size: mpv draws its first picture unseen, to be revealed from black.
    /// </summary>
    public void Conceal(bool concealed)
    {
        if (concealed == _concealed || Handle == IntPtr.Zero) return;
        _concealed = concealed;
        if (!concealed) { _clipped = true; ApplyRegion(); return; }
        var nothing = CreateRectRgn(0, 0, 0, 0);
        if (SetWindowRgn(Handle, nothing, true) == 0) { DeleteObject(nothing); _concealed = false; }
    }
    private bool _concealed;
    private void ApplyRegion()
    {
        if (Handle == IntPtr.Zero || _concealed) return;
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public int Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu, Name; public IntPtr SmallIcon;
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass window);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("gdi32")] private static extern IntPtr GetStockObject(int kind);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int extended, string cls, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32")] private static extern bool DestroyWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] private struct Box { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Dot { public int X, Y; }
    [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out Box box);
    [DllImport("user32")] private static extern bool ScreenToClient(IntPtr hwnd, ref Dot point);
    [DllImport("user32")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32")] private static extern IntPtr GetWindow(IntPtr hwnd, int relation);
    [DllImport("dwmapi")] private static extern int DwmFlush();
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int slot, IntPtr value);
    [DllImport("user32", EntryPoint = "CallWindowProcW")] private static extern IntPtr CallWindowProc(IntPtr procedure, IntPtr hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32", EntryPoint = "PostMessageW")] private static extern bool PostMessage(IntPtr hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr handle);
}
