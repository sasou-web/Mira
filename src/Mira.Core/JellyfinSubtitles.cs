using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mira.Core;

/// <summary>A subtitle file to keep on Jellyfin (POST Videos/{id}/Subtitles): its format (« srt »), ISO 639-2 language, flags and content.</summary>
public sealed record SubtitleUpload(string Format, string Language, bool Forced, bool HearingImpaired, byte[] Data);
/// <summary>
/// What became of a subtitle sent to Jellyfin. KeptInJellyfinData: Jellyfin could not write beside the video (it only
/// reads the media folders a Mira install gave it), so its library now keeps the subtitles in Jellyfin's own data.
/// </summary>
public enum SubtitleKept { Kept, KeptInJellyfinData, Refused, NotWritten }
/// <summary>A subtitle Jellyfin's subtitle extension (Open Subtitles…) found for a title (Items/{id}/RemoteSearch/Subtitles).</summary>
/// <remarks>Score and Source: Mira's own provider (« Mira ») says how well each result fits the file (0 to 100) and which site it is from.</remarks>
public sealed record RemoteSubtitle(string Id, string Name, string Provider, string Format, string Language, int Downloads, bool HashMatch, bool Forced, bool HearingImpaired, bool MachineTranslated,
    int? Score = null, string? Source = null);

/// <summary>
/// « Sous-titres gardés sur Jellyfin » (Windows and Mac; Mira web's js/subtitles.js says the same, the shared cases
/// in tests/fixtures/subtitles.json): a file loaded during playback sent to Jellyfin, which every device then offers,
/// and the search of Jellyfin's subtitle extension, shown only when one is installed.
/// </summary>
public static class JellyfinSubtitles
{
    /// <summary>The formats Mira sends: those its file pickers offer, all among Jellyfin's subtitle extensions.</summary>
    public static readonly IReadOnlyList<string> Formats = ["srt", "ass", "ssa", "vtt", "sub"];
    /// <summary>A file bigger than this is no subtitle file.</summary>
    public const int MaxSize = 4 * 1024 * 1024;
    /// <summary>The languages searched and guessed, by the code Jellyfin understands (ISO 639-2, as its own dashboard sends).</summary>
    private static readonly (string Code, string[] Names)[] Known =
    [
        ("fre", ["fre", "fra", "fr", "french", "francais", "français", "vf", "vff", "vfq", "vostfr"]),
        ("eng", ["eng", "en", "english", "anglais", "vosta"]),
        ("jpn", ["jpn", "ja", "jp", "japanese", "japonais"]),
        ("spa", ["spa", "es", "spanish", "espanol", "español"]),
        ("ger", ["ger", "deu", "de", "german", "deutsch", "allemand"]),
        ("ita", ["ita", "it", "italian", "italiano"]),
        ("por", ["por", "pt", "portuguese", "portugues", "português"]),
    ];
    private static readonly string[] ForcedNames = ["forced", "force", "forces", "forcé", "forcés"];
    private static readonly string[] HearingNames = ["sdh", "cc", "hi"];

    /// <summary>The code Jellyfin searches with for a language code (« fr », « fra » → « fre »); null when Mira does not know it.</summary>
    public static string? SearchCode(string? code) =>
        code?.Trim().Split('-', '_')[0].ToLowerInvariant() is { Length: > 0 } main ? Known.FirstOrDefault(x => x.Names.Contains(main)).Code : null;
    /// <summary>The languages offered to search: those of the subtitle preferences, then Mira's language and the other one, three at most.</summary>
    public static IReadOnlyList<string> SearchLanguages(string? preferences) =>
        (preferences ?? "").Split(',').Select(SearchCode).Append(IsFrench ? "fre" : "eng").Append(IsFrench ? "eng" : "fre").OfType<string>().Distinct().Take(3).ToList();

    /// <summary>
    /// What a file's name says of it: its format, then the language, « forced » and « sdh » tags its name ends with
    /// (« Film.2021.fr.forced.srt »), else the first language of the preferences. Null for a format Mira does not send.
    /// </summary>
    public static (string Format, string Language, bool Forced, bool HearingImpaired)? Guess(string fileName, string? preferences)
    {
        var format = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        if (!Formats.Contains(format)) return null;
        string? language = null; bool forced = false, hearing = false;
        var words = Path.GetFileNameWithoutExtension(fileName).Split(['.', '-', '_', ' ', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        // From the end of the name: the tags stop at the first word that is none (« Movie.It.Follows.en » is English).
        foreach (var word in words.Reverse().Select(x => x.ToLowerInvariant()))
        {
            if (ForcedNames.Contains(word)) forced = true;
            else if (HearingNames.Contains(word)) hearing = true;
            else if (language is null && Known.FirstOrDefault(x => x.Names.Contains(word)).Code is { } code) language = code;
            else break;
        }
        return (format, language ?? SearchLanguages(preferences)[0], forced, hearing);
    }

    /// <summary>
    /// The search's results: the version of this very file first (its hash), then those people wrote; among them, by how
    /// well Mira says they fit the file (a result of another provider counts as an average one, <see cref="Unscored"/>),
    /// then the most downloaded first.
    /// </summary>
    public static IReadOnlyList<RemoteSubtitle> Sort(IEnumerable<RemoteSubtitle> found) =>
        found.OrderByDescending(x => x.HashMatch).ThenBy(x => x.MachineTranslated).ThenByDescending(x => x.Score ?? Unscored).ThenByDescending(x => x.Downloads).ToList();
    /// <summary>Where a result Mira did not score stands among those it did: what Mira would take once aligned.</summary>
    public const int Unscored = 35;
    /// <summary>A result as a row: its name, then « OpenSubtitles · SRT · 1 234 téléchargements · pour ce fichier ».</summary>
    public static (string Label, string Details) Describe(RemoteSubtitle subtitle) => (subtitle.Name.Length > 0 ? subtitle.Name : L("Sous-titres", "Subtitles"), string.Join(" · ", new[]
    {
        subtitle.Provider == "Mira" && subtitle.Source is { Length: > 0 } source ? "Mira · " + source : subtitle.Provider,
        subtitle.Score is { } score ? L($"note {score}/100", $"score {score}/100") : "", subtitle.Format.ToUpperInvariant(),
        subtitle.Downloads > 0 ? Thousands(subtitle.Downloads) + " " + Plural(subtitle.Downloads, "téléchargement", "téléchargements", "download", "downloads") : "",
        subtitle.HashMatch ? L("pour ce fichier", "for this file") : "", subtitle.Forced ? L("forcés", "forced") : "", subtitle.HearingImpaired ? L("pour sourds et malentendants", "for the deaf and hard of hearing") : "",
        subtitle.MachineTranslated ? L("traduction automatique", "machine translation") : "",
    }.Where(x => x.Length > 0)));
    internal static string Thousands(int count) => IsFrench ? Regex.Replace(count.ToString(CultureInfo.InvariantCulture), @"\B(?=(\d{3})+(?!\d))", "\u00A0") : count.ToString("N0", Culture);
    /// <summary>A result as Jellyfin gives it (RemoteSubtitleInfo); null without an id to keep it by.</summary>
    public static RemoteSubtitle? FromJellyfin(JsonObject found, string language) => Field(found, "Id") is { Length: > 0 } id
        ? new RemoteSubtitle(id, Field(found, "Name") ?? "", Field(found, "ProviderName") ?? "", Field(found, "Format") ?? "srt", Field(found, "ThreeLetterISOLanguageName") ?? language,
            found["DownloadCount"] is JsonValue count && count.TryGetValue<int>(out var downloads) ? downloads : 0,
            Flag(found, "IsHashMatch"), Flag(found, "Forced"), Flag(found, "HearingImpaired"), Flag(found, "MachineTranslated") || Flag(found, "AiTranslated"),
            MiraScore(found), Field(found, "ProviderName") == "Mira" ? Field(found, "Author") : null)
        : null;
    /// <summary>The score « Mira » writes at the head of its comment (« 57/100 · … »); null for another provider.</summary>
    private static int? MiraScore(JsonObject found) =>
        Field(found, "ProviderName") == "Mira" && Field(found, "Comment") is { } comment && Regex.Match(comment, @"^(\d{1,3})/100") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    private static string? Field(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<bool>(out var on) && on;
    /// <summary>
    /// The sentence said before any search: what leaves the server, and for whom. « Mira » (the extension's own provider)
    /// sends neither the file's name nor anything of the file but its fingerprint: it gets its own sentence.
    /// </summary>
    public static string Privacy(IReadOnlyList<string> providers)
    {
        var mira = providers.Contains("Mira");
        var others = providers.Where(p => p != "Mira").ToList();
        var said = others.Count > 0 || !mira
            ? L($"Jellyfin demande à {(others.Count > 0 ? string.Join(" et ", others) : "son extension de sous-titres")} : le nom du fichier et son empreinte partent chez ce service.",
                $"Jellyfin asks {(others.Count > 0 ? string.Join(" and ", others) : "its subtitles plugin")}: the file’s name and its fingerprint go to that service.")
            : "";
        var miraSaid = mira ? L("Mira demande à OpenSubtitles, et à SubDL s’il est réglé : l’empreinte et la taille du fichier, ses identifiants IMDb ou TMDb et la langue partent chez ces services, jamais le nom du fichier.", "Mira asks OpenSubtitles, and SubDL when it is set up: the file’s fingerprint and size, the title’s IMDb or TMDb ids and the language go to those services, never the file’s name.") : "";
        return string.Join(" ", new[] { said, miraSaid }.Where(x => x.Length > 0));
    }

    /// <summary>What happened, in a sentence: « Sous-titres gardés sur Jellyfin, en français : … ».</summary>
    public static string KeptText(SubtitleKept kept, string? language)
    {
        var name = PlayerText.Language(language) is { } known ? L(", en " + known.ToLower(Culture), ", in " + known) : "";
        return kept switch
        {
            SubtitleKept.Kept => L($"Sous-titres gardés sur Jellyfin{name} : tes autres appareils les proposent aussi.", $"Subtitles kept on Jellyfin{name}: your other devices offer them too."),
            SubtitleKept.KeptInJellyfinData => L($"Sous-titres gardés sur Jellyfin{name} : tes autres appareils les proposent aussi. Jellyfin n’a que le droit de lire le dossier de tes vidéos : il range ceux de cette bibliothèque dans ses propres données.", $"Subtitles kept on Jellyfin{name}: your other devices offer them too. Jellyfin may only read your videos’ folder: it keeps those of this library in its own data."),
            SubtitleKept.Refused => L("Ton compte Jellyfin n’a pas le droit de gérer les sous-titres : son administrateur peut le lui donner (Tableau de bord → Utilisateurs).", "Your Jellyfin account isn’t allowed to manage subtitles: its administrator can allow it (Dashboard → Users)."),
            _ => L("Jellyfin n’a pas pu enregistrer ces sous-titres : il n’a pas le droit d’écrire dans le dossier de ce titre.", "Jellyfin couldn’t save these subtitles: it isn’t allowed to write in this title’s folder."),
        };
    }
    /// <summary>Said once a file is loaded during playback, with « Garder sur Jellyfin ».</summary>
    public static string LoadedText => L("Ces sous-titres ne valent que pour cette lecture. Gardés sur Jellyfin, tes autres appareils les proposent aussi.",
        "These subtitles are only for this playback. Kept on Jellyfin, your other devices offer them too.");
    public static string TooBigText => L("Ce fichier est trop gros pour des sous-titres : Mira ne l’envoie pas à Jellyfin.", "This file is too big to be subtitles: Mira doesn’t send it to Jellyfin.");
    public static string UnreadableText => L("Mira n’a pas pu relire ce fichier de sous-titres : il a peut-être été déplacé.", "Mira couldn’t read this subtitle file again: it may have been moved.");
    /// <summary>A file loaded during playback, read to be sent (<see cref="Guess"/> for what it is); null when it is too big to be subtitles.</summary>
    public static async Task<SubtitleUpload?> ReadAsync(string file, (string Format, string Language, bool Forced, bool HearingImpaired) guess, CancellationToken ct = default) =>
        new FileInfo(file).Length > MaxSize ? null : new SubtitleUpload(guess.Format, guess.Language, guess.Forced, guess.HearingImpaired, await File.ReadAllBytesAsync(file, ct));
    /// <summary>A subtitle stream's file extension for Jellyfin's Stream.{ext} address (subrip → srt, webvtt → vtt).</summary>
    public static string Extension(string? codec) => codec?.ToLowerInvariant() switch { "subrip" or "srt" or null or "" => "srt", "webvtt" or "vtt" => "vtt", var other => other };
    /// <summary>
    /// The subtitle file a title did not have at a previous look: the one just kept, once Jellyfin has read it. By its
    /// path, since a new file may move the indexes of the others.
    /// </summary>
    public static MediaStream? Arrived(IEnumerable<MediaStream> before, IEnumerable<MediaStream> now)
    {
        var known = before.Where(x => x.Type == "Subtitle" && x.IsExternal).Select(x => x.Path).ToHashSet();
        return now.FirstOrDefault(x => x.Type == "Subtitle" && x.IsExternal && x.Path is { Length: > 0 } && !known.Contains(x.Path));
    }
}
