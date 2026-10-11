namespace Mira.Core;

/// <summary>
/// Decoded artwork kept for reuse (Windows and Mac): one request per image, shared by every caller, and the least
/// recently used let go once their estimated size passes <see cref="Limit"/>, down to <see cref="Target"/>. Images on
/// screen keep their own reference: letting one go here only stops its reuse.
/// </summary>
public sealed class ImageMemory<T>(long limit, long target, Func<T, long> size) where T : class
{
    /// <summary>128 MB: the home page (five banners, posters, resume cards) fits with room for a title page.</summary>
    public const long DefaultLimit = 128L << 20, DefaultTarget = 96L << 20;
    private sealed class Entry(Task<T?> image, long lastUse) { public readonly Task<T?> Image = image; public long LastUse = lastUse, Bytes; }
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly object _gate = new();
    private long _bytes, _clock;
    private int _settled;
    public long Limit => limit;
    public long Target => target;
    /// <summary>Estimated size of the images held for reuse.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }
    /// <summary>Requests finished and accounted for.</summary>
    public int Settled { get { lock (_gate) return _settled; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    /// <summary>The image under <paramref name="key"/>, from memory or from <paramref name="fetch"/>, which must not take this lock.</summary>
    public Task<T?> Get(string key, Func<Task<T?>> fetch)
    {
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var hit)) { hit.LastUse = ++_clock; return hit.Image; }
            // Started inside the lock: no other caller can start the same request, nor see a half-made entry.
            entry = new Entry(fetch(), ++_clock); _entries[key] = entry;
        }
        _ = entry.Image.ContinueWith(done => Settle(key, entry, done.IsCompletedSuccessfully ? done.Result : null), TaskScheduler.Default);
        return entry.Image;
    }
    /// <summary>Lets go of everything held for reuse (the window minimized); requests on their way still answer their callers.</summary>
    public void Clear() { lock (_gate) { _entries.Clear(); _bytes = 0; } }
    private void Settle(string key, Entry entry, T? image)
    {
        lock (_gate)
        {
            _settled++;
            if (!_entries.TryGetValue(key, out var current) || current != entry) return;
            // A failed image is asked again next time instead of staying missing.
            if (image is null) { _entries.Remove(key); return; }
            entry.Bytes = Math.Max(0, size(image));
            _bytes += entry.Bytes;
            if (_bytes <= limit) return;
            foreach (var (oldKey, old) in _entries.Where(x => x.Value.Bytes > 0).OrderBy(x => x.Value.LastUse).ToList())
            {
                if (_bytes <= target) break;
                _entries.Remove(oldKey); _bytes -= old.Bytes;
            }
        }
    }
}
