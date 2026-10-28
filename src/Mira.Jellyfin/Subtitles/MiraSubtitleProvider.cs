using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Mira.Core.TorLink;
using Mira.Core.Subtitles;

namespace Mira.Jellyfin;

/// <summary>
/// « Mira » among Jellyfin's subtitle providers: asked by Jellyfin for a title and a language (« Chercher des
/// sous-titres » of the apps, Jellyfin's own nightly task, and the extension's own loop), it asks the sources set in
/// the Réglages (<see cref="OpenSubtitlesApi"/>, <see cref="SubDlApi"/>), scores every answer for the file
/// (<see cref="SubtitleMatch"/>) and gives them back best first. Asked for an automatic choice, it gives only what is
/// worth taking without anyone looking. A file fetched is read and checked against the video's length before Jellyfin
/// writes it. Nothing of the file's name leaves the server: see <see cref="SubtitleQuery"/>.
/// </summary>
public sealed partial class MiraSubtitleProvider : ISubtitleProvider
{
    public const string ProviderName = "Mira";
    /// <summary>Results given to Jellyfin are remembered this long, to check the file fetched against the video.</summary>
    private static readonly TimeSpan Remember = TimeSpan.FromHours(2);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (SubtitleScore Score, VideoFacts Video, DateTimeOffset At, Guid Item)> Given = new(StringComparer.Ordinal);
    private static readonly Dictionary<(Guid Item, string Language), DateTimeOffset> Fetched = [];

    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IHttpClientFactory _clients;
    private readonly ILogger<MiraSubtitleProvider> _logger;

    public MiraSubtitleProvider(ILibraryManager library, IMediaSourceManager mediaSources, IHttpClientFactory clients, ILogger<MiraSubtitleProvider> logger)
    {
        _library = library; _mediaSources = mediaSources; _clients = clients; _logger = logger;
    }

    public string Name => ProviderName;
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    private static SubtitleConfig Config => Plugin.Subtitles;
    private static SubtitleJournal Journal => Plugin.SubtitleJournal;

    /// <summary>The sources set and ready, in the order they are asked.</summary>
    public List<SubtitleSource> Sources(SubtitleSettings settings)
    {
        var sources = new List<SubtitleSource>();
        var agent = $"Mira v{WebFiles.Version}";
        if (settings.OpenSubtitles.Enabled && (settings.OpenSubtitles.ApiKey.Length > 0 || OpenSubtitlesApi.MiraKey.Length > 0))
            sources.Add(new OpenSubtitlesApi(_clients.CreateClient(), settings.OpenSubtitles.ApiKey.Length > 0 ? settings.OpenSubtitles.ApiKey : OpenSubtitlesApi.MiraKey, agent,
                settings.OpenSubtitles.Username, settings.OpenSubtitles.Password, settings.OpenSubtitles.BaseUrl));
        if (settings.SubDl.Enabled && settings.SubDl.ApiKey.Length > 0)
            sources.Add(new SubDlApi(_clients.CreateClient(), settings.SubDl.ApiKey, agent, settings.SubDl.BaseUrl, settings.SubDl.FilesUrl));
        return sources;
    }

    /// <summary>What the file is, read from Jellyfin's item: never sent anywhere. Season and episode: see <see cref="Numbers"/>.</summary>
    public VideoFacts Facts(Video video, int? season = null, int? episode = null)
    {
        var streams = Streams(video.Id);
        var picture = streams.FirstOrDefault(x => x.Type == MediaStreamType.Video);
        double? fps = picture?.RealFrameRate ?? picture?.AverageFrameRate;
        return new VideoFacts(video.Path ?? "", video.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : null, fps is > 0 ? fps : null, picture?.Width,
            video is Episode ? video.ParentIndexNumber ?? season : null, video is Episode ? video.IndexNumber ?? episode : null, video is Movie ? video.ProductionYear : null);
    }

    /// <summary>
    /// An episode's season and number: Jellyfin's, else those of Jellyfin's request, else those its file name says.
    /// Jellyfin asks for subtitles while it scans, sometimes before it has numbered a new episode (seen on 12.1): asked
    /// without them, a source answers for every episode of the series.
    /// </summary>
    public static (int? Season, int? Episode) Numbers(Video video, int? season = null, int? episode = null)
    {
        if (video is not Episode) return (null, null);
        var named = ReleaseRanking.Parse(Path.GetFileName(video.Path ?? ""));
        return (video.ParentIndexNumber ?? season ?? named.Season, video.IndexNumber ?? episode ?? named.Episode);
    }

    /// <summary>
    /// The streams Jellyfin read in a file. By reflection: IMediaSourceManager.GetMediaStreams(Guid) gives a List on
    /// 10.9 and another collection on 12.x, and a call compiled against 10.9 fails there (MissingMethodException).
    /// </summary>
    public List<MediaStream> Streams(Guid id)
    {
        var method = _mediaSources.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetMediaStreams" && m.GetParameters() is [{ ParameterType: var type }] && type == typeof(Guid));
        return method?.Invoke(_mediaSources, [id]) is System.Collections.IEnumerable found ? found.OfType<MediaStream>().ToList() : [];
    }

    /// <summary>What the sources are asked: ids, season and episode, the fingerprint; the title's name only without an id.</summary>
    public static SubtitleQuery Query(Video video, string? hash, long? size, bool hashOnly, int? season = null, int? number = null)
    {
        var episode = video as Episode;
        var series = episode?.Series;
        string? Id(BaseItem? item, string name) => item?.ProviderIds is { } ids && ids.TryGetValue(name, out var id) && !string.IsNullOrWhiteSpace(id) ? id.Trim() : null;
        return new SubtitleQuery(episode is not null, hash, size, Id(video, "Imdb"), Id(video, "Tmdb"), Id(series, "Imdb"), Id(series, "Tmdb"),
            episode is null ? null : episode.ParentIndexNumber ?? season, episode is null ? null : episode.IndexNumber ?? number, Bare(episode is not null ? series?.Name ?? episode.SeriesName : video.Name), video.ProductionYear, hashOnly);
    }

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.MediaPath) || _library.FindByPath(request.MediaPath, false) is not Video video) return [];
        var settings = Config.Current;
        var sources = Sources(settings);
        if (sources.Count == 0) return [];
        var language = request.Language ?? "";
        if (SubtitleLanguages.Two(language) is not { } two) return [];
        // Jellyfin asks again for a title it just got subtitles for (its scan, then its nightly task, then the refresh
        // that follows, before its database knows the new file: seen on 12.1, three files for one title). Each download
        // counts against the source's daily quota: an automatic search for a language the title already has is not made.
        if (request.IsAutomated && AlreadyHas(video, two)) return [];
        var (season, episode) = Numbers(video, request.ParentIndexNumber, request.IndexNumber);
        if (request.IsAutomated && video is Episode && episode is null) return [];
        var ranked = await RankAsync(video, sources, two, settings.Preferences, request.IsPerfectMatch, cancellationToken, season, episode).ConfigureAwait(false);
        var given = request.IsAutomated ? SubtitleMatch.Automatic(ranked, syncAvailable: false) : ranked;
        if (request.IsPerfectMatch) given = given.Where(x => x.Candidate.HashMatch).ToList();
        var three = SubtitleLanguages.Three(two);
        return given.Select(x => Info(x, three)).ToList();
    }

    /// <summary>
    /// Whether the title already has subtitles in this language: a file beside the video named for it
    /// (« Film.fre.srt », « Film.fr.forced.srt »), or one Mira gave Jellyfin for it within <see cref="Remember"/>.
    /// </summary>
    public static bool AlreadyHas(Video video, string two)
    {
        lock (Gate)
            if (Fetched.TryGetValue((video.Id, two), out var at) && DateTimeOffset.UtcNow - at < Remember) return true;
        return video.Path is { Length: > 0 } path && SubtitleFiles.HasBeside(path, two);
    }

    /// <summary>Every candidate of every source for the video in one language, scored, best first; a source that fails is logged and skipped.</summary>
    public async Task<List<SubtitleScore>> RankAsync(Video video, List<SubtitleSource> sources, string two, MatchPreferences preferences, bool hashOnly, CancellationToken ct,
        int? season = null, int? episode = null)
    {
        if (season is null && episode is null) (season, episode) = Numbers(video);
        var facts = Facts(video, season, episode);
        string? hash = null; long? size = null;
        try
        {
            if (video.Path is { Length: > 0 } path && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) { hash = MovieHash.Compute(path); size = new FileInfo(path).Length; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Mira: could not read {Path} for its hash", video.Path); }
        var query = Query(video, hash, size, hashOnly, season, episode);
        var candidates = new List<SubtitleCandidate>();
        foreach (var source in sources)
        {
            try { candidates.AddRange(await source.SearchAsync(query, two, ct).ConfigureAwait(false)); }
            catch (SubtitleSourceException ex)
            {
                _logger.LogWarning("Mira: {Source} did not answer: {Message}", source.Name, ex.Message);
                Journal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, video.Id.ToString("N"), Title(video), SubtitleLanguages.Three(two), ex.Until is not null ? "quota" : "failed", source.Name, null, null, ex.Message));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.LogWarning(ex, "Mira: {Source} unreachable", source.Name);
                Journal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, video.Id.ToString("N"), Title(video), SubtitleLanguages.Three(two), "failed", source.Name, null, null, ex.Message));
            }
        }
        var ranked = SubtitleMatch.Rank(facts, candidates, preferences);
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var old in Given.Where(x => now - x.Value.At > Remember).Select(x => x.Key).ToList()) Given.Remove(old);
            foreach (var old in Fetched.Where(x => now - x.Value > Remember).Select(x => x.Key).ToList()) Fetched.Remove(old);
            foreach (var score in ranked) Given[Id(score.Candidate)] = (score, facts, now, video.Id);
        }
        return ranked;
    }

    /// <summary>The id given to Jellyfin for a candidate (see <see cref="SubtitleIds"/>).</summary>
    public static string Id(SubtitleCandidate candidate) =>
        SubtitleIds.Make(candidate.Source == "SubDL" ? SubtitleIds.SubDl : SubtitleIds.OpenSubtitles, candidate.Language, candidate.Id);

    private static RemoteSubtitleInfo Info(SubtitleScore score, string language)
    {
        var c = score.Candidate;
        var reasons = string.Join(" · ", score.Reasons.Select(r => SubtitleMatch.Text(r)));
        return new RemoteSubtitleInfo
        {
            Id = Id(c),
            ProviderName = ProviderName,
            Name = c.ReleaseName.Length > 0 ? c.ReleaseName : c.FileNames?.FirstOrDefault() ?? c.Source,
            Format = c.Format,
            Author = c.Source,
            Comment = $"{score.Score}/100{(reasons.Length > 0 ? " · " + reasons : "")}",
            ThreeLetterISOLanguageName = language,
            DownloadCount = c.Downloads,
            CommunityRating = c.Rating is { } rating ? (float)rating : null,
            FrameRate = c.Fps is { } fps ? (float)fps : null,
            IsHashMatch = c.HashMatch,
            HearingImpaired = c.HearingImpaired,
            Forced = c.Forced,
            MachineTranslated = c.MachineTranslated,
            AiTranslated = false,
        };
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        if (SubtitleIds.Parse(id) is not { } parsed) throw new ArgumentException("Not a Mira subtitle id", nameof(id));
        var (sourceKey, language, key) = parsed;
        (SubtitleScore Score, VideoFacts Video, DateTimeOffset At, Guid Item)? given;
        lock (Gate) given = Given.TryGetValue(id, out var g) ? g : null;
        var settings = Config.Current;
        var source = Sources(settings).FirstOrDefault(x => (sourceKey == SubtitleIds.SubDl) == x is SubDlApi) ?? throw new InvalidOperationException("This source is no longer set in Mira's settings");
        var fetched = await FetchCheckedAsync(source, key, given?.Score, given?.Video, language, cancellationToken).ConfigureAwait(false);
        if (given is { } kept && SubtitleLanguages.Two(language) is { } fetchedTwo) lock (Gate) Fetched[(kept.Item, fetchedTwo)] = DateTimeOffset.UtcNow;
        return new SubtitleResponse
        {
            Format = fetched.Format,
            Language = language,
            IsForced = given?.Score.Candidate.Forced ?? false,
            IsHearingImpaired = given?.Score.Candidate.HearingImpaired ?? false,
            Stream = new MemoryStream(fetched.Bytes),
        };
    }

    /// <summary>
    /// The file of a candidate, read (any encoding, format told from the content) and checked against the video's
    /// length when the video is known; given back as UTF-8 in its format. A file that fails the check is not given.
    /// </summary>
    public async Task<FetchedSubtitle> FetchCheckedAsync(SubtitleSource source, string key, SubtitleScore? score, VideoFacts? video, string language, CancellationToken ct)
    {
        var fetched = await source.FetchAsync(key, video?.Season, video?.Episode, ct).ConfigureAwait(false);
        var text = SubtitleText.Decode(fetched.Bytes);
        var format = SubtitleText.FormatOf(text) ?? fetched.Format;
        var cues = SubtitleText.Parse(text);
        var check = SubtitleMatch.Check(cues, video?.Duration);
        var name = video is not null ? Path.GetFileNameWithoutExtension(video.FileName) : key;
        if (check.Verdict != SubtitleCheckVerdict.Ok)
        {
            Journal.Add(new SubtitleJournalEntry(DateTimeOffset.UtcNow, "", name, language, "rejected", source.Name, score?.Candidate.ReleaseName, score?.Score, SubtitleMatch.Text(check)));
            throw new InvalidOperationException($"Mira set this file aside: {SubtitleMatch.Text(check)}");
        }
        if (format == "srt") text = SubtitleText.ToSrt(cues);
        return new FetchedSubtitle(Encoding.UTF8.GetBytes(text.TrimStart('﻿')), fetched.FileName, format);
    }

    /// <summary>How the journal names a title: « Série S01E02 », « Film (2024) ».</summary>
    public static string Title(Video video) => video is Episode episode
        ? $"{episode.SeriesName} S{(episode.ParentIndexNumber ?? 0).ToString("00", CultureInfo.InvariantCulture)}E{(episode.IndexNumber ?? 0).ToString("00", CultureInfo.InvariantCulture)}"
        : video.ProductionYear is { } year ? $"{Bare(video.Name)} ({year})" : video.Name;

    /// <summary>A name without the « (2024) » of its folder (a title Jellyfin found nothing about keeps it).</summary>
    private static string? Bare(string? name) => name is null ? null : YearSuffix().Replace(name, "");

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*\((?:19|20)\d\d\)\s*$")]
    private static partial System.Text.RegularExpressions.Regex YearSuffix();
}
