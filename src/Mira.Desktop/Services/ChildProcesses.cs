using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>
/// The programs Mira starts and keeps running beside it (mira-tailscale.exe): put in one job object that Mira holds
/// until it exits, so that Windows ends them with Mira, even after a crash or when Mira is ended from the Task Manager.
/// </summary>
internal static class ChildProcesses
{
    private static readonly Lazy<IntPtr> Job = new(() =>
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception();
        var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = new Win32Exception();
            CloseHandle(job);
            throw error;
        }
        // Never closed: Windows closes it when Mira's process ends, and the job's programs end with it.
        return job;
    });

    /// <summary>Ties a program just started to Mira's life. A program that already ended is left as it is.</summary>
    public static void BindToMira(Process process)
    {
        if (!AssignProcessToJobObject(Job.Value, process.Handle) && !process.HasExited) throw new Win32Exception();
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
