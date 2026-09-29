using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>Jellyfin media segment (10.10+): typed start/end markers such as an intro or credits.</summary>
public sealed record MediaSegment
{
    public string Type { get; init; } = "Unknown";
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}
public sealed record MediaSegmentsResult { public List<MediaSegment> Items { get; init; } = []; }
public sealed record ChapterInfo
{
    public long StartPositionTicks { get; init; }
    public string? Name { get; init; }
}
public readonly record struct ChapterMark(double Start, string? Title);
public enum SkipKind { Intro, Outro, Recap, Preview, Commercial }
public readonly record struct SkipSegment(SkipKind Kind, double Start, double End);

/// <summary>Turns Jellyfin segments and chapter titles into skippable passages.</summary>
public static class PlaybackMarkers
{
    /// <summary>Shorter passages are noise: a skip button would vanish before it can be pressed.</summary>
    public const double MinimumLength = 3;
    /// <summary>A chapter longer than this is not an opening, recap or preview, whatever its title.</summary>
    public const double MaximumShortPassage = 360;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly Regex Recap = new(@"\b(recap|recapitulatif|previously|precedemment|resume de l.episode precedent)\b", Options);
    private static readonly Regex Preview = new(@"\b(preview|next episode|prochain episode|apercu|bande annonce|yokoku)\b", Options);
    private static readonly Regex Intro = new(@"\b(opening|intro|introduction|ouverture|theme song|op\s?\d{0,2}|generique (de )?(debut|d.ouverture))\b", Options);
    private static readonly Regex Outro = new(@"\b(ending|outro|credits|end credits|ed\s?\d{0,2}|generique (de )?fin)\b", Options);
    private static readonly Regex Generic = new(@"\bgenerique\b", Options);

    public static double Seconds(long ticks) => ticks / (double)TimeSpan.TicksPerSecond;

    public static List<SkipSegment> FromJellyfin(IEnumerable<MediaSegment> segments) => segments
        .Select(s => (Kind: Parse(s.Type), Start: Seconds(s.StartTicks), End: Seconds(s.EndTicks)))
        .Where(s => s.Kind is not null && s.Start >= 0 && s.End - s.Start >= MinimumLength)
        .Select(s => new SkipSegment(s.Kind!.Value, s.Start, s.End))
        .OrderBy(s => s.Start).ToList();

    public static List<ChapterMark> FromJellyfin(IEnumerable<ChapterInfo> chapters) =>
        chapters.Select(c => new ChapterMark(Seconds(c.StartPositionTicks), c.Name)).Where(c => c.Start >= 0).OrderBy(c => c.Start).ToList();

    /// <summary>Anime and film releases often name their chapters: "Opening", "ED", "Générique de fin"…</summary>
    public static List<SkipSegment> FromChapters(IReadOnlyList<ChapterMark> chapters, double duration)
    {
        var ordered = chapters.Where(c => double.IsFinite(c.Start) && c.Start >= 0).OrderBy(c => c.Start).ToList();
        var result = new List<SkipSegment>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = ordered[i].Start;
            var end = i + 1 < ordered.Count ? ordered[i + 1].Start : duration;
            if (!double.IsFinite(end) || end - start < MinimumLength) continue;
            if (Classify(ordered[i].Title, start, duration) is not { } kind) continue;
            if (kind is not SkipKind.Outro && end - start > MaximumShortPassage) continue;
            result.Add(new(kind, start, end));
        }
        return result;
    }
    public static SkipKind? Classify(string? title, double start = 0, double duration = 0)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var text = Plain(title);
        if (Recap.IsMatch(text)) return SkipKind.Recap;
        if (Preview.IsMatch(text)) return SkipKind.Preview;
        if (Intro.IsMatch(text)) return SkipKind.Intro;
        if (Outro.IsMatch(text)) return SkipKind.Outro;
        // A bare "Générique" is the opening near the start and the credits near the end.
        // "Avant-générique" is the cold open: part of the story, never skipped.
        if (Generic.IsMatch(text) && !text.Contains("avant generique")) return duration > 0 && start > duration / 2 ? SkipKind.Outro : SkipKind.Intro;
        return null;
    }
    /// <summary>Server segments are authoritative; chapter guesses only fill kinds and times the server left empty.</summary>
    public static List<SkipSegment> Merge(IReadOnlyList<SkipSegment> server, IReadOnlyList<SkipSegment> chapters) => server
        .Concat(chapters.Where(c => server.All(s => s.Kind != c.Kind && (c.End <= s.Start || c.Start >= s.End))))
        .OrderBy(s => s.Start).ToList();
    /// <summary>The passage under the playhead. The last second is excluded so the button leaves cleanly.</summary>
    public static SkipSegment? Active(IReadOnlyList<SkipSegment> segments, double position)
    {
        foreach (var segment in segments) if (position >= segment.Start - .25 && position < segment.End - 1) return segment;
        return null;
    }
    /// <summary>True when skipping would only leave further credits/previews or a few seconds of video.</summary>
    public static bool EndsPlayback(IReadOnlyList<SkipSegment> segments, SkipSegment segment, double duration)
    {
        if (duration <= 0 || segment.Kind is not (SkipKind.Outro or SkipKind.Preview)) return false;
        var end = segment.End;
        foreach (var next in segments.OrderBy(s => s.Start))
            if (next.Start >= segment.Start && next.Start <= end + 2 && next.Kind is SkipKind.Outro or SkipKind.Preview) end = Math.Max(end, next.End);
        return duration - end < 5;
    }
    public static string Label(SkipKind kind, bool episode, bool nextEpisode) => nextEpisode ? "Épisode suivant" : kind switch
    {
        SkipKind.Intro => episode ? "Passer l’opening" : "Passer l’intro",
        SkipKind.Outro => episode ? "Passer l’ending" : "Passer le générique",
        SkipKind.Recap => "Passer le récap",
        SkipKind.Preview => "Passer l’aperçu",
        _ => "Passer la pub"
    };
    private static SkipKind? Parse(string? type) => type switch
    {
        "Intro" => SkipKind.Intro, "Outro" => SkipKind.Outro, "Recap" => SkipKind.Recap,
        "Preview" => SkipKind.Preview, "Commercial" => SkipKind.Commercial, _ => null
    };
    private static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c is '_' or '-' or '’' ? ' ' : c);
        return builder.ToString().ToLowerInvariant();
    }
}
