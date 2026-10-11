using System.Text.Json;

namespace Mira.Core;

/// <summary>An audio or subtitle track: mpv id, "audio" or "sub", its menu name and details (codec, channels…).</summary>
public sealed record MpvTrack(string Id, string Type, string Label, string Details);

/// <summary>mpv's track-list and chapter-list properties (JSON), read the same way by the Windows and Mac players.</summary>
public static class MpvLists
{
    /// <summary>Audio and subtitle tracks, named for the menu: title or language first, then codec and channels.</summary>
    public static List<MpvTrack> Tracks(string? trackList)
    {
        try
        {
            using var doc = JsonDocument.Parse(trackList ?? "[]");
            return doc.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() is "audio" or "sub").Select(t =>
            {
                string Read(string name) => t.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.ToString() : "";
                bool Flag(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                var channels = t.TryGetProperty("demux-channel-count", out var count) && count.TryGetInt32(out var value) ? value : 0;
                var id = Read("id"); var type = Read("type");
                var (label, details) = PlayerText.Track(id, Read("title"), Read("lang"), Read("codec"), type == "audio" ? channels : 0, Flag("forced"), Flag("external"));
                return new MpvTrack(id, type, label, details);
            }).ToList();
        }
        catch (JsonException) { return []; }
    }
    /// <summary>Chapters stored in the media file itself (Matroska/MP4 chapter titles and times).</summary>
    public static List<ChapterMark> Chapters(string? chapterList)
    {
        try
        {
            using var doc = JsonDocument.Parse(chapterList ?? "[]");
            return doc.RootElement.EnumerateArray()
                .Where(c => c.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Number)
                .Select(c => new ChapterMark(c.GetProperty("time").GetDouble(), c.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null))
                .OrderBy(c => c.Start).ToList();
        }
        catch (JsonException) { return []; }
    }
}
