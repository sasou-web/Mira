namespace Mira.Core;

/// <summary>
/// The room a title takes on the server's disks, as Jellyfin knows its files (<c>MediaSources[].Size</c>, nothing asked
/// of anyone else): a film's file, its other versions and its other parts (cd1/cd2) too; a series' episodes, each season
/// and each episode. Said on the title's page, in its line of facts (« 2022 · 2 h · 14,2 Go »), after a season's count
/// (« Épisodes · 12 · 48 Go ») and on an episode's tile (« 24 min · 1,2 Go »). The iPhone's StorageSize.swift agrees
/// (cases of tests/fixtures/storage.json).
/// </summary>
public static class TitleStorage
{
    /// <summary>
    /// The files of a title: each version once (by its path, else its id), those whose size Jellyfin does not know left
    /// out; null when it knows none.
    /// </summary>
    public static long? Of(IEnumerable<MediaSource>? sources)
    {
        if (sources is null) return null;
        long total = 0; var any = false; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (source.Size is not > 0 || !seen.Add(source.Path is { Length: > 0 } path ? path : source.Id)) continue;
            total += source.Size.Value; any = true;
        }
        return any ? total : null;
    }

    /// <summary>A title and its other parts (a film in cd1/cd2: Jellyfin lists the first file alone as the title's).</summary>
    public static long? Of(MediaItem item, IEnumerable<MediaItem>? parts = null)
    {
        var sizes = new[] { Of(item.MediaSources) }.Concat((parts ?? []).Select(x => Of(x.MediaSources))).Where(x => x is not null).ToList();
        return sizes.Count == 0 ? null : sizes.Sum();
    }

    /// <summary>The episodes known among <paramref name="episodes"/>: null when none is (Jellyfin knows no size yet).</summary>
    public static long? Sum(IEnumerable<MediaItem> episodes, IReadOnlyDictionary<string, long> sizes)
    {
        long total = 0; var any = false;
        foreach (var episode in episodes.DistinctBy(x => x.Id))
            if (sizes.TryGetValue(episode.Id, out var size)) { total += size; any = true; }
        return any ? total : null;
    }

    /// <summary>Each episode's files by its id (those Jellyfin knows a size of), from a list asked with <c>fields=MediaSources</c>.</summary>
    public static Dictionary<string, long> Sizes(IEnumerable<MediaItem> episodes)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var episode in episodes) if (Of(episode.MediaSources) is { } size) sizes[episode.Id] = size;
        return sizes;
    }

    /// <summary>« 850 Mo », « 14,2 Go », « 312 Go », « 1,4 To »; null for no size.</summary>
    public static string? Text(long? bytes) => bytes is > 0 ? OfflineCopies.SizeText(bytes.Value) : null;
}

/// <summary>
/// The sizes asked of Jellyfin for the pages, kept for <see cref="Keep"/>: a series' episodes ask for their files, a long
/// list (an anime of a thousand episodes: several megabytes), so the page never waits for it and opens again at once. A
/// series whose number of episodes changed (one added) is asked again.
/// </summary>
public sealed class TitleSizes(JellyfinClient client)
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, (DateTimeOffset At, Task<Dictionary<string, long>> Task)> _series = [];
    private readonly Dictionary<string, (DateTimeOffset At, Task<long?> Task)> _films = [];
    private readonly object _gate = new();

    /// <summary>Each episode's size by its id; <paramref name="count"/>: the episodes the page lists (another number = asked again).</summary>
    public Task<Dictionary<string, long>> EpisodesAsync(string seriesId, int count) =>
        Get(_series, $"{seriesId}:{count}", async () => TitleStorage.Sizes(await client.EpisodeFilesAsync(seriesId).ConfigureAwait(false)));

    /// <summary>A film's files, its other parts asked when it has some (<paramref name="item"/>: as <c>Items/{id}</c> gives it, with its sources).</summary>
    public Task<long?> FilmAsync(MediaItem item) =>
        Get(_films, item.Id, async () => TitleStorage.Of(item, item.PartCount is > 1 ? await client.AdditionalPartsAsync(item.Id).ConfigureAwait(false) : null));

    /// <summary>Known at once: what was asked before and is still kept, already answered.</summary>
    public long? KnownFilm(string id) { lock (_gate) return _films.TryGetValue(id, out var hit) && Fresh(hit.At) && hit.Task.IsCompletedSuccessfully ? hit.Task.Result : null; }
    public Dictionary<string, long>? KnownEpisodes(string seriesId, int count)
    { lock (_gate) return _series.TryGetValue($"{seriesId}:{count}", out var hit) && Fresh(hit.At) && hit.Task.IsCompletedSuccessfully ? hit.Task.Result : null; }

    public void Clear() { lock (_gate) { _series.Clear(); _films.Clear(); } }

    private static bool Fresh(DateTimeOffset at) => DateTimeOffset.UtcNow - at < Keep;
    private Task<T> Get<T>(Dictionary<string, (DateTimeOffset At, Task<T> Task)> kept, string key, Func<Task<T>> load)
    {
        lock (_gate)
        {
            if (kept.TryGetValue(key, out var hit) && Fresh(hit.At) && !hit.Task.IsFaulted && !hit.Task.IsCanceled) return hit.Task;
            if (kept.Count > 128) kept.Clear();
            var task = load(); kept[key] = (DateTimeOffset.UtcNow, task); return task;
        }
    }
}
