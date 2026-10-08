using Mira.Core;

namespace Mira.Mac.Services;

/// <summary>
/// One signed-in Jellyfin account: its client, its local cache and progress queue (shared with the Windows app's
/// Mira.Core), its artwork, and the server's change notifications.
/// </summary>
public sealed class Session : IAsyncDisposable
{
    public JellyfinClient Client { get; }
    public LibraryStore Store { get; }
    public SyncService Sync { get; }
    public Images Images { get; }
    public Connection Connection => Client.Connection;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, (DateTimeOffset Expires, object Task)> _requests = [];

    /// <param name="handler">Another way to reach Jellyfin: the self-check's stand-in server.</param>
    public Session(Connection connection, Profile profile, HttpMessageHandler? handler = null)
    {
        Client = new JellyfinClient(connection, handler);
        Store = new LibraryStore(profile.DirectoryPath, connection.Server + "|" + connection.UserId);
        Sync = new SyncService(Client, Store);
        Images = new Images(Client, profile.DirectoryPath);
    }

    /// <summary>Calls <paramref name="changed"/> (on a worker thread) when the library changes on the server.</summary>
    public void Listen(Action changed) => _ = ListenSafelyAsync(changed);
    private async Task ListenSafelyAsync(Action changed)
    {
        try { await Client.ListenAsync(changed, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    // Short-lived request sharing for the title pages: opening the same title twice asks Jellyfin once.
    public Task<MediaItem> ItemAsync(string id) => Shared("item:" + id, () => Client.ItemAsync(id));
    public Task<ItemsResult> EpisodesAsync(string id) => Shared("episodes:" + id, () => Client.EpisodesAsync(id));
    public Task<ItemsResult> SimilarAsync(string id) => Shared("similar:" + id, () => Client.SimilarAsync(id));
    public void Forget() => _requests.Clear();
    private Task<T> Shared<T>(string key, Func<Task<T>> load)
    {
        if (_requests.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow && hit.Task is Task<T> task && !task.IsFaulted && !task.IsCanceled) return task;
        if (_requests.Count > 256) _requests.Clear();
        var pending = load(); _requests[key] = (DateTimeOffset.UtcNow.AddMinutes(2), pending); return pending;
    }

    /// <summary>Jellyfin's next episode of a series, with the progress still waiting to be sent applied.</summary>
    public async Task<MediaItem?> NextEpisodeAsync(string seriesId)
    {
        try
        {
            var next = await Client.NextEpisodeAsync(seriesId);
            if (next is not null) await Task.Run(() => Store.ApplyLocalProgress([next]));
            // Jellyfin may still return the episode whose watched report is queued locally.
            return next?.UserData.Played == true ? null : next;
        }
        catch (Exception ex) when (Errors.Expected(ex)) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        // The last reports get a short chance to leave; the rest wait in SQLite for the next launch.
        await Sync.DisposeAsync();
        Client.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>The errors Mira expects from the network and the disk, and how it words them.</summary>
public static class Errors
{
    public static bool Expected(Exception e) => e is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException
        or OperationCanceledException or System.Text.Json.JsonException or TimeoutException;
    public static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ex.Message, ArgumentException => ex.Message, ServerDiscoveryException => ex.Message,
        OperationCanceledException or TimeoutException => "Jellyfin met trop de temps à répondre.",
        HttpRequestException h when h.StatusCode is not null => h.Message,
        HttpRequestException => "Le serveur Jellyfin est momentanément inaccessible.",
        _ => "L’opération n’a pas abouti. Vérifie la connexion et réessaie."
    };
}
