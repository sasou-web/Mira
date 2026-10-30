using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Mira.Mac.Playback;

/// <summary>
/// The colour profile (ICC) of a Mac's screen, as macOS has it in « Displays » settings, for mpv to convert the video to
/// it: what mpv's own window does with icc-profile-auto, and what an app drawing mpv's frames itself must hand it.
/// </summary>
internal static partial class DisplayProfile
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [LibraryImport(CoreGraphics)] private static partial uint CGMainDisplayID();
    [LibraryImport(CoreGraphics)] private static partial IntPtr CGDisplayCopyColorSpace(uint display);
    [LibraryImport(CoreGraphics)] private static partial IntPtr CGColorSpaceCopyICCData(IntPtr space);
    [LibraryImport(CoreGraphics)] private static partial IntPtr CGColorSpaceCopyName(IntPtr space);
    [LibraryImport(CoreFoundation)] private static partial nint CFDataGetLength(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial IntPtr CFDataGetBytePtr(IntPtr data);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(IntPtr value);
    [LibraryImport(CoreFoundation)] private static partial nint CFStringGetLength(IntPtr value);
    [LibraryImport(CoreFoundation)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CFStringGetCString(IntPtr value, byte[] buffer, nint size, uint encoding);
    private const uint Utf8 = 0x08000100;

    /// <summary>The screen showing <paramref name="view"/> (Avalonia names it by its CGDirectDisplayID), else the main one.</summary>
    public static uint DisplayOf(Control view)
    {
        var handle = TopLevel.GetTopLevel(view)?.Screens?.ScreenFromVisual(view)?.TryGetPlatformHandle();
        return handle is { HandleDescriptor: "CGDirectDisplayID" } ? (uint)handle.Handle : CGMainDisplayID();
    }

    /// <summary>The screen's ICC profile and the name of its colour space; null when macOS gives none.</summary>
    public static (byte[] Data, string Name)? Read(uint display)
    {
        var space = CGDisplayCopyColorSpace(display);
        if (space == IntPtr.Zero) return null;
        try
        {
            var icc = CGColorSpaceCopyICCData(space);
            if (icc == IntPtr.Zero) return null;
            try
            {
                var length = CFDataGetLength(icc);
                if (length <= 0) return null;
                var data = new byte[length];
                Marshal.Copy(CFDataGetBytePtr(icc), data, 0, data.Length);
                return (data, Name(space));
            }
            finally { CFRelease(icc); }
        }
        finally { CFRelease(space); }
    }
    private static string Name(IntPtr space)
    {
        var name = CGColorSpaceCopyName(space);
        if (name == IntPtr.Zero) return "ICC";
        try
        {
            var buffer = new byte[CFStringGetLength(name) * 4 + 1];
            return CFStringGetCString(name, buffer, buffer.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') : "ICC";
        }
        finally { CFRelease(name); }
    }
}
