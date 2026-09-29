using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Mira.Desktop.TorLink;

/// <summary>
/// A console program running in a Windows pseudo console (ConPTY), the mechanism behind Windows Terminal and
/// the VS Code terminal. Output arrives as UTF-8 text with VT sequences; input goes back the same way.
/// The program and everything it starts belong to a job object: they end with Mira, even after a crash.
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private readonly IntPtr _console, _process, _job;
    private readonly SafeFileHandle _inputWrite, _outputRead;
    private readonly FileStream _input;
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _exitWait;
    private readonly ProcessWait _exitEvent;
    private int _disposed;
    /// <summary>Terminal output, on the reader thread.</summary>
    public event Action<string>? Output;
    /// <summary>Exit code, on a thread-pool thread.</summary>
    public event Action<int>? Exited;
    public int ProcessId { get; }
    public bool HasExited => _exit.Task.IsCompleted;
    public Task<int> Completion => _exit.Task;

    private PseudoConsole(IntPtr console, IntPtr process, IntPtr job, int processId, SafeFileHandle inputWrite, SafeFileHandle outputRead, Action<string>? output, Action<int>? exited)
    {
        _console = console; _process = process; _job = job; ProcessId = processId; _inputWrite = inputWrite; _outputRead = outputRead;
        // Attached before reading and waiting start: neither the first output nor a very early exit can be missed.
        if (output is not null) Output += output;
        if (exited is not null) Exited += exited;
        _input = new FileStream(_inputWrite, FileAccess.Write, 1);
        new Thread(ReadOutput) { IsBackground = true, Name = "TorLink output" }.Start();
        _ = PumpInputAsync();
        _exitEvent = new ProcessWait(_process);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) =>
        {
            var code = GetExitCodeProcess(_process, out var value) ? unchecked((int)value) : -1;
            if (_exit.TrySetResult(code)) Exited?.Invoke(code);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>
    /// Starts <paramref name="application"/> in a new pseudo console of the given size. <paramref name="output"/> and
    /// <paramref name="exited"/> receive everything from the very start (Output and Exited can be subscribed later too).
    /// </summary>
    public static PseudoConsole Start(string application, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment, short columns, short rows,
        Action<string>? output = null, Action<int>? exited = null)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) { inputRead.Dispose(); inputWrite.Dispose(); throw new Win32Exception(); }
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero, block = IntPtr.Zero, job = IntPtr.Zero;
        var info = default(ProcessInformation);
        try
        {
            var result = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inputRead, outputWrite, 0, out console);
            if (result != 0) throw new Win32Exception(result);
            // The pseudo console keeps its own duplicates of these ends.
            inputRead.Dispose(); outputWrite.Dispose();

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PseudoConsoleAttribute, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = UseStdHandles }, lpAttributeList = attributes };
            // Null standard handles with STARTF_USESTDHANDLES: the child opens the pseudo console rather than inheriting Mira's.
            block = Marshal.StringToHGlobalUni(string.Concat(environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value + "\0")) + "\0");
            var command = new StringBuilder(string.Join(' ', new[] { application }.Concat(arguments).Select(Quote)));
            if (!CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfo | UnicodeEnvironment | Suspended, block, workingDirectory, ref startup, out info))
                throw new Win32Exception();
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()) || !AssignProcessToJobObject(job, info.hProcess))
                throw new Win32Exception();
            ResumeThread(info.hThread); CloseHandle(info.hThread);
            var started = new PseudoConsole(console, info.hProcess, job, info.dwProcessId, inputWrite, outputRead, output, exited);
            console = IntPtr.Zero; job = IntPtr.Zero; info = default;
            return started;
        }
        catch
        {
            if (info.hProcess != IntPtr.Zero) { TerminateProcess(info.hProcess, 1); CloseHandle(info.hProcess); CloseHandle(info.hThread); }
            if (job != IntPtr.Zero) CloseHandle(job);
            if (console != IntPtr.Zero) ClosePseudoConsole(console);
            inputRead.Dispose(); outputWrite.Dispose(); inputWrite.Dispose(); outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (block != IntPtr.Zero) Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>Queues keyboard input (UTF-8 text and VT sequences); never blocks the caller.</summary>
    public void Write(string text) { if (text.Length > 0 && _disposed == 0) _writes.Writer.TryWrite(Encoding.UTF8.GetBytes(text)); }

    public void Resize(short columns, short rows)
    {
        if (_disposed == 0 && !HasExited) ResizePseudoConsole(_console, new Coord { X = columns, Y = rows });
    }

    /// <summary>Asks the program to quit with Ctrl+C, then ends the whole job after <paramref name="grace"/>.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (HasExited) return;
        Write("\u0003");
        if (await Task.WhenAny(_exit.Task, Task.Delay(grace)) != _exit.Task) Kill();
        await Task.WhenAny(_exit.Task, Task.Delay(1000));
    }

    public void Kill() { if (_disposed == 0) TerminateJobObject(_job, 1); }

    private void ReadOutput()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[16384]; var chars = new char[16384 + 4];
        try
        {
            using var stream = new FileStream(_outputRead, FileAccess.Read, 1);
            int read;
            while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                if (count > 0) Output?.Invoke(new string(chars, 0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await foreach (var bytes in _writes.Reader.ReadAllAsync())
            {
                // Pipe writes are synchronous; this loop runs on the thread pool, never on the interface.
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writes.Writer.TryComplete();
        if (!HasExited) TerminateJobObject(_job, 1);
        _exitWait.Unregister(null);
        // Closing the pseudo console can wait for its output to drain: the reader thread keeps reading meanwhile.
        var console = _console;
        _ = Task.Run(() =>
        {
            ClosePseudoConsole(console);
            try { _input.Dispose(); } catch (IOException) { }
            _inputWrite.Dispose(); _outputRead.Dispose();
            _exitEvent.Dispose();
            CloseHandle(_process); CloseHandle(_job);
        });
    }

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules): backslashes only double before a quote.</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { builder.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            builder.Append('\\', backslashes).Append(c); backslashes = 0;
        }
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(IntPtr process) => SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
    }

    private const int PseudoConsoleAttribute = 0x00020016, ExtendedLimitInformation = 9, UseStdHandles = 0x100;
    private const uint ExtendedStartupInfo = 0x00080000, UnicodeEnvironment = 0x00000400, Suspended = 0x00000004, KillOnJobClose = 0x2000;
    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
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
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
