using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>
/// What macOS says of the trackpad, read rather than assumed: the scrolling direction (System Settings → Trackpad →
/// Scroll &amp; Zoom → Natural scrolling) and whether two fingers swipe between pages (More Gestures → Swipe between pages).
/// And the Force Touch trackpad's firm press, which Avalonia does not pass on (<see cref="ForceClicked"/>).
/// </summary>
public static partial class MacTrackpad
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr application);
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFPreferencesAppSynchronize(IntPtr application);
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(IntPtr value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFBooleanGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFNumberGetTypeID();
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFBooleanGetValue(IntPtr value);
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFNumberGetValue(IntPtr number, nint type, out int value);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC)][return: MarshalAs(UnmanagedType.U1)] private static unsafe partial bool class_addMethod(IntPtr type, IntPtr selector, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte SendBool(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint SendInteger(IntPtr receiver, IntPtr selector);

    private static bool? _naturalOverride, _swipesOverride;
    /// <summary>Natural scrolling: the content follows the fingers. On by default on a Mac, and where nothing says otherwise.</summary>
    public static bool Natural { get; private set; } = true;
    /// <summary>Two fingers sideways go back and forward, as in Safari; off when the Mac gives that swipe to three fingers or to nothing.</summary>
    public static bool SwipesBetweenPages { get; private set; } = true;

    /// <summary>Reads both settings again: at start and each time Mira comes back in front (they may have changed meanwhile).</summary>
    public static void Refresh()
    {
        Natural = _naturalOverride ?? ReadNatural() ?? true;
        SwipesBetweenPages = _swipesOverride ?? ReadSwipes() ?? true;
    }
    /// <summary>For the self-check: the same answers whatever the runner's settings.</summary>
    public static void Override(bool natural, bool swipes) { _naturalOverride = natural; _swipesOverride = swipes; Refresh(); }

    /// <summary>com.apple.swipescrolldirection of the global domain, the key « Natural scrolling » writes; null when unread.</summary>
    private static bool? ReadNatural()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            // The constant itself, exported by CoreFoundation, rather than a copy of its text.
            var library = NativeLibrary.Load(CoreFoundation);
            var any = Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFPreferencesAnyApplication"));
            var key = CFStringCreateWithCString(IntPtr.Zero, "com.apple.swipescrolldirection", 0x08000100);
            try
            {
                CFPreferencesAppSynchronize(any);
                var value = CFPreferencesCopyAppValue(key, any);
                if (value == IntPtr.Zero) return null;
                try
                {
                    var type = CFGetTypeID(value);
                    if (type == CFBooleanGetTypeID()) return CFBooleanGetValue(value);
                    if (type == CFNumberGetTypeID() && CFNumberGetValue(value, 9 /* kCFNumberIntType */, out var number)) return number != 0;
                    return null;
                }
                finally { CFRelease(value); }
            }
            finally { CFRelease(key); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }
    /// <summary>[NSEvent isSwipeTrackingFromScrollEventsEnabled], what Safari asks before a two-finger swipe goes back.</summary>
    private static bool? ReadSwipes()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var type = objc_getClass("NSEvent");
            return type == IntPtr.Zero ? null : SendBool(type, sel_registerName("isSwipeTrackingFromScrollEventsEnabled")) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    // —— Force click ——
    /// <summary>A firm press on a Force Touch trackpad (its second click), on the main thread, where Avalonia runs.</summary>
    public static event Action? ForceClicked;
    /// <summary>Where an error in the press's handling goes (errors.log), since it cannot be thrown.</summary>
    public static Action<Exception>? Failed;
    private static nint _stage;
    private static bool _listening;
    /// <summary>
    /// Teaches Avalonia's view (AvnView, which leaves pressureChangeWithEvent: to NSResponder) to report the press's
    /// stage. Added once, never replacing a method of Avalonia's own: if a later Avalonia has one, nothing is added.
    /// </summary>
    public static unsafe void ListenForForceClick()
    {
        if (_listening || !OperatingSystem.IsMacOS()) return;
        try
        {
            var view = objc_getClass("AvnView");
            if (view == IntPtr.Zero) return;
            _listening = class_addMethod(view, sel_registerName("pressureChangeWithEvent:"), &PressureChanged, "v@:@");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
    [UnmanagedCallersOnly]
    private static void PressureChanged(IntPtr self, IntPtr selector, IntPtr nsEvent)
    {
        // Nothing may leave this method: an exception through native code ends the app.
        try
        {
            var stage = SendInteger(nsEvent, sel_registerName("stage"));
            var rising = stage == 2 && _stage < 2;
            _stage = stage;
            if (rising) ForceClicked?.Invoke();
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
    }
}
