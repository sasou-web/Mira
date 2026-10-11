namespace Mira.Core;

/// <summary>
/// The thumbnail of each season an episode card shows (« Continuer à regarder »): asked of Jellyfin once per new season,
/// not at every refresh, then set on the episodes. Used from the interface thread, which alone adds to it.
/// </summary>
public sealed class SeasonThumbs
{
    private readonly Dictionary<string, string?> _known = [];

    /// <summary>
    /// Asks Jellyfin for the thumbnails of the seasons not seen yet, then sets the known ones on <paramref name="items"/>.
    /// A failure only leaves those cards on their next choice; only the caller's own cancellation goes on up (HttpClient's
    /// 12 s limit included).
    /// </summary>
    public async Task LoadAsync(JellyfinClient client, IEnumerable<MediaItem> items, CancellationToken ct)
    {
        var list = items.ToList();
        var missing = Artwork.Seasons(list).Where(x => !_known.ContainsKey(x)).ToList();
        if (missing.Count > 0)
        {
            try { foreach (var (season, tag) in await client.SeasonThumbsAsync(missing, ct)) _known[season] = tag; }
            catch (Exception ex) when (ExpectedErrors.Are(ex) && !ct.IsCancellationRequested) { }
        }
        Apply(list);
    }
    /// <summary>A copy of the known thumbnails, for a worker thread: the interface thread may be adding to them.</summary>
    public IReadOnlyDictionary<string, string?> Snapshot() => new Dictionary<string, string?>(_known);
    /// <summary>Sets the known thumbnails on the episodes of <paramref name="items"/>; on a worker thread, with a <see cref="Snapshot"/>.</summary>
    public void Apply(IEnumerable<MediaItem> items, IReadOnlyDictionary<string, string?>? known = null)
    {
        known ??= _known;
        foreach (var item in items)
            if (item.Type == "Episode" && item.SeasonId is { } season && known.TryGetValue(season, out var tag)) item.SeasonThumbImageTag = tag;
    }
    /// <summary>Another account: nothing known any more.</summary>
    public void Clear() => _known.Clear();
}
