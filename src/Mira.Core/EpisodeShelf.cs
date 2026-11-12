namespace Mira.Core;

/// <summary>What a line of a season's list holds: an episode of the library, or episodes it doesn't have (TheTVDB).</summary>
public enum ShelfKind
{
    /// <summary>An episode of the library.</summary>
    Episode,
    /// <summary>Episodes aired and not in the library, side by side: one thin line where they would be.</summary>
    Missing,
    /// <summary>The next episode to come out in this season: a row of its own, drawn hollow, with its day.</summary>
    Coming,
    /// <summary>The episodes announced after it: one thin line.</summary>
    Later,
}

/// <summary>A line of a season's list; <see cref="Episodes"/> holds one episode, or the run of a thin line.</summary>
public sealed record ShelfLine(ShelfKind Kind, IReadOnlyList<MediaItem> Episodes)
{
    public MediaItem First => Episodes[0];
}

/// <summary>
/// Missing and coming episodes « à leur place » (ogsas, 2026-11-12, mock-up B): no card above a series' episodes any
/// more, each one where it belongs. In the season shown, the episodes it lacks are one thin line between those it has
/// (« É3–4 · pas dans ta bibliothèque »), the next one to come out a hollow row at its place, those after it one more
/// line; a season the library has none of is in the season list (« Saison 1 · absente »), shown as a calm card that
/// opens its list on demand. Nothing in amber, nothing above the list: some never want those episodes, and
/// « Montrer les épisodes manquants » off keeps only those coming. The virtual episodes are TheTVDB's, through
/// Jellyfin (<see cref="EpisodeCalendar"/>). The iPhone's EpisodeShelf, cases in tests/fixtures/calendar.json (shelf).
/// </summary>
public static class EpisodeShelf
{
    /// <summary>
    /// The lines of a season's page: <paramref name="shown"/> (its episodes of the library, in Jellyfin's order), with the
    /// series' virtual episodes of this season at their place by number. A slice of a long season (<see cref="EpisodePages"/>)
    /// takes those between its first and last episode, the first slice those before, the last those after.
    /// </summary>
    public static List<ShelfLine> Lines(IReadOnlyList<MediaItem> shown, IEnumerable<MediaItem> virtuals, int season, bool firstPage, bool lastPage, DateOnly today, bool showMissing)
    {
        var numbers = shown.Select(x => x.IndexNumber).OfType<int>().ToList();
        var low = firstPage || numbers.Count == 0 ? int.MinValue : numbers.Min();
        var high = lastPage || numbers.Count == 0 ? int.MaxValue : numbers.Max();
        var held = numbers.ToHashSet();
        var absent = Dated(virtuals).Where(x => x.ParentIndexNumber == season && x.IndexNumber is { } n && n >= low && n <= high && !held.Contains(n))
            .GroupBy(x => x.IndexNumber).Select(x => x.First()).OrderBy(x => x.IndexNumber).ToList();
        // The library's episodes in their order, each virtual one before the first with a higher number.
        var merged = new List<(MediaItem Item, bool Virtual)>();
        var next = 0;
        foreach (var episode in shown)
        {
            if (episode.IndexNumber is { } number)
                for (; next < absent.Count && absent[next].IndexNumber < number; next++) merged.Add((absent[next], true));
            merged.Add((episode, false));
        }
        for (; next < absent.Count; next++) merged.Add((absent[next], true));
        var lines = new List<ShelfLine>();
        var coming = false;
        foreach (var (item, isVirtual) in merged)
        {
            var kind = !isVirtual ? ShelfKind.Episode : EpisodeCalendar.AirDay(item) < today ? ShelfKind.Missing : coming ? ShelfKind.Later : ShelfKind.Coming;
            if (kind == ShelfKind.Missing && !showMissing) continue;
            if (kind == ShelfKind.Coming) coming = true;
            if (kind is ShelfKind.Missing or ShelfKind.Later && lines.Count > 0 && lines[^1].Kind == kind) lines[^1] = lines[^1] with { Episodes = [.. lines[^1].Episodes, item] };
            else lines.Add(new(kind, [item]));
        }
        return lines;
    }

    /// <summary>
    /// The seasons the library has none of, but TheTVDB knows dated episodes of: numbered seasons only, in order; with
    /// « Montrer les épisodes manquants » off, only those with an episode still to come out.
    /// </summary>
    public static List<int> AbsentSeasons(IReadOnlyList<MediaItem> episodes, IEnumerable<MediaItem> virtuals, DateOnly today, bool showMissing)
    {
        var held = episodes.Select(EpisodePages.Season).ToHashSet();
        return Dated(virtuals).Where(x => !held.Contains(x.ParentIndexNumber!.Value) && (showMissing || EpisodeCalendar.AirDay(x) >= today))
            .Select(x => x.ParentIndexNumber!.Value).Distinct().Order().ToList();
    }

    /// <summary>
    /// The season list of a series page: the library's pages, and an empty one for each season it has none of
    /// (<see cref="EpisodePage.Absent"/>), in season order: « Saison 1 · absente », « Saison 3 · à venir ».
    /// </summary>
    public static List<EpisodePage> Pages(IReadOnlyList<EpisodePage> pages, IReadOnlyList<MediaItem> episodes, IReadOnlyList<MediaItem> virtuals, DateOnly today, bool showMissing)
    {
        var all = pages.ToList();
        foreach (var season in AbsentSeasons(episodes, virtuals, today, showMissing))
        {
            var aired = Dated(virtuals).Any(x => x.ParentIndexNumber == season && EpisodeCalendar.AirDay(x) < today);
            all.Add(new EpisodePage(season, 0, 0, aired ? L($"Saison {season} · absente", $"Season {season} · not in library") : L($"Saison {season} · à venir", $"Season {season} · coming"), Absent: true));
        }
        // The specials stay where Jellyfin lists them, first.
        return [.. all.Where(x => x.Season == 0), .. all.Where(x => x.Season != 0).OrderBy(x => x.Season).ThenBy(x => x.Skip)];
    }

    /// <summary>The setting, Réglages → Lecture (<see cref="PlayerSettings.ShowMissingEpisodes"/>).</summary>
    public static string Setting => L("Montrer les épisodes manquants", "Show missing episodes");
    public static string SettingHelp => L("Sur la page d’une série, les épisodes sortis qui ne sont pas dans ta bibliothèque, d’après TheTVDB. Ceux à venir restent.", "On a series’ page, aired episodes that aren’t in your library, from TheTVDB. Coming ones stay.");

    /// <summary>« É3 », « É3–4 ».</summary>
    public static string Code(IReadOnlyList<MediaItem> run)
    {
        var (from, to) = (run[0].IndexNumber, run[^1].IndexNumber);
        return L("É", "E") + (from == to ? $"{from}" : $"{from}–{to}");
    }

    /// <summary>« É3–4 · pas dans ta bibliothèque · sortis du 25 oct. au 1 nov. », a thin line of missing episodes.</summary>
    public static string MissingText(IReadOnlyList<MediaItem> run, DateOnly today) =>
        $"{Code(run)} · {L("pas dans ta bibliothèque", "not in your library")} · {Aired(run, today)}";

    /// <summary>« É7–9 · à venir · du 22 nov. au 6 déc. », a thin line of the episodes announced after the next one.</summary>
    public static string LaterText(IReadOnlyList<MediaItem> run, DateOnly today) =>
        $"{Code(run)} · {L("à venir", "coming")} · {(run.Count == 1 ? EpisodeCalendar.DayText(Day(run[0]), today) : Span(run, today, L("du ", ""), L(" au ", " – ")))}";

    /// <summary>« sorti mardi 20 octobre », « sortis du 8 janv. au 25 juin 2026 ».</summary>
    public static string Aired(IReadOnlyList<MediaItem> run, DateOnly today) => run.Count == 1
        ? L("sorti ", "aired ") + EpisodeCalendar.DayText(Day(run[0]), today)
        : L("sortis du ", "aired ") + Span(run, today, "", L(" au ", " – "));

    /// <summary>The title of a hollow row: « Le nom » that TheTVDB gave, else « Épisode 6 ».</summary>
    public static string Name(MediaItem episode) => episode.Name.Length > 0 ? episode.Name : L($"Épisode {episode.IndexNumber}", $"Episode {episode.IndexNumber}");

    /// <summary>Under the title of the hollow row.</summary>
    public static string NotOut => L("Pas encore sorti.", "Not out yet.");

    /// <summary>The card of a season the library has none of.</summary>
    public static string SeasonTitle(int season) => L($"La saison {season} n’est pas dans ta bibliothèque", $"Season {season} isn’t in your library");

    /// <summary>« 25 épisodes, sortis du 8 janv. au 25 juin 2026. D’après TheTVDB, par ton serveur Jellyfin. »</summary>
    public static string SeasonText(IReadOnlyList<MediaItem> run, DateOnly today) =>
        $"{Lang.Count(run.Count, "épisode", "épisodes", "episode", "episodes")}, {Aired(run, today)}. {Source}";

    public static string Source => L("D’après TheTVDB, par ton serveur Jellyfin.", "From TheTVDB, through your Jellyfin server.");

    /// <summary>The button of a thin line or of the card: its episodes one by one.</summary>
    public static string Show(int count, bool open) => open ? L("Masquer", "Hide") : count == 1 ? L("Voir", "Show") : L($"Voir les {count}", $"Show all {count}");

    /// <summary>The button of an absent season's card: « Voir les 25 épisodes ».</summary>
    public static string ShowSeason(int count, bool open) => open ? L("Masquer", "Hide") : count == 1 ? L("Voir l’épisode", "Show the episode") : L($"Voir les {count} épisodes", $"Show the {count} episodes");

    /// <summary>A line of the opened list: « É3 · Le nom », and when it aired or comes out.</summary>
    public static (string Name, string When) Entry(MediaItem episode, DateOnly today)
    {
        var name = L("É", "E") + episode.IndexNumber + (episode.Name.Length > 0 ? " · " + episode.Name : "");
        var day = Day(episode);
        return (name, day < today ? L("Sorti ", "Aired ") + EpisodeCalendar.DayText(day, today) : EpisodeCalendar.DayTitle(day, today));
    }

    /// <summary>A short day, « 25 oct. », « Oct 25 », with its year in another year than today's.</summary>
    public static string ShortDay(DateOnly day, DateOnly today) => Short(day, day.Year != today.Year);

    private static string Short(DateOnly day, bool year) => day.ToDateTime(TimeOnly.MinValue).ToString(year ? L("d MMM yyyy", "MMM d, yyyy") : L("d MMM", "MMM d"), Lang.Culture);

    // From the first day to the last, the year said once when both share it: « 8 janv. au 25 juin 2026 ».
    private static string Span(IReadOnlyList<MediaItem> run, DateOnly today, string before, string between)
    {
        var (first, last) = (Day(run[0]), Day(run[^1]));
        if (first > last) (first, last) = (last, first);
        var apart = first.Year != last.Year;
        return before + (first == last ? ShortDay(first, today) : $"{Short(first, apart)}{between}{Short(last, apart || last.Year != today.Year)}");
    }

    private static DateOnly Day(MediaItem episode) => EpisodeCalendar.AirDay(episode)!.Value;

    // The virtual episodes that count: numbered, of a numbered season, with their day (EpisodeCalendar.Read's).
    private static IEnumerable<MediaItem> Dated(IEnumerable<MediaItem> virtuals) =>
        virtuals.Where(x => x.Type == "Episode" && x.LocationType == "Virtual" && x.ParentIndexNumber >= 1 && x.IndexNumber is not null && x.PremiereDate is not null);
}
