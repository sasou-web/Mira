using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core.Subtitles;

/// <summary>A line of subtitles: when it shows, when it goes, what it says (override tags removed, lines joined by « \n »).</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Subtitle files as text: the bytes decoded (UTF-8, UTF-16 with a mark, else Windows-1252, the encoding of old French
/// files), the format told from the content, the cues read from SRT, WebVTT and ASS/SSA, and written back as SRT.
/// What « Sous-titres automatiques » checks a downloaded file with (<see cref="SubtitleMatch.Check"/>) and what the
/// alignment shifts.
/// </summary>
public static partial class SubtitleText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    // Windows-1252, 0x80 to 0x9F: where it differs from Latin-1 (« € », « œ », « ’ », « … »); the rest is Latin-1.
    private static readonly string Cp1252High = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";

    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})")] private static partial Regex SrtTimes();
    [GeneratedRegex(@"(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(?:(\d{1,2}):)?(\d{2}):(\d{2})\.(\d{3})")] private static partial Regex VttTimes();
    [GeneratedRegex(@"\{[^}]*\}")] private static partial Regex AssTags();
    [GeneratedRegex(@"</?[A-Za-z][^>]*>")] private static partial Regex HtmlTags();
    [GeneratedRegex(@"\r\n?")] private static partial Regex LineEnds();

    /// <summary>The file as text: by its byte order mark, else UTF-8 when every byte sequence is valid, else Windows-1252.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] is >= 0x80 and <= 0x9F ? Cp1252High[bytes[i] - 0x80] : (char)bytes[i];
        return new string(chars);
    }

    /// <summary>« srt », « vtt » or « ass » from the content itself (a downloaded file is not always named for what it is); null for none of them.</summary>
    public static string? FormatOf(string text)
    {
        var head = text.Length > 4096 ? text[..4096] : text;
        if (head.TrimStart('﻿', ' ', '\r', '\n').StartsWith("WEBVTT", StringComparison.Ordinal)) return "vtt";
        if (head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) || head.Contains("[Events]", StringComparison.OrdinalIgnoreCase)
            || head.Contains("Dialogue:", StringComparison.Ordinal)) return "ass";
        return SrtTimes().IsMatch(head) ? "srt" : null;
    }

    /// <summary>The cues of a file in any of the three formats, in the order of the file; empty when nothing was read.</summary>
    public static List<SubtitleCue> Parse(string text) => FormatOf(text) switch
    {
        "vtt" => ParseVtt(text),
        "ass" => ParseAss(text),
        "srt" => ParseSrt(text),
        _ => [],
    };

    private static List<SubtitleCue> ParseSrt(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in LineEnds().Replace(text, "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var at = lines.FindIndex(x => SrtTimes().IsMatch(x));
            if (at < 0) continue;
            var m = SrtTimes().Match(lines[at]);
            cues.Add(new SubtitleCue(Time(m, 1), Time(m, 5), Clean(string.Join("\n", lines.Skip(at + 1)))));
        }
        return cues;
    }

    private static List<SubtitleCue> ParseVtt(string text)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in LineEnds().Replace(text, "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (lines.Count == 0 || lines[0].StartsWith("NOTE", StringComparison.Ordinal) || lines[0].StartsWith("STYLE", StringComparison.Ordinal) || lines[0].StartsWith("REGION", StringComparison.Ordinal)) continue;
            var at = lines.FindIndex(x => VttTimes().IsMatch(x));
            if (at < 0) continue;
            var m = VttTimes().Match(lines[at]);
            cues.Add(new SubtitleCue(VttTime(m, 1), VttTime(m, 5), Clean(string.Join("\n", lines.Skip(at + 1)))));
        }
        return cues;
    }

    private static List<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        int start = 1, end = 2, textAt = 9, fields = 10; var inEvents = false;
        foreach (var raw in LineEnds().Replace(text, "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inEvents && !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = line[7..].Split(',').Select(x => x.Trim()).ToList();
                fields = names.Count; start = names.FindIndex(x => x.Equals("Start", StringComparison.OrdinalIgnoreCase)); end = names.FindIndex(x => x.Equals("End", StringComparison.OrdinalIgnoreCase));
                textAt = names.FindIndex(x => x.Equals("Text", StringComparison.OrdinalIgnoreCase));
                if (start < 0 || end < 0 || textAt < 0) { start = 1; end = 2; textAt = fields - 1; }
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line[9..].Split(',', fields);
            if (parts.Length <= Math.Max(Math.Max(start, end), textAt)) continue;
            if (AssTime(parts[start]) is not { } from || AssTime(parts[end]) is not { } to) continue;
            cues.Add(new SubtitleCue(from, to, Clean(AssTags().Replace(parts[textAt], "").Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " "))));
        }
        return cues;
    }

    private static TimeSpan Time(Match m, int at) => new(0, Int(m, at), Int(m, at + 1), Int(m, at + 2), Millis(m.Groups[at + 3].Value));
    private static TimeSpan VttTime(Match m, int at) => new(0, m.Groups[at].Success ? Int(m, at) : 0, Int(m, at + 1), Int(m, at + 2), Millis(m.Groups[at + 3].Value));
    private static int Int(Match m, int at) => int.Parse(m.Groups[at].Value, CultureInfo.InvariantCulture);
    private static int Millis(string digits) => int.Parse(digits.PadRight(3, '0'), CultureInfo.InvariantCulture);
    private static TimeSpan? AssTime(string value) =>
        TimeSpan.TryParseExact(value.Trim(), [@"h\:mm\:ss\.ff", @"h\:mm\:ss\.fff", @"hh\:mm\:ss\.ff"], CultureInfo.InvariantCulture, out var time) ? time : null;
    private static string Clean(string text) => HtmlTags().Replace(text, "").Trim();

    /// <summary>The cues as an SRT file (UTF-8 without a mark, CRLF as players expect), numbered from 1 in time order.</summary>
    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var n = 0;
        foreach (var cue in cues.OrderBy(x => x.Start))
        {
            builder.Append(++n).Append("\r\n").Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.End)).Append("\r\n")
                .Append(cue.Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    private static string Stamp(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }
}
