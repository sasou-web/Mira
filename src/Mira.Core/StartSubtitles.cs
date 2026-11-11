using System.Text.Json;

namespace Mira.Core;

/// <summary>
/// The subtitles a title starts with when no memory of its series decides, from the order of the settings' languages:
/// in the audio's language, forced subtitles only (signs, foreign lines); audio in the first language of the order (one's
/// own), nothing else; otherwise the first language of the order with full subtitles. The same rule for a conversion
/// (<see cref="ServerStreams.SubtitleFor"/>), for mpv playing the file on Windows and Mac (<see cref="ForMpv"/>: mpv's own
/// rule leaves out every full subtitle when the audio's language is anywhere in its slang, French ones of an English film
/// for a Mira in French), and on iPhone (TrackChoice.swift). Cases shared in tests/fixtures/subtitle-start.json.
/// </summary>
public static class StartSubtitles
{
    /// <param name="Id">The player's id of the track (mpv's, or Jellyfin's index).</param>
    /// <param name="Default">The file marks it as its default: first among those of its language.</param>
    public sealed record Track(string Id, string? Language, bool Forced, bool Default = false);

    /// <summary>
    /// The subtitles to start with (<paramref name="subtitles"/> in the file's order): Decided with the track's id, or
    /// null for none; not Decided when no language of the order has subtitles and the audio's is not one of them, where the
    /// player keeps its own choice (subtitles whose language is not said, marked as the file's default).
    /// </summary>
    public static (bool Decided, string? Id) Choose(IReadOnlyList<Track> subtitles, string? audioLanguage, string? order)
    {
        var languages = (order ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SeriesTracks.LanguageKey).OfType<string>().Distinct().ToList();
        if (languages.Count == 0) return (false, null);
        var spoken = SeriesTracks.LanguageKey(audioLanguage);
        var forced = spoken is null ? null : subtitles.FirstOrDefault(t => t.Forced && SeriesTracks.LanguageKey(t.Language) == spoken);
        // A film in one's own language: no subtitles but its forced ones, whatever other languages it has.
        if (spoken == languages[0]) return (true, forced?.Id);
        foreach (var language in languages)
        {
            if (language == spoken) continue;
            var full = subtitles.Where(t => !t.Forced && SeriesTracks.LanguageKey(t.Language) == language).OrderByDescending(t => t.Default).FirstOrDefault();
            if (full is not null) return (true, full.Id);
        }
        if (forced is not null) return (true, forced.Id);
        return spoken is not null && languages.Contains(spoken) ? (true, null) : (false, null);
    }

    /// <summary>
    /// mpv's sid to start with, from its <c>track-list</c> once the external subtitles are added: « no » for none, null to
    /// leave mpv's own choice. <paramref name="forcedFiles"/>: the external files given as forced, which mpv cannot tell
    /// from a URL of Jellyfin (Stream.srt).
    /// </summary>
    public static string? ForMpv(string? trackList, string? order, IReadOnlyCollection<string>? forcedFiles = null)
    {
        var subtitles = new List<Track>(); string? audio = null;
        try
        {
            using var doc = JsonDocument.Parse(trackList ?? "[]");
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                string? Text(string name) => t.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number && v.ToString() is { Length: > 0 } text ? text : null;
                bool Flag(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                switch (Text("type"))
                {
                    case "audio" when Flag("selected"): audio = Text("lang"); break;
                    case "sub" when Text("id") is { } id:
                        var file = Text("external-filename");
                        subtitles.Add(new(id, Text("lang"), Flag("forced") || file is not null && forcedFiles?.Any(f => SameFile(f, file)) == true, Flag("default")));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
        var (decided, chosen) = Choose(subtitles, audio, order);
        return decided ? chosen ?? "no" : null;
    }
    private static bool SameFile(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
