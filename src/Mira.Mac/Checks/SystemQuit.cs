using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace Mira.Mac.Checks;

/// <summary>
/// For the self-check, last: macOS asking Mira to quit, as it does to shut down, restart or log out. On a Mac, the real
/// thing: [NSApp terminate:], which asks Avalonia's delegate (applicationShouldTerminate:); accepted, AppKit ends the
/// process before this returns. Elsewhere (Linux, where the self-check also runs), Avalonia's handler of that same request
/// (OnShutdownRequested of the desktop lifetime), which the delegate calls on a Mac.
/// </summary>
internal static partial class SystemQuit
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendObject(IntPtr receiver, IntPtr selector, IntPtr value);

    /// <summary>Asks; true when the quit was refused (macOS would then stop its shutdown or restart).</summary>
    public static bool Ask(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (OperatingSystem.IsMacOS())
        {
            var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            SendObject(application, sel_registerName("terminate:"), IntPtr.Zero);
            // Still here: refused.
            return true;
        }
        var request = new ShutdownRequestedEventArgs();
        var handler = desktop.GetType().GetMethod("OnShutdownRequested", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Avalonia's OnShutdownRequested is gone: find what the native delegate calls now.");
        handler.Invoke(desktop, [null, request]);
        return request.Cancel;
    }
}
