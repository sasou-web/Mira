using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Mira.Core.TorLink;

namespace Mira.Core.Subtitles;

/// <summary>
/// What is known of the video file without sending anything anywhere: its name (read as a release by
/// <see cref="ReleaseRanking.Parse"/>: group, source, resolution, codec, and the edition tags it carries), what Jellyfin
/// read in it (duration, frame rate, width), and which episode it is.
/// </summary>
public sealed record VideoFacts(string FileName, TimeSpan? Duration = null, double? Fps = null, int? Width = null, int? Season = null, int? Episode = null, int? Year = null)
{
    public ReleaseInfo Release { get; } = ReleaseRanking.Parse(Path.GetFileName(FileName));
    public IReadOnlySet<string> Editions { get; } = SubtitleMatch.EditionsOf(Path.GetFileName(FileName));
}

/// <summary>
/// A subtitle a source offers: where it comes from, the release it says it was made for (and the names of its files,
/// when the source gives them), its frame rate when known, and what the source says of it. HashMatch: the source
/// recognised the video file itself (OpenSubtitles' moviehash).
/// </summary>
public sealed record SubtitleCandidate(string Source, string Id, string Language, string ReleaseName, double? Fps = null, bool HashMatch = false,
    bool HearingImpaired = false, bool Forced = false, bool MachineTranslated = false, bool Trusted = false, int Downloads = 0, double? Rating = null,
    string Format = "srt", IReadOnlyList<string>? FileNames = null);

/// <summary>Subtitles for the deaf and hard of hearing: taken like the others, preferred, or left aside.</summary>
public enum HearingPreference { Any, Prefer, Avoid }
/// <summary>What the person accepts: hearing-impaired subtitles, machine translations, and whether forced ones are wanted.</summary>
public sealed record MatchPreferences(HearingPreference Hearing = HearingPreference.Any, bool AllowMachineTranslated = false, bool Forced = false);

/// <summary>
/// Sure: the source recognised the file (hash). Good: the same release or close enough to take as is. IfSynced: worth
/// taking only once the alignment on the audio confirms it (other group, other frame rate, edition not named).
/// Rejected: never in automatic mode (still shown in « Chercher des sous-titres »).
/// </summary>
public enum MatchVerdict { Rejected, IfSynced, Good, Sure }

/// <summary>Why a candidate scored as it did: a code (for the journal, said in the language of the reader by <see cref="SubtitleMatch.Text"/>) and a detail.</summary>
public sealed record MatchReason(string Code, string? Detail = null);

/// <summary>A candidate scored for a video: 0 to 100, its verdict and the reasons.</summary>
public sealed record SubtitleScore(SubtitleCandidate Candidate, int Score, MatchVerdict Verdict, IReadOnlyList<MatchReason> Reasons);

/// <summary>What a downloaded file is worth: readable, and timed for a video of that length.</summary>
public enum SubtitleCheckVerdict { Ok, Unreadable, TooFew, TooShort, TooLong }
public sealed record SubtitleCheck(SubtitleCheckVerdict Verdict, int Cues, TimeSpan LastEnd);

/// <summary>
/// « Sous-titres automatiques »: how well a subtitle offered by a source fits the video file, computed here from what the
/// source says of it and what Mira knows of the file (nothing of the file leaves for this). The hash is certain; else
/// the release group, the source (WEB-DL against BluRay: other cuts, other lengths), the frame rate, the resolution and
/// codec add up, and another edition, another episode, a machine translation are out. Thresholds: <see cref="GoodScore"/>
/// to take as is, <see cref="SyncedScore"/> to take once the alignment on the audio confirms. Then the downloaded
/// file is read and its length checked against the video's (<see cref="Check"/>). Shared cases in
/// tests/fixtures/subtitle-match.json.
/// </summary>
public static partial class SubtitleMatch
{
    public const int GoodScore = 60, SyncedScore = 35;
    private const double FpsTolerance = 0.05;
    // Editions that are other cuts of the film (other lengths): subtitles of one do not fit another. Remastered is the same cut.
    private static readonly (string Key, string Pattern)[] EditionPatterns =
    [
        ("extended", @"extended(?:[ ._-]?(?:cut|edition|version))?"), ("unrated", "unrated"), ("uncut", "uncut"), ("theatrical", @"theatrical(?:[ ._-]?(?:cut|edition|version))?"),
        ("directors", @"director'?s?[ ._-]?cut|dir[ ._-]?cut"), ("finalcut", @"final[ ._-]?cut"), ("ultimate", @"ultimate[ ._-]?(?:cut|edition)"), ("imax", "imax"),
        ("criterion", "criterion"), ("special", @"special[ ._-]?edition"), ("openmatte", @"open[ ._-]?matte"), ("despecialized", "despecialized"),
    ];
    private static readonly Dictionary<string, Regex> EditionRegexes = EditionPatterns.ToDictionary(x => x.Key, x => new Regex($"(?<![a-z0-9])(?:{x.Pattern})(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    private static readonly string[][] CompatibleSources = [["webdl", "webrip"], ["bluray", "remux"]];

    /// <summary>The edition tags of a release or file name, as keys (« extended », « directors »…); empty for the plain cut.</summary>
    public static IReadOnlySet<string> EditionsOf(string? name)
    {
        var text = (name ?? "").Replace('.', ' ').Replace('_', ' ');
        return EditionRegexes.Where(x => x.Value.IsMatch(text)).Select(x => x.Key).ToHashSet();
    }

    /// <summary>The score of one candidate for the video, with its verdict and reasons.</summary>
    public static SubtitleScore Score(VideoFacts video, SubtitleCandidate candidate, MatchPreferences? preferences = null)
    {
        preferences ??= new MatchPreferences();
        var reasons = new List<MatchReason>();
        bool rejected = false, needsSync = false;
        void Out(string code, string? detail = null) { reasons.Add(new MatchReason(code, detail)); rejected = true; }

        if (candidate.Forced != preferences.Forced) Out(candidate.Forced ? "forced" : "not-forced");
        if (candidate.MachineTranslated && !preferences.AllowMachineTranslated) Out("machine");
        if (candidate.HashMatch)
        {
            reasons.Insert(0, new MatchReason("hash"));
            if (candidate.HearingImpaired) reasons.Add(new MatchReason("hearing"));
            return new SubtitleScore(candidate, rejected ? 0 : 100, rejected ? MatchVerdict.Rejected : MatchVerdict.Sure, reasons);
        }

        // The release the subtitles were made for: the best reading among the release name and the file names.
        var names = new List<string> { candidate.ReleaseName };
        if (candidate.FileNames is { Count: > 0 } files) names.AddRange(files);
        var best = names.Select(WithoutSubtitleExtension).Select(x => (Name: x, Release: ReleaseRanking.Parse(x), Points: 0)).ToList();
        for (var i = 0; i < best.Count; i++)
        {
            var points = 0; var release = best[i].Release;
            if (SameGroup(video.Release.Group, release.Group)) points += 40;
            if (video.Release.Source is { } source && release.Source is { } other)
                points += source == other ? 20 : Compatible(source, other) ? 10 : -10;
            if (video.Release.Resolution is { } p && release.Resolution == p) points += 5;
            if (video.Release.Codec is { } codec && string.Equals(release.Codec, codec, StringComparison.OrdinalIgnoreCase)) points += 5;
            best[i] = best[i] with { Points = points };
        }
        var chosen = best.OrderByDescending(x => x.Points).First();
        var said = chosen.Release;

        // Out: another episode, another year (a remake), another cut.
        if (video.Episode is { } episode && said.Episode is { } saidEpisode && (saidEpisode != episode || (video.Season is { } season && said.Season is { } saidSeason && saidSeason != season)))
            Out("other-episode", ReleaseRanking.EpisodeLabel(said));
        if (video.Episode is not null && said.Episode is null && said.Pack && video.Season is { } s && said.Season is { } ps && ps != s) Out("other-episode", ReleaseRanking.EpisodeLabel(said));
        if (video.Year is { } year && said.Year is { } saidYear && Math.Abs(saidYear - year) > 1 && video.Episode is null) Out("other-year", saidYear.ToString(CultureInfo.InvariantCulture));
        // The cut: named by the candidate and not the video's, it is another file; named by the video alone, the
        // candidate may be either (many uploads say nothing): worth taking once the audio confirms it.
        var editions = EditionsOf(chosen.Name);
        if (!editions.SetEquals(video.Editions))
        {
            if (editions.Count > 0) Out("other-edition", string.Join(", ", editions));
            else { needsSync = true; reasons.Add(new MatchReason("edition-unsaid", string.Join(", ", video.Editions))); }
        }

        var score = 20 + chosen.Points;
        if (SameGroup(video.Release.Group, said.Group)) reasons.Add(new MatchReason("same-group", said.Group));
        else if (video.Release.Group is { } g && said.Group is { } sg) reasons.Add(new MatchReason("other-group", sg));
        if (video.Release.Source is { } vs && said.Source is { } ss)
            reasons.Add(new MatchReason(vs == ss ? "same-source" : Compatible(vs, ss) ? "close-source" : "other-source", ReleaseRanking.SourceLabel(ss)));
        if (video.Fps is { } fps && candidate.Fps is { } cfps && cfps > 0)
        {
            if (Math.Abs(fps - cfps) <= FpsTolerance) { score += 15; reasons.Add(new MatchReason("same-fps", Fps(cfps))); }
            else { score -= 25; needsSync = true; reasons.Add(new MatchReason("other-fps", Fps(cfps) + " / " + Fps(fps))); }
        }
        if (candidate.Trusted) { score += 5; reasons.Add(new MatchReason("trusted")); }
        if (candidate.HearingImpaired)
        {
            reasons.Add(new MatchReason("hearing"));
            if (preferences.Hearing == HearingPreference.Prefer) score += 5;
            else if (preferences.Hearing == HearingPreference.Avoid) score -= 15;
        }
        else if (preferences.Hearing == HearingPreference.Prefer) score -= 5;
        if (candidate.Downloads > 0) score += Math.Min(5, (int)Math.Log10(candidate.Downloads + 1));
        if (candidate.Rating is >= 8) score += 2;
        score = Math.Clamp(score, 0, 99);

        var verdict = rejected ? MatchVerdict.Rejected
            : score >= GoodScore && !needsSync ? MatchVerdict.Good
            : score >= SyncedScore ? MatchVerdict.IfSynced
            : MatchVerdict.Rejected;
        return new SubtitleScore(candidate, rejected ? 0 : score, verdict, reasons);
    }

    /// <summary>Every candidate scored, the best first (verdict, then score, then downloads).</summary>
    public static List<SubtitleScore> Rank(VideoFacts video, IEnumerable<SubtitleCandidate> candidates, MatchPreferences? preferences = null) =>
        candidates.Select(x => Score(video, x, preferences)).OrderByDescending(x => x.Verdict).ThenByDescending(x => x.Score).ThenByDescending(x => x.Candidate.Downloads).ToList();

    /// <summary>
    /// The candidates worth trying without anyone looking, best first: Sure and Good ones; IfSynced ones only when the
    /// alignment on the audio can confirm them (<paramref name="syncAvailable"/>). Empty: nothing is taken for this title now.
    /// </summary>
    public static List<SubtitleScore> Automatic(IEnumerable<SubtitleScore> ranked, bool syncAvailable) =>
        ranked.Where(x => x.Verdict >= MatchVerdict.Good || (syncAvailable && x.Verdict == MatchVerdict.IfSynced)).ToList();

    /// <summary>
    /// A downloaded file against the video: readable (five cues at least), timed for a video of that length (the last cue
    /// ends between 70 % of the video and a minute or 2 % past its end), and dense enough (two cues per five minutes).
    /// Without the video's duration, only its readability.
    /// </summary>
    public static SubtitleCheck Check(IReadOnlyList<SubtitleCue> cues, TimeSpan? duration)
    {
        if (cues.Count < 5) return new SubtitleCheck(SubtitleCheckVerdict.Unreadable, cues.Count, TimeSpan.Zero);
        var last = cues.Max(x => x.End);
        if (duration is not { } length || length <= TimeSpan.Zero) return new SubtitleCheck(SubtitleCheckVerdict.Ok, cues.Count, last);
        var slack = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(60).Ticks, length.Ticks / 50));
        var verdict = last > length + slack ? SubtitleCheckVerdict.TooLong
            : last < TimeSpan.FromTicks(length.Ticks * 7 / 10) ? SubtitleCheckVerdict.TooShort
            : cues.Count < length.TotalMinutes * 0.4 ? SubtitleCheckVerdict.TooFew
            : SubtitleCheckVerdict.Ok;
        return new SubtitleCheck(verdict, cues.Count, last);
    }

    /// <summary>
    /// A reason in words: « même release (QTZ) », « cadence différente (25 / 23,976 i/s) ». In Mira's language, or in
    /// the one asked (<paramref name="french"/>: the extension answers each request in its own language).
    /// </summary>
    public static string Text(MatchReason reason, bool? french = null)
    {
        var fr = french ?? IsFrench;
        // Frame rates are written with a dot (CultureInfo.InvariantCulture), and said with the reader's decimal mark.
        var d = reason.Code is "same-fps" or "other-fps" && fr ? reason.Detail?.Replace('.', ',') : reason.Detail;
        return reason.Code switch
        {
            "hash" => fr ? "pour ce fichier (empreinte reconnue)" : "for this file (fingerprint recognised)",
            "same-group" => fr ? $"même release ({d})" : $"same release ({d})",
            "other-group" => fr ? $"autre release ({d})" : $"other release ({d})",
            "same-source" => fr ? $"même source ({d})" : $"same source ({d})",
            "close-source" => fr ? $"source proche ({d})" : $"close source ({d})",
            "other-source" => fr ? $"autre source ({d})" : $"other source ({d})",
            "same-fps" => fr ? $"même cadence ({d} i/s)" : $"same frame rate ({d} fps)",
            "other-fps" => fr ? $"cadence différente ({d} i/s)" : $"different frame rate ({d} fps)",
            "trusted" => fr ? "auteur de confiance" : "trusted uploader",
            "hearing" => fr ? "pour sourds et malentendants" : "for the deaf and hard of hearing",
            "machine" => fr ? "traduction automatique" : "machine translation",
            "forced" => fr ? "sous-titres forcés" : "forced subtitles",
            "not-forced" => fr ? "pas des sous-titres forcés" : "not forced subtitles",
            "other-episode" => fr ? $"autre épisode ({d})" : $"other episode ({d})",
            "other-year" => fr ? $"autre année ({d})" : $"other year ({d})",
            "other-edition" => fr ? $"autre montage ({d})" : $"other cut ({d})",
            "edition-unsaid" => fr ? $"montage incertain ({d})" : $"uncertain cut ({d})",
            _ => d ?? reason.Code,
        };
    }

    /// <summary>The verdict of a check in words: why a downloaded file was set aside.</summary>
    public static string Text(SubtitleCheck check, bool? french = null)
    {
        var fr = french ?? IsFrench;
        return check.Verdict switch
        {
            SubtitleCheckVerdict.Ok => fr ? "fichier vérifié" : "file checked",
            SubtitleCheckVerdict.Unreadable => fr ? "fichier illisible" : "unreadable file",
            SubtitleCheckVerdict.TooFew => fr ? $"trop peu de lignes ({check.Cues})" : $"too few lines ({check.Cues})",
            SubtitleCheckVerdict.TooShort => fr ? "finit bien avant la vidéo" : "ends well before the video",
            _ => fr ? "dure plus que la vidéo" : "runs longer than the video",
        };
    }

    private static string WithoutSubtitleExtension(string name) => ReleaseName.IsSubtitle(name) ? name[..name.LastIndexOf('.')] : name;
    private static bool SameGroup(string? a, string? b) => a is { Length: > 0 } && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Compatible(string a, string b) => CompatibleSources.Any(x => x.Contains(a) && x.Contains(b));
    private static string Fps(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);
}
