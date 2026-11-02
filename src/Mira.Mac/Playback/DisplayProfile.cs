using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Mira.Mac.Playback;

/// <summary>
/// The colours of a Mac's screen, as macOS has them in « Displays » settings, for mpv to convert the video to them
/// (VideoView.ShowColours).
/// </summary>
internal static partial class DisplayProfile
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [LibraryImport(CoreGraphics)] private static partial uint CGMainDisplayID();
    [LibraryImport(CoreGraphics)] private static partial IntPtr CGDisplayCopyColorSpace(uint display);
    [LibraryImport(CoreGraphics)][return: MarshalAs(UnmanagedType.U1)] private static partial bool CGColorSpaceIsWideGamutRGB(IntPtr space);
    [LibraryImport(CoreGraphics)] private static partial IntPtr CGColorSpaceCopyName(IntPtr space);
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

    /// <summary>
    /// The name of the screen's colour space when it is wide gamut (P3: the Mac's own screen since 2016, most recent
    /// monitors), null for an sRGB screen or when macOS says nothing.
    /// </summary>
    public static string? WideGamut(uint display)
    {
        var space = CGDisplayCopyColorSpace(display);
        if (space == IntPtr.Zero) return null;
        try { return CGColorSpaceIsWideGamutRGB(space) ? Name(space) : null; }
        finally { CFRelease(space); }
    }
    private static string Name(IntPtr space)
    {
        var name = CGColorSpaceCopyName(space);
        if (name == IntPtr.Zero) return "P3";
        try
        {
            var buffer = new byte[CFStringGetLength(name) * 4 + 1];
            return CFStringGetCString(name, buffer, buffer.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0') : "P3";
        }
        finally { CFRelease(name); }
    }
}
