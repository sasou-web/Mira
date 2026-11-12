namespace Mira.Core;

/// <summary>One choice of a filter: what <see cref="CatalogQuery"/> keeps (« » = all) and what the list shows.</summary>
public sealed record FilterChoice(string Key, string Label);

/// <summary>
/// The filters of Films, Séries and Ta bibliothèque beyond genre, year and progress (Windows and Mac fill their lists
/// from here, the iPhone has the same choices in Browse.swift; cases shared in tests/fixtures/filters.json): picture
/// (4K, HD, SD), audio language, subtitles (any, none, a language), community rating. Every one is a parameter of
/// Jellyfin's Items request, combined with the others: « 4K, not watched, in French » is one request.
/// </summary>
public static class LibraryFilters
{
    /// <summary>
    /// The languages offered, with every code Jellyfin may have stored for them (seen on 12.1), all asked: a file's tag
    /// says « fre » (ISO 639-2 bibliographic) where a subtitle file named « .fr.srt » becomes « fra » (terminological),
    /// and the regions Jellyfin knows keep their own code, in lower case: « .fr-CA.srt » is « fr-ca », likewise
    /// « es-419 », « zh-cn », « zh-tw », « zh-hk », « zh-hans », « zh-hant ». Other regions (« .fr-FR.srt »,
    /// « .en-US.srt ») are stored without a language: no filter can find them.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Codes)> Languages =
    [
        ("fr", "fre,fra,fr-ca"), ("en", "eng"), ("ja", "jpn"), ("es", "spa,es-419"), ("de", "ger,deu"), ("it", "ita"), ("ko", "kor"),
        ("zh", "chi,zho,zh-cn,zh-tw,zh-hk,zh-hans,zh-hant"),
    ];

    public static string? Codes(string? language) => Languages.FirstOrDefault(x => x.Key == language).Codes;
    public static string LanguageName(string key) => key switch
    {
        "fr" => L("Français", "French"),
        "en" => L("Anglais", "English"),
        "ja" => L("Japonais", "Japanese"),
        "es" => L("Espagnol", "Spanish"),
        "de" => L("Allemand", "German"),
        "it" => L("Italien", "Italian"),
        "ko" => L("Coréen", "Korean"),
        "zh" => L("Chinois", "Chinese"),
        _ => key
    };

    /// <summary>
    /// Jellyfin filters by audio and subtitle language from 12 on (`audioLanguages`, `subtitleLanguages`): 10.11 and
    /// before ignore them and would give the whole list. An unknown version: not offered.
    /// </summary>
    public static bool LanguagesSupported(string? serverVersion) => JellyfinReleases.ParseVersion(serverVersion) is { Major: >= 12 };

    /// <summary>4K (3840 px or so), HD (720p and 1080p), SD (below): Jellyfin's is4K and isHd, a series by its episodes.</summary>
    public static IReadOnlyList<FilterChoice> Qualities() =>
        [new("", L("Toutes", "All")), new("4k", "4K"), new("hd", L("HD (720p, 1080p)", "HD (720p, 1080p)")), new("sd", "SD")];
    public static IReadOnlyList<FilterChoice> Audio() =>
        [new("", L("Toutes", "All")), .. Languages.Select(x => new FilterChoice(x.Key, LanguageName(x.Key)))];
    /// <summary>Any subtitles, none, then each language when the server can tell (<see cref="LanguagesSupported"/>).</summary>
    public static IReadOnlyList<FilterChoice> Subtitles(bool languages) =>
        [new("", L("Tous", "All")), new("any", L("Avec sous-titres", "With subtitles")), new("none", L("Sans sous-titres", "Without subtitles")),
         .. languages ? Languages.Select(x => new FilterChoice(x.Key, LanguageName(x.Key))) : []];
    /// <summary>The community rating (TMDb's, out of 10) at least.</summary>
    public static IReadOnlyList<FilterChoice> Ratings() =>
        [new("", L("Toutes", "All")), new("6", L("6 et plus", "6 and up")), new("7", L("7 et plus", "7 and up")), new("8", L("8 et plus", "8 and up"))];

    /// <summary>The Items parameters of these filters (none for « all »), as <see cref="CatalogQuery.Parameters"/> adds them.</summary>
    public static string Parameters(string? quality, string? audio, string? subtitles, int? minRating) =>
        (quality switch { "4k" => "&is4K=true", "hd" => "&isHd=true", "sd" => "&isHd=false&is4K=false", _ => "" }) +
        (Codes(audio) is { } spoken ? "&audioLanguages=" + spoken : "") +
        (subtitles switch { "any" => "&hasSubtitles=true", "none" => "&hasSubtitles=false", _ => Codes(subtitles) is { } written ? "&subtitleLanguages=" + written : "" }) +
        (minRating is { } rating ? "&minCommunityRating=" + rating : "");
}
