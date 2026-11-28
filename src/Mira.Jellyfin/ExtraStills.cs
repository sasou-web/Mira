using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Mira.Jellyfin;

/// <summary>
/// The still of a bonus video that has none, drawn from the video by Jellyfin, one at a time (ogsas, 2026-11-28: the
/// openings and endings of « Monogatari Series » showed a placeholder in the pill « Extras », on the Mac as on Windows).
/// Jellyfin draws an extra's still only when its scan first meets the file: an extra renamed, moved, or met while
/// Jellyfin was busy keeps none, a later scan never tries again, and Mira's refresh leaves extras alone
/// (<see cref="TitleRefresh"/>). After each library scan, and after Mira's refresh of a title, each extra of a film or
/// series without a primary image gets an image refresh alone: no metadata read again (no ffprobe), one frame by
/// Jellyfin's screen grabber, the next one after it. An extra whose still could not be drawn is not tried again until
/// Jellyfin restarts (a file the grabber cannot read would be tried after every scan).
/// </summary>
public sealed class ExtraStills : ILibraryPostScanTask
{
    private static readonly ConcurrentDictionary<Guid, byte> Tried = new();
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ExtraStills> _logger;

    public ExtraStills(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<ExtraStills> logger)
    {
        _library = library; _providers = providers; _fileSystem = fileSystem; _logger = logger;
    }

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var titles = _library.GetItemsResult(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie], Recursive = true }).Items;
        var missing = titles.SelectMany(TitleRefresh.ExtrasOf).Where(Wanted).ToList();
        if (missing.Count > 0) await FillAsync(_providers, _fileSystem, _logger, missing, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <summary>An extra that is a video without a primary image, not failed since Jellyfin started.</summary>
    public static bool Wanted(BaseItem extra) => extra is Video { IsPlaceHolder: false } && !extra.HasImage(ImageType.Primary) && !Tried.ContainsKey(extra.Id);

    /// <summary>The stills of <paramref name="extras"/> that have none, one after the other.</summary>
    public static async Task FillAsync(IProviderManager providers, IFileSystem fileSystem, ILogger logger, IEnumerable<BaseItem> extras, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var drawn = 0; var tried = 0;
            foreach (var extra in extras.Where(Wanted).ToList())
            {
                ct.ThrowIfCancellationRequested();
                tried++;
                var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    // FullRefresh runs the screen grabber; ReplaceAllImages false keeps every image the extra has.
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false,
                };
                try
                {
                    await providers.RefreshSingleItem(extra, options, ct).ConfigureAwait(false);
                    if (extra.HasImage(ImageType.Primary)) drawn++; else Tried.TryAdd(extra.Id, 0);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Tried.TryAdd(extra.Id, 0); logger.LogWarning(ex, "Mira: still of the extra {Extra}", extra.Path); }
            }
            if (tried > 0) logger.LogInformation("Mira: stills drawn for {Drawn} of {Tried} extras without one", drawn, tried);
        }
        finally { OneAtATime.Release(); }
    }
}
