using System.Globalization;

namespace Mira.Core;

/// <summary>
/// « Épisodes manquants et prochaines sorties »: what Jellyfin knows of a series beyond its files, once its TheTVDB
/// extension is installed (a virtual episode, LocationType « Virtual », for each episode the library lacks, with the
/// day it airs). Missing: aired before today; coming: today or later; announced without a day: neither. The specials
/// (season 0) never count. As Mira web says it (calendar.js, cases in tests/fixtures/calendar.json).
/// </summary>
public static class EpisodeCalendar
{
    public const int MaxRanges = 3;

    /// <summary>The day an episode airs, as TheTVDB gives it (Jellyfin keeps it at midnight UTC); null without one.</summary>
    public static DateOnly? AirDay(MediaItem episode) => episode.PremiereDate is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null;

    /// <summary>Of a series' episodes (its virtual ones at least): those missing and those coming, each in the order they air.</summary>
    public static (List<MediaItem> Missing, List<MediaItem> Coming) Read(IEnumerable<MediaItem> episodes, DateOnly today)
    {
        var known = episodes.Where(x => x.Type == "Episode" && x.LocationType == "Virtual" && x.ParentIndexNumber >= 1 && x.IndexNumber is not null && x.PremiereDate is not null)
            .OrderBy(x => AirDay(x)).ThenBy(x => x.ParentIndexNumber).ThenBy(x => x.IndexNumber).ToList();
        return (known.Where(x => AirDay(x) < today).ToList(), known.Where(x => AirDay(x) >= today).ToList());
    }

    /// <summary>« S2 · É5 », « S2 · É5–8 » for episodes in a row, <see cref="MaxRanges"/> at most, and how many episodes are left out.</summary>
    public static (List<string> Labels, int Others) Ranges(IEnumerable<MediaItem> episodes)
    {
        var runs = new List<(int Season, int From, int To, int Count)>();
        foreach (var x in episodes.OrderBy(x => x.ParentIndexNumber).ThenBy(x => x.IndexNumber))
        {
            if (runs.Count > 0 && runs[^1] is var last && last.Season == x.ParentIndexNumber && x.IndexNumber == last.To + 1) runs[^1] = last with { To = last.To + 1, Count = last.Count + 1 };
            else runs.Add((x.ParentIndexNumber ?? 0, x.IndexNumber ?? 0, x.IndexNumber ?? 0, 1));
        }
        var labels = runs.Take(MaxRanges).Select(r => $"S{r.Season} · " + L("É", "E") + (r.From == r.To ? $"{r.From}" : $"{r.From}–{r.To}")).ToList();
        return (labels, runs.Skip(MaxRanges).Sum(r => r.Count));
    }

    /// <summary>« 2 épisodes manquants : S1 · É3, S2 · É2 », « … et 6 autres »; empty without any.</summary>
    public static string MissingText(IReadOnlyCollection<MediaItem> missing)
    {
        if (missing.Count == 0) return "";
        var (labels, rest) = Ranges(missing);
        var list = string.Join(", ", labels) + (rest > 0 ? L($" et {rest} autres", $" and {rest} more") : "");
        return Lang.Count(missing.Count, "épisode manquant", "épisodes manquants", "missing episode", "missing episodes") + L(" : ", ": ") + list;
    }

    /// <summary>« Prochain épisode : S2 · É3, samedi »; empty without one.</summary>
    public static string NextText(IReadOnlyList<MediaItem> coming, DateOnly today) =>
        coming.Count == 0 || AirDay(coming[0]) is not { } day ? ""
            : L("Prochain épisode : ", "Next episode: ") + $"{Code(coming[0])}, {DayText(day, today)}";

    /// <summary>
    /// What the agent of « Suivre une série » sends of a series' virtual episodes (<see cref="TorLink.FollowSchedule"/>):
    /// numbered, dated, of a real season; the latest out and the next ones, <see cref="TorLink.FollowSchedule.MaxAiring"/> at most.
    /// </summary>
    public static List<TorLink.FollowAiring> Airing(IEnumerable<MediaItem> episodes, DateTimeOffset now)
    {
        var dated = episodes.Where(x => x.LocationType == "Virtual" && x.ParentIndexNumber is >= 1 and <= 100 && x.IndexNumber is >= 1 and <= 5000 && x.PremiereDate is not null)
            .Select(x => new TorLink.FollowAiring(x.ParentIndexNumber!.Value, x.IndexNumber!.Value, new DateTimeOffset(x.PremiereDate!.Value.UtcDateTime.Date, TimeSpan.Zero)))
            .OrderBy(x => x.Day).ThenBy(x => x.Season).ThenBy(x => x.Episode).ToList();
        var past = dated.Where(x => x.Day <= now).TakeLast(TorLink.FollowSchedule.MaxAiring / 2).ToList();
        return [.. past, .. dated.Where(x => x.Day > now).Take(TorLink.FollowSchedule.MaxAiring - past.Count)];
    }

    /// <summary>« S2 · É3 ».</summary>
    public static string Code(MediaItem episode) => $"S{episode.ParentIndexNumber} · " + L("É", "E") + $"{episode.IndexNumber}";

    /// <summary>
    /// A day as a sentence says it from <paramref name="today"/>: « aujourd’hui », « demain », « hier », « samedi » within
    /// the week, « jeudi 5 novembre » beyond, with its year in another year.
    /// </summary>
    public static string DayText(DateOnly day, DateOnly today)
    {
        var apart = day.DayNumber - today.DayNumber;
        if (apart == 0) return L("aujourd’hui", "today");
        if (apart == 1) return L("demain", "tomorrow");
        if (apart == -1) return L("hier", "yesterday");
        var at = day.ToDateTime(TimeOnly.MinValue);
        if (apart is >= 2 and <= 6) return at.ToString("dddd", Lang.Culture);
        var year = day.Year != today.Year;
        return Lang.IsFrench ? at.ToString(year ? "dddd d MMMM yyyy" : "dddd d MMMM", Lang.Culture) : at.ToString(year ? "dddd, MMMM d, yyyy" : "dddd, MMMM d", Lang.Culture);
    }

    /// <summary>The same at the head of a day's list: « Aujourd’hui », « Samedi ».</summary>
    public static string DayTitle(DateOnly day, DateOnly today) => DayText(day, today) is { Length: > 0 } text ? char.ToUpper(text[0], Lang.Culture) + text[1..] : "";

    /// <summary>Coming episodes by day, in order.</summary>
    public static List<(DateOnly Day, List<MediaItem> Episodes)> ByDay(IEnumerable<MediaItem> coming) =>
        coming.Where(x => x.PremiereDate is not null).OrderBy(AirDay).ThenBy(x => x.ParentIndexNumber).ThenBy(x => x.IndexNumber)
            .GroupBy(x => AirDay(x)!.Value).Select(g => (g.Key, g.ToList())).ToList();

    /// <summary>Today on this device, as a day.</summary>
    public static DateOnly Today(DateTime? now = null) => DateOnly.FromDateTime(now ?? DateTime.Now);
}
