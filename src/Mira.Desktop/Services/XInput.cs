using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// An Xbox controller (or one that says it is: most PC controllers, PlayStation's through Steam), read through Windows'
/// XInput for « Mode canapé » (<see cref="Couch"/>). Windows gives a controller's state to whichever program asks:
/// Mira reads it only while its window is in front.
/// </summary>
public static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Gamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct State { public uint PacketNumber; public Gamepad Gamepad; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState(uint user, out State state);
    private const uint Connected = 0;
    private static bool _missing;

    /// <summary>The first of the four controllers Windows knows that is connected, as read now; null when none is.</summary>
    public static PadState? Read()
    {
        if (_missing) return null;
        try
        {
            for (uint user = 0; user < 4; user++)
                if (GetState(user, out var state) == Connected)
                    return Couch.FromXInput(state.Gamepad.Buttons, state.Gamepad.ThumbLX, state.Gamepad.ThumbLY);
        }
        // Windows without XInput 1.4 (it has been there since Windows 8): no controller, asked again never.
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _missing = true; }
        return null;
    }
}
