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
            .Select(t => new RelayMissing(t.x.Id, t.x.Place.SeriesId, t.x.Place.Season, t.x.Place.Episodes, t.x.Name, t.x.Size, t.x.InfoHash, t.Item2, t.Item3, t.x.Created)).ToList();
    }

    /// <summary>
    /// Forgotten: placed (or refused) a while ago, failed a while ago, or sent and gone from TorLink's list and from what was
    /// placed (cancelled there); and anything after <see cref="MissingLife"/>.
    /// </summary>
    private void PruneMissing(DateTimeOffset now)
    {
        var downloads = _agent?.Downloads.Select(x => x.Id).ToHashSet() ?? [];
        var placed = _agent?.Placed.Select(x => x.Id).ToHashSet() ?? [];
        foreach (var watched in _missing)
        {
            if (placed.Contains(watched.InfoHash)) watched.PlacedAt ??= now;
            if (downloads.Contains(watched.InfoHash)) watched.Listed = true;
        }
        var asked = _requests.Select(x => x.Id).ToHashSet();
        _missing.RemoveAll(x => now - x.Created > MissingLife
            || x.PlacedAt is { } at && now - at > Kept
            || x.State == "failed" && !asked.Contains(x.Id) && x.Changed is { } failed && now - failed > Kept
            // Listed by TorLink once, now in neither list: cancelled from the Downloads page (or TorLink lost it).
            || _agent is not null && x.Listed && x.PlacedAt is null && !downloads.Contains(x.InfoHash) && !placed.Contains(x.InfoHash));
    }

    private sealed class Pending(string id, MissingPlace place, string name, long size, string infoHash, DateTimeOffset created)
    {
        public string Id { get; } = id; public MissingPlace Place { get; } = place; public string Name { get; } = name; public long Size { get; } = size;
        public string InfoHash { get; } = infoHash; public DateTimeOffset Created { get; } = created;
        public string State { get; set; } = "waiting"; public string? Error { get; set; }
        public DateTimeOffset? Changed { get; set; }
        public DateTimeOffset? PlacedAt { get; set; }
        public bool Listed { get; set; }
    }
}
