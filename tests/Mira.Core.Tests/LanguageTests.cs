using System.Globalization;
using Mira.Core;
using static TestSupport;

/// <summary>Mira in English or in French: which language, plurals, dates and numbers.</summary>
internal static class LanguageTests
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US"), France = CultureInfo.GetCultureInfo("fr-FR");

    public static async Task RunAsync(TestRun run)
    {
        var Test = run.Test;
        await Test("Langue : celle des réglages, sinon celle de l’installation, sinon le français d’un système en français, sinon l’anglais", () =>
        {
            Assert(Lang.Resolve("", null, CultureInfo.GetCultureInfo("de-DE")) == "en", "German system");
            Assert(Lang.Resolve("", null, CultureInfo.GetCultureInfo("en-GB")) == "en", "English system");
            Assert(Lang.Resolve(null, null, CultureInfo.GetCultureInfo("fr-CA")) == "fr", "French system (Canada)");
            Assert(Lang.Resolve("", null, CultureInfo.InvariantCulture) == "en", "Invariant system");
            Assert(Lang.Resolve("", "french", Us) == "fr" && Lang.Resolve("", "english", France) == "en", "Installer's choice");
            Assert(Lang.Resolve("en", "french", France) == "en" && Lang.Resolve("fr", "english", Us) == "fr", "Settings' choice");
            Assert(Lang.Resolve("de", "dutch", Us) == "en" && Lang.Resolve(" FR ", null, Us) == "fr", "Unknown choices are ignored");
            return Task.CompletedTask;
        });
        await Test("Langue : dates et nombres au format de la langue, celui du système quand il est de la même langue", () =>
        {
            Lang.Use("en", CultureInfo.GetCultureInfo("de-DE"));
            Assert(Lang.Code == "en" && !Lang.IsFrench && Lang.Culture.Name == "en-US", "English on a German format: " + Lang.Culture.Name);
            Lang.Use("en", CultureInfo.GetCultureInfo("en-GB"));
            Assert(Lang.Culture.Name == "en-GB", "British format kept: " + Lang.Culture.Name);
            Lang.Use("fr", CultureInfo.GetCultureInfo("fr-CA"));
            Assert(Lang.IsFrench && Lang.Culture.Name == "fr-CA", "Canadian French kept: " + Lang.Culture.Name);
            Lang.Use("fr", Us);
            Assert(Lang.Culture.Name == "fr-FR", "French on an American format: " + Lang.Culture.Name);
            Lang.Use("xx", Us);
            Assert(Lang.Code == "en", "Unknown code");

            var evening = new DateTime(2026, 11, 3, 22, 14, 0);
            Lang.Use("fr", France);
            Assert(Lang.L("Lecture", "Play") == "Lecture", "French text");
            Assert(Lang.Time(evening) == "22:14" && Lang.Day(evening) == "3 novembre" && Lang.Date(evening) == "3 novembre 2026", $"French date: {Lang.Time(evening)} {Lang.Day(evening)} {Lang.Date(evening)}");
            Assert(Lang.Number(0.75, "0.##") == "0,75", "French decimal comma");
            Lang.Use("en", Us);
            Assert(Lang.L("Lecture", "Play") == "Play", "English text");
            // ICU and Windows write the space before PM differently (narrow no-break or plain).
            var time = Lang.Time(evening).Replace(' ', ' ').Replace(' ', ' ');
            Assert(time == "10:14 PM" && Lang.Day(evening) == "November 3" && Lang.Date(evening) == "November 3, 2026", $"English date: {time} {Lang.Day(evening)} {Lang.Date(evening)}");
            Assert(Lang.Number(0.75, "0.##") == "0.75", "English decimal point");
            Lang.Use("en", CultureInfo.GetCultureInfo("en-GB"));
            Assert(Lang.Day(evening) == "3 November" && Lang.Time(evening) == "22:14", $"British date: {Lang.Day(evening)} {Lang.Time(evening)}");
            return Task.CompletedTask;
        });
        await Test("Langue : pluriels de chaque langue (0 au singulier en français, au pluriel en anglais)", () =>
        {
            string Episodes(long n) => Lang.Count(n, "épisode", "épisodes", "episode", "episodes");
            Lang.Use("fr", France);
            Assert(Episodes(0) == "0 épisode" && Episodes(1) == "1 épisode" && Episodes(2) == "2 épisodes", $"French: {Episodes(0)}, {Episodes(1)}, {Episodes(2)}");
            Lang.Use("en", Us);
            Assert(Episodes(0) == "0 episodes" && Episodes(1) == "1 episode" && Episodes(12) == "12 episodes", $"English: {Episodes(0)}, {Episodes(1)}, {Episodes(12)}");
            Assert(Lang.Plural(1, "titre", "titres", "title", "titles") == "title", "Noun alone");
            return Task.CompletedTask;
        });
        await Test("Langue : les réglages gardent le choix, vide tant qu’il n’est pas fait", () =>
        {
            var settings = new PlayerSettings();
            Assert(settings.Language == "", "Not chosen by default");
            settings.Language = "fr";
            var read = System.Text.Json.JsonSerializer.Deserialize<PlayerSettings>(System.Text.Json.JsonSerializer.Serialize(settings, Json.Options), Json.Options)!;
            Assert(read.Language == "fr", "Saved: " + read.Language);
            var old = System.Text.Json.JsonSerializer.Deserialize<PlayerSettings>("{\"Volume\":50}", Json.Options)!;
            Assert(old.Language == "", "A profile from before keeps no choice");
            return Task.CompletedTask;
        });
    }
}
