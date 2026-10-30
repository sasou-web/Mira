using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mira.Core;
using Mira.Core.Tailscale;
using static Mira.Core.Lang;

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
        // The cache first: when even a new one cannot be written, nothing else has been opened.
        Store = new LibraryStore(profile.DirectoryPath, AccountData.LibraryProfile(connection));
        Client = new JellyfinClient(connection, handler);
        Sync = new SyncService(Client, Store);
        Images = new Images(Client, profile.DirectoryPath);
        Lifetime = _lifetime.Token;
    }

    /// <summary>Ends with the session: what is sent for it stops when it closes.</summary>
    public CancellationToken Lifetime { get; }
    /// <summary>
    /// Calls <paramref name="changed"/> (on a worker thread) when the library changes on the server, <paramref name="listening"/>
    /// when the connection opens or closes, and <paramref name="syncPlay"/> with each message of « Regarder ensemble ».
    /// </summary>
    public void Listen(Action changed, Action<bool>? listening = null, Action<string, JsonElement>? syncPlay = null) => _ = ListenSafelyAsync(changed, listening, syncPlay);
    private async Task ListenSafelyAsync(Action changed, Action<bool>? listening, Action<string, JsonElement>? syncPlay)
    {
        try { await Client.ListenAsync(changed, Lifetime, listening, syncPlay); }
        catch (OperationCanceledException) { }
    }

    // Short-lived request sharing for the title pages: opening the same title twice asks Jellyfin once.
    public Task<MediaItem> ItemAsync(string id) => Shared("item:" + id, () => Client.ItemAsync(id));
    public Task<ItemsResult> EpisodesAsync(string id) => Shared("episodes:" + id, () => Client.EpisodesAsync(id));
    public Task<ItemsResult> SimilarAsync(string id) => Shared("similar:" + id, () => Client.SimilarAsync(id));
    public Task<Saga?> SagaAsync(string id) => Shared("saga:" + id, () => Client.SagaAsync(id));
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

    public async ValueTask DisposeAsync() => await CloseAsync(signOut: false);
    /// <summary>Closes the session and ends it on Jellyfin too. False when Jellyfin could not be told (see LogoutAsync).</summary>
    public Task<bool> SignOutAsync() => CloseAsync(signOut: true);
    private async Task<bool> CloseAsync(bool signOut)
    {
        _lifetime.Cancel();
        // The last reports get a short chance to leave; the rest wait in SQLite for the next launch.
        await Sync.DisposeAsync();
        // After them: they still need the token Jellyfin forgets.
        var closed = signOut && await Client.LogoutAsync();
        Client.Dispose();
        _lifetime.Dispose();
        return closed;
    }
}

/// <summary>The errors Mira expects from the network and the disk, and how it words them.</summary>
public static class Errors
{
    public static bool Expected(Exception e) => ExpectedErrors.Are(e) || e is TimeoutException;
    public static string Friendly(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ex.Message,
        UserInputException => ex.Message,
        ServerDiscoveryException => ex.Message,
        // Through « Tailscale intégré »: why the node could not reach Jellyfin's PC (MainWindow.Say says it with its device).
        _ when TailnetNetwork.Find(ex) is { } node && TailnetText.Problem(node.Code) is { } said => said,
        OperationCanceledException or TimeoutException => L("Jellyfin met trop de temps à répondre.", "Jellyfin is taking too long to answer."),
        HttpRequestException h when h.StatusCode is not null => h.Message,
        HttpRequestException => L("Le serveur Jellyfin est momentanément inaccessible.", "Can’t reach the Jellyfin server right now."),
        SqliteException => L("Le stockage local de Mira est indisponible (disque plein ou fichier occupé) : libère de la place sur le disque, ou relance Mira.", "Mira’s local storage is unavailable (full disk or file in use): free up some disk space, or open Mira again."),
        _ => L("L’opération n’a pas abouti. Vérifie la connexion et réessaie.", "That didn’t work. Check the connection and try again.")
    };
    /// <summary>The local cache is a bonus: a full disk, a locked or damaged database leaves Jellyfin's answer, or nothing, on screen.</summary>
    public static T? TryCache<T>(Func<T> read) { try { return read(); } catch (Exception ex) when (CacheFailure(ex)) { return default; } }
    public static void TryCache(Action write) { try { write(); } catch (Exception ex) when (CacheFailure(ex)) { } }
    private static bool CacheFailure(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException;
}
