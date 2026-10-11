using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>What the Mira already open on a profile says to a second launch of it; None when nothing answers.</summary>
internal enum ActivationAnswer { None, Shown, Starting, Closing }

/// <summary>A shortcut opened twice restores the same profile; the pipe accepts only its Windows user.</summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    /// <summary><paramref name="activate"/> runs on the pipe's thread, must not throw, and says what was done.</summary>
    public InstanceActivation(string key, Func<ActivationAnswer> activate) => _ = ListenAsync(key, activate);
    private async Task ListenAsync(string key, Func<ActivationAnswer> activate)
    {
        // Off the interface thread, busy while Mira starts: a launch meanwhile is answered within its time limit.
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("Mira-" + key, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(2000);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString()).ConfigureAwait(false);
                if (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) != "show") continue;
                await writer.WriteLineAsync(activate() switch { ActivationAnswer.Starting => "starting", ActivationAnswer.Closing => "closing", _ => "shown" }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            { if (!_stop.IsCancellationRequested) await Task.Delay(250).ConfigureAwait(false); }
        }
    }
    public static ActivationAnswer ShowExisting(string key)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "Mira-" + key, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(1500);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var timeout = new CancellationTokenSource(1500);
            if (!uint.TryParse(reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult(), out var process)) return ActivationAnswer.None;
            AllowSetForegroundWindow(process);
            writer.WriteLine("show");
            // Mira 0.7.4 and older answer nothing: their window came forward.
            string? answer = null;
            try { using var reply = new CancellationTokenSource(2000); answer = reader.ReadLineAsync(reply.Token).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            return answer switch { "starting" => ActivationAnswer.Starting, "closing" => ActivationAnswer.Closing, _ => ActivationAnswer.Shown };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return ActivationAnswer.None; }
    }
    public void Dispose() => _stop.Cancel();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
}
