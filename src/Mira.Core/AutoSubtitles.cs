using System.Text.Json.Serialization;
using static Mira.Core.Lang;

namespace Mira.Core;

/// <summary>
/// « Sous-titres automatiques » (the extension's own subtitle provider and loop, Mira.Jellyfin Subtitles/, an
/// administrator's settings at Mira/subtitles): what its journal says, in a line, and what the loop is doing. As Mira
/// web says it (js/subtitles-auto.js, the same cases in tests/fixtures/subtitles-auto.json).
/// </summary>
public static class AutoSubtitles
{
    /// <summary>The languages offered, by the code Jellyfin understands (ISO 639-2), the most asked first.</summary>
    public static readonly IReadOnlyList<string> Languages = ["fre", "eng", "spa", "ger", "ita", "por", "jpn", "dut", "rus", "pol", "tur", "ara", "chi", "kor"];
    /// <summary>At most this many languages (as the extension keeps).</summary>
    public const int MaxLanguages = 5;

    /// <summary>What the extension says to send its loop or its sources, where they are set (a privacy sentence).</summary>
    public static string Privacy => JellyfinSubtitles.Privacy(["Mira"]);

    private static string Seconds(double value) => Number(Math.Abs(value), "0.##") + " s";

    private static string Aligned(AutoSubtitleEntry entry)
    {
        if (string.IsNullOrEmpty(entry.AlignedOn)) return "";
        var on = entry.AlignedOn == "track" ? L("sur la piste du fichier", "on the file’s track") : L("sur l’audio", "on the audio");
        var parts = new List<string>();
        if (entry.Shift is { } shift and not 0) parts.Add(shift > 0 ? L($"{Seconds(shift)} plus tard", $"{Seconds(shift)} later") : L($"{Seconds(shift)} plus tôt", $"{Seconds(shift)} earlier"));
        if (entry.Speed is { } speed and not 0) parts.Add(L("vitesse corrigée", "speed fixed"));
        return parts.Count > 0 ? L($"recalés {on}, {string.Join(", ", parts)}", $"synced {on}, {string.Join(", ", parts)}") : L($"vérifiés {on}", $"checked {on}");
    }

    /// <summary>One thing the journal says, in a line: « Gardés · OpenSubtitles · note 82/100 · recalés sur l’audio, 3,17 s plus tard ».</summary>
    public static string Line(AutoSubtitleEntry entry)
    {
        var score = entry.Score is { } n ? L($"note {n}/100", $"score {n}/100") : "";
        var best = score.Length > 0 ? L($"meilleur : {score}", $"best: {score}") : "";
        var detail = entry.Detail ?? "";
        string?[] parts = entry.Kind switch
        {
            "kept" => [L("Gardés", "Kept"), entry.Source, score, Aligned(entry)],
            "none" => [L("Rien d’assez sûr pour l’instant : nouvel essai plus tard", "Nothing reliable enough yet: trying again later"), best, detail],
            "given-up" => [L("Rien trouvé en 30 jours : Mira n’en cherche plus", "Nothing found in 30 days: Mira stopped looking"), best, detail],
            "rejected" => [L("Écartés", "Set aside"), entry.Source, score, detail],
            "failed" => [entry.Source is { Length: > 0 } failed ? L($"Échec chez {failed}", $"{failed} failed") : L("Échec", "Failed"), detail],
            "quota" => [entry.Source is { Length: > 0 } spent ? L($"Quota épuisé chez {spent} : Mira attend sa remise à zéro", $"{spent} quota used up: Mira waits for it to reset") : L("Quota épuisé : Mira attend sa remise à zéro", "Quota used up: Mira waits for it to reset")],
            _ => [detail],
        };
        return string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>What the loop is doing: how many titles wait, and until when a spent quota holds them.</summary>
    public static string Waiting(AutoSubtitleState state)
    {
        var settings = state.Settings;
        if (settings?.Enabled != true) return L("Arrêtés : Mira ne cherche rien tout seul.", "Off: Mira searches nothing by itself.");
        if (settings.Languages is not { Count: > 0 }) return L("Choisis au moins une langue.", "Choose at least one language.");
        if (state.PausedUntil is { } until) return L($"Quota épuisé : reprise à {Time(until.ToLocalTime())}.", $"Quota used up: back at {Time(until.ToLocalTime())}.");
        return state.Waiting > 0
            ? JellyfinSubtitles.Thousands(state.Waiting) + " " + Plural(state.Waiting, "recherche en attente.", "recherches en attente.", "search waiting.", "searches waiting.")
            : L("Rien en attente : Mira cherche dès qu’un titre arrive.", "Nothing waiting: Mira searches as soon as a title arrives.");
    }

    /// <summary>What « Essayer les sources » answered, in a line.</summary>
    public static string Tried(IEnumerable<AutoSubtitleSourceTry> sources)
    {
        var lines = sources.Select(x => !x.Ok
            ? L($"{x.Source} : {(x.Refused ? "identifiants ou clé refusés" : x.Error)}", $"{x.Source}: {(x.Refused ? "credentials or key refused" : x.Error)}")
            : x.Remaining is { } left
                ? L($"{x.Source} répond ({JellyfinSubtitles.Thousands(left)} {Plural(left, "téléchargement restant", "téléchargements restants", "", "")})",
                    $"{x.Source} answers ({left.ToString(Lang.Culture)} {Plural(left, "", "", "download left", "downloads left")})")
                : L($"{x.Source} répond", $"{x.Source} answers")).ToList();
        return lines.Count > 0 ? string.Join(" · ", lines) : L("Aucune source allumée.", "No source turned on.");
    }

    /// <summary>A language's name in Mira's language (« Français », « French »), its code when Mira does not know it.</summary>
    public static string LanguageName(string code) => PlayerText.Language(code) ?? code;
}

/// <summary>What GET Mira/subtitles answers: the settings (secrets said only as set), the journal, the titles waiting.</summary>
public sealed record AutoSubtitleState(
    [property: JsonPropertyName("settings")] AutoSubtitleSettings? Settings,
    [property: JsonPropertyName("journal")] List<AutoSubtitleEntry>? Journal,
    [property: JsonPropertyName("waiting")] int Waiting,
    [property: JsonPropertyName("pausedUntil")] DateTimeOffset? PausedUntil);

public sealed record AutoSubtitleSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("languages")] List<string>? Languages,
    [property: JsonPropertyName("hearing")] string? Hearing,
    [property: JsonPropertyName("machineTranslated")] bool MachineTranslated,
    [property: JsonPropertyName("sync")] bool Sync,
    [property: JsonPropertyName("openSubtitles")] AutoSubtitleOpenSubtitles? OpenSubtitles,
    [property: JsonPropertyName("subDl")] AutoSubtitleSubDl? SubDl);

public sealed record AutoSubtitleOpenSubtitles(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("ready")] bool Ready,
    [property: JsonPropertyName("ownKey")] bool OwnKey,
    [property: JsonPropertyName("account")] string? Account);

public sealed record AutoSubtitleSubDl(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("ready")] bool Ready);

/// <summary>One line of the journal (its detail already in Mira's language, by Accept-Language).</summary>
public sealed record AutoSubtitleEntry(
    [property: JsonPropertyName("when")] DateTimeOffset When,
    [property: JsonPropertyName("itemId")] string? ItemId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("score")] int? Score = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("alignedOn")] string? AlignedOn = null,
    [property: JsonPropertyName("shift")] double? Shift = null,
    [property: JsonPropertyName("speed")] double? Speed = null);

/// <summary>What one source answered to « Essayer les sources ».</summary>
public sealed record AutoSubtitleSourceTry(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("remaining")] int? Remaining = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("refused")] bool Refused = false);

/// <summary>What POST Mira/subtitles/test answers.</summary>
public sealed record AutoSubtitleTries([property: JsonPropertyName("sources")] List<AutoSubtitleSourceTry>? Sources);

/// <summary>What POST Mira/subtitles/library answers.</summary>
public sealed record AutoSubtitleQueued([property: JsonPropertyName("added")] int Added, [property: JsonPropertyName("waiting")] int Waiting);
