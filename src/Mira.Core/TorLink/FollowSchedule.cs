namespace Mira.Core.TorLink;

/// <summary>
/// When the agent checks a series followed again (« Suivre une série »), from the air dates TheTVDB gives Jellyfin
/// (TheTVDB's plugin, sent by the agent as <see cref="FollowAiring"/>): every <see cref="Often"/> while an
/// episode not yet here comes out (from half a day before its air date, which TheTVDB writes in its country's day, to
/// two days after), at the start of the next such window otherwise, within a day at most. Without any date (no plugin,
/// or a series TheTVDB doesn't know), every <see cref="Every"/> as before; behind (an episode out for days and not here),
/// every <see cref="Every"/> too.
/// </summary>
public static class FollowSchedule
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6), Often = TimeSpan.FromHours(2), Longest = TimeSpan.FromDays(1);
    public static readonly TimeSpan Before = TimeSpan.FromHours(12), After = TimeSpan.FromDays(2);
    /// <summary>The episodes kept for a series: the latest out and the next ones.</summary>
    public const int MaxAiring = 10;

    /// <summary>The next check after one at <paramref name="checkedAt"/>, the series' latest episode here or taken being <paramref name="latest"/>.</summary>
    public static DateTimeOffset Next(DateTimeOffset checkedAt, FollowEpisode? latest, IReadOnlyList<FollowAiring>? airing)
    {
        if (airing is not { Count: > 0 }) return checkedAt + Every;
        var pending = Pending(latest, airing).ToList();
        if (pending.Any(x => Opens(x) <= checkedAt && checkedAt < Closes(x))) return checkedAt + Often;
        var latestAt = checkedAt + (pending.Any(x => Closes(x) <= checkedAt) ? Every : Longest);
        var opens = pending.Select(Opens).Where(x => x > checkedAt).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        return opens < latestAt ? opens : latestAt;
    }

    /// <summary>The episode to say on the follow (« S01E13 sort samedi »): the first not here whose window isn't over, else none.</summary>
    public static FollowAiring? Coming(FollowEpisode? latest, IReadOnlyList<FollowAiring>? airing, DateTimeOffset now) =>
        Pending(latest, airing).Where(x => now < Closes(x)).OrderBy(x => x.Day).ThenBy(x => x.Season).ThenBy(x => x.Episode).FirstOrDefault();

    /// <summary>The episodes TheTVDB says are out by <paramref name="now"/> (from half a day before their date, for countries ahead of UTC): the number a release should have.</summary>
    public static HashSet<FollowEpisode> Out(IReadOnlyList<FollowAiring>? airing, DateTimeOffset now) =>
        (airing ?? []).Where(x => Opens(x) <= now).Select(x => new FollowEpisode(x.Season, x.Episode)).ToHashSet();

    private static IEnumerable<FollowAiring> Pending(FollowEpisode? latest, IReadOnlyList<FollowAiring>? airing) =>
        (airing ?? []).Where(x => latest is null || x.Season > latest.Season || x.Season == latest.Season && x.Episode > latest.Episode);
    private static DateTimeOffset Opens(FollowAiring x) => x.Day - Before;
    private static DateTimeOffset Closes(FollowAiring x) => x.Day + After;
}
