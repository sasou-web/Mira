using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>
/// The Mac going to sleep and waking up (a lid closed, then opened), as NSWorkspace says it: Windows' PowerModeChanged
/// here. Its notices come on the main thread, Avalonia's: the player pauses where it is before sleeping, and opens its
/// stream from Jellyfin again on waking (PlayerPage.Power.cs). Nothing is listened for elsewhere than on a Mac (the
/// self-check under Linux calls what they call).
/// </summary>
public static unsafe partial class MacPower
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [LibraryImport(ObjC)] private static partial void objc_registerClassPair(IntPtr type);
    [LibraryImport(ObjC)][return: MarshalAs(UnmanagedType.U1)] private static partial bool class_addMethod(IntPtr type, IntPtr selector, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void Send(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr action, IntPtr name, IntPtr sender);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    /// <summary>The Mac is about to sleep: what must be done before, done at once (macOS waits a little, not long).</summary>
    public static event Action? WillSleep;
    /// <summary>The Mac woke up: the network comes back a few seconds later.</summary>
    public static event Action? DidWake;
    /// <summary>Where an error in a notice's handling goes (errors.log), since it cannot be thrown back at macOS.</summary>
    public static Action<Exception>? Failed;
    /// <summary>The notices are listened for (a Mac, and the observer made): the player's own guess from its clock is then only a fallback.</summary>
    public static bool Listening { get; private set; }
    // Kept for the life of the app: NSNotificationCenter does not retain its observers.
    private static IntPtr _observer;

    /// <summary>
    /// An observer of NSWorkspace's notification center, a small class of its own (MiraPowerObserver, NSObject with two
    /// methods), made once. Its notices come on the main thread.
    /// </summary>
    public static void Listen()
    {
        if (Listening || !OperatingSystem.IsMacOS()) return;
        try
        {
            var type = objc_getClass("MiraPowerObserver");
            if (type == IntPtr.Zero)
            {
                type = objc_allocateClassPair(objc_getClass("NSObject"), "MiraPowerObserver", 0);
                if (type == IntPtr.Zero) return;
                class_addMethod(type, sel_registerName("miraWillSleep:"), &Sleeping, "v@:@");
                class_addMethod(type, sel_registerName("miraDidWake:"), &Woken, "v@:@");
                objc_registerClassPair(type);
            }
            _observer = Send(Send(type, sel_registerName("alloc")), sel_registerName("init"));
            var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            var center = workspace == IntPtr.Zero ? IntPtr.Zero : Send(workspace, sel_registerName("notificationCenter"));
            if (_observer == IntPtr.Zero || center == IntPtr.Zero) return;
            var add = sel_registerName("addObserver:selector:name:object:");
            Send(center, add, _observer, sel_registerName("miraWillSleep:"), Name("NSWorkspaceWillSleepNotification"), IntPtr.Zero);
            Send(center, add, _observer, sel_registerName("miraDidWake:"), Name("NSWorkspaceDidWakeNotification"), IntPtr.Zero);
            Listening = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
    /// <summary>The notice's name: AppKit's own constant, else its text (the same).</summary>
    private static IntPtr Name(string constant)
    {
        try { return Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(AppKit), constant)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return CFStringCreateWithCString(IntPtr.Zero, constant, 0x08000100); }
    }

    // Nothing may leave these methods: an exception through native code ends the app.
    [UnmanagedCallersOnly]
    private static void Sleeping(IntPtr self, IntPtr selector, IntPtr notification)
    {
        try { WillSleep?.Invoke(); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
    [UnmanagedCallersOnly]
    private static void Woken(IntPtr self, IntPtr selector, IntPtr notification)
    {
        try { DidWake?.Invoke(); }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
}
