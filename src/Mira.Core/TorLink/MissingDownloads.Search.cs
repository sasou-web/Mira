namespace Mira.Core.TorLink;

// The searches behind « Télécharger les manquants », the same on Windows and on the Mac: through the relay of the
// extension, which has the PC of Jellyfin run them with TorLink (nothing leaves the device but the request to Jellyfin).
public static partial class MissingDownloads
{
    /// <summary>How long a search on the PC of Jellyfin is waited for.</summary>
    public static readonly TimeSpan SearchWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The episodes of the seasons before <paramref name="season"/>, as an anime's absolute numbers count them: the last
    /// episode of each, from the library's own and TheTVDB's.
    /// </summary>
    public static int Before(IEnumerable<MediaItem> episodes, int season)
    {
        var all = episodes.Where(x => x.ParentIndexNumber is >= 1 && x.IndexNumber is >= 1).ToList();
        return Enumerable.Range(1, Math.Max(0, season - 1)).Sum(s => all.Where(x => x.ParentIndexNumber == s).Select(x => x.IndexNumber!.Value).DefaultIfEmpty(0).Max());
    }

    /// <summary>The series is in a library of animes: TorLink is asked in « anime » too.</summary>
    public static async Task<bool> IsAnimeAsync(JellyfinClient client, MediaItem series, CancellationToken ct)
    {
        var path = series.Path ?? (await client.ItemAsync(series.Id, ct)).Path;
        if (path is null) return false;
        var library = (await client.VirtualFoldersAsync(ct) ?? []).FirstOrDefault(x => x.Locations.Any(location => Inside(path, location)));
        return library is not null && MediaLibraries.IsAnime(library);
    }

    /// <summary>
    /// <paramref name="path"/> inside <paramref name="root"/>, as the server writes them: a Windows server's paths read on a
    /// Mac as well (either separator, any case).
    /// </summary>
    public static bool Inside(string path, string root)
    {
        static string Clean(string value) => value.Replace('\\', '/').TrimEnd('/');
        var folder = Clean(root);
        return folder.Length > 0 && Clean(path).StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One query in each category through the relay, waited for: the results with the search of each, or why none.</summary>
    public static async Task<(List<(string Search, RelayHit Hit)> Hits, string? Error)> SearchAsync(JellyfinClient client, string query, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var ids = new List<string>();
        foreach (var category in categories) ids.Add(await client.TorLinkSearchAsync(query, category, ct));
        var hits = new List<(string, RelayHit)>(); string? error = null;
        var until = DateTime.UtcNow + SearchWait;
        var left = ids.ToList();
        while (left.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            foreach (var id in left.ToList())
            {
                var state = await client.TorLinkSearchStateAsync(id, ct);
                if (state is null) { left.Remove(id); continue; }
                if (state.State == "done") { hits.AddRange(state.Results.Select(x => (id, x))); left.Remove(id); }
                else if (state.State == "failed") { error ??= state.Error; left.Remove(id); }
            }
        }
        if (left.Count > 0) error ??= L("Le PC de Jellyfin n’a pas répondu à temps.", "Your Jellyfin PC didn’t answer in time.");
        return (hits, hits.Count > 0 ? null : error);
    }
}
