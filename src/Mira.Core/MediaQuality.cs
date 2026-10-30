using System.Globalization;

namespace Mira.Core;

/// <summary>What the detail page says of the file a title plays: its picture (« 4K · HDR10 ») and when it ends if started now.</summary>
public static class MediaQuality
{
    /// <summary>
    /// « 4K · HDR10 », « 1080p », « SD », « 720p · Dolby Vision »: from the first video stream of the first file, by width or
    /// height (a film cropped to 3840 × 1600 is still 4K); null when the file is not known. Same as Mira web (tests/fixtures/quality.json).
    /// </summary>
    public static string? Label(MediaItem? item) => Label(item?.MediaSources?.FirstOrDefault());
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
    /// <summary>« Fin à 23 h 12 »: when the title ends if started now, from where it resumes; null without a length.</summary>
    public static string? EndsAt(MediaItem? item, DateTime now, bool resume)
    {
        if (item?.RunTimeTicks is not > 0) return null;
        var left = item.RunTimeTicks.Value - (resume ? Math.Max(0, item.UserData.PlaybackPositionTicks) : 0);
        if (left <= 0) return null;
        var end = now + TimeSpan.FromTicks(left);
        return IsFrench ? string.Create(CultureInfo.InvariantCulture, $"Fin à {end.Hour} h {end.Minute:00}") : "Ends at " + Time(end);
    }
    /// <summary>« 1 h 27 restantes », « 42 min restantes » (« 1h 27m left »): beside Reprendre, what is left of a title begun.</summary>
    public static string? Left(MediaItem? item)
    {
        if (item?.RunTimeTicks is not > 0 || item.UserData.PlaybackPositionTicks <= 0) return null;
        var minutes = (int)Math.Ceiling(TimeSpan.FromTicks(item.RunTimeTicks.Value - item.UserData.PlaybackPositionTicks).TotalMinutes);
        if (minutes <= 0) return null;
        return minutes >= 60
            ? L($"{minutes / 60} h {minutes % 60:00} restantes", $"{minutes / 60}h {minutes % 60:00}m left")
            : L($"{minutes} min restantes", $"{minutes} min left");
    }
}
