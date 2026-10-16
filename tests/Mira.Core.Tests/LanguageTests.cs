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
        await Test("Langue : les textes de Core en anglais, sans un mot de français (guide du téléphone, lecteur, groupe, accueil, durées)", () =>
        {
            Lang.Use("en", Us);
            var texts = new List<string>();
            void Add(params string?[] more) => texts.AddRange(more.OfType<string>());
            const string server = "http://127.0.0.1:8096", home = "http://192.168.1.20:8096", tailnet = "http://100.101.102.103:8096";
            JellyfinStartup.State service = new(true, true, false, true), noService = new(false, false, false, false);
            var now = new DateTimeOffset(2026, 10, 16, 21, 5, 0, TimeSpan.Zero);
            var box = new JellyfinFirewall.PublicNetwork("Wi-Fi", "Livebox-1234");
            var facts = new PhoneSetup.Facts[]
            {
                new(server, true, Web: false, Home: home, Admin: true, Tailnet: tailnet, TailnetAllowed: false, Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Public), Boot: noService),
                new(server, true, Web: true, Home: home, Admin: true, Tailnet: tailnet, TailnetAllowed: true, Firewall: new(JellyfinFirewall.Verdict.Open, JellyfinFirewall.Private), Boot: service,
                    Node: new(true, false, now.AddDays(3), false, "jellyfin-pc", now), Power: new(30, 0, null, false)),
                new(server, true, Web: true, Home: null, Admin: false, Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Public, Public: box), NetworkKept: true,
                    Node: new(true, null, now.AddDays(-1), true, null, now)),
                new(server, true, Web: true, Home: home, Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Private, Public: box with { Known = false })),
                new(server, true, Web: true, Home: home, Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Public, Public: box)),
                new(server, true, Web: true, Home: home, Firewall: new(JellyfinFirewall.Verdict.Closed, JellyfinFirewall.Domain)),
                new(server, true, Web: true, Home: home, Firewall: new(JellyfinFirewall.Verdict.Blocked, JellyfinFirewall.Private, Kept: "Jellyfin Server")),
                new(server, true, Web: true, Home: home, Program: false),
                new(server, true, Web: true, Home: home, Firewall: new(JellyfinFirewall.Verdict.Open, JellyfinFirewall.Private), MiraRule: JellyfinFirewall.RuleState.Stale, Tailnet: tailnet),
                new(server, true, Web: null, Home: home, Answering: false, Down: LocalJellyfin.Verdict.Stopped, Boot: service),
                new(server, true, Web: null, Home: home, Answering: false, Down: LocalJellyfin.Verdict.Starting),
                new(server, true, Web: null, Home: home, Answering: false, Down: LocalJellyfin.Verdict.Failing),
                new(server, true, Web: null, Home: home, Answering: false, Tailnet: tailnet),
                new(server, true, Web: null, Home: home),
                new("http://192.168.1.30:8096", false, Web: true, Home: "http://192.168.1.30:8096"),
            };
            foreach (var step in facts.SelectMany(PhoneSetup.Steps)) Add(step.Title, step.Text, step.Action);
            foreach (var verdict in Enum.GetValues<LocalJellyfin.Verdict>()) if (LocalJellyfin.Says(verdict, true, "address already in use") is { } said) Add(said.Text, said.Action);
            Add(TailscaleNode.Warning(new(true, true, now.AddDays(2), false, "pc", now)), TailscaleNode.Warning(new(true, true, now, true, "pc", now)));
            Add(PlayerText.Track("2", null, "fra", "ac3", 6, forced: true, external: true).Label, PlayerText.Track("2", null, "fra", "ac3", 6, forced: true, external: true).Details, PlayerText.Track("3", null, null, null, 2).Label);
            Add(PlayerText.Subtitle(new MediaItem { Type = "Episode", Name = "Pilot", ParentIndexNumber = 0, IndexNumber = 1 }), new MediaItem { Type = "Series", ProductionYear = 2019 }.Subtitle,
                new MediaItem { RunTimeTicks = TimeSpan.FromMinutes(105).Ticks }.DurationLabel);
            Add(NewEpisodes.Text([new MediaItem { Type = "Episode", Name = "Pilot", ParentIndexNumber = 2, IndexNumber = 5 }]),
                NewEpisodes.Text([new MediaItem { ParentIndexNumber = 2, IndexNumber = 5 }, new MediaItem { ParentIndexNumber = 2, IndexNumber = 7 }]));
            foreach (var row in HomeLayout.Rows(null, [new LibrarySection("m", "Movies", "movies")])) Add(row.Title, HomeLayout.Detail(row));
            Add(SleepTimer.Duration(90), SleepTimer.Duration(120), WatchReport.Time(725), MediaQuality.EndsAt(new MediaItem { RunTimeTicks = TimeSpan.FromMinutes(30).Ticks }, new DateTime(2026, 10, 16, 21, 40, 0), false));
            Add(Together.GroupText(new("g", Together.GroupName("Marc"), ["Marc", "Léa"], "Idle")), Together.NamesText(["Marc", "Léa", "Sam", "Jo"]), Together.WaitingText("Marc"), Together.WaitingLead);
            Add(AccountData.SignedOut("this PC", false, true), AccountData.SignedOut("this PC", true, false), Friends.NameProblem("", []), Friends.NameProblem("<b>", []), Friends.NameProblem("marc", ["Marc"]));
            Add(SyncStatus.Describe(2, null, null, now).Text, SyncStatus.Describe(0, null, now, null).Text, SyncStatus.Describe(0, null, null, null).Text);
            Add(PlaybackFailures.When(now.AddDays(-1), now), PlaybackFailures.When(now.AddDays(-40), now));
            var french = texts.Where(x => x.IndexOfAny("àâçéèêëîïôûùüœ«»".ToCharArray()) >= 0 && !x.Contains("Léa") || x.Contains(" : ") || x.Contains(" ; ")).ToList();
            Assert(texts.Count > 80 && french.Count == 0, $"{texts.Count} texts; in French: " + string.Join(" | ", french));
            return Task.CompletedTask;
        });
        await Test("Langue : un groupe de Regarder ensemble se retrouve d’une langue à l’autre (« Groupe de Marc », “Marc’s group”)", () =>
        {
            Lang.Use("en", Us);
            Assert(Together.GroupName("Marc") == "Marc's group", "English name: " + Together.GroupName("Marc"));
            Lang.Use("fr", France);
            Assert(Together.GroupName("Marc") == "Groupe de Marc", "French name: " + Together.GroupName("Marc"));
            Assert(Together.GroupHost("Groupe de Marc") == "Marc" && Together.GroupHost("Marc's group") == "Marc" && Together.GroupHost("Marc’s group") == "Marc"
                && Together.GroupHost("'s group") is null && Together.GroupHost("Soirée") is null, "Host of both forms");
            // A friend's Mira in French opens the link of a host whose Mira is in English, after the group was made again.
            Together.Group english = new("new-id", "Marc's group", ["Marc"], "Idle"), other = new("x", "Groupe de Léa", ["Léa", "Sam"], "Idle");
            Assert(Together.FindGroup([other, english], "old-id", "Marc") == english, "Found by its host across languages");
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
