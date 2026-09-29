using System.Text.Json.Serialization;

namespace Mira.Core;

[JsonConverter(typeof(JsonStringEnumConverter<MediaKind>))]
public enum MediaKind { Movie, Series, Anime }

/// <summary>Jellyfin library folders receiving TorLink downloads. Without an anime library, anime go to the series one, and the reverse.</summary>
public sealed record MediaLibraries(string? Movies, string? Series, string? Anime)
{
    public string? RootFor(MediaKind kind) => kind switch { MediaKind.Movie => Movies, MediaKind.Anime => Anime ?? Series, _ => Series ?? Anime };
    public bool IsEmpty => Movies is null && Series is null && Anime is null;

    /// <summary>
    /// Folders of the server's libraries: the first film library, a TV library named like "Anime" for anime,
    /// the other TV library for series. Manual choices (Réglages) take precedence, one kind at a time.
    /// </summary>
    public static MediaLibraries FromJellyfin(IEnumerable<VirtualFolder>? folders, string? movies = null, string? series = null, string? anime = null)
    {
        static string? Pick(string? manual) => string.IsNullOrWhiteSpace(manual) ? null : manual.Trim();
        static string? First(VirtualFolder? folder) => folder?.Locations.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        static bool Anime(VirtualFolder folder) => AnimeWords(folder.Name) || folder.Locations.Any(x => AnimeWords(Path.GetFileName(x.TrimEnd('\\', '/'))));
        // "Anime", "Animes", "Animés" or アニメ; not "Animation", "Animaux" or "Dessins animés" (western cartoons).
        static bool AnimeWords(string text)
        {
            var words = ReleaseName.Key(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
                if (words[i] is "anime" or "animes" && (i == 0 || words[i - 1] is not ("dessin" or "dessins"))) return true;
            return text.Contains("アニメ", StringComparison.Ordinal);
        }
        var list = folders?.ToList() ?? [];
        var film = list.FirstOrDefault(x => string.Equals(x.CollectionType, "movies", StringComparison.OrdinalIgnoreCase));
        var shows = list.Where(x => string.Equals(x.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase)).ToList();
        var animeLibrary = shows.FirstOrDefault(Anime);
        var seriesLibrary = shows.FirstOrDefault(x => !ReferenceEquals(x, animeLibrary));
        return new(Pick(movies) ?? First(film), Pick(series) ?? First(seriesLibrary), Pick(anime) ?? First(animeLibrary));
    }
}

/// <summary>A downloaded file: where it is now, and its path inside the torrent (which is what gets parsed).</summary>
public sealed record SourceFile(string Path, string RelativePath, long Length);

/// <summary>One file to place in the library. Role: "video", "subtitle" or "extra".</summary>
public sealed record ImportOperation(string Source, string Destination, string Role, string RelativePath);

/// <summary>
/// Where a download goes. <see cref="Ignored"/>: not something for Jellyfin (game, no video), final.
/// <see cref="Problem"/>: cannot be placed yet (no library folder), to be retried.
/// </summary>
public sealed record ImportPlan(MediaKind Kind, string Title, string? LibraryRoot, IReadOnlyList<string> Folders, IReadOnlyList<ImportOperation> Operations, IReadOnlyList<string> Notes, string? Ignored = null, string? Problem = null);

/// <summary>Turns a finished download into Jellyfin-style paths: "Films\Title (Year)\Title (Year).mkv", "Series\Show\Season 01\Show - S01E02.mkv".</summary>
public static class MediaPlanner
{
    private static readonly HashSet<string> TvSources = new(StringComparer.OrdinalIgnoreCase) { "eztv", "solid", "tpb-tv", "x1337-tv" };
    private static readonly HashSet<string> AnimeSources = new(StringComparer.OrdinalIgnoreCase) { "nyaa", "subsplease" };

    public static ImportPlan Plan(string torrentName, string? source, IReadOnlyList<SourceFile> files, MediaLibraries libraries, MediaKind? forced = null)
    {
        if (string.Equals(source, "fitgirl", StringComparison.OrdinalIgnoreCase))
            return Ignore("Jeu vidéo : il reste dans le dossier TorLink, rien n’est ajouté à Jellyfin.");
        var videos = files.Where(f => ReleaseName.IsVideo(f.RelativePath)).ToList();
        var largest = videos.Count == 0 ? 0 : videos.Max(v => v.Length);
        // A clip called "sample" beside a real feature is left out; a lone "sample" file is not second-guessed.
        var samples = videos.Where(v => ReleaseName.IsSample(v.RelativePath) && v.Length < largest * .3).ToList();
        videos = videos.Except(samples).ToList();
        var subtitles = files.Where(f => ReleaseName.IsSubtitle(f.RelativePath) && !ReleaseName.IsSample(f.RelativePath)).ToList();
        if (videos.Count == 0) return Ignore("Aucune vidéo dans ce téléchargement.");
        var notes = new List<string>();
        var others = files.Count - videos.Count - subtitles.Count - samples.Count;
        if (samples.Count > 0) notes.Add(Count(samples.Count, "échantillon ignoré", "échantillons ignorés"));
        if (others > 0) notes.Add(Count(others, "fichier annexe laissé dans TorLink", "fichiers annexes laissés dans TorLink"));

        var context = new Context(torrentName, source, libraries, notes)
        {
            AnimeSource = source is not null && AnimeSources.Contains(source),
            TvSource = source is not null && TvSources.Contains(source),
            Grouped = ReleaseName.HasGroupPrefix(torrentName),
            PackSeason = ReleaseName.Season(torrentName)
        };
        context.AbsoluteAllowed = context.AnimeSource || context.Grouped || context.PackSeason is not null || forced is MediaKind.Anime or MediaKind.Series;
        var mains = videos.Where(v => !IsBonus(v)).ToList();
        if (mains.Count == 0) mains = videos;
        var single = videos.Count == 1;
        var episodic = forced switch
        {
            MediaKind.Movie => false,
            MediaKind.Series or MediaKind.Anime => true,
            _ => mains.Any(v => EpisodeOf(v, context, single) is not null) || (mains.Count > 1 && (context.PackSeason is not null || ReleaseName.IsCompletePack(torrentName)))
        };
        return episodic ? PlanShow(context, videos, subtitles, forced) : PlanMovies(context, videos, subtitles);
    }

    private sealed class Context(string torrentName, string? source, MediaLibraries libraries, List<string> notes)
    {
        public string TorrentName { get; } = torrentName;
        public string? Source { get; } = source;
        public MediaLibraries Libraries { get; } = libraries;
        public List<string> Notes { get; } = notes;
        public bool AnimeSource, TvSource, Grouped, AbsoluteAllowed;
        public int? PackSeason;
    }

    private static bool IsBonus(SourceFile file) =>
        ReleaseName.InExtrasFolder(file.RelativePath) || ReleaseName.CreditlessLabel(Path.GetFileName(file.RelativePath), bare: false) is not null;

    private static EpisodeNumber? EpisodeOf(SourceFile file, Context context, bool single)
    {
        var episode = ReleaseName.Episode(Path.GetFileName(file.RelativePath), context.AbsoluteAllowed)
            ?? (single ? ReleaseName.Episode(context.TorrentName, context.AbsoluteAllowed) : null);
        if (episode is not { Absolute: true }) return episode;
        // Numbered without a season: the season folder inside the pack, a Specials folder, or the pack's own season.
        var folder = Path.GetFileName(Path.GetDirectoryName(file.RelativePath) ?? "");
        var season = ReleaseName.InSpecialsFolder(file.RelativePath) ? 0 : ReleaseName.SeasonOfFolder(folder ?? "") ?? context.PackSeason;
        return season is { } value ? episode with { Season = value, Absolute = false } : episode;
    }

    private static ImportPlan PlanMovies(Context context, List<SourceFile> videos, List<SourceFile> subtitles)
    {
        var root = context.Libraries.RootFor(MediaKind.Movie);
        if (root is null) return Blocked(MediaKind.Movie, "Choisis le dossier Films de Jellyfin dans Réglages → TorLink.");
        var mains = videos.Where(v => !ReleaseName.InExtrasFolder(v.RelativePath)).OrderByDescending(v => v.Length).ToList();
        if (mains.Count == 0) mains = videos.OrderByDescending(v => v.Length).ToList();
        var bonus = videos.Except(mains).ToList();
        var main = mains[0];
        var targets = new List<(string Folder, List<(SourceFile File, string Name, string Role)> Files)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var stacked = mains.Count > 1 && mains.All(v => ReleaseName.StackPart(Path.GetFileName(v.RelativePath)) is not null);
        var large = mains.Where(v => v.Length >= main.Length * .5).ToList();
        var parsed = large.Select(v => (File: v, Movie: ReleaseName.Movie(Path.GetFileName(v.RelativePath)))).ToList();
        var pack = !stacked && large.Count > 1 && parsed.All(p => p.Movie.Title.Length > 0 && p.Movie.Year is not null)
            && parsed.Select(p => ReleaseName.Key(p.Movie.Title) + "|" + p.Movie.Year).Distinct().Count() == large.Count;
        if (pack)
        {
            // A collection: each feature becomes its own film, named from its own file.
            foreach (var (file, (title, year)) in parsed)
                targets.Add((MovieFolder(root, title, year), [(file, "", "video")]));
            var loose = mains.Except(large).Concat(bonus).Count();
            if (loose > 0) context.Notes.Add(Count(loose, "bonus non attribué laissé dans TorLink", "bonus non attribués laissés dans TorLink"));
        }
        else
        {
            var (title, year) = ReleaseName.Movie(context.TorrentName);
            if (title.Length == 0) (title, year) = ReleaseName.Movie(Path.GetFileName(main.RelativePath));
            var folder = MovieFolder(root, title.Length == 0 ? Path.GetFileNameWithoutExtension(main.RelativePath) : title, year);
            var list = new List<(SourceFile File, string Name, string Role)>();
            if (stacked)
                foreach (var part in mains.OrderBy(v => ReleaseName.StackPart(Path.GetFileName(v.RelativePath))))
                    list.Add((part, $"{folder} - part{ReleaseName.StackPart(Path.GetFileName(part.RelativePath))}", "video"));
            else
            {
                list.Add((main, folder, "video"));
                var version = 1;
                foreach (var other in mains.Skip(1))
                {
                    // A second full-length file of the same film is a version; anything shorter is bonus material.
                    if (other.Length >= main.Length * .5) list.Add((other, $"{folder} - version {++version}", "video"));
                    else bonus.Add(other);
                }
            }
            foreach (var extra in bonus) list.Add((extra, Path.Combine("Extras", ReleaseName.SafeName(Path.GetFileNameWithoutExtension(extra.RelativePath))), "extra"));
            targets.Add((folder, list));
        }

        var operations = new List<ImportOperation>();
        foreach (var (folder, list) in targets)
        {
            foreach (var (file, name, role) in list)
            {
                var stem = name.Length == 0 ? folder : name;
                operations.Add(Operation(file, Unique(Path.Combine(root, folder, stem), Path.GetExtension(file.RelativePath), used), role));
            }
        }
        // Subtitles follow their film; in a collection, only the ones named after a feature file.
        var features = targets.Select(t => (t.Folder, Videos: t.Files.Where(f => f.Role == "video").Select(f => Path.GetFileNameWithoutExtension(f.File.RelativePath)).ToList())).ToList();
        var bonusNames = targets.SelectMany(t => t.Files).Where(f => f.Role == "extra").Select(f => Path.GetFileNameWithoutExtension(f.File.RelativePath)).ToList();
        var unmatched = 0;
        foreach (var subtitle in subtitles)
        {
            var subtitleName = Path.GetFileNameWithoutExtension(subtitle.RelativePath);
            // Subtitles of bonus material must not become the film's subtitles.
            if (ReleaseName.InExtrasFolder(subtitle.RelativePath) || bonusNames.Any(b => subtitleName.StartsWith(b, StringComparison.OrdinalIgnoreCase))) { unmatched++; continue; }
            var owner = pack ? features.FirstOrDefault(t => t.Videos.Any(v => subtitleName.StartsWith(v, StringComparison.OrdinalIgnoreCase))).Folder : targets[0].Folder;
            if (owner is null) { unmatched++; continue; }
            operations.Add(Operation(subtitle, Unique(Path.Combine(root, owner, owner) + ReleaseName.SubtitleTags(subtitle.RelativePath), Path.GetExtension(subtitle.RelativePath), used), "subtitle"));
        }
        if (unmatched > 0) context.Notes.Add(Count(unmatched, "sous-titre non attribué", "sous-titres non attribués"));
        var display = targets.Count == 1 ? targets[0].Folder : $"{targets.Count} films";
        return new(MediaKind.Movie, display, root, targets.Select(t => Path.Combine(root, t.Folder)).Distinct().ToList(), operations, context.Notes);
    }

    private static ImportPlan PlanShow(Context context, List<SourceFile> videos, List<SourceFile> subtitles, MediaKind? forced)
    {
        var reference = videos.OrderByDescending(v => v.Length).First();
        var (title, year) = ReleaseName.Show(context.TorrentName);
        if (title.Length == 0) (title, year) = ReleaseName.Show(Path.GetFileName(reference.RelativePath));
        if (title.Length == 0) title = ReleaseName.SafeName(Path.GetFileNameWithoutExtension(context.TorrentName));
        var single = videos.Count == 1;
        var numbered = videos.Where(v => !IsBonus(v)).Select(v => EpisodeOf(v, context, single)).OfType<EpisodeNumber>().ToList();
        var animeFolder = FindFolder(context.Libraries.Anime, title, year);
        var seriesFolder = string.Equals(context.Libraries.Series, context.Libraries.Anime, StringComparison.OrdinalIgnoreCase) ? null : FindFolder(context.Libraries.Series, title, year);
        // An existing show decides first, then TorLink's source, then fansub conventions.
        var kind = forced ?? (animeFolder is not null ? MediaKind.Anime
            : seriesFolder is not null ? MediaKind.Series
            : context.AnimeSource ? MediaKind.Anime
            : context.TvSource ? MediaKind.Series
            : context.Grouped || numbered.Any(x => x.Absolute) ? MediaKind.Anime : MediaKind.Series);
        var root = context.Libraries.RootFor(kind);
        if (root is null) return Blocked(kind, $"Choisis le dossier {(kind == MediaKind.Anime ? "Animes" : "Séries")} de Jellyfin dans Réglages → TorLink.");
        var show = FindFolder(root, title, year) ?? ReleaseName.SafeName(year is null ? title : $"{title} ({year})");
        var showPath = Path.Combine(root, show);
        var seasons = ExistingFolders(showPath);
        string SeasonFolder(int season) => seasons.FirstOrDefault(x => ReleaseName.SeasonOfFolder(x) == season) ?? (season == 0 ? "Season 00" : $"Season {season:00}");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<ImportOperation>();
        var episodes = new Dictionary<string, (SourceFile File, EpisodeNumber Number)>(StringComparer.OrdinalIgnoreCase);
        var bonus = new List<(SourceFile File, string? Label)>();
        var duplicates = 0;
        foreach (var video in videos.OrderByDescending(v => v.Length))
        {
            var name = Path.GetFileName(video.RelativePath);
            if (IsBonus(video)) { bonus.Add((video, ReleaseName.CreditlessLabel(name, bare: true))); continue; }
            if (EpisodeOf(video, context, single) is { } number)
            {
                // Two files for one episode (v2, second release): the larger one is kept.
                if (!episodes.TryAdd(number.Label, (video, number))) duplicates++;
                continue;
            }
            bonus.Add((video, ReleaseName.CreditlessLabel(name, bare: true)));
        }
        if (duplicates > 0) context.Notes.Add(Count(duplicates, "doublon d’épisode laissé dans TorLink", "doublons d’épisodes laissés dans TorLink"));
        var episodeStems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, (file, number)) in episodes.OrderBy(x => x.Value.Number.Season).ThenBy(x => x.Value.Number.Episode))
        {
            var destination = Unique(Path.Combine(showPath, SeasonFolder(number.Season), $"{show} - {label}"), Path.GetExtension(file.RelativePath), used);
            episodeStems[label] = destination[..^Path.GetExtension(destination).Length];
            operations.Add(Operation(file, destination, "video"));
        }
        var unnumbered = 0;
        foreach (var (file, label) in bonus)
        {
            if (label is null) unnumbered++;
            var stem = label is null ? ReleaseName.SafeName(Path.GetFileNameWithoutExtension(file.RelativePath)) : $"{show} - {label}";
            operations.Add(Operation(file, Unique(Path.Combine(showPath, "Extras", stem), Path.GetExtension(file.RelativePath), used), "extra"));
        }
        if (unnumbered > 0) context.Notes.Add(Count(unnumbered, "vidéo sans numéro placée dans Extras", "vidéos sans numéro placées dans Extras"));
        var unmatched = 0;
        foreach (var subtitle in subtitles)
        {
            var number = EpisodeOf(subtitle, context, single: false);
            var stem = number is not null && episodeStems.TryGetValue(number.Label, out var match) ? match : episodeStems.Count == 1 ? episodeStems.Values.Single() : null;
            if (stem is null) { unmatched++; continue; }
            operations.Add(Operation(subtitle, Unique(stem + ReleaseName.SubtitleTags(subtitle.RelativePath), Path.GetExtension(subtitle.RelativePath), used), "subtitle"));
        }
        if (unmatched > 0) context.Notes.Add(Count(unmatched, "sous-titre non attribué", "sous-titres non attribués"));
        var cleanTitle = ReleaseName.Key(show).Length > 0 ? show : title;
        var display = episodes.Count == 1 ? $"{cleanTitle} · {episodes.Keys.Single()}" : episodes.Count > 1 ? $"{cleanTitle} · {episodes.Count} épisodes" : cleanTitle;
        return new(kind, display, root, [showPath], operations, context.Notes);
    }

    /// <summary>An existing folder of the library for this title (any punctuation or case), preferring the same year.</summary>
    public static string? FindFolder(string? root, string title, int? year)
    {
        var key = ReleaseName.Key(title);
        if (root is null || key.Length == 0) return null;
        var candidates = ExistingFolders(root).Where(x => ReleaseName.Key(x) == key).ToList();
        if (candidates.Count == 0) return null;
        if (year is not null) return candidates.FirstOrDefault(x => ReleaseName.FolderYearOf(x) == year) ?? candidates.FirstOrDefault(x => ReleaseName.FolderYearOf(x) is null);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string MovieFolder(string root, string title, int? year) =>
        FindFolder(root, title, year) ?? ReleaseName.SafeName(year is null ? title : $"{title} ({year})");

    private static List<string> ExistingFolders(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>().ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>A destination not already claimed by this plan: "Name.en.srt", then "Name.en.2.srt".</summary>
    private static string Unique(string stem, string extension, HashSet<string> used)
    {
        var candidate = stem + extension.ToLowerInvariant();
        for (var i = 2; !used.Add(candidate); i++) candidate = $"{stem}.{i}{extension.ToLowerInvariant()}";
        return candidate;
    }

    private static ImportOperation Operation(SourceFile file, string destination, string role) => new(file.Path, destination, role, file.RelativePath);
    private static ImportPlan Ignore(string reason) => new(MediaKind.Movie, "", null, [], [], [], Ignored: reason);
    private static ImportPlan Blocked(MediaKind kind, string problem) => new(kind, "", null, [], [], [], Problem: problem);
    private static string Count(int count, string one, string many) => $"{count} {(count > 1 ? many : one)}";
}
