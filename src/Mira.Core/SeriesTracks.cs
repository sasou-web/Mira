using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// The audio and subtitles last chosen in a series, by language and name rather than by track number: the next episodes
/// start with the same ones (« Complet » rather than « Panneaux », the French dub), as on Mira web.
/// </summary>
/// <param name="Audio">Language of the audio track (« jpn »); null when the file does not say.</param>
/// <param name="Subtitles">False: subtitles turned off.</param>
/// <param name="Forced">The subtitles chosen are forced ones (signs and foreign lines only).</param>
public sealed record TrackMemory(string? Audio, string? AudioTitle, bool Subtitles, string? Subtitle, string? SubtitleTitle, bool Forced);

/// <summary>A choice of tracks in mpv's track-list, kept for a series and found again in its other episodes.</summary>
public static class SeriesTracks
{
    private sealed record Track(string Id, string Type, string? Language, string? Title, bool Forced);

    /// <summary>What the tracks playing (mpv's aid and sid) leave for the next episodes; null without a known audio track.</summary>
    public static TrackMemory? Remember(string? trackList, string? audioId, string? subtitleId)
    {
        var tracks = Read(trackList);
        if (tracks.FirstOrDefault(t => t.Type == "audio" && t.Id == audioId) is not { } audio) return null;
        var subtitle = tracks.FirstOrDefault(t => t.Type == "sub" && t.Id == subtitleId);
        return new(audio.Language, audio.Title, subtitle is not null, subtitle?.Language, subtitle?.Title, subtitle?.Forced ?? false);
    }

    /// <summary>
    /// The tracks of another episode that match a memory, as mpv's aid and sid (« no »: subtitles off). Null where no track
    /// matches: mpv keeps its own choice, from the languages of the settings.
    /// </summary>
    public static (string? Audio, string? Subtitle) Choose(string? trackList, TrackMemory memory)
    {
        var tracks = Read(trackList);
        var audio = Best(tracks.Where(t => t.Type == "audio"), memory.Audio, memory.AudioTitle, null);
        var subtitle = memory.Subtitles ? Best(tracks.Where(t => t.Type == "sub"), memory.Subtitle, memory.SubtitleTitle, memory.Forced)?.Id : "no";
        return (audio?.Id, subtitle);
    }

    /// <summary>The same language (or, without one, the same name); among them the same name, then forced or not alike, then the file's order.</summary>
    private static Track? Best(IEnumerable<Track> tracks, string? language, string? title, bool? forced)
    {
        var key = LanguageKey(language);
        return tracks.Where(t => key is not null ? LanguageKey(t.Language) == key : title is not null && LanguageKey(t.Language) is null && SameTitle(t.Title, title))
            .OrderByDescending(t => SameTitle(t.Title, title)).ThenByDescending(t => forced is not { } value || t.Forced == value).FirstOrDefault();
    }
    private static bool SameTitle(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
    /// <summary>One key per language, whatever its code: « fre », « fra » and « fr » are all French; null when undetermined.</summary>
    public static string? LanguageKey(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var main = code.Trim().Split('-', '_')[0].ToLowerInvariant();
        return main is "und" or "zxx" or "mis" or "mul" ? null : PlayerText.Language(main) ?? main;
    }
    private static List<Track> Read(string? trackList)
    {
        try
        {
            using var doc = JsonDocument.Parse(trackList ?? "[]");
            return doc.RootElement.EnumerateArray().Where(t => t.TryGetProperty("type", out var type) && type.GetString() is "audio" or "sub").Select(t =>
            {
                string? Text(string name) => t.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) && v.ToString() is { Length: > 0 } text ? text : null;
                return new Track(Text("id") ?? "", Text("type")!, Text("lang"), Text("title"), t.TryGetProperty("forced", out var f) && f.ValueKind == JsonValueKind.True);
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return []; }
    }
}
