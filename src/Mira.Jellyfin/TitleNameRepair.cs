using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mira.Core;

namespace Mira.Jellyfin;

/// <summary>
/// A title Jellyfin named after a file a download tool saved as « 0c76ce5c44cd079b008af19790bb4cc3_The Nightmare
/// Before Christmas 1993.mkv » (a friend of ogsas, 2026-11-19): its name without that code (<see cref="TitleNames"/>),
/// in Jellyfin itself, so that every app, Jellyfin's own included, says it right. A title just found is renamed before
/// Jellyfin reads it from its sources, which then search for the real name; one already there is renamed when it
/// changes or someone watches it (its progress saved), then read again from its sources, so that it takes their name.
/// A name someone locked is left alone; each title is renamed once per start of Jellyfin.
/// </summary>
public sealed class TitleNameRepair : IHostedService
{
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IFileSystem _fileSystem;
    private readonly TitleRefresh _refresh;
    private readonly ILogger<TitleNameRepair> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _renamed = new();

    public TitleNameRepair(ILibraryManager library, IUserDataManager userData, IFileSystem fileSystem, TitleRefresh refresh, ILogger<TitleNameRepair> logger)
    {
        _library = library; _userData = userData; _fileSystem = fileSystem; _refresh = refresh; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemChanged;
        _userData.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemChanged;
        _userData.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <summary>A film, an episode, a video or a series whose name has a code before it, and whose name nobody locked.</summary>
    private static bool Wanted(BaseItem? item, out string clean)
    {
        clean = "";
        if (item is not (Video or Series) || item.IsLocked || item.LockedFields.Contains(MetadataField.Name) || item.Name is not { Length: > 0 } name) return false;
        clean = TitleNames.Clean(name);
        return clean != name;
    }

    /// <summary>Just found: renamed before Jellyfin's own reading of it, which saves it.</summary>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!Wanted(e.Item, out var clean)) return;
        e.Item.Name = clean;
        _renamed.TryAdd(e.Item.Id, 0);
        _ = SaveAsync(e.Item, refresh: false);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e) => Repair(e.Item);

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e) => Repair(e.Item);

    /// <summary>Already there: renamed, saved, then read again from its sources (their name replaces the file's).</summary>
    private void Repair(BaseItem? item)
    {
        if (item is null || !Wanted(item, out var clean) || !_renamed.TryAdd(item.Id, 0)) return;
        item.Name = clean;
        _ = SaveAsync(item, refresh: true);
    }

    private async Task SaveAsync(BaseItem item, bool refresh)
    {
        try
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Mira: {Id} named « {Name} », without the code a download tool put before its file's name", item.Id, item.Name);
            if (refresh)
            {
                _refresh.Queue(item, new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ImageRefreshMode = MetadataRefreshMode.Default, ReplaceAllMetadata = true, ReplaceAllImages = false,
                }, RefreshPriority.Normal);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Mira: {Id} not renamed", item.Id);
        }
    }
}
