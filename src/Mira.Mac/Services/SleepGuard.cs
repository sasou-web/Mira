using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>
/// Keeps the Mac's display awake while a video plays, as Apple's own players do (an IOKit power assertion). mpv cannot
/// do it here: it draws through Mira instead of its own window, where it would. Released on pause and at the end.
/// </summary>
public static partial class SleepGuard
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Encoding = 0x08000100; // kCFStringEncodingUTF8
    private const uint LevelOn = 255;         // kIOPMAssertionLevelOn
    private static uint _assertion;

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);
    [LibraryImport(IOKit)] private static partial int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);
    [LibraryImport(IOKit)] private static partial int IOPMAssertionRelease(uint id);

    /// <summary>True while playing: the display stays on; false: macOS may dim and sleep it again.</summary>
    public static void Set(bool awake)
    {
        if (!OperatingSystem.IsMacOS() || awake == (_assertion != 0)) return;
        try
        {
            if (!awake) { IOPMAssertionRelease(_assertion); _assertion = 0; return; }
            var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleDisplaySleep", Encoding);
            var name = CFStringCreateWithCString(IntPtr.Zero, "Mira lit une vidéo", Encoding);
            try { if (IOPMAssertionCreateWithName(type, LevelOn, name, out var id) == 0) _assertion = id; }
            finally { CFRelease(type); CFRelease(name); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
