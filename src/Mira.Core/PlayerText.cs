using System.Globalization;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>Texts of the player bar and its menus. Mira speaks French whatever the Windows number format.</summary>
public static class PlayerText
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpn"] = "Japonais", ["ja"] = "Japonais", ["fra"] = "Français", ["fre"] = "Français", ["fr"] = "Français",
        ["eng"] = "Anglais", ["en"] = "Anglais", ["deu"] = "Allemand", ["ger"] = "Allemand", ["de"] = "Allemand",
        ["spa"] = "Espagnol", ["es"] = "Espagnol", ["ita"] = "Italien", ["it"] = "Italien", ["por"] = "Portugais", ["pt"] = "Portugais",
        ["rus"] = "Russe", ["ru"] = "Russe", ["kor"] = "Coréen", ["ko"] = "Coréen", ["zho"] = "Chinois", ["chi"] = "Chinois", ["zh"] = "Chinois",
        ["ara"] = "Arabe", ["ar"] = "Arabe", ["hin"] = "Hindi", ["hi"] = "Hindi", ["pol"] = "Polonais", ["pl"] = "Polonais",
        ["nld"] = "Néerlandais", ["dut"] = "Néerlandais", ["nl"] = "Néerlandais", ["swe"] = "Suédois", ["sv"] = "Suédois",
        ["nor"] = "Norvégien", ["nob"] = "Norvégien", ["no"] = "Norvégien", ["nb"] = "Norvégien", ["dan"] = "Danois", ["da"] = "Danois",
        ["fin"] = "Finnois", ["fi"] = "Finnois", ["tur"] = "Turc", ["tr"] = "Turc", ["heb"] = "Hébreu", ["he"] = "Hébreu",
        ["tha"] = "Thaï", ["th"] = "Thaï", ["vie"] = "Vietnamien", ["vi"] = "Vietnamien", ["ind"] = "Indonésien", ["id"] = "Indonésien",
        ["ukr"] = "Ukrainien", ["uk"] = "Ukrainien", ["ces"] = "Tchèque", ["cze"] = "Tchèque", ["cs"] = "Tchèque",
        ["hun"] = "Hongrois", ["hu"] = "Hongrois", ["ell"] = "Grec", ["gre"] = "Grec", ["el"] = "Grec",
        ["ron"] = "Roumain", ["rum"] = "Roumain", ["ro"] = "Roumain", ["cat"] = "Catalan", ["ca"] = "Catalan",
        ["msa"] = "Malais", ["may"] = "Malais", ["ms"] = "Malais", ["fil"] = "Filipino", ["tgl"] = "Tagalog"
    };

    /// <summary>Menu text of a track: its title, else its language ("Japonais"), else "Piste 2". Details: the language
    /// when a title is shown, the codec, the channel layout of audio, and whether subtitles are forced or external.</summary>
    public static (string Label, string Details) Track(string id, string? title, string? language, string? codec, int channels = 0, bool forced = false, bool external = false)
    {
        var name = Language(language);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var label = hasTitle ? title!.Trim() : name ?? $"Piste {id}";
        var details = new List<string>();
        if (hasTitle && name is not null && !label.Contains(name, StringComparison.CurrentCultureIgnoreCase)) details.Add(name);
        if (Codec(codec) is { } format) details.Add(format);
        if (channels > 0) details.Add(channels switch { 1 => "mono", 2 => "stéréo", 6 => "5.1", 8 => "7.1", _ => $"{channels} canaux" });
        if (forced) details.Add("forcés");
        if (external) details.Add("fichier externe");
        return (Capitalize(label), string.Join(" · ", details));
    }
    /// <summary>French name of an ISO 639 code ("jpn", "fre", "pt-BR"); null when unknown or undetermined.</summary>
    public static string? Language(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var main = code.Trim().Split('-', '_')[0];
        if (main is "und" or "zxx" or "mis" or "mul") return null;
        return Languages.GetValueOrDefault(main);
    }
    public static string? Codec(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "aac" => "AAC",
        "ac3" => "Dolby Digital",
        "eac3" => "Dolby Digital Plus",
        "truehd" => "Dolby TrueHD",
        "dts" => "DTS",
        "flac" => "FLAC",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "mp3" => "MP3",
        var pcm when pcm.StartsWith("pcm", StringComparison.Ordinal) => "PCM",
        "subrip" or "srt" => "SRT",
        "ass" or "ssa" => "ASS",
        "hdmv_pgs_subtitle" or "pgs" => "PGS",
        "dvd_subtitle" or "vobsub" => "VobSub",
        "webvtt" => "WebVTT",
        "mov_text" => "Texte",
        "h264" => "H.264",
        "hevc" or "h265" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        var other => other.ToUpperInvariant()
    };
    /// <summary>Playback speed as shown in the menu: "0,75×", "1×".</summary>
    public static string Speed(double speed) => speed.ToString("0.##", French) + "×";
    /// <summary>Subtitle offset: "0,0 s", "+0,5 s", "−1,5 s".</summary>
    public static string Delay(double seconds) => (seconds > 0 ? "+" : seconds < 0 ? "−" : "") + Math.Abs(seconds).ToString("0.0", French) + " s";
    private static string Capitalize(string text) => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], French) + text[1..] : text;
    // "Episode 5", "Épisode 5", "Ep. 5", "E05" or "5": the number is already shown, the name adds nothing.
    private static readonly Regex GenericName = new(@"^\s*(?:(?:e|ep|eps|episode|épisode)\.?\s*)?\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Line under the title. Episodes: "Épisode 3 / 12 · Name", preceded by the season when the series has several
    /// (or when it is not the first). <paramref name="seriesEpisodes"/>, every episode of the series, gives the total
    /// once it is loaded. Other titles: year and duration.
    /// </summary>
    public static string Subtitle(MediaItem item, IReadOnlyList<MediaItem>? seriesEpisodes = null)
    {
        if (item.Type != "Episode") return item.Subtitle;
        var season = item.ParentIndexNumber;
        var episodes = seriesEpisodes ?? [];
        var sameSeason = episodes.Where(x => x.ParentIndexNumber == season).ToList();
        var position = sameSeason.FindIndex(x => x.Id == item.Id);
        var number = item.IndexNumber ?? (position >= 0 ? position + 1 : (int?)null);
        var seasons = episodes.Select(x => x.ParentIndexNumber ?? 0).Where(x => x > 0).Distinct().Count();
        var parts = new List<string>();
        if (season == 0) parts.Add("Hors-série");
        else if (season is > 0 && (seasons > 1 || season > 1)) parts.Add($"Saison {season}");
        var episode = number is null ? "Épisode" : $"Épisode {number}";
        // A partial list must not claim "Épisode 30 / 12".
        if (number is not null && sameSeason.Count >= number) episode += $" / {sameSeason.Count}";
        parts.Add(episode);
        var name = item.Name.Trim();
        if (name.Length > 0 && !GenericName.IsMatch(name) && !string.Equals(name, item.SeriesName, StringComparison.CurrentCultureIgnoreCase)) parts.Add(name);
        return string.Join(" · ", parts);
    }
}
