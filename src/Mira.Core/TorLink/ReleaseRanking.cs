using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mira.Core.TorLink;

/// <summary>
/// What a release name says: title, year, the other name given in parentheses, season and episode (<see cref="Absolute"/>:
/// numbered without a season; <see cref="Pack"/>: a whole season or more), resolution, source (remux, bluray, webdl,
/// webrip, hdrip, hdtv, dvd, cam), codec, HDR, languages (MULTI, VFF, VFQ, VF, VOSTFR), group (<see cref="Fansub"/>: given in
/// brackets in front, as fansub groups of anime do, « [Erai-raws] »).
/// </summary>
public sealed record ReleaseInfo(string Title, int? Year, string? Alternate, int? Season, int? Episode, int? LastEpisode, bool Absolute, bool Pack,
    int? Resolution, string? Source, string? Codec, bool TenBit, string? Hdr, IReadOnlyList<string> Languages, string? Group, bool Repack, bool Fansub = false);

/// <summary>A result of a search, what its name says and how good it is for us (<see cref="ReleaseRanking.Score"/>).</summary>
public sealed record RankedRelease(RelayHit Hit, ReleaseInfo Release, int Order, int Score);

/// <summary>
/// A film, an episode or a season pack: its name, « S06E05 » or « Saison 3 complète », the best release, the others;
/// <paramref name="Category"/>: the search of TorLink that found them (movies, tv, anime), null when the hits do not say.
/// </summary>
public sealed record ReleaseGroup(string Key, string Show, string Title, string Label, RankedRelease Best, IReadOnlyList<RankedRelease> Others, bool Pack, ReleaseInfo Release, string? Category = null);

/// <summary>
/// « Télécharger sur le PC serveur »: the release Mira puts forward for each title or episode, as Mira web does
/// (src/Mira.Web/js/releases.js; cases shared in tests/fixtures/releases.json): source first, 1080p before 4K for a small
/// server and a phone, MULTI then VF; seeders only rule out dead releases and break ties.
/// </summary>
public static partial class ReleaseRanking
{
    private static Dictionary<string, string> SourceLabels => new()
    { ["remux"] = "Remux", ["bluray"] = "BluRay", ["webdl"] = "WEB-DL", ["webrip"] = "WEBRip", ["hdrip"] = "HDRip", ["hdtv"] = "HDTV", ["dvd"] = "DVD", ["cam"] = L("Filmé en salle", "Cam") };
    /// <summary>A source as the page says it: « WEB-DL », « BluRay »; the key itself when Mira has no word for it.</summary>
    public static string SourceLabel(string source) => SourceLabels.TryGetValue(source, out var label) ? label : source;
    private static readonly Dictionary<string, int> SourceScore = new()
    { ["remux"] = 14, ["bluray"] = 12, ["webdl"] = 10, ["webrip"] = 7, ["hdrip"] = 5, ["hdtv"] = 4, ["dvd"] = 2, ["cam"] = -100 };
    private static readonly Dictionary<int, int> ResolutionScore = new() { [2160] = 20, [1080] = 30, [720] = 16, [576] = 4, [480] = 4 };
    private static readonly Dictionary<string, int> LanguageScore = new() { ["MULTI"] = 8, ["VFF"] = 5, ["VFQ"] = 5, ["VF"] = 5, ["VOSTFR"] = 3 };

    // Words a title never goes past: the first of them ends it.
    private static readonly HashSet<string> Tags = new("""
        4k 8k uhd hdr hdr10 hdr10+ hdr10plus dv dovi hlg sdr bluray blu-ray bdrip brrip bdremux remux bd bdmv
        web-dl webdl webrip web-rip web hdtv pdtv hdrip dvdrip dvd dvdr dvd5 dvd9 dvdscr screener scr cam camrip hdcam ts hdts
        telesync tc hdtc telecine tvrip xvid divx hevc avc av1 vp9 x264 x265 h264 h265 hi10 hi10p 10bit 8bit aac ac3 eac3 ddp
        dts truehd atmos flac opus mp3 dual dual-audio multi multi-audio multisub multi-subs msubs proper repack rerip internal
        nf amzn dsnp hmax atvp pcok cr french truefrench vff vfq vfi vf vostfr subfrench complete integrale intégrale batch
        extended unrated uncut remastered imax
        """.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
    // Tags that are also words of titles (« Charlottes Web 2006 », « The French Dispatch », « A Complete Unknown »,
    // « Mr Hollands Opus », « Star Wars The Bad Batch S03E15 »): one ends a title only before another tag or at the end,
    // and those no title ends with (« Film FRENCH 2019 », « Show Integrale 2005-2009 ») before a year too.
    private static readonly HashSet<string> TitleWords = new(StringComparer.Ordinal) { "web", "french", "opus", "complete", "integrale", "intégrale", "batch" };
    private static readonly HashSet<string> BeforeYears = new(StringComparer.Ordinal) { "french", "complete", "integrale", "intégrale" };
    private static readonly HashSet<string> PackWords = new(StringComparer.Ordinal) { "complete", "integrale", "intégrale", "batch" };

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    [GeneratedRegex(@"[^\s\[\](){}]+")] private static partial Regex Token();
    [GeneratedRegex(@"^[-.,:;+~]+|[-.,:;~]+$")] private static partial Regex Edges();
    [GeneratedRegex(@"^\d{3,4}[pi]$|^[hx]\.?26[45]$|^(?:dd|ddp|eac3|ac3|aac|dts)[\d.+]*$|^\d{1,2}-?bits?$")] private static partial Regex TagPattern();
    [GeneratedRegex(@"[\u0000-\u001f\u007f-\u009f]")] private static partial Regex Control();
    [GeneratedRegex(@"\.(?:mkv|mp4|m4v|avi|webm|ts|torrent)$", Options)] private static partial Regex Extension();
    [GeneratedRegex(@"^\s*\[?\s*(?:www\.)?[\w-]+\.(?:com|org|net|to|se|cc|tv|xyz|io|me|info|ws|mx|lol|nz|ch|la|in|is|am|li|pw|site|club|top|rs|ru|eu|co|uk|fr)\s*\]?\s*[-–—:|]+\s*", Options)] private static partial Regex Site();
    [GeneratedRegex(@"^\s*\[([^\[\]]{1,40})\]\s*")] private static partial Regex LeadingGroup();
    [GeneratedRegex(@"\(([^()]*)\)")] private static partial Regex Parenthesized();
    [GeneratedRegex(@"-\s?([A-Za-z0-9][A-Za-z0-9_]{1,24})\s*(?:\([^()]*\)|\[[^\[\]]*\]|\{[^{}]*\})*\s*$")] private static partial Regex TrailingGroup();
    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")] private static partial Regex Bracketed();
    [GeneratedRegex(@"^(?:19|20)\d\d$")] private static partial Regex YearOnly();
    [GeneratedRegex(@"(?<![0-9])(?:19|20)[0-9]{2}(?![0-9])")] private static partial Regex AnyYear();
    [GeneratedRegex(@"^[0-9A-Fa-f]{8}$")] private static partial Regex Checksum();
    [GeneratedRegex(@"^(?:19|20)\d\d(?:[-~](?:19|20)?\d\d)?$")] private static partial Regex Years();
    [GeneratedRegex(@"(?<![a-z0-9])s(\d{1,2})[ ._-]?e(\d{1,4})(?:-?e(\d{1,4})|-(\d{1,4})(?![0-9p]))?(?![0-9])", Options)] private static partial Regex SeasonEpisode();
    [GeneratedRegex(@"(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])", Options)] private static partial Regex CrossEpisode();
    [GeneratedRegex(@"(?<![a-z0-9])(?:s\d{1,2}\s*[-~]\s*s\d{1,2}|(?:seasons?|saisons?)\s*\d{1,2}\s*[-~]\s*\d{1,2})(?![0-9])", Options)] private static partial Regex SeasonRange();
    [GeneratedRegex(@"(?<![a-z0-9])(?:s(\d{1,2})(?![0-9a-z])|(?:season|saison|temporada)\s*(\d{1,2})(?![0-9])|(\d{1,2})(?:st|nd|rd|th)\s+season)", Options)] private static partial Regex SeasonMarker();
    [GeneratedRegex(@"(?:^|\s)[-–]\s*(?:ep?\.?\s*|episode\s*|#)?(\d{1,4})(?:v\d)?(?:\s*[-~]\s*(\d{1,4})(?:v\d)?)?(?=\s|$)", Options)] private static partial Regex Numbered();
    [GeneratedRegex(@"(?<![a-z0-9])(?:complete(?:\s+series)?|int[ée]grale|batch)(?![a-z0-9])", Options)] private static partial Regex Complete();
    // What « Complete » and « Intégrale » may come before and still mean every episode or every film: « Complete Collection »,
    // « COMPLETE S01-S05 », « Complete Movie Series », « Complete 8-Film Collection », « Complete Trilogy », « Integrale Livres I a VI ».
    [GeneratedRegex(@"^(?:series|seasons?|saisons?|s\d{1,2}(?:[-~]s?\d{1,2})?|collection|saga|box|boxset|pack|movies?|films?|(?:tri|duo|di|tetra|quadri|penta|hexa|antho)log(?:y|ie)|livres?|\d{1,2}-?(?:films?|movies?)|\d{1,3}[-~]\d{1,3})$", Options)] private static partial Regex Whole();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"^[\s\-–—.:;,_+~|]+|[\s\-–—.:;,_+~|(\[]+$")] private static partial Regex TidyEdges();

    private static readonly Dictionary<string, Regex> Patterns = [];
    private static bool Has(string text, string pattern)
    {
        Regex? regex;
        lock (Patterns)
            if (!Patterns.TryGetValue(pattern, out regex)) Patterns[pattern] = regex = new Regex($"(?<![a-z0-9])(?:{pattern})(?![a-z0-9])", Options);
        return regex.IsMatch(text);
    }

    private static string Bare(string token) => Edges().Replace(token.ToLowerInvariant(), "");
    private static bool IsTag(string token)
    {
        var t = Bare(token);
        return Tags.Contains(t) || TagPattern().IsMatch(t) || (t.IndexOf('-') is > 0 and var dash && Tags.Contains(t[..dash]));
    }

    // « Batch-GRP » is « batch »; « WEB-DL » stays itself.
    private static string Word(string token) { var t = Bare(token); return t.IndexOf('-') is > 0 and var dash && !Tags.Contains(t) ? t[..dash] : t; }

    /// <summary>
    /// Whether the token at <paramref name="i"/> ends a title: a tag, except a word of titles that another word, an episode
    /// or a year follows (« Charlottes Web 2006 », « A Complete Unknown », « The Bad Batch S03E15 »). « Complete » and
    /// « Intégrale » also end it before what makes every episode (« Complete Series », « COMPLETE S01-S05 »).
    /// </summary>
    private static bool EndsTitle(IReadOnlyList<Match> tokens, int i)
    {
        if (!IsTag(tokens[i].Value)) return false;
        var word = Word(tokens[i].Value);
        if (!TitleWords.Contains(word)) return true;
        var next = i + 1;
        while (next < tokens.Count && !tokens[next].Value.Any(char.IsLetterOrDigit)) next++;
        if (next >= tokens.Count) return true;
        var following = Bare(tokens[next].Value);
        if (BeforeYears.Contains(word) && Years().IsMatch(following)) return true;
        if (!PackWords.Contains(word)) return EndsTitle(tokens, next);
        // A pack word before another (« The Bad Batch Complete Series ») is the title's own.
        return !PackWords.Contains(Word(tokens[next].Value)) && EndsTitle(tokens, next) || word != "batch" && Whole().IsMatch(following);
    }

    /// <summary>
    /// A title to compare, its letters and digits only: lower case, no accent, ligature or exponent (« œ » as « oe », « ³ »
    /// as « 3 »), « & » as « and », without apostrophes, spaces or punctuation. Release names write a title in plain letters,
    /// an elided article as a word of its own (« L.Arnacoeur », « Le.Fabuleux.Destin.D.Amelie.Poulain ») and an English
    /// apostrophe as nothing (« Charlottes.Web »): « L'Arnacœur », « Charlotte's Web » and « Alien³ » are them.
    /// </summary>
    public static string TitleKey(string? title)
    {
        var builder = new StringBuilder();
        foreach (var rune in (title ?? "").Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (rune.Value == '&') builder.Append("and");
            else if (Rune.IsLetterOrDigit(rune)) builder.Append(Rune.ToLowerInvariant(rune).Value switch
            {
                'œ' => "oe",
                'æ' => "ae",
                'ß' => "ss",
                'ø' => "o",
                'ł' => "l",
                'đ' => "d",
                var letter => char.ConvertFromUtf32(letter),
            });
        }
        return builder.ToString();
    }

    private static string Tidy(string text)
    {
        var title = TidyEdges().Replace(Spaces().Replace(text, " ").Trim(), "");
        if (title != title.ToLowerInvariant() || !title.Any(char.IsLetter)) return title;
        var chars = title.ToCharArray();
        for (var i = 0; i < chars.Length; i++) if (char.IsLetter(chars[i]) && (i == 0 || char.IsWhiteSpace(chars[i - 1]))) chars[i] = char.ToUpperInvariant(chars[i]);
        return new string(chars);
    }

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    public static ReleaseInfo Parse(string? name)
    {
        var text = Extension().Replace(Control().Replace(name ?? "", " ").Trim(), "");
        text = Site().Replace(text, "");
        string? group = null;
        var lead = LeadingGroup().Match(text);
        if (lead.Success) { group = lead.Groups[1].Value.Trim(); text = text[lead.Length..]; }

        // The other name, in parentheses after the release's own: « (JoJo no Kimyou na Bouken: Steel Ball Run, Dual-Audio) »,
        // « (VOSTFR, Battle Spirits Re Zekkai no Kuu) »; never a tag, a platform or a note (« (READNFO, …) », « (WKN) »).
        string? alternate = null;
        foreach (Match m in Parenthesized().Matches(text))
        {
            foreach (var element in m.Groups[1].Value.Split(','))
            {
                var first = element;
                if (first.Contains(" - ", StringComparison.Ordinal)) first = first[..first.IndexOf(" - ", StringComparison.Ordinal)];
                first = first.Trim();
                if (first.Length < 3 || !first.Any(char.IsLetter) || first.Length <= 6 && AnyYear().IsMatch(first) || Checksum().IsMatch(first)
                    || Token().Matches(first).Any(t => IsTag(t.Value)) || ReleaseName.NamesNothing(first)) continue;
                // « Kikansha no Mahou wa Tokubetsu desu 2nd Season »: the show, as its title would be read.
                if (Parse(first).Title is { Length: > 0 } named && !ReleaseName.NamesNothing(named)) alternate = named;
                break;
            }
            if (alternate is not null) break;
        }

        // The group at the end of a scene name: « …H.264-VARYG (…) ».
        if (group is null && TrailingGroup().Match(text) is { Success: true } tail)
        {
            var value = tail.Groups[1].Value;
            if (!value.All(char.IsAsciiDigit) && !IsTag(value) && !text[..tail.Index].EndsWith("web", StringComparison.OrdinalIgnoreCase)) group = value;
        }

        // Scene names put dots or underscores for spaces; « Mr. Robot », « AAC2.0 » and « 5.1 » keep theirs, « 2049.2017 » does not.
        var spaced = text.Replace('_', ' ');
        if (spaced.Count(c => c == '.') >= spaced.Count(c => c == ' '))
        {
            var chars = spaced.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '.') continue;
                var decimalDot = i > 0 && char.IsAsciiDigit(spaced[i - 1]) && i + 1 < spaced.Length && char.IsAsciiDigit(spaced[i + 1]) && (i + 2 >= spaced.Length || !char.IsAsciiDigit(spaced[i + 2]));
                if (!decimalDot) chars[i] = ' ';
            }
            spaced = new string(chars);
        }
        var plain = Parenthesized().Replace(Bracketed().Replace(spaced, " "), m => YearOnly().IsMatch(m.Groups[1].Value.Trim()) ? $" {m.Groups[1].Value} " : " ");

        // Season and episode.
        int? season = null, episode = null, lastEpisode = null; bool absolute = false, pack = false; var cut = plain.Length;
        void Mark(int index) { if (index >= 0 && index < cut) cut = index; }
        var se = SeasonEpisode().Match(plain);
        if (se.Success)
        {
            season = Int(se.Groups[1]); episode = Int(se.Groups[2]);
            lastEpisode = se.Groups[3].Success ? Int(se.Groups[3]) : se.Groups[4].Success ? Int(se.Groups[4]) : null;
            Mark(se.Index);
        }
        else if (CrossEpisode().Match(plain) is { Success: true } cross) { season = Int(cross.Groups[1]); episode = Int(cross.Groups[2]); Mark(cross.Index); }
        if (episode is null)
        {
            // Fansub numbering: « Show - 05 », « Show - 01 ~ 12 » (a batch); never a year.
            var number = Numbered().Match(plain);
            var range = SeasonRange().Match(plain);
            // « Show Season 2 - 05 » is season 2, episode 5, where ReleaseName places it, never seasons 2 to 5: the dash is the episode's.
            if (range.Success && number.Success && number.Index > range.Index && number.Index < range.Index + range.Length
                && !range.Value.StartsWith("seasons", StringComparison.OrdinalIgnoreCase) && !range.Value.StartsWith("saisons", StringComparison.OrdinalIgnoreCase)) range = Match.Empty;
            var single = SeasonMarker().Match(plain);
            if (range.Success) { pack = true; Mark(range.Index); }
            else if (single.Success) { season = Int(single.Groups[1].Success ? single.Groups[1] : single.Groups[2].Success ? single.Groups[2] : single.Groups[3]); pack = true; Mark(single.Index); }
            if (number.Success && !YearOnly().IsMatch(number.Groups[1].Value) && (group is not null || season is not null || number.Groups[1].Length <= 3))
            {
                episode = Int(number.Groups[1]); Mark(number.Index);
                if (number.Groups[2].Success) { lastEpisode = Int(number.Groups[2]); pack = true; }
                else if (pack) pack = false;
                absolute = season is null;
            }
        }
        var tokens = Token().Matches(plain).ToList();
        foreach (Match complete in Complete().Matches(plain))
        {
            // « A Complete Unknown », « Star Wars The Bad Batch S03E15 »: words of the title, not every episode.
            var at = tokens.FindIndex(t => t.Index == complete.Index);
            if (at >= 0 && !complete.Value.Contains(' ') && !EndsTitle(tokens, at)) continue;
            if (episode is null || lastEpisode is not null) pack = true;
            Mark(complete.Index);
            break;
        }

        // The title ends at the first marker: an episode, a year after its first word, a tag.
        int? year = null;
        for (var i = 1; i < tokens.Count && tokens[i].Index < cut; i++)
        {
            var t = Bare(tokens[i].Value);
            if (YearOnly().IsMatch(t) && int.Parse(t, CultureInfo.InvariantCulture) <= DateTime.UtcNow.Year + 1) { year = int.Parse(t, CultureInfo.InvariantCulture); continue; }
            if (EndsTitle(tokens, i)) { Mark(tokens[i].Index); break; }
        }
        if (year is not null)
        {
            // The last plausible year before the tags wins (« Blade Runner 2049 2017 ») and ends the title.
            var chosen = tokens.Where((t, i) => i > 0 && t.Index < cut && YearOnly().IsMatch(Bare(t.Value))).Last();
            year = int.Parse(Bare(chosen.Value), CultureInfo.InvariantCulture); Mark(chosen.Index);
        }
        var title = Tidy(plain[..cut]);
        if (title.Length == 0) title = Tidy(plain) is { Length: > 0 } whole ? whole : name ?? "";
        // What the release says of itself comes after its title: « The French Dispatch » is not in French, « Cam » (2018)
        // was not filmed in a theatre. The title's place in the name is known while nothing in brackets comes before its end.
        var end = plain[..cut].TrimEnd().Length;
        var lower = (end > 0 && string.CompareOrdinal(spaced, 0, plain, 0, end) == 0 ? text[end..] : text).ToLowerInvariant();

        // Picture and sound.
        int? resolution = Has(lower, "2160p|4k|uhd") ? 2160 : Has(lower, "1080[pi]") ? 1080 : Has(lower, "720p") ? 720 : Has(lower, "576p") ? 576 : Has(lower, "480p|360p") ? 480 : null;
        var source = Has(lower, "cam|camrip|hdcam|ts|hdts|telesync|tc|hdtc|telecine|scr|screener|dvdscr|pre-?dvd") ? "cam"
            : Has(lower, "remux|bdremux") ? "remux"
            : Has(lower, "blu-?ray|bdrip|brrip|bd|bdmv|bd-?rip") ? "bluray"
            : Has(lower, "web-?rip|web rip") ? "webrip"
            : Has(lower, "web-?dl|web dl|web|amzn|nf|dsnp|hmax|atvp|cr") ? "webdl"
            : Has(lower, "hdrip") ? "hdrip"
            : Has(lower, "hdtv|pdtv|tvrip|dsr|satrip") ? "hdtv"
            : Has(lower, "dvdrip|dvd|dvd5|dvd9|dvdr") ? "dvd" : null;
        var codec = Has(lower, @"hevc|x\.?265|h\.?\s?265") ? "HEVC" : Has(lower, "av1") ? "AV1" : Has(lower, @"avc|x\.?264|h\.?\s?264") ? "H.264"
            : Has(lower, "xvid|divx") ? "XviD" : Has(lower, "vp9") ? "VP9" : null;
        var vision = Has(lower, "dv|dovi|dolby[ .]?vision");
        var hdr = Has(lower, @"hdr10\+|hdr10plus") ? "HDR10+" : Has(lower, "hdr|hdr10") ? "HDR" : null;
        var languages = new List<string>();
        if (Has(lower, "multi(?![-. ]?subs?)(?:[-. ]?audio)?|multi[-. ]?vf2?|multilang")) languages.Add("MULTI");
        if (Has(lower, "truefrench|vff")) languages.Add("VFF");
        if (Has(lower, "vfq")) languages.Add("VFQ");
        if (!languages.Any(x => x is "VFF" or "VFQ") && Has(lower, "french|vfi|vf")) languages.Add("VF");
        if (Has(lower, "vostfr|subfrench")) languages.Add("VOSTFR");

        return new(title, year, alternate, season, episode, lastEpisode, absolute, pack, resolution, source, codec, Has(lower, "10-?bits?|hi10p?|main10"),
            vision ? (hdr is null ? "DV" : $"DV {hdr}") : hdr, languages, group, Has(lower, "repack|proper|rerip"), lead.Success);
    }

    /// <summary>
    /// « 1080p · WEB-DL · HEVC · MULTI · ToonsHub »: what the release is, in the words of the page. The group only when
    /// it is a fansub's (Erai-raws, SubsPlease tell who translated an anime); a scene group (FW, QTZ) says nothing to most.
    /// </summary>
    public static string QualityLine(ReleaseInfo release) => string.Join(" · ", new[]
    {
        release.Resolution == 2160 ? "4K" : release.Resolution is { } p ? $"{p}p" : "", release.Source is { } s ? SourceLabels[s] : "", release.Codec ?? "", release.Hdr ?? "",
    }.Concat(release.Languages).Append(release.Fansub ? release.Group ?? "" : "").Where(x => x.Length > 0));

    /// <summary>
    /// What a download is called on the screen: the title read from its name (« Dune Part Two (2024) », « The Bear · Saison 3
    /// complète ») and the name itself to show under it; the name alone (<c>Full</c> null) when nothing more can be read.
    /// </summary>
    public static (string Title, string? Full) Readable(string? name)
    {
        var raw = (name ?? "").Trim();
        var release = Parse(raw);
        var label = EpisodeLabel(release);
        var title = string.Join(" · ", new[] { release.Title + (release.Year is { } y && label.Length == 0 ? $" ({y})" : ""), label }.Where(x => x.Length > 0));
        return release.Title.Length > 0 && TitleKey(title) != TitleKey(raw) ? (title, raw) : (raw, null);
    }

    /// <summary>Whether a site of TorLink counts who shares its results: SubsPlease never does.</summary>
    public static bool ReportsHealth(string? source) => !string.Equals(source, "subsplease", StringComparison.OrdinalIgnoreCase);

    /// <summary>How good a result is for us, the higher the better (releases.js: releaseScore).</summary>
    public static int Score(RelayHit hit, ReleaseInfo release, string? category = null)
    {
        var score = (release.Resolution is { } r && ResolutionScore.TryGetValue(r, out var rs) ? rs : 12) + (release.Source is { } s ? SourceScore[s] : 5);
        score += release.Languages.Select(x => LanguageScore.GetValueOrDefault(x)).DefaultIfEmpty(0).Max();
        if (release.Repack) score += 1;
        if (release.Codec == "AV1") score -= 2;
        if (release.Codec == "XviD") score -= 4;
        if (release.TenBit && release.Codec == "H.264") score -= 4;
        if (release.Hdr == "DV") score -= 3;
        const double Gib = 1L << 30;
        var episodic = release.Episode is not null || release.Pack || (hit.Category ?? category) is "tv" or "anime";
        if (hit.Size > 0 && !release.Pack && release.Resolution >= 1080 && hit.Size < (episodic ? 0.25 : 1.2) * Gib) score -= 4;
        if (ReportsHealth(hit.Source))
        {
            if (hit.Seeders <= 0) score -= 200;
            else if (hit.Seeders < 3) score -= 6;
        }
        return score;
    }

    /// <summary>« S06E05 », « S01E01-E03 », « Épisode 5 », « Saison 2 complète », « Toutes les saisons »; empty for a film.</summary>
    public static string EpisodeLabel(ReleaseInfo release)
    {
        static string Two(int? n) => (n ?? 0).ToString("00", CultureInfo.InvariantCulture);
        if (release.Pack)
        {
            if (release.Episode is { } from && release.LastEpisode is { } to)
                return L($"{(release.Season is { } s ? $"Saison {s}, é" : "É")}pisodes {from} à {to}", $"{(release.Season is { } t ? $"Season {t}, e" : "E")}pisodes {from} to {to}");
            return release.Season is { } season ? L($"Saison {season} complète", $"Complete season {season}") : L("Toutes les saisons", "All seasons");
        }
        if (release.Episode is not { } episode) return "";
        if (release.Absolute) return release.LastEpisode is { } last ? L($"Épisodes {episode} à {last}", $"Episodes {episode} to {last}") : L($"Épisode {episode}", $"Episode {episode}");
        return $"S{Two(release.Season)}E{Two(episode)}{(release.LastEpisode > episode ? $"-E{Two(release.LastEpisode)}" : "")}";
    }

    /// <summary>What makes a release worth less, in a few words; empty when nothing does.</summary>
    public static string Warning(RankedRelease entry)
    {
        if (entry.Release.Source == "cam") return L("Filmé dans une salle de cinéma : image et son médiocres.", "Filmed in a movie theater: poor picture and sound.");
        if (!ReportsHealth(entry.Hit.Source)) return "";
        return entry.Hit.Seeders <= 0 ? L("Personne ne partage ce fichier : il ne se téléchargera sans doute pas.", "No one is sharing this file: it probably won’t download.")
            : entry.Hit.Seeders < 3 ? L("Peu de sources : le téléchargement sera lent.", "Few sources: the download will be slow.") : "";
    }

    /// <summary>
    /// The results gathered by title and episode (or season, or film and year), the best release of each first; a
    /// release giving another name in parentheses meets the releases under that name. Titles keep the order of their
    /// best-shared result; inside a title, whole seasons first, then the latest episodes. Hits of a search in every
    /// category (<see cref="RelayHit.Category"/>) meet only those of the same category: an anime and its live-action series stay apart.
    /// </summary>
    public static List<ReleaseGroup> Group(IEnumerable<RelayHit> hits, string? category = null)
    {
        var entries = hits.Select((hit, order) => { var release = Parse(hit.Name); return new RankedRelease(hit, release, order, Score(hit, release, category)); }).ToList();
        var names = new Dictionary<string, string>();
        foreach (var entry in entries)
        {
            var own = TitleKey(entry.Release.Title);
            if (entry.Release.Alternate is { } other && TitleKey(other) != own) names[TitleKey(other)] = own;
        }
        string ShowOf(ReleaseInfo release) => names.GetValueOrDefault(TitleKey(release.Title)) ?? TitleKey(release.Title);
        static bool FilmLike(ReleaseInfo release) => release.Episode is null && !release.Pack;
        var groups = new Dictionary<string, (string Show, string? Category, List<RankedRelease> Entries)>();
        var keys = new List<string>();
        foreach (var entry in entries)
        {
            var r = entry.Release; var show = ShowOf(r); var kind = entry.Hit.Category;
            var key = (kind is null ? "" : kind + "|") + (FilmLike(r) ? $"{show}|{r.Year}" : r.Pack ? $"{show}|pack|{r.Season}|{r.Episode}-{r.LastEpisode}" : $"{show}|e|{(r.Absolute ? "" : r.Season)}|{r.Episode}");
            if (!groups.TryGetValue(key, out var group)) { groups[key] = group = (show, kind, []); keys.Add(key); }
            group.Entries.Add(entry);
        }
        // An episode numbered without a season meets the same number of the same show with one, when only one season has it.
        foreach (var key in keys.ToList())
        {
            if (!groups.TryGetValue(key, out var group)) continue;
            var first = group.Entries[0].Release;
            if (!first.Absolute || first.Pack) continue;
            var matches = keys.Where(k => k != key && groups.ContainsKey(k)).Select(k => (Key: k, Group: groups[k]))
                .Where(g => g.Group.Show == group.Show && g.Group.Category == group.Category && g.Group.Entries[0].Release is { Absolute: false, Pack: false } o && o.Episode == first.Episode).ToList();
            if (matches.Count != 1) continue;
            matches[0].Group.Entries.AddRange(group.Entries); groups.Remove(key); keys.Remove(key);
        }
        var showOrder = new Dictionary<string, int>();
        foreach (var entry in entries) showOrder.TryAdd(ShowOf(entry.Release), entry.Order);
        return keys.Select(key =>
        {
            var (show, kind, list) = groups[key];
            var sorted = list.OrderByDescending(x => x.Score).ThenByDescending(x => x.Hit.Seeders).ThenBy(x => x.Order).ToList();
            var named = sorted.FirstOrDefault(x => !x.Release.Absolute && TitleKey(x.Release.Title) == show) ?? sorted[0];
            var shown = sorted.FirstOrDefault(x => x.Release is { Absolute: false, Pack: false, Episode: not null }) ?? sorted[0];
            var year = FilmLike(named.Release) && named.Release.Year is { } y ? $" ({y})" : "";
            return new ReleaseGroup(key, show, named.Release.Title + year, EpisodeLabel(shown.Release), sorted[0], sorted.Skip(1).ToList(), shown.Release.Pack, shown.Release, kind);
        })
        .OrderBy(g => showOrder[g.Show]).ThenBy(g => g.Show, StringComparer.Ordinal).ThenBy(g => g.Pack ? 0 : 1)
        .ThenByDescending(g => g.Release.Season ?? 0).ThenByDescending(g => g.Release.Episode ?? 0).ThenBy(g => g.Best.Order).ToList();
    }
}
