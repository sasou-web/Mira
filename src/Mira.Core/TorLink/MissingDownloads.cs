using System.Globalization;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>
/// What a click on « Télécharger É6–9 » or « Télécharger la saison 2 » asks: the season and its missing episodes.
/// <paramref name="WholeSeason"/>: the library has nothing of that season, a pack of it comes first.
/// </summary>
public sealed record MissingAsk(int Season, IReadOnlyList<int> Episodes, bool WholeSeason);

/// <summary>
/// The series as the page knows it: its names, its year, the episodes of its earlier seasons (<paramref name="Before"/>: a
/// release numbered from the first episode of the series, « Show - 31 », is S02E03 after 28), the name typed instead
/// (<paramref name="Query"/>, « Rien trouvé » then « Chercher »), and the other names the PC of Jellyfin searched it under
/// (<paramref name="Also"/>: an anime's, from AniList, « Sousou no Frieren » for « Frieren »).
/// </summary>
public sealed record MissingSeries(string Name, string? OriginalTitle, int? Year, int Before, string? Query = null, IReadOnlyList<string>? Also = null);

/// <summary>A release for an episode or a season: the relay's search that found it (to ask for it) and how it ranks.</summary>
public sealed record MissingOption(string Search, RankedRelease Entry);

/// <summary>The releases of an episode, the one Mira puts forward first.</summary>
public sealed record MissingChoice(int Episode, MissingOption Best, IReadOnlyList<MissingOption> Others);

/// <summary>
/// What was found: season packs (only for a whole season, best first), one choice for each episode found, and the episodes
/// nothing was found for.
/// </summary>
public sealed record MissingPicks(IReadOnlyList<MissingOption> Packs, IReadOnlyList<MissingChoice> Episodes, IReadOnlyList<int> NotFound)
{
    public bool Empty => Packs.Count == 0 && Episodes.Count == 0;
}

/// <summary>
/// Where the downloads asked for some episodes are, as a tile and a season's pill show it. <see cref="Adding"/>: downloaded, Mira
/// places the files on the PC of Jellyfin; <see cref="Placed"/>: placed, Jellyfin has yet to list the episode (the tile goes
/// once it does). Both say « Adding to your library », never the button again.
/// </summary>
public enum MissingStage { Waiting, Downloading, Adding, Failed, Placed }

/// <summary>Why a season pack is put forward among the three: the best picture, the best for its size, the smallest.</summary>
public enum MissingPackKind { Quality, Balanced, Smallest }

/// <summary>A season pack put forward, and why.</summary>
public sealed record MissingPackChoice(MissingPackKind Kind, MissingOption Option);

/// <summary>
/// <paramref name="Percent"/>: of all of them, by size; <paramref name="Title"/> and <paramref name="Line"/>: what the tile says;
/// <paramref name="Ids"/>: the requests, to cancel or ask again. <paramref name="Rescan"/>: placed a while ago and Jellyfin has
/// still not listed it, the tile offers to scan the libraries.
/// </summary>
public sealed record MissingProgress(MissingStage Stage, int Percent, string Title, string Line, IReadOnlyList<string> Ids, bool Rescan = false);

/// <summary>
/// « Télécharger les épisodes manquants » from a series' page (the missing episodes at their place, the season absent): TorLink
/// searches on the PC of Jellyfin, through the relay as on the Downloads page; Mira keeps the results under a name of the
/// series (as <see cref="SeriesFollowing"/> does), the year give or take one, and puts forward for each episode the best
/// release, the one of the group and picture most of the episodes share when it is nearly as good. A gap in a season gets one
/// release per episode, never a pack that would bring again what the library has; a season the library lacks gets a pack
/// first. Never a cam, a release nobody shares, nor one over three times the usual size of an episode. Cases shared with the
/// iPhone in tests/fixtures/missing.json.
/// </summary>
public static partial class MissingDownloads
{
    public const int MaxOthers = 8;
    /// <summary>A gap a click can ask at once: past this, a season pack is what the page offers.</summary>
    public const int MaxEpisodes = 30;
    /// <summary>A release within this many points of the best is as good: the one of the family most episodes share goes first.</summary>
    public const int FamilyMargin = 5;

    /// <summary>The searches of TorLink: an anime's in the anime sites, then the series ones.</summary>
    public static IReadOnlyList<string> Categories(bool anime) => anime ? ["anime", "tv"] : ["tv"];

    /// <summary>
    /// What to search: the name typed alone; otherwise « Arcane S02 » then « Arcane » (an anime's releases seldom say the
    /// season), without the year Jellyfin may put after the name; and the same with its original title when it is another
    /// name in Latin letters (a library in French: « Les Anneaux de pouvoir », released as « The Lord of the Rings: The Rings
    /// of Power »). A Japanese or Korean original title is no search: the PC of Jellyfin finds an anime's other names itself.
    /// </summary>
    public static List<string> Queries(MissingSeries series, int season)
    {
        if (RemoteDownloads.CleanQuery(series.Query) is { } typed) return [typed];
        var names = new List<string>();
        foreach (var (name, original) in new[] { (series.Name, false), (series.OriginalTitle, true) })
            if (name is not null && (RemoteDownloads.CleanQuery(Year().Replace(name, "")) ?? RemoteDownloads.CleanQuery(name)) is { } clean
                && (!original || Latin(clean)) && names.All(x => ReleaseRanking.TitleKey(x) != ReleaseRanking.TitleKey(clean)))
                names.Add(clean);
        return [.. names.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x} S{season:00}")), .. names];
    }

    /// <summary>Mostly Latin letters: a name the sites of TorLink can find releases under.</summary>
    private static bool Latin(string name)
    {
        var letters = name.Where(char.IsLetter).ToList();
        return letters.Count > 0 && letters.Count(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 'À' and <= 'ɏ') or (>= 'Ḁ' and <= 'ỿ')) * 2 >= letters.Count;
    }

    /// <summary>The releases of TorLink's results (with the search of each) for what was asked.</summary>
    public static MissingPicks Choose(MissingAsk ask, MissingSeries series, IReadOnlyList<(string Search, RelayHit Hit)> hits)
    {
        var wanted = ask.Episodes.Where(x => x >= 1).ToHashSet();
        var (first, last) = wanted.Count > 0 ? (wanted.Min(), wanted.Max()) : (1, int.MaxValue);
        var keys = new[] { series.Name, Year().Replace(series.Name, ""), series.OriginalTitle, series.Query }.Concat(series.Also ?? [])
            .Select(ReleaseRanking.TitleKey).Where(x => x.Length > 0).ToHashSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packs = new List<MissingOption>();
        var episodes = new List<(int Episode, MissingOption Option)>();
        var order = 0;
        foreach (var (search, hit) in hits)
        {
            if (!seen.Add(hit.Id)) continue;
            var release = ReleaseRanking.Parse(hit.Name);
            var titled = keys.Contains(ReleaseRanking.TitleKey(release.Title)) || release.Alternate is { } other && keys.Contains(ReleaseRanking.TitleKey(other));
            if (!titled || release.Year is { } year && series.Year is { } born && Math.Abs(year - born) > 1) continue;
            if (release.Source == "cam" || ReleaseRanking.ReportsHealth(hit.Source) && hit.Seeders <= 0) continue;
            var option = new MissingOption(search, new RankedRelease(hit, release, order++, ReleaseRanking.Score(hit, release, hit.Category)));
            if (release.Pack || release.Episode is null && release.Half is null && release.Season is not null)
            {
                // Only the season asked, whole: « S02 », or « S02E01-E09 » when it covers the episodes the page knows of.
                if (ask.WholeSeason && release.Season == ask.Season && (release.Episode is null || release.Episode <= first && (release.LastEpisode ?? 0) >= (wanted.Count > 0 ? last : 0)))
                    packs.Add(option);
                continue;
            }
            // One episode, never two in one (« S02E06-E07 ») nor one between two (« 07.5 »).
            if (release.Episode is not { } number || release.LastEpisode > number || release.Half is not null) continue;
            // Numbered without a season: from the first episode of the series, or of the season for the first one.
            int? episode = release.Season is { } s ? (s == ask.Season ? number : null)
                : ask.Season == 1 ? number
                : series.Before > 0 && number > series.Before ? number - series.Before : null;
            if (episode is { } e && wanted.Contains(e)) episodes.Add((e, option));
        }

        // Over three times the usual size of an episode: a season in one file, or a mistake.
        var sizes = episodes.Select(x => x.Option.Entry.Hit.Size).Where(x => x > 0).Order().ToList();
        var limit = sizes.Count >= 3 ? Math.Min(SeriesFollowing.MaxSize, sizes[sizes.Count / 2] * 3) : SeriesFollowing.MaxSize;
        episodes.RemoveAll(x => x.Option.Entry.Hit.Size > limit);

        // The group and picture most episodes share: a season looks the same from one episode to the next.
        var family = episodes.GroupBy(x => Family(x.Option.Entry.Release)).Select(x => (Key: x.Key, Count: x.Select(y => y.Episode).Distinct().Count(), Best: x.Max(y => y.Option.Entry.Score)))
            .Where(x => x.Count >= 2).OrderByDescending(x => x.Count).ThenByDescending(x => x.Best).Select(x => x.Key).FirstOrDefault();
        var choices = new List<MissingChoice>();
        foreach (var byEpisode in episodes.GroupBy(x => x.Episode).OrderBy(x => x.Key))
        {
            var sorted = Ranked(byEpisode.Select(x => x.Option)).ToList();
            var top = sorted[0];
            if (family is not null && sorted.FirstOrDefault(x => Family(x.Entry.Release) == family) is { } alike && alike.Entry.Score >= top.Entry.Score - FamilyMargin) top = alike;
            choices.Add(new(byEpisode.Key, top, sorted.Where(x => x != top).Take(MaxOthers).ToList()));
        }
        var found = choices.Select(x => x.Episode).ToHashSet();
        return new(Ranked(packs).Take(MaxOthers + 1).ToList(), choices, wanted.Where(x => !found.Contains(x)).Order().ToList());
    }

    /// <summary>
    /// Whether the menu has what it needs after a search: every episode asked, or enough season packs to choose from
    /// (<see cref="PackChoices"/>); otherwise the wider search (the name alone) runs too.
    /// </summary>
    public static bool SearchedEnough(MissingPicks picks) => picks.NotFound.Count == 0 || picks.Packs.Count >= 3;

    /// <summary>
    /// Up to three season packs to choose from, the best picture first (checked at first): <see cref="MissingPackKind.Quality"/>
    /// = the best ranked; <see cref="MissingPackKind.Smallest"/> = the smallest at most 60 % of its size;
    /// <see cref="MissingPackKind.Balanced"/> = between them, the best ranked (the larger at equal rank) at least 1.5 times the
    /// smallest. Never a pack too small for the <paramref name="episodes"/> of the season (<see cref="Doubtful"/>) as the
    /// lighter ones: it is still among the others. One pack found, one choice.
    /// </summary>
    public static IReadOnlyList<MissingPackChoice> PackChoices(IReadOnlyList<MissingOption> packs, int episodes)
    {
        if (packs.Count == 0) return [];
        var best = packs[0];
        var choices = new List<MissingPackChoice> { new(MissingPackKind.Quality, best) };
        var size = best.Entry.Hit.Size;
        if (size <= 0) return choices;
        var lighter = packs.Skip(1).Where(x => x.Entry.Hit.Size > 0 && x.Entry.Hit.Size <= size * Lighter && !Doubtful(x, episodes)).ToList();
        if (lighter.Count == 0) return choices;
        var smallest = lighter.OrderBy(x => x.Entry.Hit.Size).ThenByDescending(x => x.Entry.Score).First();
        var balanced = lighter.Where(x => x != smallest && x.Entry.Score >= smallest.Entry.Score && x.Entry.Hit.Size >= smallest.Entry.Hit.Size * 1.5)
            .OrderByDescending(x => x.Entry.Score).ThenByDescending(x => x.Entry.Hit.Size).FirstOrDefault();
        if (balanced is not null) choices.Add(new(MissingPackKind.Balanced, balanced));
        choices.Add(new(MissingPackKind.Smallest, smallest));
        return choices;
    }
    private const double Lighter = .6;

    /// <summary>« Meilleure qualité », « Meilleur compromis », « Le plus léger ».</summary>
    public static string PackKindName(MissingPackKind kind) => kind switch
    {
        MissingPackKind.Quality => L("Meilleure qualité", "Best quality"),
        MissingPackKind.Balanced => L("Meilleur compromis", "Best balance"),
        _ => L("Le plus léger", "Smallest"),
    };

    /// <summary>
    /// What a season pack holds, as its name says, for a season of <paramref name="episodes"/> episodes: « É1–48 · ≈ 400 Mo par
    /// épisode »; a name without its episodes: « Épisodes non dits dans le nom · ≈ 400 Mo chacun s’il a les 48 »; and when that
    /// is too little for its picture (a season split in two, the second half elsewhere): « Peut-être pas les 48 épisodes ».
    /// </summary>
    public static string Covers(MissingOption pack, int episodes)
    {
        var release = pack.Entry.Release; var size = pack.Entry.Hit.Size;
        var each = episodes > 0 && size > 0 ? RemoteLines.Size(size / episodes) : null;
        if (release.Episode is { } from && release.LastEpisode is { } to)
            return each is null ? Code(from, to) : L($"{Code(from, to)} · ≈ {each} par épisode", $"{Code(from, to)} · ≈ {each} per episode");
        if (each is null) return L("Épisodes non dits dans le nom", "Episodes not in its name");
        if (Doubtful(pack, episodes)) return L($"Peut-être pas les {episodes} épisodes : ≈ {each} chacun s’il les a", $"Maybe not all {episodes} episodes: ≈ {each} each if so");
        return L($"Épisodes non dits dans le nom · ≈ {each} chacun s’il a les {episodes}", $"Episodes not in its name · ≈ {each} each for all {episodes}");
    }

    /// <summary>A pack whose name doesn't say its episodes, too small for all of them at its picture.</summary>
    public static bool Doubtful(MissingOption pack, int episodes)
    {
        var release = pack.Entry.Release; var size = pack.Entry.Hit.Size;
        if (release.Episode is not null || episodes <= 1 || size <= 0) return false;
        var floor = release.Resolution switch { >= 2160 => 500, >= 1080 => 120, >= 720 => 70, _ => 40 } * (1L << 20);
        return size / episodes < floor;
    }

    private static IEnumerable<MissingOption> Ranked(IEnumerable<MissingOption> options) =>
        options.OrderByDescending(x => x.Entry.Score).ThenByDescending(x => x.Entry.Hit.Seeders).ThenBy(x => x.Entry.Order);

    private static string Family(ReleaseInfo release) => $"{release.Group?.ToLowerInvariant()}|{release.Resolution}";

    /// <summary>
    /// Where what a series' page asked stands: the downloads of <paramref name="seriesId"/> for these episodes of this season
    /// (or the whole season), from the relay's list, TorLink's downloads and what Mira placed; null when nothing was asked.
    /// <paramref name="episodes"/> null: anything of that season (its pill).
    /// </summary>
    public static MissingProgress? Progress(RelayStatus? status, string seriesId, int season, IEnumerable<int>? episodes, DateTimeOffset? now = null)
    {
        if (status?.Missing is not { Count: > 0 } missing) return null;
        var id = seriesId.Replace("-", "", StringComparison.Ordinal);
        var asked = episodes?.ToHashSet();
        var mine = missing.Where(x => string.Equals(x.SeriesId.Replace("-", "", StringComparison.Ordinal), id, StringComparison.OrdinalIgnoreCase) && x.Season == season
            && (asked is null || x.Episodes.Count == 0 || x.Episodes.Any(asked.Contains))).ToList();
        if (mine.Count == 0) return null;
        var parts = mine.Select(x => (Ask: x, Part: PartOf(x, status))).ToList();
        var active = parts.Where(x => x.Part.Stage is MissingStage.Waiting or MissingStage.Downloading or MissingStage.Adding).ToList();
        var ids = mine.Select(x => x.Id).ToList();
        if (active.Count == 0)
        {
            // One placed, another failed: what was asked is there, as far as the tile can tell.
            if (parts.All(x => x.Part.Stage != MissingStage.Placed) && parts.FirstOrDefault(x => x.Part.Stage == MissingStage.Failed) is { Ask: not null } failed)
                return new(MissingStage.Failed, 0, L("Le téléchargement a échoué", "The download failed"), failed.Part.Error ?? "", ids);
            // Placed: Jellyfin reads the folder after its monitor's delay (a minute); a page still showing the tile long after
            // means it did not see it (or numbered it otherwise): said, with a scan of the libraries.
            var placedAt = parts.Select(x => x.Ask.PlacedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
            var late = now is { } at && placedAt != default && at - placedAt > Late;
            return new(MissingStage.Placed, 100, AddingTitle, late
                ? L("Jellyfin ne l’a pas encore ajouté à la série.", "Jellyfin hasn’t added it to the series yet.")
                : L("Rangé. Jellyfin l’ajoute à la série…", "Filed. Jellyfin is adding it to the series…"), ids, Rescan: late);
        }
        // By size, what each has; what is not known yet counts as one equal share.
        var counted = parts.Where(x => x.Part.Stage != MissingStage.Failed).ToList();
        var total = counted.Sum(x => Math.Max(1, x.Part.Size));
        var done = counted.Sum(x => Math.Max(1, x.Part.Size) * x.Part.Percent / 100.0);
        var percent = (int)Math.Clamp(Math.Round(done * 100 / Math.Max(1, total)), 0, 100);
        if (active.Any(x => x.Part.Stage == MissingStage.Downloading))
        {
            var left = active.Select(x => x.Part.Left).OfType<double>().DefaultIfEmpty(-1).Max();
            var line = left >= 0 ? L($"{percent} % · encore {RemoteLines.Remaining(left)}", $"{percent}% · about {RemoteLines.Remaining(left)} left") : L($"{percent} %", $"{percent}%");
            return new(MissingStage.Downloading, percent, L("Téléchargement sur le PC de Jellyfin", "Downloading on your Jellyfin PC"), line, ids);
        }
        if (active.Any(x => x.Part.Stage == MissingStage.Waiting))
        {
            var sending = active.Any(x => x.Ask.State is "claimed" or "sent");
            return new(MissingStage.Waiting, percent, sending ? L("Envoi à TorLink…", "Sending to TorLink…") : L("En attente du PC de Jellyfin", "Waiting for your Jellyfin PC"),
                sending ? "" : L("Il le prend dès que Mira y est ouvert.", "It starts as soon as Mira is open there."), ids);
        }
        return new(MissingStage.Adding, 100, AddingTitle, L("Téléchargé. Mira le range sur le PC de Jellyfin…", "Downloaded. Mira is filing it on your Jellyfin PC…"), ids);
    }

    /// <summary>Placed this long ago and still not in the series: Jellyfin did not see it (its folder monitor takes about a minute).</summary>
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(3);

    private static string AddingTitle => L("Rangement dans ta bibliothèque…", "Adding to your library…");

    /// <summary>
    /// When a page waiting for Jellyfin to list what was placed reads the series' episodes again: often at first (Jellyfin
    /// lists it about a minute after it is placed), then once a minute.
    /// </summary>
    public static TimeSpan LookAgain(TimeSpan sincePlaced) => sincePlaced < TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(10) : TimeSpan.FromMinutes(1);

    /// <summary>Episodes Jellyfin lists now that the page did not show: they take the place of the hollow tiles.</summary>
    public static bool Arrived(IEnumerable<MediaItem> shown, IEnumerable<MediaItem> now)
    {
        var known = shown.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return now.Any(x => !known.Contains(x.Id));
    }

    private readonly record struct Part(MissingStage Stage, int Percent, long Size, double? Left, string? Error);

    private static Part PartOf(RelayMissing ask, RelayStatus status)
    {
        if (ask.State == "failed") return new(MissingStage.Failed, 0, ask.Size, null, ask.Error ?? L("TorLink a refusé ce téléchargement.", "TorLink refused this download."));
        if (status.Placed.FirstOrDefault(x => string.Equals(x.Id, ask.InfoHash, StringComparison.OrdinalIgnoreCase)) is { } placed)
            return placed.Ok ? new(MissingStage.Placed, 100, ask.Size, null, null) : new(MissingStage.Failed, 0, ask.Size, null, placed.Status);
        // Placed, and no longer among the latest the agent lists.
        if (ask.PlacedAt is not null) return new(MissingStage.Placed, 100, ask.Size, null, null);
        if (status.Downloads.FirstOrDefault(x => string.Equals(x.Id, ask.InfoHash, StringComparison.OrdinalIgnoreCase)) is { } download)
        {
            var size = download.Size > 0 ? download.Size : ask.Size;
            return download.Status switch
            {
                "completed" or "seeding" => new(MissingStage.Adding, 100, size, null, null),
                "failed" => new(MissingStage.Failed, 0, size, null, RemoteLines.Failure(download.Error) is { Length: > 0 } why ? why : L("TorLink a abandonné ce téléchargement.", "TorLink gave up on this download.")),
                _ => new(MissingStage.Downloading, Math.Clamp(download.Progress, 0, 100), size, RemoteLines.TimeLeft(download), null),
            };
        }
        // Finished and gone from TorLink's list (to its history): Mira places it.
        if (ask.Finished || ask.Listed) return new(MissingStage.Adding, 100, ask.Size, null, null);
        // Sent, not listed by TorLink yet; or still at the relay.
        return new(MissingStage.Waiting, 0, ask.Size, null, null);
    }

    // ---- What the page says --------------------------------------------------------------------------------------

    /// <summary>« Télécharger É6–9 » (« Get E6–9 »), the button of the missing episodes at their place.</summary>
    public static string GetEpisodes(int from, int to) => L($"Télécharger {Code(from, to)}", $"Get {Code(from, to)}");

    /// <summary>« Télécharger la saison 2 » (« Get season 2 »), the button of a season the library lacks.</summary>
    public static string GetSeason(int season) => L($"Télécharger la saison {season}", $"Get season {season}");

    /// <summary>« É6–9 », « É6 ».</summary>
    public static string Code(int from, int to) => L("É", "E") + (from == to ? $"{from}" : $"{from}–{to}");

    public static string Searching => L("Recherche sur le PC de Jellyfin…", "Searching on your Jellyfin PC…");

    /// <summary>The episode's line in the menu: « É6 ».</summary>
    public static string EpisodeLabel(int episode) => Code(episode, episode);

    /// <summary>« Rien trouvé pour « Arcane S02 ». » with the name searched, to change it.</summary>
    public static string NothingFound(string query) => L($"Rien trouvé pour « {query} ».", $"Nothing found for “{query}”.");

    public static string NothingHelp => L("Essaie un autre nom (celui des sorties, en anglais souvent), ou cherche dans Téléchargements.",
        "Try another name (the one releases use, often English), or search in Downloads.");

    public static string NotFound(IReadOnlyList<int> episodes) => episodes.Count == 0 ? ""
        : L($"Rien trouvé pour {Codes(episodes)}.", $"Nothing found for {Codes(episodes)}.");

    private static string Codes(IReadOnlyList<int> episodes) => string.Join(", ", Runs(episodes).Select(x => Code(x.From, x.To)));

    /// <summary>Episodes in runs: 6, 7, 9 → 6–7, 9.</summary>
    public static List<(int From, int To)> Runs(IEnumerable<int> episodes)
    {
        var runs = new List<(int From, int To)>();
        foreach (var e in episodes.Distinct().Order())
            if (runs.Count > 0 && runs[^1].To == e - 1) runs[^1] = (runs[^1].From, e); else runs.Add((e, e));
        return runs;
    }

    /// <summary>A release in the menu: « 1080p · WEB-DL · MULTI », then « 1,4 Go · 120 sources · YTS ».</summary>
    public static (string Quality, string Detail) Option(MissingOption option)
    {
        var quality = ReleaseRanking.QualityLine(option.Entry.Release);
        return (quality.Length > 0 ? quality : L("Qualité inconnue", "Unknown quality"), RemoteLines.Hit(option.Entry.Hit));
    }

    public static string Change => L("Changer", "Change");

    /// <summary>« Autres choix (6) ».</summary>
    public static string OtherChoices(int count) => L($"Autres choix ({count})", $"Other choices ({count})");

    /// <summary>Under the menu's heading: what it offers (a pack, episodes one by one), then where it was found.</summary>
    public static string Lead(bool? pack) => pack switch
    {
        true => L("Toute la saison en un téléchargement, trouvée par TorLink sur le PC de Jellyfin.", "The whole season in one download, found by TorLink on your Jellyfin PC."),
        false => L("Chaque épisode à part, trouvé par TorLink sur le PC de Jellyfin.", "Each episode on its own, found by TorLink on your Jellyfin PC."),
        _ => L("Trouvé par TorLink sur le PC de Jellyfin.", "Found by TorLink on your Jellyfin PC."),
    };

    /// <summary>The foot's place, after its size in bold: « dans Arcane › Saison 02 ».</summary>
    public static string Destination(string show, int season) => L($"dans {show} › Saison {season:00}", $"into {show} › Season {season:00}");

    /// <summary>The menu's foot: « 5,6 Go · dans Arcane › Saison 02 ».</summary>
    public static string Footer(long size, string show, int season) =>
        L($"{RemoteLines.Size(size)} · dans {show} › Saison {season:00}", $"{RemoteLines.Size(size)} · into {show} › Season {season:00}");

    /// <summary>« Télécharger 4 » (« Download 4 »), « Télécharger la saison ».</summary>
    public static string Download(int count, bool season) => season ? L("Télécharger la saison", "Download season") : L($"Télécharger {count}", $"Download {count}");

    public static string Retry => L("Réessayer", "Try again");
    /// <summary>« Analyser les médiathèques », as Jellyfin's dashboard says it.</summary>
    public static string Rescan => L("Analyser les médiathèques", "Scan libraries");
    public static string Rescanning => L("Jellyfin analyse tes médiathèques…", "Jellyfin is scanning your libraries…");
    public static string SearchAgain => L("Chercher", "Search again");
    public static string OpenDownloads => L("Ouvrir Téléchargements", "Open Downloads");

    /// <summary>What goes out: said once in the menu, as the Downloads page does.</summary>
    public static string Privacy => L("Les sites de TorLink reçoivent le nom de la série, depuis le PC de Jellyfin.", "TorLink’s sites get the series’ name, from your Jellyfin PC.");

    /// <summary>The short text of a season's pill while its episodes come: « 38 % ».</summary>
    public static string Pill(MissingProgress progress) => progress.Stage switch
    {
        MissingStage.Downloading => L($"{progress.Percent} %", $"{progress.Percent}%"),
        MissingStage.Waiting => L("en attente", "waiting"),
        // Placed: the pill can't tell whether Jellyfin has listed it already; the tile says it while it is missing.
        MissingStage.Adding => L("rangement", "adding"),
        MissingStage.Failed => L("échec", "failed"),
        _ => "",
    };

    [GeneratedRegex(@"\s*\((?:19|20)\d\d\)\s*$")] private static partial Regex Year();
}
