using System.IO;
using System.Security;
using Microsoft.Win32;
using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>
/// The Run key's value of <see cref="SignInStart"/>, in the person's own registry (no Windows authorization), and Windows'
/// Startup apps choice for it, read only. Every failure is « not written »: said in Réglages, tried again a minute later.
/// </summary>
internal static class SignInLaunch
{
    public static string? Current()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(SignInStart.RunKey); return key?.GetValue(SignInStart.Name) as string; }
        catch (Exception ex) when (Failed(ex)) { return null; }
    }

    /// <summary>The value after the step: what is now in the key.</summary>
    public static string? Apply(SignInStart.Step step, string executable)
    {
        try
        {
            if (step == SignInStart.Step.Write)
            {
                using var key = Registry.CurrentUser.CreateSubKey(SignInStart.RunKey, writable: true);
                key.SetValue(SignInStart.Name, SignInStart.Command(executable), RegistryValueKind.String);
            }
            else if (step == SignInStart.Step.Remove)
            {
                using var key = Registry.CurrentUser.OpenSubKey(SignInStart.RunKey, writable: true);
                key?.DeleteValue(SignInStart.Name, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (Failed(ex)) { }
        return Current();
    }

    public static bool TurnedOffInWindows()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(SignInStart.ApprovedKey); return SignInStart.TurnedOffInWindows(key?.GetValue(SignInStart.Name) as byte[]); }
        catch (Exception ex) when (Failed(ex)) { return false; }
    }

    /// <summary>
    /// The command line Windows relaunches Mira with after a restart it made (Windows Update, <c>shutdown /g</c>):
    /// <see cref="SignInStart.Flag"/> while Mira is the one Windows starts at sign-in, so that it comes back by the clock;
    /// never after a crash, a hang or an update of Mira itself (its installer reopens it).
    /// </summary>
    public static void RestartByTheClock(bool on)
    {
        try { _ = on ? RegisterApplicationRestart(SignInStart.Flag, RestartNoCrash | RestartNoHang | RestartNoPatch) : UnregisterApplicationRestart(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }
    private const int RestartNoCrash = 1, RestartNoHang = 2, RestartNoPatch = 4;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int RegisterApplicationRestart(string commandLine, int flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern int UnregisterApplicationRestart();

    private static bool Failed(Exception ex) => ex is UnauthorizedAccessException or SecurityException or IOException;
}
