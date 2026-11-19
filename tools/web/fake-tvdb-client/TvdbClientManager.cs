using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Tvdb;

/// <summary>Registered as the real plugin registers its own: one for Jellyfin, which Mira asks Jellyfin's services for.</summary>
public sealed class Registrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost) =>
        serviceCollection.AddSingleton<TvdbClientManager>();
}

/// <summary>One episode as TheTVDB's SDK gives it: the properties Mira reads.</summary>
public sealed record EpisodeBaseRecord(int? SeasonNumber, int? Number);

/// <summary>A series' episodes in one order, as the SDK's « extended » episodes page.</summary>
public sealed record SeriesEpisodes(IReadOnlyList<EpisodeBaseRecord> Episodes);

/// <summary>TheTVDB, as the real plugin's client asks it, for the test series only.</summary>
public sealed class TvdbClientManager
{
    /// <summary>« Ordre Test »: its folder says so (tools/web/media.sh).</summary>
    public const int OrdreTest = 424242;

    private static readonly Dictionary<string, (int Season, int Episode)[]> Orders = new(StringComparer.Ordinal)
    {
        // Its files are S01E01, S01E02 and S02E01: only two of three in the aired order, all of them on the DVDs.
        ["official"] = [(1, 1), (2, 1), (2, 2), (2, 3), (2, 4)],
        ["dvd"] = [(1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (2, 3), (2, 4)],
    };

    /// <summary>Asked by Mira (TvdbOrders) once for each order; an order TheTVDB lacks for the series: none of its episodes.</summary>
    public Task<SeriesEpisodes> GetSeriesEpisodesAsync(int tvdbId, string language, string seasonType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var episodes = tvdbId == OrdreTest && Orders.TryGetValue(seasonType, out var list) ? list.Select(x => new EpisodeBaseRecord(x.Season, x.Episode)).ToList() : [];
        return Task.FromResult(new SeriesEpisodes(episodes));
    }
}
