using System.Globalization;

namespace Mira.Core;

/// <summary>
/// The languages of the tracks: the order given to mpv (alang, slang) and to Jellyfin before any choice, and what Settings
/// offers. A device that never chose starts with its own language when it is neither English nor French, else Mira's
/// (<see cref="Start"/>); a choice writes « the language, then English » (English: then French), Japanese first for the
/// audio when asked. Cases shared with the iPhone in tests/fixtures/languages.json.
/// </summary>
public static class TrackLanguages
{
    /// <summary>Each language Settings offers, with every code a file or Jellyfin may give it (ISO 639-2 B and T, 639-1).</summary>
    public static readonly IReadOnlyList<(string Two, string Codes)> Known =
    [
        ("ja", "jpn,ja"), ("fr", "fre,fra,fr"), ("en", "eng,en"), ("de", "ger,deu,de"), ("es", "spa,es"), ("it", "ita,it"),
        ("pt", "por,pt"), ("ru", "rus,ru"), ("ko", "kor,ko"), ("zh", "chi,zho,zh"), ("ar", "ara,ar"), ("hi", "hin,hi"),
        ("pl", "pol,pl"), ("nl", "dut,nld,nl"), ("sv", "swe,sv"), ("no", "nor,nob,no,nb"), ("da", "dan,da"), ("fi", "fin,fi"),
        ("tr", "tur,tr"), ("he", "heb,he"), ("th", "tha,th"), ("vi", "vie,vi"), ("id", "ind,id"), ("uk", "ukr,uk"),
        ("cs", "cze,ces,cs"), ("hu", "hun,hu"), ("el", "gre,ell,el"), ("ro", "rum,ron,ro"), ("ca", "cat,ca"), ("ms", "may,msa,ms"),
    ];
    public const string Japanese = "ja", English = "en", French = "fr";

    /// <summary>
    /// The system's language (« de-DE », « pt-BR »), set once at startup before Mira's language takes the place of the
    /// culture: Windows' display language, the first of the Mac's preferred languages. Empty: Mira's language only.
    /// </summary>
    public static string Device { get; set; } = "";
    public static void UseDevice(CultureInfo culture) => Device = culture.Name;

    /// <summary>The first language of a device that never chose: its own, unless English or French (Mira's then, as before).</summary>
    public static string StartFor(string? device, string mira) =>
        Two(device) is { } own && own is not (English or French) ? own : mira == French ? French : English;
    public static string Start => StartFor(Device, Lang.Code);
    public static string DefaultAudio => Audio(Start, japaneseFirst: true);
    public static string DefaultSubtitles => Subtitles(Start);

    /// <summary>The language of a code (« ger », « pt-BR », « nb »), as Settings lists it; null when not listed.</summary>
    public static string? Two(string? code)
    {
        var main = (code ?? "").Trim().Split('-', '_')[0].ToLowerInvariant();
        if (main.Length == 0) return null;
        foreach (var (two, codes) in Known)
            if (codes.Split(',').Contains(main)) return two;
        return null;
    }
    public static string Codes(string two) => Known.FirstOrDefault(x => x.Two == two).Codes ?? two;
    private static string Then(string first) => first == English ? French : English;

    /// <summary>The audio order a choice writes: Japanese first when asked, the language, then English (English: then French).</summary>
    public static string Audio(string first, bool japaneseFirst) =>
        string.Join(",", new[] { japaneseFirst ? Japanese : null, first, Then(first) }.OfType<string>().Distinct().Select(Codes));
    /// <summary>The subtitles' order a choice writes: the language, then English (English: then French).</summary>
    public static string Subtitles(string first) => string.Join(",", new[] { first, Then(first) }.Distinct().Select(Codes));

    /// <summary>
    /// What Settings shows of an order: its first language other than Japanese (Japanese when the order is Japanese, then
    /// English at most), null when one of its codes is not listed (« Ordre personnalisé »).
    /// </summary>
    public static string? FirstOf(string? order)
    {
        var codes = Split(order);
        var languages = codes.Select(Two).ToList();
        if (languages.Count == 0 || languages.Any(x => x is null)) return null;
        var distinct = languages.OfType<string>().Distinct().ToList();
        if (distinct[0] != Japanese || distinct.Count == 1 || distinct is [_, English]) return distinct[0];
        return distinct[1];
    }
    /// <summary>Whether a Japanese track goes before the others (an anime's original language).</summary>
    public static bool JapaneseFirst(string? order) => Split(order) is [var first, ..] && Two(first) == Japanese;
    private static List<string> Split(string? order) => (order ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>Whether Settings' choices write this order; otherwise Windows shows it as « Ordre personnalisé ».</summary>
    public static bool IsAudioChoice(string? order) => FirstOf(order) is { } first && Same(order, Audio(first, JapaneseFirst(order)));
    public static bool IsSubtitleChoice(string? order) => FirstOf(order) is { } first && Same(order, Subtitles(first));
    private static bool Same(string? a, string b) => Split(a).Select(x => x.ToLowerInvariant()).SequenceEqual(Split(b));

    /// <summary>The languages Settings offers, in Mira's language: this device's first, then by name.</summary>
    public static IReadOnlyList<(string Two, string Name)> Choices()
    {
        var start = Start;
        return [.. Known.Select(x => (x.Two, Name: PlayerText.Language(x.Two) ?? x.Two))
            .OrderBy(x => x.Two == start ? 0 : 1).ThenBy(x => x.Name, StringComparer.Create(Lang.Culture, ignoreCase: true))];
    }
}
