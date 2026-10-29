using System.Globalization;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>What the agent read of a followed series in Jellyfin: its names, its year and the episodes it has.</summary>
public sealed record FollowedSeries(string Name, string? OriginalTitle, int? Year, IReadOnlyList<FollowEpisode> Owned);

/// <summary>What a check chose: the releases to send, what to say of it, the names of the series learned, the latest episode of the library.</summary>
public sealed record FollowCheck(List<FollowPick> Picks, FollowNote Note, List<string> Names, FollowEpisode? Latest);

/// <summary>
/// « Suivre une série »: which results of TorLink are the next episodes of a series followed, and which release of each
/// to take. A result counts only under a name of the series (Jellyfin's, its original title, the name searched, the
/// names its releases gave before), the year of the series give or take one; its episode only when it comes right after
/// the latest known (<see cref="Window"/> at most), in the latest season or the first episodes of the next. Never a pack
/// (several episodes in the name, or over 3 times the usual size), a release few share, another language than the one
/// taken before, nor a release refused (cancelled, or failed in TorLink). Then the same group and picture as last time,
/// and the ranking of the Downloads page (<see cref="ReleaseRanking.Score"/>).
/// </summary>
public static partial class SeriesFollowing
{
    /// <summary>How long a release chosen waits for « Annuler » before going to TorLink.</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);
    public const int MaxPicks = 6, Window = 12, MinSeeders = 2, MaxNames = 6, MaxQueries = 2;
    /// <summary>No episode is that large: a whole season sold as one.</summary>
    public const long MaxSize = 25L << 30;

    /// <summary>The searches of TorLink for a series: anime first in the anime sites, then the series ones.</summary>
    public static IReadOnlyList<string> Categories(string? category) => category == "anime" ? ["anime", "tv"] : ["tv"];

    /// <summary>
    /// What to search: the name chosen for it alone; otherwise the name its releases go by (<paramref name="releaseTitle"/>),
    /// then Jellyfin's, without its year in parentheses.
    /// </summary>
    public static List<string> Queries(string? query, string name, string? releaseTitle)
    {
        if (RemoteDownloads.CleanQuery(query) is { } chosen) return [chosen];
        return new[] { releaseTitle, Year().Replace(name, "") }.Select(RemoteDownloads.CleanQuery).OfType<string>()
            .DistinctBy(ReleaseRanking.TitleKey).Take(MaxQueries).ToList();
    }

    /// <summary>« S01E13 »: an episode as the pages say it, in both languages.</summary>
    public static string Label(FollowEpisode episode) => string.Create(CultureInfo.InvariantCulture, $"S{episode.Season:00}E{episode.Episode:00}");

    /// <summary>« S02 », « S01E13-E24 », « E13-E24 »: a pack in words of no language (the page that shows it may not have the agent's).</summary>
    public static string PackLabel(ReleaseInfo release) => (release.Season, release.Episode, release.LastEpisode) switch
    {
        ({ } s, { } from, { } to) => FormattableString.Invariant($"S{s:00}E{from:00}-E{to:00}"),
        ({ } s, _, _) => FormattableString.Invariant($"S{s:00}"),
        (null, { } from, { } to) => FormattableString.Invariant($"E{from:00}-E{to:00}"),
        _ => "",
    };

    /// <summary>The episodes of a series as Jellyfin numbers them, each of a double one (E01-E02) counted; never the specials (season 0).</summary>
    public static List<FollowEpisode> Owned(IEnumerable<MediaItem> episodes) =>
        episodes.Where(x => x.ParentIndexNumber is >= 1 && x.IndexNumber is >= 1)
            .SelectMany(x => Enumerable.Range(x.IndexNumber!.Value, Math.Clamp((x.IndexNumberEnd ?? 0) - x.IndexNumber.Value, 0, 50) + 1).Select(e => new FollowEpisode(x.ParentIndexNumber!.Value, e)))
            .Distinct().ToList();

    /// <summary>The latest of these episodes; null without any.</summary>
    public static FollowEpisode? Latest(IEnumerable<FollowEpisode?> episodes) =>
        episodes.OfType<FollowEpisode>().OrderBy(x => x.Season).ThenBy(x => x.Episode).LastOrDefault();

    /// <summary>
    /// What Mira placed before in the series' folder: the names its releases gave, the latest of them (its title, to
    /// search) and what it was (to take the next one alike). Nothing when Mira never placed anything there.
    /// </summary>
    public static (List<string> Names, string? Title, FollowRelease? Prefer) FromJournal(IEnumerable<TorLinkImportEntry> entries, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return ([], null, null);
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var placed = entries.Where(x => x.Kind is MediaKind.Series or MediaKind.Anime && x.Files.Any(f => f.Role == "video" && !f.Found
            && Path.GetFullPath(f.Destination).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).OrderByDescending(x => x.CompletedAt).ToList();
        if (placed.Count == 0) return ([], null, null);
        var names = placed.Select(x => ReleaseRanking.Parse(x.Name)).SelectMany(x => new[] { x.Title, x.Alternate }).OfType<string>()
            .Where(x => ReleaseRanking.TitleKey(x).Length > 0).DistinctBy(ReleaseRanking.TitleKey).Take(MaxNames).ToList();
        var latest = ReleaseRanking.Parse(placed[0].Name);
        return (names, latest.Title, new FollowRelease(latest.Group, latest.Languages.ToList(), latest.Resolution));
    }

    /// <summary>
    /// The releases to take among the results of a check (<paramref name="hits"/>, with the search that found each), what
    /// to say of it, the names learned and the latest episode the library has. <paramref name="names"/>: other names of the
    /// series known on the PC of Jellyfin (<see cref="FromJournal"/>); <paramref name="prefer"/>: the release taken last;
    /// <paramref name="expected"/>: the episodes TheTVDB says are out (<see cref="FollowSchedule.Out"/>), when known.
    /// </summary>
    public static FollowCheck Choose(WorkFollow follow, FollowedSeries series, IEnumerable<string> names, IReadOnlyList<(string Category, AgentHit Hit)> hits, FollowRelease? prefer,
        IReadOnlySet<FollowEpisode>? expected = null)
    {
        var library = Latest(series.Owned);
        var latestLibrary = Latest([library, follow.Latest]);
        var taken = (follow.Taken ?? []).ToHashSet();
        var known = series.Owned.Concat(taken).Concat(latestLibrary is null ? [] : [latestLibrary]).ToList();
        var (lastSeason, lastEpisode) = Latest(known) is { } last ? (last.Season, last.Episode) : (1, 0);
        var seasonEnds = known.GroupBy(x => x.Season).ToDictionary(x => x.Key, x => x.Max(e => e.Episode));
        int Before(int season) => seasonEnds.Where(x => x.Key >= 1 && x.Key < season).Sum(x => x.Value);
        var refused = (follow.Refused ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var learned = (follow.Names ?? []).ToList();
        var keys = new[] { series.Name, Year().Replace(series.Name, ""), series.OriginalTitle, follow.Query }.Concat(names).Concat(learned)
            .Select(ReleaseRanking.TitleKey).Where(x => x.Length > 0).ToHashSet();

        var matched = false;
        var candidates = new List<(FollowEpisode Episode, RankedRelease Entry, AgentHit Found)>();
        // What was found and not taken, in the order of the episodes (a pack at its first one).
        var held = new List<(FollowEpisode Order, string Label, string Reason)>();
        var seen = new HashSet<string>();
        foreach (var (category, found) in hits)
        {
            if (RemoteDownloads.InfoHash(found.Magnet) is not { } hash || !seen.Add(hash)) continue;
            var hit = new RelayHit(found.Id, found.Name, found.Size, found.Seeders, found.Leechers, found.Source, found.Added, category);
            var release = ReleaseRanking.Parse(found.Name);
            var titled = keys.Contains(ReleaseRanking.TitleKey(release.Title)) || release.Alternate is { } other && keys.Contains(ReleaseRanking.TitleKey(other));
            if (!titled || release.Year is { } year && series.Year is { } born && Math.Abs(year - born) > 1) continue;
            matched = true;
            // Never « Season 02 », which a pack may give in parentheses.
            foreach (var name in new[] { release.Title, release.Alternate }.OfType<string>())
                if (learned.Count < MaxNames && ReleaseRanking.TitleKey(name) is { Length: > 0 } key && !keys.Contains(key) && ReleaseRanking.Parse(name) is { Season: null, Episode: null, Pack: false })
                { learned.Add(name); keys.Add(key); }
            // Several episodes in one: worth saying when they go past the latest, never taken.
            if (release.Episode is not { } number || release.Pack || release.LastEpisode > number)
            {
                var end = release.LastEpisode ?? release.Episode;
                var after = release.Season is { } packSeason ? packSeason > lastSeason || packSeason == lastSeason && end > lastEpisode : end > lastEpisode;
                if ((release.Pack || end is not null) && after) held.Add((new(release.Season ?? lastSeason, release.Episode ?? 0), PackLabel(release), "pack"));
                continue;
            }
            // Numbered in its season, or from the first episode of the series (« Show - 31 » for S02E03).
            var season = release.Season ?? lastSeason;
            var mapped = new[] { new FollowEpisode(season, number), new FollowEpisode(season, number - Before(season)) }.Distinct()
                .Where(x => x.Episode >= 1 && (x.Season == lastSeason && x.Episode > lastEpisode && x.Episode <= lastEpisode + Window
                    || release.Season is not null && x.Season == lastSeason + 1 && x.Episode <= Window)).ToList();
            // Both readings possible (« Show - 9 » after S02E03, five episodes in season 1: S02E09 or S02E04): the one TheTVDB says is out.
            if (mapped.Count == 2 && expected is not null && mapped.Where(expected.Contains).ToList() is [var aired]) mapped = [aired];
            if (mapped is not [var episode] || taken.Contains(episode) || refused.Contains(hash) || release.Source == "cam") continue;
            candidates.Add((episode, new RankedRelease(hit, release, candidates.Count, ReleaseRanking.Score(hit, release, category)), found));
        }

        // Over three times the usual size of an episode: a season in one file, or a mistake.
        var sizes = candidates.Select(x => x.Entry.Hit.Size).Where(x => x > 0).Order().ToList();
        var limit = sizes.Count >= 3 ? Math.Min(MaxSize, sizes[sizes.Count / 2] * 3) : MaxSize;
        var picks = new List<FollowPick>();
        foreach (var byEpisode in candidates.GroupBy(x => x.Episode).OrderBy(x => x.Key.Season).ThenBy(x => x.Key.Episode))
        {
            var fit = new List<(RankedRelease Entry, AgentHit Found)>();
            foreach (var (_, entry, found) in byEpisode)
            {
                var reason = entry.Hit.Size > limit ? "size"
                    : ReleaseRanking.ReportsHealth(entry.Hit.Source) && entry.Hit.Seeders < MinSeeders ? "health"
                    : !SameLanguage(entry.Release.Languages, prefer?.Languages) ? "language" : null;
                if (reason is null) fit.Add((entry, found)); else held.Add((byEpisode.Key, Label(byEpisode.Key), reason));
            }
            var best = fit.OrderByDescending(x => prefer?.Group is { } group && string.Equals(x.Entry.Release.Group, group, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => prefer?.Resolution is { } resolution && x.Entry.Release.Resolution == resolution)
                .ThenByDescending(x => x.Entry.Score).ThenByDescending(x => x.Entry.Hit.Seeders).ThenBy(x => x.Entry.Order).FirstOrDefault();
            if (best.Found is not { } chosen || picks.Count >= MaxPicks) continue;
            var r = best.Entry.Release;
            picks.Add(new(byEpisode.Key.Season, byEpisode.Key.Episode, chosen.Name, chosen.Magnet, chosen.Size, chosen.Seeders, chosen.Source, r.Group, r.Languages.ToList(), r.Resolution));
        }

        var still = held.OrderBy(x => x.Order.Season).ThenBy(x => x.Order.Episode).ToList();
        var note = picks.Count > 0 ? new FollowNote("found", string.Join(", ", picks.Select(x => Label(new(x.Season, x.Episode)))))
            : still.Count > 0 ? new FollowNote("held", still[0].Label, still[0].Reason)
            : matched ? new FollowNote("nothing", Latest(known) is { } newest ? Label(newest) : null)
            : new FollowNote("unknown");
        return new(picks, note, learned, library);
    }

    /// <summary>
    /// A release in the language taken before: MULTI stays MULTI, a French one French or MULTI, VOSTFR VOSTFR or MULTI,
    /// the original version never a French dub alone. Anything goes for the first one.
    /// </summary>
    public static bool SameLanguage(IReadOnlyList<string> languages, IReadOnlyList<string>? before)
    {
        if (before is null) return true;
        static bool French(IEnumerable<string> x) => x.Any(l => l is "VF" or "VFF" or "VFQ");
        var multi = languages.Contains("MULTI");
        if (before.Contains("MULTI")) return multi;
        if (French(before)) return multi || French(languages);
        if (before.Contains("VOSTFR")) return multi || languages.Contains("VOSTFR");
        return multi || !French(languages);
    }

    [GeneratedRegex(@"\s*\((?:19|20)\d\d\)\s*$")] private static partial Regex Year();
}
