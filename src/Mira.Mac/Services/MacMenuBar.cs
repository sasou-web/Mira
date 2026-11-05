using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>
/// The menu bar as Mira makes it, and no more (MainWindow.Menu.cs): AppKit adds its own « Enter Full Screen » when it
/// does not recognise Mira's (an Avalonia item has no toggleFullScreen: of AppKit), and « Show Tab Bar » to a menu called
/// View; Mira has one window and no tabs. Avalonia only turns the first off for an app run outside its bundle.
/// Before the app finishes launching, where AppKit reads them.
/// </summary>
public static partial class MacMenuBar
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr SendText(IntPtr receiver, IntPtr selector, string text);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendBool(IntPtr receiver, IntPtr selector, byte value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SendBoolFor(IntPtr receiver, IntPtr selector, byte value, IntPtr key);

    public static void Prepare()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var defaults = Send(objc_getClass("NSUserDefaults"), sel_registerName("standardUserDefaults"));
            var key = SendText(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "NSFullScreenMenuItemEverywhere");
            if (defaults != IntPtr.Zero && key != IntPtr.Zero) SendBoolFor(defaults, sel_registerName("setBool:forKey:"), 0, key);
            SendBool(objc_getClass("NSWindow"), sel_registerName("setAllowsAutomaticWindowTabbing:"), 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}
