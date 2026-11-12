using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>Season and episode read from a release name. <see cref="Absolute"/>: numbered without a season ("[Group] Show - 05").</summary>
public sealed record EpisodeNumber(int Season, int Episode, int? LastEpisode = null, bool Absolute = false)
{
    public string Label => $"S{Season:00}E{Episode:00}" + (LastEpisode is { } last && last > Episode ? $"-E{last:00}" : "");
}

/// <summary>An episode between two (« Show - 07.5 »): its number as written, and the season the name gives.</summary>
public sealed record HalfEpisodeNumber(int? Season, string Number)
{
    /// <summary>« S03 12.5 », or the number alone: its name among the show's extras.</summary>
    public string Label => Season is { } season ? $"S{season:00} {Number}" : Number;
}

/// <summary>Titles, years, seasons and episode numbers read from release and file names, and safe Windows names for them.</summary>
public static partial class ReleaseName
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".webm", ".mpg", ".mpeg", ".ogv", ".flv" };
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup" };
    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));
    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));

    // Release tags: the title always stops before the first one.
    private static readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase)
    {
        "4k", "8k", "uhd", "hdr", "hdr10", "hdr10plus", "dv", "dovi", "hlg", "sdr",
        "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux", "bd", "bdmv", "bdiso", "web-dl", "webdl", "webrip", "web", "hdtv", "pdtv", "hdrip",
        "dvdrip", "dvd", "dvdr", "dvd5", "dvd9", "dvdscr", "screener", "hdcam", "camrip", "hdts", "telesync", "tvrip", "vhsrip",
        "xvid", "divx", "hevc", "avc", "av1", "vp9", "mpeg2", "hi10", "hi10p",
        "aac", "ac3", "eac3", "ddp", "dts", "dts-hd", "dtshd", "dts-x", "dtsx", "truehd", "atmos", "flac", "opus", "mp3", "lpcm", "pcm",
        "dual-audio", "multi-audio", "proper", "repack", "rerip", "internal", "nf", "amzn", "dsnp", "hmax", "atvp", "pcok"
    };
    // Tags that are also words of titles ("Charlottes Web 2006", "Mr Hollands Opus 1995"): one ends a title only before
    // another tag or at the very end, never before a year, another word or an episode.
    private static readonly HashSet<string> TitleWords = new(StringComparer.OrdinalIgnoreCase) { "web", "opus" };
    // Edition or dub markers left at the end of a title ("Film EXTENDED", "Film TRUEFRENCH").
    private static readonly HashSet<string> TrailingTags = new(StringComparer.OrdinalIgnoreCase)
    { "extended", "unrated", "uncut", "remastered", "restored", "criterion", "imax", "multi", "multisub", "msubs", "subbed", "dubbed", "vostfr", "vost", "vff", "vfq", "vfi", "truefrench", "subfrench" };
    // What a release gives in parentheses that is not a name: languages and subtitles, platforms, notes.
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "vostfr", "vostf", "vost", "vosta", "vf", "vff", "vfq", "vfi", "vf2", "vo", "french", "truefrench", "subfrench", "multi", "multisub", "multisubs", "msubs",
        "sub", "subs", "subbed", "subtitles", "softsubs", "hardsub", "hardsubs", "dub", "dubbed", "dual", "audio", "raw", "raws", "eng", "english", "jpn", "jap", "japanese", "ad",
        "wkn", "wakanim", "adn", "cr", "crunchyroll", "funi", "funimation", "hidive", "netflix", "amazon", "prime", "disney", "hulu", "bili", "bilibili", "b-global", "abema", "iqiyi", "viki",
        "readnfo", "nfofix", "nfo", "read", "uncensored", "censored", "batch", "complete", "integrale", "intégrale"
    };
    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    { "extras", "extra", "featurettes", "featurette", "bonus", "bonuses", "special features", "behind the scenes", "deleted scenes", "interviews", "scenes", "shorts", "trailers", "other", "menus", "nc", "ncop", "nced", "creditless" };
    private static readonly HashSet<string> SpecialFolders = new(StringComparer.OrdinalIgnoreCase) { "specials", "special", "sp", "sps", "ova", "ovas", "oad", "oads" };
    // Full language names are recognised anywhere; ISO codes only at the end of a file name ("Film.en.forced.srt").
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["french"] = "fr", ["français"] = "fr", ["francais"] = "fr", ["vostfr"] = "fr", ["japanese"] = "ja", ["spanish"] = "es", ["español"] = "es", ["espanol"] = "es",
        ["castellano"] = "es", ["latino"] = "es", ["portuguese"] = "pt", ["brazilian"] = "pt-BR", ["german"] = "de", ["deutsch"] = "de", ["italian"] = "it", ["italiano"] = "it",
        ["russian"] = "ru", ["chinese"] = "zh", ["korean"] = "ko", ["arabic"] = "ar", ["dutch"] = "nl", ["polish"] = "pl", ["swedish"] = "sv", ["norwegian"] = "no", ["danish"] = "da",
        ["finnish"] = "fi", ["turkish"] = "tr", ["greek"] = "el", ["hebrew"] = "he", ["czech"] = "cs", ["hungarian"] = "hu", ["romanian"] = "ro", ["indonesian"] = "id",
        ["vietnamese"] = "vi", ["thai"] = "th", ["ukrainian"] = "uk", ["hindi"] = "hi"
    };
    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en", ["eng"] = "en", ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["vf"] = "fr", ["vff"] = "fr", ["ja"] = "ja", ["jp"] = "ja", ["jpn"] = "ja", ["es"] = "es", ["spa"] = "es",
        ["pt"] = "pt", ["por"] = "pt", ["pob"] = "pt-BR", ["ptbr"] = "pt-BR", ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["it"] = "it", ["ita"] = "it", ["ru"] = "ru", ["rus"] = "ru",
        ["zh"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["chs"] = "zh", ["cht"] = "zh", ["ko"] = "ko", ["kor"] = "ko", ["ar"] = "ar", ["ara"] = "ar", ["nl"] = "nl", ["dut"] = "nl", ["nld"] = "nl",
        ["pl"] = "pl", ["pol"] = "pl", ["sv"] = "sv", ["swe"] = "sv", ["no"] = "no", ["nor"] = "no", ["da"] = "da", ["dan"] = "da", ["fi"] = "fi", ["fin"] = "fi", ["tr"] = "tr", ["tur"] = "tr",
        ["el"] = "el", ["gre"] = "el", ["ell"] = "el", ["he"] = "he", ["heb"] = "he", ["cs"] = "cs", ["cze"] = "cs", ["ces"] = "cs", ["hu"] = "hu", ["hun"] = "hu", ["ro"] = "ro", ["rum"] = "ro",
        ["ron"] = "ro", ["id"] = "id", ["ind"] = "id", ["vi"] = "vi", ["vie"] = "vi", ["th"] = "th", ["tha"] = "th", ["uk"] = "uk", ["ukr"] = "uk"
    };

    private const string Domains = "com|org|net|mx|to|se|ws|cc|tv|me|info|io|xyz|lol|nz|ch|la|in|is|am|li|pw|site|club|top|rs|ru|eu|co|uk|fr|ag|sx|st|cx|onl|gg|re|vip|biz|pro|link|live|world|zone|one";
    private const string PlainDomains = "com|org|net|mx|se|ws|cc|tv|info|io|xyz|lol|nz|ch|la|li|pw|site|club|top|rs|ru|eu|co|uk|fr|ag|sx|st|cx|onl|gg|re|vip|biz";
    // "www.Site.com - Title", "Site.org   -   Title", "[ www.site.com ] Title": never a bare "Title.Me" without a separator.
    [GeneratedRegex(@"^\s*(?:\[\s*(?:www\.)?[\w-]+(?:\.[\w-]+)*\.(?:" + Domains + @")\s*\]\s*[-–—:|]*|www\.[\w-]+(?:\.[\w-]+)*\.(?:" + Domains + @")(?![\w-])\s*[-–—:|]*|[\w-]+(?:\.[\w-]+)*\.(?:" + PlainDomains + @")\s*[-–—:|]+)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SitePrefix();
    [GeneratedRegex(@"^\s*\[(?<g>[^\[\]]{1,64})\]\s*")] private static partial Regex LeadingGroup();
    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}|【[^】]*】")] private static partial Regex SquareOrCurly();
    [GeneratedRegex(@"\((?<c>[^()]*)\)")] private static partial Regex Parenthesized();
    [GeneratedRegex(@"(?<![0-9])(?<y>(?:19|20)[0-9]{2})(?![0-9])")] private static partial Regex Year();
    [GeneratedRegex(@"(?<y>(?:19|20)[0-9]{2})\s*$")] private static partial Regex TrailingYear();
    [GeneratedRegex(@"\s*\((?:19|20)[0-9]{2}\)\s*$")] private static partial Regex FolderYearSuffix();
    [GeneratedRegex(@"\((?<y>(?:19|20)[0-9]{2})\)\s*$")] private static partial Regex FolderYear();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^(?<a>\d{1,4})\s*[-~–]\s*(?<b>\d{1,4})$")] private static partial Regex EpisodeRange();
    [GeneratedRegex(@"[\s._\-\[\]()]+")] private static partial Regex TokenSeparators();
    [GeneratedRegex(@"^(?:\d{3,4}[pi]|[hx]\.?26[45]|(?:dd|ddp|eac3|ac3|aac|dts)[\d.+]*|\d{1,2}-?bit)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
    [GeneratedRegex(@"^[0-9A-Fa-f]{8}$")] private static partial Regex Checksum();
    [GeneratedRegex(@"(?<![a-z0-9])S(?<s>\d{1,2})[\s._-]*E(?<e>\d{1,4})(?:[\s._-]*E(?<e2>\d{1,4})|-(?<e2>\d{1,4})(?![0-9]))?(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisode();
    [GeneratedRegex(@"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CrossEpisode();
    [GeneratedRegex(@"\b(?:Season|Saison|Temporada|Staffel)\s*(?<s>\d{1,2})\s*[-,.]?\s*(?:Episode|Épisode|Episodio|Folge|Ep\.?)\s*(?<e>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordsEpisode();
    [GeneratedRegex(@"(?:^|\s)[-–]\s*(?:Ep\.?\s*|Episode\s*|E|#)?(?<e>\d{1,4})(?:v\d{1,2})?(?:\s*[-~]\s*(?<e2>\d{1,4})(?:v\d{1,2})?)?(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteEpisode();
    [GeneratedRegex(@"(?<![a-z])(?:Episode|Épisode|Ep\.?)\s*(?<e>\d{1,4})\b(?![.,]\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeWord();
    // « Show - 07.5 », « Show S3 - Episode 12.5 »: an episode between two (a recap, a special), numbered as none of them.
    [GeneratedRegex(@"(?:(?:^|\s)[-–]\s*(?:Ep\.?\s*|Episode\s*|Épisode\s*|E|#)?|(?<![a-z])(?:Episode|Épisode|Ep\.?)\s*)(?<e>\d{1,4})[.,]5(?:v\d{1,2})?(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HalfNumber();
    // "01 - Title", "01. Title", "E01 - Title", "01-02 - Title": the number a file starts with.
    [GeneratedRegex(@"^(?:E|Ep\.?\s*)?(?<e>\d{1,3})(?![.,]\d)(?:\s*[-~]\s*E?(?<e2>\d{1,3}))?(?=$|[\s._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();
    [GeneratedRegex(@"(?<![a-z0-9])S(?<s>\d{1,2})(?![0-9a-z])(?![\s._-]*E\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonMarker();
    [GeneratedRegex(@"\b(?:Season|Saison|Temporada|Staffel)\s*(?<s>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWord();
    [GeneratedRegex(@"\b(?<s>\d{1,2})(?:st|nd|rd|th)\s+Season\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalSeason();
    [GeneratedRegex(@"\b(?:Seasons?|Saisons?|S)\s*\d{1,2}\s*[-~]\s*S?\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonRange();
    [GeneratedRegex(@"\b(?:Complete(?:\s+Series)?|Int[ée]grale|Batch)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompleteMarker();
    // What "Complete" and "Intégrale" may come before and still mean every episode or every film: "Complete Collection",
    // "COMPLETE S01-S05", "Integrale 2005-2009", "Complete Movie Series", "Complete 8-Film Collection", "Complete Trilogy",
    // "Integrale Livres I a VI".
    [GeneratedRegex(@"^(?:series|seasons?|saisons?|s\d{1,2}(?:[-~]s?\d{1,2})?|collection|saga|box|boxset|pack|movies?|films?|(?:tri|duo|di|tetra|quadri|penta|hexa|antho)log(?:y|ie)|livres?|\d{1,2}-?(?:films?|movies?)|\d{1,3}[-~]\d{1,3}|(?:19|20)\d\d(?:[-~](?:19|20)?\d\d)?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Whole();
    [GeneratedRegex(@"(?<![a-z0-9])(?:cd|dvd|disc|disk|part|pt)[\s._-]*(?<n>\d{1,2})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StackMarker();
    [GeneratedRegex(@"(?<![a-z0-9])sample(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleMarker();
    [GeneratedRegex(@"(?<![a-z0-9])(?:NC\s*(?<k>OP|ED)|(?:Creditless|Clean|Textless)\s+(?<k>Opening|Ending|OP|ED))\s*(?<n>\d{0,2})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Creditless();
    [GeneratedRegex(@"(?<![a-z0-9])(?<k>OP|ED)\s*(?<n>\d{1,2})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpeningEnding();
    [GeneratedRegex(@"^(?:Season|Saison|S)\s*0*(?<n>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonFolder();
    [GeneratedRegex(@"^(?:(?:part|partie|cour|vol(?:ume)?|chapter|chapitre|arc|ep(?:isode)?|e|s|season|saison)\s*\d{1,3}|\d{1,2}(?:st|nd|rd|th)\s+(?:season|cour|part))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartAlone();

    /// <summary>A release name without extension, site prefix, fansub group, bracketed tags and separators.</summary>
    public static string Readable(string name, out string? group)
    {
        var text = StripExtension(name.Trim());
        for (var i = 0; i < 3; i++) { var stripped = SitePrefix().Replace(text, ""); if (stripped == text) break; text = stripped; }
        group = null;
        var lead = LeadingGroup().Match(text);
        if (lead.Success) { group = lead.Groups["g"].Value.Trim(); text = text[lead.Length..]; }
        text = SquareOrCurly().Replace(text, " ");
        text = Parenthesized().Replace(text, m =>
        {
            var content = m.Groups["c"].Value.Trim();
            if (content.Length == 4 && Year().IsMatch(content)) return " " + content + " ";
            // The episodes of a batch (« [SubsPlease] Show (01-28) (1080p) [Batch] »): not part of the title.
            if (EpisodeRange().Match(content) is { Success: true } range && !(Year().IsMatch(range.Groups["a"].Value) && Year().IsMatch(range.Groups["b"].Value))) return " ";
            return IsTagGroup(content) ? " " : " " + content + " ";
        });
        // Scene names use dots or underscores as spaces; a few dots in a spaced title ("Mr. Robot") are kept.
        var dots = text.Count(c => c == '.');
        if (dots > 0 && dots >= text.Count(c => c == ' ')) text = text.Replace('.', ' ');
        return Spaces().Replace(text.Replace('_', ' '), " ").Trim();
    }

    /// <summary>Film title and year: "L'argent.1983.Criterion.1080p…" gives ("L'argent", 1983).</summary>
    public static (string Title, int? Year) Movie(string name)
    {
        var text = Readable(name, out _);
        var limit = DateTime.UtcNow.Year + 1;
        Match? chosen = null;
        // The last plausible year wins: "2001 A Space Odyssey 1968", "Blade Runner 2049 2017".
        foreach (Match m in Year().Matches(text)) if (m.Index > 0 && int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) <= limit) chosen = m;
        if (chosen is not null && Tidy(CutAtTags(text[..chosen.Index], followed: true)) is { Length: > 0 } title)
            return (title, int.Parse(chosen.Groups["y"].Value, CultureInfo.InvariantCulture));
        return (Tidy(CutAtTags(text)), null);
    }

    /// <summary>Series title and optional year, before the first season or episode marker.</summary>
    public static (string Title, int? Year) Show(string name)
    {
        var text = Readable(name, out _);
        var cut = text.Length;
        foreach (var marker in new[] { SeasonEpisode(), CrossEpisode(), WordsEpisode(), AbsoluteEpisode(), HalfNumber(), SeasonMarker(), SeasonWord(), OrdinalSeason(), SeasonRange(), EpisodeWord() })
        {
            var m = marker.Match(text);
            if (m.Success && m.Index < cut) cut = m.Index;
        }
        if (PackMarker(text) is { } pack && pack.Index < cut) cut = pack.Index;
        var head = CutAtTags(text[..cut], followed: cut < text.Length);
        var year = TrailingYear().Match(head);
        if (year.Success && year.Index > 0 && Tidy(head[..year.Index]) is { Length: > 0 } title)
            return (title, int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture));
        return (Tidy(head), null);
    }

    /// <summary>Episode numbering of a file. Numbers without a season ("Show - 05") are only read when <paramref name="allowAbsolute"/>.</summary>
    public static EpisodeNumber? Episode(string name, bool allowAbsolute)
    {
        var text = Readable(name, out var group);
        var m = SeasonEpisode().Match(text);
        if (m.Success) return new(Number(m, "s"), Number(m, "e"), OptionalNumber(m, "e2"));
        m = WordsEpisode().Match(text);
        if (m.Success) return new(Number(m, "s"), Number(m, "e"));
        m = CrossEpisode().Match(text);
        if (m.Success) return new(Number(m, "s"), Number(m, "e"));
        if (!allowAbsolute && group is null) return null;
        m = AbsoluteEpisode().Match(text);
        if (!m.Success) m = EpisodeWord().Match(text);
        if (!m.Success) return null;
        var episode = Number(m, "e");
        // "Show - 2014" is a year, not episode 2014.
        if (m.Groups["e"].Length == 4 && Year().IsMatch(m.Groups["e"].Value)) return null;
        var season = Season(text[..m.Index]);
        return new(season ?? 1, episode, OptionalNumber(m, "e2"), season is null);
    }

    /// <summary>
    /// An episode between two (« Show - 07.5 », « Show S3 - Episode 12.5 »: a recap or a special), numbered as none of the
    /// show's: its number as the name writes it and the season the name gives; null for any other name. Read as
    /// <see cref="Episode"/> reads a number without a season.
    /// </summary>
    public static HalfEpisodeNumber? HalfEpisode(string name, bool allowAbsolute)
    {
        var text = Readable(name, out var group);
        if (SeasonEpisode().IsMatch(text) || WordsEpisode().IsMatch(text) || CrossEpisode().IsMatch(text) || !allowAbsolute && group is null) return null;
        var m = HalfNumber().Match(text);
        return m.Success ? new(Season(text[..m.Index]), m.Groups["e"].Value + ".5") : null;
    }

    /// <summary>
    /// The number a file starts with (« 01 - Pilot », « 01. Pilot », « E01 - Pilot »), or that follows the show's title
    /// (« Show 01 Pilot »), as an episode without its season: how many season packs name their files. The digits of a
    /// title are not one (« 30 Rock - Pilot »).
    /// </summary>
    public static EpisodeNumber? LeadingEpisode(string fileName, string show)
    {
        var text = Readable(fileName, out _);
        var title = Key(show);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; title.Length > 0 && i <= words.Length; i++)
        {
            if (Key(string.Join(' ', words[..i])) != title) continue;
            text = string.Join(' ', words[i..]);
            break;
        }
        var m = LeadingNumber().Match(text);
        return m.Success ? new(1, Number(m, "e"), OptionalNumber(m, "e2"), Absolute: true) : null;
    }

    /// <summary>Season of a pack ("Show - S01", "Season 2", "2nd Season"); null when absent or ambiguous (multi-season packs).</summary>
    public static int? Season(string name)
    {
        var text = Readable(name, out _);
        if (SeasonRange().IsMatch(text)) return null;
        var values = SeasonMarker().Matches(text).Concat(SeasonWord().Matches(text)).Concat(OrdinalSeason().Matches(text))
            .Select(x => Number(x, "s")).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }
    /// <summary>
    /// Other names a release gives its show in parentheses, as fansub and anime groups do: "[ToonsHub] JoJos Bizarre
    /// Adventure S06E05 1080p … (JoJo no Kimyou na Bouken: Steel Ball Run, Dual-Audio, Multi-Subs)" gives "JoJo no Kimyou
    /// na Bouken: Steel Ball Run". What follows " - " (a part or a stage) is left out; tags and years are not names.
    /// The first element of the parentheses that names something: "(VOSTFR, Battle Spirits Re Zekkai no Kuu)" gives the
    /// second one, "(WKN)" (Wakanim) and "(Season 2)" none: two shows of one platform never meet under its name.
    /// </summary>
    public static IReadOnlyList<string> AlternateTitles(string name)
    {
        var titles = new List<string>();
        foreach (Match m in Parenthesized().Matches(StripExtension(name.Trim())))
            foreach (var element in m.Groups["c"].Value.Split(','))
            {
                var first = element;
                var dash = first.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0) first = first[..dash];
                first = first.Trim();
                if (first.Length < 3 || !first.Any(char.IsLetter) || Year().IsMatch(first) && first.Length <= 6 || IsTagGroup(first) || NamesNothing(first)) continue;
                // « Kikansha no Mahou wa Tokubetsu desu 2nd Season »: the show, as its title would be read.
                if (Show(first).Title is { Length: > 0 } title && !NamesNothing(title) && !titles.Contains(title, StringComparer.OrdinalIgnoreCase)) titles.Add(title);
                break;
            }
        return titles;
    }

    /// <summary>
    /// Words of a release that name nothing: a language or subtitles (« VOSTFR », « Multi Subs »), a platform (« WKN »,
    /// « ADN »), a note (« READNFO »), a season or a part alone (« Season 2 », « Part 2 »), or one word in capitals.
    /// </summary>
    public static bool NamesNothing(string text)
    {
        var value = text.Trim();
        if (value.Length == 0 || PartAlone().IsMatch(value)) return true;
        var words = value.Split([' ', '.', '_', '+', '/', '&'], StringSplitOptions.RemoveEmptyEntries);
        if (words.All(w => IsTag(w) || NotNames.Contains(w.Trim('-')) || TrailingTags.Contains(w.Trim('-')) || w.Split('-', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts && parts.All(x => NotNames.Contains(x) || TrailingTags.Contains(x) || IsTag(x))))
            return true;
        return words is [var word] && word.Length <= 8 && word.All(char.IsAsciiLetter) && word == word.ToUpperInvariant();
    }
    public static bool IsCompletePack(string name) => PackMarker(Readable(name, out _)) is not null;
    public static bool HasGroupPrefix(string name) { Readable(name, out var group); return group is not null; }
    /// <summary>Sample clip: "sample" in the file name or one of its folders (the torrent's own top folder is not considered).</summary>
    public static bool IsSample(string relativePath)
    {
        var parts = relativePath.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length > 1 ? parts[1..] : parts).Any(x => SampleMarker().IsMatch(x));
    }
    public static int? StackPart(string fileName) { var m = StackMarker().Match(Readable(fileName, out _)); return m.Success ? Number(m, "n") : null; }

    /// <summary>Folder of bonus material inside the torrent (Extras, Featurettes, NC…), below its top folder.</summary>
    public static bool InExtrasFolder(string relativePath) => Folders(relativePath).Any(ExtraFolders.Contains);
    public static bool InSpecialsFolder(string relativePath) => Folders(relativePath).Any(SpecialFolders.Contains);
    private static IEnumerable<string> Folders(string relativePath)
    {
        var parts = relativePath.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? [] : parts[1..^1].Select(x => x.Trim());
    }

    /// <summary>"NCOP01", "NCED02" for creditless openings and endings; <paramref name="bare"/> also accepts "OP1" / "ED2".</summary>
    public static string? CreditlessLabel(string fileName, bool bare)
    {
        var text = Readable(fileName, out _);
        var m = Creditless().Match(text);
        if (!m.Success && bare) m = OpeningEnding().Match(text);
        if (!m.Success) return null;
        var kind = m.Groups["k"].Value.ToUpperInvariant() is "OP" or "OPENING" ? "NCOP" : "NCED";
        return kind + (m.Groups["n"].Length > 0 ? Number(m, "n").ToString("00", CultureInfo.InvariantCulture) : "");
    }

    /// <summary>Jellyfin subtitle suffix: ".fr", ".en.forced", ".ja.sdh" or "".</summary>
    public static string SubtitleTags(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var tokens = TokenSeparators().Split(name).Where(x => x.Length > 0).ToList();
        string? language = null; var forced = false; var sdh = false;
        for (var i = tokens.Count - 1; i >= Math.Max(0, tokens.Count - 4); i--)
        {
            var token = tokens[i].ToLowerInvariant();
            if (token is "forced" or "force" or "forcé" or "forces") { forced = true; continue; }
            if (token is "sdh" or "cc" or "hi" or "hoh") { sdh = true; continue; }
            if (LanguageCodes.TryGetValue(token, out var code) || LanguageNames.TryGetValue(token, out code)) { language ??= code; continue; }
            break;
        }
        language ??= tokens.Select(x => LanguageNames.GetValueOrDefault(x)).FirstOrDefault(x => x is not null)
            ?? LanguageNames.GetValueOrDefault(Path.GetFileName(Path.GetDirectoryName(relativePath) ?? "") ?? "");
        return (language is null ? "" : "." + language) + (forced ? ".forced" : "") + (sdh ? ".sdh" : "");
    }

    /// <summary>Matching key for titles and folders: "JoJo's Bizarre Adventure (2012)" and "JoJos.Bizarre.Adventure" meet.</summary>
    public static string Key(string title)
    {
        var text = FolderYearSuffix().Replace(title, "").Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or '`' or '´') continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Spaces().Replace(builder.ToString(), " ").Trim();
    }
    public static int? FolderYearOf(string folder) { var m = FolderYear().Match(folder); return m.Success ? Number(m, "y") : null; }
    public static int? SeasonOfFolder(string folder)
    {
        if (SpecialFolders.Contains(folder.Trim())) return 0;
        var m = SeasonFolder().Match(folder.Trim());
        return m.Success ? Number(m, "n") : null;
    }

    /// <summary>A name Windows and Jellyfin both accept: no reserved characters or device names, no trailing dots, 120 characters at most.</summary>
    public static string SafeName(string name)
    {
        var text = name.Replace(": ", " - ").Replace(':', '-');
        var builder = new StringBuilder(text.Length);
        foreach (var c in text) builder.Append(c < 32 || "<>\"/\\|?*".IndexOf(c) >= 0 ? ' ' : c);
        var safe = Spaces().Replace(builder.ToString(), " ").Trim().TrimEnd('.', ' ');
        if (safe.Length > 120)
        {
            var cut = 120;
            if (char.IsHighSurrogate(safe[cut - 1])) cut--;
            safe = safe[..cut].TrimEnd('.', ' ');
        }
        var stem = safe.Split('.')[0].Trim().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) safe += "_";
        return safe.Length == 0 ? L("Sans titre", "Untitled") : safe;
    }

    private static string StripExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return VideoExtensions.Contains(extension) || SubtitleExtensions.Contains(extension) || extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase) ? name[..^extension.Length] : name;
    }
    private static bool IsTag(string token)
    {
        var value = token.Trim('-', '.', ',', '+', '(', ')', '[', ']').ToLowerInvariant();
        if (value.Length == 0) return false;
        if (Tags.Contains(value) || TagPattern().IsMatch(value)) return true;
        var dash = value.IndexOf('-');
        return dash > 0 && (Tags.Contains(value[..dash]) || TagPattern().IsMatch(value[..dash]));
    }
    private static bool IsTagGroup(string content) => Checksum().IsMatch(content) || content.Split([' ', ',', '.', '_'], StringSplitOptions.RemoveEmptyEntries).Any(IsTag);
    // The title before the first tag; <paramref name="followed"/>: a year or an episode comes after this text.
    private static string CutAtTags(string text, bool followed = false)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < tokens.Length; i++) if (EndsTitle(tokens, i, followed)) return string.Join(' ', tokens[..i]);
        return text;
    }
    private static string Bare(string token) => token.Trim('-', '.', ',', '+', '(', ')', '[', ']');
    // "Batch-GRP" is "Batch"; "WEB-DL" stays itself.
    private static string Word(string token) { var t = Bare(token); return t.IndexOf('-') is > 0 and var dash && !Tags.Contains(t) ? t[..dash] : t; }
    private static bool IsPackWord(string token) => Word(token).ToLowerInvariant() is "complete" or "integrale" or "intégrale" or "batch";
    // A tag, an edition or a language: what never names a title ("VOSTFR", "MULTi"), "Complete" and "Batch" aside.
    private static bool Tagged(string token) => IsTag(token) || (TrailingTags.Contains(Bare(token)) || NotNames.Contains(Bare(token))) && !IsPackWord(token);
    // Whether tokens[i] ends a title: a tag, except a word of titles before another word, or last before a year or an episode.
    private static bool EndsTitle(string[] tokens, int i, bool followed)
    {
        if (!IsTag(tokens[i])) return false;
        if (!TitleWords.Contains(Word(tokens[i]))) return true;
        var next = i + 1;
        while (next < tokens.Length && !tokens[next].Any(char.IsLetterOrDigit)) next++;
        if (next >= tokens.Length) return !followed;
        return TitleWords.Contains(Word(tokens[next])) ? EndsTitle(tokens, next, followed) : Tagged(tokens[next]);
    }
    /// <summary>
    /// "Complete", "Intégrale" or "Batch" when it means every episode: before nothing, a tag or a language, and for the first
    /// two before what makes every episode or years ("Complete Collection", "COMPLETE S01-S05", "Integrale 2005-2009"); never a
    /// word of the title ("A Complete Unknown 2024", "Star Wars The Bad Batch S03E15", "The Bad Batch (2021) S01E01").
    /// </summary>
    private static Match? PackMarker(string text)
    {
        foreach (Match m in CompleteMarker().Matches(text))
        {
            if (m.Value.Contains(' ')) return m;
            var rest = text[(m.Index + m.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Any(char.IsLetterOrDigit)).ToArray();
            // "Show.Complete-GRP": the group of the release.
            if (rest.Length == 0 || rest.Length == 1 && rest[0].StartsWith('-')) return m;
            var batch = m.Value.Equals("batch", StringComparison.OrdinalIgnoreCase);
            if (!IsPackWord(rest[0]) && Tagged(rest[0]) || !batch && Whole().IsMatch(Bare(rest[0]))) return m;
        }
        return null;
    }
    private static string Tidy(string text)
    {
        var tokens = Spaces().Replace(text, " ").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && TrailingTags.Contains(tokens[^1].Trim('-', '.'))) tokens.RemoveAt(tokens.Count - 1);
        var title = string.Join(' ', tokens).Trim(' ', '-', '–', '—', '.', '_', ',', ':', ';', '+', '~', '(', ')', '[', ']', '|');
        return title.Length > 0 && title == title.ToLowerInvariant() && title.Any(char.IsLetter)
            ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(title) : title;
    }
    private static int Number(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
    private static int? OptionalNumber(Match m, string group) => m.Groups[group].Success && m.Groups[group].Length > 0 ? Number(m, group) : null;
}
