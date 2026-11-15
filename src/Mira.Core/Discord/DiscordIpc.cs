using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Mira.Core.Discord;

/// <summary>
/// The local pipe of the Discord app of this computer, by which it takes a Rich Presence (« RPC », its IPC transport):
/// a named pipe \\.\pipe\discord-ipc-0 to -9 on Windows, a Unix socket discord-ipc-0 to -9 in the temporary folder on
/// the Mac and Linux. Frames: an opcode and a length (two little-endian int32), then JSON. The handshake names
/// Discord's application (<see cref="DiscordPresence.ApplicationId"/>); SET_ACTIVITY then sets the card, null clears it.
/// No library, nothing on the network: what Discord does with the card is Discord's.
/// </summary>
public sealed class DiscordIpc : IAsyncDisposable
{
    private const int Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4;
    private const int Largest = 1 << 20;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly Task _reading;
    private int _nonce;

    /// <summary>The pipe broke or Discord closed it (Discord quit, or refused the application): connect again later.</summary>
    public bool Closed { get; private set; }
    /// <summary>What Discord said last of a card it refused (an ERROR event), or why it closed; null while all is well.</summary>
    public string? Error { get; private set; }
    /// <summary>Raised once when the pipe breaks or Discord closes it, on the reading thread.</summary>
    public event Action? Broken;

    private DiscordIpc(Stream stream)
    {
        _stream = stream;
        _reading = Task.Run(ReadAsync);
    }

    /// <summary>
    /// The paths the Discord app may listen at, in order. <c>MIRA_DISCORD_IPC</c> names the one to use instead (the checks'
    /// stand-in Discord).
    /// </summary>
    public static IEnumerable<string> Paths()
    {
        if (Environment.GetEnvironmentVariable("MIRA_DISCORD_IPC") is { Length: > 0 } own) { yield return own; yield break; }
        if (OperatingSystem.IsWindows())
        {
            for (var i = 0; i < 10; i++) yield return $"discord-ipc-{i}";
            yield break;
        }
        var folders = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }.Select(Environment.GetEnvironmentVariable).OfType<string>().Where(x => x.Length > 0).Append("/tmp").Distinct().ToList();
        foreach (var folder in folders)
            foreach (var sub in new[] { "", "app/com.discordapp.Discord", "snap.discord", ".flatpak/com.discordapp.Discord/xdg-run" })
                for (var i = 0; i < 10; i++) yield return Path.Combine(folder, sub, $"discord-ipc-{i}");
    }

    /// <summary>Whether a Discord app listens on this computer, without connecting to it.</summary>
    public static bool Available()
    {
        try
        {
            if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("MIRA_DISCORD_IPC") is not { Length: > 0 })
                return Directory.EnumerateFiles(@"\\.\pipe\").Any(x => Path.GetFileName(x).StartsWith("discord-ipc-", StringComparison.Ordinal));
            return Paths().Any(x => OperatingSystem.IsWindows() || File.Exists(x));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The first Discord that answers the handshake, or null when none does (Discord closed, or another user's).</summary>
    public static async Task<DiscordIpc?> ConnectAsync(string applicationId, CancellationToken ct = default)
    {
        foreach (var path in Paths())
        {
            ct.ThrowIfCancellationRequested();
            Stream? stream = null;
            try
            {
                stream = await OpenAsync(path, ct);
                if (stream is null) continue;
                await WriteFrameAsync(stream, Handshake, new JsonObject { ["v"] = 1, ["client_id"] = applicationId }, ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                var (op, body) = await ReadFrameAsync(stream, wait.Token);
                if (op == Frame && (body as JsonObject)?["evt"]?.GetValue<string>() == "READY") { var ipc = new DiscordIpc(stream); stream = null; return ipc; }
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException && !ct.IsCancellationRequested) { }
            finally { if (stream is not null) await stream.DisposeAsync(); }
        }
        return null;
    }

    private static async Task<Stream?> OpenAsync(string path, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(250, ct); return pipe; }
            catch { await pipe.DisposeAsync(); throw; }
        }
        if (!File.Exists(path)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Sets the card (<paramref name="activity"/>), or clears it (null). False when the pipe broke on the way.</summary>
    public async Task<bool> SetActivityAsync(JsonObject? activity, CancellationToken ct = default)
    {
        if (Closed) return false;
        var command = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity?.DeepClone() },
            ["nonce"] = Interlocked.Increment(ref _nonce).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await _write.WaitAsync(ct);
        try { await WriteFrameAsync(_stream, Frame, command, ct); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { Lost(ex.Message); return false; }
        finally { _write.Release(); }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                var (op, body) = await ReadFrameAsync(_stream, _life.Token);
                var evt = (body as JsonObject)?["evt"]?.GetValue<string>();
                var cmd = (body as JsonObject)?["cmd"]?.GetValue<string>();
                if (op == Ping)
                {
                    await _write.WaitAsync(_life.Token);
                    try { await WriteFrameAsync(_stream, Pong, body ?? new JsonObject(), _life.Token); } finally { _write.Release(); }
                }
                else if (op == Close) { Lost((body as JsonObject)?["message"]?.GetValue<string>() ?? "closed"); return; }
                else if (op == Frame && evt == "ERROR") Error = (body as JsonObject)?["data"]?["message"]?.GetValue<string>() ?? "error";
                else if (op == Frame && cmd == "SET_ACTIVITY") Error = null;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (!_life.IsCancellationRequested) Lost(ex.Message);
        }
    }

    private void Lost(string why)
    {
        if (Closed) return;
        Closed = true; Error ??= why;
        Broken?.Invoke();
    }

    private static async Task WriteFrameAsync(Stream stream, int op, JsonNode body, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(body.ToJsonString());
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(int Op, JsonNode? Body)> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > Largest) throw new InvalidDataException("frame of " + length);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    /// <summary>Clears the card, then closes the pipe: Discord takes the card away at once (it would anyway when Mira's process ends).</summary>
    public async ValueTask DisposeAsync()
    {
        if (!Closed)
            try
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SetActivityAsync(null, quick.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
        Closed = true;
        await _life.CancelAsync();
        await _stream.DisposeAsync();
        try { await _reading; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        _life.Dispose(); _write.Dispose();
    }
}
