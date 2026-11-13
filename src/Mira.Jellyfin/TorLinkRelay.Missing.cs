using Mira.Core.TorLink;

namespace Mira.Jellyfin;

/// <summary>
/// « Télécharger les manquants »: an episode, a few, or a season asked from a series' page. Each goes as any request (a result
/// of the relay's searches), with where to place it; the relay keeps it listed (<see cref="RelayStatus.Missing"/>) after it
/// leaves the requests, until Mira placed it or TorLink dropped it, so that every device shows its progress on the page.
/// In memory, as the requests: a restart of Jellyfin forgets the list, not the downloads.
/// </summary>
public sealed partial class TorLinkRelay
{
    /// <summary>Asked this long ago and nowhere to be seen any more (cancelled in TorLink, or Mira closed for good): forgotten.</summary>
    public static readonly TimeSpan MissingLife = TimeSpan.FromDays(3);
    /// <summary>Placed this long ago: Jellyfin has listed it, or never will where it went (the page stops saying it is coming).</summary>
    public static readonly TimeSpan MissingPlacedLife = TimeSpan.FromDays(1);
    /// <summary>Gone from TorLink's list before it finished, this long: cancelled there.</summary>
    public static readonly TimeSpan MissingGrace = TimeSpan.FromMinutes(1);
    public const int MaxMissing = 60, MaxMissingEpisodes = 60;

    private readonly List<Pending> _missing = [];

    private string? MissingProblem(MissingPlace missing, string? replace, string? torrent, string? searchId)
    {
        if (replace is not null || torrent is not null || searchId is null || Clean(missing) is null) return "Demande inconnue.";
        // An older Mira on the PC of Jellyfin would place it where its name says, maybe as another series.
        if (_agent is not { Seasons: true }) return "Mira sur le PC serveur ne sait pas encore ranger un épisode manquant : mets-le à jour.";
        return null;
    }

    /// <summary>A place checked: a series id of Jellyfin, a season, episodes in it (none: the whole season).</summary>
    private static MissingPlace? Clean(MissingPlace? missing) =>
        missing is { SeriesId: { } id, Season: >= 1 and <= 100 } && RemoteDownloads.IsItemId(id.Replace("-", "", StringComparison.Ordinal))
        && (missing.Episodes ?? []) is { Count: <= MaxMissingEpisodes } episodes && episodes.All(x => x is >= 1 and <= 5000)
            ? new MissingPlace(id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant(), missing.Season, episodes.Distinct().Order().ToList())
            : null;

    private void Watch(Ask ask)
    {
        _missing.RemoveAll(x => x.InfoHash == ask.InfoHash);
        _missing.Add(new Pending(ask.Id, ask.Missing!, ask.Name, ask.Size, ask.InfoHash, ask.Created));
        if (_missing.Count > MaxMissing) _missing.RemoveAt(0);
    }

    /// <summary>The request answered by the agent: its state kept beyond the request's own life.</summary>
    private void Settled(Ask ask, DateTimeOffset now)
    {
        if (_missing.FirstOrDefault(x => x.Id == ask.Id) is { } watched) (watched.State, watched.Error, watched.Changed) = (ask.State, ask.Error, now);
    }

    private List<RelayMissing> MissingList()
    {
        var requests = _requests.ToDictionary(x => x.Id);
        return _missing.Select(x => requests.TryGetValue(x.Id, out var ask) ? (x, ask.State, ask.Error) : (x, x.State, x.Error))
            .Select(t => new RelayMissing(t.x.Id, t.x.Place.SeriesId, t.x.Place.Season, t.x.Place.Episodes, t.x.Name, t.x.Size, t.x.InfoHash, t.Item2, t.Item3, t.x.Created,
                t.x.Listed, t.x.Finished, t.x.PlacedOk ? t.x.PlacedAt : null)).ToList();
    }

    /// <summary>
    /// Forgotten: placed a while ago (a day when it went well: the page says « Adding to your library » until Jellyfin lists the
    /// episode, and no longer once it is there), refused or failed a while ago, sent and gone from TorLink's list before it
    /// finished (cancelled there: a minute's grace, the time Mira takes to see a download TorLink just finished); and anything
    /// after <see cref="MissingLife"/>. A finished one Mira has not placed yet (the agent's Sorting, or gone from TorLink's
    /// list once finished) stays: it is being placed.
    /// </summary>
    private void PruneMissing(DateTimeOffset now)
    {
        var downloads = _agent?.Downloads.DistinctBy(x => x.Id).ToDictionary(x => x.Id) ?? [];
        var placed = _agent?.Placed.DistinctBy(x => x.Id).ToDictionary(x => x.Id) ?? [];
        var sorting = _agent?.Sorting.ToHashSet() ?? [];
        foreach (var watched in _missing)
        {
            if (placed.TryGetValue(watched.InfoHash, out var done)) { watched.PlacedAt ??= now; watched.PlacedOk = done.Ok; watched.Finished = true; }
            if (downloads.TryGetValue(watched.InfoHash, out var download))
            {
                watched.Listed = true;
                if (download.Status is "completed" or "seeding" || download.Progress >= 100) watched.Finished = true;
            }
            if (sorting.Contains(watched.InfoHash)) watched.Finished = true;
            var gone = _agent is not null && watched.Listed && !downloads.ContainsKey(watched.InfoHash) && !placed.ContainsKey(watched.InfoHash) && !sorting.Contains(watched.InfoHash);
            watched.GoneAt = gone ? watched.GoneAt ?? now : null;
        }
        var asked = _requests.Select(x => x.Id).ToHashSet();
        _missing.RemoveAll(x => now - x.Created > MissingLife
            || x.PlacedAt is { } at && now - at > (x.PlacedOk ? MissingPlacedLife : Kept)
            || x.State == "failed" && !asked.Contains(x.Id) && x.Changed is { } failed && now - failed > Kept
            // Listed by TorLink once, now in no list before it finished: cancelled from the Downloads page (or TorLink lost it).
            || x.GoneAt is { } left && !x.Finished && x.PlacedAt is null && now - left > MissingGrace);
    }

    private sealed class Pending(string id, MissingPlace place, string name, long size, string infoHash, DateTimeOffset created)
    {
        public string Id { get; } = id; public MissingPlace Place { get; } = place; public string Name { get; } = name; public long Size { get; } = size;
        public string InfoHash { get; } = infoHash; public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "waiting"; public string? Error { get; set; }
        public DateTimeOffset? Changed { get; set; }
        public DateTimeOffset? PlacedAt { get; set; }
        public bool PlacedOk { get; set; }
        public bool Listed { get; set; }
        public bool Finished { get; set; }
        public DateTimeOffset? GoneAt { get; set; }
    }
}
