using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mira.Desktop;

/// <summary>
/// Filming Mira (--tour, never merged): every clock of this process slowed by a factor (tools/tour/dilate.c, a DLL
/// named by MIRA_DILATE_DLL), so that a runner without a graphics card draws every moment of an animation, filmed in
/// real time and played back that many times faster. WPF's animations are then timed by the wall clock (its
/// TimeManager's GTCClock, which reads DateTime.Now, dilated), not by the screen's refreshes, which stay real.
/// </summary>
internal static class TimeDilation
{
    private static IntPtr _library;
    public static bool Loaded => _library != IntPtr.Zero;
    public static double Factor { get; private set; } = 1;
    public static string Report { get; private set; } = "";

    /// <summary>Loads the DLL if MIRA_DILATE_DLL names one, the clocks still real; true when it is there.</summary>
    public static bool Load()
    {
        if (Loaded) return true;
        var path = Environment.GetEnvironmentVariable("MIRA_DILATE_DLL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        _library = NativeLibrary.Load(path);
        var hooked = Dilate(1);
        Report = $"{hooked} hooked\n" + Marshal.PtrToStringAnsi(Describe());
        return hooked > 0;
    }

    /// <summary>From now on the clocks run <paramref name="factor"/> times slower than the wall's (1 = real time).</summary>
    public static void Set(double factor)
    {
        if (!Loaded) return;
        Dilate(factor); Factor = factor;
    }

    /// <summary>The wall's time, never dilated.</summary>
    public static DateTime RealUtc => Loaded ? DateTime.FromFileTimeUtc(RealFileTime()) : DateTime.UtcNow;

    /// <summary>WPF's animations on the wall clock (dilated), before any of them starts.</summary>
    public static void TimeWpfByTheClock(Dispatcher dispatcher)
    {
        var core = typeof(System.Windows.Media.Visual).Assembly;
        var context = core.GetType("System.Windows.Media.MediaContext", true)!.GetMethod("From", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Dispatcher)])!.Invoke(null, [dispatcher])!;
        var manager = context.GetType().GetProperty("TimeManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        var clockType = manager.GetType().GetNestedType("GTCClock", BindingFlags.NonPublic)!;
        var clock = Activator.CreateInstance(clockType, nonPublic: true)!;
        var type = manager.GetType();
        type.GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(manager, null);
        type.GetProperty("Clock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(manager, clock);
        type.GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(manager, null);
    }

    [DllImport("mira-dilate", EntryPoint = "MiraDilate")] private static extern int Dilate(double factor);
    [DllImport("mira-dilate", EntryPoint = "MiraRealFileTime")] private static extern long RealFileTime();
    [DllImport("mira-dilate", EntryPoint = "MiraDilateReport")] private static extern IntPtr Describe();
}
