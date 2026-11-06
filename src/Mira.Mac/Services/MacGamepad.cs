using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// A game controller paired with this Mac (Xbox, PlayStation, MFi), read through Apple's GameController framework for
/// « Mode canapé » (<see cref="Couch"/>): its buttons and its left stick, read on the interface's thread, as macOS wants.
/// macOS gives them only while Mira is in front. Nothing where the framework is missing (Linux, where the self-check runs).
/// </summary>
public static partial class MacGamepad
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Framework = "/System/Library/Frameworks/GameController.framework/GameController";
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr Send(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nuint SendCount(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte SendBool(IntPtr receiver, IntPtr selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial float SendFloat(IntPtr receiver, IntPtr selector);

    private static bool? _loaded;
    private static PadState? _forced;
    private static bool _forcedSet;
    /// <summary>For the self-check: a controller as if one were paired (null: none), whatever this machine has.</summary>
    public static void Override(PadState? state) { _forced = state; _forcedSet = true; }
    public static void StopOverride() => _forcedSet = false;

    /// <summary>The first controller with a full set of buttons, as read now; null when none is connected.</summary>
    public static PadState? Read()
    {
        if (_forcedSet) return _forced;
        if (!OperatingSystem.IsMacOS() || !Load()) return null;
        try
        {
            var controllers = Send(objc_getClass("GCController"), sel_registerName("controllers"));
            if (controllers == IntPtr.Zero) return null;
            var count = SendCount(controllers, sel_registerName("count"));
            for (nuint i = 0; i < count; i++)
            {
                var pad = Send(SendIndex(controllers, sel_registerName("objectAtIndex:"), i), sel_registerName("extendedGamepad"));
                if (pad == IntPtr.Zero) continue;
                var buttons = Pad.None;
                var cross = Send(pad, sel_registerName("dpad"));
                if (Pressed(cross, "up")) buttons |= Pad.Up;
                if (Pressed(cross, "down")) buttons |= Pad.Down;
                if (Pressed(cross, "left")) buttons |= Pad.Left;
                if (Pressed(cross, "right")) buttons |= Pad.Right;
                foreach (var (name, button) in new[] { ("buttonA", Pad.A), ("buttonB", Pad.B), ("buttonX", Pad.X), ("buttonY", Pad.Y), ("buttonMenu", Pad.Menu), ("buttonOptions", Pad.View), ("leftShoulder", Pad.LeftShoulder), ("rightShoulder", Pad.RightShoulder) })
                    if (Pressed(pad, name)) buttons |= button;
                var stick = Send(pad, sel_registerName("leftThumbstick"));
                return new PadState(buttons, Axis(stick, "xAxis"), Axis(stick, "yAxis"));
            }
            return null;
        }
        // A framework that answers otherwise than documented: no controller, never a crash.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return null; }
    }

    /// <summary>The element's own button (dpad.up, buttonA…), pressed; false when this controller has none (buttonOptions is optional).</summary>
    private static bool Pressed(IntPtr owner, string element)
    {
        if (owner == IntPtr.Zero) return false;
        var input = Send(owner, sel_registerName(element));
        return input != IntPtr.Zero && SendBool(input, sel_registerName("isPressed")) != 0;
    }
    private static double Axis(IntPtr stick, string axis)
    {
        if (stick == IntPtr.Zero) return 0;
        var input = Send(stick, sel_registerName(axis));
        return input == IntPtr.Zero ? 0 : SendFloat(input, sel_registerName("value"));
    }
    /// <summary>The framework, loaded once: its classes exist only once it is.</summary>
    private static bool Load() => _loaded ??= NativeLibrary.TryLoad(Framework, out _);
}
