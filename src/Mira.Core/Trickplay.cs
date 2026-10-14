namespace Mira.Core;

/// <summary>One size of Jellyfin's trickplay pictures: thumbnails every Interval ms, TileWidth × TileHeight per tile image.</summary>
public sealed record TrickplayInfo
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int TileWidth { get; init; }
    public int TileHeight { get; init; }
    public int ThumbnailCount { get; init; }
    public int Interval { get; init; }
}
/// <summary>Where a thumbnail is in its tile image, in that image's pixels (as Jellyfin made it).</summary>
public readonly record struct TrickplayCell(int Tile, int X, int Y, int Width, int Height);

/// <summary>
/// The trickplay pictures of a title, as Mira web shows them over its timeline: which tile image holds the moment
/// pointed at, and where in it. Jellyfin makes them when its library option asks (« Extraire les images trickplay »).
/// </summary>
public sealed record TrickplaySet(string ItemId, string SourceId, TrickplayInfo Info)
{
    /// <summary>The thumbnails' width Mira asks for: the smallest at least this wide, else the widest.</summary>
    public const int Wanted = 240;
    /// <summary>The set of the file played (else the first), at the size closest above <see cref="Wanted"/>; null without one.</summary>
    public static TrickplaySet? Pick(string itemId, IReadOnlyDictionary<string, Dictionary<string, TrickplayInfo>>? trickplay, string? sourceId)
    {
        if (trickplay is null || trickplay.Count == 0) return null;
        var (source, sizes) = sourceId is not null && trickplay.TryGetValue(sourceId, out var played) ? (sourceId, played) : (trickplay.Keys.First(), trickplay.Values.First());
        var usable = sizes.Values.Where(x => x is { Width: > 0, Height: > 0, TileWidth: > 0, TileHeight: > 0, ThumbnailCount: > 0, Interval: > 0 }).ToList();
        if (usable.Count == 0) return null;
        var info = usable.Where(x => x.Width >= Wanted).OrderBy(x => x.Width).FirstOrDefault() ?? usable.MaxBy(x => x.Width)!;
        return new(itemId, source, info);
    }
    public TrickplayCell Cell(double seconds)
    {
        var index = (int)Math.Clamp(Math.Floor(Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * 1000 / Info.Interval), 0, Info.ThumbnailCount - 1);
        var perTile = Info.TileWidth * Info.TileHeight; var cell = index % perTile;
        return new(index / perTile, cell % Info.TileWidth * Info.Width, cell / Info.TileWidth * Info.Height, Info.Width, Info.Height);
    }
    /// <summary>A tile image is as wide as its columns; Jellyfin leaves out the rows it does not fill.</summary>
    public int TilePixelWidth => Info.TileWidth * Info.Width;
    public string TilePath(int tile) => $"Videos/{Uri.EscapeDataString(ItemId)}/Trickplay/{Info.Width}/{tile}.jpg?mediaSourceId={Uri.EscapeDataString(SourceId)}";
}

/// <summary>
/// The tile images of one title while it plays: fetched when a moment in them is first pointed at, decoded off the
/// interface's thread, and only the last few kept (a tile decoded whole is several megabytes). <typeparamref name="T"/>
/// is the picture of each app (WPF's, Avalonia's).
/// </summary>
public sealed class TrickplayTiles<T> : IDisposable where T : class
{
    private readonly Func<int, CancellationToken, Task<byte[]>> _fetch;
    private readonly Func<byte[], T?> _decode;
    private readonly int _keep;
    private readonly List<(int Tile, T Picture)> _ready = [];
    private readonly HashSet<int> _loading = [], _failed = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    public TrickplaySet Set { get; }
    /// <summary>A tile asked for is ready: what points at it can be drawn again. On the creator's thread.</summary>
    public event Action? Loaded;
    public TrickplayTiles(TrickplaySet set, Func<int, CancellationToken, Task<byte[]>> fetch, Func<byte[], T?> decode, int keep = 2)
    { Set = set; _fetch = fetch; _decode = decode; _keep = Math.Max(1, keep); }
    /// <summary>The tile and the place of the moment <paramref name="seconds"/>; null while its tile comes (it is asked for then).</summary>
    public (T Picture, TrickplayCell Cell)? At(double seconds)
    {
        var cell = Set.Cell(seconds);
        var index = _ready.FindIndex(x => x.Tile == cell.Tile);
        if (index >= 0)
        {
            var hit = _ready[index];
            if (index != _ready.Count - 1) { _ready.RemoveAt(index); _ready.Add(hit); }
            return (hit.Picture, cell);
        }
        if (!_failed.Contains(cell.Tile) && _loading.Add(cell.Tile)) _ = LoadAsync(cell.Tile);
        return null;
    }
    private async Task LoadAsync(int tile)
    {
        T? picture = null;
        try
        {
            var token = _cancel.Token;
            picture = await Task.Run(async () => _decode(await _fetch(tile, token).ConfigureAwait(false)), token).ConfigureAwait(false);
        }
        // Without its tile the bubble shows the time alone, as before; a tile that failed is not asked for again.
        catch (Exception ex) when (ExpectedErrors.Are(ex) || ex is NotSupportedException or FormatException) { }
        void Done()
        {
            _loading.Remove(tile);
            if (_cancel.IsCancellationRequested) return;
            if (picture is null) { _failed.Add(tile); return; }
            _ready.Add((tile, picture));
            while (_ready.Count > _keep) _ready.RemoveAt(0);
            Loaded?.Invoke();
        }
        if (_context is null) Done(); else _context.Post(_ => Done(), null);
    }
    public void Dispose() { _cancel.Cancel(); _ready.Clear(); }
}
/// <summary>The chapters and trickplay pictures of a title (<see cref="JellyfinClient.TimelineAsync"/>).</summary>
public sealed record TitleTimeline(List<ChapterInfo> Chapters, TrickplaySet? Trickplay);
