using System.Globalization;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>Texts of the player bar and its menus, in Mira's language (<see cref="Lang"/>) whatever the Windows number format.</summary>
public static class PlayerText
{
    // English and French names of ISO 639 codes, three-letter (both forms) and two-letter.
    private static readonly Dictionary<string, (string French, string English)> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpn"] = ("Japonais", "Japanese"), ["ja"] = ("Japonais", "Japanese"), ["fra"] = ("Français", "French"), ["fre"] = ("Français", "French"), ["fr"] = ("Français", "French"),
        ["eng"] = ("Anglais", "English"), ["en"] = ("Anglais", "English"), ["deu"] = ("Allemand", "German"), ["ger"] = ("Allemand", "German"), ["de"] = ("Allemand", "German"),
        ["spa"] = ("Espagnol", "Spanish"), ["es"] = ("Espagnol", "Spanish"), ["ita"] = ("Italien", "Italian"), ["it"] = ("Italien", "Italian"), ["por"] = ("Portugais", "Portuguese"), ["pt"] = ("Portugais", "Portuguese"),
        ["rus"] = ("Russe", "Russian"), ["ru"] = ("Russe", "Russian"), ["kor"] = ("Coréen", "Korean"), ["ko"] = ("Coréen", "Korean"), ["zho"] = ("Chinois", "Chinese"), ["chi"] = ("Chinois", "Chinese"), ["zh"] = ("Chinois", "Chinese"),
        ["ara"] = ("Arabe", "Arabic"), ["ar"] = ("Arabe", "Arabic"), ["hin"] = ("Hindi", "Hindi"), ["hi"] = ("Hindi", "Hindi"), ["pol"] = ("Polonais", "Polish"), ["pl"] = ("Polonais", "Polish"),
        ["nld"] = ("Néerlandais", "Dutch"), ["dut"] = ("Néerlandais", "Dutch"), ["nl"] = ("Néerlandais", "Dutch"), ["swe"] = ("Suédois", "Swedish"), ["sv"] = ("Suédois", "Swedish"),
        ["nor"] = ("Norvégien", "Norwegian"), ["nob"] = ("Norvégien", "Norwegian"), ["no"] = ("Norvégien", "Norwegian"), ["nb"] = ("Norvégien", "Norwegian"), ["dan"] = ("Danois", "Danish"), ["da"] = ("Danois", "Danish"),
        ["fin"] = ("Finnois", "Finnish"), ["fi"] = ("Finnois", "Finnish"), ["tur"] = ("Turc", "Turkish"), ["tr"] = ("Turc", "Turkish"), ["heb"] = ("Hébreu", "Hebrew"), ["he"] = ("Hébreu", "Hebrew"),
        ["tha"] = ("Thaï", "Thai"), ["th"] = ("Thaï", "Thai"), ["vie"] = ("Vietnamien", "Vietnamese"), ["vi"] = ("Vietnamien", "Vietnamese"), ["ind"] = ("Indonésien", "Indonesian"), ["id"] = ("Indonésien", "Indonesian"),
        ["ukr"] = ("Ukrainien", "Ukrainian"), ["uk"] = ("Ukrainien", "Ukrainian"), ["ces"] = ("Tchèque", "Czech"), ["cze"] = ("Tchèque", "Czech"), ["cs"] = ("Tchèque", "Czech"),
        ["hun"] = ("Hongrois", "Hungarian"), ["hu"] = ("Hongrois", "Hungarian"), ["ell"] = ("Grec", "Greek"), ["gre"] = ("Grec", "Greek"), ["el"] = ("Grec", "Greek"),
        ["ron"] = ("Roumain", "Romanian"), ["rum"] = ("Roumain", "Romanian"), ["ro"] = ("Roumain", "Romanian"), ["cat"] = ("Catalan", "Catalan"), ["ca"] = ("Catalan", "Catalan"),
        ["msa"] = ("Malais", "Malay"), ["may"] = ("Malais", "Malay"), ["ms"] = ("Malais", "Malay"), ["fil"] = ("Filipino", "Filipino"), ["tgl"] = ("Tagalog", "Tagalog")
    };

    /// <summary>Menu text of a track: its title, else its language ("Japanese"), else "Track 2". Details: the language
    /// when a title is shown, the codec, the channel layout of audio, and whether subtitles are forced or external.</summary>
    public static (string Label, string Details) Track(string id, string? title, string? language, string? codec, int channels = 0, bool forced = false, bool external = false)
    {
        var name = Language(language);
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        var label = hasTitle ? title!.Trim() : name ?? L($"Piste {id}", $"Track {id}");
        var details = new List<string>();
        if (hasTitle && name is not null && !label.Contains(name, StringComparison.CurrentCultureIgnoreCase)) details.Add(name);
        if (Codec(codec) is { } format) details.Add(format);
        if (channels > 0) details.Add(channels switch { 1 => "mono", 2 => L("stéréo", "stereo"), 6 => "5.1", 8 => "7.1", _ => L($"{channels} canaux", $"{channels} channels") });
        if (forced) details.Add(L("forcés", "forced"));
        if (external) details.Add(L("fichier externe", "external file"));
        return (Capitalize(label), string.Join(" · ", details));
    }
    /// <summary>Name of a bonus video's kind (Jellyfin's ExtraType, as Mira web); null for a video of an Extras folder.</summary>
    public static string? ExtraKind(string? type) => type switch
    {
        "BehindTheScenes" => L("Coulisses", "Behind the scenes"),
        "DeletedScene" => L("Scène coupée", "Deleted scene"),
        "Featurette" => L("Making-of", "Featurette"),
        "Interview" => "Interview",
        "Scene" => L("Scène", "Scene"),
        "Short" => L("Court métrage", "Short"),
        "Clip" or "Sample" => L("Extrait", "Clip"),
        "Trailer" => L("Bande-annonce", "Trailer"),
        _ => null
    };
    /// <summary>Name of an ISO 639 code in Mira's language ("jpn", "fre", "pt-BR"); null when unknown or undetermined.</summary>
    public static string? Language(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var main = code.Trim().Split('-', '_')[0];
        if (main is "und" or "zxx" or "mis" or "mul") return null;
        return Languages.TryGetValue(main, out var name) ? L(name.French, name.English) : null;
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
        "mov_text" => L("Texte", "Text"),
        "h264" => "H.264",
        "hevc" or "h265" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        var other => other.ToUpperInvariant()
    };
    /// <summary>Playback speed as shown in the menu: "0,75×" (French), "0.75×", "1×".</summary>
    public static string Speed(double speed) => speed.ToString("0.##", Culture) + "×";
    /// <summary>Subtitle offset: "0,0 s", "+0,5 s", "−1,5 s" (French), "+0.5 s".</summary>
    public static string Delay(double seconds) => (seconds > 0 ? "+" : seconds < 0 ? "−" : "") + Math.Abs(seconds).ToString("0.0", Culture) + " s";
    private static string Capitalize(string text) => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpper(text[0], Culture) + text[1..] : text;
    // "Episode 5", "Épisode 5", "Ep. 5", "E05" or "5": the number is already shown, the name adds nothing.
    private static readonly Regex GenericName = new(@"^\s*(?:(?:e|ep|eps|episode|épisode)\.?\s*)?\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Line under the title. Episodes: "Episode 3 / 12 · Name", preceded by the season when the series has several
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
        if (season == 0) parts.Add(L("Hors-série", "Specials"));
        else if (season is > 0 && (seasons > 1 || season > 1)) parts.Add(L($"Saison {season}", $"Season {season}"));
        var episode = number is null ? L("Épisode", "Episode") : L($"Épisode {number}", $"Episode {number}");
        // A partial list must not claim "Episode 30 / 12".
        if (number is not null && sameSeason.Count >= number) episode += $" / {sameSeason.Count}";
        parts.Add(episode);
        var name = item.Name.Trim();
        if (name.Length > 0 && !GenericName.IsMatch(name) && !string.Equals(name, item.SeriesName, StringComparison.CurrentCultureIgnoreCase)) parts.Add(name);
        return string.Join(" · ", parts);
    }
}
