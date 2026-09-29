using System.Net;

namespace Mira.Core;

public sealed class SyncService : IAsyncDisposable
{
    private readonly JellyfinClient _client;
    private readonly LibraryStore _store;
    private readonly SemaphoreSlim _flush = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _writeGate = new();
    private readonly Task _loop;
    private Task _writes = Task.CompletedTask;
    private int _pending;
    public event Action? Changed;
    /// <summary>Cached after each write or delivery, so reading it never touches SQLite on the UI thread.</summary>
    public int PendingCount => Volatile.Read(ref _pending);
    public string? Error { get; private set; }
    public int Discarded { get; private set; }
    public DateTimeOffset? LastSynced { get; private set; }
    public SyncService(JellyfinClient client, LibraryStore store)
    {
        _client = client; _store = store; _pending = store.PendingCount;
        // The retry loop and its SQLite reads run on the thread pool, never on the caller's UI thread.
        _loop = Task.Run(RetryLoopAsync);
    }
    /// <summary>Durable before it returns, but written off the caller's thread and in call order:
    /// a SQLite transaction on the UI thread every few seconds shows up as a hitch during playback.</summary>
    public Task RecordAsync(string kind, PlaybackReport report)
    {
        Task write;
        lock (_writeGate)
            write = _writes = _writes.ContinueWith(_ => { _store.Enqueue(kind, report); Volatile.Write(ref _pending, _store.PendingCount); },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return AfterWriteAsync(write);
    }
    private async Task AfterWriteAsync(Task write)
    {
        await write.ConfigureAwait(false);
        Changed?.Invoke();
        _ = FlushAsync();
    }
    public async Task FlushAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        if (!await _flush.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            while (_store.Peek() is { } next)
            {
                try
                {
                    await _client.ReportAsync(next.Kind, next.Report, ct).ConfigureAwait(false);
                    _store.Acknowledge(next.Id); LastSynced = DateTimeOffset.Now; Error = null;
                }
                catch (HttpRequestException ex) when (IsPermanent(ex.StatusCode))
                {
                    // Refused for good (deleted item, rejected report): retrying forever would block every later report.
                    _store.Acknowledge(next.Id); Discarded++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
                {
                    Error = ex is UnauthorizedAccessException ? "Reconnecte ton compte" : "En attente de Jellyfin";
                    break;
                }
            }
        }
        finally { Volatile.Write(ref _pending, _store.PendingCount); _flush.Release(); Changed?.Invoke(); }
    }
    private static bool IsPermanent(HttpStatusCode? status) => status is { } code && (int)code is >= 400 and < 500
        && code is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    private async Task RetryLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try { await FlushAsync(_lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                // A storage hiccup must not end retries for the rest of the session.
                catch (Exception) { Error = "Synchronisation interrompue, nouvel essai dans un instant"; Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        // Give the last reports (usually "stop") a short chance to arrive before network calls are cancelled.
        try
        {
            await _writes.ConfigureAwait(false);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (PendingCount > 0 && DateTime.UtcNow < deadline)
            {
                var before = PendingCount; var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                await FlushAsync().WaitAsync(left).ConfigureAwait(false);
                if (PendingCount == 0 || (Error is not null && PendingCount == before)) break;
                await Task.Delay(40).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        _lifetime.Cancel();
        await _loop.ConfigureAwait(false);
        await _flush.WaitAsync().ConfigureAwait(false);
        _flush.Release();
        // Any unacknowledged report remains in SQLite for the next launch.
        _lifetime.Dispose();
    }
}
