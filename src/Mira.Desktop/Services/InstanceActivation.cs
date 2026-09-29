using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>A shortcut opened twice restores the same profile; the pipe accepts only its Windows user.</summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    public InstanceActivation(string key, Action activate) => _ = ListenAsync(key, activate);
    private async Task ListenAsync(string key, Action activate)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("Mira-" + key, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(2000);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString());
                if (await reader.ReadLineAsync(timeout.Token) == "show") activate();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            { if (!_stop.IsCancellationRequested) await Task.Delay(250); }
        }
    }
    public static bool ShowExisting(string key)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "Mira-" + key, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var timeout = new CancellationTokenSource(1500);
            if (!uint.TryParse(reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult(), out var process)) return false;
            AllowSetForegroundWindow(process);
            writer.WriteLine("show"); return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() => _stop.Cancel();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}
