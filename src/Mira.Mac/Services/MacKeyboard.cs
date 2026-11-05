using System.Runtime.InteropServices;

namespace Mira.Mac.Services;

/// <summary>Caps Lock, which Avalonia does not pass on: read from macOS (<c>[NSEvent modifierFlags]</c>), as the sign-in's notice needs.</summary>
public static partial class MacKeyboard
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nuint SendUnsigned(IntPtr receiver, IntPtr selector);
    /// <summary>NSEventModifierFlagCapsLock.</summary>
    private const nuint CapsLockFlag = 1 << 16;
    private static bool? _override;

    /// <summary>Caps Lock on; false where it cannot be read (Linux, where the self-check runs).</summary>
    public static bool CapsLock
    {
        get
        {
            if (_override is { } on) return on;
            if (!OperatingSystem.IsMacOS()) return false;
            try { return (SendUnsigned(objc_getClass("NSEvent"), sel_registerName("modifierFlags")) & CapsLockFlag) != 0; }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
    /// <summary>The self-check's Caps Lock (null: the keyboard's).</summary>
    public static void Override(bool? on) => _override = on;
}
