using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FakeTvdb;

/// <summary>« TheTVDB », as Mira looks for it in Jellyfin's plugins: its id and its name.</summary>
public sealed class Plugin(IApplicationPaths paths, IXmlSerializer xml) : BasePlugin<BasePluginConfiguration>(paths, xml)
{
    public override string Name => "TheTVDB";
    public override Guid Id => new("a677c0da-fac5-4cde-941a-7134223f14c8");
}

public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) =>
        serviceCollection.AddHostedService<MissingEpisodes>();
}

/// <summary>
/// What TheTVDB would say of the test series, in days from today (null: announced without a date). As the real plugin
/// (TvdbMissingEpisodeProvider), once Jellyfin has refreshed a series (each scan does): a virtual episode for each one
/// the library lacks, in a virtual season when the library has none.
/// </summary>
public sealed class MissingEpisodes(IProviderManager providers, ILibraryManager library, IFileSystem fileSystem, ILogger<MissingEpisodes> logger) : IHostedService
{
    private static readonly Dictionary<string, (int Season, int Episode, int? Days, string Name)[]> Calendar = new()
    {
        ["Deux Temps"] = [(1, 3, -9, "Le retour"), (2, 2, -2, "La veille"), (2, 3, 2, "Le lendemain"), (2, 4, 9, "La suite"), (3, 1, null, "Saison trois")],
        ["Courte Anime"] = [(1, 2, 0, "Ce soir"), (1, 3, 7, "La semaine prochaine")],
    };

    private readonly object _gate = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        providers.RefreshCompleted += OnRefreshed;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        providers.RefreshCompleted -= OnRefreshed;
        return Task.CompletedTask;
    }

    private void OnRefreshed(object? sender, GenericEventArgs<BaseItem> e)
    {
        if (e.Argument is not Series series || series.Name is null || !Calendar.TryGetValue(series.Name, out var episodes)) return;
        lock (_gate)
        {
            // A query rather than Folder.GetRecursiveChildren, whose return type changed after 10.9 (MissingMethodException).
            var children = library.GetItemsResult(new InternalItemsQuery { ParentId = series.Id, Recursive = true, IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode] }).Items;
            var seasons = children.OfType<Season>().Where(x => x.IndexNumber is not null).ToList();
            var owned = children.OfType<Episode>().ToList();
            logger.LogInformation("Fake TheTVDB: {Series}, {Seasons} seasons, {Episodes} episodes", series.Name, seasons.Count, owned.Count);
            foreach (var (number, index, days, name) in episodes)
            {
                if (owned.Any(x => x.ParentIndexNumber == number && x.IndexNumber == index)) continue;
                var season = seasons.FirstOrDefault(x => x.IndexNumber == number);
                if (season is null)
                {
                    season = new Season
                    {
                        Name = string.Create(CultureInfo.InvariantCulture, $"Season {number}"), IndexNumber = number,
                        Id = library.GetNewItemId(series.Id + number.ToString(CultureInfo.InvariantCulture) + "Season", typeof(Season)),
                        IsVirtualItem = true, SeriesId = series.Id, SeriesName = series.Name, SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                    };
                    series.AddChild(season);
                    providers.QueueRefresh(season.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High);
                    seasons.Add(season);
                }
                var episode = new Episode
                {
                    Name = name, IndexNumber = index, ParentIndexNumber = number,
                    Id = library.GetNewItemId($"{series.Id}{number}Episode {index}", typeof(Episode)),
                    IsVirtualItem = true, SeasonId = season.Id, SeriesId = series.Id, SeriesName = series.Name,
                    SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey, SeasonName = season.Name, DateLastSaved = DateTime.UtcNow,
                };
                // TheTVDB gives a day (« 2026-11-02 »), which the real plugin parses as such.
                if (days is { } d) episode.PremiereDate = DateTime.Parse(DateTime.UtcNow.Date.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();
                season.AddChild(episode);
                // As the real plugin: Jellyfin then saves it whole, as it saves an episode it found.
                providers.QueueRefresh(episode.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High);
                owned.Add(episode);
            }
        }
    }
}
