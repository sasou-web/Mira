using Mira.Core;

namespace Mira.Desktop.Services;

/// <summary>Short-lived request sharing for hover, hero and detail screens; scoped to one authenticated client.</summary>
public sealed class MetadataCache(JellyfinClient client)
{
    private readonly Dictionary<string, (DateTimeOffset Expires, object Task)> _requests = [];
    public Task<MediaItem> ItemAsync(string id) => Get("item:" + id, () => client.ItemAsync(id));
    public Task<ItemsResult> EpisodesAsync(string id) => Get("episodes:" + id, () => client.EpisodesAsync(id));
    public Task<ItemsResult> SimilarAsync(string id) => Get("similar:" + id, () => client.SimilarAsync(id));
    public void Clear() => _requests.Clear();
    private Task<T> Get<T>(string key, Func<Task<T>> load)
    {
        if (_requests.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow && hit.Task is Task<T> task && !task.IsFaulted && !task.IsCanceled) return task;
        if (_requests.Count > 256) _requests.Clear();
        var pending = load(); _requests[key] = (DateTimeOffset.UtcNow.AddMinutes(2), pending); return pending;
    }
}
