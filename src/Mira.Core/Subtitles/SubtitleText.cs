using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core.Subtitles;

/// <summary>A line of subtitles: when it shows, when it goes, what it says (override tags removed, lines joined by « \n »).</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Subtitle files as text: the bytes decoded (UTF-8, UTF-16 with a mark, else the old encoding of the file's language:
/// Windows-1252 for French), the format told from the content, the cues read from SRT, WebVTT and ASS/SSA, and written
/// back as SRT.
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

    /// <summary>
    /// The file as text: by its byte order mark, else UTF-8 when every byte sequence is valid, else in the encoding that
    /// Windows wrote files of <paramref name="language"/> in (ISO 639 code, the language asked of the source): Windows-1251
    /// for Russian, Shift-JIS for Japanese, GBK or Big5 for Chinese… and Windows-1252, the encoding of old French files,
    /// for the languages of Western Europe or when none is given.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string? language = null)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }
        string? read = null;
        var common = -1;
        foreach (var page in OldCodePages(language))
        {
            if (CodePagesEncodingProvider.Instance.GetEncoding(page) is not { } encoding) continue;
            var text = encoding.GetString(bytes);
            // Chinese: GBK (Simplified) or Big5 (Traditional), the one whose reading has the words every dialogue has.
            var count = 0;
            foreach (var c in text) if (CommonHan.Contains(c)) count++;
            if (count > common) (read, common) = (text, count);
        }
        if (read is not null) return read;
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] is >= 0x80 and <= 0x9F ? Cp1252High[bytes[i] - 0x80] : (char)bytes[i];
        return new string(chars);
    }

    // The most frequent characters of Chinese dialogue, Simplified and Traditional.
    private const string CommonHan = "的我你是不了一在他有什要就好去到那吧这這们們个個说說会會没沒来來吗嗎么麼";

    /// <summary>
    /// The code pages Windows wrote files of a language in (ISO 639-1 or 639-2, B or T, a region after it ignored), the
    /// likelier first; none for Windows-1252 (the languages of Western Europe).
    /// </summary>
    private static int[] OldCodePages(string? language) => language?.Trim().ToLowerInvariant().Split('-', '_')[0] switch
    {
        "pl" or "pol" or "cs" or "cze" or "ces" or "sk" or "slo" or "slk" or "hu" or "hun" or "ro" or "rum" or "ron" or "hr" or "hrv"
            or "sl" or "slv" or "sr" or "srp" or "bs" or "bos" or "sq" or "alb" or "sqi" => [1250],
        "ru" or "rus" or "uk" or "ukr" or "bg" or "bul" or "be" or "bel" or "mk" or "mac" or "mkd" => [1251],
        "el" or "gre" or "ell" => [1253],
        "tr" or "tur" => [1254],
        "he" or "heb" => [1255],
        "ar" or "ara" or "fa" or "per" or "fas" or "ur" or "urd" => [1256],
        "et" or "est" or "lv" or "lav" or "lt" or "lit" => [1257],
        "vi" or "vie" => [1258],
        "th" or "tha" => [874],
        "ja" or "jpn" => [932],
        "ko" or "kor" => [949],
        "zh" or "chi" or "zho" => [936, 950],
        _ => [],
    };

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

    /// <summary>
    /// SRT read line by line, as ffmpeg (Jellyfin, mpv) reads it: a line of times starts a cue, whatever came before it;
    /// the number just before it is the cue's, never the text of the one before; empty lines, or lines of spaces only,
    /// are left out. Blocks split at empty lines merged a cue with the next whenever the line between them held a space
    /// or was missing: the next one's number and times became text of the first.
    /// </summary>
    private static List<SubtitleCue> ParseSrt(string text)
    {
        var cues = new List<SubtitleCue>();
        Match? times = null;
        var lines = new List<string>();
        void Close()
        {
            if (times is { } m) cues.Add(new SubtitleCue(Time(m, 1), Time(m, 5), Clean(string.Join("\n", lines))));
            lines.Clear();
        }
        foreach (var raw in LineEnds().Replace(text, "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var m = SrtTimes().Match(line);
            if (!m.Success) { lines.Add(line); continue; }
            if (lines.Count > 0 && lines[^1].All(char.IsAsciiDigit)) lines.RemoveAt(lines.Count - 1);
            Close();
            times = m;
        }
        Close();
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

    /// <summary>
    /// A file once aligning moved its lines (<see cref="SubtitleAlign.Apply"/> gave <paramref name="moved"/>), and its
    /// format. An ASS/SSA file stays one: each event's times moved the same way, everything else as it was (styles, signs
    /// placed on the picture, karaoke, drawings: what mpv draws, and what an SRT has no way to say; as SRT, a drawing
    /// would show its commands as words). Any other file becomes an SRT of the moved lines.
    /// </summary>
    public static (string Text, string Format) Moved(string text, IReadOnlyList<SubtitleCue> moved, SubtitleSync sync) =>
        FormatOf(text) == "ass" ? (MovedAss(text, sync), "ass") : (ToSrt(moved), "srt");

    /// <summary>
    /// The events of an ASS/SSA file (Dialogue and Comment) with their start and end moved as <see cref="SubtitleAlign.Apply"/>
    /// moves a line: one pushed before the start is dropped, one that would start before it starts there. Every other
    /// line, and every other field of an event, as it was.
    /// </summary>
    private static string MovedAss(string text, SubtitleSync sync)
    {
        var lines = new List<string>();
        int start = 1, end = 2, fields = 10; var inEvents = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[')) inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            else if (inEvents && line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = line[7..].Split(',').Select(x => x.Trim()).ToList();
                (fields, start, end) = (names.Count, names.FindIndex(x => x.Equals("Start", StringComparison.OrdinalIgnoreCase)), names.FindIndex(x => x.Equals("End", StringComparison.OrdinalIgnoreCase)));
                if (start < 0 || end < 0) (start, end) = (1, 2);
            }
            else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || inEvents && line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
            {
                var colon = raw.IndexOf(':');
                var parts = raw[(colon + 1)..].Split(',', fields);
                if (parts.Length > Math.Max(start, end) && AssTime(parts[start]) is { } from && AssTime(parts[end]) is { } to)
                {
                    var (first, last) = (sync.At(from), sync.At(to));
                    if (last <= TimeSpan.Zero) continue;
                    (parts[start], parts[end]) = (AssStamp(first < TimeSpan.Zero ? TimeSpan.Zero : first), AssStamp(last));
                    lines.Add(raw[..(colon + 1)] + string.Join(',', parts));
                    continue;
                }
            }
            lines.Add(raw);
        }
        return string.Join('\n', lines);
    }

    /// <summary>A time as ASS writes it: hours, minutes, seconds and hundredths (« 0:01:02.35 »).</summary>
    private static string AssStamp(TimeSpan time)
    {
        var hundredths = (long)Math.Round(time.TotalMilliseconds / 10, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{hundredths / 360000}:{hundredths / 6000 % 60:00}:{hundredths / 100 % 60:00}.{hundredths % 100:00}");
    }
}
