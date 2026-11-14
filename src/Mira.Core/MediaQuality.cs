using System.Globalization;

namespace Mira.Core;

/// <summary>What the detail page says of the file a title plays: its picture (« 4K · HDR10 ») and when it ends if started now.</summary>
public static class MediaQuality
{
    /// <summary>
    /// « 4K · HDR10 », « 1080p », « SD », « 720p · Dolby Vision »: from the first video stream of the first file, by width or
    /// height (a film cropped to 3840 × 1600 is still 4K); null when the file is not known. Same as Mira web (tests/fixtures/quality.json).
    /// </summary>
    public static string? Label(MediaItem? item) => Label(item?.OwnSource);
    public static string? Label(MediaSource? source)
    {
        if (source?.MediaStreams.FirstOrDefault(s => s.Type == "Video") is not { } video) return null;
        int width = video.Width ?? 0, height = video.Height ?? 0;
        var size = width >= 3800 || height >= 2000 ? "4K"
            : width >= 2500 || height >= 1400 ? "1440p"
            : width >= 1800 || height >= 1000 ? "1080p"
            : width >= 1200 || height >= 700 ? "720p"
            : width > 0 || height > 0 ? "SD" : null;
        var range = video.VideoRangeType switch
        {
            "HDR10" => "HDR10",
            "HDR10Plus" => "HDR10+",
            "HLG" => "HLG",
            { } type when type.StartsWith("DOVI", StringComparison.Ordinal) && type != "DOVIInvalid" => "Dolby Vision",
            _ => video.VideoRange == "HDR" ? "HDR" : null
        };
        var parts = new[] { size, range }.Where(x => x is not null).ToArray();
        return parts.Length > 0 ? string.Join("  ·  ", parts) : null;
    }
    /// <summary>
    /// « Fin à 23 h 12 »: when the title ends if started now, from where it resumes; null without a length. On a series'
    /// page (<paramref name="named"/>), the episode Lecture opens says it is its own end: « S01 E03 finit à 23 h 12 ».
    /// </summary>
    public static string? EndsAt(MediaItem? item, DateTime now, bool resume, bool named = false)
    {
        if (item?.RunTimeTicks is not > 0) return null;
        var left = item.RunTimeTicks.Value - (resume ? Math.Max(0, item.UserData.PlaybackPositionTicks) : 0);
        if (left <= 0) return null;
        var end = now + TimeSpan.FromTicks(left);
        var at = IsFrench ? string.Create(CultureInfo.InvariantCulture, $"{end.Hour} h {end.Minute:00}") : Time(end);
        if (named && item.Type == "Episode")
            return string.Create(CultureInfo.InvariantCulture, $"S{item.ParentIndexNumber ?? 1:00} E{item.IndexNumber ?? 1:00} ") + L("finit à ", "ends at ") + at;
        return L("Fin à ", "Ends at ") + at;
    }
    /// <summary>
    /// « 1 h 27 restantes », « 42 min restantes » (“1 hr 27 min left”, written as the durations): beside Reprendre and
    /// on the cards of Continuer à regarder, what is left of a title begun, the same minute everywhere (rounded up).
    /// </summary>
    public static string? Left(MediaItem? item)
    {
        if (item?.RunTimeTicks is not > 0 || !item.Resumable) return null;
        return Left(TimeSpan.FromTicks(item.RunTimeTicks.Value - item.UserData.PlaybackPositionTicks));
    }
    /// <summary>The same of a time left (the episode playing, in the player's list of episodes), to the nearest minute.</summary>
    public static string? Left(TimeSpan left)
    {
        if (left <= TimeSpan.Zero) return null;
        var minutes = Math.Max(1, (int)Math.Round(left.TotalMinutes, MidpointRounding.AwayFromZero));
        return minutes >= 60
            ? L($"{minutes / 60} h {minutes % 60:00} restantes", $"{minutes / 60} hr {minutes % 60} min left")
            : L($"{minutes} min restantes", $"{minutes} min left");
    }
}
