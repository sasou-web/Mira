namespace Mira.Core;

/// <summary>
/// « Ton bilan »: what this account finished watching since a date, from Jellyfin's own record (each title's last play):
/// the time, the films and episodes, the series finished, the genres and series it spent the most time on. A title
/// watched twice counts once, at its last play. The same as Mira web's report.js: tests/fixtures/report.json.
/// </summary>
public static class WatchReport
{
    public sealed record SeriesTime(string Name, int Episodes, int Minutes);
    public sealed record Summary(int Minutes, int Films, int Episodes, List<string> Finished, List<string> Genres, List<SeriesTime> Series)
    {
        public bool Empty => Films + Episodes == 0;
    }
    /// <summary>« Ce mois-ci » starts on the 1st at midnight here, « Cette année » on 1 January.</summary>
    public static DateTimeOffset Start(DateTimeOffset now, bool year)
    {
        var local = now.ToLocalTime();
        var day = new DateTime(local.Year, year ? 1 : local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
    /// <param name="played">Films and episodes played to the end, as Jellyfin lists them (newest first or not).</param>
    /// <param name="series">The series of those episodes, with their genres and whether all is watched.</param>
    public static Summary Build(IEnumerable<MediaItem> played, IEnumerable<MediaItem> series, DateTimeOffset from)
    {
        var shows = series.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        var items = played.Where(x => x.Type is "Movie" or "Episode" && x.UserData.LastPlayedDate >= from).ToList();
        static int Minutes(MediaItem x) => (int)Math.Round(TimeSpan.FromTicks(Math.Max(0, x.RunTimeTicks ?? 0)).TotalMinutes, MidpointRounding.AwayFromZero);
        var genres = new Dictionary<string, int>();
        foreach (var item in items)
        {
            var kinds = item.Type == "Episode" && item.SeriesId is { } id && shows.TryGetValue(id, out var show) ? show.Genres : item.Genres;
            foreach (var genre in kinds.Distinct()) genres[genre] = genres.GetValueOrDefault(genre) + Minutes(item);
        }
        var episodes = items.Where(x => x.Type == "Episode" && x.SeriesId is not null).GroupBy(x => x.SeriesId!).ToList();
        var finished = episodes.Where(x => shows.TryGetValue(x.Key, out var show) && show.UserData.Played)
            .OrderByDescending(x => x.Max(e => e.UserData.LastPlayedDate)).Select(x => Name(x.Key, x)).ToList();
        var most = episodes.Select(x => new SeriesTime(Name(x.Key, x), x.Count(), x.Sum(Minutes)))
            .OrderByDescending(x => x.Minutes).ThenBy(x => x.Name, StringComparer.CurrentCulture).Take(3).ToList();
        return new(items.Sum(Minutes), items.Count(x => x.Type == "Movie"), items.Count(x => x.Type == "Episode"), finished,
            genres.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.CurrentCulture).Take(3).Select(x => x.Key).ToList(), most);
        string Name(string id, IEnumerable<MediaItem> group) => shows.TryGetValue(id, out var show) && show.Name.Length > 0 ? show.Name : group.First().SeriesName ?? "Série";
    }
    /// <summary>« 45 min », « 3 h », « 12 h 05 ».</summary>
    public static string Time(int minutes) => minutes < 60 ? $"{Math.Max(0, minutes)} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00}";
    /// <summary>« 1 film », « 3 épisodes ».</summary>
    public static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
