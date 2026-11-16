using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>Mira as macOS sees it: whether it is the active app ([NSApp isActive]).</summary>
public static partial class MacApplication
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte SendBool(IntPtr receiver, IntPtr selector);

    /// <summary>True when Mira is the app in front, false when another is; null where it cannot be read (Linux).</summary>
    public static bool? Active
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return null;
            try
            {
                var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                return application == IntPtr.Zero ? null : SendBool(application, sel_registerName("isActive")) != 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
    }
}
