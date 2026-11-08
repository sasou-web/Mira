using System.Diagnostics;
using System.Net;

namespace Mira.Mac.Checks;

/// <summary>
/// For the self-check: the stand-in Jellyfin's videos over a real connection (Videos/{id}/stream on 127.0.0.1:8096,
/// where Mira asks for them when PlaybackInfo gives no file of this Mac), sent a little faster than they play, as a
/// Jellyfin away from home would. <see cref="Hang"/> makes every connection open at that moment stop sending without
/// closing, as one does when Jellyfin's PC goes off or the Mac leaves its Wi-Fi: mpv waits for data that never comes.
/// The connections opened after it are served again.
/// </summary>
public sealed class StreamServer : IDisposable
{
    public const string Prefix = "http://127.0.0.1:8096/";
    /// <summary>Bytes a second: a little more than the 30 s test film's (about 330 kB/s), so mpv's cache stays short.</summary>
    private const int Rate = 420_000;
    private readonly HttpListener _listener = new();
    private readonly MockJellyfin _mock;
    private readonly CancellationTokenSource _closing = new();
    /// <summary>Raised by <see cref="Hang"/>: the responses under way when it was are never sent further.</summary>
    private int _cut;
    private int _streams;
    /// <summary>The connections mpv opened for a video.</summary>
    public int Streams => _streams;
    private readonly List<string> _log = [];
    /// <summary>The last requests: their range, the bytes sent and how each ended, said when a check fails.</summary>
    public string Log(int last = 24) { lock (_log) return string.Join(" | ", _log.TakeLast(last)); }
    private void Note(string line) { lock (_log) _log.Add(line); }

    private StreamServer(MockJellyfin mock)
    {
        _mock = mock;
        _listener.Prefixes.Add(Prefix);
        _listener.Start();
        _ = AcceptAsync();
    }
    /// <summary>Listening on 127.0.0.1:8096, or null (the port taken): the checks that need it say so.</summary>
    public static StreamServer? TryStart(MockJellyfin mock)
    {
        try { return new StreamServer(mock); }
        catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException) { return null; }
    }
    /// <summary>Every connection open now stops sending, and stays open.</summary>
    public void Hang() { Interlocked.Increment(ref _cut); Note("— coupure —"); }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var asked = context.Request; var answer = context.Response;
        var cut = Volatile.Read(ref _cut);
        int number = 0; string asking = "", end = "fin"; long sent = 0;
        try
        {
            var parts = asked.Url!.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 3 || parts[0] != "Videos" || parts[2] != "stream") { answer.StatusCode = 404; return; }
            number = Interlocked.Increment(ref _streams);
            asking = asked.Headers["Range"] ?? "-"; Note($"#{number} {asking} ouvert");
            var file = _mock.FileOf(parts[1]);
            var length = new FileInfo(file).Length;
            long from = 0, to = length - 1;
            if (asked.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var bounds = range[6..].Split('-');
                if (bounds[0].Length > 0) { from = long.Parse(bounds[0]); if (bounds[1].Length > 0) to = Math.Min(long.Parse(bounds[1]), length - 1); }
                else from = Math.Max(0, length - long.Parse(bounds[1]));
                if (from >= length) { answer.StatusCode = 416; return; }
                answer.StatusCode = 206;
                answer.AddHeader("Content-Range", $"bytes {from}-{to}/{length}");
            }
            answer.ContentType = "video/x-matroska";
            answer.AddHeader("Accept-Ranges", "bytes");
            answer.ContentLength64 = to - from + 1;
            if (asked.HttpMethod == "HEAD") return;
            await using var stream = File.OpenRead(file);
            stream.Position = from;
            var buffer = new byte[Rate / 10];
            var left = to - from + 1;
            // Paced by the clock, not by pauses between pieces: on the Mac runner, busy drawing the film in software,
            // each pause took far longer than asked, and the film came at a tenth of its rate.
            var started = Stopwatch.StartNew();
            while (left > 0)
            {
                // Cut: nothing more on this connection, never closed either (mpv's own timeout aside).
                if (Volatile.Read(ref _cut) != cut) { end = "coupé"; Note($"#{number} {asked.HttpMethod} {asking} {sent / 1000} ko coupé"); await Task.Delay(Timeout.Infinite, _closing.Token); return; }
                var due = (long)(Rate * started.Elapsed.TotalSeconds) + buffer.Length - sent;
                if (due <= 0) { await Task.Delay(20, _closing.Token); continue; }
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(Math.Min(buffer.Length, due), left)), _closing.Token);
                if (read == 0) break;
                await answer.OutputStream.WriteAsync(buffer.AsMemory(0, read), _closing.Token);
                left -= read; sent += read;
            }
        }
        // mpv closing a connection it no longer needs (a seek, the film stopped), or the check ending.
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or FormatException) { end = ex.GetType().Name; }
        finally
        {
            if (number > 0 && end != "coupé") Note($"#{number} {asked.HttpMethod} {asking} {sent / 1000} ko {end}");
            try { answer.Close(); }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
        try { _listener.Stop(); _listener.Close(); }
        catch (ObjectDisposedException) { }
        _closing.Dispose();
    }
}
