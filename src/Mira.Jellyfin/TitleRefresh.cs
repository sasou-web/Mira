using System.Collections;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// A title read again from its sources without analysing its bonus videos all at once (ogsas, 2026-11-18: « Monogatari
/// Series » and its 48 textless credits, 47 ffprobe of Jellyfin together on the Dell, frozen, 0.9 GB of memory left).
/// Jellyfin's own refresh in FullRefresh reads every extra of the title again, each in its own task, all awaited
/// together (<c>BaseItem.RefreshExtras</c>), whatever its concurrency settings. A title with more than
/// <see cref="ManyExtras"/> extras is refreshed here instead, as Jellyfin's queue does it minus the extras: the title
/// itself (<see cref="IProviderManager.RefreshSingleItem"/>), then a series' seasons and episodes one after the other
/// (<see cref="IMetadataContainer.RefreshAllMetadata"/>). Its extras keep what Jellyfin read of them; a new file (episode
/// or extra) is found by the library scan, as for any title. One title at a time. A title with few extras goes to Jellyfin's queue, as before.
/// </summary>
public sealed class TitleRefresh : IHostedService, IDisposable
{
    public const int ManyExtras = 4;
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly ILogger<TitleRefresh> _logger;
    private readonly Channel<(Guid Id, MetadataRefreshOptions Options)> _queue = Channel.CreateUnbounded<(Guid, MetadataRefreshOptions)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public TitleRefresh(ILibraryManager library, IProviderManager providers, ILogger<TitleRefresh> logger)
    {
        _library = library; _providers = providers; _logger = logger;
    }

    /// <summary>Refreshes <paramref name="item"/> with <paramref name="options"/>: here when it has many extras, otherwise by Jellyfin's queue.</summary>
    public void Queue(BaseItem item, MetadataRefreshOptions options, RefreshPriority priority = RefreshPriority.High)
    {
        if (Extras(item) <= ManyExtras || !_queue.Writer.TryWrite((item.Id, options))) _providers.QueueRefresh(item.Id, options, priority);
    }

    /// <summary>How many extras a title has. <c>GetExtras()</c> in 10.9 and 10.10, <c>GetExtras(User user = null)</c> since: called by name, its arguments left to their defaults.</summary>
    public static int Extras(BaseItem item)
    {
        try
        {
            var method = typeof(BaseItem).GetMethods().FirstOrDefault(m => m.Name == "GetExtras" && m.GetParameters().All(p => p.IsOptional));
            return method?.Invoke(item, method.GetParameters().Select(_ => Type.Missing).ToArray()) is IEnumerable extras ? extras.Cast<object>().Count() : 0;
        }
        catch (Exception ex) when (ex is System.Reflection.TargetInvocationException or InvalidOperationException) { return 0; }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (id, options) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (_library.GetItemById(id) is not { } item) continue;
                try { await RefreshAsync(item, options, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Mira: refreshing {Title}", item.Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RefreshAsync(BaseItem item, MetadataRefreshOptions options, CancellationToken ct)
    {
        _logger.LogInformation("Mira: {Title} read again without its {Extras} extras at once", item.Name, Extras(item));
        // Started and ended as Jellyfin's own refresh of a folder does (Folder.ValidateChildrenInternal): TheTVDB's plugin
        // adds and removes a series' episodes « not in your library » when it hears the refresh ended.
        if (item is Folder) _providers.OnRefreshStart(item);
        try
        {
            await _providers.RefreshSingleItem(item, options, ct).ConfigureAwait(false);
            // A series: its seasons and episodes, then itself again, one after the other. Not ValidateChildren: for a series
            // it begins with the series' own RefreshMetadata, extras and all (Folder.RefreshAllMetadataForContainer).
            if (item is IMetadataContainer container) await container.RefreshAllMetadata(options, new Progress<double>(), ct).ConfigureAwait(false);
            else if (item is Folder folder) await folder.ValidateChildren(new Progress<double>(), options, true, false, ct).ConfigureAwait(false);
        }
        finally
        {
            if (item is Folder) _providers.OnRefreshComplete(item);
        }
    }

    public void Dispose() => _stop.Dispose();
}
