namespace Mira.Core;

/// <summary>
/// Which of TheTVDB's episode orders fits the files of a series (ogsas, 2026-11-18: « choisir le meilleur choix qui
/// colle à ce qu'on vient de télécharger ») : a pack numbered after the novels (Monogatari's « Book Order », TheTVDB's
/// « alternate »), after the DVDs, or from 1 to 500 (« absolute »), shown by Jellyfin in that order instead of the
/// aired one, its titles then matching the files. The extension asks TheTVDB's plugin for each order and sets the
/// series' « Display order » to the one chosen; the aired order is kept whenever it fits, or nothing fits well enough.
/// </summary>
public static class TvdbOrder
{
    /// <summary>TheTVDB's season types as Jellyfin's series « Display order » names them; "" = aired (TheTVDB's « official »).</summary>
    public static readonly string[] Orders = ["", "dvd", "absolute", "alternate", "regional", "altdvd", "alttwo"];
    /// <summary>The name TheTVDB's API gives the season type of an order.</summary>
    public static string SeasonType(string order) => order.Length == 0 ? "official" : order;
    /// <summary>Another order is taken only when this share of the files is in it at least.</summary>
    public const double Enough = .95;
    /// <summary>And when it holds this much more of the files than the aired order (a near tie keeps the aired one).</summary>
    public const double Better = .2;

    /// <summary>
    /// The order to show <paramref name="files"/> (season and episode of each file, specials left out) in, among
    /// <paramref name="orders"/> (each order's episodes on TheTVDB); null when the aired order holds them all, when
    /// none holds enough of them, or when the aired order is unknown. Ties go to the order listed first in <see cref="Orders"/>.
    /// </summary>
    public static string? Best(IEnumerable<(int Season, int Episode)> files, IReadOnlyDictionary<string, IReadOnlyCollection<(int Season, int Episode)>> orders)
    {
        var local = files.Where(x => x.Season >= 1 && x.Episode >= 1).ToHashSet();
        if (local.Count == 0 || !orders.TryGetValue("", out var aired)) return null;
        var airedShare = Share(local, aired);
        if (airedShare >= 1) return null;
        string? best = null; var bestShare = 0d;
        foreach (var order in Orders.Skip(1))
        {
            if (!orders.TryGetValue(order, out var episodes) || episodes.Count == 0) continue;
            var share = Share(local, episodes);
            if (share > bestShare) (best, bestShare) = (order, share);
        }
        return best is not null && bestShare >= Enough && bestShare - airedShare >= Better ? best : null;
    }

    /// <summary>The share of <paramref name="local"/> that <paramref name="episodes"/> has, specials of TheTVDB left out.</summary>
    private static double Share(HashSet<(int Season, int Episode)> local, IReadOnlyCollection<(int Season, int Episode)> episodes)
    {
        var known = episodes.Where(x => x.Season >= 1).ToHashSet();
        return (double)local.Count(known.Contains) / local.Count;
    }
}
