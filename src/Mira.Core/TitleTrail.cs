namespace Mira.Core;

/// <summary>
/// The title pages left for another one (a similar title, the saga, the studio): « Retour » comes back to each in turn,
/// scrolled where it was, before the page closes. A title already in the trail is gone back to, never stacked twice.
/// </summary>
public sealed class TitleTrail
{
    /// <summary>Enough for any real wander from title to title; the oldest pages drop out beyond.</summary>
    public const int Limit = 30;
    private readonly List<(MediaItem Item, double Offset)> _pages = [];
    public int Count => _pages.Count;
    public IReadOnlyList<(MediaItem Item, double Offset)> Pages => _pages;
    /// <summary>
    /// <paramref name="next"/> opens over <paramref name="current"/>, scrolled to <paramref name="offset"/>: the current
    /// page is kept to come back to. Opening a page of the trail goes back to it instead (and drops what was after it).
    /// </summary>
    public void Leave(MediaItem current, double offset, MediaItem next)
    {
        if (current.Id == next.Id) return;
        var seen = _pages.FindIndex(x => x.Item.Id == next.Id);
        if (seen >= 0) { _pages.RemoveRange(seen, _pages.Count - seen); return; }
        _pages.Add((current, Math.Max(0, offset)));
        if (_pages.Count > Limit) _pages.RemoveAt(0);
    }
    /// <summary>The page « Retour » comes back to, taken off the trail; none = the page closes.</summary>
    public (MediaItem Item, double Offset)? Back()
    {
        if (_pages.Count == 0) return null;
        var page = _pages[^1]; _pages.RemoveAt(_pages.Count - 1);
        return page;
    }
    /// <summary>A title gone from the library (put in the Recycle Bin): never come back to.</summary>
    public void Forget(string id) => _pages.RemoveAll(x => x.Item.Id == id);
    public void Clear() => _pages.Clear();
    /// <summary>The same pages, kept while the trail is left for a person's or a studio's titles.</summary>
    public void Replace(IEnumerable<(MediaItem Item, double Offset)> pages) { var copy = pages.ToList(); _pages.Clear(); _pages.AddRange(copy.TakeLast(Limit)); }
}
