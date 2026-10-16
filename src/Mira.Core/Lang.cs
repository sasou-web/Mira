using System.Globalization;

namespace Mira.Core;

/// <summary>
/// The language of Mira's texts: English, or French when it is chosen in the settings, chosen at installation (Windows),
/// or the language of the system. Each text is written in both at the place it is shown (<see cref="L(string, string)"/>),
/// so none can be left without its English; dates and numbers follow <see cref="Culture"/>. Chosen once at startup,
/// before any window: a new choice applies when Mira opens again.
/// </summary>
public static class Lang
{
    public const string English = "en", French = "fr";
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US"), FrenchCulture = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>"en" or "fr".</summary>
    public static string Code { get; private set; } = English;
    public static bool IsFrench { get; private set; }
    /// <summary>Dates and numbers: the system's own format when it is of that language (en-GB, fr-CA…), else en-US or
    /// fr-FR; never the system's customised separators.</summary>
    public static CultureInfo Culture { get; private set; } = EnglishCulture;

    /// <summary>The text in Mira's language: <c>L("Lecture", "Play")</c>.</summary>
    public static string L(string french, string english) => IsFrench ? french : english;
    /// <summary>
    /// A count and its noun, singular or plural by each language's rule (0 is singular in French, plural in English):
    /// <c>Count(n, "épisode", "épisodes", "episode", "episodes")</c> → "1 épisode", "0 episodes".
    /// </summary>
    public static string Count(long count, string frenchOne, string frenchMany, string englishOne, string englishMany) =>
        count.ToString(Culture) + " " + Plural(count, frenchOne, frenchMany, englishOne, englishMany);
    /// <summary>The noun alone, singular or plural for <paramref name="count"/>, as <see cref="Count"/>.</summary>
    public static string Plural(long count, string frenchOne, string frenchMany, string englishOne, string englishMany) =>
        IsFrench ? (Math.Abs(count) <= 1 ? frenchOne : frenchMany) : (Math.Abs(count) == 1 ? englishOne : englishMany);

    /// <summary>A time of day: "22:14" in French, "10:14 PM" in American English.</summary>
    public static string Time(DateTime at) => at.ToString("t", Culture);
    public static string Time(DateTimeOffset at) => Time(at.DateTime);
    /// <summary>A day without its year: "3 novembre", "November 3".</summary>
    public static string Day(DateTime at) => at.ToString(IsFrench ? "d MMMM" : Culture.DateTimeFormat.MonthDayPattern, Culture);
    public static string Day(DateTimeOffset at) => Day(at.DateTime);
    /// <summary>A day with its year: "3 novembre 2026", "November 3, 2026".</summary>
    public static string Date(DateTime at) => at.ToString(IsFrench ? "d MMMM yyyy" : Culture.DateTimeFormat.LongDatePattern.Replace("dddd, ", "").Replace("dddd ", ""), Culture);
    public static string Date(DateTimeOffset at) => Date(at.DateTime);
    /// <summary>A number with the language's decimal separator: "0,5" or "0.5".</summary>
    public static string Number(double value, string format = "0.#") => value.ToString(format, Culture);

    /// <summary>
    /// The language to use: the one chosen in Mira's settings, else the one chosen at installation ("french" or
    /// "english", Inno Setup's names), else French when the system's language is French, else English.
    /// </summary>
    public static string Resolve(string? chosen, string? installation, CultureInfo system) =>
        Known(chosen) ?? Known(installation) ?? (system.TwoLetterISOLanguageName == French ? French : English);
    /// <summary>"en" or "fr" for a code or an installer's language name; null for anything else.</summary>
    public static string? Known(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "en" or "english" => English,
        "fr" or "french" => French,
        _ => null
    };
    /// <summary>
    /// Sets the language, with the formats of <paramref name="format"/> when they are of that language (the system's
    /// regional format by default).
    /// </summary>
    public static void Use(string code, CultureInfo? format = null)
    {
        Code = Known(code) ?? English; IsFrench = Code == French;
        format ??= CultureInfo.CurrentCulture;
        var fallback = IsFrench ? FrenchCulture : EnglishCulture;
        Culture = format.TwoLetterISOLanguageName == Code && !string.IsNullOrEmpty(format.Name) ? ReadOnly(format.Name) ?? fallback : fallback;
    }
    /// <summary>The culture of that name without the user's customisations, or null when this system does not know it.</summary>
    private static CultureInfo? ReadOnly(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return null; }
    }
}
