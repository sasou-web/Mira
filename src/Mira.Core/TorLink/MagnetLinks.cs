using System.Globalization;

namespace Mira.Core.TorLink;

// « Coller un lien magnet » (mock-up B « Écrin », chosen by ogsas on 2026-11-15): when a magnet link is pasted on the Downloads
// page, a window under « Paste a magnet link » says what the link is (its name read at once), what the library has of it
// (Jellyfin), what it holds (its files, read by the qBittorrent of the PC of Jellyfin: WorkLook) and proposes what to take
// (the episodes the library lacks, everything, or episodes chosen one by one), before anything is downloaded.

/// <summary>An episode a link holds: its season and number (a file of two: <paramref name="Last"/>), its size and its files (video and subtitles).</summary>
public sealed record LinkEpisode(int Season, int Number, int? Last, long Size, IReadOnlyList<int> Files)
{
    public (int Season, int Number) Key => (Season, Number);
}

/// <summary>A group of episodes in the window: a season, or 50 numbers of an anime's (« E101–E150 »).</summary>
public sealed record LinkGroup(string Label, IReadOnlyList<LinkEpisode> Episodes);

/// <summary>
/// What a link holds, read from its files as Mira will place them (<see cref="MediaPlanner"/>): episodes, or films;
/// <paramref name="Films"/> its films' files (videos and their subtitles, samples and other files left out),
/// <paramref name="FilmSubtitles"/> how many of them are subtitles; <paramref name="Total"/> every file's size.
/// </summary>
public sealed record LinkContents(MediaKind Kind, IReadOnlyList<LinkEpisode> Episodes, IReadOnlyList<int> Films, int FilmCount, int FilmSubtitles, long FilmSize,
    long Total, int FileCount, bool Samples)
{
    public bool Episodic => Kind != MediaKind.Movie;
}

/// <summary>The three proposals of the window (a film has none: <see cref="Film"/> alone).</summary>
public enum LinkOption { Missing, Everything, Choose, Film }

/// <summary>Everything, nothing, or some of a group, for its box.</summary>
public enum LinkGroupState { None, Some, All, Owned }

public static class MagnetLinks
{
    /// <summary>How long a link is read before « No one is sharing this link right now ».</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);
    /// <summary>A peeked link not downloaded is let go of this long after it was read (qBittorrent keeps it stopped meanwhile).</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromMinutes(15);
    /// <summary>The files a link's list keeps at most, and the length of a path: a 500-episode pack fits, the relay's 2 MB too.</summary>
    public const int MaxFiles = RemoteDownloads.MaxLinkFiles, MaxPath = RemoteDownloads.MaxLinkPath;
    /// <summary>Episodes of a single season shown as one grid; more are cut into <see cref="Slice"/> numbers.</summary>
    public const int Flat = 30, Slice = 50;
    /// <summary>qBittorrent's category of a link being read: never among Mira's downloads, let go of after <see cref="Kept"/>.</summary>
    public const string LookCategory = "Mira-look";

    /// <summary>
    /// What <paramref name="files"/> hold, as Mira will place them: episodes by their numbers (with the subtitles that go with
    /// each), or films. A sample, a file of another kind and an extra are never part of what is proposed.
    /// </summary>
    public static LinkContents Read(string name, IReadOnlyList<LinkFile> files)
    {
        // Libraries that are nowhere: the plan only names files, it never looks at a folder that would exist.
        var nowhere = Path.Combine(Path.GetTempPath(), "mira-nowhere-" + Guid.NewGuid().ToString("N"));
        var sources = files.Select(f => new SourceFile("#" + f.Index.ToString(CultureInfo.InvariantCulture), f.Path, Math.Max(0, f.Size))).ToList();
        var plan = MediaPlanner.Plan(name, null, sources, new MediaLibraries(Path.Combine(nowhere, "M"), Path.Combine(nowhere, "S"), Path.Combine(nowhere, "A")));
        var total = files.Sum(f => Math.Max(0, f.Size));
        var sizes = files.ToDictionary(f => f.Index, f => Math.Max(0, f.Size));
        var samples = files.Any(f => ReleaseName.IsVideo(f.Path) && ReleaseName.IsSample(f.Path));
        static int IndexOf(ImportOperation op) => int.Parse(op.Source[1..], CultureInfo.InvariantCulture);
        if (plan.Ignored is not null || plan.Operations.Count == 0) return new(MediaKind.Movie, [], [], 0, 0, 0, total, files.Count, samples);
        if (plan.Kind == MediaKind.Movie)
        {
            var kept = plan.Operations.Where(x => x.Role is "video" or "subtitle").Select(IndexOf).Distinct().Order().ToList();
            return new(MediaKind.Movie, [], kept, Math.Max(1, plan.Folders.Count), plan.Operations.Count(x => x.Role == "subtitle"), kept.Sum(i => sizes.GetValueOrDefault(i)), total, files.Count, samples);
        }
        var episodes = new List<(EpisodeNumber Number, string Stem, List<int> Files)>();
        foreach (var op in plan.Operations.Where(x => x.Role == "video"))
        {
            if (ReleaseName.Episode(Path.GetFileNameWithoutExtension(op.Destination), allowAbsolute: false) is not { } number) continue;
            episodes.Add((number, op.Destination[..^Path.GetExtension(op.Destination).Length], [IndexOf(op)]));
        }
        foreach (var op in plan.Operations.Where(x => x.Role == "subtitle"))
            if (episodes.FirstOrDefault(e => op.Destination.StartsWith(e.Stem + ".", StringComparison.OrdinalIgnoreCase)) is { Files: { } owner }) owner.Add(IndexOf(op));
        var list = episodes.Select(e => new LinkEpisode(e.Number.Season, e.Number.Episode, e.Number.LastEpisode > e.Number.Episode ? e.Number.LastEpisode : null,
                e.Files.Sum(i => sizes.GetValueOrDefault(i)), e.Files.Order().ToList()))
            .OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        if (list.Count == 0) return new(MediaKind.Movie, [], [], 0, 0, 0, total, files.Count, samples);
        return new(plan.Kind, list, [], 0, 0, 0, total, files.Count, samples);
    }

    /// <summary>
    /// Where Mira will put what the link holds, on the PC of Jellyfin: the library's folder (« SERIES »), the title's folder
    /// there (« Severance », « Millennium Actress (2001) ») and whether it is there already. Null without a library for it.
    /// </summary>
    public static LinkPlace? Place(string name, IReadOnlyList<LinkFile> files, MediaLibraries libraries, IReadOnlyList<KnownShow>? known = null)
    {
        if (libraries.IsEmpty) return null;
        var sources = files.Select(f => new SourceFile("#" + f.Index.ToString(CultureInfo.InvariantCulture), f.Path, Math.Max(0, f.Size))).ToList();
        var plan = MediaPlanner.Plan(name, null, sources, libraries, known: known);
        if (plan.Ignored is not null || plan.Problem is not null || plan.LibraryRoot is not { } root || plan.Operations.FirstOrDefault(x => x.Role == "video") is not { } video) return null;
        var parts = Path.GetRelativePath(root, video.Destination).Split('\\', '/');
        if (parts.Length < 2 || parts[0] is "." or "..") return null;
        var library = Path.GetFileName(root.TrimEnd('\\', '/'));
        return new(library.Length > 0 ? library : root, parts[0], Directory.Exists(Path.Combine(root, parts[0])));
    }

    /// <summary>The episodes the library has of these (<paramref name="owned"/>: its episodes by season and number, a file of two counted twice).</summary>
    public static bool Owned(LinkEpisode episode, IReadOnlySet<(int Season, int Number)> owned) =>
        Enumerable.Range(episode.Number, (episode.Last ?? episode.Number) - episode.Number + 1).All(n => owned.Contains((episode.Season, n)));

    /// <summary>
    /// The groups of the window: none (one grid) for a season of up to <see cref="Flat"/> episodes, a season each when there
    /// are several, otherwise <see cref="Slice"/> numbers each (« E101–E150 »).
    /// </summary>
    public static IReadOnlyList<LinkGroup> Groups(IReadOnlyList<LinkEpisode> episodes)
    {
        var seasons = episodes.Select(e => e.Season).Distinct().Order().ToList();
        if (seasons.Count > 1) return seasons.Select(s => new LinkGroup(SeasonName(s), episodes.Where(e => e.Season == s).ToList())).ToList();
        if (episodes.Count <= Flat) return [new LinkGroup("", episodes)];
        var width = Digits(episodes.Max(e => e.Last ?? e.Number));
        return episodes.GroupBy(e => (e.Number - 1) / Slice).OrderBy(g => g.Key)
            .Select(g => new LinkGroup($"E{(g.Key * Slice + 1).ToString("D" + width, CultureInfo.InvariantCulture)}–E{((g.Key + 1) * Slice).ToString("D" + width, CultureInfo.InvariantCulture)}", g.ToList())).ToList();
    }

    /// <summary>Whether a group's episodes are numbered in it alone (« 101 »), not « E01 »: the 50-number slices.</summary>
    public static bool Numbered(IReadOnlyList<LinkGroup> groups) => groups.Count > 0 && groups[0].Label.StartsWith('E');

    /// <summary>« E04 », « E04–E05 » in a season's grid; « 104 » in a slice of an anime's.</summary>
    public static string Cell(LinkEpisode episode, bool numbered, int width = 2)
    {
        string N(int n) => n.ToString("D" + width, CultureInfo.InvariantCulture);
        return numbered ? N(episode.Number) + (episode.Last is { } l ? "–" + N(l) : "") : "E" + N(episode.Number) + (episode.Last is { } last ? "–E" + N(last) : "");
    }

    public static int Digits(int number) => Math.Max(2, number.ToString(CultureInfo.InvariantCulture).Length);

    public static LinkGroupState State(LinkGroup group, IReadOnlySet<(int, int)> chosen, IReadOnlySet<(int Season, int Number)> owned)
    {
        var free = group.Episodes.Where(e => !Owned(e, owned)).ToList();
        if (free.Count == 0) return LinkGroupState.Owned;
        var n = free.Count(e => chosen.Contains(e.Key));
        return n == 0 ? LinkGroupState.None : n == free.Count ? LinkGroupState.All : LinkGroupState.Some;
    }

    /// <summary>
    /// A click on an episode (<paramref name="from"/>: the episode clicked before, with Shift held): that episode, or every
    /// one between the two, takes the state the click gives it; an episode of the library is never chosen.
    /// </summary>
    public static void Click(ISet<(int, int)> chosen, IReadOnlyList<LinkEpisode> episodes, LinkEpisode clicked, LinkEpisode? from, IReadOnlySet<(int Season, int Number)> owned)
    {
        if (Owned(clicked, owned)) return;
        var on = !chosen.Contains(clicked.Key);
        var a = from is null ? -1 : IndexOf(episodes, from); var b = IndexOf(episodes, clicked);
        var range = a < 0 || b < 0 ? [clicked] : episodes.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1);
        foreach (var episode in range.Where(e => !Owned(e, owned)))
            if (on) chosen.Add(episode.Key); else chosen.Remove(episode.Key);
    }

    private static int IndexOf(IReadOnlyList<LinkEpisode> episodes, LinkEpisode episode)
    {
        for (var i = 0; i < episodes.Count; i++) if (episodes[i].Key == episode.Key) return i;
        return -1;
    }

    /// <summary>A group's box: all of it chosen, or none when it all was (an episode of the library never).</summary>
    public static void Toggle(ISet<(int, int)> chosen, LinkGroup group, IReadOnlySet<(int Season, int Number)> owned)
    {
        var free = group.Episodes.Where(e => !Owned(e, owned)).ToList();
        var all = free.All(e => chosen.Contains(e.Key));
        foreach (var e in free) if (all) chosen.Remove(e.Key); else chosen.Add(e.Key);
    }

    /// <summary>The files of these episodes, by their index; null when they are all of the link's (nothing to leave out).</summary>
    public static List<int>? Files(LinkContents contents, IEnumerable<LinkEpisode> episodes)
    {
        var files = episodes.SelectMany(e => e.Files).Distinct().Order().ToList();
        return files.Count == contents.FileCount ? null : files;
    }

    /// <summary>The files of a film's link: its films and their subtitles, null when that is every file.</summary>
    public static List<int>? FilmFiles(LinkContents contents) => contents.Films.Count == contents.FileCount ? null : contents.Films.ToList();

    // ---- In the window's words ------------------------------------------------------------------------------------------

    public static string SeasonName(int season) => season == 0 ? L("Épisodes spéciaux", "Specials") : L($"Saison {season}", $"Season {season}");

    /// <summary>« Season 2 », « Complete series », « Episodes 1 to 12 », « S01E05 »: the badge beside the title; empty for a film.</summary>
    public static string Badge(ReleaseInfo release)
    {
        if (release.Pack && release.Season is null && release.Episode is null) return L("Série complète", "Complete series");
        if (release.Pack && release.Season is { } season && release.Episode is null) return SeasonName(season);
        return ReleaseRanking.EpisodeLabel(release);
    }

    /// <summary>The kind beside the badge: Movie, Show, Anime.</summary>
    public static string KindName(MediaKind kind) => kind switch { MediaKind.Movie => L("Film", "Movie"), MediaKind.Anime => L("Anime", "Anime"), _ => L("Série", "Show") };

    /// <summary>« Unnamed link » for a link without a name, and the link itself shortened under it.</summary>
    public static string Unnamed => L("Lien sans nom", "Unnamed link");
    public static string Short(string magnet, string hash) => $"magnet:?xt=urn:btih:{hash[..10]}…{hash[^4..]}";

    /// <summary>« E04–E10 », « E01, E03, E05–E06 »: episodes of one season as their numbers.</summary>
    public static string Range(IEnumerable<LinkEpisode> episodes)
    {
        var numbers = episodes.SelectMany(e => Enumerable.Range(e.Number, (e.Last ?? e.Number) - e.Number + 1)).Distinct().Order().ToList();
        if (numbers.Count == 0) return "";
        var width = Digits(numbers[^1]);
        string N(int n) => "E" + n.ToString("D" + width, CultureInfo.InvariantCulture);
        var runs = new List<string>();
        for (var i = 0; i < numbers.Count;)
        {
            var j = i;
            while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
            runs.Add(j == i ? N(numbers[i]) : N(numbers[i]) + "–" + N(numbers[j]));
            i = j + 1;
        }
        return runs.Count <= 3 ? string.Join(", ", runs) : string.Join(", ", runs.Take(2)) + "…";
    }

    /// <summary>
    /// What some episodes are, said short: « E04–E10 » in one season, « Seasons 2 to 4 », « Season 2, E03–E08 and Season 3 »:
    /// whole seasons by their names.
    /// </summary>
    public static string Span(IReadOnlyList<LinkEpisode> some, IReadOnlyList<LinkEpisode> all)
    {
        var seasons = some.Select(e => e.Season).Distinct().Order().ToList();
        if (seasons.Count == 1 && all.Select(e => e.Season).Distinct().Count() == 1) return Range(some);
        var whole = seasons.All(s => all.Where(e => e.Season == s).All(e => some.Any(x => x.Key == e.Key)));
        if (whole && seasons.Count > 1 && seasons[^1] - seasons[0] == seasons.Count - 1)
            return L($"Saisons {seasons[0]} à {seasons[^1]}", $"Seasons {seasons[0]} to {seasons[^1]}");
        if (seasons.Count == 1) return SeasonName(seasons[0]) + (whole ? "" : ", " + Range(some));
        return Lang.Count(seasons.Count, "saison", "saisons", "season", "seasons");
    }

    /// <summary>« E01–E03 already in your library », « Season 1 already in your library », « Not in your library yet ».</summary>
    public static string Library(LinkContents? contents, IReadOnlySet<(int Season, int Number)> owned, bool? inLibrary)
    {
        if (inLibrary is null) return "";
        if (contents is { Episodic: true } c)
        {
            var have = c.Episodes.Where(e => Owned(e, owned)).ToList();
            if (have.Count == c.Episodes.Count && have.Count > 0) return L("Tout est déjà dans ta bibliothèque", "All of it is already in your library");
            if (have.Count > 0) return L($"{Span(have, c.Episodes)} déjà dans ta bibliothèque", $"{Span(have, c.Episodes)} already in your library");
        }
        return inLibrary == true ? (contents is { Episodic: true } ? L("Série dans ta bibliothèque", "Show in your library") : L("Déjà dans ta bibliothèque", "Already in your library"))
            : L("Pas encore dans ta bibliothèque", "Not in your library yet");
    }

    /// <summary>The proposals offered, in their order, and the one put forward.</summary>
    public static (IReadOnlyList<LinkOption> Options, LinkOption Suggested) Options(LinkContents contents, IReadOnlySet<(int Season, int Number)> owned)
    {
        if (!contents.Episodic) return ([LinkOption.Film], LinkOption.Film);
        var have = contents.Episodes.Count(e => Owned(e, owned));
        if (have > 0 && have < contents.Episodes.Count) return ([LinkOption.Missing, LinkOption.Everything, LinkOption.Choose], LinkOption.Missing);
        return ([LinkOption.Everything, LinkOption.Choose], LinkOption.Everything);
    }

    /// <summary>« The 7 episodes you don’t have », « Everything in the link », « Choose the episodes… ».</summary>
    public static string Title(LinkOption option, LinkContents contents, IReadOnlySet<(int Season, int Number)> owned) => option switch
    {
        LinkOption.Missing => Lacking(contents, owned).Count is var n && n == 1 ? L("L’épisode que tu n’as pas", "The episode you don’t have")
            : L($"Les {n} épisodes que tu n’as pas", $"The {n} episodes you don’t have"),
        LinkOption.Everything => L("Tout le lien", "Everything in the link"),
        LinkOption.Choose => L("Choisir les épisodes…", "Choose the episodes…"),
        _ => contents.FilmCount > 1 ? Lang.Count(contents.FilmCount, "film", "films", "film", "films")
            : contents.FilmSubtitles > 0 ? L($"Le film et ses {Lang.Count(contents.FilmSubtitles, "fichier de sous-titres", "fichiers de sous-titres", "subtitle file", "subtitle files")}", $"The film and its {Lang.Count(contents.FilmSubtitles, "fichier de sous-titres", "fichiers de sous-titres", "subtitle file", "subtitle files")}")
            : L("Le film", "The film"),
    };

    /// <summary>What a proposal holds: « E04–E10 · 9.9 GB », « 10 episodes · 14.2 GB », « E01 to E10 », « 4 chosen · 5.7 GB ».</summary>
    public static string Detail(LinkOption option, LinkContents contents, IReadOnlySet<(int Season, int Number)> owned, IReadOnlySet<(int, int)> chosen) => option switch
    {
        LinkOption.Missing => $"{Span(Lacking(contents, owned), contents.Episodes)} · {RemoteLines.Size(Lacking(contents, owned).Sum(e => e.Size))}",
        LinkOption.Everything => $"{Lang.Count(contents.Episodes.Count, "épisode", "épisodes", "episode", "episodes")} · {RemoteLines.Size(contents.Total)}",
        LinkOption.Choose => Chosen(contents, chosen),
        _ => RemoteLines.Size(contents.FilmSize),
    };

    /// <summary>« 4 chosen · 5.7 GB », « None chosen yet ».</summary>
    public static string Chosen(LinkContents contents, IReadOnlySet<(int, int)> chosen)
    {
        var picked = contents.Episodes.Where(e => chosen.Contains(e.Key)).ToList();
        return picked.Count == 0 ? L("Aucun choisi pour l’instant", "None chosen yet")
            : L($"{picked.Count} {(picked.Count == 1 ? "choisi" : "choisis")} · {RemoteLines.Size(picked.Sum(e => e.Size))}", $"{picked.Count} chosen · {RemoteLines.Size(picked.Sum(e => e.Size))}");
    }

    /// <summary>« E01 to E10 », « Seasons 1 to 4 »: what « Choose the episodes… » chooses from, before it is open.</summary>
    public static string ChooseFrom(LinkContents contents)
    {
        var seasons = contents.Episodes.Select(e => e.Season).Distinct().Order().ToList();
        if (seasons.Count > 1) return L($"Saisons {seasons[0]} à {seasons[^1]}", $"Seasons {seasons[0]} to {seasons[^1]}");
        var width = Digits(contents.Episodes.Max(e => e.Last ?? e.Number));
        string N(int n) => "E" + n.ToString("D" + width, CultureInfo.InvariantCulture);
        return L($"{N(contents.Episodes[0].Number)} à {N(contents.Episodes[^1].Last ?? contents.Episodes[^1].Number)}", $"{N(contents.Episodes[0].Number)} to {N(contents.Episodes[^1].Last ?? contents.Episodes[^1].Number)}");
    }

    /// <summary>The episodes the library lacks.</summary>
    public static List<LinkEpisode> Lacking(LinkContents contents, IReadOnlySet<(int Season, int Number)> owned) => contents.Episodes.Where(e => !Owned(e, owned)).ToList();

    /// <summary>The episodes a proposal takes.</summary>
    public static List<LinkEpisode> Taken(LinkOption option, LinkContents contents, IReadOnlySet<(int Season, int Number)> owned, IReadOnlySet<(int, int)> chosen) => option switch
    {
        LinkOption.Missing => Lacking(contents, owned),
        LinkOption.Choose => contents.Episodes.Where(e => chosen.Contains(e.Key)).ToList(),
        _ => contents.Episodes.ToList(),
    };

    /// <summary>The files a proposal downloads: null for all of the link's.</summary>
    public static List<int>? FilesOf(LinkOption option, LinkContents contents, IReadOnlySet<(int Season, int Number)> owned, IReadOnlySet<(int, int)> chosen) => option switch
    {
        LinkOption.Everything => null,
        LinkOption.Film => FilmFiles(contents),
        _ => Files(contents, Taken(option, contents, owned, chosen)),
    };

    /// <summary>
    /// « into » and where, the second part in bold: « Severance › Season 02 » for a series already there, « SERIES › Severance »
    /// for a new one, « FILMS › Millennium Actress (2001) »; « your library » when the PC of Jellyfin didn't say.
    /// </summary>
    public static (string Into, string Where) Destination(LinkPlace? place, IReadOnlyCollection<int> seasons, bool episodic)
    {
        var into = L("dans ", "into ");
        if (place is null) return (into, L("ta bibliothèque", "your library"));
        if (!episodic || !place.Exists) return (into, $"{place.Root} › {place.Folder}");
        var list = seasons.Distinct().Order().ToList();
        string Two(int s) => s.ToString("00", CultureInfo.InvariantCulture);
        var part = list.Count switch
        {
            0 => "",
            1 => list[0] == 0 ? L(" › Épisodes spéciaux", " › Specials") : L($" › Saison {Two(list[0])}", $" › Season {Two(list[0])}"),
            _ when list[^1] - list[0] == list.Count - 1 => L($" › Saisons {Two(list[0])} à {Two(list[^1])}", $" › Seasons {Two(list[0])} to {Two(list[^1])}"),
            _ => " › " + Lang.Count(list.Count, "saison", "saisons", "season", "seasons"),
        };
        return (into, place.Folder + part);
    }

    /// <summary>« 4.6 TB free on LIVING-ROOM-PC ».</summary>
    public static string Free(long? free, string? pc) =>
        free is not { } bytes || bytes < 0 ? "" : pc is { Length: > 0 } name ? L($"{RemoteLines.Size(bytes)} libres sur {name}", $"{RemoteLines.Size(bytes)} free on {name}") : L($"{RemoteLines.Size(bytes)} libres", $"{RemoteLines.Size(bytes)} free");

    public static string Reading => L("Lecture de son contenu…", "Reading what’s inside…");
    /// <summary>Who asks: this PC when the link is read here (Mira on Jellyfin's PC), else the server PC by its name.</summary>
    private static string Asker(string? pc, bool here) => here ? L("Ce PC", "This PC") : pc ?? L("Le PC serveur", "The server PC");
    public static string ReadingHow(string? pc, bool here = false) => L($"{Asker(pc, here)} demande la liste aux personnes qui partagent ce lien.", $"{Asker(pc, here)} is asking the people who share this link.");
    /// <summary>« 48 s at most », the time left before « No one is sharing », counted from when the reading began.</summary>
    public static string ReadingLeft(TimeSpan waited)
    {
        var left = (int)Math.Ceiling(Math.Max(1, (Patience - waited).TotalSeconds));
        return L($"{left} s au plus", $"{left} s at most");
    }
    public static string Silent => L("Personne ne partage ce lien en ce moment", "No one is sharing this link right now");
    public static string SilentHow(string? pc, bool here = false) => L($"{Asker(pc, here)} a cherché une source pendant 60 secondes sans en trouver : Mira ne peut pas lire ce qu’il contient.",
        $"{Asker(pc, here)} looked for 60 seconds without finding a source, so Mira can’t read what’s inside.");
    public static string AnywayHow => L("« Télécharger quand même » : il part tout seul si une source revient.", "“Download anyway”: it starts on its own if a source comes back.");
    public static string WholeOnly(bool local) => local
        ? L("Pour choisir les épisodes, installe qBittorrent sur ce PC : TorLink prend tout le lien.", "To choose the episodes, install qBittorrent on this PC: TorLink takes the whole link.")
        : L("Pour choisir les épisodes, il faut qBittorrent sur le PC serveur, et Mira à jour : sinon, tout le lien.", "Choosing episodes needs qBittorrent on the server PC, and Mira up to date there: otherwise, the whole link.");
    public static string Already => L("Déjà sur le PC serveur", "Already on the server PC");
    public static string AlreadyPlaced => L("Déjà téléchargé : il est dans ta bibliothèque", "Already downloaded: it’s in your library");
    public static string SampleOut => L("L’échantillon reste de côté.", "The sample stays out.");
    public static string ShiftHint => L("Clique un numéro, ou Maj+clic pour prendre toute une plage", "Click a number, or Shift-click to take a whole range");
    public static string InLibrary => L("dans ta bibliothèque", "in library");
    public static string Download => L("Télécharger", "Download");
    public static string DownloadAnyway => L("Télécharger quand même", "Download anyway");
    public static string TryAgain => L("Réessayer", "Try again");
    public static string Suggested => L("Proposé", "Suggested");
    public static string ShowInList => L("Le voir dans la liste", "Show it in the list");
    public static string Resume => L("Reprendre", "Resume");
    public static string NotMagnet => L("Ce n’est pas un lien magnet (« magnet:?xt=urn:btih:… »).", "That’s not a magnet link (“magnet:?xt=urn:btih:…”).");
    public static string PasteHere => L("Colle un lien magnet ici", "Paste a magnet link here");
    public static string AllNone => L("Tout · Rien", "All · None");
    public static string NoVideo => L("Aucune vidéo dans ce lien : Mira n’a rien à ranger.", "No video in this link: Mira has nothing to sort.");
    /// <summary>Said beside the window, for the privacy page: reading a link joins those who share it.</summary>
    public static string Privacy(bool here = false) => here
        ? L("Pour lire ce qu’il contient, ce PC rejoint les personnes qui partagent ce lien, avant ton clic sur Télécharger.",
            "To read what’s inside, this PC joins the people who share this link, before you click Download.")
        : L("Pour lire ce qu’il contient, le PC serveur rejoint les personnes qui partagent ce lien, avant ton clic sur Télécharger.",
            "To read what’s inside, the server PC joins the people who share this link, before you click Download.");
}
