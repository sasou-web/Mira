using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mira.Desktop;

/// <summary>
/// --ui-audit never shows on the screen: each top-level window of the process (the main window, a menu, a list, a
/// dialog such as « Create a group ») is cloaked by DWM as soon as Windows creates it. Kept under everything was not
/// enough: on 2026-11-21 at 03:30, with nothing in front of it, the Dell showed the audit's menus and dialogs opening
/// and closing by themselves (each popup is a window of its own, above its owner). A cloaked window is still laid out,
/// still placed on the screen (where WPF puts a menu for real) and still drawn by WPF: the captures (RenderTargetBitmap)
/// and the transitions (WPF values) do not change; DWM just never composes it.
/// </summary>
internal static class AuditCloak
{
    private const uint EventObjectCreate = 0x8000, EventObjectShow = 0x8002, OutOfContext = 0;
    private const int ObjectWindow = 0, Cloak = 13, RootAncestor = 2, CallWindowProc = 4, ShowWindowMessage = 0x0018, PositionChanging = 0x0046, ShowFlag = 0x0040;
    private static readonly WinEventProc Callback = OnEvent;
    private static readonly HookProc Before = OnMessage;
    private static IntPtr _hook, _messages;

    /// <summary>Cloaks <paramref name="window"/> now, and every top-level window the process creates from then on.</summary>
    internal static void Start(IntPtr window)
    {
        Hide(window);
        // On the UI thread, before a window is shown (WM_SHOWWINDOW, or SWP_SHOWWINDOW for a popup): never one frame on
        // the screen. The event hook stays for any window of another thread (it comes a moment later).
        if (_messages == IntPtr.Zero)
            _messages = SetWindowsHookEx(CallWindowProc, Before, IntPtr.Zero, GetCurrentThreadId());
        if (_hook == IntPtr.Zero)
            _hook = SetWinEventHook(EventObjectCreate, EventObjectShow, IntPtr.Zero, Callback, (uint)Environment.ProcessId, 0, OutOfContext);
    }

    private static IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            // CWPSTRUCT: lParam, wParam, message, hwnd. WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags.
            var message = Marshal.ReadInt32(lParam, 2 * IntPtr.Size);
            var window = Marshal.ReadIntPtr(lParam, 3 * IntPtr.Size);
            var showing = message == ShowWindowMessage && Marshal.ReadIntPtr(lParam, IntPtr.Size) != IntPtr.Zero
                || message == PositionChanging && (Marshal.ReadInt32(Marshal.ReadIntPtr(lParam), 2 * IntPtr.Size + 16) & ShowFlag) != 0;
            if (showing && GetAncestor(window, RootAncestor) == window) Hide(window);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>True when DWM keeps <paramref name="window"/> off the screen (for the audit's own check).</summary>
    internal static bool IsCloaked(IntPtr window) => DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static void OnEvent(IntPtr hook, uint type, IntPtr window, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjectWindow || idChild != 0 || window == IntPtr.Zero || GetAncestor(window, RootAncestor) != window) return;
        Hide(window);
    }

    private static void Hide(IntPtr window)
    {
        var on = 1;
        var result = DwmSetWindowAttribute(window, Cloak, ref on, sizeof(int));
        if (result != 0) Debug.WriteLine($"ui-audit: DWM did not cloak 0x{window:X} (0x{result:X}).");
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32")] private static extern uint GetCurrentThreadId();
    private delegate void WinEventProc(IntPtr hook, uint type, IntPtr window, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32")] private static extern IntPtr GetAncestor(IntPtr window, int flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
