using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>macOS's « Réduire les animations » (Accessibilité → Affichage), as Windows' animation setting is read there.</summary>
public static partial class MacMotion
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte SendBool(IntPtr receiver, IntPtr selector);

    private static bool? _forced;
    /// <summary>For the self-check: macOS's choice set aside (null gives it back). The Mac runner leaves out motion.</summary>
    public static void Override(bool? reduced) => _forced = reduced;
    /// <summary>The motion to leave out (a pulse, a slide); false where it cannot be read (Linux, where the self-check runs).</summary>
    public static bool Reduced
    {
        get
        {
            if (_forced is { } forced) return forced;
            if (!OperatingSystem.IsMacOS()) return false;
            try
            {
                var workspace = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                return workspace != IntPtr.Zero && SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion")) != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}
