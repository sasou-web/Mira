using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mira.Core;

namespace Mira.Jellyfin;

/// <summary>
/// The episode order of each series picked from its files (<see cref="TvdbOrder"/>): once a series' episodes change
/// (added or removed), its files are compared with each of TheTVDB's orders, asked through TheTVDB's own plugin (its
/// key, its cache; nothing without it), and the series' « Display order » set to the one that fits, then the series
/// read again so that its titles follow. An order someone chose in Jellyfin is never changed; neither is a series
/// TheTVDB doesn't know. Looked at every 10 minutes, a few series at a time, never while anyone watches. What it
/// decided is kept beside the plugins' settings (<c>Mira.tvdb-orders.json</c>), so that each series is weighed again
/// only when its files change.
/// </summary>
public sealed class TvdbOrders : IHostedService, IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    private const int PerTick = 5;
    public static string? File { get; set; }

    private readonly ILibraryManager _library;
    private readonly ISessionManager _sessions;
    private readonly IFileSystem _fileSystem;
    private readonly IServiceProvider _services;
    private readonly TitleRefresh _refresh;
    private readonly ILogger<TvdbOrders> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Guid> _dirty = [];
    private Dictionary<string, Decision>? _decisions;
    private bool _everything = true;
    private Task? _loop;

    /// <summary>
    /// For a series: how many files it had when weighed, the order Mira set ("" = aired), and whether its order is
    /// someone's own (chosen in Jellyfin before Mira looked, or changed there after): then never touched again.
    /// </summary>
    public sealed record Decision(int Files, string Order, bool Theirs = false);

    public TvdbOrders(ILibraryManager library, ISessionManager sessions, IFileSystem fileSystem, IServiceProvider services, TitleRefresh refresh, ILogger<TvdbOrders> logger)
    {
        _library = library; _sessions = sessions; _fileSystem = fileSystem; _services = services; _refresh = refresh; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnChanged;
        _library.ItemRemoved += OnChanged;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnChanged;
        _library.ItemRemoved -= OnChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    public void Dispose() => _stop.Dispose();

    private void OnChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Episode { IsVirtualItem: false } episode && episode.SeriesId != Guid.Empty) lock (_gate) _dirty.Add(episode.SeriesId);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Mira: TheTVDB episode order"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_sessions.Sessions.Any(s => s.NowPlayingItem is not null) || Tvdb() is not { } tvdb) return;
        List<Guid> next;
        lock (_gate)
        {
            // After a start of Jellyfin, every series once: one that didn't change since it was weighed costs nothing.
            if (_everything)
            {
                foreach (var series in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true })) _dirty.Add(series.Id);
                _everything = false;
            }
            next = _dirty.Take(PerTick).ToList();
            foreach (var id in next) _dirty.Remove(id);
        }
        foreach (var id in next)
        {
            ct.ThrowIfCancellationRequested();
            if (_library.GetItemById(id) is Series series && !await WeighAsync(series, tvdb, ct).ConfigureAwait(false)) lock (_gate) _dirty.Add(id);
        }
    }

    /// <summary>The series weighed and its order set when another fits; false when TheTVDB could not be asked (tried again later).</summary>
    private async Task<bool> WeighAsync(Series series, (object Manager, MethodInfo Episodes) tvdb, CancellationToken ct)
    {
        var key = series.Id.ToString("N");
        var decisions = Decisions();
        decisions.TryGetValue(key, out var before);
        var current = series.DisplayOrder ?? "";
        var files = _library.GetItemList(new InternalItemsQuery { AncestorIds = [series.Id], IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false, Recursive = true })
            .OfType<Episode>().Where(x => x.ParentIndexNumber is >= 1 && x.IndexNumber is >= 1)
            .SelectMany(x => Enumerable.Range(x.IndexNumber!.Value, Math.Max(1, (x.IndexNumberEnd ?? x.IndexNumber!.Value) - x.IndexNumber!.Value + 1)).Select(e => (x.ParentIndexNumber!.Value, e)))
            .Distinct().ToList();
        if (before is { Theirs: true }) return true;
        // Chosen by someone in Jellyfin (or by Mira and then changed there): theirs.
        if (before is null ? current.Length > 0 : current != before.Order) { Remember(key, new Decision(files.Count, current, Theirs: true)); return true; }
        if (before is not null && before.Files == files.Count) return true;
        if (files.Count == 0 || !int.TryParse(series.GetProviderId(MetadataProvider.Tvdb), out var tvdbId)) { Remember(key, new Decision(files.Count, current)); return true; }
        var language = series.GetPreferredMetadataLanguage() ?? "en";
        var orders = new Dictionary<string, IReadOnlyCollection<(int Season, int Episode)>>();
        if (await EpisodesAsync(tvdb, tvdbId, language, "", ct).ConfigureAwait(false) is not { } aired) return false;
        orders[""] = aired;
        // Every file in the aired order: no need to ask for the others.
        if (!files.All(aired.Contains))
        {
            foreach (var order in TvdbOrder.Orders.Skip(1))
                if (await EpisodesAsync(tvdb, tvdbId, language, order, ct).ConfigureAwait(false) is { Count: > 0 } episodes) orders[order] = episodes;
        }
        var chosen = TvdbOrder.Best(files, orders) ?? "";
        Remember(key, new Decision(files.Count, chosen));
        if (chosen == current) return true;
        series.DisplayOrder = chosen;
        await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        // Without reading its extras again all at once (TitleRefresh): a franchise's textless credits, for one.
        _refresh.Queue(series, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ImageRefreshMode = MetadataRefreshMode.Default, ReplaceAllMetadata = true, ReplaceAllImages = false,
        }, RefreshPriority.Normal);
        _logger.LogInformation("Mira: {Series} shown in TheTVDB's {Order} order, which fits its {Files} files", series.Name, TvdbOrder.SeasonType(chosen), files.Count);
        return true;
    }

    /// <summary>TheTVDB's plugin as Jellyfin runs it, and its method that lists a series' episodes in an order; null without it.</summary>
    private (object Manager, MethodInfo Episodes)? Tvdb()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name == "Jellyfin.Plugin.Tvdb")
                .Select(x => x.GetType("Jellyfin.Plugin.Tvdb.TvdbClientManager", false)).FirstOrDefault(x => x is not null);
            var method = type?.GetMethod("GetSeriesEpisodesAsync", [typeof(int), typeof(string), typeof(string), typeof(CancellationToken)]);
            return type is not null && method is not null && _services.GetService(type) is { } manager ? (manager, method) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Mira: TheTVDB's plugin"); return null; }
    }

    /// <summary>A series' episodes in one of TheTVDB's orders (season, number); empty when TheTVDB has no such order for it, null when it could not be asked.</summary>
    private async Task<List<(int Season, int Episode)>?> EpisodesAsync((object Manager, MethodInfo Episodes) tvdb, int id, string language, string order, CancellationToken ct)
    {
        try
        {
            var task = (Task)tvdb.Episodes.Invoke(tvdb.Manager, [id, language, TvdbOrder.SeasonType(order), ct])!;
            await task.ConfigureAwait(false);
            var data = task.GetType().GetProperty("Result")?.GetValue(task);
            var list = new List<(int Season, int Episode)>();
            if (data?.GetType().GetProperty("Episodes")?.GetValue(data) is not IEnumerable episodes) return list;
            foreach (var episode in episodes)
            {
                var type = episode?.GetType();
                if (Number(type?.GetProperty("SeasonNumber")?.GetValue(episode)) is { } season && Number(type?.GetProperty("Number")?.GetValue(episode)) is { } number) list.Add((season, number));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Mira: TheTVDB's {Order} order of {Id}", TvdbOrder.SeasonType(order), id);
            return order.Length == 0 ? null : [];
        }
    }

    private static int? Number(object? value) => value switch { int x => x, long x => (int)x, double x => (int)x, _ => null };

    private Dictionary<string, Decision> Decisions()
    {
        lock (_gate)
        {
            if (_decisions is not null) return _decisions;
            try { _decisions = File is not null && System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Dictionary<string, Decision>>(System.IO.File.ReadAllText(File)) ?? [] : []; }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _decisions = []; }
            return _decisions;
        }
    }

    private void Remember(string key, Decision decision)
    {
        lock (_gate)
        {
            Decisions()[key] = decision;
            if (File is null) return;
            try
            {
                var temp = File + ".tmp";
                System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(_decisions));
                System.IO.File.Move(temp, File, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Mira: TheTVDB episode orders not saved"); }
        }
    }
}
