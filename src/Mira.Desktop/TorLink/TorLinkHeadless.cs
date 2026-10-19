using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Mira.Core.TorLink;

namespace Mira.Desktop.TorLink;

/// <summary>
/// TorLink without a window, for « Télécharger sur le PC serveur » (<see cref="TorLinkRemote"/>): « torlnk serve » in a
/// hidden pseudo console, as the TorLink of the page runs (Ctrl+C lets it save its queue, its job object ends it with
/// Mira), and « torlnk search », which touches no download. The same queue as the TorLink of the page: MainWindow never
/// runs both at once.
/// </summary>
internal sealed class TorLinkHeadless : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<int> _searches = [];
    private readonly StringBuilder _tail = new();
    private PseudoConsole? _console;
    private TorLinkServeClient? _client;

    /// <summary>TorLink's API while it runs and answers.</summary>
    public TorLinkServeClient? Client => Running ? _client : null;
    public bool Running => _console is { HasExited: false };
    /// <summary>Started, then ended on its own (an error): <see cref="StopAsync"/> lets its port and token go.</summary>
    public bool Ended => _console is { HasExited: true };
    /// <summary>Why the last start failed, in words.</summary>
    public string? Problem { get; private set; }
    /// <summary>The TorLink processes Mira runs here: not « another TorLink » for the page's.</summary>
    public IReadOnlyCollection<int> ProcessIds
    {
        get
        {
            lock (_gate) return _console is { HasExited: false } console ? [.. _searches, console.ProcessId] : [.. _searches];
        }
    }

    /// <summary>Starts « torlnk serve » on a free port of 127.0.0.1 behind a new token; true once it answers.</summary>
    public async Task<bool> StartAsync(TorLinkInstallation installation, CancellationToken ct)
    {
        if (Running && _client is not null) return true;
        await StopAsync();
        var port = FreePort();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var environment = new Dictionary<string, string>(TorLinkTerminal.ChildEnvironment(installation.Managed), StringComparer.OrdinalIgnoreCase) { [TorLinkRemote.TokenVariable] = token };
        lock (_tail) _tail.Clear();
        try
        {
            var console = PseudoConsole.Start(installation.Node, TorLinkRemote.ServeArguments(installation.Entry, port), installation.Root, environment, 120, 30, Keep);
            lock (_gate) _console = console;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Problem = L("TorLink n’a pas pu démarrer : ", "TorLink couldn’t start: ") + ex.Message;
            return false;
        }
        var client = _client = new TorLinkServeClient(port, token);
        var until = DateTime.UtcNow + TorLinkRemote.ServeStart;
        while (Running && DateTime.UtcNow < until)
        {
            if (await client.HealthyAsync(ct)) { Problem = null; return true; }
            await Task.Delay(500, ct);
        }
        Problem = LastLine() is { } line ? L($"TorLink ne répond pas : « {line} ».", $"TorLink isn’t answering: “{line}”.") : L("TorLink ne répond pas.", "TorLink isn’t answering.");
        await StopAsync();
        return false;
    }

    /// <summary>Asks TorLink to quit (Ctrl+C: it saves its queue and its sharing), then ends it after a short grace period.</summary>
    public async Task StopAsync()
    {
        PseudoConsole? console;
        lock (_gate) { console = _console; _console = null; }
        _client?.Dispose(); _client = null;
        if (console is null) return;
        await console.StopAsync(TimeSpan.FromSeconds(5));
        console.Dispose();
    }

    /// <summary>
    /// « torlnk search » in one category: its results, or why there are none. Stopped after
    /// <see cref="TorLinkRemote.SearchTimeout"/>, or when it prints more than <see cref="TorLinkRemote.MaxSearchOutput"/>.
    /// </summary>
    public async Task<(List<AgentHit> Hits, string? Error)> SearchAsync(TorLinkInstallation installation, string query, string category, CancellationToken ct)
    {
        var start = new ProcessStartInfo(installation.Node)
        {
            WorkingDirectory = installation.Root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in TorLinkRemote.SearchArguments(installation.Entry, query, category)) start.ArgumentList.Add(argument);
        foreach (var (key, value) in TorLinkTerminal.ChildEnvironment(installation.Managed)) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { return ([], L("TorLink n’a pas pu chercher : ", "TorLink couldn’t search: ") + ex.Message); }
        lock (_gate) _searches.Add(process.Id);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TorLinkRemote.SearchTimeout);
        var errors = ReadAsync(process.StandardError, 64 * 1024, limit.Token);
        try
        {
            var output = await ReadAsync(process.StandardOutput, TorLinkRemote.MaxSearchOutput, limit.Token);
            if (output is null) return ([], L("TorLink a répondu trop longuement.", "TorLink’s answer was too long."));
            await process.WaitForExitAsync(limit.Token);
            var (hits, error) = TorLinkRemote.ParseSearch(output);
            // Nothing on its output: its error message is the last line of its error output.
            if (output.Trim().Length == 0 && process.ExitCode != 0 && (await errors)?.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) is { } line)
                error = L("TorLink n’a pas pu chercher : ", "TorLink couldn’t search: ") + (line.Length > 160 ? line[..160] + "…" : line);
            return (hits, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return ([], L("TorLink n’a pas répondu à temps.", "TorLink didn’t answer in time.")); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            lock (_gate) _searches.Remove(process.Id);
            // Its error output ends with it: read to the end, so that nothing is left running.
            try { await errors; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        }
    }

    /// <summary>Everything up to <paramref name="most"/> characters; null beyond.</summary>
    private static async Task<string?> ReadAsync(StreamReader reader, int most, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[16 * 1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (text.Length + read > most) return null;
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    private void Keep(string output)
    {
        lock (_tail)
        {
            _tail.Append(output);
            if (_tail.Length > 4096) _tail.Remove(0, _tail.Length - 4096);
        }
    }

    /// <summary>The last line TorLink printed, without terminal control sequences.</summary>
    private string? LastLine()
    {
        string text;
        lock (_tail) text = _tail.ToString();
        var plain = System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-_]|[\x00-\x08\x0b-\x1f\x7f]", "");
        var line = plain.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
        return line is null ? null : line.Length > 160 ? line[..160] + "…" : line;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        lock (_gate) { _console?.Dispose(); _console = null; }
        _client?.Dispose(); _client = null;
    }
}
