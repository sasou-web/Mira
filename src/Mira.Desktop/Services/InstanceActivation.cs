using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Mira.Desktop.Services;

/// <summary>What the Mira already open on a profile says to a second launch of it; None when nothing answers.</summary>
internal enum ActivationAnswer { None, Shown, Starting, Closing }

/// <summary>
/// A shortcut opened twice restores the same profile, and a mira:// link opened while Mira is open goes to it (« show »
/// then the link on the same line); the pipe accepts only its Windows user.
/// </summary>
internal sealed class InstanceActivation : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    /// <summary><paramref name="activate"/> runs on the pipe's thread, must not throw, and says what was done.</summary>
    public InstanceActivation(string key, Func<string?, ActivationAnswer> activate) => _ = ListenAsync(key, activate);
    private async Task ListenAsync(string key, Func<string?, ActivationAnswer> activate)
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
                var asked = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (asked is null || asked != "show" && !asked.StartsWith("show ", StringComparison.Ordinal)) continue;
                await writer.WriteLineAsync(activate(asked.Length > 5 ? asked[5..] : null) switch { ActivationAnswer.Starting => "starting", ActivationAnswer.Closing => "closing", _ => "shown" }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            { if (!_stop.IsCancellationRequested) await Task.Delay(250).ConfigureAwait(false); }
        }
    }
    public static ActivationAnswer ShowExisting(string key, string? link = null)
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
            // Mira 0.7.8 and older ignore a line with a link: their window stays where it is, nothing else happens.
            writer.WriteLine(link is null ? "show" : "show " + link.ReplaceLineEndings(""));
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
