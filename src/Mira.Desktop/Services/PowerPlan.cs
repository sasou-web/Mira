using System.Runtime.InteropServices;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// Windows' active power plan on mains power (<see cref="ServerSleep"/>): read through powrprof without administrator
/// rights; « Pas de veille sur secteur » set by powercfg in the person's rights, as Windows' settings do, then read back.
/// </summary>
internal static class PowerPlan
{
    private static readonly Guid SleepGroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20"), StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"),
        HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364"), ButtonsGroup = new("4f971e89-eebd-4455-a8de-9e59040e7347"), LidClose = new("5ca83367-6e45-459f-a27b-476b1d01c936");

    /// <summary>Null when Windows does not say.</summary>
    public static ServerSleep.Plan? Read()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero) return null;
            Guid scheme;
            try { scheme = Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
            if (OnMains(scheme, SleepGroup, StandbyIdle) is not { } standby) return null;
            var lid = LidPresent() ? OnMains(scheme, ButtonsGroup, LidClose) : null;
            // 128: no system battery; 255: unknown.
            var battery = GetSystemPowerStatus(out var status) && status.BatteryFlag is not (128 or 255);
            return new(standby, OnMains(scheme, SleepGroup, HibernateIdle) ?? 0, lid, battery);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    /// <summary>
    /// « Pas de veille sur secteur »: no sleep nor hibernation after idle time on mains power, and, for a laptop, nothing
    /// when its lid closes on mains power. Battery settings stay. True once read back so.
    /// </summary>
    public static bool NeverOnMains()
    {
        JellyfinAutostart.RunSystem("powercfg.exe", "/change standby-timeout-ac 0");
        JellyfinAutostart.RunSystem("powercfg.exe", "/change hibernate-timeout-ac 0");
        if (LidPresent())
        {
            JellyfinAutostart.RunSystem("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 0");
            JellyfinAutostart.RunSystem("powercfg.exe", "/setactive SCHEME_CURRENT");
        }
        return Read() is { } plan && !ServerSleep.Sleeps(plan);
    }

    private static int? OnMains(Guid scheme, Guid group, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref group, ref setting, out var value) == 0 ? (int)Math.Min(value, int.MaxValue) : null;

    /// <summary>SYSTEM_POWER_CAPABILITIES.LidPresent, its third BOOLEAN.</summary>
    private static bool LidPresent()
    {
        var capabilities = Marshal.AllocHGlobal(256);
        try
        {
            for (var i = 0; i < 256; i++) Marshal.WriteByte(capabilities, i, 0);
            return GetPwrCapabilities(capabilities) && Marshal.ReadByte(capabilities, 2) != 0;
        }
        finally { Marshal.FreeHGlobal(capabilities); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")][return: MarshalAs(UnmanagedType.U1)] private static extern bool GetPwrCapabilities(IntPtr capabilities);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
